using System.Reflection;

namespace ArcadeLibraryManager.Desktop;

internal static class AppInfo
{
    internal static string Version { get; } = ReadVersion();
    internal static string DisplayName => "Arcade Library Manager " + Version;

    private static string ReadVersion()
    {
        var assembly = typeof(AppInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational)) return informational.Split('+', 2)[0];
        return assembly.GetName().Version?.ToString() ?? "Unknown";
    }
}
