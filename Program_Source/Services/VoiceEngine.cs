// ============================================================================
// INT VoiceToText — recording state machine (hotkey → record → transcribe → paste)
// ============================================================================

using System.Diagnostics;
using INTVoiceToText.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace INTVoiceToText.Services;

public enum EngineStatus { Idle, Recording, Transcribing }

/// <summary>
/// Coordinates microphone capture, local Whisper transcription and text insertion,
/// pushing real-time state changes to the UI through SignalR.
/// </summary>
public sealed class VoiceEngine : IDisposable
{
    private readonly AppState _state;
    private readonly AudioRecorder _recorder = new();
    private readonly SpeechService _speech = new();
    private readonly WordLibrary _wordLibrary = new();
    private HotkeyManager? _hotkeys;
    private readonly object _toggleGate = new();

    public IHubContext<VoiceHub>? Hub { get; set; }
    public Action? BringWindow { get; set; }
    public Action<string>? SetCompactMode { get; set; }  // "off", "recording", "transcribing", "ready"

    private volatile EngineStatus _status = EngineStatus.Idle;
    private volatile string? _lastError;
    public string? LastError => _lastError;
    private readonly string _debugPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "INT_VoiceToText", "debug.log");

    private void DebugLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        try { Directory.CreateDirectory(Path.GetDirectoryName(_debugPath)!); File.AppendAllText(_debugPath, line + Environment.NewLine); } catch { }
        try { if (_state.DebugMode) _ = Hub?.Clients.All.SendAsync("debug", new { line }); } catch { }
    }
    private IntPtr _targetWindow;          // window holding the cursor at recording start
    private long _recordingStartedTicks;   // Stopwatch ticks for duration reporting

    public EngineStatus Status => _status;
    public bool HotkeyRegistered => _hotkeys?.IsRegistered ?? false;
    public void ApplyAudioSettings()
    {
        _recorder.Configure(_state.MicrophoneDevice, _state.MicrophoneSensitivity);
        _speech.ConfigureCompute(_state.ComputeMode, _state.GpuDevice);
    }

    public VoiceEngine(AppState state)
    {
        _state = state;
        _recorder.Configure(state.MicrophoneDevice, state.MicrophoneSensitivity);
        _speech.ConfigureCompute(state.ComputeMode, state.GpuDevice);
        _recorder.LevelAvailable += level => Broadcast("level", new { level });
        _speech.ProgressChanged += progress => Broadcast("modelProgress", new { bytes = progress.Bytes, totalBytes = progress.TotalBytes, percent = progress.Percent, completed = progress.Completed });
    }

    /// <summary>Start hotkey hosting. Call once after the hub context is wired.</summary>
    public void Start()
    {
        DebugLog($"ENGINE_START hotkey={_state.HotkeyLabel()} device={_state.MicrophoneDevice} sensitivity={_state.MicrophoneSensitivity:0.00} modelReady={ModelReady}");
        if (_hotkeys != null) return;
        _hotkeys = new HotkeyManager();
        _hotkeys.HotkeyPressed += Toggle;
        var (mods, vk) = _state.ToWin32();
        bool ok = vk != 0 && _hotkeys.Register(mods, vk);
        DebugLog($"HOTKEY_REGISTER mods=0x{mods:X} vk=0x{vk:X} => {ok}");
    }

    /// <summary>Re-register hotkey after settings change.</summary>
    public bool ApplyHotkey()
    {
        _hotkeys?.Unregister();
        var (mods, vk) = _state.ToWin32();
        return vk == 0 || (_hotkeys != null && _hotkeys.Register(mods, vk));
    }

    /// <summary>Toggle record/stop without blocking the HTTP request or hotkey message loop.</summary>
    public void Toggle()
    {
        _ = Task.Run(() =>
        {
            lock (_toggleGate) OnToggle();
        });
    }

    private void OnToggle()
    {
        DebugLog($"TOGGLE status={_status}");
        switch (_status)
        {
            case EngineStatus.Idle:
                BeginRecording();
                break;

            case EngineStatus.Recording:
                FinishAndTranscribe();
                break;

            case EngineStatus.Transcribing:
                // Debounce — ignore extra presses while whisper is running.
                break;
        }
    }

    private void BeginRecording()
    {
        if (!_speech.IsReady)
        {
            _lastError = _speech.IsDownloading ? "MODEL_DOWNLOADING: Модель ещё скачивается — дождитесь окончания загрузки" : "MODEL_NOT_READY: Модель распознавания ещё не готова";
            Broadcast("error", new { message = _lastError });
            DebugLog("RECORD_REJECTED " + _lastError);
            return;
        }
        DebugLog($"RECORD_START device={_state.MicrophoneDevice} sensitivity={_state.MicrophoneSensitivity:0.00}");
        try { _recorder.Start(); }
        catch (Exception ex)
        {
            DebugLog("RECORD_START_ERROR " + ex);
            _lastError = "MICROPHONE_ERROR: " + ex.Message;
            Broadcast("error", new { message = _lastError });
            return;
        }

        _lastError = null;
        // Remember which window has focus NOW — that's where the cursor lives.
        _targetWindow = Native.W32.GetForegroundWindow();
        _recordingStartedTicks = Stopwatch.GetTimestamp();
        try { SetCompactMode?.Invoke("recording"); } catch { }
        SetStatus(EngineStatus.Recording);
    }

    private void FinishAndTranscribe()
    {
        float[] samples;
        DebugLog("RECORD_STOP requested");
        try { samples = _recorder.Stop(); }
        catch (Exception ex)
        {
            DebugLog("RECORD_STOP_ERROR " + ex);
            _lastError = "STOP_ERROR: " + ex.Message;
            Broadcast("error", new { message = _lastError });
            SetStatus(EngineStatus.Idle);
            return;
        }

        DebugLog($"RECORD_STOP samples={samples.Length} seconds={samples.Length / 16000.0:0.00}");
        try { SetCompactMode?.Invoke("transcribing"); } catch { }
        if (samples.Length < 16000 / 4) // less than ~250 ms of audio — treat as accidental tap
        {
            DebugLog("RECORD_REJECTED too_short");
            _lastError = "TOO_SHORT";
            Broadcast("error", new { message = _lastError });
            SetStatus(EngineStatus.Idle);
            return;
        }

        DebugLog("TRANSCRIBE_START");
        SetStatus(EngineStatus.Transcribing);

        var startedAt = _recordingStartedTicks;
        var durationMs = (long)(Stopwatch.GetTimestamp() - startedAt) * 1000L / Stopwatch.Frequency;

        // Transcribe on a worker thread — never block the UI or hotkey threads.
        Task.Run(async () => await RunTranscription(samples, durationMs).ConfigureAwait(false));
    }

    private async Task RunTranscription(float[] samples, long recordingDurationMs)
    {
        try
        {
            var text = await _speech.TranscribeAsync(samples, _state.Language).ConfigureAwait(false);
            var rawText = text;
            text = _wordLibrary.Apply(text);
            DebugLog($"TRANSCRIBE_DONE chars={text.Length} dictionaryRules={_wordLibrary.Count} raw={rawText.Replace("\r", " ").Replace("\n", " ").Trim()} text={text.Replace("\r", " ").Replace("\n", " ").Trim()}");

            // The clipboard is ALWAYS updated so the user can Ctrl+V anywhere.
            if (!string.IsNullOrWhiteSpace(text)) { TextInserter.CopyToClipboard(text); DebugLog("CLIPBOARD_SET ok"); }

            bool pasted = false;
            if (_state.PasteResult && !string.IsNullOrWhiteSpace(text) && _targetWindow != IntPtr.Zero)
            {
                try { TextInserter.Insert(text, _targetWindow); pasted = true; }
                catch (Exception pasteEx)
                {
                    Broadcast("error", new { message = "PASTE_ERROR: " + pasteEx.Message });
                }
            }

            Broadcast("text", new { text, durationMs = recordingDurationMs, pasted });
        }
        catch (Exception ex)
        {
            DebugLog("TRANSCRIBE_ERROR " + ex);
            if (ex.Message.Contains("MODEL_DOWNLOADING"))
                _lastError = "MODEL_DOWNLOADING: Модель ещё скачивается — дождитесь окончания загрузки";
            else if (ex is FileNotFoundException || ex.Message.Contains("Native Library"))
                _lastError = "RUNTIME_MISSING: Отсутствуют файлы Whisper runtime. Откройте Настройки → Whisper Runtime для диагностики.";
            else
                _lastError = "TRANSCRIBE_ERROR: " + ex.Message;
            Broadcast("error", new { message = _lastError });
        }
        finally
        {
            SetStatus(EngineStatus.Idle);
            _ = Task.Run(async () => { await Task.Delay(3000); try { SetCompactMode?.Invoke("off"); } catch { } });
        }
    }

    private void SetStatus(EngineStatus status)
    {
        _status = status;
        Broadcast("state", new
        {
            status = status.ToString().ToLowerInvariant(), // "idle" | "recording" | "transcribing"
            t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
    }

    private void Broadcast(string method, object payload)
    {
        try
        {
            // Never block the NAudio callback or the hotkey thread on network I/O.
            _ = Hub?.Clients.All.SendAsync(method, payload);
        }
        catch { /* UI may not be connected yet — never crash the engine */ }
    }

    /// <summary>Current model status for the settings panel.</summary>
    public bool ModelReady => _speech.IsReady;

    /// <summary>Check which native Whisper DLLs exist beside the exe.</summary>
    public static object GetRuntimeDiagnostics()
    {
        var dir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        string[] critical = ["whisper.dll", "ggml-whisper.dll", "ggml-base-whisper.dll", "ggml-cpu-whisper.dll"];
        var dlls = new List<object>();
        foreach (var name in critical)
        {
            var fullPath = Path.Combine(dir, name);
            var exists = File.Exists(fullPath);
            var size = exists ? new FileInfo(fullPath).Length : 0L;
            dlls.Add(new { name, exists, sizeKB = size / 1024 });
        }
        var nativeDir = Path.Combine(dir, "runtimes", "win-x64", "native");
        return new { directory = dir, runtimesDirExists = Directory.Exists(nativeDir), dlls };
    }

    /// <summary>True while the large model is being downloaded for the first time.</summary>
    public bool ModelDownloading => _speech.IsDownloading;

    /// <summary>Bytes/progress of the ongoing (or completed) model download.</summary>
    public SpeechService.ModelProgress ModelProgress => _speech.Progress;

    /// <summary>Prefetch/locate the STT model (called at startup, non-blocking).</summary>
    public Task WarmupModelAsync() => _speech.EnsureModelAsync();

    /// <summary>List all model files in models/ dir with sizes and validity.</summary>
    public static object GetModelList() => SpeechService.GetModelList();

    /// <summary>Force re-download of the model.</summary>
    public Task<object> RetryModelDownloadAsync(CancellationToken ct) => _speech.RetryDownloadAsync(ct);

    /// <summary>Download a specific model by id (tiny/base/small/medium/large-v3).</summary>
    public Task<string?> DownloadModelAsync(string modelId, CancellationToken ct) => _speech.DownloadModelAsync(modelId, ct);

    /// <summary>Last download error (null if none).</summary>
    public string? ModelLastError => _speech.LastError;

    public void Dispose()
    {
        try { _hotkeys?.Dispose(); } catch { }
        _recorder.Dispose();
        _speech.Dispose();
    }
}
