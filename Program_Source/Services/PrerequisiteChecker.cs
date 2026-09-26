using Microsoft.Win32;
using System.Diagnostics;
using System.Windows.Forms;

namespace INTVoiceToText.Services;

public static class PrerequisiteChecker
{
    private const string WebView2Guid = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    public static bool WebView2Installed()
    {
        var paths = new[]
        {
            $@"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{WebView2Guid}",
            $@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{WebView2Guid}"
        };
        foreach (var path in paths)
        {
            try
            {
                using var machine = Registry.LocalMachine.OpenSubKey(path);
                using var user = Registry.CurrentUser.OpenSubKey(path);
                if (machine?.GetValue("pv") is string m && !string.IsNullOrWhiteSpace(m)) return true;
                if (user?.GetValue("pv") is string u && !string.IsNullOrWhiteSpace(u)) return true;
            }
            catch { }
        }
        return false;
    }

    /// <summary>Returns false when the application should close while prerequisites are installed.</summary>
    public static bool Ensure()
    {
        if (WebView2Installed()) return true;
        var answer = MessageBox.Show(
            "Для запуска INT VoiceToText нужен Microsoft Edge WebView2 Runtime.\n\nУстановить его сейчас?",
            "INT VoiceToText — требуется компонент",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Information);
        if (answer != DialogResult.Yes) return false;

        var script = Path.Combine(AppContext.BaseDirectory, "Install-Requirements.bat");
        if (File.Exists(script))
        {
            Process.Start(new ProcessStartInfo(script) { WorkingDirectory = AppContext.BaseDirectory, UseShellExecute = true });
            return false;
        }
        MessageBox.Show("Файл Install-Requirements.bat не найден. Скачайте WebView2 Runtime с официального сайта Microsoft.", "INT VoiceToText", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }
}
