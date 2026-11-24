using Microsoft.Data.Sqlite;
using XboxPc.Application;
using XboxPc.Domain;
using XboxPc.Infrastructure;

namespace XboxPc.Tests.Integration;

public class MigrationTests
{
    [Fact]
    public async Task Fresh_database_migrates_to_latest()
    {
        using var dir = new TempDir();
        var db = new SqliteDatabase(Path.Combine(dir.Path, "m.db"));
        var log = new ListLog();
        var version = await db.MigrateAsync(log);
        Assert.Equal(db.LatestVersion, version);
        Assert.Equal(SqliteDatabase.Migrations.Count, log.Entries.Count(e => e.Message.StartsWith("Applied migration", StringComparison.Ordinal)));
        Assert.Equal(version, await db.GetVersionAsync());
    }

    [Fact]
    public async Task Migrating_twice_is_a_no_op()
    {
        using var dir = new TempDir();
        var db = await TestDb.CreateAsync(dir);
        var log = new ListLog();
        await db.MigrateAsync(log);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task Partial_upgrade_from_v1_preserves_data()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "up.db");
        var v1 = new SqliteDatabase(path, [SqliteDatabase.Migrations[0]]);
        await v1.MigrateAsync(NullAppLog.Instance);
        // A row written by a v1 build (the current repository writes columns v1 does not have).
        await using (var c = await v1.OpenAsync(CancellationToken.None))
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO games (id, display_name, generation, metadata_source, added_at) VALUES ($id, 'Kept', $gen, 'User', '2025-01-01T00:00:00.0000000+00:00');";
            cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            cmd.Parameters.AddWithValue("$gen", (int)ConsoleGeneration.Other);
            await cmd.ExecuteNonQueryAsync();
        }

        var full = new SqliteDatabase(path);
        Assert.Equal(full.LatestVersion, await full.MigrateAsync(NullAppLog.Instance));
        var all = await new SqliteLibraryRepository(full).ListAsync(new LibraryQuery(), CancellationToken.None);
        Assert.Equal("Kept", Assert.Single(all).DisplayName);
    }

    [Fact]
    public async Task Failing_migration_rolls_back_and_keeps_previous_version()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "bad.db");
        var migrations = new List<Migration>(SqliteDatabase.Migrations) { new(99, "broken", "CREATE TABLE ok_part (x INTEGER); THIS IS NOT SQL;") };
        var db = new SqliteDatabase(path, migrations);
        await Assert.ThrowsAsync<SqliteException>(() => db.MigrateAsync(NullAppLog.Instance));
        Assert.Equal(SqliteDatabase.Migrations.Max(m => m.Version), await db.GetVersionAsync());

        await using var c = await db.OpenAsync(CancellationToken.None);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE name='ok_part';";
        Assert.Equal(0L, await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Newer_database_is_refused_without_modification()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "future.db");
        var future = new SqliteDatabase(path, [.. SqliteDatabase.Migrations, new Migration(50, "future", "CREATE TABLE future_t (x INTEGER);")]);
        await future.MigrateAsync(NullAppLog.Instance);
        var current = new SqliteDatabase(path);
        await Assert.ThrowsAsync<InvalidOperationException>(() => current.MigrateAsync(NullAppLog.Instance));
        Assert.Equal(50, await current.GetVersionAsync());
    }
}

public class LibraryServiceTests
{
    [Fact]
    public async Task Crud_round_trip()
    {
        using var dir = new TempDir();
        var (library, _) = await TestDb.LibraryAsync(dir);
        var content = dir.File("game.xpct", "XPCT 1\nhalt");
        var added = await library.AddAsync(new GameEntry { DisplayName = "  Test  ", Generation = ConsoleGeneration.Other, ContentPath = content, UserNotes = "n" });
        Assert.Equal("Test", added.DisplayName);

        var loaded = await library.GetAsync(added.Id);
        Assert.NotNull(loaded);
        Assert.Equal(Path.GetFullPath(content), loaded.ContentPath);
        Assert.Equal(added.AddedAt.ToUnixTimeMilliseconds(), loaded.AddedAt.ToUnixTimeMilliseconds());

        loaded.DisplayName = "Renamed";
        await library.UpdateAsync(loaded);
        Assert.Equal("Renamed", (await library.GetAsync(added.Id))!.DisplayName);

        Assert.True(await library.RemoveAsync(added.Id));
        Assert.Null(await library.GetAsync(added.Id));
        Assert.False(await library.RemoveAsync(added.Id));
    }

    [Fact]
    public async Task Duplicate_name_in_same_generation_is_rejected()
    {
        using var dir = new TempDir();
        var (library, _) = await TestDb.LibraryAsync(dir);
        await library.AddAsync(new GameEntry { DisplayName = "Halo-like", Generation = ConsoleGeneration.Xbox360 });
        await Assert.ThrowsAsync<ValidationException>(() => library.AddAsync(new GameEntry { DisplayName = "halo-LIKE", Generation = ConsoleGeneration.Xbox360 }));
        await library.AddAsync(new GameEntry { DisplayName = "Halo-like", Generation = ConsoleGeneration.XboxOne }); // different generation is fine
    }

    [Fact]
    public async Task Duplicate_content_path_is_rejected()
    {
        using var dir = new TempDir();
        var (library, _) = await TestDb.LibraryAsync(dir);
        var file = dir.File("same.bin", "x");
        await library.AddAsync(new GameEntry { DisplayName = "A", Generation = ConsoleGeneration.Other, ContentPath = file });
        var ex = await Assert.ThrowsAsync<ValidationException>(() => library.AddAsync(new GameEntry { DisplayName = "B", Generation = ConsoleGeneration.Other, ContentPath = file.ToUpperInvariant() }));
        Assert.Contains("already exists", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_name_is_rejected(string name)
    {
        using var dir = new TempDir();
        var (library, _) = await TestDb.LibraryAsync(dir);
        await Assert.ThrowsAsync<ValidationException>(() => library.AddAsync(new GameEntry { DisplayName = name, Generation = ConsoleGeneration.Other }));
    }

    [Fact]
    public async Task Missing_and_traversal_paths_are_rejected()
    {
        using var dir = new TempDir();
        var (library, _) = await TestDb.LibraryAsync(dir);
        await Assert.ThrowsAsync<ValidationException>(() => library.AddAsync(new GameEntry { DisplayName = "M", Generation = ConsoleGeneration.Other, ContentPath = Path.Combine(dir.Path, "missing.xpct") }));
        await Assert.ThrowsAsync<ValidationException>(() => library.AddAsync(new GameEntry { DisplayName = "T", Generation = ConsoleGeneration.Other, ContentPath = dir.Path + @"\..\..\x" }));
    }

    [Fact]
    public async Task Fake_cover_art_is_rejected()
    {
        using var dir = new TempDir();
        var (library, _) = await TestDb.LibraryAsync(dir);
        var fake = dir.File("cover.png", "this is not a png");
        await Assert.ThrowsAsync<ValidationException>(() => library.AddAsync(new GameEntry { DisplayName = "C", Generation = ConsoleGeneration.Other, CoverArtPath = fake }));
    }

    [Fact]
    public async Task Search_filter_and_sort()
    {
        using var dir = new TempDir();
        var (library, _) = await TestDb.LibraryAsync(dir);
        await library.AddAsync(new GameEntry { DisplayName = "Bravo", Generation = ConsoleGeneration.Xbox360, AddedAt = DateTimeOffset.UtcNow.AddDays(-2) });
        await library.AddAsync(new GameEntry { DisplayName = "Alpha 100%", Generation = ConsoleGeneration.SeriesXS, AddedAt = DateTimeOffset.UtcNow.AddDays(-1) });
        await library.AddAsync(new GameEntry { DisplayName = "Charlie'; DROP TABLE games;--", Generation = ConsoleGeneration.Xbox360, UserNotes = "under_score" });

        Assert.Equal(["Alpha 100%", "Bravo", "Charlie'; DROP TABLE games;--"], (await library.SearchAsync(new LibraryQuery())).Select(g => g.DisplayName));
        Assert.Equal(["Charlie'; DROP TABLE games;--", "Bravo", "Alpha 100%"], (await library.SearchAsync(new LibraryQuery(Sort: LibrarySort.NameDescending))).Select(g => g.DisplayName));
        Assert.Equal(2, (await library.SearchAsync(new LibraryQuery(Generation: ConsoleGeneration.Xbox360))).Count);
        Assert.Equal("Alpha 100%", Assert.Single(await library.SearchAsync(new LibraryQuery("100%"))).DisplayName);
        Assert.Single(await library.SearchAsync(new LibraryQuery("under_score")));
        Assert.Empty(await library.SearchAsync(new LibraryQuery("_x")));
        Assert.Equal("Charlie'; DROP TABLE games;--", (await library.RecentlyAddedAsync(1))[0].DisplayName);
        Assert.Equal(3, (await library.SearchAsync(new LibraryQuery("a"))).Count);
    }

    [Fact]
    public async Task Recently_launched_only_lists_launched_entries()
    {
        using var dir = new TempDir();
        var (library, _) = await TestDb.LibraryAsync(dir);
        var a = await library.AddAsync(new GameEntry { DisplayName = "A", Generation = ConsoleGeneration.Other });
        await library.AddAsync(new GameEntry { DisplayName = "B", Generation = ConsoleGeneration.Other });
        Assert.Empty(await library.RecentlyLaunchedAsync(5));
        await library.MarkLaunchedAsync(a.Id, DateTimeOffset.UtcNow);
        Assert.Equal("A", Assert.Single(await library.RecentlyLaunchedAsync(5)).DisplayName);
    }
}

public class ProfileAndSettingsTests
{
    private static async Task<(ProfileService Profiles, SqliteDatabase Db)> CreateAsync(TempDir dir)
    {
        var db = await TestDb.CreateAsync(dir);
        var service = new ProfileService(new SqliteProfileRepository(db), new SqliteInputMapRepository(db), NullAppLog.Instance);
        await service.InitializeAsync();
        return (service, db);
    }

    [Fact]
    public async Task First_run_creates_a_profile_and_it_persists()
    {
        using var dir = new TempDir();
        var (profiles, db) = await CreateAsync(dir);
        Assert.Equal(ProfileService.DefaultProfileName, profiles.Active!.DisplayName);
        var again = new ProfileService(new SqliteProfileRepository(db), new SqliteInputMapRepository(db), NullAppLog.Instance);
        Assert.Equal(profiles.Active.Id, (await again.InitializeAsync()).Id);
    }

    [Fact]
    public async Task Preferences_and_switching_persist()
    {
        using var dir = new TempDir();
        var (profiles, db) = await CreateAsync(dir);
        var second = await profiles.CreateAsync("Second");
        await profiles.SwitchAsync(second.Id);
        await profiles.SavePreferencesAsync(new UserPreferences { RenderPreset = RenderQualityPreset.Performance, ReduceMotion = true, UiScale = 1.25 });

        var reloaded = new ProfileService(new SqliteProfileRepository(db), new SqliteInputMapRepository(db), NullAppLog.Instance);
        var active = await reloaded.InitializeAsync();
        Assert.Equal(second.Id, active.Id);
        Assert.Equal(RenderQualityPreset.Performance, active.Preferences.RenderPreset);
        Assert.True(active.Preferences.ReduceMotion);
        Assert.Equal(1.25, active.Preferences.UiScale);
    }

    [Fact]
    public async Task Profile_rules()
    {
        using var dir = new TempDir();
        var (profiles, _) = await CreateAsync(dir);
        await Assert.ThrowsAsync<ValidationException>(() => profiles.DeleteAsync(profiles.Active!.Id)); // last profile
        await Assert.ThrowsAsync<ValidationException>(() => profiles.CreateAsync(ProfileService.DefaultProfileName.ToUpperInvariant()));
        await Assert.ThrowsAsync<ValidationException>(() => profiles.CreateAsync(new string('x', 41)));
        var other = await profiles.CreateAsync("Other");
        await Assert.ThrowsAsync<ValidationException>(() => profiles.DeleteAsync(profiles.Active!.Id)); // active profile
        await profiles.DeleteAsync(other.Id);
        Assert.Single(await profiles.ListAsync());
    }

    [Fact]
    public async Task Input_map_persists_per_profile_and_invalid_maps_are_refused()
    {
        using var dir = new TempDir();
        var (profiles, _) = await CreateAsync(dir);
        var custom = InputMap.CreateDefault() with { LeftStickDeadZone = 0.4, StickSensitivity = 2 };
        var bindings = custom.Bindings.ToDictionary(k => k.Key, v => v.Value);
        bindings[UiAction.Menu] = ["Pad.Y", "Key.F2"];
        custom = custom with { Bindings = bindings };
        await profiles.SaveInputMapAsync(custom);

        var loaded = await profiles.GetInputMapAsync();
        Assert.Equal(0.4, loaded.LeftStickDeadZone);
        Assert.Equal(UiAction.Menu, loaded.Resolve("Pad.Y"));
        Assert.Null(loaded.Resolve("Pad.Start"));

        var other = await profiles.CreateAsync("Other");
        await profiles.SwitchAsync(other.Id);
        Assert.Equal(UiAction.Menu, (await profiles.GetInputMapAsync()).Resolve("Pad.Start")); // other profile still has defaults

        await Assert.ThrowsAsync<ValidationException>(() => profiles.SaveInputMapAsync(InputMap.CreateDefault() with { LeftStickDeadZone = 2 }));
    }

    [Fact]
    public async Task Corrupt_stored_input_map_falls_back_to_defaults()
    {
        using var dir = new TempDir();
        var (profiles, db) = await CreateAsync(dir);
        await using (var c = await db.OpenAsync(CancellationToken.None))
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO input_maps (profile_id, map_json) VALUES ($p, '{not json');";
            cmd.Parameters.AddWithValue("$p", profiles.Active!.Id.ToString());
            await cmd.ExecuteNonQueryAsync();
        }
        Assert.Empty((await profiles.GetInputMapAsync()).Validate());
    }

    [Fact]
    public async Task Per_game_settings_round_trip_and_validate()
    {
        using var dir = new TempDir();
        var (profiles, db) = await CreateAsync(dir);
        var library = new LibraryService(new SqliteLibraryRepository(db), NullAppLog.Instance);
        var game = await library.AddAsync(new GameEntry { DisplayName = "G", Generation = ConsoleGeneration.Other });
        var settings = new GameSettingsService(new SqliteGameSettingsRepository(db));

        Assert.Equal(30, (await settings.GetAsync(game.Id, profiles.Active!.Id)).LaunchTimeoutSeconds);
        await settings.SaveAsync(new PerGameSettings { GameId = game.Id, ProfileId = profiles.Active.Id, PreferredRuntimeId = "x", LaunchTimeoutSeconds = 90 });
        var loaded = await settings.GetAsync(game.Id, profiles.Active.Id);
        Assert.Equal(90, loaded.LaunchTimeoutSeconds);
        Assert.Equal("x", loaded.PreferredRuntimeId);
        await Assert.ThrowsAsync<ValidationException>(() => settings.SaveAsync(loaded with { LaunchTimeoutSeconds = 0 }));

        await library.RemoveAsync(game.Id); // cascade
        Assert.Equal(30, (await settings.GetAsync(game.Id, profiles.Active.Id)).LaunchTimeoutSeconds);
    }
}

public class CompatibilityServiceTests
{
    private static readonly HostInfo Host = new("Windows 11", "CPU", "GPU", "16 GB", "1.0");

    private static CompatibilityReport Claim(Guid game, CompatibilityStatus status, ReportSource source, DateTimeOffset when, string version = "1.0") => new()
    {
        GameEntryId = game, RuntimeId = "rt", RuntimeVersion = version, Host = Host, Outcome = status, Source = source, TestDate = when,
        TestAssetFingerprint = "sha256:x", TestProcedure = "p", EvidenceReferences = ["log"], Reviewer = "QA", ReviewerConfirmed = true,
    };

    [Fact]
    public async Task Unsupported_claims_are_not_stored_and_status_stays_not_tested()
    {
        using var dir = new TempDir();
        var (library, db) = await TestDb.LibraryAsync(dir);
        var game = await library.AddAsync(new GameEntry { DisplayName = "GTA VI", Generation = ConsoleGeneration.SeriesXS });
        var service = new CompatibilityService(new SqliteCompatibilityRepository(db), NullAppLog.Instance);

        await Assert.ThrowsAsync<ValidationException>(() => service.SubmitAsync(new CompatibilityReport
        {
            GameEntryId = game.Id, RuntimeId = "xpc.series-xs.research", RuntimeVersion = "0", Host = Host, Outcome = CompatibilityStatus.Playable,
        }));
        var summary = await service.GetSummaryAsync(game.Id);
        Assert.Equal(CompatibilityStatus.NotTested, summary.Status);
        Assert.Null(summary.Source);
    }

    [Fact]
    public async Task Local_reports_win_over_newer_community_reports_and_history_is_ordered()
    {
        using var dir = new TempDir();
        var (library, db) = await TestDb.LibraryAsync(dir);
        var game = await library.AddAsync(new GameEntry { DisplayName = "G", Generation = ConsoleGeneration.Other });
        var service = new CompatibilityService(new SqliteCompatibilityRepository(db), NullAppLog.Instance);
        var now = DateTimeOffset.UtcNow;
        await service.SubmitAsync(Claim(game.Id, CompatibilityStatus.Boots, ReportSource.LocalVerified, now.AddDays(-3)));
        await service.SubmitAsync(Claim(game.Id, CompatibilityStatus.Playable, ReportSource.Community, now.AddDays(-1)));

        var summary = await service.GetSummaryAsync(game.Id);
        Assert.Equal(CompatibilityStatus.Boots, summary.Status);
        Assert.Equal(ReportSource.LocalVerified, summary.Source);
        var history = await service.HistoryAsync(game.Id);
        Assert.Equal([CompatibilityStatus.Playable, CompatibilityStatus.Boots], history.Select(h => h.Outcome));
    }

    [Fact]
    public void Regression_detection()
    {
        var game = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var old = Claim(game, CompatibilityStatus.InGame, ReportSource.LocalVerified, now.AddDays(-1), "1.0");
        var newer = Claim(game, CompatibilityStatus.Boots, ReportSource.LocalVerified, now, "1.1");
        var result = CompatibilityService.Compare([newer, old], newer);
        Assert.True(result.IsRegression);
        Assert.Equal(CompatibilityStatus.InGame, result.Previous);
        Assert.False(CompatibilityService.Compare([old], old).IsRegression);
        Assert.False(CompatibilityService.Compare([old, newer], old with { ReportId = Guid.NewGuid(), Outcome = CompatibilityStatus.Playable, TestDate = now.AddDays(1) }).IsRegression);
    }
}

public class JsonLinesLogTests
{
    [Fact]
    public void Writes_redacted_json_lines()
    {
        using var dir = new TempDir();
        string file;
        using (var log = new JsonLinesLog(dir.Path))
        {
            log.Write(AppLogLevel.Information, "Test", "token=abc123 done");
            file = log.CurrentFile!;
            Assert.Single(log.Recent);
        }
        var line = File.ReadAllLines(file).Single();
        using var json = System.Text.Json.JsonDocument.Parse(line);
        Assert.Equal("Information", json.RootElement.GetProperty("level").GetString());
        Assert.DoesNotContain("abc123", line, StringComparison.Ordinal);
    }
}
