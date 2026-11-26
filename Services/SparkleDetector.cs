using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Collections.Generic;

namespace HockeyRoller.Services;

/// <summary>
/// Detects small bright "sparkle" dots on a card (used to tell Rivalry from Rivalry Captain).
/// Large white areas (player name text, logos) are ignored by only counting small components.
/// </summary>
public static class SparkleDetector
{
    public static int CountSparkles(Bitmap bmp)
    {
        try
        {
            // work on a downscaled copy for speed
            int scale = Math.Max(1, bmp.Width / 180);
            int w = bmp.Width / scale, h = bmp.Height / scale;
            if (w < 4 || h < 4) return 0;

            var mask = new bool[w * h];
            using var small = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.DrawImage(bmp, 0, 0, w, h);
            }
            var data = small.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            int count = 0;
            try
            {
                unsafe
                {
                    byte* p = (byte*)data.Scan0;
                    int stride = data.Stride;
                    for (int y = 0; y < h; y++)
                    {
                        byte* row = p + y * stride;
                        for (int x = 0; x < w; x++)
                        {
                            int b = row[x * 3], g = row[x * 3 + 1], r = row[x * 3 + 2];
                            int max = Math.Max(r, Math.Max(g, b));
                            int min = Math.Min(r, Math.Min(g, b));
                            // near-white and bright => candidate sparkle
                            mask[y * w + x] = (max >= 235 && (max - min) <= 28);
                        }
                    }
                }

                var seen = new bool[w * h];
                var stack = new Stack<int>();
                for (int i = 0; i < mask.Length; i++)
                {
                    if (!mask[i] || seen[i]) continue;
                    int area = 0;
                    stack.Push(i); seen[i] = true;
                    while (stack.Count > 0)
                    {
                        int idx = stack.Pop();
                        area++;
                        int cx = idx % w, cy = idx / w;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = cx + dx, ny = cy + dy;
                                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                                int n = ny * w + nx;
                                if (mask[n] && !seen[n]) { seen[n] = true; stack.Push(n); }
                            }
                    }
                    // sparkles are tiny isolated dots
                    if (area >= 2 && area <= 90) count++;
                }
            }
            finally { small.UnlockBits(data); }
            return count;
        }
        catch { return 0; }
    }
}
