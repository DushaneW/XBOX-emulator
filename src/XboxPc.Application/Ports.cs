using XboxPc.Domain;

namespace XboxPc.Application;

public enum LibrarySort
{
    NameAscending,
    NameDescending,
    RecentlyAdded,
    RecentlyLaunched,
    Generation,
}

public sealed record LibraryQuery(string? Text = null, ConsoleGeneration? Generation = null, LibrarySort Sort = LibrarySort.NameAscending, bool FavoritesOnly = false);

public interface ILibraryRepository
{
    Task<IReadOnlyList<GameEntry>> ListAsync(LibraryQuery query, CancellationToken ct);
    Task<GameEntry?> GetAsync(Guid id, CancellationToken ct);
    Task<GameEntry?> FindDuplicateAsync(string displayName, ConsoleGeneration generation, string? contentPath, Guid excludeId, CancellationToken ct);
    Task AddAsync(GameEntry entry, CancellationToken ct);
    Task UpdateAsync(GameEntry entry, CancellationToken ct);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);
}

public interface IProfileRepository
{
    Task<IReadOnlyList<UserProfile>> ListAsync(CancellationToken ct);
    Task<UserProfile?> GetAsync(Guid id, CancellationToken ct);
    Task AddAsync(UserProfile profile, CancellationToken ct);
    Task UpdateAsync(UserProfile profile, CancellationToken ct);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);
    Task<Guid?> GetActiveProfileIdAsync(CancellationToken ct);
    Task SetActiveProfileIdAsync(Guid id, CancellationToken ct);
}

public interface IGameSettingsRepository
{
    Task<PerGameSettings?> GetAsync(Guid gameId, Guid profileId, CancellationToken ct);
    Task SaveAsync(PerGameSettings settings, CancellationToken ct);
}

public interface IInputMapRepository
{
    Task<InputMap?> GetAsync(Guid profileId, CancellationToken ct);
    Task SaveAsync(Guid profileId, InputMap map, CancellationToken ct);
}

public interface ICompatibilityRepository
{
    Task AddAsync(CompatibilityReport report, CancellationToken ct);
    Task<IReadOnlyList<CompatibilityReport>> ListForGameAsync(Guid gameId, CancellationToken ct);
}

public enum AppLogLevel
{
    Debug,
    Information,
    Warning,
    Error,
}

public sealed record AppLogEntry(DateTimeOffset Timestamp, AppLogLevel Level, string Category, string Message, string? Exception);

public interface IAppLog
{
    void Write(AppLogLevel level, string category, string message, Exception? exception = null);
}

public static class AppLogExtensions
{
    public static void Info(this IAppLog log, string category, string message) => log?.Write(AppLogLevel.Information, category, message);
    public static void Warn(this IAppLog log, string category, string message, Exception? ex = null) => log?.Write(AppLogLevel.Warning, category, message, ex);
    public static void Error(this IAppLog log, string category, string message, Exception? ex = null) => log?.Write(AppLogLevel.Error, category, message, ex);
}

public sealed class NullAppLog : IAppLog
{
    public static readonly NullAppLog Instance = new();
    public void Write(AppLogLevel level, string category, string message, Exception? exception = null) { }
}

/// <summary>Thrown for user-correctable problems; the message is shown to the user as-is.</summary>
public sealed class ValidationException(IReadOnlyList<string> errors) : Exception(string.Join(Environment.NewLine, errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;

    public ValidationException(string error) : this([error]) { }

    public ValidationException() : this("Validation failed.") { }

    public ValidationException(string message, Exception inner) : this([message]) => _ = inner;
}
