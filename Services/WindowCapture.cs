using System;
using System.Drawing;
using System.Drawing.Imaging;
using HockeyRoller.Services;

namespace HockeyRoller.Services;

public static class WindowCapture
{
    public static string LastInfo { get; private set; } = "";
    public static int LastClientW { get; private set; }
    public static int LastClientH { get; private set; }

    // 0 = auto (best of both), 1 = screen grab, 2 = PrintWindow
    public static Bitmap? Capture(IntPtr hWnd, int method = 0)
    {
        if (hWnd == IntPtr.Zero) return null;
        Win32.GetClientRect(hWnd, out var crc);
        int cw = crc.Right - crc.Left;
        int ch = crc.Bottom - crc.Top;
        if (cw <= 0 || ch <= 0) return null;

        Win32.GetWindowRect(hWnd, out var wr);
        int ww = wr.Right - wr.Left;
        int wh = wr.Bottom - wr.Top;

        var pt = new Win32.POINT { X = 0, Y = 0 };
        Win32.ClientToScreen(hWnd, ref pt);
        int offX = pt.X - wr.Left;
        int offY = pt.Y - wr.Top;

        int sx = Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN);
        int sy = Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);
        int sw = Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN);
        int sh = Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN);

        string used = "none";
        Bitmap? best = null;

        try
        {
            if (method == 1)
            {
                best = CaptureScreen(pt.X, pt.Y, cw, ch);
                used = "screen";
            }
            else if (method == 2)
            {
                best = CapturePrintWindow(hWnd, cw, ch, ww, wh, offX, offY, ref used);
            }
            else
            {
                // Auto: grab both and keep the one with more actual picture content.
                // PrintWindow often returns a blank or half-drawn frame for DirectX games
                // (especially in exclusive fullscreen), while the screen grab is exact.
                var pw = CapturePrintWindow(hWnd, cw, ch, ww, wh, offX, offY, ref used);
                var sc = CaptureScreen(pt.X, pt.Y, cw, ch);
                double dpw = pw != null ? DetailScore(pw) : -1;
                double dsc = sc != null ? DetailScore(sc) : -1;

                if (pw != null && sc != null)
                {
                    if (dpw >= dsc) { best = pw; sc.Dispose(); used = $"printwindow(detail {dpw:F1})"; }
                    else { best = sc; pw.Dispose(); used = $"screen(detail {dsc:F1})"; }
                }
                else if (pw != null) { best = pw; used = $"printwindow(detail {dpw:F1})"; }
                else if (sc != null) { best = sc; used = $"screen(detail {dsc:F1})"; }
            }
        }
        catch { best?.Dispose(); best = null; }

        bool offScreen = wr.Left < sx || wr.Top < sy || wr.Right > sx + sw || wr.Bottom > sy + sh;
        LastClientW = cw; LastClientH = ch;
        LastInfo = $"win=({wr.Left},{wr.Top}) {ww}x{wh} | client={cw}x{ch} at ({pt.X},{pt.Y}) | screen={sw}x{sh}@({sx},{sy}) | {used} | partlyOffScreen={offScreen}";
        return best;
    }

    /// <summary>How much "picture" is in the image (luminance spread). Blank frames score ~0.</summary>
    private static double DetailScore(Bitmap bmp)
    {
        try
        {
            const int n = 24;
            double sum = 0, sum2 = 0;
            int count = 0;
            for (int y = 0; y < n; y++)
            {
                int sy = Math.Min(bmp.Height - 1, y * bmp.Height / n);
                for (int x = 0; x < n; x++)
                {
                    int sx = Math.Min(bmp.Width - 1, x * bmp.Width / n);
                    var c = bmp.GetPixel(sx, sy);
                    double lum = (c.R * 0.299 + c.G * 0.587 + c.B * 0.114);
                    sum += lum; sum2 += lum * lum; count++;
                }
            }
            if (count == 0) return 0;
            double mean = sum / count;
            double var2 = Math.Max(0, sum2 / count - mean * mean);
            return Math.Sqrt(var2);
        }
        catch { return 0; }
    }

    private static Bitmap? CapturePrintWindow(IntPtr hWnd, int cw, int ch, int ww, int wh, int offX, int offY, ref string used)
    {
        if (ww <= 0 || wh <= 0) return null;
        using var full = new Bitmap(ww, wh, PixelFormat.Format24bppRgb);
        using (var gFull = Graphics.FromImage(full))
        {
            IntPtr hdc = gFull.GetHdc();
            bool ok = Win32.PrintWindow(hWnd, hdc, Win32.PW_RENDERFULLCONTENT);
            if (!ok)
            {
                IntPtr hdcWin = Win32.GetWindowDC(hWnd);
                Win32.BitBlt(hdc, 0, 0, ww, wh, hdcWin, 0, 0, Win32.SRCCOPY);
                Win32.ReleaseDC(hWnd, hdcWin);
                used = "bitblt";
            }
            else used = "printwindow";
            gFull.ReleaseHdc(hdc);
        }
        var bmp = new Bitmap(cw, ch, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.DrawImage(full, new Rectangle(0, 0, cw, ch),
                new Rectangle(offX, offY, cw, ch), GraphicsUnit.Pixel);
        }
        return bmp;
    }

    private static Bitmap? CaptureScreen(int x, int y, int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        try
        {
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
        }
        catch { bmp.Dispose(); return null; }
        return bmp;
    }

    /// <summary>
    /// Finds red buttons (the game's "X" close buttons) in the upper part of the image.
    /// Returns each button's centre, top-to-bottom order.
    /// </summary>
    public static List<System.Drawing.Point> FindRedButtons(Bitmap bmp, int minBrightness = 110)
    {
        var result = new List<System.Drawing.Point>();
        try
        {
            int maxY = Math.Min(bmp.Height, 600);
            var cols = new List<int>();
            for (int x = 0; x < bmp.Width; x++)
            {
                bool has = false;
                for (int y = 0; y < maxY && !has; y++)
                {
                    var c = bmp.GetPixel(x, y);
                    if (c.R > minBrightness && c.R - c.G > 45 && c.R - c.B > 45) has = true;
                }
                if (has) cols.Add(x);
            }
            if (cols.Count == 0) return result;

            var clusters = new List<(int minX, int maxX)>();
            int start = cols[0], prev = cols[0];
            for (int i = 1; i < cols.Count; i++)
            {
                if (cols[i] - prev > 40)
                {
                    clusters.Add((start, prev));
                    start = cols[i];
                }
                prev = cols[i];
            }
            clusters.Add((start, prev));

            foreach (var cl in clusters)
            {
                int minY = int.MaxValue, maxY2 = 0;
                for (int y = 0; y < maxY; y++)
                    for (int x = cl.minX; x <= cl.maxX; x++)
                    {
                        var c = bmp.GetPixel(x, y);
                        if (c.R > minBrightness && c.R - c.G > 45 && c.R - c.B > 45)
                        {
                            if (y < minY) minY = y;
                            if (y > maxY2) maxY2 = y;
                        }
                    }
                if (minY == int.MaxValue) continue;
                // ignore wide red bands (banners) - keep button sized things
                int w = cl.maxX - cl.minX, h = maxY2 - minY;
                if (w > 200 || h > 200 || w < 20) continue;
                result.Add(new System.Drawing.Point((cl.minX + cl.maxX) / 2, (minY + maxY2) / 2));
            }
            return result;
        }
        catch { return result; }
    }

    /// <summary>Right-most red button in the top bar (the game's own close button).</summary>
    public static System.Drawing.Point? FindRedCloseButton(Bitmap bmp)
    {
        var buttons = FindRedButtons(bmp, 140);
        System.Drawing.Point? best = null;
        foreach (var b in buttons)
        {
            if (b.Y > 200) continue;               // top bar only
            if (best == null || b.X > best.Value.X) best = b;
        }
        return best;
    }

    /// <summary>
    /// The red X inside the odds panel (the modal's own close button), which is what
    /// actually closes the odds window. Null when the panel is not open.
    /// </summary>
    public static System.Drawing.Point? FindPanelCloseButton(Bitmap bmp)
    {
        var buttons = FindRedButtons(bmp, 140);    // bright red only (RESTART is darker)
        System.Drawing.Point? best = null;
        foreach (var b in buttons)
        {
            if (b.Y < 220) continue;               // below the top bar
            if (best == null || b.Y > best.Value.Y) best = b;   // lowest red button
        }
        return best;
    }

    /// <summary>The purple ODDS button in the top bar.</summary>
    public static System.Drawing.Rectangle? FindOddsButton(Bitmap bmp)
    {
        int top = Math.Min(200, bmp.Height);
        var rect = FindBlock(bmp, new Rectangle(560, Math.Max(0, top - 140), 480, top),
            (r, g, b) => b > 70 && b - g > 40 && b - r > 8, 70, 20);
        return rect;
    }

    /// <summary>
    /// Finds a solid coloured rectangle (button) inside an area.
    /// </summary>
    public static System.Drawing.Rectangle? FindBlock(Bitmap bmp, Rectangle area,
        Func<int, int, int, bool> test, int minWidth, int minHeight)
    {
        try
        {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, count = 0;
            for (int y = Math.Max(0, area.Y); y < Math.Min(bmp.Height, area.Bottom); y++)
                for (int x = Math.Max(0, area.X); x < Math.Min(bmp.Width, area.Right); x++)
                {
                    var c = bmp.GetPixel(x, y);
                    if (!test(c.R, c.G, c.B)) continue;
                    count++;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            if (count == 0 || minX == int.MaxValue) return null;
            var rect = new System.Drawing.Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
            if (rect.Width < minWidth || rect.Height < minHeight) return null;
            if (count < rect.Width * rect.Height * 0.15) return null;   // too sparse -> not a solid block
            return rect;
        }
        catch { return null; }
    }

    /// <summary>The big green ROLL button (bottom left of the roll screen).</summary>
    public static System.Drawing.Rectangle? FindRollButton(Bitmap bmp)
    {
        int minY = (int)(bmp.Height * 0.60);
        return FindBlock(bmp, new Rectangle(0, minY, Math.Min(900, bmp.Width), bmp.Height - minY),
            (r, g, b) => g > 90 && g - r > 45 && g - b > 45, 220, 22);
    }

    /// <summary>The big blue REFRESH button (under the ROLL button).</summary>
    public static System.Drawing.Rectangle? FindRefreshButton(Bitmap bmp)
    {
        int minY = (int)(bmp.Height * 0.78);
        return FindBlock(bmp, new Rectangle(0, minY, Math.Min(900, bmp.Width), bmp.Height - minY),
            (r, g, b) => b > 90 && b - r > 45 && b - g > 30, 220, 22);
    }

    public static Bitmap Crop(Bitmap src, System.Drawing.Rectangle r)
    {
        var dst = new Bitmap(r.Width, r.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(dst);
        g.DrawImage(src, new Rectangle(0, 0, r.Width, r.Height), r, GraphicsUnit.Pixel);
        return dst;
    }
}
