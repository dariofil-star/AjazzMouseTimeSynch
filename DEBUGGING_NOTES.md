# Debugging Notes

## ObjectDisposedException in NetworkStream

### Issue Description
When debugging this application in Visual Studio, you may encounter an `ObjectDisposedException` with the message:
```
Cannot access a disposed object.
Object name: 'System.Net.Sockets.NetworkStream'.
```

### Root Cause
This exception is **NOT a bug in the application code**. It occurs in Visual Studio's Hot Reload and Browser Link infrastructure:

1. Visual Studio injects monitoring DLLs (`Microsoft.WebTools.BrowserLink.Net.dll`, `Microsoft.AspNetCore.Watch.BrowserRefresh.dll`) into your process
2. These tools create HTTP connections to your ASP.NET Core server for development features
3. The .NET HTTP client connection pool periodically performs "scavenging" to clean up stale connections
4. A race condition can occur where:
   - A connection is closed/disposed (by timeout or server closing it)
   - The pool's scavenger tries to check connection health with a zero-byte read
   - The read attempt finds the stream already disposed and throws

### Evidence
- Exception occurs in `HttpConnection.CheckUsabilityOnScavenge`
- Thread is a background thread pool worker (`.NET TP Worker`)
- No user code in the call stack
- The NetworkStream's `_disposed` flag is `true`
- The underlying socket is disconnected

### Solution Applied
The `launchSettings.json` has been updated to disable these Visual Studio features:

```json
{
  "environmentVariables": {
	"DOTNET_WATCH_SUPPRESS_BROWSER_REFRESH": "1"
  },
  "hotReloadEnabled": false
}
```

### Alternative: Debugger Exception Settings
If you still see this exception with the above settings:

1. In Visual Studio, go to **Debug → Windows → Exception Settings** (Ctrl+Alt+E)
2. Expand **Common Language Runtime Exceptions** → **System** → **System.ObjectDisposedException**
3. Uncheck the box (or add a condition to ignore when thrown from `System.Net.Http.dll`)

### Impact
This exception is **benign** and normally caught/handled internally by the HTTP stack. It does not affect your application's functionality.

### References
- This is a known pattern in HTTP connection pooling where background health checks race with connection disposal
- The scavenging operation is part of .NET's built-in connection pool management
- Visual Studio's development tools use HTTP to communicate with the running application
