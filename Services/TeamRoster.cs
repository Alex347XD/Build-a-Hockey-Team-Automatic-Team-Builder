using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace HockeyRoller.Services;

/// <summary>
/// Per-team rosters, read from the shared Google Sheet (team -> players with OVR).
/// Used to check whether the current team can still give a player you don't already have.
/// </summary>
public class TeamRoster
{
    public const string SheetId = "1cfBNoGj6MUhfI-z0AC8tjbY450ADO0poQnO-N5sEOQs";
    public const string SheetUrl = $"https://docs.google.com/spreadsheets/d/{SheetId}/export?format=csv&gid=0";

    public class Player
    {
        public string Name = "";
        public int Ovr;
        public string Pos = "";
        public int Year;
        public string Tier = "";   // rarity from the sheet
        public string Team = "";
    }

    public Dictionary<string, List<Player>> Teams { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public int Count => Teams.Values.Sum(v => v.Count);

    private static readonly Regex TeamRe =
        new(@"^(?<name>.+?)\s*\((?<n>\d+)(\s*/\s*(?<m>\d+))?\)$", RegexOptions.Compiled);
    private static readonly Regex YearInRe = new(@"\b(1[89]\d{2}|20\d{2})\b", RegexOptions.Compiled);

    public static string Norm(string s) =>
        Regex.Replace((s ?? "").Trim().ToLowerInvariant(), @"[^a-z0-9]", "");

    /// <summary>
    /// The roster data ships EMBEDDED in the exe (nothing to find/download). A fresh copy
    /// is fetched from the shared sheet when online and cached, but the embedded copy is
    /// always the offline fallback.
    /// </summary>
    public static async Task<TeamRoster> LoadAsync()
    {
        var r = new TeamRoster();
        string embedded = ReadEmbedded();
        string csv = "";

        // 1) try to refresh from the shared sheet
        try
        {
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(20);
            string online = await http.GetStringAsync(SheetUrl);
            if (!string.IsNullOrWhiteSpace(online))
            {
                csv = online;
                try { Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!); File.WriteAllText(CachePath, online); } catch { }
            }
        }
        catch { }

        // 2) cached copy
        if (string.IsNullOrWhiteSpace(csv))
            try { if (File.Exists(CachePath)) csv = File.ReadAllText(CachePath); } catch { }

        // 3) embedded copy bundled in the exe
        if (string.IsNullOrWhiteSpace(csv)) csv = embedded;

        r.Parse(string.IsNullOrWhiteSpace(csv) ? embedded : csv);
        return r;
    }

    /// <summary>The embedded roster CSV bundled into the exe.</summary>
    public static string ReadEmbedded()
    {
        try
        {
            var asm = typeof(TeamRoster).Assembly;
            string name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("teams.csv", StringComparison.OrdinalIgnoreCase)) ?? "";
            if (name.Length == 0) return "";
            using var s = asm.GetManifestResourceStream(name);
            if (s == null) return "";
            using var sr = new StreamReader(s);
            return sr.ReadToEnd();
        }
        catch { return ""; }
    }

    public static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HockeyRoller", "teams.csv");

    private static readonly HashSet<string> Positions = new(StringComparer.OrdinalIgnoreCase)
    { "LW", "C", "RW", "D", "G", "RW?", "LW?", "C?", "D?", "G?" };

    // tiers listed across the top row of the sheet
    private static readonly string[] TierNames =
    { "Franchise", "Mythic", "Icon", "Diamond", "Gold", "Silver", "Bronze",
      "Prismatic", "Limit Breaker", "Generational", "Rivalry", "Rivalry Captain" };

    public void Parse(string csv)
    {
        Teams.Clear();
        var rows = Csv(csv);
        string team = "";
        int teamYear = 0;

        // rarity column -> tier, read from the sheet header row
        var tierAt = new Dictionary<int, string>();

        // player slots keyed by column: OVR + POS + tier + year, then the name
        var pending = new Dictionary<int, (int Ovr, string Pos, string Tier, int Year)>();

        foreach (var row in rows)
        {
            // a team header resets the current team (and records the year); it can sit
            // in any column (the sheets put it in column 1, not 0)
            {
                Match? m = null;
                foreach (var cell in row)
                {
                    var t = cell.Trim();
                    if (t.Length == 0) continue;
                    var mm = TeamRe.Match(t);
                    if (mm.Success) { m = mm; break; }
                }
                if (m != null && m.Success)
                {
                    team = m.Groups["name"].Value.Trim();
                    var ym = YearInRe.Match(team);
                    teamYear = ym.Success && int.TryParse(ym.Value, out int ty) ? ty : 0;
                    pending.Clear();
                    continue;
                }
            }

            // tier header row (Franchise / Mythic / Icon / ...) maps columns to tiers
            foreach (var tier in TierNames)
                for (int col = 0; col < row.Count; col++)
                    if (row[col].Trim().Equals(tier, StringComparison.OrdinalIgnoreCase))
                        tierAt[col] = tier;

            for (int col = 0; col < row.Count; col++)
            {
                string cell = row[col].Trim();
                if (cell.Length == 0) continue;

                // a name sitting under a pending OVR/POS slot?
                if (pending.TryGetValue(col, out var wait) && wait.Ovr > 0)
                {
                    string name = CleanName(cell);
                    if (IsName(name))
                    {
                        AddPlayer(team, name, wait.Ovr, wait.Pos, wait.Year, wait.Tier);
                        pending.Remove(col);
                    }
                    continue;
                }

                // OVR in this cell, POS in the next non-empty cell, name below; the
                // column says which RARITY it is in on the sheet
                if (int.TryParse(cell, out int ovr) && ovr > 0 && ovr < 200)
                {
                    string pos = "";
                    for (int k = col + 1; k < row.Count; k++)
                    {
                        string nx = row[k].Trim();
                        if (nx.Length == 0) continue;
                        if (Positions.Contains(nx)) pos = nx;
                        break;
                    }
                    string tier = tierAt.TryGetValue(col, out string? t) ? t : "";
                    pending[col] = (ovr, pos, tier, teamYear);
                }
            }
        }
    }

    private static bool IsName(string s)
    {
        if (s.Length < 2) return false;
        if (int.TryParse(s, out _)) return false;
        if (Positions.Contains(s)) return false;
        if (IsHeader(s)) return false;
        if (TeamRe.IsMatch(s)) return false;
        // years, percentages, numbers inside
        if (Regex.IsMatch(s, @"^\d{4}$")) return false;
        return Regex.IsMatch(s, @"[A-Za-z]");
    }
    private static bool IsHeader(string s) =>
        s.Equals("Name", StringComparison.OrdinalIgnoreCase) ||
        s.Equals("Franchise", StringComparison.OrdinalIgnoreCase) ||
        s.Equals("Mythic", StringComparison.OrdinalIgnoreCase);

    private static string CleanName(string s)
    {
        s = s.Trim().Trim('"').Trim();
        return s;
    }

    private void AddPlayer(string team, string name, int ovr, string pos, int year, string tier = "")
    {
        if (team.Length == 0 || name.Length < 2) return;
        if (!Teams.TryGetValue(team, out var list))
        {
            list = new List<Player>();
            Teams[team] = list;
        }
        // de-dupe
        if (list.Any(p => Norm(p.Name) == Norm(name))) return;
        list.Add(new Player { Name = name, Ovr = ovr, Pos = pos, Year = year, Tier = tier, Team = team });
    }

    /// <summary>Players in a team whose name contains/fuzzy-matches; optionally filtered by year.</summary>
    public List<Player> TeamPlayers(string teamName, int year = 0)
    {
        var n = NormTeam(teamName);
        if (n.Length == 0) return new List<Player>();

        string keyOf(string k) => NormTeam(k);

        // exact, then substring, then token-overlap; year from the key or the arg
        foreach (var kv in Teams)
            if (keyOf(kv.Key) == n) return kv.Value;
        foreach (var kv in Teams)
            if (keyOf(kv.Key).Contains(n) || n.Contains(keyOf(kv.Key))) return kv.Value;
        foreach (var kv in Teams)
        {
            string k = keyOf(kv.Key);
            var kt = Tokens(kv.Key); var nt = Tokens(teamName);
            // team word (not year/olympics/team) overlaps and year matches
            var ktTeam = kt.Where(t => t != "olympics" && t != "team" && !(int.TryParse(t, out _)));
            var ntTeam = nt.Where(t => t != "olympics" && t != "team" && !(int.TryParse(t, out _)));
            bool nameOk = ktTeam.Intersect(ntTeam, StringComparer.OrdinalIgnoreCase).Any() ||
                          ntTeam.Any(t => UsaAlias(t) && ktTeam.Any(k => UsaAlias(k))) ||
                          ntTeam.Any(t => UsaAlias(t) && (k.Contains("unitedstates") || ktTeam.Any(k => k == "united" || k == "states")));
            bool yearOk = year <= 0 || kt.Contains(year.ToString()) || k.Contains(year.ToString());
            if (nameOk && yearOk) return kv.Value;
        }
        // fall back: name only
        foreach (var kv in Teams)
            if (keyOf(kv.Key).Contains(n) || n.Contains(keyOf(kv.Key))) return kv.Value;
        return new List<Player>();
    }

    private static bool UsaAlias(string t) =>
        t.Equals("usa", StringComparison.OrdinalIgnoreCase) ||
        t.Equals("unitedstates", StringComparison.OrdinalIgnoreCase) ||
        t.Equals("america", StringComparison.OrdinalIgnoreCase);

    private static List<string> Tokens(string s) =>
        Regex.Split((s ?? "").ToLowerInvariant(), @"[^a-z0-9]+")
            .Where(t => t.Length > 0).ToList();

    /// <summary>Team-name normalisation: USA == United States, drop noise words, keep year.</summary>
    public static string NormTeam(string s)
    {
        var t = Tokens(s);
        // usa <-> united states
        var parts = new List<string>();
        for (int i = 0; i < t.Count; i++)
        {
            if (t[i] == "usa") parts.Add("unitedstates");
            else if (t[i] == "united" && i + 1 < t.Count && t[i + 1] == "states") { parts.Add("unitedstates"); i++; }
            else parts.Add(t[i]);
        }
        return string.Concat(parts);
    }

    /// <summary>
    /// True when every player in this team that matches the target range is already owned
    /// (name normalised), i.e. there is no eligible player left to collect from this team.
    /// </summary>
    public bool AllEligibleOwned(string teamName, ISet<string> owned, Func<Player, bool> eligible)
    {
        var players = TeamPlayers(teamName);
        if (players.Count == 0) return false;      // unknown team - can't say
        var elig = players.Where(eligible).ToList();
        if (elig.Count == 0) return true;          // nothing eligible in this team anyway
        return elig.All(p => owned.Contains(Norm(p.Name)));
    }

    /// <summary>Minimal CSV reader (quotes, embedded commas).</summary>
    public static List<List<string>> Csv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new System.Text.StringBuilder();
        bool quoted = false;

        foreach (char ch in text)
        {
            if (quoted)
            {
                if (ch == '"') { /* end quote handled on next char */ quoted = false; }
                else cell.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (ch == '\n') { row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = new List<string>(); }
            else if (ch != '\r') cell.Append(ch);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }
}
