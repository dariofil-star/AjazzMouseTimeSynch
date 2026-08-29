# TaskCanceledException During Kestrel Server Startup - Root Cause & Fix

## Issue Summary
The application was throwing a `System.Threading.Tasks.TaskCanceledException: A task was canceled.` during Kestrel server startup, preventing the web server from binding to the configured address (`http://127.0.0.1:5588`).

## Root Cause Analysis

### Exception Details
- **Exception Type**: `System.Threading.Tasks.TaskCanceledException`
- **Exception Location**: `Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerImpl.BindAsync` line 226
- **Call Stack**: Exception occurred while Kestrel was attempting to bind to the configured URL during `app.RunAsync()`

### Underlying Problem
The issue was caused by a **race condition between ASP.NET Core hosted service initialization and Kestrel server binding**:

1. **ASP.NET Core starts hosted services concurrently** with server binding
2. **`AjazzClockSyncService.ExecuteAsync`** begins executing and calls `_deviceMonitor.Start()`
3. **`AjazzDeviceMonitor.Start()`** calls Win32 API `CM_Register_Notification` to register for device change notifications
4. **Win32 API call fails** (likely due to insufficient permissions, missing device notification infrastructure, or environment-specific issues)
5. **Unhandled exception propagates** from the hosted service, triggering cancellation of the startup cancellation token
6. **Kestrel's binding operation is cancelled** mid-execution while waiting on `_bindSemaphore.WaitAsync(cancellationToken)`

### Why It Happens
The `BackgroundService` base class doesn't automatically catch exceptions in `ExecuteAsync`. If an exception occurs during startup (before the service enters its main loop), it can cause the entire application startup to be cancelled, preventing Kestrel from completing its binding process.

## Applied Fix

### Changes Made to `AjazzMouseTimeSynch\Ajazz.cs`

#### 1. Wrapped Device Monitor Initialization
Added try-catch block around `_deviceMonitor.Start()` to prevent device monitoring failures from canceling application startup:

```csharp
try
{
	_deviceMonitor.Start();
}
catch (Exception ex)
{
	logger.LogError(DeviceChangeErrorEvent, ex, "Failed to start device monitor. Device monitoring will be unavailable.");
}
```

**Benefit**: The application can start successfully even if Windows device notification registration fails. Device monitoring features will be unavailable, but the web server and time sync functionality remain operational.

#### 2. Protected Startup Time Sync
Added try-catch block around startup time synchronization to isolate failures:

```csharp
if (settingsStore.GetSettings().SyncOnStartup)
{
	try
	{
		await TrySyncNowAsync("startup", stoppingToken);
	}
	catch (Exception ex) when (ex is not OperationCanceledException)
	{
		logger.LogError(TimeSyncErrorEvent, ex, "Startup time sync failed.");
	}
}
```

**Benefit**: If the initial time sync fails (e.g., device not connected), the application continues running and will retry during scheduled sync intervals.

## Testing Recommendations

1. **Test normal startup**: Verify the application starts successfully with device connected
2. **Test without device**: Verify graceful degradation when device is not connected
3. **Test with insufficient permissions**: Run without admin privileges to ensure proper error logging
4. **Review logs**: Check that error messages are properly logged when device monitoring fails
5. **Verify functionality**: Ensure scheduled time sync still works after startup failures

## Prevention

To prevent similar issues in future hosted services:

1. **Always wrap initialization code** in try-catch blocks within `BackgroundService.ExecuteAsync`
2. **Use defensive programming** for external dependencies (Win32 APIs, hardware devices, network calls)
3. **Log detailed errors** to help diagnose environmental issues
4. **Design for graceful degradation** - allow the application to function with reduced capabilities
5. **Consider startup health checks** to detect and report initialization failures without crashing

## Impact
- **Before Fix**: Application would crash during startup with cryptic `TaskCanceledException`
- **After Fix**: Application starts successfully, logs detailed error messages about specific failures, and provides as much functionality as possible given the environment constraints
