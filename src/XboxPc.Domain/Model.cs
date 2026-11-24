namespace XboxPc.Domain;

public enum ConsoleGeneration
{
    OriginalXbox,
    Xbox360,
    XboxOne,
    SeriesXS,
    Other,
}

public static class ConsoleGenerationNames
{
    public static string ToDisplay(this ConsoleGeneration generation) => generation switch
    {
        ConsoleGeneration.OriginalXbox => "Original Xbox",
        ConsoleGeneration.Xbox360 => "Xbox 360",
        ConsoleGeneration.XboxOne => "Xbox One",
        ConsoleGeneration.SeriesXS => "Xbox Series X|S",
        _ => "Other / Test program",
    };
}

/// <summary>A library entry. Being in the library says nothing about whether the content can run.</summary>
public sealed class GameEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string DisplayName { get; set; }
    public ConsoleGeneration Generation { get; set; }
    public string? ContentPath { get; set; }
    public string? ContentFingerprint { get; set; }
    public string? CoverArtPath { get; set; }
    public string MetadataSource { get; set; } = "User";
    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLaunchedAt { get; set; }
    public string? UserNotes { get; set; }
    public bool IsFavorite { get; set; }

    public GameEntry Clone() => (GameEntry)MemberwiseClone();
}

public enum RenderQualityPreset
{
    Quality,
    Balanced,
    Performance,
}

public sealed record UserPreferences
{
    public RenderQualityPreset RenderPreset { get; init; } = RenderQualityPreset.Balanced;
    public bool ReduceMotion { get; init; }
    public double UiScale { get; init; } = 1.0;
    public bool ShowFrameStats { get; init; }

    /// <summary>Console start-up sequence (logo animation). The sound is a separate choice.</summary>
    public bool StartupAnimation { get; init; } = true;
    public bool StartupSound { get; init; } = true;

    /// <summary>Volume for interface sounds (start-up) and emulated audio, 0-1.</summary>
    public double Volume { get; init; } = 0.7;

    /// <summary>Open the emulator full screen when a game starts.</summary>
    public bool LaunchFullscreen { get; init; }

    /// <summary>Show the dashboard full screen (F11 toggles it at any time).</summary>
    public bool DashboardFullscreen { get; init; }

    /// <summary>Emulated picture: bilinear filtering instead of sharp pixels; 4:3 letterbox instead of filling the window.</summary>
    public bool SmoothScaling { get; init; }
    public bool KeepAspectRatio { get; init; } = true;

    /// <summary>Draw the emulated GPU's work with Direct3D 12 (falls back to the CPU picture when unavailable).</summary>
    public bool GpuRenderer { get; init; } = true;

    /// <summary>Direct3D 12 resolution scale: 1 = native 640x480 ... 6 = 3840x2880.</summary>
    public int RenderScale { get; init; } = 2;

    public const double MinUiScale = 0.75;
    public const double MaxUiScale = 1.75;

    public UserPreferences Normalized() => this with
    {
        UiScale = Math.Clamp(double.IsFinite(UiScale) ? UiScale : 1.0, MinUiScale, MaxUiScale),
        Volume = Math.Clamp(double.IsFinite(Volume) ? Volume : 0.7, 0, 1),
        RenderScale = Math.Clamp(RenderScale, 1, 6),
    };
}

public sealed class UserProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string DisplayName { get; set; }
    public string? AvatarPath { get; set; }
    public UserPreferences Preferences { get; set; } = new();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Per-game, per-profile settings.</summary>
public sealed record PerGameSettings
{
    public required Guid GameId { get; init; }
    public required Guid ProfileId { get; init; }
    public string? PreferredRuntimeId { get; init; }
    public int LaunchTimeoutSeconds { get; init; } = 30;

    public const int MinTimeout = 1;
    public const int MaxTimeout = 3600;
}
