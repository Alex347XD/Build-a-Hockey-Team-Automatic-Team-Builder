using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HockeyRoller.Services;

/// <summary>
/// A tier learned from a reference card image: the colour profile of the card's frame
/// (hue histogram + peak colour + how spread out the hues are).
/// </summary>
public class TierProfile
{
    public const int Bins = 36;              // 10 degree hue bins
    public const int Grid = 20;               // samples taken across the frame band

    public string Name { get; set; } = "";
    public string SourceFile { get; set; } = "";
    public double[] HueHist { get; set; } = new double[Bins];
    public Color Peak { get; set; } = Color.Black;
    public double Dispersion { get; set; }
    public double Saturation { get; set; }
    public double MeanHue { get; set; }        // saturation weighted mean hue (degrees)
    public int SpreadBins { get; set; }         // how many distinct hues the frame shows
    public int RainbowBins { get; set; }        // distinct hues with real weight (rainbow frames)
    public double DominantHue { get; set; }     // hue of the strongest band
    public double BodyHue { get; set; }         // hue of the card body (decides shiny tiers)
    public double BodySat { get; set; }
    public double[] Hist => HueHist;
    public Color Body { get; set; } = Color.Black;

    /// <summary>
    /// Builds a profile from a window inside a bitmap. The outer band of the window is
    /// sampled (that is the card frame). The same routine is used for reference images
    /// and for captured cards, so the numbers are directly comparable.
    /// </summary>
    public static TierProfile FromWindow(Bitmap bmp, Rectangle win, string name = "", string source = "")
    {
        var p = new TierProfile { Name = name, SourceFile = source };

        var r = new Rectangle(
            Math.Max(0, win.X), Math.Max(0, win.Y),
            Math.Min(win.Width, bmp.Width - win.X), Math.Min(win.Height, bmp.Height - win.Y));
        if (r.Width < 6 || r.Height < 6) return p;

        int bandX = Math.Max(1, (int)(r.Width * 0.18));
        int bandY = Math.Max(1, (int)(r.Height * 0.18));

        double sumX = 0, sumY = 0, wsum = 0, satSum = 0;
        int counted = 0;
        double bestScore = -1;
        Color peak = Color.Black;

        for (int gy = 0; gy < Grid; gy++)
            for (int gx = 0; gx < Grid; gx++)
            {
                // Sample the card's tier-coloured frame only:
                //   - the top border
                //   - the left/right borders of the UPPER half
                // The lower part of the card holds the country flag, the team logo and the
                // position badge - their colours have nothing to do with the tier.
                double fx = (gx + 0.5) / Grid, fy = (gy + 0.5) / Grid;
                int x = r.X + (int)(fx * r.Width);
                int y = r.Y + (int)(fy * r.Height);
                bool upperHalf = y < r.Y + (int)(r.Height * 0.5);
                bool onBand = y < r.Y + bandY ||
                              (upperHalf && (x < r.X + bandX || x >= r.X + r.Width - bandX));
                if (!onBand) continue;

                var c = bmp.GetPixel(x, y);
                double h, s, v;
                ColorMatch.RgbToHsv(c.R, c.G, c.B, out h, out s, out v);
                satSum += s; counted++;
                if (s > 0.15 && v > 0.12)
                {
                    int bin = Math.Min(Bins - 1, (int)(h / 360.0 * Bins));
                    p.HueHist[bin] += s;
                    double rad = h * Math.PI / 180.0;
                    sumX += Math.Cos(rad) * s; sumY += Math.Sin(rad) * s; wsum += s;
                    double score = s * v;
                    if (score > bestScore) { bestScore = score; peak = c; }
                }
            }

        if (counted == 0) return p;
        p.Saturation = satSum / counted;
        p.Peak = peak;
        double total = p.HueHist.Sum();
        if (total > 0) for (int i = 0; i < Bins; i++) p.HueHist[i] /= total;
        double resultant = wsum > 0 ? Math.Sqrt(sumX * sumX + sumY * sumY) / wsum : 1;
        p.Dispersion = 1.0 - resultant;
        p.MeanHue = wsum > 0 ? (Math.Atan2(sumY, sumX) * 180.0 / Math.PI + 360) % 360 : 0;

        // how many clearly different hues does the frame show? 1-2 for a normal card,
        // many for a prismatic rainbow frame
        int spread = 0;
        foreach (var v in p.HueHist) if (v > 0.07) spread++;
        p.SpreadBins = spread;

        int rainbow = 0;
        foreach (var v in p.HueHist) if (v > 0.02) rainbow++;
        p.RainbowBins = rainbow;

        int topBin = 0;
        for (int i = 1; i < Bins; i++) if (p.HueHist[i] > p.HueHist[topBin]) topBin = i;
        p.DominantHue = topBin * 10 + 5;

        var inner = new Rectangle(r.X + (int)(r.Width * 0.25), r.Y + (int)(r.Height * 0.25),
                                  Math.Max(1, (int)(r.Width * 0.5)), Math.Max(1, (int)(r.Height * 0.5)));
        p.Body = ColorMatch.DominantColor(bmp, inner, skipDark: true);
        double bh, bs, bv;
        ColorMatch.RgbToHsv(p.Body.R, p.Body.G, p.Body.B, out bh, out bs, out bv);
        p.BodyHue = bh;
        p.BodySat = bs;
        return p;
    }

    public static double HueDistance(double a, double b)
    {
        double d = Math.Abs(a - b) % 360;
        return d > 180 ? 360 - d : d;
    }

    /// <summary>0 = identical profile, larger = worse match.</summary>
    public double DistanceTo(TierProfile other)
    {
        if (other == null) return double.MaxValue;

        // A prismatic card AND a shiny card both have a rainbow frame, so for those the
        // tier is decided by the CARD BODY colour (prismatic = white body, shiny X keeps
        // the body colour of tier X).
        bool aMulti = RainbowBins >= 8;
        bool bMulti = other.RainbowBins >= 8;

        if (aMulti || bMulti)
        {
            // For a rainbow frame the tier comes from the card body. Comparing the body's
            // HUE (not its brightness) is what separates e.g. a brown (bronze) body from a
            // maroon (mythic) body - their RGB values are nearly identical.
            double bodyHueTerm = HueDistance(BodyHue, other.BodyHue) * 0.9;
            double bodySatTerm = Math.Abs(BodySat - other.BodySat) * 50;
            double bodyRgbTerm = ColorMatch.Distance(Body, other.Body) / 255.0 * 40;
            // a rainbow frame on both sides is a good sign, on one side only it means the
            // card is a shiny variant of the other tier
            double rainbowTerm = aMulti && bMulti ? Math.Abs(RainbowBins - other.RainbowBins) * 5 : 8;
            double satTermRainbow = Math.Abs(Saturation - other.Saturation) * 25;
            return bodyHueTerm + bodySatTerm + bodyRgbTerm + rainbowTerm + satTermRainbow;
        }

        double hueTerm = HueDistance(DominantHue, other.DominantHue) * 1.2;
        double peakTerm = ColorMatch.Distance(Peak, other.Peak) / 255.0 * 60;
        double satTerm = Math.Abs(Saturation - other.Saturation) * 40;
        double dispTerm = Math.Abs(Dispersion - other.Dispersion) * 20;

        return hueTerm + peakTerm + satTerm + dispTerm;
    }
}

public static class ReferenceLibrary
{
    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HockeyRoller", "references");

    private static string Normalize(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char ch in s.ToLowerInvariant())
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        return sb.ToString();
    }

    /// <summary>
    /// Removes the uniform border (screenshots often have a black/transparent margin
    /// around the card). Without this the frame samples are diluted by the margin.
    /// </summary>
    public static Bitmap TrimBackground(Bitmap bmp)
    {
        try
        {
            bool hasAlpha = Image.IsAlphaPixelFormat(bmp.PixelFormat);
            Func<int, int, bool> isBg = (x, y) =>
            {
                var c = bmp.GetPixel(x, y);
                if (hasAlpha && c.A < 40) return true;
                int max = Math.Max(c.R, Math.Max(c.G, c.B));
                int min = Math.Min(c.R, Math.Min(c.G, c.B));
                return max < 45 && (max - min) < 25;      // near-black / near-grey
            };

            int top = 0, bottom = bmp.Height - 1, left = 0, right = bmp.Width - 1;

            bool RowIsBg(int y)
            {
                int bg = 0, total = 0;
                for (int x = left; x <= right; x += 2) { total++; if (isBg(x, y)) bg++; }
                return total > 0 && (double)bg / total > 0.75;
            }
            bool ColIsBg(int x)
            {
                int bg = 0, total = 0;
                for (int y = top; y <= bottom; y += 2) { total++; if (isBg(x, y)) bg++; }
                return total > 0 && (double)bg / total > 0.75;
            }

            while (top < bottom && RowIsBg(top)) top++;
            while (bottom > top && RowIsBg(bottom)) bottom--;
            while (left < right && ColIsBg(left)) left++;
            while (right > left && ColIsBg(right)) right--;

            int w = right - left + 1, h = bottom - top + 1;
            if (w < 10 || h < 10 || (w == bmp.Width && h == bmp.Height)) return bmp;

            var trimmed = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(trimmed))
                g.DrawImage(bmp, new Rectangle(0, 0, w, h), new Rectangle(left, top, w, h), GraphicsUnit.Pixel);
            return trimmed;
        }
        catch { return bmp; }
    }

    public static List<TierProfile> Load(string folder, IEnumerable<string> tierNames)
    {
        var result = new List<TierProfile>();
        if (!Directory.Exists(folder)) return result;

        var files = Directory.GetFiles(folder)
            .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var tier in tierNames)
        {
            string want = Normalize(tier);
            var file = files.FirstOrDefault(f => Normalize(Path.GetFileNameWithoutExtension(f)) == want);
            if (file == null) continue;
            try
            {
                using var raw = new Bitmap(file);
                using var bmp = TrimBackground(raw);
                var prof = TierProfile.FromWindow(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height), tier, Path.GetFileName(file));
                result.Add(prof);
            }
            catch { }
        }
        return result;
    }
}

public class CardHit
{
    public Rectangle Rect { get; set; }        // inside the searched region
    public string Tier { get; set; } = "";
    public double Distance { get; set; }
    public double Margin { get; set; }         // gap to the next best (different) tier
    public bool Rainbow { get; set; }          // rainbow (prismatic or shiny) frame
}

public static class CardFinder
{
    /// <summary>
    /// Searches a region for cards using the reference profiles.
    /// The region is split into the expected card cells (2x2 grid etc.) and inside each
    /// cell a sub-window of several sizes/positions is tested, so the card is found even
    /// when it is much smaller than the cell or not perfectly aligned.
    /// </summary>
    public static List<CardHit> Find(Bitmap region, List<TierProfile> profiles, int cardCount,
                                      string layout, double maxDistance = 45, int maxCards = 4)
    {
        var hits = new List<CardHit>();
        if (profiles == null || profiles.Count == 0 || region.Width < 20 || region.Height < 20)
            return hits;

        // the cells the cards sit in (coordinates are relative to the region)
        var cells = new List<Rectangle>();
        switch (layout)
        {
            case "Stacked (vertical)":
                {
                    int h = region.Height / Math.Max(1, cardCount);
                    for (int i = 0; i < cardCount; i++) cells.Add(new Rectangle(0, i * h, region.Width, h));
                    break;
                }
            case "Side by side":
                {
                    int w = region.Width / Math.Max(1, cardCount);
                    for (int i = 0; i < cardCount; i++) cells.Add(new Rectangle(i * w, 0, w, region.Height));
                    break;
                }
            default: // 2 x 2 grid
                {
                    int w = region.Width / 2, h = region.Height / 2;
                    cells.Add(new Rectangle(0, 0, w, h));
                    cells.Add(new Rectangle(w, 0, w, h));
                    cells.Add(new Rectangle(0, h, w, h));
                    cells.Add(new Rectangle(w, h, w, h));
                    break;
                }
        }

        double[] sizeScales = { 1.0, 0.85, 0.7, 0.55, 0.42 };
        double[] posScales = { 0.0, 0.15, 0.3, 0.45 };

        foreach (var cell in cells)
        {
            CardHit? best = null;
            double bestScore = double.MaxValue;
            string bestTier = "";
            double bestDist = 0;
            double secondDist = double.MaxValue;
            bool bestRainbow = false;

            foreach (double ss in sizeScales)
            {
                int w = (int)(cell.Width * ss), h = (int)(cell.Height * ss);
                if (w < 12 || h < 12) continue;
                foreach (double px in posScales)
                    foreach (double py in posScales)
                    {
                        int x = cell.Left + (int)((cell.Width - w) * px);
                        int y = cell.Top + (int)((cell.Height - h) * py);
                        var win = new Rectangle(x, y, w, h);
                        var prof = TierProfile.FromWindow(region, win);
                        if (prof.Peak.R + prof.Peak.G + prof.Peak.B == 0) continue;

                        var ranked = profiles.Select(p => (p.Name, d: prof.DistanceTo(p))).OrderBy(t => t.d).ToList();
                        if (ranked.Count == 0) continue;
                        double d = ranked[0].d;
                        // prefer a window that covers the whole card - a small window that
                        // happens to match (e.g. the grey face of a silver card) is worse
                        double score = d + (1 - ss) * 35;

                        if (score < bestScore)
                        {
                            bestScore = score;
                            bestTier = ranked[0].Name;
                            bestDist = d;
                            bestRainbow = prof.RainbowBins >= 8;
                            secondDist = ranked.Count > 1 ? ranked[1].d : double.MaxValue;
                            best = new CardHit { Rect = win, Tier = bestTier, Distance = d, Rainbow = bestRainbow };
                        }
                    }
            }
            if (best != null && best.Distance <= maxDistance)
            {
                best.Margin = secondDist == double.MaxValue ? 999 : secondDist - bestDist;
                hits.Add(best);
            }
        }
        return hits;
    }
}
