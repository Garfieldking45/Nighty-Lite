using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Nighty.Services;

/// <summary>
/// Checks the GitHub releases of this project for a newer version and can replace the running exe with it.
/// Nothing is installed without the user pressing the update button. Downloads are HTTPS only, the size is
/// checked against the release asset, and the SHA-256 is checked whenever GitHub provides one.
/// </summary>
public sealed class UpdateService
{
    private const string Repo = "Garfieldking45/Nighty-Lite";
    private const string AssetName = "NightyLite.exe";

    public sealed record Release(Version Version, string Tag, string Url, long Size, string? Sha256, string PageUrl);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("NightyLite-Updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public static Version Current
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    public Release? Available { get; private set; }

    /// <summary>Returns the newest release if it is newer than the running version, otherwise null. Never throws.</summary>
    public async Task<Release?> CheckAsync()
    {
        try
        {
            using var resp = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
            if (!resp.IsSuccessStatusCode) { Log.Info("Update check: HTTP " + (int)resp.StatusCode); return null; }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
            if (root.TryGetProperty("prerelease", out var p) && p.GetBoolean()) return null;

            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var ver)) { Log.Warn("Update check: bad tag " + tag); return null; }
            if (ver <= Current) { Log.Info($"Update check: up to date ({Current})"); return null; }

            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                if (!string.Equals(a.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
                string url = a.GetProperty("browser_download_url").GetString() ?? "";
                if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;
                string? sha = null;
                if (a.TryGetProperty("digest", out var dg) && dg.GetString() is { } s && s.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    sha = s[7..];
                Available = new Release(ver, tag, url, a.GetProperty("size").GetInt64(), sha, root.GetProperty("html_url").GetString() ?? "");
                Log.Info($"Update available: {tag}");
                return Available;
            }
            Log.Warn($"Update check: release {tag} has no {AssetName}");
        }
        catch (Exception ex) { Log.Warn("Update check failed", ex); }
        return null;
    }

    /// <summary>Downloads the release, then starts a helper that swaps the exe once this process has exited. Returns an error message, or null when the caller should shut the app down.</summary>
    public async Task<string?> InstallAsync(Release rel, IProgress<double>? progress = null)
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe is null || !exe.EndsWith(AssetName, StringComparison.OrdinalIgnoreCase))
                return "Auto-update only works from the released NightyLite.exe. Download the new version from GitHub.";

            string dir = Path.GetDirectoryName(exe)!;
            string tmp = Path.Combine(dir, "NightyLite.update.exe");
            try { using (File.Create(tmp)) { } }
            catch { tmp = Path.Combine(Path.GetTempPath(), "NightyLite.update.exe"); }

            using (var resp = await Http.GetAsync(rel.Url, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? rel.Size;
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = File.Create(tmp);
                var buf = new byte[81920];
                long read = 0; int n;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n));
                    read += n;
                    if (total > 0) progress?.Report((double)read / total);
                }
            }

            if (new FileInfo(tmp).Length != rel.Size) { File.Delete(tmp); return "Download was incomplete. Try again."; }
            if (rel.Sha256 is not null)
            {
                await using var fs = File.OpenRead(tmp);
                string hash = Convert.ToHexString(await SHA256.HashDataAsync(fs));
                if (!hash.Equals(rel.Sha256, StringComparison.OrdinalIgnoreCase)) { fs.Close(); File.Delete(tmp); return "Download failed the checksum test. Update cancelled."; }
            }

            // Helper: wait for this process to exit, swap the file (retrying while it is locked), restart.
            string script = Path.Combine(Path.GetTempPath(), "nighty-lite-update.ps1");
            File.WriteAllText(script,
                "param([int]$Pid_, [string]$Src, [string]$Dst)\r\n" +
                "try { Wait-Process -Id $Pid_ -Timeout 30 -ErrorAction SilentlyContinue } catch {}\r\n" +
                "for ($i = 0; $i -lt 40; $i++) {\r\n" +
                "  try { Move-Item -LiteralPath $Src -Destination $Dst -Force -ErrorAction Stop; break } catch { Start-Sleep -Milliseconds 500 }\r\n" +
                "}\r\n" +
                "Start-Process -FilePath $Dst\r\n");

            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" {Environment.ProcessId} \"{tmp}\" \"{exe}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            Log.Info($"Update {rel.Tag} downloaded; restarting");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("Update install failed", ex);
            return "Update failed: " + ex.Message;
        }
    }
}
