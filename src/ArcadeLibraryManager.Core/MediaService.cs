using System.Text.RegularExpressions;
using System.Globalization;

namespace ArcadeLibraryManager.Core;

/// <summary>Conservative local media discovery; all paths are read-only until an explicit render.</summary>
public sealed class MediaService
{
    private readonly RelatedThemeDiscovery relatedThemes = new();
    private readonly Dictionary<string, (DateTime Stamp, string[] Files)> indexes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTime Stamp, long Length, bool Usable)> transparency = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp", ".bmp" };
    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".avi", ".webm", ".mov", ".flv", ".wmv", ".mpg", ".mpeg" };

    /// <summary>Theme work follows a real LaunchBox game, not each alternate TeknoParrot launch profile.</summary>
    public static IReadOnlyList<GameRecord> SelectThemeGames(AppSettings settings, IEnumerable<GameRecord> games)
    {
        var existing = games.Where(g => g.Installed).Select(g => (Game: g, Identity: LaunchBoxService.FindGameIdentity(settings, g)))
            .Where(x => x.Identity is { Duplicate: false } && !string.IsNullOrWhiteSpace(x.Identity.GameGuid));
        var parents = existing.GroupBy(x => x.Identity!.GameGuid, StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            var defaults = group.Where(x => !x.Identity!.IsAdditionalApplication).Select(x => x.Game).ToList();
            var candidates = defaults.Count > 0 ? defaults : group.Select(x => x.Game).ToList();
            return candidates.OrderBy(x => GameSelectionPolicy.RegionRank(x.Name)).ThenBy(GameSelectionPolicy.LoaderRank)
                .ThenByDescending(x => x.PathsValid).ThenBy(x => x.Id, StringComparer.OrdinalIgnoreCase).First();
        });
        return GameSelectionPolicy.SelectPreferred(parents).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public MediaAssets FindAssets(AppSettings settings, GameRecord game, string? databaseId = null, IEnumerable<string>? aliases = null)
    {
        var folders = LaunchBoxService.DiscoverMediaFolders(settings);
        var identity = LaunchBoxService.FindGameIdentity(settings, game);
        var title = identity is { Duplicate: false } && !string.IsNullOrWhiteSpace(identity.Title) ? identity.Title : game.Name;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Canonical(title), Canonical(game.Name), Canonical(game.Id) };
        if (identity is { Duplicate: false })
        {
            if (identity.DatabaseId is > 0) names.Add(identity.DatabaseId.Value.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(identity.GameGuid)) names.Add(Canonical(identity.GameGuid));
        }
        if (!string.IsNullOrWhiteSpace(databaseId)) names.Add(Canonical(databaseId));
        foreach (var alias in aliases ?? []) if (!string.IsNullOrWhiteSpace(alias)) names.Add(Canonical(alias));
        names.Remove("");
        string Folder(string key, string fallback) => folders.TryGetValue(key, out var found) && !string.IsNullOrWhiteSpace(found) ? Path.GetFullPath(found) : Path.GetFullPath(Path.Combine(settings.LaunchBoxPath, fallback, settings.PlatformName));
        var snapFolder = Folder("Video", "Videos");
        var themeFolder = folders.TryGetValue("Theme Video", out var theme) && !string.IsNullOrWhiteSpace(theme) ? Path.GetFullPath(theme) : Path.Combine(snapFolder, "Theme");
        string[] Find(string key) => FindMatches(folders.TryGetValue(key, out var directory) ? directory : Path.Combine(settings.LaunchBoxPath, "Images", settings.PlatformName, key), names, Images, true).Take(8).ToArray();
        var fanart = Find("Fanart - Background");
        var flyers = Find("Advertisement Flyer - Front");
        var box = Find("Box - Front");
        var screenshots = Find("Screenshot - Gameplay");
        var assetsRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(settings.ThemeAssetsPath) ? Path.Combine(settings.LaunchBoxPath, "Images", settings.PlatformName, "Theme Assets") : settings.ThemeAssetsPath);
        var packDirectories = new[] { game.Id, identity is { Duplicate: false } ? identity.GameGuid : "" }
            .Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => Path.Combine(assetsRoot, SafeFileName(id!, "unknown"))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var packFiles = IsPlainDirectory(assetsRoot) ? packDirectories.Where(IsPlainDirectory).SelectMany(p => IndexedFiles(p, false)).ToArray() : [];
        string Pack(string name) => packFiles.FirstOrDefault(p => Images.Contains(Path.GetExtension(p)) && Path.GetFileNameWithoutExtension(p).Equals(name, StringComparison.OrdinalIgnoreCase)) ?? "";
        var backgroundCandidates = new[] { (Path: Pack("background"), Kind: "Theme asset"), (Path: First(fanart), Kind: "Fanart"), (Path: First(flyers), Kind: "Flyer"), (Path: First(box), Kind: "Box front"), (Path: First(screenshots), Kind: "Screenshot") };
        var background = backgroundCandidates.FirstOrDefault(x => x.Path.Length > 0);
        var cutouts = packFiles.Where(p => Path.GetExtension(p).Equals(".png", StringComparison.OrdinalIgnoreCase) && Path.GetFileNameWithoutExtension(p).StartsWith("cutout", StringComparison.OrdinalIgnoreCase))
            .Where(HasTransparency).Take(6).ToList();
        var result = new MediaAssets
        {
            ProfileId = game.Id, Title = title,
            Background = background.Path ?? "", BackgroundKind = background.Kind ?? "",
            ArtworkSources = flyers.Concat(box).Concat(fanart).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList(),
            Cutouts = cutouts, ThemeAssetDirectory = packDirectories.FirstOrDefault(IsPlainDirectory) ?? packDirectories.FirstOrDefault() ?? assetsRoot,
            Logo = First(Pack("logo"), First(Find("Clear Logo"))),
            Snap = FindMatches(snapFolder, names, Videos, false).FirstOrDefault(NonemptyFile) ?? "",
            Theme = FindMatches(themeFolder, names, Videos, false).FirstOrDefault() ?? "",
            ThemeDestination = Path.Combine(themeFolder, SafeFileName(title, game.Id) + ".mp4")
        };
        // A collision is preserved even if its name did not enter the matching index.
        if (string.IsNullOrWhiteSpace(result.Theme) && File.Exists(result.ThemeDestination)) result.Theme = result.ThemeDestination;
        relatedThemes.Find(settings, game, identity, databaseId, result);
        game.ArtworkStatus = !string.IsNullOrEmpty(result.Background) || !string.IsNullOrEmpty(result.Logo) || result.Cutouts.Count > 0 ? "Local artwork found" : "Missing artwork";
        game.ThemeStatus = !string.IsNullOrEmpty(result.Theme) ? "Existing theme" : HasReusableTheme(result) ? "Existing theme from " + result.ReusableThemePlatform : HasVideoSnap(result) ? "Can render from gameplay" : "Missing video snap";
        return result;
    }

    /// <summary>A discovered candidate is copied only by an explicitly requested theme job.</summary>
    public static bool HasReusableTheme(MediaAssets assets) => !string.IsNullOrWhiteSpace(assets.ReusableTheme);
    internal static IReadOnlyList<string> RelatedThemeFolders(AppSettings settings, string platform) => RelatedThemeDiscovery.ResolveFolders(settings, platform);

    /// <summary>Cheap readiness check; the renderer probes the file's video stream before preparing any artwork.</summary>
    public static bool HasVideoSnap(MediaAssets assets) => NonemptyFile(assets.Snap);
    private static bool NonemptyFile(string path)
    {
        try { return !string.IsNullOrWhiteSpace(path) && File.Exists(path) && new FileInfo(path).Length > 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    public void ClearCache() { indexes.Clear(); transparency.Clear(); relatedThemes.ClearCache(); }

    private bool HasTransparency(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!transparency.TryGetValue(path, out var cached) || cached.Stamp != info.LastWriteTimeUtc || cached.Length != info.Length)
                transparency[path] = cached = (info.LastWriteTimeUtc, info.Length, ThemeCutoutService.HasUsableTransparency(path));
            return cached.Usable;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private IEnumerable<string> FindMatches(string folder, HashSet<string> names, HashSet<string> extensions, bool includeRegionFolders)
    {
        return IndexedFiles(folder, includeRegionFolders).Where(f => extensions.Contains(Path.GetExtension(f)) && (names.Contains(Canonical(Path.GetFileNameWithoutExtension(f))) || names.Contains(Canonical(RemoveMediaIndex(Path.GetFileNameWithoutExtension(f))))))
            .OrderBy(f => Path.GetDirectoryName(f)?.Equals(folder, StringComparison.OrdinalIgnoreCase) == true ? 0 : 1)
            .ThenBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).ThenBy(f => f, StringComparer.OrdinalIgnoreCase);
    }

    private string[] IndexedFiles(string folder, bool includeRegionFolders)
    {
        if (string.IsNullOrWhiteSpace(folder) || !IsPlainDirectory(folder)) return [];
        try
        {
            var directory = new DirectoryInfo(folder);
            var stamp = directory.LastWriteTimeUtc;
            var key = folder + "|" + includeRegionFolders;
            if (!indexes.TryGetValue(key, out var cache) || cache.Stamp != stamp)
            {
                var files = Directory.EnumerateFiles(folder).Where(IsPlainFile).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
                if (includeRegionFolders)
                    foreach (var region in Directory.EnumerateDirectories(folder).Where(IsPlainDirectory).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                        files.AddRange(Directory.EnumerateFiles(region).Where(IsPlainFile));
                cache = (stamp, files.ToArray()); indexes[key] = cache;
            }
            return cache.Files;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }
    private static bool IsPlainDirectory(string path) { try { return Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; } }
    private static bool IsPlainFile(string path) { try { return File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; } }
    // LaunchBox appends a two-digit media index. Never remove edition/region/version suffixes.
    private static string RemoveMediaIndex(string name) => Regex.Replace(name, @"-\d{2}$", "");
    private static string First(params string[] paths) => paths.FirstOrDefault(p => !string.IsNullOrEmpty(p)) ?? "";
    private static string Canonical(string name) => Regex.Replace(SafeFileName(name, "").Normalize(), @"\s+", " ").Trim().ToUpperInvariant();
    internal static string SafeFileName(string text, string fallback)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*"));
        var value = new string(text.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (value.Length > 160) value = value[..160].TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(value) || value is "." or "..") value = fallback;
        if (Regex.IsMatch(value, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase)) value = "_" + value;
        return value;
    }
}
