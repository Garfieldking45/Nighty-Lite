using System.Net.Http;
using System.Text;
using System.Text.Json;
using Nighty.Models;

namespace Nighty.Services;

/// <summary>
/// Anonymous usage ping, once per start. It sends only:
///   - a random install ID and the app version
///   - the Windows generation ("Windows 10" / "Windows 11")
///   - which features were used since the last ping (names only, e.g. "clicker")
///   - how many errors were logged and how many update attempts failed since the last ping
/// No IP is stored by the server, and no names, settings values, hotkeys, games or account data are sent.
/// The Settings toggle turns the whole thing off.
/// </summary>
public static class StatsService
{
    /// <summary>The stats worker (see stats-worker/README.md). Empty = disabled.</summary>
    public const string Endpoint = "https://nighty-stats.gamerethanosvog.workers.dev/ping";

    private static readonly string[] FeatureNames = { "clicker", "crossbow", "autotool", "boost", "crosshair", "cps", "sprint", "afk" };

    // Collected this session (thread safe: the clicker, crossbow and logger run off the UI thread).
    private static int _usedMask;
    private static int _errors;

    /// <summary>Called from the logger so the session's feature use and error count are noticed without touching each service.</summary>
    internal static void NoteLog(string level, string msg)
    {
        if (level == "ERROR") { Interlocked.Increment(ref _errors); return; }
        if (level != "INFO") return;
        int bit = msg.StartsWith("Clicker started", StringComparison.Ordinal) ? 0
                : msg.StartsWith("Crossbow started", StringComparison.Ordinal) ? 1
                : msg.StartsWith("Auto Tool:", StringComparison.Ordinal) ? 2
                : msg.StartsWith("Boost: high-performance", StringComparison.Ordinal) || msg.StartsWith("Boost: Roblox pid", StringComparison.Ordinal) ? 3
                : -1;
        if (bit >= 0) Interlocked.Or(ref _usedMask, 1 << bit);
    }

    /// <summary>Counts a failed update attempt (it is reported with the next ping).</summary>
    public static void NoteUpdateFailed(ExtrasConfig cfg) { cfg.PendingUpdateFails++; Svc.Settings.Save(); }

    /// <summary>Moves this session's counters into the saved settings so they survive a restart. Call on exit.</summary>
    public static void Persist(ExtrasConfig cfg)
    {
        try
        {
            var used = Merge(cfg.PendingFeatures, MaskToNames(_usedMask));
            cfg.PendingFeatures = string.Join(',', used);
            cfg.PendingErrors += Interlocked.Exchange(ref _errors, 0);
            _usedMask = 0;
            Svc.Settings.Save();
        }
        catch { }
    }

    private static IEnumerable<string> MaskToNames(int mask)
    {
        for (int i = 0; i < 4; i++) if ((mask & (1 << i)) != 0) yield return FeatureNames[i];
    }

    private static List<string> Merge(string saved, IEnumerable<string> more) =>
        saved.Split(',', StringSplitOptions.RemoveEmptyEntries).Concat(more).Where(FeatureNames.Contains).Distinct().ToList();

    private static string WindowsName() => Environment.OSVersion.Version.Build >= 22000 ? "Windows 11" : "Windows 10";

    public static async Task PingAsync(AppSettings s)
    {
        try
        {
            var cfg = s.Extras;
            if (Endpoint.Length == 0 || !cfg.ShareUsageStats) return;
            if (!Endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
            if (cfg.InstallId.Length == 0) cfg.InstallId = Guid.NewGuid().ToString("D");

            // Features: what the previous runs used, plus the overlay options that are left on between runs.
            var features = Merge(cfg.PendingFeatures, Array.Empty<string>());
            if (s.Hud.CrosshairEnabled) features.Add("crosshair");
            if (s.Hud.CpsEnabled) features.Add("cps");
            if (cfg.AutoSprint) features.Add("sprint");
            if (cfg.AntiAfk) features.Add("afk");
            int errors = cfg.PendingErrors, fails = cfg.PendingUpdateFails;
            string sentFeatures = cfg.PendingFeatures;

            var v = UpdateService.Current;
            string body = JsonSerializer.Serialize(new
            {
                id = cfg.InstallId,
                v = $"{v.Major}.{v.Minor}.{v.Build}",
                os = WindowsName(),
                f = features.Distinct(),
                err = Math.Min(errors, 1000),
                uf = Math.Min(fails, 100),
            });
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NightyLite-Stats");
            using var resp = await http.PostAsync(Endpoint, new StringContent(body, Encoding.UTF8, "application/json"));
            Log.Info("Stats ping: HTTP " + (int)resp.StatusCode);

            if (resp.IsSuccessStatusCode)
            {
                // Reported, so start counting again. Anything added while the request was in flight is kept.
                cfg.PendingErrors -= errors;
                cfg.PendingUpdateFails -= fails;
                if (cfg.PendingFeatures == sentFeatures) cfg.PendingFeatures = "";
                Svc.Settings.Save();
            }
        }
        catch (Exception ex) { Log.Warn("Stats ping failed", ex); }
    }
}
