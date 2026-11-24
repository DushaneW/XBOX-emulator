using XboxPc.Domain;

namespace XboxPc.Application;

public sealed class LibraryService(ILibraryRepository repository, IAppLog log)
{
    public const int MaxNameLength = 200;
    public const int MaxNotesLength = 4000;

    public Task<IReadOnlyList<GameEntry>> SearchAsync(LibraryQuery query, CancellationToken ct = default) =>
        repository.ListAsync(query, ct);

    public Task<GameEntry?> GetAsync(Guid id, CancellationToken ct = default) => repository.GetAsync(id, ct);

    public async Task<IReadOnlyList<GameEntry>> RecentlyAddedAsync(int count, CancellationToken ct = default) =>
        (await repository.ListAsync(new LibraryQuery(Sort: LibrarySort.RecentlyAdded), ct).ConfigureAwait(false)).Take(count).ToList();

    public async Task<IReadOnlyList<GameEntry>> RecentlyLaunchedAsync(int count, CancellationToken ct = default) =>
        (await repository.ListAsync(new LibraryQuery(Sort: LibrarySort.RecentlyLaunched), ct).ConfigureAwait(false))
            .Where(g => g.LastLaunchedAt is not null).Take(count).ToList();

    public async Task<GameEntry> AddAsync(GameEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await NormalizeAndValidateAsync(entry, ct).ConfigureAwait(false);
        await repository.AddAsync(entry, ct).ConfigureAwait(false);
        log.Info("Library", $"Added '{entry.DisplayName}' ({entry.Generation}).");
        return entry;
    }

    public async Task UpdateAsync(GameEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (await repository.GetAsync(entry.Id, ct).ConfigureAwait(false) is null)
            throw new ValidationException("This entry no longer exists.");
        await NormalizeAndValidateAsync(entry, ct).ConfigureAwait(false);
        await repository.UpdateAsync(entry, ct).ConfigureAwait(false);
        log.Info("Library", $"Updated '{entry.DisplayName}'.");
    }

    public async Task<bool> RemoveAsync(Guid id, CancellationToken ct = default)
    {
        var removed = await repository.DeleteAsync(id, ct).ConfigureAwait(false);
        log.Info("Library", removed ? $"Removed entry {id}." : $"Remove requested for missing entry {id}.");
        return removed;
    }

    public async Task MarkLaunchedAsync(Guid id, DateTimeOffset when, CancellationToken ct = default)
    {
        var entry = await repository.GetAsync(id, ct).ConfigureAwait(false);
        if (entry is null) return;
        entry.LastLaunchedAt = when;
        await repository.UpdateAsync(entry, ct).ConfigureAwait(false);
    }

    private async Task NormalizeAndValidateAsync(GameEntry entry, CancellationToken ct)
    {
        var errors = new List<string>();
        entry.DisplayName = (entry.DisplayName ?? "").Trim();
        if (entry.DisplayName.Length == 0) errors.Add("Name is required.");
        if (entry.DisplayName.Length > MaxNameLength) errors.Add($"Name must be at most {MaxNameLength} characters.");
        if (!Enum.IsDefined(entry.Generation)) errors.Add("Unknown generation.");
        if (entry.UserNotes is { Length: > MaxNotesLength }) errors.Add($"Notes must be at most {MaxNotesLength} characters.");

        if (string.IsNullOrWhiteSpace(entry.ContentPath)) entry.ContentPath = null;
        else
        {
            // Content may be a file or a folder. Existence is required when the user sets it.
            var (path, error) = SafePath.Validate(entry.ContentPath, mustExist: true, allowDirectory: true);
            if (error is not null) errors.Add($"Content path: {error}");
            else entry.ContentPath = path;
        }

        if (string.IsNullOrWhiteSpace(entry.CoverArtPath)) entry.CoverArtPath = null;
        else
        {
            var (path, error) = SafePath.Validate(entry.CoverArtPath, mustExist: true);
            if (error is not null) errors.Add($"Cover art: {error}");
            else if (ImageFileValidator.Validate(path!) is { } imageError) errors.Add($"Cover art: {imageError}");
            else entry.CoverArtPath = path;
        }

        if (errors.Count == 0)
        {
            var dup = await repository.FindDuplicateAsync(entry.DisplayName, entry.Generation, entry.ContentPath, entry.Id, ct).ConfigureAwait(false);
            if (dup is not null) errors.Add($"An entry '{dup.DisplayName}' ({dup.Generation.ToDisplay()}) with the same name or content path already exists.");
        }
        if (errors.Count > 0) throw new ValidationException(errors);
    }
}

public sealed class ProfileService(IProfileRepository repository, IInputMapRepository inputMaps, IAppLog log)
{
    public const string DefaultProfileName = "Player 1";

    public event EventHandler<UserProfile>? ActiveProfileChanged;

    public UserProfile? Active { get; private set; }

    /// <summary>Loads the active profile, creating a first profile on first run.</summary>
    public async Task<UserProfile> InitializeAsync(CancellationToken ct = default)
    {
        var profiles = await repository.ListAsync(ct).ConfigureAwait(false);
        if (profiles.Count == 0)
        {
            var first = new UserProfile { DisplayName = DefaultProfileName };
            await repository.AddAsync(first, ct).ConfigureAwait(false);
            await repository.SetActiveProfileIdAsync(first.Id, ct).ConfigureAwait(false);
            log.Info("Profiles", "Created first profile.");
            profiles = [first];
        }
        var activeId = await repository.GetActiveProfileIdAsync(ct).ConfigureAwait(false);
        Active = profiles.FirstOrDefault(p => p.Id == activeId) ?? profiles[0];
        return Active;
    }

    public Task<IReadOnlyList<UserProfile>> ListAsync(CancellationToken ct = default) => repository.ListAsync(ct);

    public async Task<UserProfile> CreateAsync(string name, CancellationToken ct = default)
    {
        var profile = new UserProfile { DisplayName = ValidateName(name) };
        var existing = await repository.ListAsync(ct).ConfigureAwait(false);
        if (existing.Any(p => string.Equals(p.DisplayName, profile.DisplayName, StringComparison.OrdinalIgnoreCase)))
            throw new ValidationException($"A profile named '{profile.DisplayName}' already exists.");
        await repository.AddAsync(profile, ct).ConfigureAwait(false);
        log.Info("Profiles", "Created profile.");
        return profile;
    }

    public async Task RenameAsync(Guid id, string name, CancellationToken ct = default)
    {
        var profile = await repository.GetAsync(id, ct).ConfigureAwait(false) ?? throw new ValidationException("Profile not found.");
        var newName = ValidateName(name);
        var existing = await repository.ListAsync(ct).ConfigureAwait(false);
        if (existing.Any(p => p.Id != id && string.Equals(p.DisplayName, newName, StringComparison.OrdinalIgnoreCase)))
            throw new ValidationException($"A profile named '{newName}' already exists.");
        profile.DisplayName = newName;
        await repository.UpdateAsync(profile, ct).ConfigureAwait(false);
        if (Active?.Id == id) { Active = profile; ActiveProfileChanged?.Invoke(this, profile); }
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var profiles = await repository.ListAsync(ct).ConfigureAwait(false);
        if (profiles.Count <= 1) throw new ValidationException("The last profile cannot be deleted.");
        if (Active?.Id == id) throw new ValidationException("Switch to another profile before deleting the active one.");
        await repository.DeleteAsync(id, ct).ConfigureAwait(false);
        log.Info("Profiles", "Deleted profile.");
    }

    public async Task SwitchAsync(Guid id, CancellationToken ct = default)
    {
        var profile = await repository.GetAsync(id, ct).ConfigureAwait(false) ?? throw new ValidationException("Profile not found.");
        await repository.SetActiveProfileIdAsync(id, ct).ConfigureAwait(false);
        Active = profile;
        ActiveProfileChanged?.Invoke(this, profile);
    }

    public async Task SavePreferencesAsync(UserPreferences preferences, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var active = Active ?? throw new InvalidOperationException("Profiles are not initialized.");
        if (!Enum.IsDefined(preferences.RenderPreset)) throw new ValidationException("Unknown render preset.");
        active.Preferences = preferences.Normalized();
        await repository.UpdateAsync(active, ct).ConfigureAwait(false);
        ActiveProfileChanged?.Invoke(this, active);
    }

    public async Task<InputMap> GetInputMapAsync(CancellationToken ct = default)
    {
        var active = Active ?? throw new InvalidOperationException("Profiles are not initialized.");
        return await inputMaps.GetAsync(active.Id, ct).ConfigureAwait(false) ?? InputMap.CreateDefault();
    }

    public async Task SaveInputMapAsync(InputMap map, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(map);
        var active = Active ?? throw new InvalidOperationException("Profiles are not initialized.");
        var errors = map.Validate();
        if (errors.Count > 0) throw new ValidationException(errors);
        await inputMaps.SaveAsync(active.Id, map, ct).ConfigureAwait(false);
        log.Info("Input", "Saved input mapping.");
    }

    private static string ValidateName(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length is 0 or > 40) throw new ValidationException("Profile name must be 1-40 characters.");
        return trimmed;
    }
}

public sealed class GameSettingsService(IGameSettingsRepository repository)
{
    public async Task<PerGameSettings> GetAsync(Guid gameId, Guid profileId, CancellationToken ct = default) =>
        await repository.GetAsync(gameId, profileId, ct).ConfigureAwait(false) ?? new PerGameSettings { GameId = gameId, ProfileId = profileId };

    public Task SaveAsync(PerGameSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.LaunchTimeoutSeconds is < PerGameSettings.MinTimeout or > PerGameSettings.MaxTimeout)
            throw new ValidationException($"Launch timeout must be between {PerGameSettings.MinTimeout} and {PerGameSettings.MaxTimeout} seconds.");
        return repository.SaveAsync(settings, ct);
    }
}

public sealed record CompatibilitySummary(CompatibilityStatus Status, ReportSource? Source, CompatibilityReport? Report)
{
    public static readonly CompatibilitySummary NotTested = new(CompatibilityStatus.NotTested, null, null);
}

public sealed record RegressionResult(bool IsRegression, CompatibilityStatus? Previous, CompatibilityStatus Current, string Message);

public sealed class CompatibilityService(ICompatibilityRepository repository, IAppLog log)
{
    public async Task SubmitAsync(CompatibilityReport report, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        var errors = CompatibilityRules.Validate(report);
        if (errors.Count > 0) throw new ValidationException(errors);
        await repository.AddAsync(report, ct).ConfigureAwait(false);
        log.Info("Compatibility", $"Report {report.ReportId} saved: {report.Outcome} ({report.Source}).");
    }

    /// <summary>Newest first.</summary>
    public Task<IReadOnlyList<CompatibilityReport>> HistoryAsync(Guid gameId, CancellationToken ct = default) =>
        repository.ListForGameAsync(gameId, ct);

    /// <summary>Latest locally verified report wins; community reports are only used if no local report exists.</summary>
    public async Task<CompatibilitySummary> GetSummaryAsync(Guid gameId, CancellationToken ct = default)
    {
        var history = await repository.ListForGameAsync(gameId, ct).ConfigureAwait(false);
        var report = history.FirstOrDefault(r => r.Source == ReportSource.LocalVerified) ?? (history.Count > 0 ? history[0] : null);
        return report is null ? CompatibilitySummary.NotTested : new CompatibilitySummary(report.Outcome, report.Source, report);
    }

    /// <summary>Compares a report against the previous report for the same runtime.</summary>
    public static RegressionResult Compare(IReadOnlyList<CompatibilityReport> historyNewestFirst, CompatibilityReport current)
    {
        ArgumentNullException.ThrowIfNull(historyNewestFirst);
        ArgumentNullException.ThrowIfNull(current);
        var previous = historyNewestFirst.FirstOrDefault(r => r.ReportId != current.ReportId && r.RuntimeId == current.RuntimeId && r.TestDate <= current.TestDate);
        if (previous is null) return new(false, null, current.Outcome, "No earlier report for this runtime.");
        if (current.Outcome < previous.Outcome)
            return new(true, previous.Outcome, current.Outcome, $"Regression: {previous.Outcome.ToDisplay()} ({previous.RuntimeVersion}) -> {current.Outcome.ToDisplay()} ({current.RuntimeVersion}).");
        return new(false, previous.Outcome, current.Outcome, current.Outcome > previous.Outcome
            ? $"Improved: {previous.Outcome.ToDisplay()} -> {current.Outcome.ToDisplay()}."
            : "Unchanged.");
    }
}
