namespace ArcadeLibraryManager.Core;

/// <summary>Length policy shared by generated themes, previews, and reference presets.</summary>
public static class ThemeDuration
{
    public const int MinimumSeconds = 25;
    public const int MaximumSeconds = 33;
    public const int DefaultSeconds = 30;

    public static int Normalize(int seconds) => Math.Clamp(seconds, MinimumSeconds, MaximumSeconds);

    public static int FromReference(double seconds) => double.IsFinite(seconds)
        ? (int)Math.Clamp(Math.Round(seconds), MinimumSeconds, MaximumSeconds)
        : DefaultSeconds;
}
