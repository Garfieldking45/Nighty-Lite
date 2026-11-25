using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Nighty.Models;
using Nighty.Native;

namespace Nighty.Services;

/// <summary>Counts left clicks (real and sent by programs) in the last second, with a mouse hook that lives on its own thread.</summary>
internal sealed class CpsCounter
{
    private IntPtr _hook;
    private NativeMethods.LowLevelProc? _proc;
    private uint _threadId;
    private bool _running;
    private readonly Queue<long> _clicks = new();
    private readonly object _gate = new();

    public void Start()
    {
        if (_running) return;
        _running = true;
        var ready = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            _proc = Callback;
            _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
            _threadId = NativeMethods.GetCurrentThreadId();
            if (_hook == IntPtr.Zero) Log.Warn("CPS mouse hook could not be installed");
            ready.Set();
            if (_hook == IntPtr.Zero) return;
            while (NativeMethods.GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }) { IsBackground = true, Name = "NightyLite CPS hook" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        ready.Wait(2000);
        if (_hook == IntPtr.Zero) _running = false;
    }

    /// <summary>Removes the hook. It is only installed while the counter is switched on.</summary>
    public void Stop()
    {
        if (!_running) return;
        _running = false;
        NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        lock (_gate) _clicks.Clear();
    }

    public int Read()
    {
        lock (_gate)
        {
            long now = Environment.TickCount64;
            while (_clicks.Count > 0 && now - _clicks.Peek() > 1000) _clicks.Dequeue();
            return _clicks.Count;
        }
    }

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (int)wParam == NativeMethods.WM_LBUTTONDOWN) lock (_gate) _clicks.Enqueue(Environment.TickCount64);
        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }
}

/// <summary>Draws the crosshair from a <see cref="HudConfig"/>.</summary>
internal sealed class CrosshairVisual : FrameworkElement
{
    private const int Pad = 3;
    private readonly HudConfig _c;

    public CrosshairVisual(HudConfig c)
    {
        _c = c;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    }

    public void Update() { InvalidateMeasure(); InvalidateVisual(); }
    private int Reach => _c.Gap + _c.ArmLength + (_c.Style == CrosshairStyle.Circle ? _c.Thickness : 0);
    protected override Size MeasureOverride(Size availableSize) { int side = (Reach + Pad) * 2; return new Size(side, side); }

    private static Color Parse(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return Color.FromRgb(0, 0xFF, 0x55); }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double c = Reach + Pad, half = _c.Thickness / 2.0;
        int t = _c.Thickness, gap = _c.Gap, arm = _c.ArmLength;
        var fill = new SolidColorBrush(Parse(_c.Color)); fill.Freeze();
        var edge = new SolidColorBrush(Color.FromArgb(0xE0, 0, 0, 0)); edge.Freeze();

        void Rect(double x, double y, double w, double h)
        {
            if (_c.Outline) dc.DrawRectangle(edge, null, new Rect(x - 1, y - 1, w + 2, h + 2));
            dc.DrawRectangle(fill, null, new Rect(x, y, w, h));
        }
        void Dot(double r)
        {
            if (_c.Outline) dc.DrawEllipse(edge, null, new Point(c, c), r + 1, r + 1);
            dc.DrawEllipse(fill, null, new Point(c, c), r, r);
        }

        switch (_c.Style)
        {
            case CrosshairStyle.Cross:
            case CrosshairStyle.CrossDot:
                Rect(c - gap - arm, c - half, arm, t);
                Rect(c + gap, c - half, arm, t);
                Rect(c - half, c - gap - arm, t, arm);
                Rect(c - half, c + gap, t, arm);
                if (_c.Style == CrosshairStyle.CrossDot) Dot(Math.Max(1, t * 0.75));
                break;
            case CrosshairStyle.Dot:
                Dot(Math.Max(1.5, t * 1.25));
                break;
            case CrosshairStyle.Circle:
                double r = gap + arm / 2.0;
                if (_c.Outline) dc.DrawEllipse(null, new Pen(edge, t + 2), new Point(c, c), r, r);
                dc.DrawEllipse(null, new Pen(fill, t), new Point(c, c), r, r);
                break;
        }
    }
}

/// <summary>
/// The on-screen overlays. One transparent, click-through window sits over the Roblox window and shows only while Roblox is in front,
/// so the crosshair and counter never get in the way of anything else. It never takes focus or mouse input.
/// </summary>
public sealed class HudService
{
    [StructLayout(LayoutKind.Sequential)] private struct PT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out NativeMethods.RECT r);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h, ref PT p);

    private readonly HudConfig _cfg;
    private readonly CpsCounter _cps = new();
    private Window? _win;
    private Canvas? _canvas;
    private CrosshairVisual? _cross;
    private Border? _cpsBox;
    private TextBlock? _cpsText, _fpsText, _pingText;
    private Border? _fpsBox, _pingBox;
    private readonly FpsMeter _fps = new();
    private readonly PingMeter _ping = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private bool _shown;

    public HudService(HudConfig cfg) => _cfg = cfg;

    public void Start()
    {
        _cfg.PropertyChanged += (_, _) => { _cross?.Update(); Tick(); };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public void Shutdown()
    {
        _timer.Stop();
        _cps.Stop();
        _fps.Stop();
        _ping.Stop();
        try { _win?.Close(); } catch { }
    }

    private void Build()
    {
        _canvas = new Canvas { IsHitTestVisible = false };
        _cross = new CrosshairVisual(_cfg);
        static (Border, TextBlock) Box()
        {
            var t = new TextBlock { Foreground = Brushes.White, FontFamily = new FontFamily("Segoe UI Variable, Segoe UI"), FontSize = 15, FontWeight = FontWeights.SemiBold };
            return (new Border { Background = new SolidColorBrush(Color.FromArgb(0xC8, 0x10, 0x10, 0x14)), CornerRadius = new CornerRadius(5), Padding = new Thickness(10, 4, 10, 4), Child = t }, t);
        }
        (_cpsBox, _cpsText) = Box();
        (_fpsBox, _fpsText) = Box();
        (_pingBox, _pingText) = Box();
        _canvas.Children.Add(_cross);
        _canvas.Children.Add(_cpsBox);
        _canvas.Children.Add(_fpsBox);
        _canvas.Children.Add(_pingBox);
        _win = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize,
            Topmost = true, ShowInTaskbar = false, ShowActivated = false, Focusable = false, Content = _canvas, Title = "Nighty Lite HUD",
        };
        _win.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(_win).Handle;
            long ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE,
                new IntPtr(ex | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TRANSPARENT));
        };
    }

    private void Tick()
    {
        try
        {
            bool wanted = _cfg.CrosshairEnabled || _cfg.CpsEnabled || _cfg.FpsEnabled || _cfg.PingEnabled;
            if (_cfg.CpsEnabled) _cps.Start(); else _cps.Stop();
            if (_cfg.FpsEnabled) _fps.Start(); else _fps.Stop();
            if (_cfg.PingEnabled) _ping.Start(() => _cfg.PingHost); else _ping.Stop();

            var fg = NativeMethods.GetForegroundWindow();
            NativeMethods.RECT r = default;
            bool ok = wanted && Svc.Roblox.IsForeground && fg != IntPtr.Zero && GetClientRect(fg, out r) && r.R - r.L > 50 && r.B - r.T > 50;
            if (!ok)
            {
                if (_shown) { _win!.Hide(); _shown = false; }
                return;
            }
            if (_win == null) Build();
            var pt = new PT { X = 0, Y = 0 };
            ClientToScreen(fg, ref pt);
            int w = r.R - r.L, h = r.B - r.T;

            var hwnd = new WindowInteropHelper(_win!).EnsureHandle();
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, pt.X, pt.Y, w, h, NativeMethods.SWP_NOACTIVATE);
            if (!_shown) { _win!.Show(); _shown = true; NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, pt.X, pt.Y, w, h, NativeMethods.SWP_NOACTIVATE); }

            double scale = VisualTreeHelper.GetDpi(_win!).DpiScaleX;
            double cw = w / scale, ch = h / scale;
            _cross!.Visibility = _cfg.CrosshairEnabled ? Visibility.Visible : Visibility.Collapsed;
            if (_cfg.CrosshairEnabled)
            {
                _cross.Measure(new Size(cw, ch));
                Canvas.SetLeft(_cross, Math.Round(cw / 2 - _cross.DesiredSize.Width / 2));
                Canvas.SetTop(_cross, Math.Round(ch / 2 - _cross.DesiredSize.Height / 2));
            }
            _cpsBox!.Visibility = _cfg.CpsEnabled ? Visibility.Visible : Visibility.Collapsed;
            if (_cfg.CpsEnabled)
            {
                _cpsText!.Text = $"{_cps.Read()} CPS";
                _cpsBox.LayoutTransform = new ScaleTransform(_cfg.CpsScale, _cfg.CpsScale);
                _cpsBox.Measure(new Size(cw, ch));
                var sz = _cpsBox.DesiredSize;
                Canvas.SetLeft(_cpsBox, Math.Clamp((cw - sz.Width) * _cfg.CpsX / 100, 0, Math.Max(0, cw - sz.Width)));
                Canvas.SetTop(_cpsBox, Math.Clamp((ch - sz.Height) * _cfg.CpsY / 100, 0, Math.Max(0, ch - sz.Height)));
            }

            void Place(Border box, TextBlock text, bool on, string label, double px, double py)
            {
                box.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                if (!on) return;
                text.Text = label;
                box.LayoutTransform = new ScaleTransform(_cfg.CpsScale, _cfg.CpsScale);
                box.Measure(new Size(cw, ch));
                var sz = box.DesiredSize;
                Canvas.SetLeft(box, Math.Clamp((cw - sz.Width) * px / 100, 0, Math.Max(0, cw - sz.Width)));
                Canvas.SetTop(box, Math.Clamp((ch - sz.Height) * py / 100, 0, Math.Max(0, ch - sz.Height)));
            }
            Place(_fpsBox!, _fpsText!, _cfg.FpsEnabled, _fps.Fps is double f ? $"{f:0} FPS" : $"FPS: {_fps.Status}", _cfg.FpsX, _cfg.FpsY);
            var (ms, jit, loss) = _ping.Read();
            Place(_pingBox!, _pingText!, _cfg.PingEnabled, ms is int m ? $"{m} ms  \u00B1{jit}{(loss > 0 ? $"  {loss}% loss" : "")}" : "Ping: --", _cfg.PingX, _cfg.PingY);
        }
        catch (Exception ex) { Log.Error("HUD update failed", ex); }
    }
}
