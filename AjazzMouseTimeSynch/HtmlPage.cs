public static class HtmlPage
{
    public static string Content => """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>AJAZZ Clock Sync</title>
  <style>
    :root {
      color-scheme: dark;
      --bg: #060b16;
      --panel: rgba(15, 23, 42, 0.82);
      --muted: #9fb0d6;
      --text: #edf4ff;
      --primary: #5b8cff;
      --primary-hover: #7ca6ff;
      --success: #22d190;
      --border: rgba(145, 169, 221, 0.2);
      --shadow: 0 22px 60px rgba(0,0,0,.45);
    }

    * { box-sizing: border-box; }

    body {
      margin: 0;
      font-family: "Segoe UI", Inter, system-ui, sans-serif;
      background:
        radial-gradient(1200px 700px at -10% -15%, #2a3f80 0%, transparent 45%),
        radial-gradient(900px 500px at 120% 110%, #1b7b63 0%, transparent 45%),
        var(--bg);
      color: var(--text);
      min-height: 100vh;
      display: flex;
      align-items: center;
      justify-content: center;
      padding: 26px;
      overflow: hidden;
    }

    .aurora {
      position: fixed;
      inset: -25%;
      z-index: -1;
      background:
        conic-gradient(from 220deg at 40% 35%, #4f78ff55, transparent 35%),
        conic-gradient(from 20deg at 70% 65%, #1fd18d44, transparent 35%);
      filter: blur(28px);
      animation: spin 26s linear infinite;
    }

    @keyframes spin {
      from { transform: rotate(0deg) scale(1.05); }
      to { transform: rotate(360deg) scale(1.05); }
    }

    .card {
      width: 100%;
      max-width: 920px;
      background: linear-gradient(180deg, rgba(20, 30, 55, 0.96), var(--panel));
      border: 1px solid var(--border);
      border-radius: 20px;
      padding: 26px;
      box-shadow: var(--shadow);
      backdrop-filter: blur(8px);
      animation: rise .45s ease-out;
    }

    @keyframes rise {
      from { opacity: 0; transform: translateY(8px); }
      to { opacity: 1; transform: translateY(0); }
    }

    h1 { margin: 0 0 8px; font-size: 1.68rem; font-weight: 700; letter-spacing: .01em; }
    p { margin: 0 0 18px; color: var(--muted); }

    .section {
      border: 1px solid var(--border);
      border-radius: 14px;
      padding: 14px;
      margin-bottom: 14px;
      background: rgba(7, 14, 27, .4);
      transition: border-color .2s ease, transform .2s ease;
    }

    .section:hover {
      border-color: rgba(154, 184, 255, 0.35);
      transform: translateY(-1px);
    }

    .section-title {
      margin: 0 0 10px;
      font-size: .86rem;
      letter-spacing: .08em;
      color: #cedbfd;
      text-transform: uppercase;
      font-weight: 700;
    }

    .row { display: grid; grid-template-columns: 1fr auto; gap: 12px; margin-bottom: 12px; }
    .controls { display: grid; grid-template-columns: 1fr 190px auto auto; gap: 12px; margin-bottom: 10px; }
    .custom-controls { display: grid; grid-template-columns: 1fr auto; gap: 12px; }
    .toggle-grid { display: grid; grid-template-columns: repeat(3, minmax(180px, 1fr)); gap: 10px; }

    input, select, button {
      border: 1px solid var(--border);
      border-radius: 11px;
      background: #0c152a;
      color: var(--text);
      padding: 11px 12px;
      font-size: .95rem;
      transition: .18s ease;
    }

    input:focus, select:focus {
      outline: none;
      border-color: #7ea5ff;
      box-shadow: 0 0 0 3px rgba(126, 165, 255, .18);
    }

    button {
      cursor: pointer;
      background: linear-gradient(180deg, #6b98ff, #507be6);
      border: none;
      font-weight: 600;
      box-shadow: 0 8px 16px rgba(25, 49, 102, .35);
    }

    button:hover { transform: translateY(-1px); background: linear-gradient(180deg, #84abff, #5b89ef); }
    button:active { transform: translateY(0); }
    button.secondary { background: linear-gradient(180deg, #334a76, #243757); }
    button.success { background: linear-gradient(180deg, #2ce3a2, #18b87f); color: #052819; }

    .toggle {
      display: flex;
      align-items: center;
      gap: 9px;
      background: #0d1528;
      border: 1px solid var(--border);
      border-radius: 11px;
      padding: 10px 12px;
      font-size: .88rem;
      color: var(--muted);
      transition: border-color .2s ease;
    }

    .toggle:hover { border-color: rgba(152, 185, 255, .35); }

    .toggle input[type="checkbox"] {
      width: 16px;
      height: 16px;
      accent-color: var(--primary);
      margin: 0;
    }

    .status {
      min-height: 24px;
      color: var(--muted);
      font-size: .93rem;
      margin: 8px 2px 0;
      transition: color .2s ease;
    }

    .small { color: var(--muted); font-size: .84rem; word-break: break-all; }

    .chip {
      display: inline-block;
      margin-left: 8px;
      padding: 3px 8px;
      border-radius: 999px;
      background: #1f2f53;
      font-size: .76rem;
      color: #c8d8ff;
      border: 1px solid rgba(140, 170, 240, .25);
      vertical-align: middle;
    }

    @media (max-width: 920px) {
      .controls { grid-template-columns: 1fr; }
      .custom-controls { grid-template-columns: 1fr; }
      .toggle-grid { grid-template-columns: 1fr; }
      .row { grid-template-columns: 1fr; }
    }
  </style>
</head>
<body>
  <div class="aurora"></div>

  <div class="card">
    <h1>AJAZZ Clock Sync <span class="chip" id="portChip">Port</span></h1>
    <p>Select your AJAZZ mouse, control auto-sync behavior, and push custom time.</p>

    <div class="section">
      <div class="section-title">Device Selection</div>
      <div class="row">
        <select id="deviceSelect"></select>
        <button class="secondary" id="refreshBtn">Refresh Devices</button>
      </div>
      <div class="small" id="selectedPath"></div>
    </div>

    <div class="section">
      <div class="section-title">Automatic Sync</div>
      <div class="controls">
        <input id="intervalHours" type="number" min="1" step="1" placeholder="Sync interval (hours)" />
        <button id="saveBtn">Save Settings</button>
        <button class="success" id="syncBtn">Sync Now</button>
        <button class="secondary" id="reloadBtn">Reload</button>
      </div>
      <div class="toggle-grid">
        <label class="toggle"><input id="syncIntervalEnabled" type="checkbox" /> Enable Automatic Sync</label>
        <label class="toggle"><input id="syncOnStartup" type="checkbox" /> Sync on App Startup</label>
        <label class="toggle"><input id="syncOnDeviceConnect" type="checkbox" /> Sync when Mouse Connects</label>
      </div>
    </div>

    <div class="section">
      <div class="section-title">Manual Custom Date & Time</div>
      <div class="custom-controls">
        <input id="customDateTime" type="datetime-local" />
        <button class="success" id="syncCustomBtn">Sync Custom Time</button>
      </div>
    </div>

    <div class="status" id="status"></div>
  </div>

  <script>
    const deviceSelect = document.getElementById('deviceSelect');
    const intervalHours = document.getElementById('intervalHours');
    const customDateTime = document.getElementById('customDateTime');
    const syncIntervalEnabled = document.getElementById('syncIntervalEnabled');
    const syncOnStartup = document.getElementById('syncOnStartup');
    const syncOnDeviceConnect = document.getElementById('syncOnDeviceConnect');
    const status = document.getElementById('status');
    const selectedPath = document.getElementById('selectedPath');
    const portChip = document.getElementById('portChip');

    async function call(url, options) {
      const res = await fetch(url, options);
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      return res.headers.get('content-type')?.includes('application/json') ? await res.json() : null;
    }

    function setStatus(text, ok = true) {
      status.textContent = text;
      status.style.color = ok ? '#9ef2cb' : '#ffb8b8';
    }

    function toLocalDateTimeInputValue(date) {
      const pad = (n) => n.toString().padStart(2, '0');
      return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
    }

    function renderDevices(devices) {
      const previous = deviceSelect.value;
      deviceSelect.innerHTML = '';

      const emptyOption = document.createElement('option');
      emptyOption.value = '';
      emptyOption.textContent = 'Auto-detect first compatible AJAZZ device';
      deviceSelect.appendChild(emptyOption);

      for (const d of devices) {
        const option = document.createElement('option');
        option.value = d.devicePath;
        option.textContent = `${d.productName} (VID:${d.vendorId.toString(16).toUpperCase().padStart(4, '0')} PID:${d.productId.toString(16).toUpperCase().padStart(4, '0')})`;
        if (d.isSelected) option.selected = true;
        deviceSelect.appendChild(option);
      }

      if (!Array.from(deviceSelect.options).some(o => o.selected)) {
        deviceSelect.value = previous || '';
      }
    }

    async function loadAll() {
      try {
        const [settings, devices] = await Promise.all([call('/api/settings'), call('/api/devices')]);

        intervalHours.value = settings.syncIntervalHours;
        syncIntervalEnabled.checked = !!settings.syncIntervalEnabled;
        syncOnStartup.checked = !!settings.syncOnStartup;
        syncOnDeviceConnect.checked = !!settings.syncOnDeviceConnect;

        renderDevices(devices);
        selectedPath.textContent = settings.selectedDevicePath
          ? `Selected: ${settings.selectedDevicePath}`
          : 'Selected: auto-detect';

        portChip.textContent = `Port ${settings.webPort}`;
        if (!customDateTime.value) customDateTime.value = toLocalDateTimeInputValue(new Date());

        setStatus('Configuration loaded.');
      } catch (err) {
        setStatus(`Load failed: ${err.message}`, false);
      }
    }

    document.getElementById('refreshBtn').addEventListener('click', async () => {
      try {
        const devices = await call('/api/devices');
        renderDevices(devices);
        setStatus('Device list refreshed.');
      } catch (err) {
        setStatus(`Refresh failed: ${err.message}`, false);
      }
    });

    document.getElementById('saveBtn').addEventListener('click', async () => {
      try {
        const payload = {
          selectedDevicePath: deviceSelect.value,
          syncIntervalHours: Number(intervalHours.value || 1),
          syncIntervalEnabled: syncIntervalEnabled.checked,
          syncOnStartup: syncOnStartup.checked,
          syncOnDeviceConnect: syncOnDeviceConnect.checked
        };

        const updated = await call('/api/settings', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(payload)
        });

        intervalHours.value = updated.syncIntervalHours;
        syncIntervalEnabled.checked = !!updated.syncIntervalEnabled;
        syncOnStartup.checked = !!updated.syncOnStartup;
        syncOnDeviceConnect.checked = !!updated.syncOnDeviceConnect;
        selectedPath.textContent = updated.selectedDevicePath
          ? `Selected: ${updated.selectedDevicePath}`
          : 'Selected: auto-detect';

        setStatus('Settings saved.');
      } catch (err) {
        setStatus(`Save failed: ${err.message}`, false);
      }
    });

    document.getElementById('syncBtn').addEventListener('click', async () => {
      try {
        const result = await call('/api/sync', { method: 'POST' });
        setStatus(result.success ? 'Manual sync succeeded.' : 'Manual sync failed (device unavailable or rejected).', result.success);
      } catch (err) {
        setStatus(`Sync failed: ${err.message}`, false);
      }
    });

    document.getElementById('syncCustomBtn').addEventListener('click', async () => {
      try {
        if (!customDateTime.value) {
          setStatus('Select a custom date and time first.', false);
          return;
        }

        const payload = { targetDateTime: customDateTime.value };
        const result = await call('/api/sync/custom', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(payload)
        });

        setStatus(result.success
          ? `Custom sync succeeded for ${result.timestamp}.`
          : 'Custom sync failed (device unavailable or rejected).', result.success);
      } catch (err) {
        setStatus(`Custom sync failed: ${err.message}`, false);
      }
    });

    document.getElementById('reloadBtn').addEventListener('click', () => loadAll());

    loadAll();
  </script>
</body>
</html>
""";
}
