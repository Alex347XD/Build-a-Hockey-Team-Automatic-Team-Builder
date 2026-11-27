using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace HockeyRoller.Services;

public static class OcrService
{
    private static OcrEngine? _engine;

    public static bool Available
    {
        get
        {
            try
            {
                if (_engine != null) return true;
                _engine = CreateEngine();
                return _engine != null;
            }
            catch { return false; }
        }
    }

    private static OcrEngine? CreateEngine()
    {
        try
        {
            var e = OcrEngine.TryCreateFromUserProfileLanguages();
            if (e != null) return e;
        }
        catch { }
        try
        {
            var lang = new Windows.Globalization.Language("en-US");
            return OcrEngine.TryCreateFromLanguage(lang);
        }
        catch { return null; }
    }

    public class WordInfo
    {
        public string Text { get; set; } = "";
        public Rectangle Rect { get; set; }   // in the coordinate space of the bitmap that was OCR'd
    }

    /// <summary>
    /// Recognises text and returns every word with its bounding box. Used to find the
    /// rarity names in the odds panel and read the colour they are drawn in.
    /// </summary>
    public static string LastError { get; private set; } = "";

    public static async Task<List<WordInfo>> RecognizeWordsAsync(Bitmap bmp, bool enhance = true)
    {
        var words = new List<WordInfo>();
        LastError = "";
        try
        {
            _engine ??= CreateEngine();
            if (_engine == null) { LastError = "no OCR engine available"; return words; }

            bool owned = false;
            var scaled = enhance ? ScaleForOcr(bmp, out owned) : bmp;
            using var ms = new MemoryStream();
            scaled.Save(ms, ImageFormat.Png);
            ms.Position = 0;

            using var random = new InMemoryRandomAccessStream();
            // NB: do NOT dispose this stream - disposing it closes the RandomAccessStream
            var outStream = random.AsStreamForWrite();
            await outStream.WriteAsync(ms.ToArray(), 0, (int)ms.Length);
            await outStream.FlushAsync();
            random.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(random);
            var sb = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var result = await _engine.RecognizeAsync(sb);
            sb.Dispose();

            double sx = scaled.Width / (double)bmp.Width;
            double sy = scaled.Height / (double)bmp.Height;
            foreach (var line in result.Lines)
                foreach (var w in line.Words)
                {
                    var r = w.BoundingRect;
                    words.Add(new WordInfo
                    {
                        Text = w.Text,
                        Rect = new Rectangle((int)(r.X / sx), (int)(r.Y / sy),
                                             (int)(r.Width / sx), (int)(r.Height / sy))
                    });
                }
            if (owned && enhance) scaled.Dispose();
        }
        catch (Exception ex) { LastError = ex.GetType().Name + ": " + ex.Message; }
        return words;
    }

    /// <summary>Most common colour of a rectangle (used to read the tier colour of a word).</summary>
    public static Color DominantInk(Bitmap bmp, Rectangle rect)
    {
        var r = new Rectangle(
            Math.Max(0, rect.X), Math.Max(0, rect.Y),
            Math.Min(rect.Width, bmp.Width - rect.X), Math.Min(rect.Height, bmp.Height - rect.Y));
        if (r.Width <= 0 || r.Height <= 0) return Color.Black;

        var counts = new Dictionary<int, int>();
        var sums = new Dictionary<int, long[]>();
        for (int y = r.Top; y < r.Bottom; y++)
            for (int x = r.Left; x < r.Right; x++)
            {
                var c = bmp.GetPixel(x, y);
                double h, s, v;
                ColorMatch.RgbToHsv(c.R, c.G, c.B, out h, out s, out v);
                if (s < 0.30 || v < 0.25) continue;          // skip background / grey pixels
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

    private static Bitmap ScaleForOcr(Bitmap bmp, out bool owned)
    {
        owned = false;
        int maxDim = Math.Max(bmp.Width, bmp.Height);
        Bitmap work;

        if (maxDim > 2400)
        {
            double scale = 2400.0 / maxDim;
            work = new Bitmap((int)(bmp.Width * scale), (int)(bmp.Height * scale), PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(work))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(bmp, 0, 0, work.Width, work.Height);
            }
        }
        else if (maxDim < 700)
        {
            double scale = Math.Min(4.0, 1400.0 / Math.Max(1, maxDim));
            work = new Bitmap((int)(bmp.Width * scale), (int)(bmp.Height * scale), PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(work))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(bmp, 0, 0, work.Width, work.Height);
            }
        }
        else
        {
            work = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(work))
                g.DrawImage(bmp, 0, 0, work.Width, work.Height);
        }

        // boost contrast so dim text (grey on dark blue) is readable
        var result = EnhanceContrast(work);
        work.Dispose();
        owned = true;
        return result;
    }

    /// <summary>Greyscale + contrast stretch. Makes low-contrast UI text readable for OCR
    /// without flattening it into a hard threshold (which destroys large numbers).</summary>
    private static Bitmap EnhanceContrast(Bitmap src)
    {
        var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
        var data = src.LockBits(new Rectangle(0, 0, src.Width, src.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var outData = dst.LockBits(new Rectangle(0, 0, dst.Width, dst.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            byte min = 255, max = 0;
            unsafe
            {
                byte* p = (byte*)data.Scan0;
                int stride = data.Stride;
                for (int y = 0; y < src.Height; y++)
                {
                    byte* row = p + y * stride;
                    for (int x = 0; x < src.Width; x++)
                    {
                        int g = (row[x * 3] * 30 + row[x * 3 + 1] * 59 + row[x * 3 + 2] * 11) / 100;
                        if (g < min) min = (byte)g;
                        if (g > max) max = (byte)g;
                    }
                }
                double range = Math.Max(1, max - min);
                byte* o = (byte*)outData.Scan0;
                int oStride = outData.Stride;
                for (int y = 0; y < src.Height; y++)
                {
                    byte* row = p + y * stride;
                    byte* orow = o + y * oStride;
                    for (int x = 0; x < src.Width; x++)
                    {
                        int g = (row[x * 3] * 30 + row[x * 3 + 1] * 59 + row[x * 3 + 2] * 11) / 100;
                        int v = (int)Math.Round((g - min) * 255.0 / range);
                        if (v < 0) v = 0; if (v > 255) v = 255;
                        orow[x * 3] = (byte)v;
                        orow[x * 3 + 1] = (byte)v;
                        orow[x * 3 + 2] = (byte)v;
                    }
                }
            }
        }
        finally { src.UnlockBits(data); dst.UnlockBits(outData); }
        return dst;
    }

    public static async Task<string> RecognizeAsync(Bitmap bmp)
    {
        try
        {
            _engine ??= CreateEngine();
            if (_engine == null) return "";
            if (bmp.Width == 0 || bmp.Height == 0) return "";

            // OCR engines cap at 2600px; scale down if needed
            Bitmap work = ScaleForOcr(bmp, out bool owned);
            using var ms = new MemoryStream();
            work.Save(ms, ImageFormat.Png);
            ms.Position = 0;

            using var random = new InMemoryRandomAccessStream();
            // NB: do NOT dispose this stream - disposing it closes the RandomAccessStream
            var outStream = random.AsStreamForWrite();
            await outStream.WriteAsync(ms.ToArray(), 0, (int)ms.Length);
            await outStream.FlushAsync();
            random.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(random);
            var sb = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var result = await _engine.RecognizeAsync(sb);
            sb.Dispose();
            if (!ReferenceEquals(work, bmp)) work.Dispose();
            return result.Text ?? "";
        }
        catch
        {
            return "";
        }
    }
}
