// ============================================================================
// INT VoiceToText — local speech-to-text (Whisper.net, fully offline)
// ============================================================================

using System.IO.Compression;
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
    public SpeechService()
    {
        _instance = this;
        // CRITICAL: Whisper.net's default RuntimeLibraryOrder starts with CUDA. Before
        // ConfigureCompute runs we must force CPU-first, otherwise the first factory creation
        // tries a GPU backend and crashes (0xc0000409) on machines with incompatible drivers.
        try
        {
            Whisper.net.LibraryLoader.RuntimeOptions.RuntimeLibraryOrder =
                new List<Whisper.net.LibraryLoader.RuntimeLibrary> { Whisper.net.LibraryLoader.RuntimeLibrary.Cpu };
            Whisper.net.LibraryLoader.RuntimeOptions.LoadedLibrary = null;
        }
        catch { }
    }
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

            // CRITICAL: Whisper.net caches the loaded native library in a static field. Reset it
            // so the new mode actually takes effect on the next factory creation (otherwise a
            // previously loaded GPU backend keeps being reused even after switching to CPU).
            try
            {
                Whisper.net.LibraryLoader.RuntimeOptions.LoadedLibrary = null;
            }
            catch { }

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

    /// <summary>
    /// Download CUDA DLL (ggml-cuda-whisper.dll) from NuGet for NVIDIA GPU support.
    /// The DLL is ~538 MB and is downloaded only when user selects NVIDIA mode for the first time.
    /// </summary>
    public async Task<bool> DownloadCudaDllAsync(CancellationToken ct = default)
    {
        var cudaDir = Path.Combine(ExeDir(), "cuda");
        var cudaDll = Path.Combine(cudaDir, "ggml-cuda-whisper.dll");
        if (File.Exists(cudaDll)) return true; // already downloaded

        Directory.CreateDirectory(cudaDir);

        // Download whisper.net.runtime.cuda12.windows nupkg (ZIP) and extract ggml-cuda-whisper.dll
        var nupkgUrl = "https://api.nuget.org/v3-flatcontainer/whisper.net.runtime.cuda12.windows/1.9.1/whisper.net.runtime.cuda12.windows.1.9.1.nupkg";
        var mirrors = new[]
        {
            nupkgUrl,
            nupkgUrl.Replace("api.nuget.org", "nuget.cdn.azure.cn"),
        };

        var tmpNupkg = Path.Combine(cudaDir, "cuda.nupkg.downloading");

        foreach (var url in mirrors)
        {
            try
            {
                OnDebug?.Invoke($"CUDA_DLL_DOWNLOAD: скачиваю CUDA DLL с {new Uri(url).Host}...");
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                var totalBytes = resp.Content.Headers.ContentLength ?? 538_000_000L;
                await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var fs = File.Create(tmpNupkg);

                var buffer = new byte[81920];
                long downloaded = 0;
                int read;
                var lastReport = DateTime.UtcNow;

                while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    downloaded += read;

                    if ((DateTime.UtcNow - lastReport).TotalSeconds > 2)
                    {
                        lastReport = DateTime.UtcNow;
                        var pct = (int)(downloaded * 100 / totalBytes);
                        OnDebug?.Invoke($"CUDA_DLL_PROGRESS: {pct}% ({downloaded / 1048576}MB / {totalBytes / 1048576}MB)");
                        ProgressChanged?.Invoke(Progress with { Bytes = downloaded, TotalBytes = totalBytes, Completed = false });
                    }
                }

                await fs.FlushAsync(ct).ConfigureAwait(false);
                fs.Close();

                // Extract ggml-cuda-whisper.dll from nupkg (ZIP)
                OnDebug?.Invoke("CUDA_DLL_EXTRACT: распаковка...");
                using var zip = System.IO.Compression.ZipFile.OpenRead(tmpNupkg);
                var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals("ggml-cuda-whisper.dll", StringComparison.OrdinalIgnoreCase));
                if (entry == null) throw new FileNotFoundException("ggml-cuda-whisper.dll not found in nupkg");

                entry.ExtractToFile(cudaDll, true);
                zip.Dispose();

                try { File.Delete(tmpNupkg); } catch { }

                OnDebug?.Invoke($"CUDA_DLL_OK: CUDA DLL установлен ({new FileInfo(cudaDll).Length / 1048576}MB)");
                ProgressChanged?.Invoke(Progress with { Bytes = totalBytes, TotalBytes = totalBytes, Completed = true });
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                OnDebug?.Invoke($"CUDA_DLL_FAIL: {new Uri(url).Host} — {ex.Message}");
                try { if (File.Exists(tmpNupkg)) File.Delete(tmpNupkg); } catch { }
            }
        }

        LastError = "CUDA_DLL_DOWNLOAD_FAILED: Не удалось скачать CUDA DLL. Скачайте вручную с https://developer.nvidia.com/cuda-downloads";
        return false;
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
    /// Whisper.net resolves each backend into its OWN subfolder:
    ///   CPU    → runtimes/win-x64/
    ///   Vulkan → runtimes/vulkan/win-x64/
    ///   CUDA   → runtimes/cuda/win-x64/  (and cuda12/win-x64/)
    /// The loader then loads EVERY ggml-*.dll sitting in that subfolder (see NativeLibraryLoader.dependencyOrder).
    /// Therefore a stray ggml-vulkan/cuda-whisper.dll inside runtimes/win-x64/ is loaded even in CPU mode
    /// and hard-crashes (0xc0000409 in ucrtbase.dll) on GPUs whose driver is incompatible. This method
    /// guarantees the CPU subfolder only ever contains CPU DLLs, and assembles the CUDA subfolder on demand.
    /// </summary>
    public static string DeployNativeLibraries(string mode = "cpu")
    {
        lock (_nativeLock)
        {
            var exeDir = ExeDir();
            var useCuda = mode is "gpu-nvidia" or "hybrid-nvidia";
            var searchBases = new[] { AppContext.BaseDirectory, exeDir }
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Distinct()
                .ToArray();

            // 1) CRITICAL FIX: purge GPU backend DLLs from the CPU runtime folder and the app root.
            //    This is the single most important line of defence against the 0xc0000409 crash.
            foreach (var baseDir in searchBases)
            {
                foreach (var sub in new[] { "", "runtimes/win-x64", "runtimes/win-x64/native" })
                {
                    foreach (var gpuDll in new[] { "ggml-cuda-whisper.dll", "ggml-vulkan-whisper.dll" })
                    {
                        var p = Path.Combine(baseDir, sub, gpuDll);
                        try { if (File.Exists(p)) File.Delete(p); } catch { }
                    }
                }
            }

            int deployed = 0;

            // 2) Ensure CPU DLLs are present in runtimes/win-x64/ for every search base.
            //    Single-file publish already extracts them; this covers the portable folder fallback.
            foreach (var baseDir in searchBases)
            {
                var runtimeDir = Path.Combine(baseDir, "runtimes", "win-x64");
                Directory.CreateDirectory(runtimeDir);
                foreach (var dll in CpuDlls)
                {
                    if (EnsureDll(exeDir, runtimeDir, dll)) deployed++;
                }
            }

            // 3) CUDA mode: assemble runtimes/cuda/win-x64/ = CPU DLLs + ggml-cuda-whisper.dll.
            //    NEVER copy ggml-cuda-whisper.dll into runtimes/win-x64/.
            if (useCuda)
            {
                foreach (var baseDir in searchBases)
                {
                    var cudaDir = Path.Combine(baseDir, "runtimes", "cuda", "win-x64");
                    Directory.CreateDirectory(cudaDir);
                    foreach (var dll in CpuDlls)
                    {
                        if (EnsureDll(exeDir, cudaDir, dll)) deployed++;
                    }
                    if (EnsureDll(Path.Combine(exeDir, "cuda"), cudaDir, "ggml-cuda-whisper.dll")) deployed++;
                }
            }

            // 4) Vulkan mode: single-file publish already extracts runtimes/vulkan/win-x64/ correctly,
            //    so no manual copy is needed and we never touch that folder (AMD/Vulkan keeps working).

            _nativeDeployed = true;
            return $"DLL deploy [{mode}]: {deployed} files";
        }
    }

    private static readonly string[] CpuDlls =
        { "whisper.dll", "ggml-whisper.dll", "ggml-base-whisper.dll", "ggml-cpu-whisper.dll" };

    /// <summary>Copy <paramref name="dll"/> from any of the usual source folders into <paramref name="dstDir"/>.
    /// Returns true when the file is present at the destination afterwards.</summary>
    private static bool EnsureDll(string srcDir, string dstDir, string dll)
    {
        var candidates = new[]
        {
            Path.Combine(srcDir, dll),
            Path.Combine(srcDir, "runtimes", "win-x64", dll),
            Path.Combine(srcDir, "runtimes", "win-x64", "native", dll),
        };
        var src = candidates.FirstOrDefault(File.Exists);
        if (src == null) return false;

        var dst = Path.Combine(dstDir, dll);
        try
        {
            if (!File.Exists(dst) || new FileInfo(dst).Length != new FileInfo(src).Length)
                File.Copy(src, dst, true);
            return true;
        }
        catch { return false; }
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
