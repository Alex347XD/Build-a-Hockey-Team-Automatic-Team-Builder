using System;
using System.Drawing;
using System.Drawing.Imaging;

namespace HockeyRoller.Services;

/// <summary>
/// Detects the diagonal black ribbon (with white text) that the game draws across a card
/// when the player is already on your team or the position is full.
/// </summary>
public static class DiagonalStripDetector
{
    public static bool HasDiagonalStrip(Bitmap bmp, out double score)
    {
        score = 0;
        try
        {
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            if (bmp.Width < 20 || bmp.Height < 20) return false;

            var dark = new bool[bmp.Width * bmp.Height];
            int darkCount = 0;
            for (int y = 0; y < bmp.Height; y++)
                for (int x = 0; x < bmp.Width; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    int max = Math.Max(c.R, Math.Max(c.G, c.B));
                    bool isDark = max < 55;   // near-black
                    dark[y * bmp.Width + x] = isDark;
                    if (isDark) darkCount++;
                }

            if (darkCount < bmp.Width * bmp.Height * 0.01) return false;

            int step = Math.Max(3, Math.Min(bmp.Width, bmp.Height) / 30);
            int diag = 0, hv = 0;

            for (int y = 0; y + step < bmp.Height; y++)
                for (int x = 0; x + step < bmp.Width; x++)
                {
                    if (!dark[y * bmp.Width + x]) continue;
                    // 45 degree diagonals
                    if (dark[(y + step) * bmp.Width + (x + step)]) diag++;
                    if (dark[(y + step) * bmp.Width + (x - step)] && x >= step) diag++;
                    // horizontal / vertical reference
                    if (dark[y * bmp.Width + (x + step)]) hv++;
                    if (dark[(y + step) * bmp.Width + x]) hv++;
                }

            if (hv == 0 && diag == 0) return false;
            double ratio = (double)diag / Math.Max(1, hv);
            // a real diagonal ribbon is much more "diagonal" than axis-aligned
            score = ratio;
            return diag > (bmp.Width * bmp.Height / 120) && ratio > 1.15;
        }
        catch { return false; }
    }
}
