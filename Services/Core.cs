using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using Nighty.Models;
using Nighty.Native;

namespace Nighty.Services;

public static class AppPaths
{
    /// <summary>NIGHTY_LITE_DATA points the app at a separate data folder (for testing next to a real copy).</summary>
    public static readonly string Root = Environment.GetEnvironmentVariable("NIGHTY_LITE_DATA") is { Length: > 0 } dev
        ? dev : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NightyLite");
    public static readonly string Logs = Path.Combine(Root, "logs");
    public static readonly string SettingsFile = Path.Combine(Root, "settings.json");
    public static void EnsureCreated() { Directory.CreateDirectory(Root); Directory.CreateDirectory(Logs); }
}

public static class Log
{
    private static readonly object Gate = new();
    public static void Info(string msg) => Write("INFO", msg, null);
    public static void Warn(string msg, Exception? ex = null) => Write("WARN", msg, ex);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", msg, ex);
    private static void Write(string level, string msg, Exception? ex)
    {
        StatsService.NoteLog(level, msg);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.Logs);
                File.AppendAllText(Path.Combine(AppPaths.Logs, $"nightylite-{DateTime.Now:yyyyMMdd}.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} [{level}] {msg}{(ex != null ? " | " + ex : "")}{Environment.NewLine}");
            }
        }
        catch { }
    }
}

/// <summary>Loads and persists <see cref="AppSettings"/> as JSON with debounced, atomic writes.</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly DispatcherTimer _debounce;
    public AppSettings Current { get; private set; } = new();

    public SettingsService()
    {
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Save(); };
    }

    public void Load()
    {
        AppPaths.EnsureCreated();
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Json) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Error("Settings file unreadable; starting from defaults", ex);
            try { File.Move(AppPaths.SettingsFile, AppPaths.SettingsFile + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}"); } catch { }
            Current = new AppSettings();
        }
        Current.Clicker.PropertyChanged += Dirty;
        Current.Crossbow.PropertyChanged += Dirty;
        Current.Boost.PropertyChanged += Dirty;
        Current.Hud.PropertyChanged += Dirty;
        Current.Extras.PropertyChanged += Dirty;
        Current.AutoTool.PropertyChanged += Dirty;
        Current.Wins.PropertyChanged += Dirty;
    }

    private void Dirty(object? s, PropertyChangedEventArgs e) { _debounce.Stop(); _debounce.Start(); }

    public void Save()
    {
        try
        {
            AppPaths.EnsureCreated();
            var tmp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Json));
            File.Move(tmp, AppPaths.SettingsFile, true);
        }
        catch (Exception ex) { Log.Error("Saving settings failed", ex); }
    }
}

/// <summary>Detects the Roblox client in front.</summary>
public sealed class RobloxService
{
    public const string ProcessName = "RobloxPlayerBeta";
    private int _fgPid;
    private bool _fgIsRoblox;
    private long _fgStamp;

    // The pid's process name is cached (re-checked every 2 s) because this is asked from the clicker thread.
    public bool IsForeground
    {
        get
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            long now = Environment.TickCount64;
            if (pid == _fgPid && now - _fgStamp < 2000) return _fgIsRoblox;
            bool result;
            try { using var p = Process.GetProcessById((int)pid); result = p.ProcessName.Equals(ProcessName, StringComparison.OrdinalIgnoreCase); }
            catch { result = false; }
            _fgPid = (int)pid; _fgIsRoblox = result; _fgStamp = now;
            return result;
        }
    }

    public static bool IsOwnWindowForeground()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return pid == Environment.ProcessId;
    }
}

/// <summary>Polls global hotkeys (no OS hooks, nothing to leak or release on exit).</summary>
public sealed class HotkeyService
{
    private sealed class Binding { public Func<(int Vk, int Mods)> Key = null!; public Action<bool> OnChange = null!; public bool Down; }
    private readonly Dictionary<string, Binding> _bindings = new();
    private readonly DispatcherTimer _timer;
    /// <summary>True while a hotkey box is capturing input, so bindings don't fire.</summary>
    public bool Suspended { get; set; }

    public HotkeyService()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(8) };
        _timer.Tick += (_, _) => Poll();
    }
    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();
    public void Register(string id, Func<(int Vk, int Mods)> key, Action<bool> onChange)
        => _bindings[id] = new Binding { Key = key, OnChange = onChange, Down = IsDown(key()) };

    private void Poll()
    {
        if (Suspended) return;
        foreach (var b in _bindings.Values.ToArray())
        {
            bool down = IsDown(b.Key());
            if (down == b.Down) continue;
            b.Down = down;
            try { b.OnChange(down); } catch (Exception ex) { Log.Error("Hotkey handler failed", ex); }
        }
    }

    public static bool IsDown((int Vk, int Mods) k)
    {
        if (k.Vk <= 0) return false;
        if ((NativeMethods.GetAsyncKeyState(k.Vk) & 0x8000) == 0) return false;
        if ((k.Mods & Hotkeys.Ctrl) != 0 && (NativeMethods.GetAsyncKeyState(0x11) & 0x8000) == 0) return false;
        if ((k.Mods & Hotkeys.Alt) != 0 && (NativeMethods.GetAsyncKeyState(0x12) & 0x8000) == 0) return false;
        if ((k.Mods & Hotkeys.Shift) != 0 && (NativeMethods.GetAsyncKeyState(0x10) & 0x8000) == 0) return false;
        if ((k.Mods & Hotkeys.Win) != 0 && (NativeMethods.GetAsyncKeyState(0x5B) & 0x8000) == 0 && (NativeMethods.GetAsyncKeyState(0x5C) & 0x8000) == 0) return false;
        return true;
    }
}

/// <summary>Which hotbar number key (1-9) was pressed last. The crossbow uses it to tell when you hold blocks or are not in slot 1.</summary>
public sealed class SlotTracker
{
    private volatile int _slot;
    private Thread? _watch;
    public int CurrentSlot => _slot;

    public void Start()
    {
        if (_watch != null) return;
        _watch = new Thread(() =>
        {
            var wasDown = new bool[9];
            while (true)
            {
                for (int i = 0; i < 9; i++)
                {
                    bool d = (NativeMethods.GetAsyncKeyState(0x31 + i) & 0x8000) != 0;
                    if (d && !wasDown[i] && _slot != i + 1) _slot = i + 1;
                    wasDown[i] = d;
                }
                Thread.Sleep(4);
            }
        }) { IsBackground = true, Name = "NightyLite slot tracker" };
        _watch.Start();
    }
}

public static class Svc
{
    public static SettingsService Settings { get; private set; } = null!;
    public static AppSettings S => Settings.Current;
    public static HotkeyService Hotkeys { get; private set; } = null!;
    public static RobloxService Roblox { get; private set; } = null!;
    public static ClickerService Clicker { get; private set; } = null!;
    public static CrossbowService Crossbow { get; private set; } = null!;
    public static BoostService Boost { get; private set; } = null!;
    public static HudService Hud { get; private set; } = null!;
    public static ExtrasService Extras { get; private set; } = null!;
    public static AutoToolService AutoTool { get; private set; } = null!;
    public static SlotTracker Slots { get; } = new();

    public static void Init()
    {
        // Windows 11 may throttle a background process (and ignore its timer resolution) while Roblox is in front: opt out of both.
        var state = new NativeMethods.PROCESS_POWER_THROTTLING_STATE
        {
            Version = 1,
            ControlMask = NativeMethods.THROTTLE_EXECUTION_SPEED | NativeMethods.THROTTLE_IGNORE_TIMER_RESOLUTION,
            StateMask = 0,
        };
        NativeMethods.SetProcessInformation(NativeMethods.GetCurrentProcess(), NativeMethods.ProcessPowerThrottling, ref state,
            System.Runtime.InteropServices.Marshal.SizeOf(state));
        Settings = new SettingsService();
        Settings.Load();
        // Both autos always start armed, whatever was saved last time.
        S.Clicker.Enabled = true;
        S.Crossbow.Enabled = true;
        Hotkeys = new HotkeyService();
        Roblox = new RobloxService();
        Clicker = new ClickerService(S.Clicker);
        Crossbow = new CrossbowService();
        Boost = new BoostService(S.Boost);
        Boost.Start();
        Hud = new HudService(S.Hud);
        Hud.Start();
        Extras = new ExtrasService(S.Extras);
        Extras.Start();
        Slots.Start();
        AutoTool = new AutoToolService(S.AutoTool);
        AutoTool.Start();
    }

    public static void StopAllInput() { Clicker.Stop(); Crossbow.Stop(); Extras?.ReleaseKeys(); }
}
