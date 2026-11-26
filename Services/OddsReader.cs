using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Threading.Tasks;

namespace HockeyRoller.Services;

public class OddsLine
{
    public string Text { get; set; } = "";
    public List<string> Tiers { get; set; } = new();
    public Rectangle Rect { get; set; }
}

/// <summary>
/// Reads the odds list out of the odds panel.
/// The panel text is small, coloured and partly unreadable for a whole-image OCR
/// (dark red on dark navy fails), so each text line is isolated, thresholded against
/// the panel background and then OCR'd on its own at 4x - that reads every tier.
/// </summary>
public static class OddsReader
{
    public static Color BackgroundOf(Bitmap bmp)
    {
        return BackgroundOf(bmp, 0, bmp.Height);
    }

    /// <summary>Most common colour in a horizontal band (used to sample the list area only).</summary>
    public static Color BackgroundOf(Bitmap bmp, int fromY, int toY)
    {
        var counts = new Dictionary<int, int>();
        fromY = Math.Max(0, fromY); toY = Math.Min(bmp.Height, toY);
        for (int y = fromY; y < toY; y += 2)
            for (int x = 0; x < bmp.Width; x += 2)
            {
                var c = bmp.GetPixel(x, y);
                int key = (c.R >> 4 << 8) | (c.G >> 4 << 4) | (c.B >> 4);
                counts.TryGetValue(key, out int n); counts[key] = n + 1;
            }
        if (counts.Count == 0) return Color.Black;
        var k = counts.OrderByDescending(e => e.Value).First().Key;
        return Color.FromArgb(((k >> 8) & 0xF) * 17, ((k >> 4) & 0xF) * 17, (k & 0xF) * 17);
    }

    /// <summary>
    /// Finds the text lines of a region. Rows that are not mostly the panel background
    /// (e.g. the coloured gradient banner at the top) are ignored, otherwise everything
    /// would merge into one giant line.
    /// </summary>
    public static List<(int top, int bot)> FindLines(Bitmap shot, Color bg)
    {
        // ignore the outer columns - the panel border/edges leave a permanent artifact
        // on every row which would merge all the lines into one
        int margin = Math.Min(10, Math.Max(2, shot.Width / 40));
        int x0 = margin, x1 = shot.Width - margin;
        int inner = Math.Max(1, x1 - x0);
        int minInk = Math.Max(6, inner / 30);

        var textRows = new bool[shot.Height];
        for (int y = 0; y < shot.Height; y++)
        {
            int ink = 0, background = 0;
            for (int x = x0; x < x1; x++)
            {
                var c = shot.GetPixel(x, y);
                if (Math.Abs(c.R - bg.R) + Math.Abs(c.G - bg.G) + Math.Abs(c.B - bg.B) > 90) ink++;
                else background++;
            }
            if (background < inner * 0.45) continue;      // gradient / banner row
            if (ink >= minInk) textRows[y] = true;
        }

        var lines = new List<(int top, int bot)>();
        int start = -1, last = -1;
        for (int y = 0; y < shot.Height; y++)
        {
            if (textRows[y]) { if (start < 0) start = y; last = y; }
            else if (start >= 0 && y - last > 4) { lines.Add((start, last)); start = -1; }
        }
        if (start >= 0) lines.Add((start, last));
        return lines;
    }

    /// <summary>
    /// Reads one region of the odds panel and returns the lines it contains.
    /// </summary>
    public static async Task<List<OddsLine>> ReadRegionAsync(Bitmap region, int minLeftX, int minRightX,
                                                               Func<string, List<string>> tierResolver)
    {
        var result = new List<OddsLine>();
        if (region.Width < 40 || region.Height < 20) return result;

        var bg = BackgroundOf(region, (int)(region.Height * 0.35), region.Height);
        var lines = FindLines(region, bg);

        foreach (var (top, bot) in lines)
        {
            int h = bot - top + 1;
            var lineRect = new Rectangle(0, Math.Max(0, top - 2), region.Width, Math.Min(region.Height, h + 4));
            using var crop = new Bitmap(lineRect.Width, lineRect.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(crop))
                g.DrawImage(region, new Rectangle(0, 0, crop.Width, crop.Height), lineRect, GraphicsUnit.Pixel);

            // threshold against the line background
            var lineBg = BackgroundOf(crop);
            int cut = (lineBg.R * 30 + lineBg.G * 59 + lineBg.B * 11) / 100 + 40;
            using var bw = new Bitmap(crop.Width, crop.Height, PixelFormat.Format24bppRgb);
            for (int y = 0; y < crop.Height; y++)
                for (int x = 0; x < crop.Width; x++)
                {
                    var c = crop.GetPixel(x, y);
                    int l = (c.R * 30 + c.G * 59 + c.B * 11) / 100;
                    byte v = (byte)(l > cut ? 255 : 0);
                    bw.SetPixel(x, y, Color.FromArgb(v, v, v));
                }

            using var big = new Bitmap(bw.Width * 4, bw.Height * 4, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(big))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(bw, 0, 0, big.Width, big.Height);
            }

            var words = await OcrService.RecognizeWordsAsync(big, false);
            string text = string.Join(" ", words.Select(w => w.Text)).Trim();
            if (text.Length == 0) continue;

            // only keep the part of the line that is in the requested column
            var tiers = tierResolver(text);
            result.Add(new OddsLine { Text = text, Tiers = tiers, Rect = lineRect });
        }
        return result;
    }
}
