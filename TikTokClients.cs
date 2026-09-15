using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Vigear.MultiWarehouse.Api;

public sealed class OAuthClient(
    HttpClient http,
    IOptions<TikTokShopOptions> options,
    ITokenVault tokenVault,
    IClock clock,
    ILogger<OAuthClient> logger)
{
    private readonly TikTokShopOptions _options = options.Value;

    public string BuildAuthorizeUrl(string state)
    {
        ValidateAppConfiguration();
        var url = new Uri(new Uri(_options.AuthorizationBaseUrl), "/open/authorize");
        return $"{url}?service_id={Uri.EscapeDataString(_options.ServiceId)}&state={Uri.EscapeDataString(state)}";
    }

    public async Task ExchangeCodeAsync(string code, CancellationToken cancellationToken)
    {
        ValidateAppConfiguration();
        var query = new Dictionary<string, string>
        {
            ["app_key"] = _options.AppKey,
            ["app_secret"] = _options.AppSecret,
            ["auth_code"] = code,
            ["grant_type"] = "authorized_code"
        };
        var token = await RequestTokenAsync("/api/v2/token/get", query, cancellationToken);
        await tokenVault.SaveAsync(token, cancellationToken);
        logger.LogInformation("TikTok Shop authorization completed; credentials stored by the protected token vault.");
    }

    public async Task<TikTokToken> RefreshAsync(TikTokToken current, CancellationToken cancellationToken)
    {
        ValidateAppConfiguration();
        var query = new Dictionary<string, string>
        {
            ["app_key"] = _options.AppKey,
            ["app_secret"] = _options.AppSecret,
            ["refresh_token"] = current.RefreshToken,
            ["grant_type"] = "refresh_token"
        };
        var fresh = await RequestTokenAsync("/api/v2/token/refresh", query, cancellationToken);
        await tokenVault.SaveAsync(fresh, cancellationToken);
        return fresh;
    }

    private async Task<TikTokToken> RequestTokenAsync(string path, IReadOnlyDictionary<string, string> query, CancellationToken cancellationToken)
    {
        var url = new Uri(new Uri(_options.TokenBaseUrl), path);
        var builder = new StringBuilder(url.ToString());
        builder.Append('?').Append(string.Join('&', query.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}")));
        using var response = await http.GetAsync(builder.ToString(), cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        using var json = JsonDocument.Parse(content);
        var root = json.RootElement;
        if (!response.IsSuccessStatusCode || root.GetPropertyOrDefault("code")?.GetInt32() != 0)
            throw new InvalidOperationException($"TikTok Shop không cấp token: {root.GetPropertyOrDefault("message")?.GetString() ?? response.StatusCode.ToString()}");
        var data = root.GetProperty("data");
        return new TikTokToken(
            data.GetRequiredString("access_token"), data.GetRequiredString("refresh_token"),
            data.GetPropertyOrDefault("access_token_expire_in")?.GetInt64() ?? 0,
            data.GetPropertyOrDefault("refresh_token_expire_in")?.GetInt64() ?? 0,
            data.GetPropertyOrDefault("open_id")?.GetString(), clock.UtcNow);
    }

    private void ValidateAppConfiguration()
    {
        if (string.IsNullOrWhiteSpace(_options.AppKey) || string.IsNullOrWhiteSpace(_options.AppSecret) || string.IsNullOrWhiteSpace(_options.ServiceId))
            throw new InvalidOperationException("Thiếu TikTokShop:AppKey, AppSecret hoặc ServiceId trong cấu hình bảo mật của IIS.");
    }
}

public sealed class TikTokApiClient(
    HttpClient http,
    IOptions<TikTokShopOptions> options,
    ITokenVault tokenVault,
    OAuthClient oauth,
    IMemoryCache cache,
    IClock clock,
    ILogger<TikTokApiClient> logger)
{
    private readonly TikTokShopOptions _options = options.Value;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<JsonObject> GetAsync(string path, Dictionary<string, string>? query, CancellationToken cancellationToken)
        => await SendAsync(HttpMethod.Get, path, query ?? [], null, cancellationToken);

    public async Task<JsonObject> PostAsync(string path, Dictionary<string, string>? query, object body, CancellationToken cancellationToken)
        => await SendAsync(HttpMethod.Post, path, query ?? [], JsonSerializer.Serialize(body, Json), cancellationToken);

    private async Task<JsonObject> SendAsync(HttpMethod method, string path, Dictionary<string, string> query, string? body, CancellationToken cancellationToken)
    {
        var token = await RequireTokenAsync(cancellationToken);
        var signedQuery = new Dictionary<string, string>(query, StringComparer.Ordinal)
        {
            ["app_key"] = _options.AppKey,
            ["timestamp"] = clock.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
        };
        var shopCipher = await GetShopCipherAsync(token.AccessToken, cancellationToken);
        if (!string.IsNullOrWhiteSpace(shopCipher)) signedQuery["shop_cipher"] = shopCipher;
        signedQuery["sign"] = BuildSignature(path, signedQuery, body ?? "");

        var url = new UriBuilder(new Uri(http.BaseAddress!, path))
        {
            Query = string.Join('&', signedQuery.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"))
        };
        using var request = new HttpRequestMessage(method, url.Uri);
        request.Headers.TryAddWithoutValidation("x-tts-access-token", token.AccessToken);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var json = JsonNode.Parse(content) as JsonObject ?? throw new InvalidOperationException("TikTok Shop trả về dữ liệu không hợp lệ.");
        if (!response.IsSuccessStatusCode || json["code"]?.GetValue<int>() != 0)
        {
            logger.LogWarning("TikTok request {Method} {Path} failed. HTTP={Status}, Code={Code}", method, path, response.StatusCode, json["code"]);
            throw new InvalidOperationException($"TikTok Shop API lỗi: {json["message"]?.GetValue<string>() ?? response.StatusCode.ToString()}");
        }
        return json;
    }

    private async Task<TikTokToken> RequireTokenAsync(CancellationToken cancellationToken)
    {
        var token = await tokenVault.GetAsync(cancellationToken) ?? throw new InvalidOperationException("Chưa có Seller nào ủy quyền. Mở /oauth/tiktok/start để kết nối.");
        return token.AccessExpiresAt <= clock.UtcNow.AddMinutes(5) ? await oauth.RefreshAsync(token, cancellationToken) : token;
    }

    private async Task<string> GetShopCipherAsync(string accessToken, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_options.ShopCipher)) return _options.ShopCipher;
        return await cache.GetOrCreateAsync("shop-cipher", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6);
            var token = await tokenVault.GetAsync(cancellationToken) ?? throw new InvalidOperationException("Không tìm thấy token Seller.");
            var timestamp = clock.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            var query = new Dictionary<string, string> { ["app_key"] = _options.AppKey, ["timestamp"] = timestamp };
            query["sign"] = BuildSignature("/authorization/202309/shops", query, "");
            var uri = new UriBuilder(new Uri(http.BaseAddress!, "/authorization/202309/shops"))
            {
                Query = string.Join('&', query.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"))
            };
            using var request = new HttpRequestMessage(HttpMethod.Get, uri.Uri);
            request.Headers.TryAddWithoutValidation("x-tts-access-token", token.AccessToken);
            using var response = await http.SendAsync(request, cancellationToken);
            var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken)) as JsonObject;
            var shops = root?["data"]?["shops"]?.AsArray() ?? throw new InvalidOperationException("Không đọc được danh sách shop đã ủy quyền.");
            return shops.FirstOrDefault(s => string.Equals(s?["region"]?.GetValue<string>(), "VN", StringComparison.OrdinalIgnoreCase))?["cipher"]?.GetValue<string>()
                ?? shops.FirstOrDefault()?["cipher"]?.GetValue<string>()
                ?? throw new InvalidOperationException("Không tìm thấy shop đã ủy quyền.");
        }) ?? throw new InvalidOperationException("Không tìm thấy shop cipher.");
    }

    private string BuildSignature(string path, IReadOnlyDictionary<string, string> query, string body)
    {
        if (string.IsNullOrWhiteSpace(_options.AppKey) || string.IsNullOrWhiteSpace(_options.AppSecret))
            throw new InvalidOperationException("Thiếu AppKey hoặc AppSecret trong cấu hình bảo mật của IIS.");
        var canonical = string.Concat(query.Where(p => p.Key is not "sign" and not "access_token")
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => p.Key + p.Value));
        var input = _options.AppSecret + path + canonical + body + _options.AppSecret;
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(_options.AppSecret), Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }
}

internal static class JsonElementExtensions
{
    public static JsonElement? GetPropertyOrDefault(this JsonElement element, string name)
        => element.TryGetProperty(name, out var value) ? value : null;
    public static string GetRequiredString(this JsonElement element, string name)
        => element.GetProperty(name).GetString() ?? throw new InvalidOperationException($"TikTok không trả về {name}.");
}
