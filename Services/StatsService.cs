using System.Net.Http;
using System.Text;
using System.Text.Json;
using Nighty.Models;

namespace Nighty.Services;

/// <summary>
/// Anonymous usage ping: a random install ID and the app version, once per start. No IP is stored by the
/// server, no machine or account data is sent, and the Settings toggle turns it off completely.
/// Set <see cref="Endpoint"/> to the URL of your deployed stats worker (see stats-worker/README.md).
/// </summary>
public static class StatsService
{
    /// <summary>e.g. "https://nighty-stats.YOURNAME.workers.dev/ping". Empty = disabled.</summary>
    public const string Endpoint = "";

    public static async Task PingAsync(ExtrasConfig cfg)
    {
        try
        {
            if (Endpoint.Length == 0 || !cfg.ShareUsageStats) return;
            if (!Endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
            if (cfg.InstallId.Length == 0) cfg.InstallId = Guid.NewGuid().ToString("D");

            var v = UpdateService.Current;
            string body = JsonSerializer.Serialize(new { id = cfg.InstallId, v = $"{v.Major}.{v.Minor}.{v.Build}" });
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NightyLite-Stats");
            using var resp = await http.PostAsync(Endpoint, new StringContent(body, Encoding.UTF8, "application/json"));
            Log.Info("Stats ping: HTTP " + (int)resp.StatusCode);
        }
        catch (Exception ex) { Log.Warn("Stats ping failed", ex); }
    }
}
