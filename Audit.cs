using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using Microsoft.Extensions.Options;

namespace Vigear.MultiWarehouse.Api;

public sealed class AuditRunner(
    TikTokApiClient tikTok,
    IReportRepository reports,
    IAlertPublisher alerts,
    IOptions<AuditOptions> options,
    IClock clock,
    ILogger<AuditRunner> logger)
{
    private readonly AuditOptions _options = options.Value;

    public async Task<AuditReport> RunAsync(CancellationToken cancellationToken)
    {
        if (_options.LookbackDays is < 1 or > 60)
            throw new InvalidOperationException("Audit:LookbackDays phải từ 1 đến 60 ngày.");

        var end = clock.VietnamNow;
        var start = end.AddDays(-_options.LookbackDays);
        var warehouses = await GetWarehousesAsync(cancellationToken);
        var orders = new List<JsonObject>();
        foreach (var status in new[] { "COMPLETED", "DELIVERED", "IN_TRANSIT", "AWAITING_COLLECTION", "AWAITING_SHIPMENT" })
            orders.AddRange(await GetAllOrdersAsync(status, start, end, cancellationToken));
        var products = await GetAllProductsAsync(cancellationToken);
        var skuIndex = IndexSkus(products);
        var inventory = await GetInventoryAsync(skuIndex.Keys, cancellationToken);

        var totalSold = new Dictionary<string, int>(StringComparer.Ordinal);
        var southSold = new Dictionary<string, int>(StringComparer.Ordinal);
        var routeIssues = new List<RouteIssue>();
        foreach (var order in orders.GroupBy(o => o["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString()).Select(g => g.First()))
        {
            var status = order["status"]?.GetValue<string>() ?? "";
            var expected = RegionPolicy.ForOrder(order);
            var actualWarehouseId = order["warehouse_id"]?.GetValue<string>() ?? "";
            var actual = FindWarehouseRegion(actualWarehouseId, warehouses);
            var province = RegionPolicy.Province(order);
            foreach (var line in order["line_items"]?.AsArray() ?? [])
            {
                if (line is not JsonObject item) continue;
                var skuId = item["sku_id"]?.GetValue<string>() ?? "";
                var sellerSku = item["seller_sku"]?.GetValue<string>() ?? skuIndex.GetValueOrDefault(skuId)?.SellerSku ?? skuId;
                if (string.IsNullOrWhiteSpace(sellerSku)) continue;
                var quantity = AsInt(item["quantity"], 1);
                if (status is "COMPLETED" or "DELIVERED")
                {
                    Add(totalSold, skuId, quantity);
                    if (expected == DestinationRegion.Hcm) Add(southSold, skuId, quantity);
                }
                if (expected != DestinationRegion.Unknown && actual != WarehouseRegion.Unknown && (int)expected != (int)actual)
                    routeIssues.Add(new RouteIssue(order["id"]?.GetValue<string>() ?? "", sellerSku, skuId, quantity, province, actual, expected, actualWarehouseId));
            }
        }

        var stockAlerts = new List<StockAlert>();
        foreach (var (skuId, sku) in skuIndex)
        {
            var rows = inventory.GetValueOrDefault(skuId) ?? [];
            var hcm = rows.Where(row => FindWarehouseRegion(row.WarehouseId, warehouses) == WarehouseRegion.Hcm).Sum(row => row.AvailableQuantity);
            var hanoi = rows.Where(row => FindWarehouseRegion(row.WarehouseId, warehouses) == WarehouseRegion.Hanoi).Sum(row => row.AvailableQuantity);
            var totalDaily = decimal.Round((decimal)totalSold.GetValueOrDefault(skuId) / _options.LookbackDays, 2);
            var southDaily = decimal.Round((decimal)southSold.GetValueOrDefault(skuId) / _options.LookbackDays, 2);
            if (southDaily > 0)
            {
                var target = (int)Math.Ceiling(southDaily * _options.HcmMinimumDays);
                if (hcm < target) stockAlerts.Add(new(StockAlertType.TransferToHcm, sku.SellerSku, skuId, hcm, target, target - hcm, southDaily, hcm / southDaily));
            }
            if (totalDaily > 0)
            {
                var target = (int)Math.Ceiling(totalDaily * _options.HanoiMinimumDays);
                if (hanoi < target) stockAlerts.Add(new(StockAlertType.ReorderForHanoi, sku.SellerSku, skuId, hanoi, target, target - hanoi, totalDaily, hanoi / totalDaily));
            }
        }

        var report = new AuditReport(clock.VietnamNow, DateOnly.FromDateTime(start.Date), DateOnly.FromDateTime(end.Date), _options.LookbackDays,
            orders.Count, skuIndex.Count, warehouses, routeIssues.OrderBy(x => x.SellerSku).ToList(), stockAlerts.OrderBy(x => x.Type).ThenBy(x => x.SellerSku).ToList());
        await reports.SaveAsync(report, cancellationToken);
        if (routeIssues.Count > 0 || stockAlerts.Count > 0) await alerts.PublishAsync(report, cancellationToken);
        logger.LogInformation("Multi-warehouse audit complete. Orders={Orders}, SKUs={Skus}, routes={Routes}, stock={Stock}", report.OrdersScanned, report.SkusScanned, routeIssues.Count, stockAlerts.Count);
        return report;
    }

    private async Task<List<Warehouse>> GetWarehousesAsync(CancellationToken ct)
    {
        var response = await tikTok.GetAsync("/logistics/202309/warehouses", null, ct);
        return (response["data"]?["warehouses"]?.AsArray() ?? []).OfType<JsonObject>().Select(raw =>
        {
            var id = raw["id"]?.GetValue<string>() ?? raw["warehouse_id"]?.GetValue<string>() ?? "";
            var name = raw["name"]?.GetValue<string>() ?? id;
            var address = raw["address"] as JsonObject;
            var location = string.Join(' ', new[] { name, address?["state"]?.GetValue<string>(), address?["city"]?.GetValue<string>(), address?["district"]?.GetValue<string>(), address?["full_address"]?.GetValue<string>() }.Where(x => !string.IsNullOrWhiteSpace(x))!);
            return new Warehouse(id, name, location, FindWarehouseRegion(id, null, location));
        }).ToList();
    }

    private async Task<List<JsonObject>> GetAllOrdersAsync(string status, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        var output = new List<JsonObject>();
        string? pageToken = null;
        do
        {
            var query = new Dictionary<string, string> { ["page_size"] = "100", ["sort_field"] = "create_time", ["sort_order"] = "ASC" };
            if (!string.IsNullOrEmpty(pageToken)) query["page_token"] = pageToken;
            var body = new { create_time_ge = start.ToUnixTimeSeconds(), create_time_lt = end.ToUnixTimeSeconds(), order_status = status };
            var response = await tikTok.PostAsync("/order/202309/orders/search", query, body, ct);
            output.AddRange((response["data"]?["orders"]?.AsArray() ?? []).OfType<JsonObject>());
            pageToken = response["data"]?["next_page_token"]?.GetValue<string>();
        } while (!string.IsNullOrWhiteSpace(pageToken));
        return output;
    }

    private async Task<List<JsonObject>> GetAllProductsAsync(CancellationToken ct)
    {
        var output = new List<JsonObject>();
        string? pageToken = null;
        do
        {
            var query = new Dictionary<string, string> { ["page_size"] = "100" };
            if (!string.IsNullOrEmpty(pageToken)) query["page_token"] = pageToken;
            var response = await tikTok.PostAsync("/product/202502/products/search", query, new { status = "ALL" }, ct);
            output.AddRange((response["data"]?["products"]?.AsArray() ?? []).OfType<JsonObject>());
            pageToken = response["data"]?["next_page_token"]?.GetValue<string>();
        } while (!string.IsNullOrWhiteSpace(pageToken));
        return output;
    }

    private async Task<Dictionary<string, List<WarehouseInventory>>> GetInventoryAsync(IEnumerable<string> skuIds, CancellationToken ct)
    {
        var result = new Dictionary<string, List<WarehouseInventory>>(StringComparer.Ordinal);
        foreach (var batch in skuIds.Chunk(600))
        {
            var response = await tikTok.PostAsync("/product/202309/inventory/search", null, new { sku_ids = batch }, ct);
            foreach (var product in (response["data"]?["inventory"]?.AsArray() ?? []).OfType<JsonObject>())
            foreach (var sku in (product["skus"]?.AsArray() ?? []).OfType<JsonObject>())
            {
                var skuId = sku["id"]?.GetValue<string>() ?? "";
                if (string.IsNullOrWhiteSpace(skuId)) continue;
                result[skuId] = (sku["warehouse_inventory"]?.AsArray() ?? []).OfType<JsonObject>()
                    .Select(row => new WarehouseInventory(row["warehouse_id"]?.GetValue<string>() ?? "", AsInt(row["available_quantity"])))
                    .ToList();
            }
        }
        return result;
    }

    private Dictionary<string, SkuInfo> IndexSkus(IEnumerable<JsonObject> products)
        => products.SelectMany(product => (product["skus"]?.AsArray() ?? []).OfType<JsonObject>())
            .Select(sku => new SkuInfo(sku["id"]?.GetValue<string>() ?? "", sku["seller_sku"]?.GetValue<string>() ?? sku["id"]?.GetValue<string>() ?? ""))
            .Where(sku => !string.IsNullOrWhiteSpace(sku.Id)).GroupBy(sku => sku.Id).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

    private WarehouseRegion FindWarehouseRegion(string id, IEnumerable<Warehouse>? warehouses, string? fallbackLocation = null)
    {
        if (_options.HanoiWarehouseIds.Contains(id, StringComparer.Ordinal)) return WarehouseRegion.Hanoi;
        if (_options.HcmWarehouseIds.Contains(id, StringComparer.Ordinal)) return WarehouseRegion.Hcm;
        var location = fallbackLocation ?? warehouses?.FirstOrDefault(item => item.Id == id)?.LocationText ?? "";
        var normalized = RegionPolicy.Normalize(location);
        if (normalized.Contains("ha noi") || normalized.Contains("hanoi")) return WarehouseRegion.Hanoi;
        if (normalized.Contains("ho chi minh") || normalized.Contains("hcm")) return WarehouseRegion.Hcm;
        return WarehouseRegion.Unknown;
    }

    private static void Add(Dictionary<string, int> values, string key, int amount) => values[key] = values.GetValueOrDefault(key) + amount;
    private static int AsInt(JsonNode? value, int fallback = 0) => value is null ? fallback : int.TryParse(value.ToString(), out var number) ? number : fallback;
    private sealed record SkuInfo(string Id, string SellerSku);
    private sealed record WarehouseInventory(string WarehouseId, int AvailableQuantity);
}

public static class RegionPolicy
{
    private static readonly HashSet<string> Hanoi = new(StringComparer.Ordinal)
    {
        "ha noi","hai phong","quang ninh","bac ninh","bac giang","bac kan","cao bang","ha giang","lang son","tuyen quang","thai nguyen","phu tho","vinh phuc","hung yen","hai duong","thai binh","nam dinh","ninh binh","lao cai","yen bai","son la","dien bien","lai chau","hoa binh","ha nam","thanh hoa","nghe an","ha tinh","quang binh","quang tri","thua thien hue","hue","da nang"
    };
    public static DestinationRegion ForOrder(JsonObject order)
    {
        var names = (order["recipient_address"]?["district_info"]?.AsArray() ?? []).OfType<JsonObject>().Select(x => Normalize(x["address_name"]?.GetValue<string>() ?? ""));
        var province = names.FirstOrDefault(Hanoi.Contains);
        if (!string.IsNullOrWhiteSpace(province)) return DestinationRegion.Hanoi;
        return names.Any() ? DestinationRegion.Hcm : DestinationRegion.Unknown;
    }
    public static string Province(JsonObject order)
        => (order["recipient_address"]?["district_info"]?.AsArray() ?? []).OfType<JsonObject>().FirstOrDefault(x => x["address_level"]?.GetValue<string>() == "L1")?["address_name"]?.GetValue<string>()
        ?? (order["recipient_address"]?["district_info"]?.AsArray() ?? []).OfType<JsonObject>().LastOrDefault()?["address_name"]?.GetValue<string>()
        ?? "Không xác định";
    public static string Normalize(string value) => value.Normalize(NormalizationForm.FormD).Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark).Aggregate(new StringBuilder(), (b, ch) => b.Append(ch)).ToString().ToLowerInvariant().Replace('đ', 'd').Trim();
}

public interface IAlertPublisher { Task PublishAsync(AuditReport report, CancellationToken cancellationToken); }
public sealed class WebhookAlertPublisher(HttpClient http, IOptions<NotificationOptions> options, ILogger<WebhookAlertPublisher> logger) : IAlertPublisher
{
    private readonly NotificationOptions _options = options.Value;
    public async Task PublishAsync(AuditReport report, CancellationToken ct)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.WebhookUrl)) return;
        var message = new
        {
            title = "Cảnh báo hàng hoá, đa kho",
            generated_at = report.GeneratedAt,
            window_days = report.LookbackDays,
            route_issues = report.RouteIssues,
            stock_alerts = report.StockAlerts
        };
        using var response = await http.PostAsJsonAsync(_options.WebhookUrl, message, ct);
        if (!response.IsSuccessStatusCode) logger.LogWarning("Alert webhook returned HTTP {Status}", response.StatusCode);
    }
}

public sealed class DailyAuditWorker(IServiceScopeFactory scopes, IOptions<AuditOptions> options, IClock clock, ILogger<DailyAuditWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = clock.VietnamNow;
            var time = TimeOnly.TryParse(options.Value.RunAtLocalTime, out var parsed) ? parsed : new TimeOnly(8, 0);
            var due = new DateTimeOffset(now.Date.Add(time.ToTimeSpan()), now.Offset);
            if (due <= now) due = due.AddDays(1);
            await Task.Delay(due - now, stoppingToken);
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<AuditRunner>().RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogError(ex, "Daily audit failed"); }
        }
    }
}
