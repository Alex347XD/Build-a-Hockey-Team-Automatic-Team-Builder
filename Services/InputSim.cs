using System;
using System.Runtime.InteropServices;
using HockeyRoller.Services;

namespace HockeyRoller.Services;

public static class InputSim
{
    public static string LastClickInfo { get; private set; } = "";

    // mouse_event flags
    const uint MOVE_ABSOLUTE = 0x8000;
    const uint MOVE_VIRTUALDESK = 0x4000;
    const uint LEFTDOWN = 0x0002;
    const uint LEFTUP = 0x0004;

    /// <summary>
    /// Some systems silently discard SendInput mouse movement (it reports success but the
    /// driver drops it). The legacy mouse_event path works everywhere, so it is the default.
    /// </summary>
    public static bool UseLegacyMouseEvent = true;

    public static void MouseEvent(uint flags, int dx, int dy)
    {
        Win32.mouse_event(flags, dx, dy, 0, UIntPtr.Zero);
    }

    /// <summary>Glides the pointer to a screen point using real move events.</summary>
    public static void MoveTo(int x, int y, int steps = 12)
    {
        var cur = new Win32.POINT();
        Win32.GetCursorPos(out cur);
        int startX = cur.X, startY = cur.Y;
        int dx = x - startX, dy = y - startY;

        for (int i = 1; i <= steps; i++)
        {
            double t = (double)i / steps;
            int nx = startX + (int)(dx * t);
            int ny = startY + (int)(dy * t);
            if (UseLegacyMouseEvent)
            {
                // relative move of the remaining distance
                MouseEvent(0x0001, nx - cur.X, ny - cur.Y);
            }
            else
            {
                SendMoveAbsolute(nx, ny);
            }
            cur.X = nx; cur.Y = ny;
            System.Threading.Thread.Sleep(16);
        }
        // land exactly on the target
        if (UseLegacyMouseEvent) Win32.SetCursorPos(x, y);
        else SendMoveAbsolute(x, y);
        System.Threading.Thread.Sleep(40);
    }

    /// <summary>
    /// Moves the pointer ONTO a target so the game actually re-evaluates its hover state.
    /// Games only update "what is under the cursor" when the pointer MOVES, so if the
    /// cursor is already parked on the button the press would be delivered to whatever
    /// the game thought was under it. This first backs off, then travels onto the target.
    /// </summary>
    public static void Approach(int x, int y)
    {
        var cur = new Win32.POINT();
        Win32.GetCursorPos(out cur);

        bool alreadyThere = Math.Abs(cur.X - x) < 90 && Math.Abs(cur.Y - y) < 90;
        if (alreadyThere)
        {
            // back off to the upper-left so the next move is a real movement onto the button
            int awayX = Math.Max(2, x - 120);
            int awayY = Math.Max(2, y - 70);
            MoveTo(awayX, awayY, 5);
            System.Threading.Thread.Sleep(40);
        }

        // travel onto the target
        MoveTo(x, y, 14);

        // make sure the pointer is exactly on the target (relative moves can drift)
        Win32.SetCursorPos(x, y);
        System.Threading.Thread.Sleep(20);

        // one final real move event AT the exact position, so the game updates its hover
        MouseEvent(0x0001, 1, 0);
        System.Threading.Thread.Sleep(10);
        MouseEvent(0x0001, -1, 0);

        // let the game settle before pressing
        System.Threading.Thread.Sleep(70);

        var now = new Win32.POINT();
        Win32.GetCursorPos(out now);
        if (now.X != x || now.Y != y) Win32.SetCursorPos(x, y);
    }

    private static void SendMoveAbsolute(int x, int y)
    {
        int vx = Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN);
        int vy = Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);
        int vw = Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN);
        int vh = Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN);
        int ax = (int)Math.Round((x - vx) * 65535.0 / Math.Max(1, vw - 1));
        int ay = (int)Math.Round((y - vy) * 65535.0 / Math.Max(1, vh - 1));

        var inp = new Win32.INPUT[1];
        inp[0].type = Win32.INPUT_MOUSE;
        inp[0].mi.dx = ax;
        inp[0].mi.dy = ay;
        inp[0].mi.dwFlags = Win32.MOUSEEVENTF_MOVE | Win32.MOUSEEVENTF_ABSOLUTE | Win32.MOUSEEVENTF_VIRTUALDESK;
        Win32.SendInput(1, inp, Marshal.SizeOf(typeof(Win32.INPUT)));
    }

    private static void Press(int holdMs)
    {
        if (UseLegacyMouseEvent)
        {
            MouseEvent(LEFTDOWN, 0, 0);
            System.Threading.Thread.Sleep(holdMs);
            MouseEvent(LEFTUP, 0, 0);
        }
        else
        {
            int size = Marshal.SizeOf(typeof(Win32.INPUT));
            var down = new Win32.INPUT[1];
            down[0].type = Win32.INPUT_MOUSE;
            down[0].mi.dwFlags = Win32.MOUSEEVENTF_LEFTDOWN;
            Win32.SendInput(1, down, size);
            System.Threading.Thread.Sleep(holdMs);
            var up = new Win32.INPUT[1];
            up[0].type = Win32.INPUT_MOUSE;
            up[0].mi.dwFlags = Win32.MOUSEEVENTF_LEFTUP;
            Win32.SendInput(1, up, size);
        }
    }

    /// <summary>Clicks a screen point the way a person does: approach, press, (press again).</summary>
    public static bool ClickScreen(int x, int y, bool doublePress = true)
    {
        Approach(x, y);

        Press(70);
        if (doublePress)
        {
            System.Threading.Thread.Sleep(180);
            Press(120);
        }

        var cur = new Win32.POINT();
        Win32.GetCursorPos(out cur);
        LastClickInfo = $"click screen({x},{y}) cursor=({cur.X},{cur.Y}) presses={(doublePress ? 2 : 1)} " +
                        $"api={(UseLegacyMouseEvent ? "mouse_event" : "SendInput")} fg='{Win32.ForegroundTitle()}' " +
                        $"under={Win32.DescribeWindowAt(cur.X, cur.Y)}";
        return cur.X == x && cur.Y == y;
    }

    public static bool ClickClient(IntPtr hWnd, int clientX, int clientY, bool doublePress = true)
    {
        var pt = new Win32.POINT { X = clientX, Y = clientY };
        Win32.ClientToScreen(hWnd, ref pt);
        return ClickScreen(pt.X, pt.Y, doublePress);
    }

    /// <summary>Posts mouse messages straight to the window (alternative click path).</summary>
    public static bool PostClick(IntPtr hWnd, int clientX, int clientY)
    {
        try
        {
            int lp = (clientY << 16) | (clientX & 0xFFFF);
            Win32.PostMessage(hWnd, Win32.WM_MOUSEMOVE, IntPtr.Zero, new IntPtr(lp));
            System.Threading.Thread.Sleep(40);
            Win32.PostMessage(hWnd, Win32.WM_LBUTTONDOWN, new IntPtr(1), new IntPtr(lp));
            System.Threading.Thread.Sleep(50);
            Win32.PostMessage(hWnd, Win32.WM_LBUTTONUP, IntPtr.Zero, new IntPtr(lp));
            LastClickInfo = $"postclick client({clientX},{clientY}) fg='{Win32.ForegroundTitle()}'";
            return true;
        }
        catch { return false; }
    }

    public static void Focus(IntPtr hWnd)
    {
        // NB: do NOT call ShowWindow(SW_RESTORE) - it would un-maximize / exit fullscreen
        Win32.SetForegroundWindow(hWnd);
        System.Threading.Thread.Sleep(100);
    }

    /// <summary>Scrolls the mouse wheel at a screen position (clicks: negative = down).</summary>
    public static void ScrollWheel(int x, int y, int clicks)
    {
        MoveTo(x, y, 4);
        System.Threading.Thread.Sleep(50);
        Win32.mouse_event(0x0800, 0, 0, unchecked((uint)(clicks * 120)), UIntPtr.Zero);
    }

    public static void PressEscape()
    {
        Win32.SendKey(0x1B);
    }
}
