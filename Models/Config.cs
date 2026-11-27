using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace HockeyRoller.Models;

public class RarityColor
{
    public string Name { get; set; } = "";
    public int R { get; set; }
    public int G { get; set; }
    public int B { get; set; }
    public bool MultiHue { get; set; }      // prismatic-style rainbow frames
    public bool HasBody { get; set; }        // secondary (card body) reference colour
    public int BodyR { get; set; }
    public int BodyG { get; set; }
    public int BodyB { get; set; }
    public bool Sparkles { get; set; }        // card shows sparkle particles
    public bool SparklesKnown { get; set; }  // sparkle state has been learned
    public bool RequireRedYellow { get; set; } // card must contain both red AND yellow
    public bool HasAlt { get; set; }           // second (alternate) frame colour
    public int AltR { get; set; }
    public int AltG { get; set; }
    public int AltB { get; set; }
    public string[] Patterns { get; set; } = Array.Empty<string>(); // OCR keywords

    public System.Drawing.Color ToColor() => System.Drawing.Color.FromArgb(R, G, B);
    [System.Text.Json.Serialization.JsonIgnore]
    public System.Windows.Media.Color DisplayColor => System.Windows.Media.Color.FromRgb((byte)R, (byte)G, (byte)B);
    public static RarityColor FromColor(string name, System.Drawing.Color c) =>
        new() { Name = name, R = c.R, G = c.G, B = c.B };
}

public class AppConfig
{
    public string WindowTitle { get; set; } = "";
    public IntPtrHwnd Hwnd { get; set; } = new();
    public int RollX { get; set; }
    public int RollY { get; set; }
    // Card region (client coordinates inside the game window)
    public int CardX { get; set; }
    public int CardY { get; set; }
    public int CardW { get; set; }
    public int CardH { get; set; }
    // how many cards are revealed in the card region
    public int CardCount { get; set; } = 1;
    public string CardLayout { get; set; } = "2 x 2 grid";
    // Roll type buttons
    public int RerollX { get; set; }
    public int RerollY { get; set; }
    public int RefreshX { get; set; }
    public int RefreshY { get; set; }
    public int OddsBtnX { get; set; } = -1;
    public int OddsBtnY { get; set; } = -1;
    // point that closes the odds panel (optional)
    public int OddsCloseX { get; set; } = -1;
    public int OddsCloseY { get; set; } = -1;
    // the "THIS TEAM" tab inside the odds panel (shows the refresh odds for the current team)
    public int OddsTeamTabX { get; set; } = -1;
    public int OddsTeamTabY { get; set; } = -1;
    // Odds region (where eligible rarities shown)
    public int OddsX { get; set; }
    public int OddsY { get; set; }
    public int OddsW { get; set; }
    public int OddsH { get; set; }
    // Blocked region (position full / already on team) - optional
    public int BlockedX { get; set; } = -1;
    public int BlockedY { get; set; } = -1;
    public int BlockedW { get; set; }
    public int BlockedH { get; set; }
    public int BlockedR { get; set; } = -1;
    public int BlockedG { get; set; } = -1;
    public int BlockedB { get; set; } = -1;
    public int BlockedThreshold { get; set; } = 50;
    // strategy: 0=smart,1=always refresh,2=always reroll
    public int Strategy { get; set; } = 0;
    // capture method: 0=auto,1=screen,2=printwindow
    public int CaptureMethod { get; set; } = 0;
    // detection: 0=auto (ocr then color), 1=ocr only, 2=color only
    public int DetectMode { get; set; } = 0;
    // treat Rivalry and Rivalry Captain as the same target group
    public bool GroupRivalry { get; set; } = true;
    // only accept cards with at least this OVR (0 = no minimum)
    public int MinOvr { get; set; } = 0;
    // also accept any rarity as long as the OVR is high enough
    public bool AcceptAnyTierByOvr { get; set; } = true;
    // ignore the rarity completely: only the OVR decides what gets taken
    public bool OvrOnly { get; set; } = false;
    // where the reference pixel sits on a card (0..1 of the card width/height)
    // per-card overrides (cards are not perfectly aligned in the grid); -1 = use the
    // global position above
    public int DelayMs { get; set; } = 1200;
    // max time to wait for the game to finish animating (screen stops changing)
    public int SettleMaxMs { get; set; } = 5000;
    // max consecutive refreshes without an eligible card before forcing a reroll (0 = no cap)
    // stop rolling when the "TEAM COMPLETE" screen appears
    public int RefreshCap { get; set; } = 0;
    public bool StopOnTeamComplete { get; set; } = true;
    // max distance for a reference-image match to be accepted
    // how much closer the best tier must be than the runner-up (colour distance)
    // max colour distance for the reference pixel to count as a tier colour
    // the pixel may not differ from the tier colour by more than this in ANY single channel
    public List<RarityColor> Rarities { get; set; } = new();
    public string MinRarity { get; set; } = "";
    public string MaxRarity { get; set; } = "";

    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "HockeyRoller", "config.json");

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath, json);
    }

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                if (cfg.Rarities.Count == 0) cfg.Rarities = RarityCatalog.Defaults();
                else RarityCatalog.MergePatterns(cfg.Rarities);
                return cfg;
            }
        }
        catch { }
        return new AppConfig { Rarities = RarityCatalog.Defaults() };
    }
}

// Json can't serialize IntPtr easily; store as long
public class IntPtrHwnd
{
    public long Value { get; set; }
    public IntPtr ToIntPtr() => new IntPtr(Value);
    public static IntPtrHwnd FromIntPtr(IntPtr p) => new() { Value = p.ToInt64() };
}

/// <summary>Built-in rarity tiers for Build A Hockey Team, lowest to highest.</summary>
public static class RarityCatalog
{
    public record Tier(string Name, int R, int G, int B, bool MultiHue, int BodyR, int BodyG, int BodyB, bool RequireRedYellow, params string[] Patterns)
    {
    }

    // Frame + body colours learned from reference card art. Can be re-learned in-app
    // with "Assign current card color" (one click per tier).
    // RefR/G/B = the colour at the reference pixel for that tier.
    public static readonly Tier[] Tiers = new[]
    {
        new Tier("Bronze",           160,  90,  45, false,  70,  40,  30, false, "bronze"),
        new Tier("Silver",           150, 155, 165, false,  58,  63,  74, false, "silver"),
        new Tier("Gold",             212, 160,  23, false, 107,  74,  32, false, "gold"),
        new Tier("Diamond",            0, 168, 143, false,  20,  90,  75, false, "diamond"),
        new Tier("Icon",             139,  92, 246, false, 110,  60, 190, false, "icon"),
        new Tier("Mythic",           122,  16,  16, false, 130,  30,  30, false, "mythic"),
        new Tier("Franchise",        139,  42, 139, false, 198, 142,  16, false, "franchise"),
        new Tier("Prismatic",        255, 255, 255, true,  245, 245, 245, false, "prismatic"),
        new Tier("Limit Breaker",      27,  58, 143, false,  10,  30,  80, false, "limit breaker", "limitbreaker", "limit-breaker"),
        new Tier("Generational",     230,  70, 150, false, 224,  64, 154, false, "generational"),
        new Tier("Rivalry",          155,  37,  54, false, 198, 142,  16, true, "rivalry"),
        new Tier("Rivalry Captain",  155,  37,  54, false, 198, 142,  16, true, "rivalry captain", "rivalrycaptain", "captain"),
    };

    public static List<RarityColor> Defaults()
    {
        var list = new List<RarityColor>();
        foreach (var t in Tiers)
            list.Add(new RarityColor
            {
                Name = t.Name,
                R = t.R, G = t.G, B = t.B,
                MultiHue = t.MultiHue,
                HasBody = true,
                BodyR = t.BodyR, BodyG = t.BodyG, BodyB = t.BodyB,
                RequireRedYellow = t.RequireRedYellow,
                Patterns = t.Patterns,
            });
        return list;
    }

    /// <summary>Restores OCR patterns for known tiers and appends any missing built-in tiers.</summary>
    public static void MergePatterns(List<RarityColor> list)
    {
        foreach (var tier in Tiers)
        {
            var existing = list.FirstOrDefault(r => string.Equals(r.Name, tier.Name, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                list.Add(new RarityColor
                {
                    Name = tier.Name, R = tier.R, G = tier.G, B = tier.B,
                    MultiHue = tier.MultiHue, Patterns = tier.Patterns
                });
            }
            else if (existing.Patterns.Length == 0)
            {
                existing.Patterns = tier.Patterns;
                existing.MultiHue = tier.MultiHue;
            }
            if (!existing.HasBody)
            {
                existing.HasBody = true;
                existing.BodyR = tier.BodyR; existing.BodyG = tier.BodyG; existing.BodyB = tier.BodyB;
            }
            if (tier.RequireRedYellow) existing.RequireRedYellow = true;
        }
        // keep the canonical tier order
        list.Sort((a, b) =>
        {
            int ia = Array.FindIndex(Tiers, t => string.Equals(t.Name, a.Name, StringComparison.OrdinalIgnoreCase));
            int ib = Array.FindIndex(Tiers, t => string.Equals(t.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            if (ia < 0) ia = int.MaxValue;
            if (ib < 0) ib = int.MaxValue;
            return ia.CompareTo(ib);
        });
    }

    /// <summary>Shiny adds +2 OVR; no card is at 100; 116 is the highest OVR.</summary>
    public const int OvrShinyBonus = 2;

    /// <summary>
    /// The tier, decided by ONE thing only: the card's OVR number.
    /// Rivalry Captain 116 | Generational 112-115 | Rivalry 112 + vs | Limit Breaker 108-111 |
    /// Prismatic 105-107 | Franchise 101-104 | Mythic 99 | Icon 95-98 | Diamond 90-94 |
    /// Gold 83-89 | Silver 75-82 | Bronze 70-74.  A shiny card adds +2 OVR first.
    /// </summary>
    public static string? TierFromOvr(int ovr, bool shiny, bool vsSymbol)
    {
        if (ovr <= 0) return null;
        int o = shiny ? ovr - OvrShinyBonus : ovr;
        if (o < 0) o = 0;

        if (o >= 116) return "Rivalry Captain";              // 116 is the highest; every 116 is a Rivalry Captain
        if (o == 112 && vsSymbol) return "Rivalry";           // 112 + vs symbol (bottom right)
        if (o >= 112 && o <= 115) return "Generational";      // 112-115 (112 without a vs symbol is Generational)
        if (o >= 108 && o <= 111) return "Limit Breaker";     // 108-111
        if (o >= 105 && o <= 107) return "Prismatic";         // 105-107
        if (o >= 101 && o <= 104) return "Franchise";         // 101-104
        if (o == 99) return "Mythic";                         // 99   (no card at 100)
        if (o >= 95 && o <= 98) return "Icon";                // 95-98
        if (o >= 90 && o <= 94) return "Diamond";             // 90-94
        if (o >= 83 && o <= 89) return "Gold";                // 83-89
        if (o >= 75 && o <= 82) return "Silver";              // 75-82
        if (o >= 70 && o <= 74) return "Bronze";              // 70-74
        return null;
    }

    /// <summary>Prismatic tier and everything above it (those cards can never be shiny).</summary>
    public static readonly string[] PrismaticAndAbove =
    { "Prismatic", "Limit Breaker", "Generational", "Rivalry", "Rivalry Captain" };

    /// <summary>Canonical order index of a tier name (-1 when unknown).</summary>
    public static int TierOrder(string? name)
    {
        if (string.IsNullOrEmpty(name)) return -1;
        for (int i = 0; i < Tiers.Length; i++)
            if (string.Equals(Tiers[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>Tiers that are visually near-identical and are treated as one target group.</summary>
    public static readonly string[] RivalryGroup = { "Rivalry", "Rivalry Captain" };

    /// <summary>
    /// Group index of a tier. Rivalry and Rivalry Captain share one group so that a target
    /// range covering either also covers the other.
    /// </summary>
    public static int GroupIndex(List<RarityColor> list, string? name, bool groupRivalry = true)
    {
        if (string.IsNullOrEmpty(name)) return -1;
        int idx = -1;
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i].Name, name, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
        if (idx < 0) return -1;
        if (!groupRivalry) return idx;
        bool inGroup = RivalryGroup.Any(g => string.Equals(g, list[idx].Name, StringComparison.OrdinalIgnoreCase));
        if (!inGroup) return idx;
        for (int i = 0; i < list.Count; i++)
            if (RivalryGroup.Any(g => string.Equals(g, list[i].Name, StringComparison.OrdinalIgnoreCase))) return i;
        return idx;
    }

    /// <summary>True when the OCR text mentions this tier (exact or fuzzy match).</summary>
    public static bool TierInText(RarityColor r, string hay)
    {
        if (string.IsNullOrWhiteSpace(hay)) return false;
        hay = hay.ToLowerInvariant();
        foreach (var p in r.Patterns)
        {
            if (string.IsNullOrEmpty(p)) continue;
            if (hay.Contains(p.ToLowerInvariant())) return true;
            if (p.Length >= 4 && ContainsFuzzy(hay, p.ToLowerInvariant())) return true;
        }
        // the word may be a single token like "FRANCHISE:" - compare token by token
        foreach (var token in hay.Split(new[] { ' ', '\n', '\r', '\t', ':', ',', '.', ';', '%', '(', ')' },
                                         StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var p in r.Patterns)
            {
                if (string.IsNullOrEmpty(p)) continue;
                string pp = p.ToLowerInvariant();
                if (token == pp) return true;
                int tol = p.Length >= 7 ? 2 : 1;
                if (p.Length >= 4 && Levenshtein(token, pp) <= tol) return true;
                // "LIMIT BREAKER" may be split into "LIMIT" and "BREAKER:"
                if (token.Length >= 4 && pp.StartsWith(token)) return true;
                if (token.Length >= 4 && token.StartsWith(pp)) return true;
            }
        }
        return false;
    }

    /// <summary>Find a tier name in OCR text. Longest patterns win (Rivalry Captain before Rivalry).</summary>
    public static string? MatchText(string text, IEnumerable<RarityColor> rarities)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var hay = text.ToLowerInvariant();
        string? best = null;
        int bestLen = 0;
        foreach (var r in rarities)
        {
            foreach (var p in r.Patterns)
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (hay.Contains(p.ToLowerInvariant()) && p.Length > bestLen)
                {
                    best = r.Name;
                    bestLen = p.Length;
                }
            }
        }
        if (best != null) return best;

        // fuzzy fallback: OCR often mangles one character ("MVTNIC", "D1AMOND")
        foreach (var r in rarities)
        {
            foreach (var p in r.Patterns)
            {
                if (string.IsNullOrEmpty(p) || p.Length < 4) continue;
                if (ContainsFuzzy(hay, p.ToLowerInvariant())) return r.Name;
            }
        }
        return null;
    }

    private static bool ContainsFuzzy(string hay, string needle)
    {
        int tol = needle.Length >= 7 ? 2 : 1;
        if (hay.Length < needle.Length) return false;
        for (int i = 0; i + needle.Length <= hay.Length; i++)
        {
            if (Levenshtein(hay.Substring(i, needle.Length), needle) <= tol) return true;
        }
        return false;
    }

    private static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
