using System;
using System.Drawing;
using System.Drawing.Imaging;

namespace HockeyRoller.Services;

/// <summary>
/// Detects the "POSITION FILLED" / "ALREADY ON TEAM" ribbon: a black band angled
/// slightly across the card with white text on it.
/// </summary>
public static class DiagonalStripDetector
{
    const int DARK_MAX = 28;      // a pixel counts as "banner black"
    const int LIGHT_MIN = 190;    // a pixel counts as "text white"

    static bool IsDark(Color c) => Math.Max(c.R, Math.Max(c.G, c.B)) <= DARK_MAX;
    static bool IsLight(Color c) => Math.Min(c.R, Math.Min(c.G, c.B)) >= LIGHT_MIN;

    /// <summary>
    /// True when the card carries the ribbon. <paramref name="score"/> is the number of
    /// "white text inside a thick black band" pixels that were found.
    /// </summary>
    public static bool HasBanner(Bitmap card, out double score)
    {
        score = 0;
        try
        {
            if (card == null || card.Width < 20 || card.Height < 20) return false;

            int w = card.Width, h = card.Height;
            int dark = 0, whiteOnDark = 0;
            int minX = int.MaxValue, maxX = -1;

            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var c = card.GetPixel(x, y);
                    if (IsDark(c)) { dark++; continue; }
                    if (!IsLight(c)) continue;

                    // is the text sitting on a black band?
                    int darkNeighbours = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                            if (IsDark(card.GetPixel(nx, ny))) darkNeighbours++;
                        }
                    if (darkNeighbours < 3) continue;

                    // the ribbon is a thick band: there must be black well above and below
                    int above = 0, below = 0;
                    for (int d = 3; d <= 14; d++)
                    {
                        if (y - d >= 0 && IsDark(card.GetPixel(x, y - d))) above++;
                        if (y + d < h && IsDark(card.GetPixel(x, y + d))) below++;
                    }
                    if (above < 4 || below < 4) continue;

                    whiteOnDark++;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                }

            score = whiteOnDark;
            if (whiteOnDark == 0) return false;

            double darkRatio = (double)dark / (w * h);
            double span = maxX > minX ? (maxX - minX) / (double)w : 0;
            // the ribbon text has to be a decent amount of pixels for the card size
            double minPixels = Math.Max(35, w * 0.12);
            // a real ribbon: enough text pixels, a real black band, and the text spans
            // a good part of the card
            return whiteOnDark >= minPixels && darkRatio >= 0.05 && span >= 0.32;
        }
        catch { return false; }
    }

    /// <summary>Kept for the old call sites - returns true when a ribbon was found.</summary>
    public static bool HasDiagonalStrip(Bitmap card, out double score)
    {
        return HasBanner(card, out score);
    }
}
