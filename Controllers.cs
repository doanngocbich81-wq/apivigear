using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Vigear.MultiWarehouse.Api;

[ApiController]
[Route("health")]
public sealed class HealthController(IOptions<TikTokShopOptions> tiktok, ITokenVault tokens) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var token = await tokens.GetAsync(cancellationToken);
        var config = tiktok.Value;
        return Ok(new
        {
            status = "running",
            configured = !string.IsNullOrWhiteSpace(config.AppKey) && !string.IsNullOrWhiteSpace(config.AppSecret) && !string.IsNullOrWhiteSpace(config.ServiceId),
            seller_authorized = token is not null,
            token_expires_at = token?.AccessExpiresAt
        });
    }
}

[ApiController]
public sealed class OAuthController(OAuthClient oauth, IMemoryCache cache) : ControllerBase
{
    [HttpGet("oauth/tiktok/start")]
    public IActionResult Start()
    {
        var state = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        cache.Set($"oauth-state:{state}", true, TimeSpan.FromMinutes(10));
        return Redirect(oauth.BuildAuthorizeUrl(state));
    }

    [HttpGet("oauth/tiktok/callback")]
    public async Task<IActionResult> Callback([FromQuery(Name = "code")] string? code, [FromQuery(Name = "state")] string? state, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state) || !cache.TryGetValue($"oauth-state:{state}", out _))
            return BadRequest("Ủy quyền không hợp lệ hoặc đã hết hạn. Hãy mở lại /oauth/tiktok/start.");
        cache.Remove($"oauth-state:{state}");
        await oauth.ExchangeCodeAsync(code, cancellationToken);
        return Content("<h1>Đã kết nối TikTok Shop</h1><p>Bạn có thể đóng trang này và quay lại hệ thống Vigear.</p>", "text/html; charset=utf-8");
    }
}

[ApiController]
[Route("api/reports")]
public sealed class ReportsController(IReportRepository reports, IOptions<AuditOptions> options) : ControllerBase
{
    [HttpGet("latest")]
    public async Task<IActionResult> Latest([FromHeader(Name = "X-Admin-Key")] string? adminKey, CancellationToken cancellationToken)
    {
        if (!AdminKey.IsValid(adminKey, options.Value.AdminApiKey)) return Unauthorized(new { error = "Thiếu hoặc sai X-Admin-Key." });
        var report = await reports.LatestAsync(cancellationToken);
        return report is null ? NotFound(new { error = "Chưa có báo cáo. Hãy chạy POST /api/sync/run sau khi Seller ủy quyền." }) : Ok(report);
    }
}

[ApiController]
[Route("api/sync")]
public sealed class SyncController(AuditRunner audit, IOptions<AuditOptions> options) : ControllerBase
{
    [HttpPost("run")]
    public async Task<IActionResult> Run([FromHeader(Name = "X-Admin-Key")] string? adminKey, CancellationToken cancellationToken)
    {
        if (!AdminKey.IsValid(adminKey, options.Value.AdminApiKey))
            return Unauthorized(new { error = "Thiếu hoặc sai X-Admin-Key." });
        var report = await audit.RunAsync(cancellationToken);
        return Ok(report);
    }
}

internal static class AdminKey
{
    public static bool IsValid(string? provided, string configured)
    {
        if (string.IsNullOrWhiteSpace(configured) || string.IsNullOrWhiteSpace(provided)) return false;
        var left = System.Text.Encoding.UTF8.GetBytes(provided);
        var right = System.Text.Encoding.UTF8.GetBytes(configured);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}
