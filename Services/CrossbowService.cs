using Nighty.Models;
using Nighty.Native;

namespace Nighty.Services;

/// <summary>
/// Auto Crossbow: equips the crossbow, fires, selects the sword and swings the moment it is selected, clicks through the
/// reload cooldown, then repeats. Runs on its own high-priority thread; keys are sent as hardware scancodes. A run can be
/// cancelled at any moment, and held keys and buttons are always released.
/// </summary>
public sealed class CrossbowService
{
    // Hold long enough to span a frame at 60 fps, or the game never sees the press.
    private const int KeyHoldMs = 20, ClickHoldMs = 10;
    private const double DefaultCooldownMs = 1350;

    /// <summary>True from the crossbow key going down until the shot is released. The Auto Clicker holds still meanwhile, so none of its clicks land inside the shot.</summary>
    public static bool ShotInProgress => Volatile.Read(ref _shots) > 0;
    private static int _shots;
    internal static void BeginShot() => Interlocked.Increment(ref _shots);
    internal static void EndShot() { if (Interlocked.Decrement(ref _shots) < 0) Interlocked.Exchange(ref _shots, 0); }

    private static long _shotsFired;
    public static long ShotsFired => Interlocked.Read(ref _shotsFired);

    private CancellationTokenSource? _cts;
    private readonly object _gate = new();
    public event Action? Changed;
    /// <summary>A short message for the window's status line (e.g. why a start was refused).</summary>
    public event Action<string>? Notice;

    public bool IsRunning => _cts != null;

    public void Start(CrossbowConfig c)
    {
        if (!c.Enabled || Svc.Hotkeys.Suspended) return;
        if (RobloxService.IsOwnWindowForeground()) { Log.Info("Crossbow refused: Nighty Lite window is in front"); return; }
        if (c.OnlyWhenRobloxFocused && !Svc.Roblox.IsForeground) { Log.Info("Crossbow refused: Roblox is not in front"); return; }
        // Limited to slot 1: a known different slot means "not now". Unknown (no number key pressed yet) is allowed.
        int slot = Svc.Slots.CurrentSlot;
        if (c.OnlyInSlotOne && slot != 0 && slot != 1)
        {
            Log.Info($"Crossbow refused: holding slot {slot}");
            Notice?.Invoke($"Auto Crossbow only works in slot 1 (you are on slot {slot}).");
            return;
        }
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_cts != null) return;
            cts = _cts = new CancellationTokenSource();
        }
        var snap = c.Snapshot();
        Log.Info("Crossbow started");
        Changed?.Invoke();
        new Thread(() => Run(snap, cts)) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "NightyLite crossbow" }.Start();
        new Thread(() => Watch(snap, cts)) { IsBackground = true, Name = "NightyLite crossbow watch" }.Start();
    }

    public void Toggle(CrossbowConfig c) { if (IsRunning) Stop(); else Start(c); }

    public void Stop()
    {
        var cts = _cts;
        if (cts == null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Ends a run the moment its hold key is let go or Roblox loses focus, without waiting for the UI timer.</summary>
    private static void Watch(CrossbowConfig c, CancellationTokenSource cts)
    {
        try
        {
            long nextFocusCheck = 0;
            while (!cts.IsCancellationRequested)
            {
                if (c.Style == CrossbowStyle.Hold && !HotkeyService.IsDown((c.HotkeyVk, c.HotkeyMods))) { cts.Cancel(); return; }
                if (c.OnlyWhenRobloxFocused && Environment.TickCount64 >= nextFocusCheck)
                {
                    nextFocusCheck = Environment.TickCount64 + 100;
                    if (!Svc.Roblox.IsForeground) { cts.Cancel(); return; }
                }
                Thread.Sleep(1);
            }
        }
        catch (ObjectDisposedException) { }
    }

    private static int SlotVk(double slot) => 0x31 + (int)Math.Clamp(slot, 1, 9) - 1;

    private static void Click(CancellationToken ct, ClickButton b = ClickButton.Left, double holdMs = ClickHoldMs)
    {
        InputSender.MouseButton(b, true);
        try { Wait.Ms(holdMs, ct); }
        finally { InputSender.MouseButton(b, false); }   // never leave the button stuck down
    }

    /// <summary>Gap between spam clicks while the cooldown runs: the Auto Clicker's speed, never faster than 20 ms.</summary>
    private static double GapMs()
    {
        var c = Svc.S.Clicker;
        double cps = Math.Clamp(c.UseRange ? c.MaxCps : c.Cps, 1, 50);
        return Math.Max(20, 1000.0 / cps);
    }

    private void Run(CrossbowConfig c, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        NativeMethods.timeBeginPeriod(1);
        try { RunSwapAndSwing(c, c.CooldownMs > 0 ? c.CooldownMs : DefaultCooldownMs, ct); }
        catch (Exception ex) { Log.Error("Auto Crossbow failed", ex); }
        finally
        {
            NativeMethods.timeEndPeriod(1);
            Log.Info("Crossbow stopped");
            lock (_gate) { if (ReferenceEquals(_cts, cts)) _cts = null; }
            cts.Dispose();
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke());
        }
    }

    /// <summary>When each part of one shot happens, in ms from the moment the crossbow key goes down. Pure, so it can be tested.</summary>
    internal readonly record struct ShotPlan(double KeyDown, double KeyUp, double ShotDown, double ShotUp, double SwordKey);

    internal static ShotPlan PlanShot(CrossbowConfig c)
    {
        // The shot must come after the key has been released and the crossbow has had time to equip, be held for
        // at least a frame or two, and only then is the sword selected.
        double equip = Math.Max(c.EquipDelayMs, KeyHoldMs + 2);
        double hold = Math.Max(c.ShotHoldMs, ClickHoldMs);
        double swap = Math.Max(0, c.SwapDelayMs);
        return new ShotPlan(0, KeyHoldMs, equip, equip + hold, equip + hold + swap);
    }

    /// <summary>
    /// Equips the crossbow and lets the Auto Clicker's own clicks fire it, then selects the sword as soon as it has fired.
    /// If no click arrives in time (clicker paused or refused), the shot is sent by hand so it still fires.
    /// </summary>
    private static long ShootWithClicker(CrossbowConfig c, int swordVk, CancellationToken ct)
    {
        var plan = PlanShot(c);
        int weaponVk = SlotVk(c.SlotA);
        long t0 = Wait.Now;
        InputSender.KeyScan(weaponVk, true);
        try { Wait.Until(t0 + Wait.FromMs(plan.KeyUp), ct); }
        finally { InputSender.KeyScan(weaponVk, false); }
        Wait.Until(t0 + Wait.FromMs(plan.ShotDown), ct);
        if (ct.IsCancellationRequested) return Wait.Now;

        var s = Svc.S.Clicker;
        double periodMs = 1000.0 / Math.Clamp(s.UseRange ? s.MaxCps : s.Cps, 1, 100);
        long from = Svc.Clicker.TotalClicks, fired = 0;
        long deadline = Wait.Now + Wait.FromMs(Math.Clamp(periodMs * 3, 60, 250));
        // Two clicks, because the first can land in the instant the equip finishes and be swallowed.
        while (!ct.IsCancellationRequested && Wait.Now < deadline)
        {
            long n = Svc.Clicker.TotalClicks - from;
            if (n >= 1 && fired == 0) fired = Wait.Now;
            if (n >= 2) break;
            Wait.Ms(1, ct);
        }
        if (fired == 0)
        {
            BeginShot();
            try { fired = Wait.Now; Click(ct, ClickButton.Left, plan.ShotUp - plan.ShotDown); }
            finally { EndShot(); }
            Wait.Ms(Math.Max(0, c.SwapDelayMs), ct);
        }

        InputSender.KeyScan(swordVk, true);
        try { Wait.Ms(ClickHoldMs, ct); }
        finally { InputSender.KeyScan(swordVk, false); }
        return fired;
    }

    private static void RunSwapAndSwing(CrossbowConfig c, double cooldownMs, CancellationToken ct)
    {
        double gap = GapMs();
        int swordVk = SlotVk(c.SlotB);
        do
        {
            // Holding blocks: leave them alone (this also ends the run if you pick the block slot mid-fight).
            if (c.BlockSlot > 0 && Svc.Slots.CurrentSlot == (int)c.BlockSlot) return;

            long fired;
            if (c.UseClickerShots && Svc.Clicker.IsClicking && Svc.S.Clicker.Button == ClickButton.Left)
            {
                fired = ShootWithClicker(c, swordVk, ct);
                if (ct.IsCancellationRequested) return;
            }
            else
            {
                BeginShot();
                bool shotOpen = true;
                try
                {
                    // One schedule from a single start time, so waits can't pile up and shift the shot.
                    var plan = PlanShot(c);
                    long t0 = Wait.Now;
                    int weaponVk = SlotVk(c.SlotA);
                    InputSender.KeyScan(weaponVk, true);
                    try { Wait.Until(t0 + Wait.FromMs(plan.KeyUp), ct); }
                    finally { InputSender.KeyScan(weaponVk, false); }
                    Wait.Until(t0 + Wait.FromMs(plan.ShotDown), ct);
                    if (ct.IsCancellationRequested) return;
                    fired = Wait.Now;
                    InputSender.MouseButton(ClickButton.Left, true);
                    try { Wait.Until(t0 + Wait.FromMs(plan.ShotUp), ct); }
                    finally { InputSender.MouseButton(ClickButton.Left, false); }   // never leave the shot held
                    shotOpen = false; EndShot();   // the shot is out; the Auto Clicker may swing again while the sword is selected
                    Wait.Until(t0 + Wait.FromMs(plan.SwordKey), ct);

                    InputSender.KeyScan(swordVk, true);
                    // The Auto Clicker is already swinging again, so a click of our own would only collide with its press and cost a hit.
                    try { if (!Svc.Clicker.IsClicking) Click(ct); else Wait.Ms(ClickHoldMs, ct); }
                    finally { InputSender.KeyScan(swordVk, false); }
                }
                finally { if (shotOpen) EndShot(); }
            }
            Interlocked.Increment(ref _shotsFired);
            if (c.Style == CrossbowStyle.Press) return;

            long swapAt = fired + Wait.FromMs(cooldownMs);
            long next = Wait.Now + Wait.FromMs(gap);
            while (!ct.IsCancellationRequested && Wait.Now < swapAt)
            {
                if (Wait.Now >= next && !Svc.Clicker.IsClicking)   // the Auto Clicker is already swinging
                {
                    next += Wait.FromMs(gap);
                    if (next <= Wait.Now) next = Wait.Now + Wait.FromMs(gap);
                    Click(ct);
                }
                Wait.Ms(2, ct);
            }
        } while (!ct.IsCancellationRequested);
    }
}
