namespace XboxPc.DesktopApp;

/// <summary>
/// Whether decorative animations may run: off when the user picked "Reduce motion" or the Performance preset, or when
/// Windows has client-area animations disabled. Set by <see cref="MainWindow.ApplyPreferences"/>.
/// </summary>
public static class Motion
{
    public static bool Enabled { get; set; } = true;
}
