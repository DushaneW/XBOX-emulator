using Microsoft.Data.Sqlite;
using XboxPc.Application;

namespace XboxPc.Infrastructure;

public sealed record Migration(int Version, string Name, string Sql);

/// <summary>SQLite connection factory with ordered, transactional migrations tracked by PRAGMA user_version.</summary>
public sealed class SqliteDatabase
{
    public static readonly IReadOnlyList<Migration> Migrations =
    [
        new(1, "initial schema", """
            CREATE TABLE games (
                id TEXT PRIMARY KEY,
                display_name TEXT NOT NULL,
                generation INTEGER NOT NULL,
                content_path TEXT NULL,
                content_fingerprint TEXT NULL,
                cover_art_path TEXT NULL,
                metadata_source TEXT NOT NULL,
                added_at TEXT NOT NULL,
                last_launched_at TEXT NULL,
                user_notes TEXT NULL
            );
            CREATE INDEX ix_games_name ON games(display_name COLLATE NOCASE);
            CREATE TABLE profiles (
                id TEXT PRIMARY KEY,
                display_name TEXT NOT NULL,
                avatar_path TEXT NULL,
                preferences_json TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE TABLE app_state (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """),
        new(2, "per-game settings and input maps", """
            CREATE TABLE game_settings (
                game_id TEXT NOT NULL REFERENCES games(id) ON DELETE CASCADE,
                profile_id TEXT NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
                settings_json TEXT NOT NULL,
                PRIMARY KEY (game_id, profile_id)
            );
            CREATE TABLE input_maps (
                profile_id TEXT PRIMARY KEY REFERENCES profiles(id) ON DELETE CASCADE,
                map_json TEXT NOT NULL
            );
            """),
        new(3, "compatibility reports", """
            CREATE TABLE compatibility_reports (
                id TEXT PRIMARY KEY,
                game_id TEXT NOT NULL REFERENCES games(id) ON DELETE CASCADE,
                runtime_id TEXT NOT NULL,
                runtime_version TEXT NOT NULL,
                test_date TEXT NOT NULL,
                outcome INTEGER NOT NULL,
                source INTEGER NOT NULL,
                report_json TEXT NOT NULL
            );
            CREATE INDEX ix_compat_game ON compatibility_reports(game_id, test_date DESC);
            """),
        new(4, "favourites", """
            ALTER TABLE games ADD COLUMN favorite INTEGER NOT NULL DEFAULT 0;
            """),
    ];

    private readonly string _connectionString;
    private readonly IReadOnlyList<Migration> _migrations;

    public SqliteDatabase(string databasePath, IReadOnlyList<Migration>? migrations = null)
    {
        DatabasePath = databasePath;
        _migrations = migrations ?? Migrations;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = false,
        }.ToString();
    }

    public string DatabasePath { get; }

    public int LatestVersion => _migrations.Count == 0 ? 0 : _migrations.Max(m => m.Version);

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }

    public async Task<int> GetVersionAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Applies pending migrations. Each migration is atomic; a failure leaves the previous version intact.</summary>
    public async Task<int> MigrateAsync(IAppLog log, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        var dir = Path.GetDirectoryName(Path.GetFullPath(DatabasePath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using (var wal = connection.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            await wal.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        int current;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version;";
            current = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        }

        if (current > LatestVersion)
            throw new InvalidOperationException($"Database version {current} is newer than this app supports ({LatestVersion}). Update the app; the database was not modified.");

        foreach (var migration in _migrations.Where(m => m.Version > current).OrderBy(m => m.Version))
        {
            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = migration.Sql + $"\nPRAGMA user_version = {migration.Version};";
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await tx.CommitAsync(ct).ConfigureAwait(false);
            log.Info("Database", $"Applied migration {migration.Version} ({migration.Name}).");
            current = migration.Version;
        }
        return current;
    }
}
