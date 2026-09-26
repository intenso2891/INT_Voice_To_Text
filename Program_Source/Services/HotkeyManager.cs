// ============================================================================
// INT VoiceToText — global hotkey manager (RegisterHotKey on a message loop thread)
// ============================================================================

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using INTVoiceToText.Native;

namespace INTVoiceToText.Services;

/// <summary>
/// Owns a hidden message-only window on a dedicated thread with a real WndProc.
/// Registers one global hotkey (RegisterHotKey) and raises HotkeyPressed when it fires.
/// Re-registration is possible at runtime from the settings UI (thread-safe).
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    public const int HotkeyId = 0xC0DE;

    private const uint WM_HOTKEY_MSG = W32.WM_HOTKEY;   // 0x0312
    private const uint WM_RUN_COMMAND = 0x8001;         // custom, posted cross-thread

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private readonly Thread _thread;
    private readonly BlockingCollection<Action> _commands = new();
    private IntPtr _hwnd = IntPtr.Zero;
    private volatile bool _registered;

    // Keep the WNDPROC delegate alive for the lifetime of this object (GC safety!).
    private WndProcDelegate? _wndProc;

    /// <summary>Raised when the registered hotkey is pressed (on the hotkey thread).</summary>
    public event Action? HotkeyPressed;

    public bool IsRegistered => _registered;
    public int LastError { get; private set; }

    public HotkeyManager()
    {
        _thread = new Thread(ThreadMain)
        {
            Name = "INTVoiceToText-Hotkeys",
            IsBackground = true,
        };
        _thread.Start();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (_hwnd == IntPtr.Zero && sw.ElapsedMilliseconds < 5000) Thread.Sleep(20);
    }

    /// <summary>Register (or re-register) the global hotkey from any thread.</summary>
    public bool Register(uint modifiers, uint vk)
    {
        if (_hwnd == IntPtr.Zero) return false;
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _commands.Add(() =>
        {
            W32.UnregisterHotKey(_hwnd, HotkeyId);
            bool ok = W32.RegisterHotKey(_hwnd, HotkeyId, modifiers | W32.MOD_NOREPEAT, vk);
            LastError = ok ? 0 : Marshal.GetLastWin32Error();
            _registered = ok;
            done.TrySetResult(ok);
        });

        // Ask the hotkey thread to process queued commands.
        PostMessageW(_hwnd, WM_RUN_COMMAND, IntPtr.Zero, IntPtr.Zero);
        return Wait(done.Task, 4000);
    }

    public void Unregister()
    {
        if (_hwnd == IntPtr.Zero) return;
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _commands.Add(() =>
        {
            W32.UnregisterHotKey(_hwnd, HotkeyId);
            _registered = false;
            done.TrySetResult(true);
        });
        PostMessageW(_hwnd, WM_RUN_COMMAND, IntPtr.Zero, IntPtr.Zero);
    }

    private static bool Wait(Task<bool> t, int ms) => t.Wait(ms) && t.Result;

    // ---------------- thread main ----------------

    private void ThreadMain()
    {
        _wndProc = WndProc; // assign BEFORE CreateWindow so the delegate is pinned in memory
        var wc = new W32.WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<W32.WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandleW(null),
            lpszClassName = "INTVoiceToTextMsgWnd",
        };

        if (RegisterClassExW(ref wc) == 0)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        _hwnd = W32.CreateWindowExW(
            0x80 /* WS_EX_TOOLWINDOW */ | 0x08 /* WS_EX_NOACTIVATE */,
            "INTVoiceToTextMsgWnd", null, 0, // no WS_VISIBLE — hidden host window
            0, 0, 1, 1,
            new IntPtr(-3), // HWND_MESSAGE → message-only window (no taskbar entry)
            IntPtr.Zero, wc.hInstance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        // Standard message pump: also receives WM_HOTKEY posted by the system.
        while (W32.GetMessageWPublic(out var msg))
        {
            W32.TranslateDispatch(ref msg);
            if (msg.message == 0x0012 /* WM_QUIT */) break;
        }

        DestroyWindow(_hwnd);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_HOTKEY_MSG:
                if ((int)wParam == HotkeyId)
                    HotkeyPressed?.Invoke();
                return IntPtr.Zero;

            case WM_RUN_COMMAND:
                while (_commands.TryTake(out var cmd))
                {
                    try { cmd(); } catch { /* keep the loop alive */ }
                }
                return IntPtr.Zero;

            default:
                break;
        }
        return W32.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        Unregister();
        _commands.CompleteAdding();
    }

    // ---------------- local P/Invoke (thread-specific bits) ----------------

    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? lpModuleName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassExW(ref W32.WNDCLASSEXW wc);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
}
