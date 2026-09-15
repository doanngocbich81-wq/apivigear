using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Vigear.MultiWarehouse.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddMemoryCache();
builder.Services.Configure<TikTokShopOptions>(builder.Configuration.GetSection(TikTokShopOptions.Section));
builder.Services.Configure<AuditOptions>(builder.Configuration.GetSection(AuditOptions.Section));
builder.Services.Configure<NotificationOptions>(builder.Configuration.GetSection(NotificationOptions.Section));

var appData = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
Directory.CreateDirectory(appData);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(appData, "data-protection")))
    .SetApplicationName("Vigear.MultiWarehouse.Api");

builder.Services.AddHttpClient<TikTokApiClient>(client =>
{
    client.BaseAddress = new Uri("https://open-api.tiktokglobalshop.com");
    client.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddHttpClient<OAuthClient>(client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient<IAlertPublisher, WebhookAlertPublisher>(client => client.Timeout = TimeSpan.FromSeconds(30));

builder.Services.AddSingleton<ITokenVault, ProtectedFileTokenVault>();
builder.Services.AddSingleton<IReportRepository, JsonReportRepository>();
builder.Services.AddSingleton<IClock, VietnamClock>();
builder.Services.AddScoped<AuditRunner>();
builder.Services.AddHostedService<DailyAuditWorker>();

var app = builder.Build();

app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("UnhandledError");
    logger.LogError("Unhandled request failure for {Path}", context.Request.Path);
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    await context.Response.WriteAsJsonAsync(new { error = "Máy chủ không thể xử lý yêu cầu. Kiểm tra nhật ký hệ thống." });
}));

app.UseHttpsRedirection();
app.MapControllers();
app.MapGet("/", () => Results.Ok(new { service = "Vigear Multi-Warehouse API", status = "running" }));

app.Run();
