using System.IO;
using System.Windows;
using System.Windows.Threading;
using XboxPc.Application;
using XboxPc.DesktopApp.ViewModels;
using XboxPc.Domain;
using XboxPc.Infrastructure;
using XboxPc.Input;

namespace XboxPc.DesktopApp;

public partial class App : System.Windows.Application
{
    private AppHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => WriteCrashFile(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { _host?.Log.Error("App", "Unobserved task exception.", args.Exception); args.SetObserved(); };

        // Options: --data-dir <dir> (separate data folder), --run <file.xbe> (open the emulator window for that XBE at startup),
        // --no-intro (skip the start-up animation for this launch).
        var dataDir = Arg(e.Args, "--data-dir") ?? AppPaths.DataDirectory;
        var runXbe = Arg(e.Args, "--run");
        var noIntro = Array.IndexOf(e.Args, "--no-intro") >= 0;
        var dialogs = new WpfDialogService();

        // Stage 1: black stage. The real start-up work starts at once behind it.
        var splash = new SplashWindow();
        splash.Show();
        splash.SetStatus("Opening your data");
        try
        {
            _host = await AppHost.CreateAsync(dataDir, dialogs).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // Crash-safe startup: any failure is shown to the user with a recovery hint.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            WriteCrashFile(ex);
            splash.OpenFolder = () => dialogs.OpenFolder(dataDir);
            await splash.ShowFailureAsync(LogRedactor.Redact(ex.Message),
                $"Data folder: {LogRedactor.Redact(dataDir)}. Your data was not deleted. If the database is damaged, move xboxpc.db out of the data folder and start again.").ConfigureAwait(true);
            splash.Close();
            Shutdown(1);
            return;
        }

        var host = _host;
        var prefs = host.Profiles.Active?.Preferences ?? new UserPreferences();
        var reduced = prefs.ReduceMotion || !SystemParameters.ClientAreaAnimation;
        splash.SkipPressed = () => Enumerable.Range(0, host.Controllers.Source.SlotCount)
            .Any(i => host.Controllers.Source.Read(i) is { } r && (r.Buttons & (PadButtons.A | PadButtons.Start | PadButtons.B)) != 0);

        // Stages 2-3: logo and the original start-up sound (each can be turned off in Settings > General).
        var intro = Task.CompletedTask;
        if (prefs.StartupAnimation && !noIntro && runXbe is null)
        {
            StartupSound? sound = null;
            if (prefs.StartupSound && prefs.Volume > 0)
            {
                try { sound = new StartupSound(prefs.Volume); }
                catch (InvalidOperationException ex) { host.Log.Warn("App", "Start-up sound unavailable: " + ex.Message); }
            }
            intro = splash.PlayIntroAsync(sound, reduced);
        }

        // Stage 4: the dashboard loads while the logo plays.
        splash.SetStatus("Loading your library");
        var main = new MainViewModel(host);
        var window = new MainWindow(main, host);
        await main.InitializeAsync().ConfigureAwait(true);
        splash.WorkFinished();
        await intro.ConfigureAwait(true);

        // Stage 5: hand over to the dashboard.
        await splash.FadeOutAsync(reduced).ConfigureAwait(true);
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
        splash.Close();
        if (runXbe is not null) main.RunXbe(runXbe);
    }

    private static string? Arg(string[] args, string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Log.Info("App", $"Exiting with code {e.ApplicationExitCode}.");
        _host?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _host?.Log.Error("App", "Unhandled UI exception.", e.Exception);
        WriteCrashFile(e.Exception);
        MessageBox.Show($"An unexpected error occurred and was logged:\n\n{LogRedactor.Redact(e.Exception.Message)}", "XBOX PC", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void WriteCrashFile(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);
            File.AppendAllText(Path.Combine(AppPaths.LogDirectory, "crash.log"),
                $"{DateTimeOffset.UtcNow:O} {LogRedactor.Redact(ex.ToString())}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
