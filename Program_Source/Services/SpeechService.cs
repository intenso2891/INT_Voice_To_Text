// ============================================================================
// INT VoiceToText — local speech-to-text (Whisper.net, fully offline)
// ============================================================================

using Whisper.net;
using Whisper.net.Ggml;

namespace INTVoiceToText.Services;

/// <summary>
/// Runs Whisper locally on this machine. No audio ever leaves the PC.
/// Model resolution order:
///   1. models\ggml-*.bin next to the executable (shipped with the app)
///   2. folder beside the executable (portable installation)
///   3. one-time download via WhisperGgmlDownloader (then cached beside the exe)
/// </summary>
public sealed class SpeechService : IDisposable
{
    private const string ModelFileName = "ggml-large-v3";
    private const long LargeV3Bytes = 3_095_033_483;
    public event Action<ModelProgress>? ProgressChanged;
    public bool IsDownloading { get; private set; }
    public ModelProgress Progress { get; private set; } = new(0, LargeV3Bytes, false);

    public sealed record ModelProgress(long Bytes, long TotalBytes, bool Completed)
    {
        public int Percent => TotalBytes > 0 ? Math.Clamp((int)(Bytes * 100L / TotalBytes), 0, 100) : 0;
    }
    private readonly object _gate = new();
    private readonly SemaphoreSlim _modelGate = new(1, 1);
    private WhisperFactory? _factory;

    public string? ModelPath { get; private set; }
    private bool _useGpu;
    private int _gpuDevice;
    public bool IsReady => !string.IsNullOrEmpty(ModelPath);

    public void ConfigureCompute(string mode, int gpuDevice)
    {
        lock (_gate)
        {
            _useGpu = !string.Equals(mode, "cpu", StringComparison.OrdinalIgnoreCase);
            _gpuDevice = Math.Max(0, gpuDevice);
            _factory?.Dispose();
            _factory = null;
        }
    }

    /// <summary>Locate (or download) the ggml model. Safe to call repeatedly.</summary>
    public async Task<string?> EnsureModelAsync(CancellationToken ct = default, Action<string>? onProgress = null)
    {
        await _modelGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrEmpty(ModelPath)) return ModelPath;

            var candidates = new List<string>
            {
                Path.Combine(AppContext.BaseDirectory, "models"),                       // shipped / embedded
                Path.Combine(ExeDir(), "models"),                                        // folder next to exe (single-file case)
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                             "INT_VoiceToText", "models"),                              // user cache
            };

            foreach (var dir in candidates.Distinct())
            {
                if (!Directory.Exists(dir)) continue;
                var bin = Directory.EnumerateFiles(dir, ModelFileName + ".bin")
                    .Where(FileValid)
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (bin != null)
                {
                    ModelPath = bin;
                    Progress = Progress with { Bytes = LargeV3Bytes, Completed = true };
                    ProgressChanged?.Invoke(Progress);
                    return bin;
                }
            }

            // Not found locally → one-time download (requires internet once only).
            onProgress?.Invoke("downloading");
            // Keep the large model beside the portable executable, so it is easy to find and move with the app.
            var targetDir = Path.Combine(ExeDir(), "models");
            Directory.CreateDirectory(targetDir);
            var targetPath = Path.Combine(targetDir, ModelFileName + ".bin");

            if (FileValid(targetPath))
            {
                ModelPath = targetPath;
                Progress = Progress with { Bytes = LargeV3Bytes, Completed = true };
                ProgressChanged?.Invoke(Progress);
                return targetPath;
            }
            File.Delete(targetPath); // corrupt/partial leftover → re-download

            // Atomic download: write to .part first so a crash can't leave a fake model.
            var partPath = targetPath + ".downloading";
            IsDownloading = true;
            ProgressChanged?.Invoke(Progress with { Bytes = 0, Completed = false });
            try
            {
                using var stream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(
                    GgmlType.LargeV3, QuantizationType.NoQuantization, ct).ConfigureAwait(false);

                using (var file = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    var buffer = new byte[128 * 1024];
                    int read;
                    while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        Progress = Progress with { Bytes = file.Length };
                        ProgressChanged?.Invoke(Progress);
                    }
                    await file.FlushAsync(ct).ConfigureAwait(false);
                }

                if (!FileValid(partPath)) throw new IOException("Downloaded model failed the integrity check.");
                if (File.Exists(targetPath)) File.Delete(targetPath);
                File.Move(partPath, targetPath);

                ModelPath = targetPath;
                Progress = Progress with { Bytes = LargeV3Bytes, Completed = true };
                ProgressChanged?.Invoke(Progress);
                return targetPath;
            }
            finally
            {
                IsDownloading = false;
                try { if (File.Exists(partPath)) File.Delete(partPath); } catch { }
            }
        }
        finally
        {
            _modelGate.Release();
        }
    }

    /// <summary>ggml integrity check: valid ggml files start with magic 0x66779691.</summary>
    private static bool FileValid(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var info = new FileInfo(path);
            if (info.Length < 1_000_000) return false; // ggml-base is ~148 MB — reject truncated junk

            var header = new byte[4];
            using var fs = File.OpenRead(path);
            int read = fs.Read(header, 0, 4);
            if (read < 4) return false;
            return BitConverter.ToInt32(header, 0) == unchecked((int)0x67676D6C); // ggml file magic ("lggl")
        }
        catch { return false; }
    }

    /// <summary>Transcribe float32 16 kHz mono samples into text (Whisper, local).</summary>
    public async Task<string> TranscribeAsync(float[] samples, string language, CancellationToken ct = default)
    {
        if (IsDownloading) throw new InvalidOperationException("MODEL_DOWNLOADING");
        if (!IsReady) await EnsureModelAsync(ct);
        if (string.IsNullOrEmpty(ModelPath)) throw new InvalidOperationException("STT model is not available.");

        lock (_gate)
        {
            _factory ??= WhisperFactory.FromPath(ModelPath!, new WhisperFactoryOptions
            {
                UseGpu = _useGpu,
                GpuDevice = _gpuDevice
            });
        }
        var factory = _factory;

        using var processor = factory.CreateBuilder()
            .WithLanguage(language.Length == 2 ? language : "ru")
            .WithoutSuppressBlank()
            .WithNoContext() // reduce hallucinated filler on short/quiet audio
            .Build();

        var sb = new System.Text.StringBuilder();
        await foreach (var segment in processor.ProcessAsync(samples, ct).ConfigureAwait(false))
        {
            if (!string.IsNullOrWhiteSpace(segment.Text))
                sb.Append(segment.Text);
        }

        return sb.ToString().Trim();
    }

    /// <summary>Directory of the running executable (works with single-file publish).</summary>
    private static string ExeDir()
    {
        var exe = Environment.ProcessPath;
        return exe != null ? Path.GetDirectoryName(exe)! : AppContext.BaseDirectory;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _factory?.Dispose();
            _factory = null;
        }
    }
}
