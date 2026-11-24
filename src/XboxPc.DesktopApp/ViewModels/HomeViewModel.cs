using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using XboxPc.Application;
using XboxPc.Domain;
using XboxPc.Runtime.Abstractions;

namespace XboxPc.DesktopApp.ViewModels;

public sealed record GameTile(Guid Id, string Name, string GenerationText, string StatusText, string? CoverPath, string LastLaunchedText)
{
    public string Initials => string.Concat(Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));
    public bool HasCover => CoverPath is not null && File.Exists(CoverPath);

    /// <summary>Why Play is unavailable for this title, or null when it plays.</summary>
    public string? PlayBlock { get; init; }
    public bool CanPlay => PlayBlock is null;
    public bool IsFavorite { get; init; }

    /// <summary>Original Xbox .xbe title played by the built-in emulator.</summary>
    public bool IsEmulatorTitle { get; init; }
    public DateTimeOffset? LastLaunchedAt { get; init; }
    public DateTimeOffset AddedAt { get; init; }

    /// <summary>"Played 3 hours ago" style text, or "Not played yet".</summary>
    public string PlayedAgo => LastLaunchedAt is { } l ? "Played " + Ago(DateTimeOffset.UtcNow - l) : "Not played yet";

    public static string Ago(TimeSpan span) => span.TotalMinutes switch
    {
        < 1 => "just now",
        < 60 => $"{(int)span.TotalMinutes} min ago",
        < 60 * 24 => (int)span.TotalHours == 1 ? "1 hour ago" : $"{(int)span.TotalHours} hours ago",
        < 60 * 24 * 2 => "yesterday",
        < 60 * 24 * 30 => $"{(int)span.TotalDays} days ago",
        _ => $"on {DateTime.Now.Add(-span):d}",
    };
}

public sealed record RuntimeCard(string Name, string Generation, string StateText, string Summary, ImplementationState State)
{
    public bool IsAvailable => State == ImplementationState.Available;

    public static RuntimeCard From(RuntimeDescriptor d) => new(d.DisplayName, d.Generation.ToDisplay(), StateLabel(d.State), d.Summary, d.State);

    public static string StateLabel(ImplementationState s) => s switch
    {
        ImplementationState.ResearchOnly => "Research Only",
        ImplementationState.NotImplemented => "Not Implemented",
        ImplementationState.Experimental => "Experimental",
        _ => "Available",
    };
}

public sealed class HomeViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private string _greeting = "Welcome";
    private int _libraryCount;
    private GameTile? _featured;
    private string _featuredEyebrow = "";

    public HomeViewModel(MainViewModel main)
    {
        _main = main;
        OpenGameCommand = new AsyncRelayCommand<GameTile>(t => t is null ? Task.CompletedTask : _main.OpenInLibraryAsync(t.Id));
        AddGameCommand = new RelayCommand(() => { _main.Navigate("library"); _main.Library.BeginNew(); });
        AddSampleCommand = new AsyncRelayCommand(AddSampleAsync, onError: _main.ReportError);
        GoPerformanceCommand = new RelayCommand(() => _main.Navigate("performance"));
        GoEmulatorCommand = new RelayCommand(() => _main.Navigate("emulator"));
        SeeAllCommand = new RelayCommand(() => _main.Navigate("library"));
        SeeRecentCommand = new RelayCommand(() => _main.Navigate("recent"));
        RunXbeCommand = new RelayCommand(_main.RunXbe);
        Runtimes = new ObservableCollection<RuntimeCard>(_main.Host.Runtimes.Descriptors.Select(RuntimeCard.From));
        var core = _main.Host.Runtimes.Descriptors.FirstOrDefault(d => d.Generation == ConsoleGeneration.OriginalXbox);
        CoreText = core is null ? "Original Xbox core not registered" : $"Original Xbox core {core.Version} · {RuntimeCard.StateLabel(core.State)}";
    }

    public string Greeting { get => _greeting; private set => Set(ref _greeting, value); }
    public int LibraryCount { get => _libraryCount; private set { Set(ref _libraryCount, value); OnPropertyChanged(nameof(HasGames)); OnPropertyChanged(nameof(LibraryCountText)); } }
    public bool HasGames => LibraryCount > 0;
    public string LibraryCountText => LibraryCount == 1 ? "1 title" : $"{LibraryCount} titles";
    public ObservableCollection<GameTile> RecentlyLaunched { get; } = [];
    public ObservableCollection<GameTile> RecentlyAdded { get; } = [];
    public ObservableCollection<GameTile> Collection { get; } = [];
    public ObservableCollection<RuntimeCard> Runtimes { get; }
    public bool HasRecentlyLaunched => RecentlyLaunched.Count > 0;
    public bool HasRecentlyAdded => RecentlyAdded.Count > 0;

    /// <summary>The title in the hero: the last one played, otherwise the newest addition.</summary>
    public GameTile? Featured { get => _featured; private set { Set(ref _featured, value); OnPropertyChanged(nameof(HasFeatured)); } }
    public bool HasFeatured => Featured is not null;
    public string FeaturedEyebrow { get => _featuredEyebrow; private set => Set(ref _featuredEyebrow, value); }
    public string CoreText { get; }

    public ICommand OpenGameCommand { get; }
    public ICommand PlayCommand => _main.PlayCommand;
    public ICommand AddGameCommand { get; }
    public ICommand AddSampleCommand { get; }
    public ICommand GoPerformanceCommand { get; }
    public ICommand GoEmulatorCommand { get; }
    public ICommand SeeAllCommand { get; }
    public ICommand SeeRecentCommand { get; }
    public ICommand RunXbeCommand { get; }

    /// <summary>Kernel coverage, from the engine's export table.</summary>
    public string KernelSummary { get; } =
        $"{XboxPc.Emulation.OriginalXbox.KernelHle.ImplementedOrdinals.Count} / {XboxPc.Emulation.OriginalXbox.KernelExports.ByOrdinal.Count} exports";

    public string KernelTooltip =>
        $"High-level emulation of the Xbox kernel: {KernelSummary} implemented (threads, memory, files, interrupts, video, USB support). " +
        "No Microsoft BIOS or kernel image is used. A program that calls a missing export stops with its name.";

    /// <summary>Boots a dropped .xbe in the emulator window.</summary>
    public void RunXbeFile(string path) => _main.RunXbe(path);

    public async Task RefreshAsync()
    {
        var host = _main.Host;
        Greeting = $"Welcome back, {host.Profiles.Active?.DisplayName ?? "player"}";
        var all = await host.Library.SearchAsync(new LibraryQuery(Sort: LibrarySort.NameAscending)).ConfigureAwait(true);
        LibraryCount = all.Count;
        await FillAsync(RecentlyLaunched, await host.Library.RecentlyLaunchedAsync(10).ConfigureAwait(true)).ConfigureAwait(true);
        await FillAsync(RecentlyAdded, await host.Library.RecentlyAddedAsync(6).ConfigureAwait(true)).ConfigureAwait(true);
        await FillAsync(Collection, all.Take(12).ToList()).ConfigureAwait(true);
        if (RecentlyLaunched.Count > 0) { Featured = RecentlyLaunched[0]; FeaturedEyebrow = "Jump back in"; }
        else if (RecentlyAdded.Count > 0) { Featured = RecentlyAdded[0]; FeaturedEyebrow = "Recently added"; }
        else { Featured = null; FeaturedEyebrow = ""; }
        _main.BackdropPath = Featured is { HasCover: true } f ? f.CoverPath : null;
        OnPropertyChanged(nameof(HasRecentlyLaunched));
        OnPropertyChanged(nameof(HasRecentlyAdded));
    }

    private async Task FillAsync(ObservableCollection<GameTile> target, IReadOnlyList<GameEntry> entries)
    {
        var tiles = new List<GameTile>();
        foreach (var e in entries) tiles.Add(await ToTileAsync(_main.Host, e).ConfigureAwait(true));
        target.Clear();
        foreach (var t in tiles) target.Add(t);
    }

    public static async Task<GameTile> ToTileAsync(AppHost host, GameEntry e)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(e);
        var summary = await host.Compatibility.GetSummaryAsync(e.Id).ConfigureAwait(true);
        var status = summary.Status.ToDisplay() + (summary.Source == ReportSource.Community ? " (community)" : "");
        var launched = e.LastLaunchedAt is { } l ? $"Last launched {l.ToLocalTime():g}" : "Never launched";
        return new GameTile(e.Id, e.DisplayName, e.Generation.ToDisplay(), status, e.CoverArtPath, launched)
        {
            PlayBlock = MainViewModel.PlayBlockReason(host, e),
            IsFavorite = e.IsFavorite,
            IsEmulatorTitle = MainViewModel.IsEmulatorTitle(e),
            LastLaunchedAt = e.LastLaunchedAt,
            AddedAt = e.AddedAt,
        };
    }

    /// <summary>Adds the bundled original test program to the library (once) and opens it.</summary>
    private async Task AddSampleAsync()
    {
        var path = Path.Combine(AppHost.BaseDirectory, "samples", "fibonacci.xpct");
        if (!File.Exists(path)) throw new ValidationException($"The bundled sample was not found at {path}.");
        var existing = (await _main.Host.Library.SearchAsync(new LibraryQuery()).ConfigureAwait(true))
            .FirstOrDefault(g => string.Equals(g.ContentPath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
        var entry = existing ?? await _main.Host.Library.AddAsync(new GameEntry
        {
            DisplayName = "XPC Test Program: Fibonacci",
            Generation = ConsoleGeneration.Other,
            ContentPath = path,
            MetadataSource = "Bundled sample",
            UserNotes = "Original test program bundled with XBOX PC. Runs on the XPC Reference Test Runtime. Not an Xbox game.",
        }).ConfigureAwait(true);
        await _main.OpenInLibraryAsync(entry.Id).ConfigureAwait(true);
    }
}

/// <summary>Recently Played: every title that has been launched, newest first.</summary>
public sealed class RecentViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<GameTile> Items { get; } = [];
    public bool IsEmpty => Items.Count == 0;
    public ICommand OpenGameCommand => main.Home.OpenGameCommand;
    public ICommand PlayCommand => main.PlayCommand;
    public ICommand GoLibraryCommand { get; } = new RelayCommand(() => main.Navigate("library"));

    public async Task RefreshAsync()
    {
        var entries = await main.Host.Library.RecentlyLaunchedAsync(48).ConfigureAwait(true);
        var tiles = new List<GameTile>();
        foreach (var e in entries) tiles.Add(await HomeViewModel.ToTileAsync(main.Host, e).ConfigureAwait(true));
        Items.Clear();
        foreach (var t in tiles) Items.Add(t);
        OnPropertyChanged(nameof(IsEmpty));
    }
}

public sealed class AsyncRelayCommand<T>(Func<T?, Task> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public async void Execute(object? parameter) => await execute(parameter is T t ? t : default).ConfigureAwait(true);
}
