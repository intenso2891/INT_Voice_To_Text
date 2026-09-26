using System.Diagnostics;
using System.IO.Compression;

if (args.Length < 3) return;

static string Arg(string[] a, string name) =>
    a.SkipWhile(x => !string.Equals(x, name, StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault() ?? "";

var pidText = Arg(args, "--pid");
var zip = Arg(args, "--zip");
var target = Arg(args, "--target");
var exe = Arg(args, "--exe");

if (!int.TryParse(pidText, out var pid) || !File.Exists(zip) || !Directory.Exists(target)) return;
try
{
    using var process = Process.GetProcessById(pid);
    process.WaitForExit(30_000);
}
catch { }

try
{
    var extract = Path.Combine(Path.GetTempPath(), "INT_VoiceToText-update-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(extract);
    ZipFile.ExtractToDirectory(zip, extract);
    var source = Directory.Exists(Path.Combine(extract, "Program")) ? Path.Combine(extract, "Program") : extract;

    foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        Directory.CreateDirectory(directory.Replace(source, target));
    foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
    {
        var destination = file.Replace(source, target);
        // The updater itself is running from the target directory and is locked.
        // Keep the old helper; it can update itself on a later manual install.
        if (string.Equals(Path.GetFileName(destination), "INT_VoiceToText.Updater.exe", StringComparison.OrdinalIgnoreCase)) continue;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(file, destination, true);
    }

    File.Delete(zip);
    Directory.Delete(extract, true);
    if (File.Exists(exe)) Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = target, UseShellExecute = true });
}
catch
{
    // The next startup can retry; never leave a half-open updater UI.
}
