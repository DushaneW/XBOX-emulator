using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using XboxPc.Application;
using XboxPc.Domain;

namespace XboxPc.DesktopApp.ViewModels;

public sealed class PageItem(string key, string title, string glyph, ObservableObject viewModel) : ObservableObject
{
    private bool _isSelected;

    public string Key { get; } = key;
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;
    public ObservableObject ViewModel { get; } = viewModel;

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

/// <summary>What the emulator window needs to present a title: the program, and the library's name and artwork for the launch transition.</summary>
public sealed record EmulatorLaunch(string XbePath, string? DisplayName = null, string? CoverPath = null);

public sealed class MainViewModel : ObservableObject
{
    private PageItem _currentPage;
    private string _statusMessage = "";
    private string _frameStatsText = "";
    private string _controllerStatus = "No controller";
    private string _profileName = "";
    private string _clockText = "";
    private string? _backdropPath;

    public MainViewModel(AppHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        Host = host;
        Home = new HomeViewModel(this);
        Library = new LibraryViewModel(this);
        Recent = new RecentViewModel(this);
        Emulator = new EmulatorViewModel(this);
        Performance = new PerformanceViewModel(this);
        Settings = new SettingsViewModel(this);
        Pages =
        [
            new PageItem("home", "Home", "", Home),
            new PageItem("library", "My Games", "", Library),
            new PageItem("recent", "Recently Played", "", Recent),
            new PageItem("emulator", "Emulator", "", Emulator),
            new PageItem("performance", "Performance", "", Performance),
            new PageItem("settings", "Settings", "", Settings),
        ];
        _currentPage = Pages[0];
        _currentPage.IsSelected = true;
        NavigateCommand = new RelayCommand<string>(key => Navigate(key ?? "home"));
        PlayCommand = new AsyncRelayCommand<GameTile>(t => t is null ? Task.CompletedTask : PlayAsync(t.Id));
        host.Profiles.ActiveProfileChanged += (_, p) => ProfileName = p.DisplayName;
        host.Controllers.Connected += (_, _) => UpdateControllerStatus();
        host.Controllers.Disconnected += (_, _) => UpdateControllerStatus();
        ProfileName = host.Profiles.Active?.DisplayName ?? "";
        UpdateControllerStatus();
        UpdateClock();
    }

    public AppHost Host { get; }
    public HomeViewModel Home { get; }
    public LibraryViewModel Library { get; }
    public RecentViewModel Recent { get; }
    public EmulatorViewModel Emulator { get; }
    public SettingsViewModel Settings { get; }
    public PerformanceViewModel Performance { get; }
    public ObservableCollection<PageItem> Pages { get; }
    public ICommand NavigateCommand { get; }

    /// <summary>Plays a library title: Original Xbox .xbe titles open the emulator; other runnable entries launch on their runtime.</summary>
    public ICommand PlayCommand { get; }

    public event EventHandler? PageChanged;
    public event EventHandler<UserPreferences>? PreferencesApplied;

    public PageItem CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (_currentPage == value) return;
            _currentPage.IsSelected = false;
            Set(ref _currentPage, value);
            value.IsSelected = true;
            PageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }
    public string FrameStatsText { get => _frameStatsText; set => Set(ref _frameStatsText, value); }
    public string ControllerStatus { get => _controllerStatus; private set => Set(ref _controllerStatus, value); }
    public bool ControllerConnected => Host.Controllers.Slots.Any(s => s.Connected);
    public string ProfileName { get => _profileName; private set { Set(ref _profileName, value); OnPropertyChanged(nameof(ProfileInitial)); } }
    public string ProfileInitial => ProfileName.Length > 0 ? char.ToUpperInvariant(ProfileName[0]).ToString() : "?";
    public string ClockText { get => _clockText; private set => Set(ref _clockText, value); }

    /// <summary>Artwork behind the dashboard (the featured title's cover), or null for the plain dark stage.</summary>
    public string? BackdropPath { get => _backdropPath; set => Set(ref _backdropPath, value); }

    public void UpdateClock() => ClockText = DateTime.Now.ToString("t", CultureInfo.CurrentCulture);

    /// <summary>Set by the view: shows the report dialog and returns true when a report was saved.</summary>
    public Func<ReportEditorViewModel, bool?>? ShowReportDialog { get; set; }

    /// <summary>Set by the view: opens an emulator window.</summary>
    public Action<EmulatorLaunch>? OpenEmulator { get; set; }

    /// <summary>Set by the view: opens the emulator input settings (global when the title ID is null).</summary>
    public Action<uint?, string?>? OpenInputSettings { get; set; }

    /// <summary>Asks for an .xbe file and runs it in the Original Xbox emulator core (experimental).</summary>
    public void RunXbe() => RunXbe(null);

    /// <summary>Runs the given XBE, or asks for one when <paramref name="path"/> is null.</summary>
    public void RunXbe(string? path)
    {
        path ??= Host.Dialogs.PickFile("Run an Original Xbox program", "Xbox executable (*.xbe)|*.xbe");
        if (path is null) return;
        Open(new EmulatorLaunch(path));
    }

    private void Open(EmulatorLaunch launch)
    {
        try { OpenEmulator?.Invoke(launch); }
        catch (Exception ex) when (ex is XboxPc.Emulation.OriginalXbox.XbeFormatException or XboxPc.Emulation.OriginalXbox.MachineStopException or IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            Host.Dialogs.ShowError("This program can't start", LogRedactor.Redact(ex.Message));
        }
    }

    /// <summary>True for entries the built-in emulator plays: Original Xbox with an .xbe program file.</summary>
    public static bool IsEmulatorTitle(GameEntry e) =>
        e is { Generation: ConsoleGeneration.OriginalXbox, ContentPath: { } p } && p.EndsWith(".xbe", StringComparison.OrdinalIgnoreCase);

    /// <summary>Why <paramref name="e"/> cannot be played, or null when Play works.</summary>
    public static string? PlayBlockReason(AppHost host, GameEntry e)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(e);
        if (IsEmulatorTitle(e)) return File.Exists(e.ContentPath) ? null : "The program file is missing. Edit the entry to point at it again.";
        return LaunchCoordinator.CheckLaunchable(host.Runtimes.Resolve(e.Generation, null), e);
    }

    public async Task PlayAsync(Guid id)
    {
        await RunSafeAsync(async () =>
        {
            var e = await Host.Library.GetAsync(id).ConfigureAwait(true);
            if (e is null) return;
            if (PlayBlockReason(Host, e) is { } reason)
            {
                Host.Dialogs.ShowError($"{e.DisplayName} can't start", reason);
                return;
            }
            if (IsEmulatorTitle(e))
            {
                await Host.Library.MarkLaunchedAsync(e.Id, DateTimeOffset.UtcNow).ConfigureAwait(true);
                Open(new EmulatorLaunch(e.ContentPath!, e.DisplayName, e.CoverArtPath));
                await Home.RefreshAsync().ConfigureAwait(true);
                return;
            }
            // Other runtimes report their result on the game page.
            await OpenInLibraryAsync(id).ConfigureAwait(true);
            if (Library.Editor?.LaunchCommand is { } launch && launch.CanExecute(null)) launch.Execute(null);
        }).ConfigureAwait(true);
    }

    public UserPreferences Preferences => Host.Profiles.Active?.Preferences ?? new UserPreferences();

    public async Task InitializeAsync()
    {
        await RunSafeAsync(async () =>
        {
            await Home.RefreshAsync().ConfigureAwait(true);
            await Library.RefreshAsync().ConfigureAwait(true);
            await Settings.LoadAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
        PreferencesApplied?.Invoke(this, Preferences);
    }

    public void Navigate(string key)
    {
        var page = Pages.FirstOrDefault(p => p.Key == key) ?? Pages[0];
        CurrentPage = page;
        if (page.ViewModel == Library) BackdropPath = Library.Editor?.CoverPreview;
        else if (page.ViewModel != Home) BackdropPath = null;
        if (page.ViewModel == Home) _ = RunSafeAsync(Home.RefreshAsync);
        if (page.ViewModel == Recent) _ = RunSafeAsync(Recent.RefreshAsync);
        if (page.ViewModel == Emulator) Emulator.Refresh();
    }

    public void NavigateRelative(int delta)
    {
        var index = Pages.IndexOf(CurrentPage);
        var next = ((index + delta) % Pages.Count + Pages.Count) % Pages.Count;
        Navigate(Pages[next].Key);
    }

    /// <summary>B / Esc: close the open game page, otherwise return Home. Returns false when there is nothing to go back from.</summary>
    public bool GoBack()
    {
        if (CurrentPage.ViewModel == Library && Library.IsDetailOpen) { Library.CloseDetail(); return true; }
        if (CurrentPage.ViewModel == Home) return false;
        Navigate("home");
        return true;
    }

    public async Task OpenInLibraryAsync(Guid gameId)
    {
        Navigate("library");
        await RunSafeAsync(() => Library.SelectAsync(gameId)).ConfigureAwait(true);
    }

    public void NotifyPreferencesChanged() => PreferencesApplied?.Invoke(this, Preferences);

    public void UpdateControllerStatus()
    {
        var src = Host.Controllers.Source;
        OnPropertyChanged(nameof(ControllerConnected));
        if (!src.IsApiAvailable) { ControllerStatus = "Controller API unavailable"; return; }
        var connected = Host.Controllers.Slots.Count(s => s.Connected);
        ControllerStatus = connected switch
        {
            0 => "No controller",
            1 => "1 controller connected",
            _ => $"{connected} controllers connected",
        };
    }

    /// <summary>Runs an action and reports failures to the user instead of crashing.</summary>
    public async Task RunSafeAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        try { await action().ConfigureAwait(true); }
        catch (ValidationException ex) { Host.Dialogs.ShowError("Please check your input", ex.Message); }
#pragma warning disable CA1031 // Unexpected failures are logged and shown, never crash the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Host.Log.Error("UI", "Operation failed.", ex);
            Host.Dialogs.ShowError("Something went wrong", LogRedactor.Redact(ex.Message));
        }
    }

    public void ReportError(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        if (ex is ValidationException) { Host.Dialogs.ShowError("Please check your input", ex.Message); return; }
        Host.Log.Error("UI", "Command failed.", ex);
        Host.Dialogs.ShowError("Something went wrong", LogRedactor.Redact(ex.Message));
    }
}
