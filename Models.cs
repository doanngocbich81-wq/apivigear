using System.Text.Json.Serialization;

namespace Vigear.MultiWarehouse.Api;

public sealed class TikTokShopOptions
{
    public const string Section = "TikTokShop";
    public string AppKey { get; init; } = "";
    public string AppSecret { get; init; } = "";
    public string ServiceId { get; init; } = "";
    public string ShopCipher { get; init; } = "";
    public string OAuthRedirectUri { get; init; } = "";
    public string AuthorizationBaseUrl { get; init; } = "https://services.tiktokshop.com";
    public string TokenBaseUrl { get; init; } = "https://auth.tiktok-shops.com";
}

public sealed class AuditOptions
{
    public const string Section = "Audit";
    public int LookbackDays { get; init; } = 60;
    public int HcmMinimumDays { get; init; } = 15;
    public int HanoiMinimumDays { get; init; } = 45;
    public string RunAtLocalTime { get; init; } = "08:00";
    public string[] HanoiWarehouseIds { get; init; } = [];
    public string[] HcmWarehouseIds { get; init; } = [];
    public string AdminApiKey { get; init; } = "";
}

public sealed class NotificationOptions
{
    public const string Section = "Notifications";
    public bool Enabled { get; init; }
    public string WebhookUrl { get; init; } = "";
}

public sealed record TikTokToken(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("access_token_expire_in")] long AccessTokenExpireIn,
    [property: JsonPropertyName("refresh_token_expire_in")] long RefreshTokenExpireIn,
    [property: JsonPropertyName("open_id")] string? OpenId,
    DateTimeOffset SavedAt)
{
    // TikTok Shop returns access_token_expire_in as a Unix timestamp (seconds),
    // while older integrations may return a duration. Accept both forms so a
    // deployed service refreshes the token before it really expires.
    public DateTimeOffset AccessExpiresAt => AccessTokenExpireIn >= 1_000_000_000
        ? DateTimeOffset.FromUnixTimeSeconds(AccessTokenExpireIn)
        : SavedAt.AddSeconds(AccessTokenExpireIn);
}

public sealed record Warehouse(string Id, string Name, string LocationText, WarehouseRegion Region);
public enum WarehouseRegion { Unknown, Hanoi, Hcm }
public enum DestinationRegion { Unknown, Hanoi, Hcm }

public sealed record RouteIssue(
    string OrderId,
    string SellerSku,
    string SkuId,
    int Quantity,
    string Province,
    WarehouseRegion ActualWarehouse,
    DestinationRegion ExpectedWarehouse,
    string WarehouseId);

public enum StockAlertType { TransferToHcm, ReorderForHanoi }
public sealed record StockAlert(
    StockAlertType Type,
    string SellerSku,
    string SkuId,
    int CurrentAvailable,
    int TargetAvailable,
    int SuggestedQuantity,
    decimal AverageDailySales,
    decimal CoverDays);

public sealed record AuditReport(
    DateTimeOffset GeneratedAt,
    DateOnly FromDate,
    DateOnly ToDate,
    int LookbackDays,
    int OrdersScanned,
    int SkusScanned,
    IReadOnlyList<Warehouse> Warehouses,
    IReadOnlyList<RouteIssue> RouteIssues,
    IReadOnlyList<StockAlert> StockAlerts)
{
    public object Summary => new
    {
        orders_scanned = OrdersScanned,
        skus_scanned = SkusScanned,
        wrong_region_order_lines = RouteIssues.Count,
        stock_alerts = StockAlerts.Count
    };
}
