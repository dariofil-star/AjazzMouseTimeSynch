using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;

var isWindowsService = OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService();

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();

if (isWindowsService)
{
    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "AjazzMouseTimeSynch";
    });

    builder.Logging.AddEventLog(settings =>
    {
        settings.SourceName = "AjazzMouseTimeSynch";
    });
}
else
{
    builder.Logging.AddSimpleConsole(options =>
    {
        options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
        options.SingleLine = true;
    });
}

var startupSettings = new AjazzSettings();
builder.Configuration.GetSection("Ajazz").Bind(startupSettings);
int webPort = startupSettings.WebPort <= 0 ? 5088 : startupSettings.WebPort;
builder.WebHost.UseUrls($"http://0.0.0.0:{webPort}");

builder.Services.AddSingleton<IAjazzSettingsStore, AjazzSettingsStore>();
builder.Services.AddSingleton<AjazzClockSyncService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AjazzClockSyncService>());

var app = builder.Build();

app.MapGet("/", () => Results.Content(GetHtmlPage(), "text/html"));

app.MapGet("/api/settings", (IAjazzSettingsStore settingsStore) =>
{
    return TypedResults.Ok(settingsStore.GetSettings());
});

app.MapPost("/api/settings", (AjazzSettingsUpdateRequest request, IAjazzSettingsStore settingsStore) =>
{
    AjazzSettings updated = settingsStore.UpdateSettings(request);
    return TypedResults.Ok(updated);
});

app.MapGet("/api/devices", (AjazzClockSyncService syncService, IAjazzSettingsStore settingsStore) =>
{
    string selected = settingsStore.GetSettings().SelectedDevicePath;

    var devices = syncService.GetAjazzDevices().Select(d => new
    {
        d.DevicePath,
        d.ProductName,
        d.VendorId,
        d.ProductId,
        IsSelected = string.Equals(d.DevicePath, selected, StringComparison.OrdinalIgnoreCase)
    });

    return TypedResults.Ok(devices);
});

app.MapPost("/api/sync", async (AjazzClockSyncService syncService, CancellationToken cancellationToken) =>
{
    bool success = await syncService.TrySyncNowAsync("manual", cancellationToken);
    return Results.Ok(new { success });
});

app.MapPost("/api/sync/custom", async (AjazzManualSyncRequest request, AjazzClockSyncService syncService, CancellationToken cancellationToken) =>
{
    if (!request.TargetDateTime.HasValue)
    {
        return Results.BadRequest(new { message = "targetDateTime is required." });
    }

    DateTime requested = request.TargetDateTime.Value;
    DateTime localTarget = requested.Kind == DateTimeKind.Utc ? requested.ToLocalTime() : requested;

    bool success = await syncService.TrySyncAtAsync("manual custom", localTarget, cancellationToken);
    return Results.Ok(new { success, timestamp = localTarget.ToString("yyyy-MM-dd HH:mm:ss") });
});

await app.RunAsync();

static string GetHtmlPage()
{
    return HtmlPage.Content;
}
