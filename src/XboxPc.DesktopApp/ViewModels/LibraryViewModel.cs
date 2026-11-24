using System.Collections.ObjectModel;
using System.Windows.Input;
using XboxPc.Application;
using XboxPc.Domain;

namespace XboxPc.DesktopApp.ViewModels;

public sealed record Choice<T>(string Label, T Value);

public sealed class LibraryViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private string _searchText = "";
    private Choice<ConsoleGeneration?> _generationFilter;
    private Choice<LibrarySort> _sort;
    private GameTile? _selected;
    private GameEditorViewModel? _editor;
    private int _refreshVersion;
    private bool _favoritesOnly;
    private bool _rowLayout;
    private Guid? _returnFocusId;

    public LibraryViewModel(MainViewModel main)
    {
        _main = main;
        GenerationFilters = [new("All generations", null), .. Enum.GetValues<ConsoleGeneration>().Select(g => new Choice<ConsoleGeneration?>(g.ToDisplay(), g))];
        SortOptions =
        [
            new("Name (A-Z)", LibrarySort.NameAscending), new("Name (Z-A)", LibrarySort.NameDescending),
            new("Recently added", LibrarySort.RecentlyAdded), new("Recently launched", LibrarySort.RecentlyLaunched),
            new("Generation", LibrarySort.Generation),
        ];
        _generationFilter = GenerationFilters[0];
        _sort = SortOptions[0];
        NewCommand = new RelayCommand(BeginNew);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, onError: _main.ReportError);
        ClearSearchCommand = new RelayCommand(() => SearchText = "", () => SearchText.Length > 0);
        OpenCommand = new AsyncRelayCommand<GameTile>(t => t is null ? Task.CompletedTask : _main.RunSafeAsync(() => OpenAsync(t.Id)));
        BackCommand = new RelayCommand(CloseDetail);
        SetFilterCommand = new RelayCommand<Choice<ConsoleGeneration?>>(c => { if (c is not null) GenerationFilter = c; });
    }

    public ObservableCollection<GameTile> Items { get; } = [];
    public IReadOnlyList<Choice<ConsoleGeneration?>> GenerationFilters { get; }
    public IReadOnlyList<Choice<LibrarySort>> SortOptions { get; }
    public ICommand NewCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand SetFilterCommand { get; }
    public ICommand PlayCommand => _main.PlayCommand;

    /// <summary>Show favourites only.</summary>
    public bool FavoritesOnly { get => _favoritesOnly; set { if (Set(ref _favoritesOnly, value)) _ = _main.RunSafeAsync(RefreshAsync); } }

    /// <summary>Layout of the collection: one large horizontal row instead of the cover grid.</summary>
    public bool RowLayout { get => _rowLayout; set { if (Set(ref _rowLayout, value)) OnPropertyChanged(nameof(GridLayout)); } }
    public bool GridLayout { get => !_rowLayout; set => RowLayout = !value; }

    /// <summary>The game page (or the new-entry form) is showing instead of the collection.</summary>
    public bool IsDetailOpen => Editor is not null;

    /// <summary>Tile that should receive focus when the collection is shown again.</summary>
    public Guid? ReturnFocusId { get => _returnFocusId; private set => Set(ref _returnFocusId, value); }

    public event EventHandler? DetailClosed;
    public bool IsEmpty => Items.Count == 0;
    public bool IsFiltered => SearchText.Length > 0 || GenerationFilter.Value is not null || FavoritesOnly;
    public string CountText => Items.Count == 1 ? "1 entry" : $"{Items.Count} entries";

    public string SearchText
    {
        get => _searchText;
        set { if (Set(ref _searchText, value ?? "")) _ = _main.RunSafeAsync(RefreshAsync); }
    }

    public Choice<ConsoleGeneration?> GenerationFilter
    {
        get => _generationFilter;
        set { if (value is not null && Set(ref _generationFilter, value)) _ = _main.RunSafeAsync(RefreshAsync); }
    }

    public Choice<LibrarySort> Sort
    {
        get => _sort;
        set { if (value is not null && Set(ref _sort, value)) _ = _main.RunSafeAsync(RefreshAsync); }
    }

    public GameTile? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            if (value is not null && Editor?.EntryId != value.Id) _ = _main.RunSafeAsync(() => LoadEditorAsync(value.Id));
        }
    }

    public GameEditorViewModel? Editor
    {
        get => _editor;
        private set
        {
            var old = _editor;
            if (!Set(ref _editor, value)) return;
            old?.Dispose();
            if (_main.CurrentPage.ViewModel == this) _main.BackdropPath = value?.CoverPreview;
            OnPropertyChanged(nameof(HasEditor));
            OnPropertyChanged(nameof(IsDetailOpen));
        }
    }

    public bool HasEditor => Editor is not null;

    public async Task RefreshAsync()
    {
        var version = ++_refreshVersion;
        var results = await _main.Host.Library.SearchAsync(new LibraryQuery(SearchText, GenerationFilter.Value, Sort.Value, FavoritesOnly)).ConfigureAwait(true);
        var tiles = new List<GameTile>();
        foreach (var e in results) tiles.Add(await HomeViewModel.ToTileAsync(_main.Host, e).ConfigureAwait(true));
        if (version != _refreshVersion) return; // a newer refresh superseded this one
        var selectedId = _selected?.Id;
        Items.Clear();
        foreach (var t in tiles) Items.Add(t);
        _selected = Items.FirstOrDefault(i => i.Id == selectedId);
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsFiltered));
        OnPropertyChanged(nameof(CountText));
    }

    /// <summary>Opens the game page for a tile.</summary>
    public async Task OpenAsync(Guid id)
    {
        ReturnFocusId = id;
        await LoadEditorAsync(id).ConfigureAwait(true);
        _selected = Items.FirstOrDefault(i => i.Id == id);
        OnPropertyChanged(nameof(Selected));
    }

    /// <summary>Back from the game page to the collection; focus returns to the tile that was opened.</summary>
    public void CloseDetail()
    {
        if (Editor is null) return;
        ReturnFocusId = Editor.IsNew ? ReturnFocusId : Editor.EntryId;
        Editor = null;
        _ = _main.RunSafeAsync(async () =>
        {
            await RefreshAsync().ConfigureAwait(true);
            DetailClosed?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>Marks or unmarks a title as favourite.</summary>
    public async Task SetFavoriteAsync(Guid id, bool favorite)
    {
        var entry = await _main.Host.Library.GetAsync(id).ConfigureAwait(true);
        if (entry is null) return;
        entry.IsFavorite = favorite;
        await _main.Host.Library.UpdateAsync(entry).ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
    }

    public void BeginNew()
    {
        _selected = null;
        OnPropertyChanged(nameof(Selected));
        Editor = new GameEditorViewModel(_main, null, OnEditorChangedAsync);
    }

    public async Task SelectAsync(Guid id)
    {
        await RefreshAsync().ConfigureAwait(true);
        if (Items.All(i => i.Id != id))
        {
            // The filter hides it; clear filters so the selection is visible.
            _searchText = "";
            _generationFilter = GenerationFilters[0];
            _favoritesOnly = false;
            OnPropertyChanged(nameof(FavoritesOnly));
            OnPropertyChanged(nameof(SearchText));
            OnPropertyChanged(nameof(GenerationFilter));
            await RefreshAsync().ConfigureAwait(true);
        }
        ReturnFocusId = id;
        await LoadEditorAsync(id).ConfigureAwait(true);
        _selected = Items.FirstOrDefault(i => i.Id == id);
        OnPropertyChanged(nameof(Selected));
    }

    private async Task LoadEditorAsync(Guid id)
    {
        var entry = await _main.Host.Library.GetAsync(id).ConfigureAwait(true);
        if (entry is null) { Editor = null; await RefreshAsync().ConfigureAwait(true); return; }
        var editor = new GameEditorViewModel(_main, entry, OnEditorChangedAsync);
        await editor.LoadAsync().ConfigureAwait(true);
        Editor = editor;
    }

    private async Task OnEditorChangedAsync(Guid? id)
    {
        await RefreshAsync().ConfigureAwait(true);
        if (id is { } gameId) await SelectAsync(gameId).ConfigureAwait(true);
        else
        {
            Editor = null;
            _selected = null;
            OnPropertyChanged(nameof(Selected));
        }
    }
}
