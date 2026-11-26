using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Collections.Generic;

namespace HockeyRoller.Services;

public struct FrameSample
{
    public Color Average;      // saturation-weighted mean colour of the frame
    public Color Peak;         // most saturated colour found on the frame
    public double Dispersion;  // 0 = single hue, 1 = rainbow
    public double Saturation;
    public bool Valid;
}

public static class ColorMatch
{
    public static double Distance(Color a, Color b)
    {
        int dr = a.R - b.R;
        int dg = a.G - b.G;
        int db = a.B - b.B;
        return Math.Sqrt(dr * dr + dg * dg + db * db);
    }

    public static Color Average(Bitmap bmp, System.Drawing.Rectangle r)
    {
        long sr = 0, sg = 0, sb = 0, n = 0;
        for (int y = r.Top; y < r.Bottom && y < bmp.Height; y++)
            for (int x = r.Left; x < r.Right && x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                sr += c.R; sg += c.G; sb += c.B; n++;
            }
        if (n == 0) return Color.Black;
        return Color.FromArgb((int)(sr / n), (int)(sg / n), (int)(sb / n));
    }

    public static Color AverageFast(Bitmap bmp, System.Drawing.Rectangle r)
    {
        var rect = new System.Drawing.Rectangle(
            Math.Max(0, r.X), Math.Max(0, r.Y),
            Math.Min(r.Width, bmp.Width - r.X), Math.Min(r.Height, bmp.Height - r.Y));
        if (rect.Width <= 0 || rect.Height <= 0) return Color.Black;
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            int stride = data.Stride;
            long sr = 0, sg = 0, sb = 0, n = 0;
            unsafe
            {
                byte* p = (byte*)data.Scan0;
                for (int y = 0; y < rect.Height; y++)
                {
                    byte* row = p + y * stride;
                    for (int x = 0; x < rect.Width; x++)
                    {
                        sb += row[x * 3];
                        sg += row[x * 3 + 1];
                        sr += row[x * 3 + 2];
                        n++;
                    }
                }
            }
            if (n == 0) return Color.Black;
            return Color.FromArgb((int)(sr / n), (int)(sg / n), (int)(sb / n));
        }
        finally { bmp.UnlockBits(data); }
    }

    /// <summary>
    /// Samples the frame of a card: the outer band of the rectangle.
    ///  - Average : saturation-weighted mean colour (good for single-hue frames)
    ///  - Peak    : most saturated colour found (survives a little background bleed)
    ///  - Dispersion : 0..1, how spread out the hues are (1 = rainbow -> prismatic)
    /// </summary>
    public static FrameSample SampleFrame(Bitmap bmp, System.Drawing.Rectangle card)
    {
        var res = new FrameSample();
        var rect = new System.Drawing.Rectangle(
            Math.Max(0, card.X), Math.Max(0, card.Y),
            Math.Min(card.Width, bmp.Width - card.X), Math.Min(card.Height, bmp.Height - card.Y));
        if (rect.Width < 8 || rect.Height < 8) return res;

        // outer band of the card (frame + a bit of the artwork)
        int bandX = Math.Max(2, (int)(rect.Width * 0.18));
        int bandY = Math.Max(2, (int)(rect.Height * 0.18));

        double sumX = 0, sumY = 0, wsum = 0, satSum = 0;
        int counted = 0;
        double bestScore = -1;
        Color peak = Color.Black;
        double hueSum = 0;

        for (int y = rect.Top; y < rect.Top + rect.Height; y++)
            for (int x = rect.Left; x < rect.Left + rect.Width; x++)
            {
                bool onBand = x < rect.Left + bandX || x >= rect.Left + rect.Width - bandX ||
                              y < rect.Top + bandY || y >= rect.Top + rect.Height - bandY;
                if (!onBand) continue;
                var c = bmp.GetPixel(x, y);
                double h, s, v;
                RgbToHsv(c.R, c.G, c.B, out h, out s, out v);
                satSum += s;
                counted++;
                if (s > 0.15 && v > 0.12)
                {
                    double rad = h * Math.PI / 180.0;
                    sumX += Math.Cos(rad) * s;
                    sumY += Math.Sin(rad) * s;
                    wsum += s;
                    hueSum += h;
                    double score = s * v;
                    if (score > bestScore) { bestScore = score; peak = c; }
                }
            }

        if (counted == 0) return res;
        res.Saturation = satSum / counted;
        res.Peak = peak;

        if (wsum > 0)
        {
            double meanHue = (Math.Atan2(sumY, sumX) * 180.0 / Math.PI + 360) % 360;
            // circular dispersion: 0 = one hue, 1 = hues spread evenly over the wheel
            double resultant = Math.Sqrt(sumX * sumX + sumY * sumY) / wsum;
            res.Dispersion = 1.0 - resultant;
            var hsv = HsvToRgb(meanHue, Math.Min(1.0, res.Saturation * 1.6), 1.0);
            res.Average = Color.FromArgb(hsv.R, hsv.G, hsv.B);
        }
        else
        {
            res.Average = AverageFast(bmp, rect);
            res.Dispersion = 0;
        }
        res.Valid = true;
        return res;
    }

    /// <summary>
    /// Fraction of pixels inside the rectangle whose hue falls in [hueMin..hueMax] (degrees),
    /// with a minimum saturation/value. Used to check "red AND yellow present" for rivalry cards.
    /// </summary>
    public static double HueBandFraction(Bitmap bmp, System.Drawing.Rectangle rect, double hueMin, double hueMax, double minSat, double minVal)
    {
        var r = new System.Drawing.Rectangle(
            Math.Max(0, rect.X), Math.Max(0, rect.Y),
            Math.Min(rect.Width, bmp.Width - rect.X), Math.Min(rect.Height, bmp.Height - rect.Y));
        if (r.Width <= 0 || r.Height <= 0) return 0;

        int hits = 0, total = 0;
        for (int y = r.Top; y < r.Top + r.Height; y++)
            for (int x = r.Left; x < r.Left + r.Width; x++)
            {
                total++;
                var c = bmp.GetPixel(x, y);
                double h, s, v;
                RgbToHsv(c.R, c.G, c.B, out h, out s, out v);
                if (s < minSat || v < minVal) continue;
                bool inBand = hueMin <= hueMax
                    ? (h >= hueMin && h <= hueMax)
                    : (h >= hueMin || h <= hueMax);   // wraps around 0
                if (inBand) hits++;
            }
        return total == 0 ? 0 : (double)hits / total;
    }

    /// <summary>Dominant colour restricted to a hue band (used for red/yellow rivalry cards).</summary>
    public static Color DominantInHueBand(Bitmap bmp, System.Drawing.Rectangle rect, double hueMin, double hueMax, double minSat, double minVal)
    {
        var r = new System.Drawing.Rectangle(
            Math.Max(0, rect.X), Math.Max(0, rect.Y),
            Math.Min(rect.Width, bmp.Width - rect.X), Math.Min(rect.Height, bmp.Height - rect.Y));
        var counts = new Dictionary<int, int>();
        var sums = new Dictionary<int, long[]>();
        for (int y = r.Top; y < r.Top + r.Height; y++)
            for (int x = r.Left; x < r.Left + r.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                double h, s, v;
                RgbToHsv(c.R, c.G, c.B, out h, out s, out v);
                if (s < minSat || v < minVal) continue;
                bool inBand = hueMin <= hueMax ? (h >= hueMin && h <= hueMax) : (h >= hueMin || h <= hueMax);
                if (!inBand) continue;
                int key = ((c.R >> 4) << 8) | ((c.G >> 4) << 4) | (c.B >> 4);
                counts.TryGetValue(key, out int n); counts[key] = n + 1;
                if (!sums.TryGetValue(key, out var arr)) { arr = new long[3]; sums[key] = arr; }
                arr[0] += c.R; arr[1] += c.G; arr[2] += c.B;
            }
        if (counts.Count == 0) return Color.Black;
        int bestKey = counts.Keys.First();
        foreach (var kv in counts) if (kv.Value > counts[bestKey]) bestKey = kv.Key;
        var s2 = sums[bestKey];
        return Color.FromArgb((int)(s2[0] / counts[bestKey]), (int)(s2[1] / counts[bestKey]), (int)(s2[2] / counts[bestKey]));
    }

    /// <summary>Most common (quantised) colour inside the rectangle - used as a secondary signal.</summary>
    public static Color DominantColor(Bitmap bmp, System.Drawing.Rectangle rect, bool skipDark = false)
    {
        var r = new System.Drawing.Rectangle(
            Math.Max(0, rect.X), Math.Max(0, rect.Y),
            Math.Min(rect.Width, bmp.Width - rect.X), Math.Min(rect.Height, bmp.Height - rect.Y));
        if (r.Width <= 0 || r.Height <= 0) return Color.Black;

        var counts = new Dictionary<int, int>();
        var sums = new Dictionary<int, long[]>();
        for (int y = r.Top; y < r.Top + r.Height; y++)
            for (int x = r.Left; x < r.Left + r.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (skipDark)
                {
                    int mx = Math.Max(c.R, Math.Max(c.G, c.B));
                    if (mx < 28) continue;          // ignore near-black pixels
                }
                int key = ((c.R >> 4) << 8) | ((c.G >> 4) << 4) | (c.B >> 4);
                counts.TryGetValue(key, out int n);
                counts[key] = n + 1;
                if (!sums.TryGetValue(key, out var s)) { s = new long[3]; sums[key] = s; }
                s[0] += c.R; s[1] += c.G; s[2] += c.B;
            }
        if (counts.Count == 0) return Color.Black;
        int bestKey = counts.Keys.First();
        foreach (var kv in counts) if (kv.Value > counts[bestKey]) bestKey = kv.Key;
        var sum = sums[bestKey];
        return Color.FromArgb((int)(sum[0] / counts[bestKey]), (int)(sum[1] / counts[bestKey]), (int)(sum[2] / counts[bestKey]));
    }

    public static void RgbToHsv(int r, int g, int b, out double h, out double s, out double v)
    {
        double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
        double max = Math.Max(rd, Math.Max(gd, bd));
        double min = Math.Min(rd, Math.Min(gd, bd));
        double d = max - min;
        if (d == 0) h = 0; else if (max == rd) h = 60 * (((gd - bd) / d) % 6);
        else if (max == gd) h = 60 * (((bd - rd) / d) + 2);
        else h = 60 * (((rd - gd) / d) + 4);
        if (h < 0) h += 360;
        s = max == 0 ? 0 : d / max;
        v = max;
    }

    public static Color HsvToRgb(double h, double s, double v)
    {
        int r = 0, g = 0, b = 0;
        double c = v * s;
        double x = c * (1 - Math.Abs(((h / 60.0) % 2) - 1));
        double m = v - c;
        if (h < 60) { r = (int)Math.Round(c * 255); g = (int)Math.Round(x * 255); b = 0; }
        else if (h < 120) { r = (int)Math.Round(x * 255); g = (int)Math.Round(c * 255); b = 0; }
        else if (h < 180) { r = 0; g = (int)Math.Round(c * 255); b = (int)Math.Round(x * 255); }
        else if (h < 240) { r = 0; g = (int)Math.Round(x * 255); b = (int)Math.Round(c * 255); }
        else if (h < 300) { r = (int)Math.Round(x * 255); g = 0; b = (int)Math.Round(c * 255); }
        else { r = (int)Math.Round(c * 255); g = 0; b = (int)Math.Round(x * 255); }
        return Color.FromArgb(
            Math.Clamp((int)(r + m * 255), 0, 255),
            Math.Clamp((int)(g + m * 255), 0, 255),
            Math.Clamp((int)(b + m * 255), 0, 255));
    }
}
