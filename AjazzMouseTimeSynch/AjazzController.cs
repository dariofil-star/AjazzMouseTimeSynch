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

    [HttpGet("hid/devices")]
    public IActionResult GetAllHidDevices()
    {
        return Ok(syncService.GetAllHidDevices());
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

    // ── Firmware version (0x80 GET_REV) ──────────────────────────────────────

    [HttpGet("device/firmware")]
    public async Task<IActionResult> GetFirmwareVersion(CancellationToken cancellationToken)
    {
        AjazzFirmwareVersion? version = await syncService.TryGetFirmwareVersionAsync(cancellationToken);
        return version is null
            ? NotFound(new { message = "Device not available or firmware query failed." })
            : Ok(version);
    }

    // ── Profile (0x05 SET_PROFILE / 0x85 GET_PROFILE) ────────────────────────

    [HttpGet("device/profile")]
    public async Task<IActionResult> GetProfile(CancellationToken cancellationToken)
    {
        int? profile = await syncService.TryGetProfileAsync(cancellationToken);
        return profile is null
            ? NotFound(new { message = "Device not available or profile query failed." })
            : Ok(new { profileIndex = profile.Value });
    }

    [HttpPost("device/profile")]
    public async Task<IActionResult> SetProfile([FromBody] AjazzSetProfileRequest request, CancellationToken cancellationToken)
    {
        if (request.ProfileIndex is < 0 or > 7)
        {
            return BadRequest(new { message = "profileIndex must be 0-7." });
        }

        bool success = await syncService.TrySetProfileAsync(request.ProfileIndex, cancellationToken);
        return Ok(new { success, profileIndex = request.ProfileIndex });
    }

    // ── Factory reset (0x02 SET_RESERT) ──────────────────────────────────────

    [HttpPost("device/reset")]
    public async Task<IActionResult> FactoryReset(CancellationToken cancellationToken)
    {
        bool success = await syncService.TrySendFactoryResetAsync(cancellationToken);
        return Ok(new { success });
    }

    // ── LED (0x07 SET_LEDPARAM / 0x87 GET_LEDPARAM) ──────────────────────────

    [HttpGet("device/led")]
    public async Task<IActionResult> GetLedParam(CancellationToken cancellationToken)
    {
        AjazzLedSetting? setting = await syncService.TryGetLedParamAsync(cancellationToken);
        return setting is null
            ? NotFound(new { message = "Device not available or LED query failed." })
            : Ok(setting);
    }

    [HttpPost("device/led")]
    public async Task<IActionResult> SetLedParam([FromBody] AjazzLedSetting request, CancellationToken cancellationToken)
    {
        bool success = await syncService.TrySetLedParamAsync(request, cancellationToken);
        return Ok(new { success });
    }

    // ── DPI table (0x54 SET_OPTIONPARAM1 / 0xD4 GET_OPTIONPARAM1) ────────────

    [HttpGet("device/dpi")]
    public async Task<IActionResult> GetDpiTable([FromQuery] int? profile = null, CancellationToken cancellationToken = default)
    {
        int resolvedProfile = profile ?? await syncService.TryGetProfileAsync(cancellationToken) ?? 0;
        AjazzDpiTableRequest? table = await syncService.TryGetDpiTableAsync(resolvedProfile, cancellationToken);
        return table is null
            ? NotFound(new { message = "Device not available or DPI query failed." })
            : Ok(table);
    }

    [HttpPost("device/dpi")]
    public async Task<IActionResult> SetDpiTable([FromBody] AjazzDpiTableRequest request, CancellationToken cancellationToken)
    {
        bool success = await syncService.TrySetDpiTableAsync(request, cancellationToken);
        return Ok(new { success });
    }

    // ── Button bind (0x50 SET_KEYMATRIX) ─────────────────────────────────────

    [HttpPost("device/button")]
    public async Task<IActionResult> SetButtonBind([FromBody] AjazzButtonBindRequest request, CancellationToken cancellationToken)
    {
        if (request.ActionBytes is null || request.ActionBytes.Length != 4)
        {
            return BadRequest(new { message = "actionBytes must be exactly 4 bytes." });
        }

        bool success = await syncService.TrySetButtonBindAsync(request, cancellationToken);
        return Ok(new { success });
    }

    // ── Omnibus mouse settings (0x53 SET_OPTIONPARAM0 / 0xD3 GET_OPTIONPARAM0)

    [HttpGet("device/settings")]
    public async Task<IActionResult> GetMouseSettings(CancellationToken cancellationToken)
    {
        AjazzMouseSettingsRequest? settings = await syncService.TryGetMouseSettingsAsync(cancellationToken);
        return settings is null
            ? NotFound(new { message = "Device not available or settings query failed." })
            : Ok(settings);
    }

    [HttpPost("device/settings")]
    public async Task<IActionResult> SetMouseSettings([FromBody] AjazzMouseSettingsRequest request, CancellationToken cancellationToken)
    {
        bool success = await syncService.TrySetMouseSettingsAsync(request, cancellationToken);
        return Ok(new { success });
    }
}
