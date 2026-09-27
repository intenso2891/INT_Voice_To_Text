using System.Diagnostics;
using System.IO.Compression;

// --- Log file next to the exe for diagnostics ---
var logPath = Path.Combine(Path.GetTempPath(), "INT_VoiceToText-updater.log");
void Log(string msg)
{
    try { File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}"); } catch { }
}

string Arg(string[] a, string name) =>
    a.SkipWhile(x => !string.Equals(x, name, StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault() ?? "";

if (args.Length < 3)
{
    Log($"Too few args ({args.Length}). Full args: [{string.Join(", ", args)}]");
    return;
}

var pidText = Arg(args, "--pid");
var zip = Arg(args, "--zip");
var target = Arg(args, "--target");
var exe = Arg(args, "--exe");

Log($"=== UPDATER START === pid={pidText} zip={zip} target={target} exe={exe}");

if (!int.TryParse(pidText, out var pid) || !File.Exists(zip) || !Directory.Exists(target))
{
    Log($"VALIDATION FAILED: pid parsed={int.TryParse(pidText, out _)}, zip exists={File.Exists(zip)}, target exists={Directory.Exists(target)}");
    System.Windows.Forms.MessageBox.Show(
        $"INT VoiceToText Updater\n\nОшибка: неверные параметры.\nPID: {pidText}\nZIP: {zip}\nTarget: {target}",
        "INT VoiceToText Updater", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
    return;
}

// Wait for the main app to exit (up to 30 seconds).
try
{
    Log($"Waiting for PID {pid} to exit...");
    using var process = Process.GetProcessById(pid);
    process.WaitForExit(30_000);
    Log("Main process exited.");
}
catch (Exception ex)
{
    Log($"Process wait: {ex.Message} (might have already exited)");
}

const int maxRetries = 3;
var retryDelay = 2000;

for (int attempt = 1; attempt <= maxRetries; attempt++)
{
    try
    {
        Log($"--- Attempt {attempt}/{maxRetries} ---");

        var extract = Path.Combine(Path.GetTempPath(), "INT_VoiceToText-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extract);
        Log($"Extracting ZIP to {extract}...");

        // Copy ZIP to a local temp file first — the original may be locked by antivirus
        var localZip = Path.Combine(extract, "update.zip");
        try
        {
            File.Copy(zip, localZip, true);
            Log($"Copied ZIP to local temp: {localZip}");
        }
        catch (Exception ex)
        {
            Log($"ZIP copy failed ({ex.Message}), trying original...");
            localZip = zip;
        }

        ZipFile.ExtractToDirectory(localZip, extract);
        Log($"Extracted OK. Contents: {Directory.GetFiles(extract, "*", SearchOption.AllDirectories).Length} files");

        var source = Directory.Exists(Path.Combine(extract, "Program")) ? Path.Combine(extract, "Program") : extract;
        Log($"Source directory: {source}");

        int copied = 0, skipped = 0, errors = 0;

        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(directory.Replace(source, target));

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = file.Replace(source, target);
            var fileName = Path.GetFileName(destination);

            // Skip the updater itself (it's running from a temp extraction dir).
            if (string.Equals(fileName, "INT_VoiceToText.Updater.exe", StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, true);
                copied++;
            }
            catch (IOException ioEx)
            {
                errors++;
                Log($"COPY FAIL (attempt {attempt}): {fileName} → {ioEx.Message}");
                // If a file is locked, the whole batch likely has locked files — retry.
                if (attempt < maxRetries)
                {
                    Log($"Retrying in {retryDelay}ms...");
                    Thread.Sleep(retryDelay);
                    continue;
                }
            }
            catch (Exception ex)
            {
                errors++;
                Log($"COPY ERROR: {fileName} → {ex.Message}");
            }
        }

        Log($"Done: copied={copied}, skipped={skipped}, errors={errors}");

        // Cleanup
        try { File.Delete(zip); Log("Deleted ZIP."); } catch (Exception ex) { Log($"ZIP delete failed: {ex.Message}"); }
        try { Directory.Delete(extract, true); Log("Deleted temp extract."); } catch (Exception ex) { Log($"Extract delete failed: {ex.Message}"); }

        if (errors > 0 && attempt < maxRetries)
        {
            Log($"{errors} errors — retrying entire attempt...");
            retryDelay += 1000;
            continue;
        }

        if (errors > 0)
        {
            System.Windows.Forms.MessageBox.Show(
                $"INT VoiceToText Updater\n\nОбновление применено с ошибками ({errors} файлов).\nСкопированных: {copied}\nПропущенных: {skipped}",
                "INT VoiceToText Updater", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Warning);
        }

        // Launch the updated app.
        if (File.Exists(exe))
        {
            Log($"Starting updated app: {exe}");
            Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = target, UseShellExecute = true });
        }
        else
        {
            Log($"WARNING: exe not found at {exe}");
        }

        Log("=== UPDATER SUCCESS ===");
        return;
    }
    catch (Exception ex)
    {
        Log($"FATAL (attempt {attempt}): {ex}");
        if (attempt >= maxRetries)
        {
            System.Windows.Forms.MessageBox.Show(
                $"INT VoiceToText Updater\n\nНе удалось применить обновление:\n{ex.Message}\n\nЛог: {logPath}",
                "INT VoiceToText Updater", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
        }
        else
        {
            Thread.Sleep(retryDelay);
            retryDelay += 1000;
        }
    }
}

Log("=== UPDATER FAILED after all retries ===");
