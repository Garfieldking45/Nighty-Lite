using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
using Nighty.Models;
using Nighty.Native;

namespace Nighty.Services;

/// <summary>
/// Boost FPS, built for a laptop with two graphics chips. Four independent, reversible tweaks:
/// <list type="bullet">
/// <item>every Roblox install (Roblox, Froststrap, Bloxstrap, including new version folders) stays on the high-performance GPU;</item>
/// <item>Windows Game Mode on;</item>
/// <item>Roblox runs at High priority and is exempt from Windows power throttling;</item>
/// <item>while Roblox runs, heavy background apps drop to low priority (nothing is closed or killed), and when RAM is tight they hand back memory they are not using.</item>
/// </list>
/// Every original value is written to a journal before it is changed, so a crash or a forced close is undone on the next start.
/// Roblox's files and memory are never touched.
/// </summary>
public sealed class BoostService
{
    private const string GpuKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string GameBarKey = @"Software\Microsoft\GameBar";

    /// <summary>Apps that eat CPU and disk while you play. Lowered in priority, never closed.</summary>
    private static readonly string[] Heavy =
    {
        "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "Discord", "Spotify", "Teams", "ms-teams", "OneDrive", "Dropbox",
        "steamwebhelper", "EpicGamesLauncher", "EpicWebHelper", "HD-Player", "BstkSVC", "BlueStacks", "BlueStacksServices", "Widgets", "WidgetService",
    };

    private sealed class RegEntry { public string Key { get; set; } = ""; public string Name { get; set; } = ""; public bool Existed { get; set; } public string? Old { get; set; } public string New { get; set; } = ""; public string Kind { get; set; } = "String"; }
    private sealed class ProcEntry { public int Pid { get; set; } public long Start { get; set; } public int Priority { get; set; } }
    private sealed class JournalData { public List<RegEntry> Reg { get; set; } = new(); public List<ProcEntry> Procs { get; set; } = new(); }

    private readonly BoostConfig _cfg;
    private readonly object _gate = new();
    private readonly string _journalPath = Path.Combine(AppPaths.Root, "boost-journal.json");
    private JournalData _j = new();
    private readonly Dictionary<int, long> _boosted = new();   // Roblox pid -> start time, so each process is tuned once
    private Timer? _timer;
    private long _nextScan;
    private int _gpuChanged, _blocked, _eased;
    private bool _gameModeSet;
    private long _nextTrim;
    private long _freedMb;

    /// <summary>Status text for the panel.</summary>
    public string Status { get; private set; } = "Off.";
    public event Action? StatusChanged;

    public BoostService(BoostConfig cfg) => _cfg = cfg;

    public void Start()
    {
        lock (_gate) Undo();   // whatever an earlier run left behind
        _cfg.PropertyChanged += (_, e) => OnConfigChanged(e.PropertyName);
        _timer = new Timer(_ => Tick(), null, 0, 2000);
    }

    public void Shutdown()
    {
        _timer?.Dispose();
        lock (_gate) Undo();
    }

    private void OnConfigChanged(string? name)
    {
        lock (_gate)
        {
            // Turning anything off, or the whole thing off, undoes it straight away; Tick applies what is on.
            Undo();
            _nextScan = 0;
        }
        Tick();
    }

    // ------------------------------------------------------------------ the loop
    private void Tick()
    {
        try
        {
            lock (_gate)
            {
                if (!_cfg.Enabled) { SetStatus("Off."); return; }
                var roblox = Process.GetProcessesByName(RobloxService.ProcessName);
                try
                {
                    if (_cfg.GpuPreference && Environment.TickCount64 >= _nextScan) { ApplyGpu(roblox); _nextScan = Environment.TickCount64 + 30_000; }
                    if (_cfg.GameMode) ApplyGameMode();

                    var live = new HashSet<int>(roblox.Select(p => p.Id));
                    foreach (var dead in _boosted.Keys.Where(k => !live.Contains(k)).ToList()) _boosted.Remove(dead);
                    if (roblox.Length > 0)
                    {
                        if (_cfg.RobloxPriority) foreach (var p in roblox) BoostRoblox(p);
                        if (_cfg.TrimBackground) EaseBackground();
                        if (_cfg.FreeMemory) FreeMemory();
                    }
                    else RestoreProcs();
                    SetStatus(Describe(roblox.Length));
                }
                finally { foreach (var p in roblox) p.Dispose(); }
            }
        }
        catch (Exception ex) { Log.Error("Boost tick failed", ex); }
    }

    private string Describe(int robloxCount)
    {
        var parts = new List<string>();
        if (_cfg.GpuPreference) parts.Add(_gpuChanged > 0 ? $"GPU set for {_gpuChanged} Roblox install(s)" : "GPU already set");
        if (_cfg.GameMode) parts.Add("Game Mode on");
        if (robloxCount == 0) parts.Add("waiting for Roblox");
        else
        {
            if (_cfg.RobloxPriority) parts.Add(_blocked > 0 ? "Roblox priority blocked by Windows" : $"Roblox boosted ({_boosted.Count})");
            if (_cfg.TrimBackground) parts.Add($"{_eased} background app(s) eased");
            if (_cfg.FreeMemory) parts.Add(_freedMb > 0 ? $"{_freedMb} MB freed" : "memory fine");
        }
        if (NativeMethods.GetSystemPowerStatus(out var ps) && ps.ACLineStatus == 0) parts.Add("ON BATTERY: plug in, the GPU is held back");
        return string.Join(" · ", parts) + ".";
    }

    private void SetStatus(string s)
    {
        if (s == Status) return;
        Status = s;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => StatusChanged?.Invoke());
    }

    // ------------------------------------------------------------------ 1. graphics card
    private void ApplyGpu(Process[] running)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var root in new[] { "Roblox", "Froststrap", "Bloxstrap", "Fishstrap", "Voidstrap" })
        {
            try
            {
                string versions = Path.Combine(local, root, "Versions");
                if (!Directory.Exists(versions)) continue;
                foreach (var dir in Directory.EnumerateDirectories(versions))
                    foreach (var exe in new[] { "RobloxPlayerBeta.exe", "RobloxStudioBeta.exe" })
                    {
                        string f = Path.Combine(dir, exe);
                        if (exe.StartsWith("RobloxPlayer") && File.Exists(f)) paths.Add(f);
                    }
            }
            catch { }
        }
        foreach (var p in running) { try { if (p.MainModule?.FileName is { Length: > 0 } f) paths.Add(f); } catch { } }

        int changed = 0;
        foreach (var path in paths) if (SetReg(GpuKey, path, "GpuPreference=2;", RegistryValueKind.String)) changed++;
        _gpuChanged = Math.Max(_gpuChanged, _j.Reg.Count(r => r.Key == GpuKey));
        if (changed > 0) Log.Info($"Boost: high-performance GPU set for {changed} Roblox path(s)");
    }

    // ------------------------------------------------------------------ 2. Game Mode
    private void ApplyGameMode()
    {
        if (_gameModeSet) return;
        SetReg(GameBarKey, "AutoGameModeEnabled", 1, RegistryValueKind.DWord);
        SetReg(GameBarKey, "AllowAutoGameMode", 1, RegistryValueKind.DWord);
        _gameModeSet = true;
    }

    // ------------------------------------------------------------------ 3 + 4. process tuning
    private static bool Throttle(Process p, uint control, uint state)
    {
        IntPtr h = NativeMethods.OpenProcess(NativeMethods.PROCESS_SET_INFORMATION, false, p.Id);
        if (h == IntPtr.Zero) return false;
        try
        {
            var s = new NativeMethods.PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = control, StateMask = state };
            return NativeMethods.SetProcessInformation(h, NativeMethods.ProcessPowerThrottling, ref s, System.Runtime.InteropServices.Marshal.SizeOf(s));
        }
        finally { NativeMethods.CloseHandle(h); }
    }

    private void BoostRoblox(Process p)
    {
        long start;
        try { start = p.StartTime.ToFileTimeUtc(); } catch { return; }
        if (_boosted.TryGetValue(p.Id, out var known) && known == start) return;
        try
        {
            if (p.PriorityClass != ProcessPriorityClass.High) p.PriorityClass = ProcessPriorityClass.High;
            Throttle(p, NativeMethods.THROTTLE_EXECUTION_SPEED, 0);   // explicitly "never throttle"
            _blocked = 0;
            Log.Info($"Boost: Roblox pid {p.Id} set to High priority");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _blocked++;   // Roblox's anti-tamper can refuse outside handles; that is fine, the other tweaks still work
            Log.Warn("Boost: Windows would not let Nighty Lite change Roblox's priority", ex);
        }
        _boosted[p.Id] = start;
    }

    private void EaseBackground()
    {
        int self = Environment.ProcessId, count = 0;
        foreach (var name in Heavy)
        {
            Process[] list;
            try { list = Process.GetProcessesByName(name); } catch { continue; }
            foreach (var p in list)
            {
                try
                {
                    if (p.Id == self) continue;
                    long start = p.StartTime.ToFileTimeUtc();
                    var existing = _j.Procs.FirstOrDefault(e => e.Pid == p.Id && e.Start == start);
                    if (existing == null)
                    {
                        var cur = p.PriorityClass;
                        if (cur is ProcessPriorityClass.BelowNormal or ProcessPriorityClass.Idle) { count++; continue; }   // already low: leave it alone
                        _j.Procs.Add(new ProcEntry { Pid = p.Id, Start = start, Priority = (int)cur });
                        SaveJournal();
                        p.PriorityClass = ProcessPriorityClass.BelowNormal;
                        Throttle(p, NativeMethods.THROTTLE_EXECUTION_SPEED, NativeMethods.THROTTLE_EXECUTION_SPEED);
                    }
                    count++;
                }
                catch { }   // exited, or not ours to change
                finally { p.Dispose(); }
            }
        }
        _eased = count;
    }

    private static ulong AvailMb()
    {
        var m = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        return NativeMethods.GlobalMemoryStatusEx(ref m) ? m.ullAvailPhys / (1024 * 1024) : 0;
    }

    /// <summary>
    /// Only when RAM is actually tight (under 3 GB free): background apps give back pages they are not using. Windows pages them
    /// back in on demand, so nothing is lost. Roblox is never trimmed, and this runs at most once a minute.
    /// </summary>
    private void FreeMemory()
    {
        if (Environment.TickCount64 < _nextTrim) return;
        _nextTrim = Environment.TickCount64 + 60_000;
        ulong before = AvailMb();
        if (before == 0 || before > 3072) return;
        foreach (var name in Heavy)
        {
            Process[] list;
            try { list = Process.GetProcessesByName(name); } catch { continue; }
            foreach (var p in list)
            {
                try
                {
                    if (p.Id == Environment.ProcessId) continue;
                    IntPtr h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_SET_QUOTA, false, p.Id);
                    if (h == IntPtr.Zero) continue;
                    try { NativeMethods.EmptyWorkingSet(h); } finally { NativeMethods.CloseHandle(h); }
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        ulong after = AvailMb();
        _freedMb = after > before ? (long)(after - before) : 0;
        Log.Info($"Boost: memory trim freed about {_freedMb} MB ({before} -> {after} MB free)");
    }

    private void RestoreProcs()
    {
        foreach (var e in _j.Procs.ToList())
        {
            try
            {
                using var p = Process.GetProcessById(e.Pid);
                if (p.StartTime.ToFileTimeUtc() != e.Start) continue;   // the pid was reused by another program
                p.PriorityClass = (ProcessPriorityClass)e.Priority;
                Throttle(p, 0, 0);   // back to the system's choice
            }
            catch { }
        }
        if (_j.Procs.Count > 0) { _j.Procs.Clear(); SaveJournal(); }
        _eased = 0;
    }

    // ------------------------------------------------------------------ registry + journal
    private bool SetReg(string key, string name, object value, RegistryValueKind kind)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(key, true);
            object? old = k.GetValue(name);
            if (old != null && old.ToString() == value.ToString()) return false;
            var entry = _j.Reg.FirstOrDefault(r => r.Key == key && r.Name == name);
            if (entry == null)
            {
                _j.Reg.Add(new RegEntry { Key = key, Name = name, Existed = old != null, Old = old?.ToString(), New = value.ToString()!, Kind = kind.ToString() });
                SaveJournal();   // written before the change, so a crash cannot lose the original
            }
            k.SetValue(name, value, kind);
            return true;
        }
        catch (Exception ex) { Log.Warn($"Boost: could not write {key}\\{name}", ex); return false; }
    }

    /// <summary>Puts back everything this class changed, but only values that still hold what it set.</summary>
    private void Undo()
    {
        LoadJournal();
        RestoreProcs();
        foreach (var r in _j.Reg.ToList())
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(r.Key, true);
                if (k == null) continue;
                if (k.GetValue(r.Name)?.ToString() != r.New) continue;   // you or Windows changed it since: leave it
                if (!r.Existed) k.DeleteValue(r.Name, false);
                else k.SetValue(r.Name, Enum.Parse<RegistryValueKind>(r.Kind) == RegistryValueKind.DWord ? int.Parse(r.Old!) : r.Old!, Enum.Parse<RegistryValueKind>(r.Kind));
            }
            catch (Exception ex) { Log.Warn($"Boost: could not restore {r.Key}\\{r.Name}", ex); }
        }
        _j.Reg.Clear();
        SaveJournal();
        _boosted.Clear();
        _gameModeSet = false;
        _gpuChanged = _blocked = _eased = 0;
        _freedMb = 0; _nextTrim = 0;
    }

    private void LoadJournal()
    {
        try { if (File.Exists(_journalPath)) _j = JsonSerializer.Deserialize<JournalData>(File.ReadAllText(_journalPath)) ?? _j; }
        catch (Exception ex) { Log.Warn("Boost journal unreadable", ex); }
    }

    private void SaveJournal()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            if (_j.Reg.Count == 0 && _j.Procs.Count == 0) { if (File.Exists(_journalPath)) File.Delete(_journalPath); return; }
            File.WriteAllText(_journalPath, JsonSerializer.Serialize(_j));
        }
        catch (Exception ex) { Log.Warn("Boost journal not saved", ex); }
    }
}
