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

    [HttpGet("hid/reports")]
    public IActionResult GetRecentHidReports([FromQuery] int take = 100)
    {
        return Ok(syncService.GetRecentHidReports(take));
    }

    [HttpGet("reverse/state")]
    public IActionResult GetReverseState()
    {
        return Ok(syncService.GetReverseEngineState());
    }

    [HttpGet("reverse/descriptors")]
    public IActionResult GetDescriptors()
    {
        return Ok(syncService.GetDescriptorSnapshots());
    }

    [HttpGet("reverse/observed")]
    public IActionResult GetObservedReports([FromQuery] int take = 200)
    {
        return Ok(syncService.GetObservedReports(take));
    }

    [HttpGet("reverse/captures")]
    public IActionResult GetCaptureSessions()
    {
        return Ok(syncService.GetCaptureSessions());
    }

    [HttpPost("reverse/captures/start")]
    public IActionResult StartCapture([FromBody] AjazzCaptureRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Label))
        {
            return BadRequest(new { message = "label is required." });
        }

        syncService.BeginCaptureSession(request.Label.Trim());
        return Ok(new { started = true, label = request.Label.Trim() });
    }

    [HttpPost("reverse/captures/stop")]
    public IActionResult StopCapture([FromBody] AjazzCaptureRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Label))
        {
            return BadRequest(new { message = "label is required." });
        }

        syncService.EndCaptureSession(request.Label.Trim());
        return Ok(new { stopped = true, label = request.Label.Trim() });
    }

    [HttpPost("reverse/captures/diff")]
    public IActionResult DiffCaptureSessions([FromBody] AjazzCaptureDiffRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.LeftLabel) || string.IsNullOrWhiteSpace(request.RightLabel))
        {
            return BadRequest(new { message = "leftLabel and rightLabel are required." });
        }

        CaptureDiffResult? diff = syncService.DiffCaptureSessions(
            request.LeftLabel.Trim(),
            request.RightLabel.Trim(),
            request.ReportId,
            request.InterfaceNumber,
            request.Endpoint);

        return diff is null
            ? NotFound(new { message = "Capture labels not found." })
            : Ok(diff);
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
