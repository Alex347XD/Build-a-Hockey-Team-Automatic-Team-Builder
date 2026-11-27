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
    public double BodyValue { get; set; }        // brightness of the card body
    public double PeakValue { get; set; }        // brightness of the frame colour
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

        var clip = new Rectangle(
            Math.Max(0, win.X), Math.Max(0, win.Y),
            Math.Min(win.Width, bmp.Width - win.X), Math.Min(win.Height, bmp.Height - win.Y));
        if (clip.Width < 6 || clip.Height < 6) return p;

        // Normalise every card to the same size before sampling. The reference images are
        // square while the cards in game are portrait, so without this the sampled frame
        // band would cover different content for each side and nothing would match.
        const int N = 128;
        using var norm = new Bitmap(N, N, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(norm))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(bmp, new Rectangle(0, 0, N, N), clip, GraphicsUnit.Pixel);
        }
        var r = new Rectangle(0, 0, N, N);

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

                var c = norm.GetPixel(x, y);
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
        double pk, ps, pv;
        ColorMatch.RgbToHsv(peak.R, peak.G, peak.B, out pk, out ps, out pv);
        p.PeakValue = pv;
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

        // The card body colour is sampled from the strips just inside the left/right frame,
        // above the name banner. On a real card that area is the tier's background tint
        // (the middle is covered by the player photo and the flag) - and it is the same area
        // on a recreated background, so both compare the same thing.
        p.Body = SampleBodyTint(norm, r, bandX, bandY);
        double bh, bs, bv;
        ColorMatch.RgbToHsv(p.Body.R, p.Body.G, p.Body.B, out bh, out bs, out bv);
        p.BodyHue = bh;
        p.BodySat = bs;
        p.BodyValue = bv;
        return p;
    }

    /// <summary>Dominant colour of the background just inside the frame (left + right strips).</summary>
    public static Color SampleBodyTint(Bitmap bmp, Rectangle r, int bandX, int bandY)
    {
        var counts = new Dictionary<int, int>();
        var sums = new Dictionary<int, long[]>();

        void Add(int x, int y)
        {
            if (x < 0 || y < 0 || x >= bmp.Width || y >= bmp.Height) return;
            var c = bmp.GetPixel(x, y);
            int mx = Math.Max(c.R, Math.Max(c.G, c.B));
            if (mx < 30) return;                       // ignore near-black
            int key = ((c.R >> 4) << 8) | ((c.G >> 4) << 4) | (c.B >> 4);
            counts.TryGetValue(key, out int n); counts[key] = n + 1;
            if (!sums.TryGetValue(key, out var s)) { s = new long[3]; sums[key] = s; }
            s[0] += c.R; s[1] += c.G; s[2] += c.B;
        }

        int y0 = r.Y + (int)(r.Height * 0.22);
        int y1 = r.Y + (int)(r.Height * 0.72);
        int strip = Math.Max(1, bandX / 2);
        for (int y = y0; y < y1; y += 2)
        {
            for (int x = r.X + bandX; x < r.X + bandX + strip; x++) Add(x, y);
            for (int x = r.X + r.Width - bandX - strip; x < r.X + r.Width - bandX; x++) Add(x, y);
        }
        if (counts.Count == 0)
        {
            var inner = new Rectangle(r.X + (int)(r.Width * 0.25), r.Y + (int)(r.Height * 0.25),
                                      Math.Max(1, (int)(r.Width * 0.5)), Math.Max(1, (int)(r.Height * 0.5)));
            return ColorMatch.DominantColor(bmp, inner, skipDark: true);
        }
        int bestKey = counts.Keys.First();
        foreach (var kv in counts) if (kv.Value > counts[bestKey]) bestKey = kv.Key;
        var s2 = sums[bestKey];
        return Color.FromArgb((int)(s2[0] / counts[bestKey]), (int)(s2[1] / counts[bestKey]), (int)(s2[2] / counts[bestKey]));
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
            double bodyValTerm = Math.Abs(BodyValue - other.BodyValue) * 40;
            double bodyRgbTerm = ColorMatch.Distance(Body, other.Body) / 255.0 * 40;
            // a rainbow frame on both sides is a good sign, on one side only it means the
            // card is a shiny variant of the other tier
            double rainbowTerm = aMulti && bMulti ? Math.Abs(RainbowBins - other.RainbowBins) * 5 : 8;
            double satTermRainbow = Math.Abs(Saturation - other.Saturation) * 25;
            return bodyHueTerm + bodySatTerm + bodyValTerm + bodyRgbTerm + rainbowTerm + satTermRainbow;
        }

        double hueTerm = HueDistance(DominantHue, other.DominantHue) * 1.2;
        // Icon (bright violet #7C2ED1) and Franchise (dark purple #331242) share the same
        // hue - only the brightness separates them, so value is compared separately
        double valueTerm = Math.Abs(PeakValue - other.PeakValue) * 50;
        double peakTerm = ColorMatch.Distance(Peak, other.Peak) / 255.0 * 25 + valueTerm;
        double satTerm = Math.Abs(Saturation - other.Saturation) * 40;
        double dispTerm = Math.Abs(Dispersion - other.Dispersion) * 20;

        // The card body is not used for the tier match: on a real card the middle is the
        // player's team banner, so it says nothing about the tier. The frame is the tier.
        return hueTerm + peakTerm + satTerm + dispTerm;
    }
}

