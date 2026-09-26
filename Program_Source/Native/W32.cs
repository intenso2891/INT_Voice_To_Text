// ============================================================================
// INT VoiceToText — Win32 P/Invoke helpers (clipboard, SendInput, work area)
// ============================================================================

using System.Runtime.InteropServices;
using System.Text;

namespace INTVoiceToText.Native;

public static class W32
{
    // ---------- Work area (screen usable rectangle) ----------
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    private const int SPI_GETWORKAREA = 0;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfoW(int uiAction, int uiParam, out RECT pvParam, int fWinIni);

    public static RECT GetWorkArea()
    {
        if (SystemParametersInfoW(SPI_GETWORKAREA, 0, out var rect, 0)) return rect;
        return new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    }

    // ---------- Foreground window ----------
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AttachThreadInput(uint idAttach, uint idTarget, bool fAttach);

    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);

    /// <summary>Restore focus to a window (best-effort) so we can paste into it.</summary>
    public static void RestoreForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            AllowSetForegroundWindow(GetWindowThreadProcessId(hwnd, out _).ToInt32());
            uint target = (uint)GetWindowThreadProcessId(hwnd, out _).ToInt64();
            uint current = GetCurrentThreadId();
            AttachThreadInput(current, target, true);
            SetForegroundWindow(hwnd);
            BringWindowToTop(hwnd);
            AttachThreadInput(current, target, false);
        }
        catch { /* best effort */ }
    }

    // ---------- Clipboard (CF_UNICODETEXT) ----------
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr hMem);

    public static void SetClipboardText(string text)
    {
        if (!OpenClipboard(IntPtr.Zero)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            EmptyClipboard();
            byte[] utf16 = Encoding.Unicode.GetBytes(text + "\0"); // NUL-terminated
            IntPtr hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)utf16.Length);
            if (hGlobal == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            IntPtr pLocked = GlobalLock(hGlobal);
            try { Marshal.Copy(utf16, 0, pLocked, utf16.Length); }
            finally { GlobalUnlock(hGlobal); }

            if (SetClipboardData(CF_UNICODETEXT, hGlobal) == IntPtr.Zero)
            {
                GlobalFree(hGlobal); // clipboard didn't take ownership on failure
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally { CloseClipboard(); }
    }

    // ---------- SendInput (Ctrl+V keystroke) ----------
    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private static readonly IntPtr EXTRA_INFO_MAGIC = new IntPtr(0xB4B0);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    public static void SendCtrlV()
    {
        ushort vkControl = 0x11;
        ushort vkV = 0x56;

        var inputs = new INPUT[4]
        {
            Key(vkControl, false), Key(vkV, false), Key(vkV, true), Key(vkControl, true)
        };

        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length && Marshal.GetLastWin32Error() != 0)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        static INPUT Key(ushort vk, bool up) => new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = up ? KEYEVENTF_KEYUP : 0u,
                    time = 0,
                    dwExtraInfo = EXTRA_INFO_MAGIC
                }
            }
        };
    }

    // ---------- Global hotkey constants (MOD_*) ----------
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    // ---------- Message-only window + hotkey registration ----------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    private static readonly DefWindowProcDelegate _defWindowProc = DefWindowProcW; // keep alive (GC safety)

    private delegate IntPtr DefWindowProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Public wrapper around DefWindowProc (delegate stays alive via _defWindowProc).</summary>
    public static IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        => DefWindowProcW(hWnd, msg, wParam, lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string? lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);

    public static void ResizeWindow(IntPtr hwnd, int x, int y, int width, int height)
    {
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    public static void SetTopMost(IntPtr hwnd, bool topMost)
    {
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, topMost ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0,
            SWP_NOACTIVATE | SWP_SHOWWINDOW | 0x0001 | 0x0002);
    }
    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;
    private const int SW_SHOWNOACTIVATE = 4;
    private const int SW_SHOWNORMAL = 1;
    private const int GWL_STYLE = -16;
    private const long WS_VISIBLE = 0x10000000L;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    public static void ForceShow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        ShowWindow(hwnd, SW_SHOWNORMAL);
        ShowWindow(hwnd, SW_RESTORE);
        var style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        if ((style & WS_VISIBLE) == 0) SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style | WS_VISIBLE));
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, 0x0001 | 0x0002 | SWP_SHOWWINDOW);
        UpdateWindow(hwnd);
        ShowWindow(hwnd, SW_RESTORE);
    }

    public static void ConstrainedShow(IntPtr hwnd, RECT wa, int width, int height)
    {
        if (hwnd == IntPtr.Zero) return;
        ShowWindow(hwnd, SW_SHOWNORMAL);
        ShowWindow(hwnd, SW_RESTORE);
        SetWindowPos(hwnd, HWND_TOPMOST, wa.Right - width - 24, wa.Top + 24, width, height, SWP_SHOWWINDOW);
        UpdateWindow(hwnd);
        ShowWindow(hwnd, SW_RESTORE);
    }
    [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr hwnd);

    /// <summary>Restore + raise a window so it becomes visible/foreground (works cross-thread).</summary>
    public static void HideWindow(IntPtr hwnd) => ShowWindow(hwnd, 0);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    public static bool IsVisible(IntPtr h) => IsWindowVisible(h);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")] private static extern uint GetWindowThreadProcessId2(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", EntryPoint = "IsWindow")] private static extern bool IsWindow2(IntPtr hwnd);

    /// <summary>Find the top-level main window of the current process whose title matches.</summary>
    public static IntPtr FindMainWindow(string titlePart = "INT VoiceToText")
    {
        IntPtr found = IntPtr.Zero;
        uint self = unchecked((uint)Environment.ProcessId);
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId2(hwnd, out var pid);
            if (pid != self || !IsWindow2(hwnd)) return true;
            int len = GetWindowTextLength(hwnd);
            if (len <= 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            if (sb.ToString().Contains(titlePart, StringComparison.OrdinalIgnoreCase)) { found = hwnd; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static void ShowWindowToFront(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            ShowWindow(hwnd, SW_RESTORE);
            ShowWindow(hwnd, SW_SHOW);
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, 0x0001 | 0x0002 | SWP_SHOWWINDOW);
            SetForegroundWindow(hwnd);
            BringWindowToTop(hwnd);
            ShowWindow(hwnd, SW_RESTORE);
        }
        catch { /* best effort */ }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string? lpModuleName);
    [DllImport("user32.dll")] public static extern bool PostThreadMessageW(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    public const uint WM_QUIT = 0x0012;
    public const uint WM_HOTKEY = 0x0312;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const uint PM_REMOVE = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX, ptY;
    }

    /// <summary>Blocking GetMessage on the calling thread (returns false on WM_QUIT).</summary>
    [DllImport("user32.dll")] private static extern bool GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    public static bool GetMessageWPublic(out MSG msg) => GetMessageW(out msg, IntPtr.Zero, 0, 0);

    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    public static void TranslateDispatch(ref MSG msg)
    {
        if (TranslateMessage(ref msg)) return;
        DispatchMessageW(ref msg);
    }
}
