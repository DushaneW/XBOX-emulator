using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using XboxPc.Application;
using XboxPc.Infrastructure;
using XboxPc.Input;
using XboxPc.Runtimes;

namespace XboxPc.DesktopApp;

/// <summary>User interaction the view-models need. Replaceable in tests.</summary>
public interface IDialogService
{
    string? PickFile(string title, string filter);
    string? PickSaveFile(string title, string filter, string defaultName);
    bool Confirm(string title, string message);
    void ShowError(string title, string message);
    void ShowInfo(string title, string message);
    void OpenFolder(string path);
    void OpenFile(string path);
}

public sealed class WpfDialogService : IDialogService
{
    public string? PickFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true, Multiselect = false };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public string? PickSaveFile(string title, string filter, string defaultName)
    {
        var dialog = new SaveFileDialog { Title = title, Filter = filter, FileName = defaultName, OverwritePrompt = true };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public bool Confirm(string title, string message) =>
        MessageBox.Show(Owner!, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    public void ShowError(string title, string message) => MessageBox.Show(Owner!, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public void ShowInfo(string title, string message) => MessageBox.Show(Owner!, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false });
    }

    public void OpenFile(string path)
    {
        if (!File.Exists(path)) { ShowError("File not found", path); return; }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static Window? Owner => System.Windows.Application.Current?.MainWindow;
}

/// <summary>Composition root: owns every service for the lifetime of the app.</summary>
public sealed class AppHost : IDisposable
{
    private AppHost(string dataDirectory, JsonLinesLog log, SqliteDatabase database, IDialogService dialogs, IGamepadSource gamepads)
    {
        DataDirectory = dataDirectory;
        Log = log;
        Database = database;
        Dialogs = dialogs;
        Runtimes = new RuntimeRegistry(BuiltInRuntimes.Create());
        Library = new LibraryService(new SqliteLibraryRepository(database), log);
        Profiles = new ProfileService(new SqliteProfileRepository(database), new SqliteInputMapRepository(database), log);
        GameSettings = new GameSettingsService(new SqliteGameSettingsRepository(database));
        Compatibility = new CompatibilityService(new SqliteCompatibilityRepository(database), log);
        Launcher = new LaunchCoordinator(Runtimes, Library, log);
        Controllers = new ControllerService(gamepads);
        InputProfiles = new XboxPc.Emulation.Input.ProfileStore(Path.Combine(dataDirectory, "input-profiles"));
    }

    public string DataDirectory { get; }
    public string LogDirectory => Path.Combine(DataDirectory, "logs");
    public JsonLinesLog Log { get; }
    public SqliteDatabase Database { get; }
    public IDialogService Dialogs { get; }
    public RuntimeRegistry Runtimes { get; }
    public LibraryService Library { get; }
    public ProfileService Profiles { get; }
    public GameSettingsService GameSettings { get; }
    public CompatibilityService Compatibility { get; }
    public LaunchCoordinator Launcher { get; }
    public ControllerService Controllers { get; }

    /// <summary>Emulator input profiles (global and per game).</summary>
    public XboxPc.Emulation.Input.ProfileStore InputProfiles { get; }

    /// <summary>Decides whether the dashboard or a running emulator receives host input.</summary>
    public XboxPc.Emulation.Input.InputArbiter InputArbiter { get; } = new();

    /// <summary>The emulator session currently running (one at a time), for the Performance screen. Null when no game runs.</summary>
    public XboxPc.Emulation.OriginalXbox.EmulationSession? ActiveSession { get; set; }

    /// <summary>Display name of the title in <see cref="ActiveSession"/>.</summary>
    public string? ActiveTitle { get; set; }

    /// <summary>Which renderer draws the running title (Direct3D 12 with adapter and scale, or the CPU picture and why).</summary>
    public string? ActiveRenderer { get; set; }

    /// <summary>Directory containing bundled sample programs and docs (next to the executable).</summary>
    public static string BaseDirectory => AppContext.BaseDirectory;

    public static async Task<AppHost> CreateAsync(string dataDirectory, IDialogService dialogs, IGamepadSource? gamepads = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(dataDirectory);
        var log = new JsonLinesLog(Path.Combine(dataDirectory, "logs"));
        log.Info("App", $"Starting XBOX PC {typeof(AppHost).Assembly.GetName().Version} on {Environment.OSVersion}.");
        var db = new SqliteDatabase(Path.Combine(dataDirectory, "xboxpc.db"));
        try
        {
            await db.MigrateAsync(log, ct).ConfigureAwait(true);
            var host = new AppHost(dataDirectory, log, db, dialogs, gamepads ?? new XInputGamepadSource());
            await host.Profiles.InitializeAsync(ct).ConfigureAwait(true);
            host.Controllers.Map = await host.Profiles.GetInputMapAsync(ct).ConfigureAwait(true);
            return host;
        }
        catch
        {
            log.Dispose();
            throw;
        }
    }

    public void Dispose() => Log.Dispose();
}
