using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace HockeyRoller.Services;

/// <summary>
/// Checks a GitHub repository's latest release and installs a newer version
/// automatically: downloads the HockeyRoller.exe asset, swaps it for the running
/// executable and restarts.
/// </summary>
public static class UpdateService
{
    public const string Repo = "Alex347XD/Build-a-Hockey-Team-Automatic-Team-Builder";
    public const string AssetName = "HockeyRoller.exe";
    public const string ReleasesPage = "https://github.com/" + Repo + "/releases";

    public class UpdateInfo
    {
        public string Tag = "";
        public string CurrentVersion = "";
        public string DownloadUrl = "";
        public string Notes = "";
        public long Size;
        public string Sha256 = "";
    }

    /// <summary>Current version string (1.2.3) from assembly info.</summary>
    public static string CurrentVersion
    {
        get
        {
            var asm = Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrEmpty(info)) return info.Split('+')[0].TrimStart('v', 'V');
            var v = asm.GetName().Version;
            return v == null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>Fetch latest release; return UpdateInfo when a newer version exists, else null.</summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(20);
            http.DefaultRequestHeaders.Add("User-Agent", "HockeyRoller");
            http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");

            string json = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tag = root.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
            string notes = root.TryGetProperty("body", out var b) ? (b.GetString() ?? "") : "";

            // find the HockeyRoller.exe asset
            string url = "", sha = ""; long size = 0;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                {
                    string name = a.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
                    if (!name.Equals(AssetName, StringComparison.OrdinalIgnoreCase)) continue;
                    url = a.TryGetProperty("browser_download_url", out var u) ? (u.GetString() ?? "") : "";
                    size = a.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
                    sha = a.TryGetProperty("digest", out var d) ? (d.GetString() ?? "").Replace("sha256:", "") : "";
                    break;
                }

            if (url.Length == 0) return null;             // no downloadable exe on this release
            if (!IsNewer(tag, CurrentVersion)) return null; // already up to date

            return new UpdateInfo
            {
                Tag = tag,
                CurrentVersion = CurrentVersion,
                DownloadUrl = url,
                Notes = notes,
                Size = size,
                Sha256 = sha,
            };
        }
        catch { return null; }
    }

    /// <summary>True when `candidate` is a higher semantic version than `current`.</summary>
    public static bool IsNewer(string candidate, string current)
    {
        var c = ParseVersion(candidate);
        var cur = ParseVersion(current);
        for (int i = 0; i < 3; i++)
        {
            if (c[i] > cur[i]) return true;
            if (c[i] < cur[i]) return false;
        }
        return false;
    }

    private static int[] ParseVersion(string v)
    {
        v = (v ?? "").Trim().TrimStart('v', 'V');
        // strip pre-release / build suffixes
        int cut = v.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0) v = v.Substring(0, cut);
        var parts = v.Split('.');
        var nums = new int[3];
        for (int i = 0; i < 3 && i < parts.Length; i++)
            int.TryParse(parts[i], out nums[i]);
        return nums;
    }

    /// <summary>
    /// Download the new exe and install it. Returns true when a swap was launched
    /// (the caller should shut down so the updater can replace the file).
    /// </summary>
    public static async Task<bool> DownloadAndInstallAsync(UpdateInfo info)
    {
        try
        {
            string currentExe = Process.GetCurrentProcess().MainModule?.FileName
                                ?? Path.Combine(AppContext.BaseDirectory, AssetName);
            string tmpDir = Path.Combine(Path.GetTempPath(), "HockeyRoller-update");
            Directory.CreateDirectory(tmpDir);
            string newExe = Path.Combine(tmpDir, AssetName + ".new");

            using (var http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromMinutes(10);
                http.DefaultRequestHeaders.Add("User-Agent", "HockeyRoller");
                var bytes = await http.GetByteArrayAsync(info.DownloadUrl);
                await File.WriteAllBytesAsync(newExe, bytes);
            }

            // optional integrity check against the release digest
            if (info.Sha256.Length > 0)
            {
                string actual = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(newExe))).ToLowerInvariant();
                if (!actual.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(newExe); } catch { }
                    return false;   // checksum mismatch - don't install
                }
            }

            // a .bat swapper: wait for us to exit, overwrite, restart
            string bat = Path.Combine(tmpDir, "update.bat");
            string backup = currentExe + ".bak";
            string[] lines =
            {
                "@echo off",
                "setlocal",
                $":wait",
                $"taskkill /F /PID {Environment.ProcessId} >nul 2>&1",
                "timeout /t 2 /nobreak >nul 2>&1",
                $"copy /Y \"{currentExe}\" \"{backup}\" >nul 2>&1",
                $"copy /Y \"{newExe}\" \"{currentExe}\" >nul 2>&1",
                "if errorlevel 1 (",
                $"  copy /Y \"{backup}\" \"{currentExe}\" >nul 2>&1",
                ")",
                $"del \"{newExe}\" >nul 2>&1",
                $"del \"{backup}\" >nul 2>&1",
                $"start \"\" \"{currentExe}\"",
                "del \"%~f0\"",
                "endlocal",
            };
            File.WriteAllLines(bat, lines);

            Process.Start(new ProcessStartInfo
            {
                FileName = bat,
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            return true;
        }
        catch { return false; }
    }
}
