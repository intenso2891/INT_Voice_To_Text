// ============================================================================
// INT VoiceToText — insert text at the current cursor position (clipboard + Ctrl+V)
// ============================================================================

using INTVoiceToText.Native;

namespace INTVoiceToText.Services;

/// <summary>
/// Pastes text into whatever application currently has the text cursor:
/// clipboard (CF_UNICODETEXT) → restore target foreground window → SendInput Ctrl+V.
/// </summary>
public static class TextInserter
{
    /// <param name="text">Text to insert.</param>
    /// <param name="targetWindow">Hwnd of the window that should receive the paste (captured at recording start).</param>
    public static void CopyToClipboard(string text)
    {
        if (!string.IsNullOrEmpty(text)) W32.SetClipboardText(text);
    }

    public static void Insert(string text, IntPtr targetWindow)
    {
        if (string.IsNullOrEmpty(text)) return;

        W32.SetClipboardText(text);

        // Bring the user's window back to focus so Ctrl+V lands in their cursor.
        if (targetWindow != IntPtr.Zero && targetWindow != W32.GetForegroundWindow())
            W32.RestoreForeground(targetWindow);

        // Small settle delay — Windows needs a beat after a foreground switch.
        Thread.Sleep(120);

        W32.SendCtrlV();
    }
}
