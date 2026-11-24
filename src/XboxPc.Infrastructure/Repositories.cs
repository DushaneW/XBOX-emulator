using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using XboxPc.Application;
using XboxPc.Domain;

namespace XboxPc.Infrastructure;

internal static class Db
{
    public static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    public static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseIso(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public static SqliteCommand Command(this SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    public static string? NullableString(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
}

public sealed class SqliteLibraryRepository(SqliteDatabase db) : ILibraryRepository
{
    private const string Columns = "id, display_name, generation, content_path, content_fingerprint, cover_art_path, metadata_source, added_at, last_launched_at, user_notes, favorite";

    public async Task<IReadOnlyList<GameEntry>> ListAsync(LibraryQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var where = new List<string>();
        var parameters = new List<(string, object?)>();
        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            where.Add("(display_name LIKE $text ESCAPE '\\' OR IFNULL(user_notes,'') LIKE $text ESCAPE '\\')");
            var escaped = query.Text.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
            parameters.Add(("$text", $"%{escaped}%"));
        }
        if (query.FavoritesOnly) where.Add("favorite = 1");
        if (query.Generation is { } gen)
        {
            where.Add("generation = $gen");
            parameters.Add(("$gen", (int)gen));
        }
        var order = query.Sort switch
        {
            LibrarySort.NameDescending => "display_name COLLATE NOCASE DESC",
            LibrarySort.RecentlyAdded => "added_at DESC",
            LibrarySort.RecentlyLaunched => "last_launched_at IS NULL, last_launched_at DESC",
            LibrarySort.Generation => "generation, display_name COLLATE NOCASE",
            _ => "display_name COLLATE NOCASE ASC",
        };
        var sql = $"SELECT {Columns} FROM games {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")} ORDER BY {order};";

        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command(sql, [.. parameters]);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<GameEntry>();
        while (await r.ReadAsync(ct).ConfigureAwait(false)) list.Add(Map(r));
        return list;
    }

    public async Task<GameEntry?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command($"SELECT {Columns} FROM games WHERE id = $id;", ("$id", id.ToString()));
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? Map(r) : null;
    }

    public async Task<GameEntry?> FindDuplicateAsync(string displayName, ConsoleGeneration generation, string? contentPath, Guid excludeId, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command(
            $"SELECT {Columns} FROM games WHERE id <> $id AND ((display_name = $name COLLATE NOCASE AND generation = $gen) OR ($path IS NOT NULL AND content_path = $path COLLATE NOCASE)) LIMIT 1;",
            ("$id", excludeId.ToString()), ("$name", displayName), ("$gen", (int)generation), ("$path", contentPath));
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? Map(r) : null;
    }

    public async Task AddAsync(GameEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command(
            $"INSERT INTO games ({Columns}) VALUES ($id,$name,$gen,$path,$fp,$cover,$src,$added,$launched,$notes,$fav);", Params(entry));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateAsync(GameEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command(
            "UPDATE games SET display_name=$name, generation=$gen, content_path=$path, content_fingerprint=$fp, cover_art_path=$cover, metadata_source=$src, added_at=$added, last_launched_at=$launched, user_notes=$notes, favorite=$fav WHERE id=$id;",
            Params(entry));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command("DELETE FROM games WHERE id=$id;", ("$id", id.ToString()));
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    private static (string, object?)[] Params(GameEntry e) =>
    [
        ("$id", e.Id.ToString()), ("$name", e.DisplayName), ("$gen", (int)e.Generation), ("$path", e.ContentPath),
        ("$fp", e.ContentFingerprint), ("$cover", e.CoverArtPath), ("$src", e.MetadataSource), ("$added", Db.Iso(e.AddedAt)),
        ("$launched", e.LastLaunchedAt is { } l ? Db.Iso(l) : null), ("$notes", e.UserNotes), ("$fav", e.IsFavorite ? 1 : 0),
    ];

    private static GameEntry Map(SqliteDataReader r) => new()
    {
        Id = Guid.Parse(r.GetString(0)),
        DisplayName = r.GetString(1),
        Generation = (ConsoleGeneration)r.GetInt32(2),
        ContentPath = r.NullableString(3),
        ContentFingerprint = r.NullableString(4),
        CoverArtPath = r.NullableString(5),
        MetadataSource = r.GetString(6),
        AddedAt = Db.ParseIso(r.GetString(7)),
        LastLaunchedAt = r.NullableString(8) is { } l ? Db.ParseIso(l) : null,
        UserNotes = r.NullableString(9),
        IsFavorite = r.GetInt32(10) != 0,
    };
}

public sealed class SqliteProfileRepository(SqliteDatabase db) : IProfileRepository
{
    public async Task<IReadOnlyList<UserProfile>> ListAsync(CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command("SELECT id, display_name, avatar_path, preferences_json, created_at FROM profiles ORDER BY created_at;");
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<UserProfile>();
        while (await r.ReadAsync(ct).ConfigureAwait(false)) list.Add(Map(r));
        return list;
    }

    public async Task<UserProfile?> GetAsync(Guid id, CancellationToken ct) => (await ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(p => p.Id == id);

    public async Task AddAsync(UserProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command("INSERT INTO profiles (id, display_name, avatar_path, preferences_json, created_at) VALUES ($id,$name,$avatar,$prefs,$created);",
            ("$id", profile.Id.ToString()), ("$name", profile.DisplayName), ("$avatar", profile.AvatarPath),
            ("$prefs", JsonSerializer.Serialize(profile.Preferences, Db.Json)), ("$created", Db.Iso(profile.CreatedAt)));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateAsync(UserProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command("UPDATE profiles SET display_name=$name, avatar_path=$avatar, preferences_json=$prefs WHERE id=$id;",
            ("$id", profile.Id.ToString()), ("$name", profile.DisplayName), ("$avatar", profile.AvatarPath),
            ("$prefs", JsonSerializer.Serialize(profile.Preferences, Db.Json)));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command("DELETE FROM profiles WHERE id=$id;", ("$id", id.ToString()));
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    public async Task<Guid?> GetActiveProfileIdAsync(CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command("SELECT value FROM app_state WHERE key='active_profile';");
        var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        return Guid.TryParse(value, out var id) ? id : null;
    }

    public async Task SetActiveProfileIdAsync(Guid id, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command("INSERT INTO app_state (key, value) VALUES ('active_profile', $v) ON CONFLICT(key) DO UPDATE SET value=excluded.value;", ("$v", id.ToString()));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static UserProfile Map(SqliteDataReader r)
    {
        UserPreferences prefs;
        try { prefs = (JsonSerializer.Deserialize<UserPreferences>(r.GetString(3), Db.Json) ?? new()).Normalized(); }
        catch (JsonException) { prefs = new(); } // Corrupt preferences fall back to defaults instead of blocking startup.
        return new UserProfile
        {
            Id = Guid.Parse(r.GetString(0)),
            DisplayName = r.GetString(1),
            AvatarPath = r.NullableString(2),
            Preferences = prefs,
            CreatedAt = Db.ParseIso(r.GetString(4)),
        };
    }
}

public sealed class SqliteGameSettingsRepository(SqliteDatabase db) : IGameSettingsRepository
{
    public async Task<PerGameSettings?> GetAsync(Guid gameId, Guid profileId, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command("SELECT settings_json FROM game_settings WHERE game_id=$g AND profile_id=$p;", ("$g", gameId.ToString()), ("$p", profileId.ToString()));
        if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not string json) return null;
        try { return JsonSerializer.Deserialize<PerGameSettings>(json, Db.Json); }
        catch (JsonException) { return null; }
    }

    public async Task SaveAsync(PerGameSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command(
            "INSERT INTO game_settings (game_id, profile_id, settings_json) VALUES ($g,$p,$j) ON CONFLICT(game_id, profile_id) DO UPDATE SET settings_json=excluded.settings_json;",
            ("$g", settings.GameId.ToString()), ("$p", settings.ProfileId.ToString()), ("$j", JsonSerializer.Serialize(settings, Db.Json)));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}

public sealed class SqliteInputMapRepository(SqliteDatabase db) : IInputMapRepository
{
    private sealed record StoredMap(Dictionary<UiAction, List<string>> Bindings, double LeftStickDeadZone, double StickSensitivity);

    public async Task<InputMap?> GetAsync(Guid profileId, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command("SELECT map_json FROM input_maps WHERE profile_id=$p;", ("$p", profileId.ToString()));
        if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not string json) return null;
        try
        {
            var stored = JsonSerializer.Deserialize<StoredMap>(json, Db.Json);
            if (stored is null) return null;
            var map = new InputMap
            {
                Bindings = stored.Bindings.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value),
                LeftStickDeadZone = stored.LeftStickDeadZone,
                StickSensitivity = stored.StickSensitivity,
            };
            // A stored map that is no longer valid (e.g. edited by hand) is ignored so the UI stays usable.
            return map.Validate().Count == 0 ? map : null;
        }
        catch (JsonException) { return null; }
    }

    public async Task SaveAsync(Guid profileId, InputMap map, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(map);
        var stored = new StoredMap(map.Bindings.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()), map.LeftStickDeadZone, map.StickSensitivity);
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command(
            "INSERT INTO input_maps (profile_id, map_json) VALUES ($p,$j) ON CONFLICT(profile_id) DO UPDATE SET map_json=excluded.map_json;",
            ("$p", profileId.ToString()), ("$j", JsonSerializer.Serialize(stored, Db.Json)));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}

public sealed class SqliteCompatibilityRepository(SqliteDatabase db) : ICompatibilityRepository
{
    public async Task AddAsync(CompatibilityReport report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command(
            "INSERT INTO compatibility_reports (id, game_id, runtime_id, runtime_version, test_date, outcome, source, report_json) VALUES ($id,$g,$rt,$rv,$d,$o,$s,$j);",
            ("$id", report.ReportId.ToString()), ("$g", report.GameEntryId.ToString()), ("$rt", report.RuntimeId), ("$rv", report.RuntimeVersion),
            ("$d", Db.Iso(report.TestDate)), ("$o", (int)report.Outcome), ("$s", (int)report.Source), ("$j", JsonSerializer.Serialize(report, Db.Json)));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CompatibilityReport>> ListForGameAsync(Guid gameId, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command("SELECT report_json FROM compatibility_reports WHERE game_id=$g ORDER BY test_date DESC;", ("$g", gameId.ToString()));
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<CompatibilityReport>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var report = JsonSerializer.Deserialize<CompatibilityReport>(r.GetString(0), Db.Json);
            if (report is not null) list.Add(report);
        }
        return list;
    }
}
