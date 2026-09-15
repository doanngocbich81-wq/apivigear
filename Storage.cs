using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Vigear.MultiWarehouse.Api;

public interface ITokenVault
{
    Task<TikTokToken?> GetAsync(CancellationToken cancellationToken);
    Task SaveAsync(TikTokToken token, CancellationToken cancellationToken);
}

public sealed class ProtectedFileTokenVault(IDataProtectionProvider protectionProvider, IWebHostEnvironment environment) : ITokenVault
{
    private readonly IDataProtector _protector = protectionProvider.CreateProtector("tiktok-shop-oauth-v1");
    private readonly string _path = Path.Combine(environment.ContentRootPath, "App_Data", "tiktok-token.protected");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<TikTokToken?> GetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_path)) return null;
            var protectedValue = await File.ReadAllTextAsync(_path, cancellationToken);
            return JsonSerializer.Deserialize<TikTokToken>(_protector.Unprotect(protectedValue));
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(TikTokToken token, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(token);
        var protectedValue = _protector.Protect(json);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await File.WriteAllTextAsync(_path, protectedValue, cancellationToken);
        }
        finally { _gate.Release(); }
    }
}

public interface IReportRepository
{
    Task SaveAsync(AuditReport report, CancellationToken cancellationToken);
    Task<AuditReport?> LatestAsync(CancellationToken cancellationToken);
}

public sealed class JsonReportRepository(IWebHostEnvironment environment) : IReportRepository
{
    private readonly string _directory = Path.Combine(environment.ContentRootPath, "App_Data", "reports");
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task SaveAsync(AuditReport report, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        var current = Path.Combine(_directory, "latest.json");
        var historic = Path.Combine(_directory, $"audit-{report.GeneratedAt:yyyy-MM-dd-HHmmss}.json");
        var json = JsonSerializer.Serialize(report, _json);
        await File.WriteAllTextAsync(current, json, cancellationToken);
        await File.WriteAllTextAsync(historic, json, cancellationToken);
    }

    public async Task<AuditReport?> LatestAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_directory, "latest.json");
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<AuditReport>(stream, cancellationToken: cancellationToken);
    }
}

public interface IClock { DateTimeOffset UtcNow { get; } DateTimeOffset VietnamNow { get; } }
public sealed class VietnamClock : IClock
{
    private static readonly TimeZoneInfo Vietnam = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "SE Asia Standard Time" : "Asia/Ho_Chi_Minh");
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public DateTimeOffset VietnamNow => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Vietnam);
}
