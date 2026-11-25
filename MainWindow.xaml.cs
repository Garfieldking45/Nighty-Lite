using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Nighty.Models;
using Nighty.Native;
using Nighty.Services;

namespace Nighty;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _manualRun;      // started from the button, so hold mode does not need the key held
    private bool _capturing;
    private Button? _capTarget;

    // ---- Overlay: visible only while the GUI key (Tab) is held over Roblox, and for a few seconds at start-up so you know it is running.
    private readonly DispatcherTimer _overlayTimer = new() { Interval = TimeSpan.FromMilliseconds(25) };
    private bool _keyDown, _keyOnRoblox, _shown;
    private long _hintUntil = Environment.TickCount64 + 6000;

    public MainWindow()
    {
        InitializeComponent();
        // Never take focus and stay out of Alt-Tab, so Roblox keeps the keyboard (and the GUI key stays held for it).
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            long ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW));
        };
        StatusLine.Text = $"Hold {Hotkeys.KeyName(Svc.S.OverlayVk)} over Roblox to open. Drag a panel header to move it.";
        BuildSwatches();
        ApplyBack((Color)ColorConverter.ConvertFromString(Svc.S.BackColor), save: false);
        ApplyTheme(Svc.S.ThemeHue, save: false);
        LoadPositions();
        _overlayTimer.Tick += (_, _) => UpdateOverlay();
        _overlayTimer.Start();
        CheckForUpdate();
        DataContext = Svc.S;
        var cl = Svc.S.Clicker;
        var bow = Svc.S.Crossbow;

        // ---- Auto Clicker wiring
        Svc.Clicker.KeepClicking = () => _manualRun || cl.Mode != ActivationMode.Hold || HotkeyService.IsDown((cl.HotkeyVk, cl.HotkeyMods));
        Svc.Hotkeys.Register("clicker", () => cl.Enabled ? (cl.HotkeyVk, cl.HotkeyMods) : (0, 0), down =>
        {
            if (cl.Mode == ActivationMode.Toggle)
            {
                if (!down) return;
                if (Svc.Clicker.IsClicking) Svc.Clicker.Stop(); else Svc.Clicker.Start();
            }
            else if (down) Svc.Clicker.Start(); else Svc.Clicker.Stop();
        });
        cl.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ClickerSettings.Enabled) && !cl.Enabled) Svc.Clicker.Stop();
            if (e.PropertyName == nameof(ClickerSettings.Mode)) Svc.Clicker.Stop();
            Refresh();
        };
        Svc.Clicker.StateChanged += () => Dispatcher.BeginInvoke(Refresh);
        Svc.Clicker.AutoStopped += reason => Dispatcher.BeginInvoke(() => StatusLine.Text = reason);
        Svc.Clicker.Blocked += () => Dispatcher.BeginInvoke(() =>
            StatusLine.Text = "Windows blocked the clicks. The game may be running as administrator: run Nighty Lite as administrator too.");

        // ---- Auto Crossbow wiring
        Svc.Hotkeys.Register("crossbow", () => bow.Enabled ? (bow.HotkeyVk, bow.HotkeyMods) : (0, 0), down =>
        {
            switch (bow.Style)
            {
                case CrossbowStyle.Hold: if (down) Svc.Crossbow.Start(bow); else Svc.Crossbow.Stop(); break;
                case CrossbowStyle.Toggle: if (down) Svc.Crossbow.Toggle(bow); break;
                default: if (down) Svc.Crossbow.Start(bow); break;
            }
        });
        bow.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CrossbowConfig.Enabled) && !bow.Enabled) Svc.Crossbow.Stop();
            if (e.PropertyName == nameof(CrossbowConfig.Style)) Svc.Crossbow.Stop();
            Refresh();
        };
        Svc.Crossbow.Changed += () => Dispatcher.BeginInvoke(Refresh);
        Svc.Crossbow.Notice += msg => Dispatcher.BeginInvoke(() => StatusLine.Text = msg);

        // ---- Auto Tool wiring
        var tool = Svc.S.AutoTool;
        Svc.Hotkeys.Register("teach", () => tool.Enabled ? (tool.TeachVk, tool.TeachMods) : (0, 0), down => { if (down) Svc.AutoTool.Teach(); });
        tool.PropertyChanged += (_, _) => Refresh();
        Svc.AutoTool.Notice += msg => Dispatcher.BeginInvoke(() => { StatusLine.Text = msg; Refresh(); });

        // ---- Emergency stop
        Svc.Hotkeys.Register("stop", () => (Svc.S.StopHotkeyVk, Svc.S.StopHotkeyMods), down =>
        {
            if (!down) return;
            Svc.StopAllInput();
            StatusLine.Text = "Everything stopped.";
        });

        // ---- Boost FPS wiring
        Svc.Boost.StatusChanged += () => Dispatcher.BeginInvoke(Refresh);

        _timer.Tick += (_, _) => { if (!Svc.Clicker.IsClicking) _manualRun = false; Refresh(); };
        _timer.Start();
        Refresh();
    }

    private void Refresh()
    {
        var cl = Svc.S.Clicker;
        var bow = Svc.S.Crossbow;

        bool clicking = Svc.Clicker.IsClicking;
        ClickerState.Text = clicking ? $"CLICKING {Svc.Clicker.MeasuredCps:0} CPS" : cl.Enabled ? "ARMED" : "OFF";
        ClickerStart.Content = clicking ? "Stop clicking" : "Start clicking";
        ClickerHint.Text = cl.HotkeyVk <= 0 ? "No hotkey set."
            : cl.Mode == ActivationMode.Hold ? $"Hold {cl.KeyText} to click; release to stop." : $"Press {cl.KeyText} to start and stop.";
        SetKey(ClickerKey, cl.KeyText);
        ClickerMode.Content = cl.Mode == ActivationMode.Hold ? "Hold" : "Toggle";
        ClickerButton.Content = cl.Button.ToString();

        bool running = Svc.Crossbow.IsRunning;
        BowState.Text = running ? "RUNNING" : bow.Enabled ? "READY" : "OFF";
        BowStart.Content = running ? "Stop crossbow" : "Start crossbow";
        SetKey(BowKey, bow.KeyText);
        BowMode.Content = bow.Style.ToString();
        PingHostBtn.Content = Svc.S.Hud.PingHost;
        HudStyle.Content = Svc.S.Hud.Style switch { CrosshairStyle.Cross => "Cross", CrosshairStyle.CrossDot => "Cross + dot", CrosshairStyle.Dot => "Dot", _ => "Circle" };
        HudColor.Content = HudConfig.Colors.FirstOrDefault(c => c.Hex.Equals(Svc.S.Hud.Color, StringComparison.OrdinalIgnoreCase)).Name ?? Svc.S.Hud.Color;
        var at = Svc.S.AutoTool;
        ToolState.Text = !at.Enabled ? "OFF" : "READY";
        SetKey(TeachKey, at.TeachText);
        ToolLearned.Text = $"Taught: {at.PickSamples.Count} pickaxe, {at.AxeSamples.Count} axe, {at.ShearsSamples.Count} shears blocks. Common blocks are built in.";
        BoostState.Text = Svc.S.Boost.Enabled ? "ON" : "OFF";
        BoostInfo.Text = Svc.Boost.Status;
        SetKey(StopKey, Hotkeys.Format(Svc.S.StopHotkeyVk, Svc.S.StopHotkeyMods));
        SetKey(GuiKey, Hotkeys.KeyName(Svc.S.OverlayVk));
    }

    private void SetKey(Button b, string text) => b.Content = _capturing && ReferenceEquals(_capTarget, b) ? "Press a key..." : text;

    // ------------------------------------------------------------------ show / hide
    private void UpdateOverlay()
    {
        int vk = Svc.S.OverlayVk;
        bool down = vk > 0 && (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;
        if (down && !_keyDown) _keyOnRoblox = Svc.Roblox.IsForeground;   // only react to the key pressed inside Roblox
        _keyDown = down;
        bool want = (down && _keyOnRoblox) || _capturing || Environment.TickCount64 < _hintUntil;
        if (want == _shown) return;
        _shown = want;
        AutoToolService.OverlayShown = want;
        if (want) { Place(); Show(); Place(); }
        else Hide();
    }

    /// <summary>Covers the Roblox window (or the primary screen) with the transparent overlay, above the game.</summary>
    private void Place()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int x = 0, y = 0, w = (int)Math.Round(SystemParameters.PrimaryScreenWidth * scale), h = (int)Math.Round(SystemParameters.PrimaryScreenHeight * scale);
        var fg = NativeMethods.GetForegroundWindow();
        if (fg != IntPtr.Zero && _keyOnRoblox && NativeMethods.GetWindowRect(fg, out var r) && r.R > r.L && r.B > r.T)
        { x = r.L; y = r.T; w = r.R - r.L; h = r.B - r.T; }
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x, y, w, h, NativeMethods.SWP_NOACTIVATE);
    }

    // ------------------------------------------------------------------ dragging panels by their header
    private string _dragName = "";
    private FrameworkElement? _dragPanel;
    private Point _dragOrigin, _dragStart;
    private bool _dragMoved;

    private FrameworkElement PanelFor(string name) => name switch { "host" => HostPanel, "combat" => CombatPanel, "utility" => UtilityPanel, "render" => RenderPanel, _ => SettingsPanel };

    private void Head_Down(object sender, MouseButtonEventArgs e)
    {
        var head = (Border)sender;
        _dragName = (string)head.Tag;
        _dragPanel = PanelFor(_dragName);
        _dragOrigin = e.GetPosition(Overlay);
        _dragStart = new Point(Canvas.GetLeft(_dragPanel), Canvas.GetTop(_dragPanel));
        _dragMoved = false;
        head.CaptureMouse();
        e.Handled = true;
    }

    private void Head_Move(object sender, MouseEventArgs e)
    {
        var head = (Border)sender;
        if (!head.IsMouseCaptured || _dragPanel == null) return;
        var p = e.GetPosition(Overlay);
        double dx = p.X - _dragOrigin.X, dy = p.Y - _dragOrigin.Y;
        if (!_dragMoved && Math.Abs(dx) + Math.Abs(dy) < 4) return;   // a click, not a drag
        _dragMoved = true;
        Canvas.SetLeft(_dragPanel, Math.Clamp(_dragStart.X + dx, 0, Math.Max(0, Overlay.ActualWidth - 60)));
        Canvas.SetTop(_dragPanel, Math.Clamp(_dragStart.Y + dy, 0, Math.Max(0, Overlay.ActualHeight - 30)));
    }

    private void Head_Up(object sender, MouseButtonEventArgs e)
    {
        var head = (Border)sender;
        if (!head.IsMouseCaptured) return;
        head.ReleaseMouseCapture();
        if (_dragMoved) SavePositions();
        else if (_dragName == "combat") Toggle(CombatBody, CombatChevron);   // a plain click on Combat folds it
        e.Handled = true;
    }

    private static readonly (string Name, double X, double Y)[] DefaultPos = { ("host", 16, 16), ("combat", 232, 16), ("utility", 484, 16), ("render", 740, 16), ("settings", 996, 16) };

    private void LoadPositions()
    {
        foreach (var (name, dx, dy) in DefaultPos)
        {
            var el = PanelFor(name);
            if (name == "settings" && Svc.S.PanelPos.TryGetValue(name, out var old) && old.Length == 2 && old[0] == 484 && old[1] == 16)
                Svc.S.PanelPos.Remove(name);   // the old default spot is now the Utility panel's
            if (Svc.S.PanelPos.TryGetValue(name, out var pos) && pos.Length == 2) { Canvas.SetLeft(el, pos[0]); Canvas.SetTop(el, pos[1]); }
            else { Canvas.SetLeft(el, dx); Canvas.SetTop(el, dy); }
        }
    }

    private void SavePositions()
    {
        foreach (var (name, _, _) in DefaultPos)
        {
            var el = PanelFor(name);
            Svc.S.PanelPos[name] = new[] { Canvas.GetLeft(el), Canvas.GetTop(el) };
        }
        Svc.Settings.Save();
    }

    private void ResetPos_Click(object sender, RoutedEventArgs e)
    {
        Svc.S.PanelPos.Clear();
        LoadPositions();
        Svc.Settings.Save();
    }

    private void Toggle(UIElement body, TextBlock chevron)
    {
        bool open = body.Visibility != Visibility.Visible;
        body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        chevron.Text = open ? "" : "";
    }

    private void CloseSettings_Click(object sender, RoutedEventArgs e) => SettingsTog.IsChecked = false;
    private void Quit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    // ------------------------------------------------------------------ updates (GitHub releases)
    private readonly UpdateService _updater = new();
    private bool _updating;

    private async void CheckForUpdate()
    {
        var rel = await _updater.CheckAsync();
        if (rel is null) return;
        UpdateBtn.Content = $"Update to {rel.Tag}";
        UpdateBtn.Visibility = Visibility.Visible;
        StatusLine.Text = $"Update {rel.Tag} is available. Open Settings and press the update button.";
        _hintUntil = Math.Max(_hintUntil, Environment.TickCount64 + 6000);
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_updating || _updater.Available is not { } rel) return;
        _updating = true;
        UpdateBtn.IsEnabled = false;
        var progress = new Progress<double>(p => UpdateBtn.Content = $"Downloading... {p:P0}");
        string? error = await _updater.InstallAsync(rel, progress);
        if (error is null) { Application.Current.Shutdown(); return; }
        StatusLine.Text = error;
        UpdateBtn.Content = $"Update to {rel.Tag}";
        UpdateBtn.IsEnabled = true;
        _updating = false;
    }

    // ------------------------------------------------------------------ theme: hue slider + panel colour
    private bool _hueDrag;

    private static Color FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = (int)(h / 60) switch
        {
            0 => (c, x, 0.0), 1 => (x, c, 0.0), 2 => (0.0, c, x), 3 => (0.0, x, c), 4 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    private void ApplyTheme(double hue, bool save)
    {
        hue = Math.Clamp(hue, 0, 1);
        var c = FromHsv(hue * 360, 0.96, 0.55);
        var brush = new SolidColorBrush(c);
        Resources["Theme"] = brush;
        Application.Current.Resources["Accent"] = brush;   // the Start buttons
        HexText.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        PlaceThumb(hue);
        if (!save) return;
        Svc.S.ThemeHue = hue;
        Svc.Settings.Save();
    }

    private void PlaceThumb(double hue)
    {
        double room = Math.Max(0, HueHost.ActualWidth - HueThumb.Width);
        HueThumb.Margin = new Thickness(hue * room, 0, 0, 0);
    }

    private void SetHueFromMouse(MouseEventArgs e, bool save)
    {
        double room = Math.Max(1, HueHost.ActualWidth - HueThumb.Width);
        double x = e.GetPosition(HueHost).X - HueThumb.Width / 2;
        ApplyTheme(x / room, save);
    }

    private void Hue_Down(object sender, MouseButtonEventArgs e) { _hueDrag = HueHost.CaptureMouse(); SetHueFromMouse(e, save: false); e.Handled = true; }
    private void Hue_Move(object sender, MouseEventArgs e) { if (_hueDrag) SetHueFromMouse(e, save: false); }
    private void Hue_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_hueDrag) return;
        _hueDrag = false;
        HueHost.ReleaseMouseCapture();
        SetHueFromMouse(e, save: true);
    }
    private void Hue_SizeChanged(object sender, SizeChangedEventArgs e) => PlaceThumb(Svc.S.ThemeHue);

    private static readonly string[] Presets = { "#1B1B1B", "#0B0B0D", "#17203A", "#241636", "#14281E", "#2E1717", "#10262B", "#34343A" };

    private void BuildSwatches()
    {
        foreach (var hex in Presets)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            var sw = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 0, 8, 6),
                Background = new SolidColorBrush(c), BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, 255, 255, 255)),
                BorderThickness = new Thickness(1), Cursor = Cursors.Hand, ToolTip = hex,
            };
            sw.MouseLeftButtonUp += (_, _) => ApplyBack(c, save: true);
            SwatchPanel.Children.Add(sw);
        }
    }

    /// <summary>Re-tints every panel from one base colour.</summary>
    private void ApplyBack(Color c, bool save)
    {
        static Color Mul(Color k, double f, byte a = 0xFF) => Color.FromArgb(a, (byte)Math.Clamp(k.R * f, 0, 255), (byte)Math.Clamp(k.G * f, 0, 255), (byte)Math.Clamp(k.B * f, 0, 255));
        static Color Add(Color k, int d) => Color.FromRgb((byte)Math.Clamp(k.R + d, 0, 255), (byte)Math.Clamp(k.G + d, 0, 255), (byte)Math.Clamp(k.B + d, 0, 255));
        Resources["Panel"] = new SolidColorBrush(Mul(c, 1.0, 0xFA));
        Resources["Head"] = new SolidColorBrush(Mul(c, 0.85, 0xFA));
        Resources["RowBg"] = new SolidColorBrush(Mul(c, 1.0));
        Resources["RowHover"] = new SolidColorBrush(Add(c, 14));
        Resources["SubBg"] = new SolidColorBrush(Mul(c, 0.68));
        if (!save) return;
        Svc.S.BackColor = c.ToString();   // #AARRGGBB, which ColorConverter reads back
        Svc.Settings.Save();
    }

    // ------------------------------------------------------------------ buttons
    private void ClickerStart_Click(object sender, RoutedEventArgs e)
    {
        if (Svc.Clicker.IsClicking) { Svc.Clicker.Stop(); return; }
        _manualRun = true;
        Svc.Clicker.Start();
        StatusLine.Text = "Clicker started. Let go of the GUI key to get back to the game.";
    }

    private void BowStart_Click(object sender, RoutedEventArgs e)
    {
        if (Svc.Crossbow.IsRunning) { Svc.Crossbow.Stop(); return; }
        StatusLine.Text = "Use the Auto Crossbow hotkey in the game to start it.";
    }

    private static readonly string[] PingHosts = { "1.1.1.1", "8.8.8.8", "9.9.9.9" };
    private void PingHost_Click(object sender, RoutedEventArgs e)
    {
        var h = Svc.S.Hud;
        int i = Array.IndexOf(PingHosts, h.PingHost);
        h.PingHost = PingHosts[(i + 1) % PingHosts.Length];
        Refresh();
    }

    private async void NetApply_Click(object sender, RoutedEventArgs e)
    {
        StatusLine.Text = "Approve the Windows prompt to apply the network tune...";
        StatusLine.Text = await NetworkTune.ApplyAsync() ? "Network tune applied (DNS flushed, Roblox marked high priority)." : "Network tune was cancelled or failed.";
    }

    private async void NetRemove_Click(object sender, RoutedEventArgs e)
    {
        StatusLine.Text = "Approve the Windows prompt to remove it...";
        StatusLine.Text = await NetworkTune.RemoveAsync() ? "Network tune removed." : "Nothing removed (cancelled, or it was not set).";
    }

    private void HudStyle_Click(object sender, RoutedEventArgs e)
    {
        var h = Svc.S.Hud;
        h.Style = (CrosshairStyle)(((int)h.Style + 1) % 4);
        Refresh();
    }

    private void HudColor_Click(object sender, RoutedEventArgs e)
    {
        var h = Svc.S.Hud;
        int i = Array.FindIndex(HudConfig.Colors, c => c.Hex.Equals(h.Color, StringComparison.OrdinalIgnoreCase));
        h.Color = HudConfig.Colors[(i + 1) % HudConfig.Colors.Length].Hex;
        Refresh();
    }

    private void ClickerMode_Click(object sender, RoutedEventArgs e)
    {
        var cl = Svc.S.Clicker;
        cl.Mode = cl.Mode == ActivationMode.Toggle ? ActivationMode.Hold : ActivationMode.Toggle;
    }

    private void ClickerButton_Click(object sender, RoutedEventArgs e)
    {
        var cl = Svc.S.Clicker;
        cl.Button = (ClickButton)(((int)cl.Button + 1) % 3);
    }

    private void BowMode_Click(object sender, RoutedEventArgs e)
    {
        var bow = Svc.S.Crossbow;
        bow.Style = (CrossbowStyle)(((int)bow.Style + 1) % 3);
    }

    private void TeachKey_Click(object sender, RoutedEventArgs e) => Capture(TeachKey, (vk, mods) => { Svc.S.AutoTool.TeachVk = vk; Svc.S.AutoTool.TeachMods = mods; });
    private void ToolForget_Click(object sender, RoutedEventArgs e)
    {
        Svc.AutoTool.ForgetAll();
        StatusLine.Text = "Auto Tool forgot every taught block.";
        Refresh();
    }

    private void ClickerKey_Click(object sender, RoutedEventArgs e) => Capture(ClickerKey, (vk, mods) => { Svc.S.Clicker.HotkeyVk = vk; Svc.S.Clicker.HotkeyMods = mods; });
    private void BowKey_Click(object sender, RoutedEventArgs e) => Capture(BowKey, (vk, mods) => { Svc.S.Crossbow.HotkeyVk = vk; Svc.S.Crossbow.HotkeyMods = mods; });
    private void StopKey_Click(object sender, RoutedEventArgs e) => Capture(StopKey, (vk, mods) => { Svc.S.StopHotkeyVk = vk; Svc.S.StopHotkeyMods = mods; Svc.Settings.Save(); });
    private void GuiKey_Click(object sender, RoutedEventArgs e) => Capture(GuiKey, (vk, _) =>
    {
        Svc.S.OverlayVk = vk;
        Svc.Settings.Save();
        StatusLine.Text = $"Hold {Hotkeys.KeyName(vk)} over Roblox to open.";
    }, ignoreVk: Svc.S.OverlayVk);

    /// <summary>Waits for the next key or side/middle mouse button (Esc cancels) and hands it back with the modifiers held.
    /// <paramref name="ignoreVk"/> is skipped until it has been let go once (the GUI key is still held when you click Rebind).</summary>
    private void Capture(Button target, Action<int, int> apply, int ignoreVk = 0)
    {
        if (_capturing) return;
        _capturing = true;
        _capTarget = target;
        Svc.Hotkeys.Suspended = true;
        target.Content = "Press a key...";
        var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        long started = Environment.TickCount64;
        bool ignoreReleased = ignoreVk <= 0;
        poll.Tick += (_, _) =>
        {
            if (Environment.TickCount64 - started < 150) return;   // let the click that opened this go
            if (!ignoreReleased && (NativeMethods.GetAsyncKeyState(ignoreVk) & 0x8000) == 0) ignoreReleased = true;
            for (int vk = 0x03; vk <= 0xFE; vk++)
            {
                if (vk == 0x01 || vk == 0x02) continue;   // left / right click stay clicks
                if (vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5)) continue;   // modifiers alone are not a hotkey
                if (!ignoreReleased && vk == ignoreVk) continue;
                if ((NativeMethods.GetAsyncKeyState(vk) & 0x8000) == 0) continue;
                poll.Stop();
                if (vk != 0x1B)
                {
                    int mods = 0;
                    if ((NativeMethods.GetAsyncKeyState(0x11) & 0x8000) != 0) mods |= Hotkeys.Ctrl;
                    if ((NativeMethods.GetAsyncKeyState(0x12) & 0x8000) != 0) mods |= Hotkeys.Alt;
                    if ((NativeMethods.GetAsyncKeyState(0x10) & 0x8000) != 0) mods |= Hotkeys.Shift;
                    apply(vk, mods);
                }
                // Hold off re-arming the bindings until the key is let go, so picking a key does not also fire it.
                var release = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
                release.Tick += (_, _) =>
                {
                    if ((NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0) return;
                    release.Stop();
                    _capturing = false;
                    _capTarget = null;
                    Svc.Hotkeys.Suspended = false;
                    Refresh();
                };
                release.Start();
                Refresh();
                return;
            }
        };
        poll.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _overlayTimer.Stop();
        Svc.StopAllInput();
        Svc.Extras.Shutdown();
        Svc.AutoTool.Shutdown();
        Svc.Hud.Shutdown();
        Svc.Boost.Shutdown();
        Svc.Hotkeys.Stop();
        Svc.Settings.Save();
        base.OnClosed(e);
    }
}
