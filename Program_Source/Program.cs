// ============================================================================
// INT VoiceToText v1.0 — entry point
// Frontend: React 19 + TS (wwwroot, embedded) • Backend: ASP.NET Core + SignalR
// Platform: Photino.NET 4 native window • STT: Whisper (local, offline)
// ============================================================================

using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using INTVoiceToText.Hubs;
using INTVoiceToText.Native;
using INTVoiceToText.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Photino.NET;

// ---------------------------------------------------------------------------
// 1. Application state (persisted in %LOCALAPPDATA%\INT_VoiceToText)
// ---------------------------------------------------------------------------
internal static class Program
{
    [STAThread]
    public static async Task Main(string[] args)
    {
        var state = AppState.Load();
        state.StartWithWindows = StartupManager.IsEnabled();

// ---------------------------------------------------------------------------
// 2. ASP.NET Core backend (localhost only) + SignalR realtime push
// ---------------------------------------------------------------------------
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:0"); // ephemeral loopback port — no conflicts

// The Web SDK wires up wwwroot (static web assets) automatically, including the
// single-file embedded copy — do NOT override UseWebRoot here.

builder.Services.AddSignalR(); // required before MapHub<VoiceHub>

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// Fallback: serve the React app from embedded resources when no physical wwwroot exists.
app.Use(async (ctx, next) =>
{
    await next();
    if (!ctx.Response.HasStarted && ctx.Response.StatusCode == 404)
        await INTVoiceToText.Web.EmbeddedWebRoot.TryServeAsync(ctx);
});

app.MapHub<VoiceHub>("/hubs/voice");

// ---------------------------------------------------------------------------
// 3. Engine: hotkeys → mic capture → local Whisper → paste at cursor
// ---------------------------------------------------------------------------
var engine = new VoiceEngine(state);
engine.Hub = app.Services.GetRequiredService<IHubContext<VoiceHub>>();

app.MapGet("/api/state", () => Results.Ok(new
{
    version = AppState.AppVersion,
    status = engine.Status.ToString().ToLowerInvariant(),
    hotkey = new { modifiers = state.Hotkey.Modifiers, key = state.Hotkey.Key, label = state.HotkeyLabel() },
    hotkeyRegistered = engine.HotkeyRegistered,
    lastError = engine.LastError,
    language = state.Language,
    pasteResult = state.PasteResult,
    microphones = INTVoiceToText.Services.AudioRecorder.GetDevices()
        .Select(d => new { index = d.index, name = d.name }),
    microphoneDevice = state.MicrophoneDevice,
    microphoneSensitivity = state.MicrophoneSensitivity,
    startWithWindows = state.StartWithWindows,
    computeMode = state.ComputeMode,
    gpuDevice = state.GpuDevice,
    minimizeToTrayOnClose = state.MinimizeToTrayOnClose,
    debugMode = state.DebugMode,
    modelReady = engine.ModelReady,
}));

app.MapGet("/api/debug/log", () =>
{
    var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "INT_VoiceToText", "debug.log");
    try { return Results.Text(File.Exists(path) ? File.ReadAllText(path) : "(debug log пока пуст)"); }
    catch (Exception ex) { return Results.Text("DEBUG_READ_ERROR: " + ex.Message); }
});

app.MapPost("/api/settings", (SettingsUpdateBody body) =>
{
    if (body.hotkey is { } hk)
    {
        var key = (hk.Key ?? "").Trim();
        uint vk = AppState.VkForKey(key);
        if (vk == 0) return Results.BadRequest(new { error = "UNSUPPORTED_KEY" });

        var mods = (hk.Modifiers ?? new List<string>())
                   .Where(m => m is "ctrl" or "shift" or "alt" or "win")
                   .Distinct()
                   .ToList();

        state.Hotkey = new HotkeyConfig(mods, key);
    }

    if (!string.IsNullOrWhiteSpace(body.language))
        state.Language = body.language.Trim().ToLowerInvariant();

    if (body.pasteResult.HasValue)
        state.PasteResult = body.pasteResult.Value;
    if (body.microphoneDevice.HasValue)
        state.MicrophoneDevice = Math.Max(0, body.microphoneDevice.Value);
    if (body.microphoneSensitivity.HasValue)
        state.MicrophoneSensitivity = Math.Clamp(body.microphoneSensitivity.Value, 0.25f, 10f);
    state.ComputeMode = string.IsNullOrWhiteSpace(body.computeMode) ? state.ComputeMode : body.computeMode.Trim().ToLowerInvariant();
    if (body.gpuDevice.HasValue) state.GpuDevice = body.gpuDevice.Value;
    if (body.debugMode.HasValue)
        state.DebugMode = body.debugMode.Value;
    if (body.startWithWindows.HasValue)
        StartWindows(state, body.startWithWindows.Value);
    if (body.minimizeToTrayOnClose.HasValue) state.MinimizeToTrayOnClose = body.minimizeToTrayOnClose.Value;
    engine.ApplyAudioSettings();

    state.Save();

    bool hotkeyOk = true;
    if (body.hotkey is not null)
        hotkeyOk = engine.ApplyHotkey(); // re-register global hotkey immediately

    return Results.Ok(new { ok = hotkeyOk, label = state.HotkeyLabel() });
});

app.MapPost("/api/toggle", () =>
{
    engine.Toggle(); // same path as the global hotkey (UI stop button)
    return Results.NoContent();
});

var updater = new UpdateService();

app.MapPost("/api/update/install", async (UpdateInstallBody body) =>
{
    try
    {
        var update = new AvailableUpdate(body.version, body.releaseUrl, body.assetUrl, body.assetName);
        var zip = await updater.DownloadAsync(update);
        var target = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var updaterExe = Path.Combine(target, "INT_VoiceToText.Updater.exe");
        if (!File.Exists(updaterExe)) return Results.NotFound(new { error = "UPDATER_NOT_INSTALLED" });
        var exe = Environment.ProcessPath ?? Path.Combine(target, "INT_VoiceToText.exe");
        Process.Start(new ProcessStartInfo(updaterExe, $"--pid {Environment.ProcessId} --zip \"{zip}\" --target \"{target}\" --exe \"{exe}\"") { WorkingDirectory = target, UseShellExecute = true });
        _ = Task.Run(async () => { await Task.Delay(500); Environment.Exit(0); });
        return Results.Ok(new { started = true });
    }
    catch (Exception ex) { return Results.Problem(ex.Message); }
});

await app.StartAsync();

var serverAddresses = app.Services.GetRequiredService<IServer>()
                                 .Features.Get<IServerAddressesFeature>()!;
string baseUrl = serverAddresses.Addresses.First(a => a.StartsWith("http://"));
if (!baseUrl.EndsWith('/')) baseUrl += "/";
var appUrl = new Uri(baseUrl);

// Small diagnostic log next to the exe (helps with support & port discovery).
try
{
    var dir = Environment.ProcessPath != null ? Path.GetDirectoryName(Environment.ProcessPath)! : AppContext.BaseDirectory;
    File.WriteAllText(Path.Combine(dir, "int_voicetext_debug.log"),
        $"started {DateTime.Now:yyyy-MM-dd HH:mm:ss}\nbackend {appUrl}\nversion {AppState.AppVersion}");
}
catch { /* non-fatal */ }

// Hotkeys live + model warm-up in the background (offline after first use).
engine.Start();
_ = engine.WarmupModelAsync();

// ---------------------------------------------------------------------------
// 3b. GitHub Releases auto-update check (non-blocking, silent on failure)
// ---------------------------------------------------------------------------
_ = Task.Run(async () =>
{
    try
    {
        var update = await updater.CheckAsync().ConfigureAwait(false);
        if (update != null)
            try { await app.Services.GetRequiredService<IHubContext<VoiceHub>>().Clients.All.SendAsync("update", new { version = update.Version, releaseUrl = update.ReleaseUrl, assetUrl = update.AssetUrl, assetName = update.AssetName }); } catch { }
    }
    catch { /* offline/rate-limited — silently skip */ }
});

Debug.WriteLine($"[INT VoiceToText] backend ready at {appUrl}");

// ---------------------------------------------------------------------------
// 4. Photino.NET native window — compact recorder card, top-right corner
// ---------------------------------------------------------------------------
// Spacious layout: settings and recording visualization remain visible without clipping.
const int WinW = 760;
const int WinH = 840;

// WebView2 requires COM STA. ASP.NET Core may initialize the hosting thread as MTA,
// so the native Photino window gets its own dedicated STA message-loop thread.
var uiThread = new Thread(() =>
{
    try
    {
        var window = new PhotinoWindow(null); // null parent → top-level window
        IntPtr windowHandle = IntPtr.Zero;
        window.SetTitle("INT VoiceToText v1.3")
              .SetUseOsDefaultSize(false)
              .SetSize(WinW, WinH)
              .SetMinSize(360, 100)
              .SetJavascriptClipboardAccessEnabled(true)
              .SetDevToolsEnabled(true)
              .Load(appUrl);

        window.RegisterWindowCreatedHandler((_, _) =>
        {
            try
            {
                windowHandle = W32.FindMainWindow();
                try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "int_voicetext_debug.log"), $"CREATED hwnd={windowHandle}\n"); } catch { }
                var wa = W32.GetWorkArea();
                window.MoveTo(wa.Right - WinW - 24, wa.Top + 24, true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[INT VoiceToText] position failed: " + ex.Message);
            }
        });

        // A global hotkey can arrive on another thread: raise the native HWND directly (no UI-thread hop).
        bool allowExit = false;
        bool hiddenInTray = false;
        // Tray icon switches colour with recording state: green = recording, blue = ready.
        var trayBluePath = Path.Combine(AppContext.BaseDirectory, "tray-blue.ico");
        var trayGreenPath = Path.Combine(AppContext.BaseDirectory, "tray-green.ico");
        using var trayIcon = new Icon(File.Exists(trayBluePath) ? trayBluePath : Path.Combine(AppContext.BaseDirectory, "icon.ico"));
        using var tray = new NotifyIcon
        {
            Visible = true,
            Text = "INT VoiceToText — ожидание записи",
            Icon = File.Exists(trayBluePath) ? new Icon(trayBluePath) : trayIcon
        };

        void SetTrayState(bool recording)
        {
            try
            {
                var icon = recording ? trayGreenPath : trayBluePath;
                tray.Icon = new Icon(File.Exists(icon) ? icon : Path.Combine(AppContext.BaseDirectory, "icon.ico"));
                tray.Text = recording ? "INT VoiceToText — идёт запись…" : "INT VoiceToText — ожидание записи";
            }
            catch { /* keep previous icon on failure */ }
        }
        IntPtr CurrentHwnd() => windowHandle != IntPtr.Zero ? windowHandle : W32.FindMainWindow();
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Открыть настройки", null, (_, _) =>
        {
            hiddenInTray = false;
            var hwnd = CurrentHwnd();
            if (hwnd != IntPtr.Zero) { W32.SetTopMost(hwnd, false); W32.ShowWindowToFront(hwnd); }
        });
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Выход", null, (_, _) =>
        {
            allowExit = true;
            tray.Visible = false;
            window.Close();
        });
        tray.ContextMenuStrip = trayMenu;
        tray.DoubleClick += (_, _) =>
        {
            hiddenInTray = false;
            var hwnd = CurrentHwnd();
            if (hwnd != IntPtr.Zero) { W32.SetTopMost(hwnd, false); W32.ShowWindowToFront(hwnd); }
        };

        window.RegisterWindowClosingHandler((_, _) =>
        {
            if (!allowExit && state.MinimizeToTrayOnClose)
            {
                hiddenInTray = true;
                var hwnd = CurrentHwnd();
                if (hwnd != IntPtr.Zero) W32.HideWindow(hwnd);
                return true;
            }
            tray.Visible = false;
            return false;
        });

        engine.BringWindow = () => { var hwnd = CurrentHwnd(); if (hwnd != IntPtr.Zero) W32.ShowWindowToFront(hwnd); };
        engine.SetCompactMode = mode =>
        {
            var hwnd = CurrentHwnd();
            if (hwnd == IntPtr.Zero) return;
            var wa = W32.GetWorkArea();
            bool compact = mode is "recording" or "ready";
            SetTrayState(mode == "recording");
            if (!compact && hiddenInTray) { W32.SetTopMost(hwnd, false); W32.HideWindow(hwnd); return; }
            bool overlayOnly = hiddenInTray && compact; // in tray → tiny overlay bar, not the full UI
            int width = overlayOnly ? 500 : compact ? 430 : WinW;
            int height = overlayOnly ? 200 : compact ? 360 : WinH;
            if (compact) { W32.ConstrainedShow(hwnd, wa, width, height); W32.SetTopMost(hwnd, true); }
            else { W32.SetTopMost(hwnd, false); W32.ResizeWindow(hwnd, wa.Right - width - 24, wa.Top + 24, width, height); }
        };

        window.WaitForClose();
    }
    catch (Exception ex)
    {
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "int_voicetext_debug.log"), $"UI ERROR: {ex}\n"); } catch { }
    }
});
uiThread.SetApartmentState(ApartmentState.STA);
uiThread.IsBackground = false;
uiThread.Start();
uiThread.Join();

Debug.WriteLine("[INT VoiceToText] window closed — shutting down");
await app.StopAsync();
engine.Dispose();
    }
    private static void StartWindows(AppState state, bool enabled)
    {
        state.StartWithWindows = enabled;
        StartupManager.Apply(enabled);
        state.Save();
    }
}

record SettingsUpdateBody(HotkeyConfig? hotkey, string? language, bool? pasteResult, int? microphoneDevice, float? microphoneSensitivity, bool? debugMode, bool? startWithWindows, bool? minimizeToTrayOnClose, string? computeMode, int? gpuDevice);
record UpdateInstallBody(string version, string releaseUrl, string assetUrl, string assetName);
