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
    private readonly Mutex _crossProcessDownload = new(false, @"Local\INT_VoiceToText.ModelDownload");
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

    /// <summary>Last error from EnsureModelAsync (for diagnostics).</summary>
    public string? LastError { get; private set; }

    /// <summary>List all model files across all candidate directories with sizes.</summary>
    public static object GetModelList()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "models"),
            Path.Combine(ExeDir(), "models"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "INT_VoiceToText", "models"),
        };
        var files = new List<object>();
        var dirs = new List<object>();
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in candidates.Distinct())
        {
            var exists = Directory.Exists(dir);
            dirs.Add(new { path = dir, exists });
            if (!exists) continue;
            foreach (var f in Directory.EnumerateFiles(dir).OrderByDescending(File.GetLastWriteTimeUtc))
            {
                var key = Path.GetFileName(f).ToLowerInvariant();
                if (!found.Add(key)) continue; // skip duplicates (show first found)
                var info = new FileInfo(f);
                files.Add(new { name = Path.GetFileName(f), sizeMB = Math.Round(info.Length / 1048576.0, 1), valid = FileValid(f), location = dir });
            }
        }
        return new { dirs, files };
    }

    /// <summary>Force a fresh download attempt (resets cached state).</summary>
    public async Task<object> RetryDownloadAsync(CancellationToken ct = default)
    {
        LastError = null;
        // Reset so EnsureModelAsync will re-scan and re-download.
        ModelPath = null;
        Progress = new ModelProgress(0, LargeV3Bytes, false);
        ProgressChanged?.Invoke(Progress);
        try
        {
            var path = await EnsureModelAsync(ct);
            return new { ok = true, path };
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return new { ok = false, error = ex.Message };
        }
    }

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

    /// <summary>Locate the ggml model. Does NOT download — user must choose manually.</summary>
    public async Task<string?> EnsureModelAsync(CancellationToken ct = default, Action<string>? onProgress = null)
    {
        await _modelGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrEmpty(ModelPath)) return ModelPath;
            LastError = null;

            // Clean up stale .downloading files from previous crashed downloads.
            try
            {
                var staleDir = Path.Combine(ExeDir(), "models");
                if (Directory.Exists(staleDir))
                    foreach (var f in Directory.EnumerateFiles(staleDir, "*.downloading"))
                        try { File.Delete(f); } catch { }
            }
            catch { }

            var candidates = new List<string>
            {
                Path.Combine(AppContext.BaseDirectory, "models"),
                Path.Combine(ExeDir(), "models"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                             "INT_VoiceToText", "models"),
            };

            foreach (var dir in candidates.Distinct())
            {
                if (!Directory.Exists(dir)) continue;
                var bin = Directory.EnumerateFiles(dir, "*.bin")
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

            // No model found — DON'T download automatically. User must choose.
            return null;
        }
        finally
        {
            _modelGate.Release();
        }
    }

    /// <summary>Available Whisper models for manual selection.</summary>
    public static object GetModelCatalog()
    {
        var models = new[]
        {
            new { id = "tiny",    name = "Tiny",    sizeMB = 75L,   desc = "Очень быстрая, низкое качество" },
            new { id = "base",    name = "Base",    sizeMB = 142L,  desc = "Быстрая, приемлемое качество" },
            new { id = "small",   name = "Small",   sizeMB = 466L,  desc = "Баланс скорости и качества" },
            new { id = "medium",  name = "Medium",  sizeMB = 1500L, desc = "Хорошее качество, медленнее" },
            new { id = "large-v3", name = "Large V3", sizeMB = 3095L, desc = "Лучшее качество (рекомендуется)" },
        };
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in new[] {
            Path.Combine(ExeDir(), "models"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "INT_VoiceToText", "models"),
        })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*.bin").Where(FileValid))
            {
                var n = Path.GetFileNameWithoutExtension(f).Replace("ggml-", "").ToLowerInvariant();
                found.Add(n);
            }
        }
        return new { models = models.Select(m => new { m.id, m.name, m.sizeMB, m.desc, installed = found.Contains(m.id) }).ToArray() };
    }

    /// <summary>Delete all downloaded model files.</summary>
    public static object DeleteAllModels()
    {
        int deleted = 0; long freed = 0;
        foreach (var dir in new[] {
            Path.Combine(ExeDir(), "models"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "INT_VoiceToText", "models"),
        })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*.bin").Concat(Directory.EnumerateFiles(dir, "*.downloading")))
            {
                try { var sz = new FileInfo(f).Length; File.Delete(f); deleted++; freed += sz; }
                catch { }
            }
        }
        return new { deleted, freedMB = Math.Round(freed / 1048576.0, 1) };
    }

    /// <summary>Download a specific model by id. Replaces any existing model.</summary>
    public async Task<string?> DownloadModelAsync(string modelId, CancellationToken ct = default)
    {
        await _modelGate.WaitAsync(ct).ConfigureAwait(false);
        string? targetPath = null;
        string? partPath = null;
        try
        {
            if (IsDownloading) return null;
            LastError = null;

            var ggmlType = modelId switch
            {
                "tiny" => GgmlType.Tiny,
                "base" => GgmlType.Base,
                "small" => GgmlType.Small,
                "medium" => GgmlType.Medium,
                "large-v3" => GgmlType.LargeV3,
                _ => GgmlType.LargeV3,
            };
            long expectedSize = modelId switch
            {
                "tiny" => 75_000_000L,
                "base" => 142_000_000L,
                "small" => 466_000_000L,
                "medium" => 1_500_000_000L,
                _ => LargeV3Bytes,
            };

            var targetDir = Path.Combine(ExeDir(), "models");
            Directory.CreateDirectory(targetDir);
            targetPath = Path.Combine(targetDir, $"ggml-{modelId}.bin");
            partPath = targetPath + $".{Environment.ProcessId}.{Guid.NewGuid():N}.downloading";

            try { _crossProcessDownload.WaitOne(); } catch (AbandonedMutexException) { }
            try
            {
                if (FileValid(targetPath))
                {
                    ModelPath = targetPath;
                    Progress = Progress with { Bytes = expectedSize, Completed = true };
                    ProgressChanged?.Invoke(Progress);
                    return targetPath;
                }

                const int maxRetries = 3;
                for (int attempt = 1; attempt <= maxRetries; attempt++)
                {
                    try
                    {
                        IsDownloading = true;
                        Progress = new ModelProgress(0, expectedSize, false);
                        ProgressChanged?.Invoke(Progress);
                        using var stream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(
                            ggmlType, QuantizationType.NoQuantization, ct).ConfigureAwait(false);

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

                        if (!FileValid(partPath))
                        {
                            if (attempt < maxRetries) { await Task.Delay(2000 * attempt, ct).ConfigureAwait(false); continue; }
                            throw new IOException("Integrity check failed after " + maxRetries + " attempts.");
                        }

                        if (File.Exists(targetPath)) File.Delete(targetPath);
                        File.Move(partPath, targetPath);
                        ModelPath = targetPath;
                        IsDownloading = false;
                        Progress = new ModelProgress(new FileInfo(targetPath).Length, new FileInfo(targetPath).Length, true);
                        ProgressChanged?.Invoke(Progress);
                        return targetPath;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (ex is IOException or HttpRequestException or System.Net.Sockets.SocketException)
                    {
                        LastError = ex.Message;
                        if (attempt < maxRetries) { await Task.Delay(3000 * (int)Math.Pow(2, attempt - 1), ct).ConfigureAwait(false); continue; }
                        throw;
                    }
                }
                throw new IOException("Download failed after " + maxRetries + " attempts.");
            }
            finally
            {
                IsDownloading = false;
                if (partPath != null && !FileValid(targetPath!))
                    try { File.Delete(partPath); } catch { }
                try { _crossProcessDownload.ReleaseMutex(); } catch { }
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
            if (info.Length < 1_000_000) return false;

            var header = new byte[4];
            using var fs = File.OpenRead(path);
            int read = fs.Read(header, 0, 4);
            if (read < 4) return false;
            return BitConverter.ToInt32(header, 0) == unchecked((int)0x67676D6C); // ggml file magic ("lggl")
        }
        catch { return false; }
    }

    private static bool _nativeDeployed;
    private static readonly object _nativeLock = new();

    /// <summary>
    /// Deploy native Whisper DLLs to the EXACT paths Whisper.net searches.
    ///
    /// From Whisper.net source (NativeLibraryLoader.GetRuntimePaths):
    ///   runtimePath = Path.Combine(assemblySearchPath, "runtimes", "win-x64")
    ///   whisperPath = Path.Combine(runtimePath, "whisper.dll")
    ///
    /// So DLLs must be in &lt;searchPath&gt;/runtimes/win-x64/ (NOT runtimes/win-x64/native/!)
    /// </summary>
    public static string DeployNativeLibraries()
    {
        lock (_nativeLock)
        {
            var exeDir = ExeDir();
            string[] dlls = { "whisper.dll", "ggml-whisper.dll", "ggml-base-whisper.dll", "ggml-cpu-whisper.dll", "ggml-vulkan-whisper.dll" };
            var report = new System.Text.StringBuilder();
            int deployed = 0;

            // Whisper.net searches these directories for runtimes/win-x64/*.dll:
            // 1. AppContext.BaseDirectory (temp dir for single-file)
            // 2. Exe directory (from GetCommandLineArgs)
            // 3. Assembly.Location directory
            var searchBases = new List<string> { AppContext.BaseDirectory, exeDir };

            foreach (var baseDir in searchBases.Distinct())
            {
                var runtimeDir = Path.Combine(baseDir, "runtimes", "win-x64");
                var nativeDir = Path.Combine(runtimeDir, "native");
                Directory.CreateDirectory(runtimeDir);
                Directory.CreateDirectory(nativeDir);

                foreach (var dll in dlls)
                {
                    string? src = null;
                    foreach (var srcDir in new[] { Path.Combine(exeDir, "runtimes", "win-x64"), Path.Combine(exeDir, "runtimes", "win-x64", "native"), exeDir })
                    {
                        var p = Path.Combine(srcDir, dll);
                        if (File.Exists(p)) { src = p; break; }
                    }
                    if (src == null) continue;

                    foreach (var dst in new[] { Path.Combine(runtimeDir, dll), Path.Combine(nativeDir, dll) })
                    {
                        try
                        {
                            if (!File.Exists(dst) || new FileInfo(dst).Length != new FileInfo(src).Length)
                                File.Copy(src, dst, true);
                        }
                        catch { }
                    }
                    deployed++;
                }
            }

            _nativeDeployed = true;
            report.Append($"DLL deploy: {deployed} files → runtimes/win-x64/ in {searchBases.Count} locations");
            return report.ToString();
        }
    }

    /// <summary>Check native library status (for diagnostics).</summary>
    public static object GetNativeLibraryStatus()
    {
        var exeDir = ExeDir();
        var targetBase = AppContext.BaseDirectory;
        var sourceNative = Path.Combine(exeDir, "runtimes", "win-x64", "native");
        var targetNative = Path.Combine(targetBase, "runtimes", "win-x64", "native");
        string[] dlls = { "whisper.dll", "ggml-whisper.dll", "ggml-base-whisper.dll", "ggml-cpu-whisper.dll", "ggml-vulkan-whisper.dll" };
        var items = new List<object>();
        foreach (var dll in dlls)
        {
            bool inExeDir = File.Exists(Path.Combine(exeDir, dll));
            bool inExeNative = File.Exists(Path.Combine(sourceNative, dll));
            bool inTempDir = File.Exists(Path.Combine(targetBase, dll));
            bool inTempNative = File.Exists(Path.Combine(targetNative, dll));
            long size = 0;
            foreach (var p in new[] { Path.Combine(sourceNative, dll), Path.Combine(exeDir, dll) })
            {
                if (File.Exists(p)) { size = new FileInfo(p).Length; break; }
            }
            items.Add(new { name = dll, inExeDir, inExeNative, inTempDir, inTempNative, sizeKB = size / 1024 });
        }
        return new { exeDir, tempDir = targetBase, deployed = _nativeDeployed, dlls = items };
    }

    /// <summary>Transcribe float32 16 kHz mono samples into text (Whisper, local).</summary>
    public async Task<string> TranscribeAsync(float[] samples, string language, CancellationToken ct = default)
    {
        if (IsDownloading) throw new InvalidOperationException("MODEL_DOWNLOADING");
        if (!IsReady) await EnsureModelAsync(ct);
        if (string.IsNullOrEmpty(ModelPath)) throw new InvalidOperationException("STT model is not available.");

        DeployNativeLibraries();

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
        _crossProcessDownload.Dispose();
    }
}
