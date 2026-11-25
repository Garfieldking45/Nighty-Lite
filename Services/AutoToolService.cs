using System.Runtime.InteropServices;
using Nighty.Models;
using Nighty.Native;

namespace Nighty.Services;

/// <summary>
/// Auto Tool: while you are holding one of your tool slots (pickaxe / axe / shears) and aiming at a block, selects the
/// slot of the tool that breaks that block. The block is recognised from the screen pixels around the crosshair
/// (see <see cref="BlockClassifier"/>). Only presses your own number keys, never when the overlay or the crossbow is busy.
/// </summary>
public sealed class AutoToolService
{
    private const int SkipRadius = 3;          // ignore the crosshair itself
    private const int PollMs = 25;
    private const int LockoutMs = 220;         // after a switch, give the hotbar and the view time to settle

    /// <summary>Set by the overlay window: while the GUI is drawn over the game it would be captured, so nothing is read.</summary>
    public static volatile bool OverlayShown;

    private readonly AutoToolConfig _c;
    private Thread? _thread;
    private volatile bool _run;

    /// <summary>A short message for the window's status line.</summary>
    public event Action<string>? Notice;
    /// <summary>What the last look at the block decided (for the status line).</summary>
    public volatile string LastLook = "";

    public AutoToolService(AutoToolConfig c) { _c = c; }

    public void Start()
    {
        if (_thread != null) return;
        _run = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "NightyLite auto tool", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Shutdown() => _run = false;

    private int SlotOf(ToolKind t) => t switch
    {
        ToolKind.Pickaxe => (int)_c.PickSlot, ToolKind.Axe => (int)_c.AxeSlot, ToolKind.Shears => (int)_c.ShearsSlot, _ => 0,
    };

    /// <summary>Which tool the current hotbar slot is, or None when it is not one of the three tool slots.</summary>
    private ToolKind ToolInSlot(int slot)
    {
        if (slot <= 0) return ToolKind.None;
        if (slot == (int)_c.PickSlot) return ToolKind.Pickaxe;
        if (slot == (int)_c.AxeSlot) return ToolKind.Axe;
        if (slot == (int)_c.ShearsSlot) return ToolKind.Shears;
        return ToolKind.None;
    }

    private bool Ready()
    {
        if (Svc.Hotkeys.Suspended || OverlayShown) return false;
        if (RobloxService.IsOwnWindowForeground() || !Svc.Roblox.IsForeground) return false;
        return true;
    }

    private void Loop()
    {
        ToolKind pending = ToolKind.None;
        int pendingCount = 0;
        long lockUntil = 0;
        while (_run)
        {
            try
            {
                Thread.Sleep(PollMs);
                if (!_c.Enabled || !Ready()) { pending = ToolKind.None; pendingCount = 0; continue; }
                if (Environment.TickCount64 < lockUntil) continue;
                if (Svc.Crossbow.IsRunning || CrossbowService.ShotInProgress) continue;

                int slot = Svc.Slots.CurrentSlot;
                if (ToolInSlot(slot) == ToolKind.None) { pending = ToolKind.None; pendingCount = 0; continue; }   // not in a tool slot
                if (_c.OnlyWhileMining && !Mining()) { pending = ToolKind.None; pendingCount = 0; continue; }

                // Read the block twice: the full square and just its middle. Near an edge (a bed sitting on wool) the two
                // disagree, and then nothing is changed, so only a clear look at one block switches the tool.
                int full = (int)Math.Clamp(_c.PatchSize, 8, 64);
                var sig = Look(full);
                var core = Look(Math.Max(8, full / 2));
                if (sig == null || core == null) continue;
                var wantFull = BlockClassifier.Classify(sig, Join(BuiltInBlocks.Pickaxe, _c.PickSamples), Join(BuiltInBlocks.Axe, _c.AxeSamples),
                    Join(BuiltInBlocks.Shears, _c.ShearsSamples), _c.Strictness / 100.0);
                var wantCore = BlockClassifier.Classify(core, Join(BuiltInBlocks.Pickaxe, _c.PickSamples), Join(BuiltInBlocks.Axe, _c.AxeSamples),
                    Join(BuiltInBlocks.Shears, _c.ShearsSamples), _c.Strictness / 100.0);
                var want = wantFull == wantCore ? wantFull : ToolKind.None;
                LastLook = want == ToolKind.None ? "no known block" : want.ToString();
                if (want == ToolKind.None) { pending = ToolKind.None; pendingCount = 0; continue; }

                // Two looks in a row must agree, so one odd frame (a particle, a flash) never switches the tool.
                if (want == pending) pendingCount++; else { pending = want; pendingCount = 1; }
                if (pendingCount < 2) continue;

                int target = SlotOf(want);
                if (target <= 0 || target == slot) continue;
                PressSlot(target);
                lockUntil = Environment.TickCount64 + LockoutMs;
                pending = ToolKind.None; pendingCount = 0;
                Log.Info($"Auto Tool: {want} for the block ahead, slot {slot} -> {target} (texture {sig[BlockClassifier.Bins]:0.000}, middle {core[BlockClassifier.Bins]:0.000})");
            }
            catch (Exception ex) { Log.Error("Auto Tool failed", ex); Thread.Sleep(500); }
        }
    }

    /// <summary>The blocks that ship with the app (made from screenshots) followed by the ones you taught.</summary>
    private static List<float[]> Join(float[][] builtIn, List<float[]> taught) { var l = new List<float[]>(builtIn); l.AddRange(taught); return l; }

    private bool Mining() => (NativeMethods.GetAsyncKeyState(0x01) & 0x8000) != 0 || Svc.Clicker.IsClicking;

    private static void PressSlot(int slot)
    {
        int vk = 0x31 + Math.Clamp(slot, 1, 9) - 1;
        InputSender.KeyScan(vk, true);
        Thread.Sleep(20);   // long enough to span a frame, or the game never sees the press
        InputSender.KeyScan(vk, false);
    }

    /// <summary>Teach key: remember what the block ahead looks like as belonging to the tool you are holding.</summary>
    public void Teach()
    {
        string msg;
        var system = System.Media.SystemSounds.Hand;
        try
        {
            var tool = ToolInSlot(Svc.Slots.CurrentSlot);
            if (!Svc.Roblox.IsForeground || RobloxService.IsOwnWindowForeground()) msg = "Teach: Roblox must be in front.";
            else if (OverlayShown) msg = "Teach: let go of the GUI key first.";
            else if (tool == ToolKind.None) msg = "Teach: hold your pickaxe, axe or shears (press its number key) and aim at the block.";
            else
            {
                var sig = Look();
                if (sig == null) msg = "Teach: could not read the screen.";
                else
                {
                    var list = tool switch { ToolKind.Pickaxe => _c.PickSamples, ToolKind.Axe => _c.AxeSamples, _ => _c.ShearsSamples };
                    list.Add(sig);
                    if (list.Count > 24) list.RemoveAt(0);
                    _c.RaiseLearned();
                    Svc.Settings.Save();
                    msg = $"Taught: this block is for the {tool}. ({_c.LearnedCount} saved)";
                    system = System.Media.SystemSounds.Asterisk;
                }
            }
        }
        catch (Exception ex) { Log.Error("Auto Tool teach failed", ex); msg = "Teach failed (see log)."; }
        try { system.Play(); } catch { }
        Notice?.Invoke(msg);
    }

    public void ForgetAll()
    {
        _c.PickSamples.Clear(); _c.AxeSamples.Clear(); _c.ShearsSamples.Clear();
        _c.RaiseLearned();
        Svc.Settings.Save();
    }

    // ------------------------------------------------------------------ reading the screen
    private float[]? Look(int size = 0)
    {
        size = size > 0 ? size : (int)Math.Clamp(_c.PatchSize, 8, 64);
        if (!AimPoint(out int x, out int y)) return null;
        var px = Grab(x - size / 2, y - size / 2, size, size);
        return px == null ? null : BlockClassifier.Signature(px, size, size, SkipRadius);
    }

    /// <summary>Where the crosshair is: the mouse pointer while it is visible, otherwise (mouse-look) the middle of the game window.</summary>
    private static bool AimPoint(out int x, out int y)
    {
        x = y = 0;
        var ci = new NativeMethods.CURSORINFO { cbSize = Marshal.SizeOf<NativeMethods.CURSORINFO>() };
        if (NativeMethods.GetCursorInfo(ref ci) && (ci.flags & NativeMethods.CURSOR_SHOWING) != 0)
        { x = ci.ptScreenPos.X; y = ci.ptScreenPos.Y; return true; }
        var fg = NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero || !NativeMethods.GetClientRect(fg, out var rc) || rc.R <= 0 || rc.B <= 0) return false;
        var p = new NativeMethods.POINT { X = rc.R / 2, Y = rc.B / 2 };
        if (!NativeMethods.ClientToScreen(fg, ref p)) return false;
        x = p.X; y = p.Y;
        return true;
    }

    /// <summary>Copies a w x h square of the desktop as BGRA bytes.</summary>
    private static byte[]? Grab(int x0, int y0, int w, int h)
    {
        IntPtr screen = NativeMethods.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return null;
        IntPtr mem = NativeMethods.CreateCompatibleDC(screen);
        IntPtr bmp = mem == IntPtr.Zero ? IntPtr.Zero : NativeMethods.CreateCompatibleBitmap(screen, w, h);
        try
        {
            if (mem == IntPtr.Zero || bmp == IntPtr.Zero) return null;
            IntPtr old = NativeMethods.SelectObject(mem, bmp);
            bool ok = NativeMethods.BitBlt(mem, 0, 0, w, h, screen, x0, y0, NativeMethods.SRCCOPY);
            NativeMethods.SelectObject(mem, old);   // a bitmap must not be selected into a DC while GetDIBits reads it
            if (!ok) return null;
            var bi = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32,
            };
            var buf = new byte[w * h * 4];
            return NativeMethods.GetDIBits(mem, bmp, 0, (uint)h, buf, ref bi, 0) == h ? buf : null;
        }
        finally
        {
            if (bmp != IntPtr.Zero) NativeMethods.DeleteObject(bmp);
            if (mem != IntPtr.Zero) NativeMethods.DeleteDC(mem);
            NativeMethods.ReleaseDC(IntPtr.Zero, screen);
        }
    }
}
