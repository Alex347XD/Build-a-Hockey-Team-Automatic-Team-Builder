using System;
using System.Runtime.InteropServices;

namespace HockeyRoller.Services;

public static class Win32
{
    public const int PW_CLIENTONLY = 0x00000001;
    public const int PW_RENDERFULLCONTENT = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindowDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    public static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight,
        IntPtr hdcSrc, int nXSrc, int nYSrc, uint dwRop);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr hdc);

    public const uint SRCCOPY = 0x00CC0020;

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    public const int SW_RESTORE = 9;

    // Input
    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
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

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(4)] public MOUSEINPUT mi;
        [FieldOffset(4)] public KEYBDINPUT ki;
        [FieldOffset(4)] public HARDWAREINPUT hi;
    }

    public const uint INPUT_MOUSE = 0;
    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    public const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    public static void SendKey(byte vk)
    {
        var inputs = new INPUT[2];
        inputs[0].type = 1; // INPUT_KEYBOARD
        inputs[0].ki.wVk = vk;
        inputs[0].ki.dwFlags = 0;
        inputs[1].type = 1;
        inputs[1].ki.wVk = vk;
        inputs[1].ki.dwFlags = 0x0002; // KEYEVENTF_KEYUP
        SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
    }

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);
    public const int SM_CXSCREEN = 0;
    public const int SM_CYSCREEN = 1;
    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
    public const uint GA_ROOT = 2;

    [DllImport("user32.dll")]
    public static extern IntPtr GetFocus();

    /// <summary>Describes the window that is actually under a screen point.</summary>
    public static string DescribeWindowAt(int x, int y)
    {
        try
        {
            var pt = new POINT { X = x, Y = y };
            IntPtr h = WindowFromPoint(pt);
            if (h == IntPtr.Zero) return "none";
            var sb = new System.Text.StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            GetWindowThreadProcessId(h, out uint pid);
            string title = "";
            int len = GetWindowTextLength(h);
            if (len > 0)
            {
                var tsb = new System.Text.StringBuilder(len + 1);
                GetWindowText(h, tsb, tsb.Capacity);
                title = tsb.ToString();
            }
            IntPtr root = GetAncestor(h, GA_ROOT);
            return $"hwnd=0x{h.ToInt64():X} class='{sb}' pid={pid} root=0x{root.ToInt64():X} title='{title}'";
        }
        catch { return "?"; }
    }

    // global keyboard hook (so ESC works even when the tool is minimised)
    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern IntPtr GetModuleHandle(string lpModuleName);
    public const int WH_KEYBOARD_LL = 13;

    [DllImport("shell32.dll")]
    public static extern bool IsUserAnAdmin();

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass,
        out TokenElevation elevation, int tokenInformationLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    public struct TokenElevation { public int TokenIsElevated; }

    public const uint TOKEN_QUERY = 0x0008;
    public const int TokenElevationClass = 20;

    /// <summary>True when this process runs elevated (as administrator).</summary>
    public static bool IsElevated()
    {
        try
        {
            if (!IsUserAnAdmin()) return false;
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out var token)) return false;
            try
            {
                if (!GetTokenInformation(token, TokenElevationClass, out var elev, Marshal.SizeOf<TokenElevation>(), out _))
                    return false;
                return elev.TokenIsElevated != 0;
            }
            finally { CloseHandle(token); }
        }
        catch { return false; }
    }

    /// <summary>True when the window belongs to a process running elevated.</summary>
    public static bool IsWindowElevated(IntPtr hWnd)
    {
        try
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            IntPtr proc = OpenProcess(0x1000 /*PROCESS_QUERY_LIMITED_INFORMATION*/, false, (IntPtr)pid);
            if (proc == IntPtr.Zero) return false;
            try
            {
                if (!OpenProcessToken(proc, TOKEN_QUERY, out var token)) return false;
                try
                {
                    if (!GetTokenInformation(token, TokenElevationClass, out var elev, Marshal.SizeOf<TokenElevation>(), out _))
                        return false;
                    return elev.TokenIsElevated != 0;
                }
                finally { CloseHandle(token); }
            }
            finally { CloseHandle(proc); }
        }
        catch { return false; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, IntPtr processId);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_MOUSEMOVE = 0x0200;

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

    public static string ForegroundTitle()
    {
        try
        {
            IntPtr h = GetForegroundWindow();
            if (h == IntPtr.Zero) return "";
            int len = GetWindowTextLength(h);
            if (len == 0) return "(no title)";
            var sb = new System.Text.StringBuilder(len + 1);
            GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }
        catch { return ""; }
    }

    [DllImport("shcore.dll")]
    public static extern int SetProcessDpiAwareness(int value);
    public const int PROCESS_PER_MONITOR_DPI_AWARE = 2;

    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();
}
