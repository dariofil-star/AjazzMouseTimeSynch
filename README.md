# AJAZZ Mouse Time Sync

AJAZZ Mouse Time Sync is a .NET 10 Windows app/service that syncs the onboard clock of supported AJAZZ mice over HID/USB.

It provides:
- Automatic and manual time sync
- Device selection from detected AJAZZ HID interfaces
- Configurable sync interval
- Optional sync on app startup
- Optional sync when mouse connects
- Web UI for configuration and sync actions
- Console logging (console mode) and Event Viewer logging (Windows Service mode)

## Tested Devices

Tested with:
- **AJAZZ AJ179 Apex**

Expected to work with similar models (not fully validated):
- **AJAZZ AJ199**
- **AJAZZ AJ159**

## Requirements

- **Windows 10/11**
- **.NET 10 SDK** (for build) or **.NET 10 Runtime** (for run)
- Run from an **elevated (Administrator) Command Prompt/PowerShell** if you want the web interface to be available in console mode
- A supported AJAZZ mouse connected through the expected HID interface

## Features

- Enumerates AJAZZ HID devices and allows selecting a specific device
- Sends current local time to the mouse clock
- Sends a custom date/time to the mouse clock
- Interval-based automatic sync (enable/disable + hours)
- Sync on startup (enable/disable)
- Sync on device connect (enable/disable)
- Works in both console app mode and Windows Service mode
- Web UI remains available when running as a Windows Service

## Configuration

Configuration is stored in `appsettings.json`:

```json
{
  "Ajazz": {
    "WebPort": 5088,
    "SelectedDevicePath": "",
    "SyncIntervalHours": 1,
    "SyncIntervalEnabled": true,
    "SyncOnStartup": false,
    "SyncOnDeviceConnect": false
  }
}
```

- `WebPort`: HTTP port for the Web UI/API
- `SelectedDevicePath`: specific device path, or empty for auto-detect
- `SyncIntervalHours`: interval value in hours
- `SyncIntervalEnabled`: enables/disables interval sync
- `SyncOnStartup`: enables/disables startup sync
- `SyncOnDeviceConnect`: enables/disables sync on mouse connect event

## Precompiled Release

If you do not want to compile locally, download the precompiled release:
- https://github.com/dariofil-star/AjazzMouseTimeSynch/releases/tag/PublicRelease_1

## Run as Console App (from source)

From repository root:

```powershell
dotnet run --project .\AjazzMouseTimeSynch\AjazzMouseTimeSynch.csproj
```

Open browser:
- `http://127.0.0.1:5088` (or configured `WebPort`)

## Run Precompiled Binary in Command Prompt (and view console output)

1. Open **Command Prompt as Administrator**.
2. Navigate to the extracted release folder:

```cmd
cd C:\Apps\AjazzMouseTimeSynch
```

3. Run the executable directly:

```cmd
AjazzMouseTimeSynch.exe
```

4. Keep that console window open to see live output/logs.
5. Open the web UI at:
   - `http://127.0.0.1:5088` (or your configured `WebPort`)

## Publish for Service Deployment

```powershell
dotnet publish .\AjazzMouseTimeSynch\AjazzMouseTimeSynch.csproj -c Release -o C:\Apps\AjazzMouseTimeSynch
```

## Install as Windows Service

Run commands in an elevated terminal:

```powershell
sc.exe create AjazzMouseTimeSynch binPath= "C:\Apps\AjazzMouseTimeSynch\AjazzMouseTimeSynch.exe" start= auto
sc.exe start AjazzMouseTimeSynch
```

Service management:

```powershell
sc.exe stop AjazzMouseTimeSynch
sc.exe delete AjazzMouseTimeSynch
```

## Windows Service Notes

- The app reads/writes `appsettings.json` from the application content directory.
- Ensure the service identity has write permission to that folder if settings are changed via Web UI.
- Logging is written to **Windows Event Viewer** when running as a service.
- Web UI/API is still available while running as a service on the configured `WebPort`.

## Web UI Screenshot

![AJAZZ Clock Sync Web UI](docs/web-ui.png)
