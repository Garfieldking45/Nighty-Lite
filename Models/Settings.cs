using System.Text.Json.Serialization;
using Nighty.Mvvm;

namespace Nighty.Models;

public enum ClickButton { Left, Right, Middle }
/// <summary>Efficient waits on the OS timer only, Balanced spins the last 0.4 ms, Precise spins the last 2 ms.</summary>
public enum PrecisionMode { Efficient, Balanced, Precise }
public enum ActivationMode { Toggle, Hold }
/// <summary>How the crossbow key works: Toggle on/off, Hold while the key is down, or Press for a single shot.</summary>
public enum CrossbowStyle { Toggle, Press, Hold }

public sealed class ClickerSettings : ObservableObject
{
    private bool _enabled = true;
    private double _cps = 12, _minCps = 10, _maxCps = 14;
    private bool _useRange, _onlyRoblox;
    private ClickButton _button = ClickButton.Left;
    private int _duty = 50, _vk = 0x75, _mods;
    private ActivationMode _mode = ActivationMode.Toggle;

    /// <summary>Armed: the hotkey starts the clicker. Off = the hotkey does nothing.</summary>
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    private bool _jitter;
    /// <summary>Randomize each gap between clicks to 60-140% of the base period.</summary>
    public bool Jitter { get => _jitter; set => Set(ref _jitter, value); }
    private int _perHit = 1;
    public int ClicksPerHit { get => _perHit; set => Set(ref _perHit, Math.Clamp(value, 1, 5)); }
    public double Cps { get => _cps; set => Set(ref _cps, Math.Clamp(Math.Round(value, 1), 1, 100)); }
    public bool UseRange { get => _useRange; set => Set(ref _useRange, value); }
    public double MinCps
    {
        get => _minCps;
        set { if (Set(ref _minCps, Math.Clamp(Math.Round(value, 1), 1, 100)) && _minCps > _maxCps) MaxCps = _minCps; }
    }
    public double MaxCps
    {
        get => _maxCps;
        set { if (Set(ref _maxCps, Math.Clamp(Math.Round(value, 1), 1, 100)) && _maxCps < _minCps) MinCps = _maxCps; }
    }
    public ClickButton Button { get => _button; set => Set(ref _button, value); }
    public int DutyCycle { get => _duty; set => Set(ref _duty, Math.Clamp(value, 5, 95)); }
    public ActivationMode Mode { get => _mode; set => Set(ref _mode, value); }
    public int HotkeyVk { get => _vk; set { if (Set(ref _vk, value)) OnPropertyChanged(nameof(KeyText)); } }
    public int HotkeyMods { get => _mods; set { if (Set(ref _mods, value)) OnPropertyChanged(nameof(KeyText)); } }
    [JsonIgnore] public string KeyText => Hotkeys.Format(HotkeyVk, HotkeyMods);
    public bool OnlyWhenRobloxFocused { get => _onlyRoblox; set => Set(ref _onlyRoblox, value); }

    private bool _cornerStop = true;
    /// <summary>Failsafe: throwing the mouse into a corner of the desktop stops the clicker.</summary>
    public bool StopInCorner { get => _cornerStop; set => Set(ref _cornerStop, value); }

    private bool _hitFix = true;
    private int _stopAfter, _timeLimit, _startDelayMs;
    /// <summary>Steadier clicks: the clicker thread gets real-time priority, its own CPU core and an exact final spin before every click.</summary>
    public bool HitFix { get => _hitFix; set => Set(ref _hitFix, value); }
    public int StopAfterClicks { get => _stopAfter; set => Set(ref _stopAfter, Math.Clamp(value, 0, 1_000_000)); }
    public int TimeLimitSec { get => _timeLimit; set => Set(ref _timeLimit, Math.Clamp(value, 0, 86400)); }
    public int StartDelayMs { get => _startDelayMs; set => Set(ref _startDelayMs, Math.Clamp(value, 0, 10000)); }
}

/// <summary>Auto Crossbow: equips the crossbow, fires, swings the sword the instant it is selected, clicks through the cooldown, then repeats.</summary>
public sealed class CrossbowConfig : ObservableObject
{
    private bool _enabled = true, _onlyRoblox = true, _clickerShots, _onlySlotOne = true;
    private int _vk = 0x05, _mods;
    private double _a = 3, _b = 1, _block = 2;
    private double _equipMs = 35, _shotHoldMs = 35, _swapMs = 25, _cooldownMs;
    private CrossbowStyle _style = CrossbowStyle.Hold;

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public bool OnlyWhenRobloxFocused { get => _onlyRoblox; set => Set(ref _onlyRoblox, value); }
    /// <summary>Time from pressing the crossbow's number key to the shot. Too short and the click lands before it is equipped.</summary>
    public double EquipDelayMs { get => _equipMs; set => Set(ref _equipMs, Math.Clamp(Math.Round(value), 22, 200)); }
    /// <summary>How long the shot is held down.</summary>
    public double ShotHoldMs { get => _shotHoldMs; set => Set(ref _shotHoldMs, Math.Clamp(Math.Round(value), 10, 150)); }
    /// <summary>Pause between the shot and swapping to the sword.</summary>
    public double SwapDelayMs { get => _swapMs; set => Set(ref _swapMs, Math.Clamp(Math.Round(value), 0, 150)); }
    /// <summary>Let the running Auto Clicker's own clicks fire the shot instead of clicking separately.</summary>
    public bool UseClickerShots { get => _clickerShots; set => Set(ref _clickerShots, value); }
    /// <summary>Time between shots. 0 = automatic (1350 ms).</summary>
    public double CooldownMs { get => _cooldownMs; set => Set(ref _cooldownMs, value <= 0 ? 0 : Math.Clamp(Math.Round(value), 800, 3000)); }
    /// <summary>Start only while hotbar slot 1 is the slot you are holding (pick it with the 1 key).</summary>
    public bool OnlyInSlotOne { get => _onlySlotOne; set => Set(ref _onlySlotOne, value); }
    public int HotkeyVk { get => _vk; set { if (Set(ref _vk, value)) OnPropertyChanged(nameof(KeyText)); } }
    public int HotkeyMods { get => _mods; set { if (Set(ref _mods, value)) OnPropertyChanged(nameof(KeyText)); } }
    [JsonIgnore] public string KeyText => Hotkeys.Format(HotkeyVk, HotkeyMods);
    /// <summary>Hotbar slot holding the crossbow.</summary>
    public double SlotA { get => _a; set => Set(ref _a, Math.Clamp(Math.Round(value), 1, 9)); }
    /// <summary>Hotbar slot holding the sword.</summary>
    public double SlotB { get => _b; set => Set(ref _b, Math.Clamp(Math.Round(value), 1, 9)); }
    /// <summary>Hotbar slot holding your blocks (0 = off). Nothing fires while it is selected, so you keep placing blocks.</summary>
    public double BlockSlot { get => _block; set => Set(ref _block, Math.Clamp(Math.Round(value), 0, 9)); }
    public CrossbowStyle Style { get => _style; set => Set(ref _style, value); }

    public CrossbowConfig Snapshot() => (CrossbowConfig)MemberwiseClone();
}

/// <summary>Boost FPS: which tweaks are on. Everything is reversible and follows the master switch.</summary>
public sealed class BoostConfig : ObservableObject
{
    private bool _enabled, _gpu = true, _gameMode = true, _priority = true, _trim = true, _mem = true;
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    /// <summary>Keep every Roblox install on the high-performance graphics card (matters on laptops with two GPUs).</summary>
    public bool GpuPreference { get => _gpu; set => Set(ref _gpu, value); }
    /// <summary>Windows Game Mode on.</summary>
    public bool GameMode { get => _gameMode; set => Set(ref _gameMode, value); }
    /// <summary>Roblox runs at High priority and is exempt from Windows power throttling.</summary>
    public bool RobloxPriority { get => _priority; set => Set(ref _priority, value); }
    /// <summary>While Roblox runs, heavy background apps (browsers, emulators, launchers) drop to low priority. Nothing is closed.</summary>
    public bool TrimBackground { get => _trim; set => Set(ref _trim, value); }
    /// <summary>When memory is getting tight, background apps hand back RAM they are not using right now. Roblox itself is never trimmed.</summary>
    public bool FreeMemory { get => _mem; set => Set(ref _mem, value); }
}

public enum CrosshairStyle { Cross, CrossDot, Dot, Circle }

/// <summary>On-screen overlays drawn over Roblox: a crosshair and a clicks-per-second counter.</summary>
public sealed class HudConfig : ObservableObject
{
    public static readonly (string Name, string Hex)[] Colors =
    {
        ("Green", "#00FF55"), ("Red", "#FF3B30"), ("White", "#FFFFFF"), ("Cyan", "#00E5FF"),
        ("Yellow", "#FFE600"), ("Magenta", "#FF2DDC"), ("Blue", "#3B82F6"), ("Black", "#000000"),
    };

    private bool _crossOn, _outline = true, _cpsOn;
    private CrosshairStyle _style = CrosshairStyle.Cross;
    private string _color = "#00FF55";
    private int _arm = 10, _thick = 2, _gap = 4;
    private double _cpsX = 50, _cpsY = 4, _cpsScale = 1;
    private bool _fpsOn, _pingOn;
    private double _fpsX = 2, _fpsY = 4, _pingX = 98, _pingY = 4;
    private string _pingHost = "1.1.1.1";

    public bool CrosshairEnabled { get => _crossOn; set => Set(ref _crossOn, value); }
    public CrosshairStyle Style { get => _style; set => Set(ref _style, value); }
    public string Color { get => _color; set => Set(ref _color, value); }
    public int ArmLength { get => _arm; set => Set(ref _arm, Math.Clamp(value, 1, 80)); }
    public int Thickness { get => _thick; set => Set(ref _thick, Math.Clamp(value, 1, 10)); }
    public int Gap { get => _gap; set => Set(ref _gap, Math.Clamp(value, 0, 60)); }
    public bool Outline { get => _outline; set => Set(ref _outline, value); }

    public bool CpsEnabled { get => _cpsOn; set => Set(ref _cpsOn, value); }
    /// <summary>Counter position as a percentage of the Roblox window.</summary>
    public double CpsX { get => _cpsX; set => Set(ref _cpsX, Math.Clamp(Math.Round(value), 0, 100)); }
    public double CpsY { get => _cpsY; set => Set(ref _cpsY, Math.Clamp(Math.Round(value), 0, 100)); }
    public bool FpsEnabled { get => _fpsOn; set => Set(ref _fpsOn, value); }
    public double FpsX { get => _fpsX; set => Set(ref _fpsX, Math.Clamp(Math.Round(value), 0, 100)); }
    public double FpsY { get => _fpsY; set => Set(ref _fpsY, Math.Clamp(Math.Round(value), 0, 100)); }
    public bool PingEnabled { get => _pingOn; set => Set(ref _pingOn, value); }
    public double PingX { get => _pingX; set => Set(ref _pingX, Math.Clamp(Math.Round(value), 0, 100)); }
    public double PingY { get => _pingY; set => Set(ref _pingY, Math.Clamp(Math.Round(value), 0, 100)); }
    /// <summary>What the ping overlay measures. Roblox game servers cannot be pinged reliably (UDP), so this is the route to the internet.</summary>
    public string PingHost { get => _pingHost; set => Set(ref _pingHost, value); }
    public double CpsScale { get => _cpsScale; set => Set(ref _cpsScale, Math.Clamp(Math.Round(value, 1), 0.5, 3)); }
}

/// <summary>
/// Wins tracker: a counter you add to with a hotkey, drawn over Roblox so you never have to leave the game.
/// Add win defaults to F7, take one back to Ctrl+F7.
/// </summary>
public sealed class WinsConfig : ObservableObject
{
    private int _count;
    private bool _on = true;
    private double _x = 2, _y = 12, _scale = 1;
    private int _addVk = 0x76, _addMods, _undoVk = 0x76, _undoMods = Hotkeys.Ctrl;

    public const int MaxWins = 999_999;
    /// <summary>The number of wins.</summary>
    public int Count { get => _count; set => Set(ref _count, Math.Clamp(value, 0, MaxWins)); }
    /// <summary>Show the counter over Roblox.</summary>
    public bool Enabled { get => _on; set => Set(ref _on, value); }
    /// <summary>Counter position as a percentage of the Roblox window.</summary>
    public double X { get => _x; set => Set(ref _x, Math.Clamp(Math.Round(value), 0, 100)); }
    public double Y { get => _y; set => Set(ref _y, Math.Clamp(Math.Round(value), 0, 100)); }
    public double Scale { get => _scale; set => Set(ref _scale, Math.Clamp(Math.Round(value, 1), 0.5, 3)); }
    /// <summary>Key that adds one win.</summary>
    public int AddVk { get => _addVk; set => Set(ref _addVk, value); }
    public int AddMods { get => _addMods; set => Set(ref _addMods, value); }
    /// <summary>Key that takes one win back (for a mis-press).</summary>
    public int UndoVk { get => _undoVk; set => Set(ref _undoVk, value); }
    public int UndoMods { get => _undoMods; set => Set(ref _undoMods, value); }
}

/// <summary>Small input helpers that only press your own keys.</summary>
public sealed class ExtrasConfig : ObservableObject
{
    private bool _sprint, _afk;
    /// <summary>Holds Shift while Roblox is in front, so you sprint without holding it.</summary>
    public bool AutoSprint { get => _sprint; set => Set(ref _sprint, value); }
    /// <summary>Taps a harmless key every few minutes while Roblox is in front, so the 20-minute idle kick never fires.</summary>
    public bool AntiAfk { get => _afk; set => Set(ref _afk, value); }

    private bool _stats = true;
    /// <summary>Sends an anonymous ping (random install ID + app version) at start-up so the author can count users per version. Nothing else.</summary>
    public bool ShareUsageStats { get => _stats; set => Set(ref _stats, value); }
    /// <summary>Random ID created on first run. Not linked to your name, PC, IP or Roblox account.</summary>
    public string InstallId { get; set; } = "";
    // Counters waiting to be reported with the next ping (see StatsService).
    public string PendingFeatures { get; set; } = "";
    public int PendingErrors { get; set; }
    public int PendingUpdateFails { get; set; }
}

/// <summary>
/// Auto Tool: while you hold one of your tool slots and aim at a block, switches to the tool that breaks it.
/// Pickaxe / axe / shears slots are set here; what each block looks like is taught with the Teach key.
/// </summary>
public sealed class AutoToolConfig : ObservableObject
{
    private bool _enabled = true, _onlyMining = true;
    private double _pick, _axe, _shears, _patch = 24, _strict = 62;
    private int _teachVk = 0x77, _teachMods;

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    /// <summary>Only switch while the left button is held or the Auto Clicker is running, so looking around never changes your slot.</summary>
    public bool OnlyWhileMining { get => _onlyMining; set => Set(ref _onlyMining, value); }
    public double PickSlot { get => _pick; set => Set(ref _pick, Math.Clamp(Math.Round(value), 0, 9)); }
    public double AxeSlot { get => _axe; set => Set(ref _axe, Math.Clamp(Math.Round(value), 0, 9)); }
    public double ShearsSlot { get => _shears; set => Set(ref _shears, Math.Clamp(Math.Round(value), 0, 9)); }
    /// <summary>Width of the square of screen (in pixels) looked at under the crosshair.</summary>
    public double PatchSize { get => _patch; set => Set(ref _patch, Math.Clamp(Math.Round(value / 2) * 2, 8, 64)); }
    /// <summary>How closely a block must match something taught before the tool switches (percent). Higher = fewer wrong switches.</summary>
    public double Strictness { get => _strict; set => Set(ref _strict, Math.Clamp(Math.Round(value), 40, 90)); }
    public int TeachVk { get => _teachVk; set { if (Set(ref _teachVk, value)) OnPropertyChanged(nameof(TeachText)); } }
    public int TeachMods { get => _teachMods; set { if (Set(ref _teachMods, value)) OnPropertyChanged(nameof(TeachText)); } }
    [JsonIgnore] public string TeachText => Hotkeys.Format(TeachVk, TeachMods);

    // What blocks look like, taught in game (see BlockClassifier for the format).
    public List<float[]> PickSamples { get; set; } = new();
    public List<float[]> AxeSamples { get; set; } = new();
    public List<float[]> ShearsSamples { get; set; } = new();
    [JsonIgnore] public int LearnedCount => PickSamples.Count + AxeSamples.Count + ShearsSamples.Count;
    public void RaiseLearned() => OnPropertyChanged(nameof(LearnedCount));
}

public sealed class AppSettings
{
    public ClickerSettings Clicker { get; set; } = new();
    public CrossbowConfig Crossbow { get; set; } = new();
    public BoostConfig Boost { get; set; } = new();
    public HudConfig Hud { get; set; } = new();
    public ExtrasConfig Extras { get; set; } = new();
    public AutoToolConfig AutoTool { get; set; } = new();
    public WinsConfig Wins { get; set; } = new();
    public PrecisionMode Precision { get; set; } = PrecisionMode.Balanced;
    /// <summary>Emergency stop: stops the clicker and the crossbow. Default F9.</summary>
    public int StopHotkeyVk { get; set; } = 0x78;
    public int StopHotkeyMods { get; set; }
    /// <summary>Panel background colour (hex).</summary>
    public string BackColor { get; set; } = "#1B1B1B";
    /// <summary>GUI theme (the colour of enabled rows), as a hue from 0 to 1.</summary>
    public double ThemeHue { get; set; } = 163.0 / 360.0;
    /// <summary>The key you hold to show the overlay. Default Tab.</summary>
    public int OverlayVk { get; set; } = 0x09;
    /// <summary>Where each panel was dragged to (x, y in overlay pixels), by panel name.</summary>
    public Dictionary<string, double[]> PanelPos { get; set; } = new();
}
