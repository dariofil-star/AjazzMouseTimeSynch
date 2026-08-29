using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api")]
public sealed class AjazzController(IAjazzSettingsStore settingsStore, AjazzClockSyncService syncService) : ControllerBase
{
    [HttpGet("settings")]
    public ActionResult<AjazzSettings> GetSettings()
    {
        return Ok(settingsStore.GetSettings());
    }

    [HttpPost("settings")]
    public ActionResult<AjazzSettings> UpdateSettings([FromBody] AjazzSettingsUpdateRequest request)
    {
        AjazzSettings updated = settingsStore.UpdateSettings(request);
        return Ok(updated);
    }

    [HttpGet("devices")]
    public IActionResult GetDevices()
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

        return Ok(devices);
    }

    [HttpPost("sync")]
    public async Task<IActionResult> SyncNow(CancellationToken cancellationToken)
    {
        bool success = await syncService.TrySyncNowAsync("manual", cancellationToken);
        return Ok(new { success });
    }

    [HttpPost("sync/custom")]
    public async Task<IActionResult> SyncCustom([FromBody] AjazzManualSyncRequest request, CancellationToken cancellationToken)
    {
        if (!request.TargetDateTime.HasValue)
        {
            return BadRequest(new { message = "targetDateTime is required." });
        }

        DateTime requested = request.TargetDateTime.Value;
        DateTime localTarget = requested.Kind == DateTimeKind.Utc ? requested.ToLocalTime() : requested;

        AjazzSettings persisted = settingsStore.UpdateSettings(new AjazzSettingsUpdateRequest
        {
            LastCustomDateTime = localTarget.ToString("yyyy-MM-ddTHH:mm")
        });

        bool success = await syncService.TrySyncAtAsync("manual custom", localTarget, cancellationToken);
        return Ok(new
        {
            success,
            timestamp = localTarget.ToString("yyyy-MM-dd HH:mm:ss"),
            lastCustomDateTime = persisted.LastCustomDateTime
        });
    }

    [HttpGet("monitoring")]
    public ActionResult<AjazzMonitoringStatus> GetMonitoringStatus()
    {
        return Ok(syncService.GetMonitoringStatus());
    }

    [HttpGet("status")]
    public IActionResult Status()
    {
        return Ok(new
        {
            running = true,
            utc = DateTime.UtcNow
        });
    }
}
