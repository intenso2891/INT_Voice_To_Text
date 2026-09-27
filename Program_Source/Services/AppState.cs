// ============================================================================
// INT VoiceToText — application state: settings persistence + hotkey mapping
// ============================================================================

using System.Text.Json;
using System.Text.Json.Serialization;

namespace INTVoiceToText.Services;

public sealed record HotkeyConfig(
    [property: JsonPropertyName("modifiers")] List<string> Modifiers,
    [property: JsonPropertyName("key")] string Key
);

public sealed class AppState
{
    public const string AppVersion = "1.6.19";
    private static readonly JsonSerializerOptions JsonOpts = new()
    { WriteIndented = true };

    public HotkeyConfig Hotkey { get; set; } = new(new List<string> { "ctrl", "win", "alt" }, "A");
    public string Language { get; set; } = "ru";          // whisper target language (for RU UI use "ru")
    public bool PasteResult { get; set; } = false;         // optional auto-insert; clipboard is always updated
    public int MicrophoneDevice { get; set; } = 0;
    public float MicrophoneSensitivity { get; set; } = 3.0f;
    public bool DebugMode { get; set; } = false;
    public bool StartWithWindows { get; set; } = false;
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public string ComputeMode { get; set; } = "cpu"; // cpu | gpu-vulkan | gpu-nvidia | hybrid
    public int GpuDevice { get; set; } = 0;

    private static string SettingsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "INT_VoiceToText");

    private static string SettingsPath => Path.Combine(SettingsDir, "settings.json");

    public static AppState Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppState>(File.ReadAllText(SettingsPath), JsonOpts) ?? Default();
        }
        catch { /* fall through to defaults */ }
        var def = Default();
        def.Save();
        return def;
    }

    public static AppState Default() => new();

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* non-fatal */ }
    }

    /// <summary>Map a hotkey description to Win32 MOD_* flags + virtual key code.</summary>
    public (uint modifiers, uint vk) ToWin32()
    {
        uint mods = 0;
        foreach (var m in Hotkey.Modifiers)
            mods |= m switch
            {
                "ctrl" or "control" => Native.W32.MOD_CONTROL,
                "shift" => Native.W32.MOD_SHIFT,
                "alt" => Native.W32.MOD_ALT,
                "win" => Native.W32.MOD_WIN,
                _ => 0u
            };

        uint vk = VkForKey(Hotkey.Key);
        return (mods, vk);
    }

    public static uint VkForKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return 0;
        var k = key.Trim();

        // F1..F24
        if (k.Length > 1 && k[0] == 'F' && uint.TryParse(k.AsSpan(1), out var fnum) && fnum is >= 1 and <= 24)
            return 0x70 + (uint)(fnum - 1);

        // Named keys
        switch (k.ToLowerInvariant())
        {
            case "space": return 0x20;
            case "enter" or "return": return 0x0D;
            case "esc" or "escape": return 0x1B;
            case "tab": return 0x09;
            case "backspace": return 0x08;
            case "delete" or "del": return 0x2E;
            case "insert": return 0x2D;
            case "home": return 0x24;
            case "end": return 0x23;
            case "pageup": return 0x21;
            case "pagedown": return 0x22;
        }

        // Single printable char → uppercase ASCII == VK code for A-Z, 0-9 and most symbols
        if (k.Length == 1)
            return (uint)char.ToUpperInvariant(k[0]);

        return 0; // unsupported → caller must reject
    }

    /// <summary>Human-readable hotkey label, e.g. "Ctrl+Shift+A".</summary>
    public string HotkeyLabel() => string.Join("+",
        Hotkey.Modifiers.Select(m => m switch { "ctrl" => "Ctrl", "shift" => "Shift", "alt" => "Alt", "win" => "Win", _ => m })
        .Append(Hotkey.Key.ToUpperInvariant()));
}
