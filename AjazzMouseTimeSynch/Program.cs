using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;

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
        settings.LogName = "Application";
    });

    builder.Logging.AddFilter<EventLogLoggerProvider>(null, LogLevel.Information);
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
builder.WebHost.UseUrls($"http://127.0.0.1:{webPort}");

builder.Services.AddControllers();
builder.Services.AddSingleton<IAjazzSettingsStore, AjazzSettingsStore>();
builder.Services.AddSingleton<AjazzClockSyncService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AjazzClockSyncService>());

var app = builder.Build();

app.MapGet("/", () => Results.Content(GetHtmlPage(), "text/html"));
app.MapGet("/favicon.svg", () => Results.Content(HtmlPage.FavIcon, "image/svg+xml"));
app.MapGet("/favicon.ico", () => Results.Content(HtmlPage.FavIcon, "image/svg+xml"));

app.MapControllers();

await app.RunAsync();

static string GetHtmlPage()
{
    return HtmlPage.Content;
}
