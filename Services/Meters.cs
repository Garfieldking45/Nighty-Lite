using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Principal;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Nighty.Models;
using Nighty.Native;

namespace Nighty.Services;

/// <summary>
/// Real Roblox frame rate: counts the frames Roblox presents (DXGI Present events over ETW, the way PresentMon does).
/// Needs administrator rights or membership of "Performance Log Users"; without either it says so instead of guessing.
/// </summary>
public sealed class FpsMeter
{
    private const string SessionName = "NightyLiteFps";
    private static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private TraceEventSession? _session;
    private CancellationTokenSource? _cts;
    private volatile HashSet<int> _pids = new();
    private long _frames;

    public double? Fps { get; private set; }
    public string Status { get; private set; } = "Idle";

    public static bool CanTrace
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            var p = new WindowsPrincipal(id);
            return p.IsInRole(WindowsBuiltInRole.Administrator) || p.IsInRole(new SecurityIdentifier("S-1-5-32-559"));
        }
    }

    public void Start()
    {
        if (_cts != null) return;
        if (!CanTrace) { Status = "Needs admin"; return; }
        var cts = _cts = new CancellationTokenSource();
        Status = "Starting";
        _ = Task.Run(RunSession);
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try { await Task.Delay(1000, cts.Token); } catch { break; }
                var ids = new HashSet<int>();
                foreach (var p in Process.GetProcessesByName(RobloxService.ProcessName)) { ids.Add(p.Id); p.Dispose(); }
                _pids = ids;
                long frames = Interlocked.Exchange(ref _frames, 0);
                if (ids.Count == 0) { Fps = null; Status = "Roblox not running"; }
                else if (frames == 0) { Fps = null; Status = "No frames"; }
                else { Fps = frames; Status = "OK"; }
            }
        });
    }

    private void RunSession()
    {
        try
        {
            TraceEventSession.GetActiveSession(SessionName)?.Stop();
            _session = new TraceEventSession(SessionName) { StopOnDispose = true };
            _session.EnableProvider(Dxgi, TraceEventLevel.Informational, ulong.MaxValue);
            _session.Source.Dynamic.All += (TraceEvent e) =>
            {
                if (e.ProviderGuid == Dxgi && (int)e.ID == 42 && _pids.Contains(e.ProcessID)) Interlocked.Increment(ref _frames);
            };
            _session.Source.Process();
        }
        catch (Exception ex)
        {
            Log.Error("FPS tracing failed", ex);
            Status = "Unavailable";
            Fps = null;
        }
    }

    /// <summary>Always called when the overlay is switched off and on quit, so no tracing session is left running.</summary>
    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        try { _session?.Dispose(); } catch { }
        _session = null;
        Fps = null;
        Status = "Idle";
    }
}

/// <summary>Round-trip time to a host once a second, with jitter and packet loss over the last 20 pings.</summary>
public sealed class PingMeter
{
    private CancellationTokenSource? _cts;
    private readonly Queue<int> _recent = new();   // -1 = lost
    private readonly object _gate = new();

    public (int? Ms, int Jitter, int LossPct) Read()
    {
        lock (_gate)
        {
            var ok = _recent.Where(x => x >= 0).ToArray();
            int? last = _recent.Count > 0 && _recent.Last() >= 0 ? _recent.Last() : null;
            int jitter = ok.Length > 1 ? (int)Math.Round(ok.Zip(ok.Skip(1), (a, b) => Math.Abs(a - b)).Average()) : 0;
            int loss = _recent.Count == 0 ? 0 : (int)Math.Round(100.0 * _recent.Count(x => x < 0) / _recent.Count);
            return (last, jitter, loss);
        }
    }

    public void Start(Func<string> host)
    {
        if (_cts != null) return;
        var cts = _cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            using var ping = new Ping();
            while (!cts.IsCancellationRequested)
            {
                int ms = -1;
                try { var r = await ping.SendPingAsync(host(), 1000); if (r.Status == IPStatus.Success) ms = (int)r.RoundtripTime; }
                catch { }
                lock (_gate) { _recent.Enqueue(ms); while (_recent.Count > 20) _recent.Dequeue(); }
                try { await Task.Delay(1000, cts.Token); } catch { break; }
            }
        });
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        lock (_gate) _recent.Clear();
    }
}

/// <summary>Auto Sprint and Anti-AFK: they only press your own keys, and only while Roblox is in front. Held keys are always released.</summary>
public sealed class ExtrasService
{
    private const int Shift = 0xA0, AfkKey = 0x7C;   // left Shift; F13 (a key no game binds)
    private readonly ExtrasConfig _cfg;
    private readonly System.Windows.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private bool _shiftHeld;
    private long _nextAfk;

    public ExtrasService(ExtrasConfig cfg) => _cfg = cfg;

    public void Start()
    {
        _nextAfk = Environment.TickCount64 + 4 * 60_000;
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public void ReleaseKeys()
    {
        if (!_shiftHeld) return;
        _shiftHeld = false;
        InputSender.KeyScan(Shift, false);
    }

    public void Shutdown() { _timer.Stop(); ReleaseKeys(); }

    private void Tick()
    {
        try
        {
            bool front = Svc.Roblox.IsForeground && !RobloxService.IsOwnWindowForeground();
            bool wantShift = _cfg.AutoSprint && front;
            if (wantShift && !_shiftHeld) { InputSender.KeyScan(Shift, true); _shiftHeld = true; }
            else if (!wantShift && _shiftHeld) ReleaseKeys();

            if (_cfg.AntiAfk && front && Environment.TickCount64 >= _nextAfk)
            {
                _nextAfk = Environment.TickCount64 + 4 * 60_000;
                InputSender.KeyScan(AfkKey, true);
                Thread.Sleep(30);
                InputSender.KeyScan(AfkKey, false);
            }
        }
        catch (Exception ex) { Log.Error("Extras tick failed", ex); }
    }
}

/// <summary>
/// Network tune. No program can lower the physical delay to a server, so this only does the real things: flush the DNS cache and mark
/// Roblox's packets as high priority (a Windows QoS policy, which routers that honour it use to send them first). Both need
/// administrator approval, which Windows asks for; the policy can be removed again.
/// </summary>
public static class NetworkTune
{
    private const string Policy = "NightyLiteRoblox";

    private static bool RunElevated(string script)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe") { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
            psi.ArgumentList.Add("-NoProfile"); psi.ArgumentList.Add("-Command"); psi.ArgumentList.Add(script);
            using var p = Process.Start(psi);
            p?.WaitForExit(15000);
            return p is { ExitCode: 0 };
        }
        catch (Exception ex) { Log.Warn("Network tune cancelled or failed", ex); return false; }
    }

    public static Task<bool> ApplyAsync() => Task.Run(() => RunElevated(
        $"ipconfig /flushdns; Remove-NetQosPolicy -Name {Policy} -Confirm:$false -ErrorAction SilentlyContinue; " +
        $"New-NetQosPolicy -Name {Policy} -AppPathNameMatchCondition RobloxPlayerBeta.exe -DSCPAction 46 -NetworkProfile All -ErrorAction Stop"));

    public static Task<bool> RemoveAsync() => Task.Run(() => RunElevated($"Remove-NetQosPolicy -Name {Policy} -Confirm:$false -ErrorAction Stop"));
}
