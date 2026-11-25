using System.Windows;
using System.Windows.Threading;
using Nighty.Services;

namespace Nighty;

public partial class App : Application
{
    private Mutex? _single;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _single = new Mutex(true, Environment.GetEnvironmentVariable("NIGHTY_LITE_DATA") is { Length: > 0 } ? "NightyLite.SingleInstance.Dev" : "NightyLite.SingleInstance", out bool created);
        _ownsMutex = created;
        if (!created)
        {
            MessageBox.Show("Nighty Lite is already running.", "Nighty Lite", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, a) =>
        {
            Log.Error("UI exception", a.Exception);
            a.Handled = true;
            MessageBox.Show(a.Exception.Message + "\n\nDetails were written to " + AppPaths.Logs, "Nighty Lite", MessageBoxButton.OK, MessageBoxImage.Warning);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Error("Unhandled exception", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { Log.Error("Unobserved task exception", a.Exception); a.SetObserved(); };

        try
        {
            AppPaths.EnsureCreated();
            Svc.Init();
            var window = new MainWindow();
            MainWindow = window;
            Svc.Hotkeys.Start();
            // The overlay shows itself while Tab is held (see MainWindow.UpdateOverlay).
            // Warm up the timing code so the first real click does not pay for JIT.
            _ = Task.Run(Services.ClickerService.Prewarm);
        }
        catch (Exception ex)
        {
            Log.Error("Start-up failed", ex);
            MessageBox.Show("Nighty Lite could not start:\n\n" + ex.Message + "\n\nDetails were written to " + AppPaths.Logs, "Nighty Lite", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex) _single?.ReleaseMutex();
        base.OnExit(e);
    }
}
