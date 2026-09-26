using Microsoft.Win32;

namespace INTVoiceToText.Services;

public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "INTVoiceToText";

    public static bool IsEnabled()
    {
        try { using var key = Registry.CurrentUser.OpenSubKey(RunKey, false); return key?.GetValue(ValueName) is string; }
        catch { return false; }
    }

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "INT_VoiceToText.exe");
                key.SetValue(ValueName, $"\"{exe}\"");
            }
            else key.DeleteValue(ValueName, false);
        }
        catch { /* settings UI reports state; registry restrictions are non-fatal */ }
    }
}
