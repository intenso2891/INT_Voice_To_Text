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
    internal static SpeechService? _instance;
    private const string ModelFileName = "ggml-large-v3";
    public SpeechService() { _instance = this; }
    private readonly Mutex _crossProcessDownload = new(false, @"Local\INT_VoiceToText.ModelDownload");
    private const long LargeV3Bytes = 3_095_033_483;
    public event Action<ModelProgress>? ProgressChanged;
    public event Action<string>? OnDebug;
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
    private string _gpuBackend = "none"; // none | vulkan | cuda | auto
    private string _currentMode = "cpu"; // cpu | gpu-vulkan | gpu-nvidia | hybrid-vulkan | hybrid-nvidia
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
            _currentMode = mode;
            _gpuBackend = mode switch
            {
                "gpu-nvidia" => "cuda",
                "gpu-vulkan" => "vulkan",
                "hybrid-nvidia" => "cuda-hybrid",
                "hybrid-vulkan" => "vulkan-hybrid",
                _ => "none"
            };
            _useGpu = mode is "gpu-nvidia" or "gpu-vulkan" or "hybrid-nvidia" or "hybrid-vulkan";
            _gpuDevice = Math.Max(0, gpuDevice);

            // Deploy correct native libraries for this mode
            DeployNativeLibraries(mode);

            // Force the GPU backend via Whisper.net RuntimeLibraryOrder
            try
            {
                Whisper.net.LibraryLoader.RuntimeOptions.RuntimeLibraryOrder = _gpuBackend switch
                {
                    "cuda" or "cuda-hybrid" => new List<Whisper.net.LibraryLoader.RuntimeLibrary>
                    {
                        Whisper.net.LibraryLoader.RuntimeLibrary.Cuda,
                        Whisper.net.LibraryLoader.RuntimeLibrary.Cuda12,
                        Whisper.net.LibraryLoader.RuntimeLibrary.Cpu,
                    },
                    "vulkan" or "vulkan-hybrid" => new List<Whisper.net.LibraryLoader.RuntimeLibrary>
                    {
                        Whisper.net.LibraryLoader.RuntimeLibrary.Vulkan,
                        Whisper.net.LibraryLoader.RuntimeLibrary.Cpu,
                    },
                    _ => new List<Whisper.net.LibraryLoader.RuntimeLibrary> { Whisper.net.LibraryLoader.RuntimeLibrary.Cpu },
                };
            }
            catch { }

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
        // Determine active model
        var activeId = "";
        if (!string.IsNullOrEmpty(_instance?.ModelPath))
        {
            activeId = Path.GetFileNameWithoutExtension(_instance.ModelPath).Replace("ggml-", "").ToLowerInvariant();
        }
        return new { models = models.Select(m => new { m.id, m.name, m.sizeMB, m.desc, installed = found.Contains(m.id), active = m.id == activeId }).ToArray() };
    }

    /// <summary>Select an already-downloaded model as the active one.</summary>
    public static object SelectModel(string modelId)
    {
        foreach (var dir in new[] {
            Path.Combine(ExeDir(), "models"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "INT_VoiceToText", "models"),
        })
        {
            var path = Path.Combine(dir, $"ggml-{modelId}.bin");
            if (File.Exists(path) && FileValid(path))
            {
                if (_instance != null)
                {
                    _instance.ModelPath = path;
                    _instance.LastError = null;
                }
                return new { ok = true, path };
            }
        }
        return new { ok = false, error = $"Model {modelId} not found" };
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

    /// <summary>Download a specific model by id. Tries multiple mirrors with stall detection.</summary>
    public async Task<string?> DownloadModelAsync(string modelId, CancellationToken ct = default)
    {
        await _modelGate.WaitAsync(ct).ConfigureAwait(false);
        string? targetPath = null;
        string? partPath = null;
        try
        {
            if (IsDownloading) return null;
            LastError = null;

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

            // Multiple download sources (mirrors) for resilience
            var fileName = $"ggml-{modelId}.bin";
            var sources = new[]
            {
                $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{fileName}",
                $"https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/{fileName}",
                $"https://mirror.ghproxy.com/https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{fileName}",
            };

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

                IsDownloading = true;
                Progress = new ModelProgress(0, expectedSize, false);
                ProgressChanged?.Invoke(Progress);

                Exception? lastEx = null;
                string[] tried = Array.Empty<string>();

                foreach (var url in sources)
                {
                    try
                    {
                        // Check if source is reachable (HEAD request with 10s timeout)
                        using (var headCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                        {
                            headCts.CancelAfter(10_000);
                            using var headClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                            using var headReq = new HttpRequestMessage(HttpMethod.Head, url);
                            var head = await headClient.SendAsync(headReq, headCts.Token).ConfigureAwait(false);
                            if (!head.IsSuccessStatusCode)
                            {
                                tried = tried.Append(url).ToArray();
                                continue;
                            }
                        }

                        // Download with stall detection (10s without data = switch source)
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                        var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                        if (!resp.IsSuccessStatusCode)
                        {
                            tried = tried.Append(url).ToArray();
                            continue;
                        }

                        using (var file = new FileStream(partPath!, FileMode.Create, FileAccess.Write, FileShare.Read))
                        using (var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false))
                        {
                            var buffer = new byte[128 * 1024];
                            int read;
                            long lastActivity = Environment.TickCount64;
                            while (true)
                            {
                                // Stall detection: 10 seconds without data → abort this source
                                using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                                stallCts.CancelAfter(10_000);
                                try
                                {
                                    read = await stream.ReadAsync(buffer, stallCts.Token).ConfigureAwait(false);
                                }
                                catch (OperationCanceledException) when (!cts.Token.IsCancellationRequested)
                                {
                                    throw new IOException($"Скачивание зависло (нет данных 10 сек) с {new Uri(url).Host}");
                                }

                                if (read <= 0) break;
                                await file.WriteAsync(buffer.AsMemory(0, read), cts.Token).ConfigureAwait(false);
                                lastActivity = Environment.TickCount64;
                                Progress = Progress with { Bytes = file.Length };
                                ProgressChanged?.Invoke(Progress);
                            }
                            await file.FlushAsync(cts.Token).ConfigureAwait(false);
                        }

                        if (!FileValid(partPath!))
                        {
                            tried = tried.Append(url).ToArray();
                            continue;
                        }

                        if (File.Exists(targetPath)) File.Delete(targetPath);
                        File.Move(partPath!, targetPath!);
                        ModelPath = targetPath;
                        IsDownloading = false;
                        Progress = new ModelProgress(new FileInfo(targetPath).Length, new FileInfo(targetPath).Length, true);
                        ProgressChanged?.Invoke(Progress);
                        return targetPath;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        lastEx = ex;
                        tried = tried.Append(url).ToArray();
                        if (partPath != null && File.Exists(partPath)) try { File.Delete(partPath); } catch { }
                        continue;
                    }
                }

                // All sources failed — show manual download instructions
                var manualUrl = $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{fileName}";
                var modelDir = Path.Combine(ExeDir(), "models");
                throw new IOException(
                    $"Не удалось скачать модель {modelId} ни с одного источника.\n" +
                    $"Проблемы: {string.Join("; ", tried.Select(u => new Uri(u).Host))}\n\n" +
                    $"Скачай файл вручную:\n{manualUrl}\n\n" +
                    $"И положи его сюда:\n{targetPath}");
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
    /// Deploy native Whisper DLLs for the selected compute mode.
    /// CRITICAL: ggml-cuda-whisper.dll MUST NOT be in runtimes/win-x64/ unless NVIDIA mode is active.
    /// Whisper.net NativeLibraryLoader scans ALL DLLs in runtimes/win-x64/ and loads them.
    /// If ggml-cuda-whisper.dll is present but CUDA runtime is missing/incompatible → ucrtbase c0000409 crash.
    /// </summary>
    public static string DeployNativeLibraries(string mode = "cpu")
    {
        lock (_nativeLock)
        {
            var exeDir = ExeDir();
            var useCuda = mode is "gpu-nvidia" or "hybrid-nvidia";
            var useVulkan = mode is "gpu-vulkan" or "hybrid-vulkan";

            // 1. ALWAYS remove GPU DLLs from ALL runtime search paths first (safety)
            foreach (var baseDir in new[] { AppContext.BaseDirectory, exeDir })
            {
                foreach (var sub in new[] { "", "runtimes/win-x64", "runtimes/win-x64/native", "runtimes/cuda/win-x64", "runtimes/cuda12/win-x64", "runtimes/vulkan/win-x64" })
                {
                    foreach (var gpuDll in new[] { "ggml-cuda-whisper.dll", "ggml-vulkan-whisper.dll" })
                    {
                        var p = Path.Combine(baseDir, sub, gpuDll);
                        try { if (File.Exists(p)) File.Delete(p); } catch { }
                    }
                }
            }

            // 2. Build DLL list for this mode
            var dllList = new List<string> { "whisper.dll", "ggml-whisper.dll", "ggml-base-whisper.dll", "ggml-cpu-whisper.dll" };
            if (useVulkan) dllList.Add("ggml-vulkan-whisper.dll");
            if (useCuda) dllList.Add("ggml-cuda-whisper.dll");
            string[] dlls = dllList.ToArray();

            // 3. Deploy DLLs to runtimes/win-x64/ (Whisper.net search path)
            var report = new System.Text.StringBuilder();
            int deployed = 0;
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
                    // Search in exeDir subdirs for the source DLL
                    foreach (var srcDir in new[] { Path.Combine(exeDir, "runtimes", "win-x64"), Path.Combine(exeDir, "runtimes", "win-x64", "native"), Path.Combine(exeDir, "cuda"), exeDir })
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

    /// <summary>Transcribe float32 16 kHz mono samples into text (Whisper, local).
    /// If GPU mode crashes or hangs, auto-falls back to CPU with timeout.</summary>
    public async Task<string> TranscribeAsync(float[] samples, string language, CancellationToken ct = default)
    {
        if (IsDownloading) throw new InvalidOperationException("MODEL_DOWNLOADING");
        if (!IsReady) await EnsureModelAsync(ct);
        if (string.IsNullOrEmpty(ModelPath)) throw new InvalidOperationException("STT model is not available.");

        // GPU mode: add 30s timeout — if hangs, fall back to CPU
        if (_useGpu)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                return await TranscribeInternalAsync(samples, language, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                OnDebug?.Invoke("GPU_TIMEOUT: распознавание зависло (>30 сек) — переключаюсь на CPU...");
                ConfigureCompute("cpu", 0);
                return await TranscribeInternalAsync(samples, language, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                OnDebug?.Invoke("GPU_TIMEOUT: модель не загрузилась за 30 сек (GPU завис) — переключаюсь на CPU...");
                _factory?.Dispose(); _factory = null;
                ConfigureCompute("cpu", 0);
                return await TranscribeInternalAsync(samples, language, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                OnDebug?.Invoke($"GPU_FAILED: {ex.Message} — переключаюсь на CPU...");
                ConfigureCompute("cpu", 0);
                return await TranscribeInternalAsync(samples, language, ct).ConfigureAwait(false);
            }
        }

        return await TranscribeInternalAsync(samples, language, ct).ConfigureAwait(false);
    }

    private async Task<string> TranscribeInternalAsync(float[] samples, string language, CancellationToken ct)
    {
        DeployNativeLibraries(_currentMode);

        // First-time model loading can take 10-60s for large models
        if (_factory == null)
        {
            OnDebug?.Invoke($"MODEL_LOADING size={new FileInfo(ModelPath!).Length / 1024 / 1024}MB — загрузка модели в память, подождите...");
        }

        // CRITICAL: WhisperFactory.FromPath is BLOCKING and can hang on GPU init (Vulkan/CUDA).
        // Run it in a Task with timeout so we can fall back to CPU if GPU hangs.
        if (_factory == null)
        {
            _factory = await Task.Run(() =>
            {
                lock (_gate)
                {
                    return _factory ??= WhisperFactory.FromPath(ModelPath!, new WhisperFactoryOptions
                    {
                        UseGpu = _useGpu,
                        GpuDevice = _gpuDevice
                    });
                }
            }, ct).WaitAsync(TimeSpan.FromSeconds(_useGpu ? 30 : 120), ct).ConfigureAwait(false) ?? throw new TimeoutException("MODEL_LOAD_TIMEOUT");
        }

        if (_factory != null) OnDebug?.Invoke("MODEL_LOADED");
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
    /// <summary>Check if Vulkan Runtime is available (vulkan-1.dll in System32).</summary>
    private static bool IsVulkanAvailable()
    {
        try
        {
            return File.Exists(Path.Combine(Environment.SystemDirectory, "vulkan-1.dll"));
        }
        catch { return false; }
    }

    /// <summary>Check if CUDA Toolkit is installed (cudart64_*.dll in system32 or CUDA_PATH set).</summary>
    private static bool IsCudaAvailable()
    {
        // Check CUDA_PATH environment variable
        var cudaPath = Environment.GetEnvironmentVariable("CUDA_PATH");
        if (!string.IsNullOrEmpty(cudaPath) && Directory.Exists(cudaPath)) return true;

        // Check for cudart64_*.dll in System32 (installed by CUDA Toolkit or NVIDIA driver)
        try
        {
            var sys32 = Environment.SystemDirectory;
            if (Directory.EnumerateFiles(sys32, "cudart64_*.dll").Any()) return true;
        }
        catch { }

        // Check common CUDA Toolkit installation paths
        foreach (var ver in new[] { "v13.0", "v12.6", "v12.5", "v12.4", "v12.3", "v12.2", "v12.1", "v12.0", "v11.8" })
        {
            var p = $@"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\{ver}\bin\cudart64_*.dll";
            try { if (Directory.EnumerateFiles(Path.GetDirectoryName(p)!, Path.GetFileName(p)).Any()) return true; } catch { }
        }

        return false;
    }

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
