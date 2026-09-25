using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ArcadeLibraryManager.Core;

/// <summary>Read-only, exact-identity reuse of existing classic-platform theme videos.</summary>
internal sealed class RelatedThemeDiscovery
{
    private static readonly HashSet<string> ClassicTypes = new(StringComparer.OrdinalIgnoreCase)
        { "TeknoS11", "TeknoS21", "TeknoS22", "TeknoS23", "TeknoModel1", "TeknoHNG64", "TeknoGClub", "TeknoHornet", "TeknoViper", "TeknoCobra", "TeknoAir", "TeknoAGX", "TeknoVUnit", "TeknoZeus", "TeknoVegas" };
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".avi", ".webm", ".mov", ".flv", ".wmv", ".mpg", ".mpeg" };
    private readonly Dictionary<string, Snapshot> snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTime Stamp, string[] Files)> fileLists = new(StringComparer.OrdinalIgnoreCase);
    private sealed record SourceGame(string Id, string Title, string RomStem, int? DatabaseId);
    private sealed record Snapshot((bool Exists, DateTime Stamp, long Length) PlatformStamp,
        (bool Exists, DateTime Stamp, long Length) ConfigStamp, SourceGame[] Games, string[] Folders);
    private sealed record MediaMatch(string Path, int Index, bool HasDatabaseId);

    public void ClearCache() { snapshots.Clear(); fileLists.Clear(); }

    public void Find(AppSettings settings, GameRecord game, LaunchBoxIdentity? identity, string? databaseId, MediaAssets assets)
    {
        if (!string.IsNullOrWhiteSpace(assets.Theme)) return;
        var platform = game.Emulator.Trim() switch
        {
            var type when type.Equals("TeknoModel2", StringComparison.OrdinalIgnoreCase) || type.Equals("TeknoM2", StringComparison.OrdinalIgnoreCase) => "Sega Model 2",
            var type when ClassicTypes.Contains(type) => "Arcade",
            _ => ""
        };
        if (platform.Length == 0 || settings.PlatformName.Equals(platform, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var snapshot = GetSnapshot(settings, platform);
            var profileStem = Normalize(game.Id);
            var zipStems = new[] { ZipStem(game.ExecutableName), ZipStem(game.GamePath) }
                .Where(x => x.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var sourceGames = snapshot.Games.Where(x => Normalize(x.RomStem) == profileStem && profileStem.Length > 0).ToArray();
            var matchReason = "matching ROM name";
            if (sourceGames.Length == 0) sourceGames = snapshot.Games.Where(x => zipStems.Contains(Normalize(x.RomStem))).ToArray();
            var db = identity is { Duplicate: false, DatabaseId: > 0 } ? identity.DatabaseId
                : int.TryParse(databaseId, NumberStyles.None, CultureInfo.InvariantCulture, out var suppliedId) && suppliedId > 0 ? suppliedId : (int?)null;
            if (sourceGames.Length == 0 && db is > 0)
            {
                sourceGames = snapshot.Games.Where(x => x.DatabaseId == db).ToArray();
                matchReason = "shared LaunchBox database ID";
                if (sourceGames.Any(x => ConflictingRegion(assets.Title, x.Title)))
                {
                    assets.ThemeReuseDetail = $"Existing {platform} themes need review: the shared database ID belongs to a different region.";
                    return;
                }
            }
            if (sourceGames.Length > 1)
            {
                assets.ThemeReuseDetail = $"Existing {platform} themes need review: more than one source game matches the {matchReason}.";
                return;
            }
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int? mediaDatabaseId;
            if (sourceGames.Length == 1)
            {
                var source = sourceGames[0];
                AddName(names, source.Title); AddName(names, source.RomStem);
                if (Guid.TryParse(source.Id, out var sourceGuid))
                {
                    AddName(names, source.Id); AddName(names, sourceGuid.ToString("D"));
                }
                mediaDatabaseId = source.DatabaseId;
            }
            else
            {
                // An absent platform XML is common for old Model 2 media. Exact filename aliases
                // remain useful, but edition/region-qualified source filenames are never reduced.
                AddName(names, assets.Title); AddName(names, game.Name); AddName(names, game.Id);
                foreach (var stem in zipStems) AddName(names, stem);
                AddUnqualifiedName(names, assets.Title); AddUnqualifiedName(names, game.Name);
                mediaDatabaseId = db;
                var sameTitle = snapshot.Games.Where(x => names.Contains(Normalize(x.Title))).ToArray();
                if (sameTitle.Length > 1)
                {
                    assets.ThemeReuseDetail = $"Existing {platform} themes need review: the filename title identifies multiple source games.";
                    return;
                }
                matchReason = "exact media filename";
            }
            var matches = snapshot.Folders.SelectMany(IndexedFiles).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => Match(path, names, mediaDatabaseId)).Where(x => x != null).Cast<MediaMatch>()
                .Where(x => NonemptyPlainFile(x.Path))
                .OrderBy(x => x.Index).ThenByDescending(x => x.HasDatabaseId)
                .ThenBy(x => ExtensionRank(x.Path)).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray();
            if (matches.Length == 0) return;
            var selected = matches[0].Path;
            var destination = Path.Combine(Path.GetDirectoryName(assets.ThemeDestination)!,
                Path.GetFileNameWithoutExtension(assets.ThemeDestination) + Path.GetExtension(selected));
            if (File.Exists(destination) || Directory.Exists(destination))
            {
                assets.ThemeReuseDetail = "Existing theme destination is already occupied; it will be preserved.";
                return;
            }
            var info = new FileInfo(selected);
            assets.ReusableTheme = selected;
            assets.ReusableThemePlatform = platform;
            assets.ReusableThemeLength = info.Length;
            assets.ReusableThemeLastWriteUtc = info.LastWriteTimeUtc;
            assets.ThemeReuseDestination = destination;
            assets.ThemeReuseDetail = $"Reuse existing {platform} theme ({matchReason}); the source video stays in place.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or XmlException or InvalidOperationException)
        {
            assets.ThemeReuseDetail = $"Existing {platform} themes could not be checked: {ex.Message}";
        }
    }

    private Snapshot GetSnapshot(AppSettings settings, string platform)
    {
        var platformPath = Path.GetFullPath(Path.Combine(settings.LaunchBoxPath, "Data", "Platforms", platform + ".xml"));
        var configPath = Path.Combine(settings.LaunchBoxPath, "Data", "Platforms.xml");
        var platformStamp = Stamp(platformPath); var configStamp = Stamp(configPath);
        if (snapshots.TryGetValue(platformPath, out var cached) && cached.PlatformStamp == platformStamp && cached.ConfigStamp == configStamp) return cached;
        var games = platformStamp.Exists ? ReadXml(platformPath).Root?.Elements("Game").Select(node => new SourceGame(
            Value(node, "ID"), Value(node, "Title"), FileStem(Value(node, "ApplicationPath")),
            int.TryParse(Value(node, "DatabaseID"), out var db) && db > 0 ? db : null)).ToArray() ?? [] : [];
        if (games.Length > 100000) throw new IOException("The source platform contains too many games to index safely.");
        var snapshot = new Snapshot(platformStamp, configStamp, games, ResolveFolders(settings, platform).ToArray());
        // NTFS can defer a directory timestamp update after a newly written media file.
        // Changed platform/config metadata requires a fresh corresponding file list too.
        foreach (var folder in snapshot.Folders.Concat(cached?.Folders ?? []).Distinct(StringComparer.OrdinalIgnoreCase)) fileLists.Remove(folder);
        if (snapshots.Count >= 8) snapshots.Clear();
        snapshots[platformPath] = snapshot;
        return snapshot;
    }

    internal static IReadOnlyList<string> ResolveFolders(AppSettings settings, string platform)
    {
        var sourceSettings = new AppSettings { LaunchBoxPath = settings.LaunchBoxPath, PlatformName = platform, TeknoParrotPath = settings.TeknoParrotPath };
        var folders = LaunchBoxService.DiscoverMediaFolders(sourceSettings);
        var config = Path.Combine(settings.LaunchBoxPath, "Data", "Platforms.xml");
        var explicitTheme = false; var explicitVideo = false;
        if (File.Exists(config))
        {
            var document = ReadXml(config);
            explicitVideo = document.Root?.Elements("Platform").Any(x => Value(x, "Name").Equals(platform, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(Value(x, "VideosFolder"))) == true;
            foreach (var node in document.Root?.Elements("PlatformFolder").Where(x => Value(x, "Platform").Equals(platform, StringComparison.OrdinalIgnoreCase)) ?? [])
            {
                if (string.IsNullOrWhiteSpace(Value(node, "FolderPath"))) continue;
                explicitTheme |= Value(node, "MediaType").Equals("Theme Video", StringComparison.OrdinalIgnoreCase);
                explicitVideo |= Value(node, "MediaType").Equals("Video", StringComparison.OrdinalIgnoreCase);
            }
        }
        // A PlatformFolder Video override is applied after LaunchBox's default Theme Video path.
        // Derive Theme from the final Video setting unless Theme Video itself was explicitly set.
        var theme = explicitTheme ? folders["Theme Video"] : Path.Combine(folders["Video"], "Theme");
        var result = new List<string> { Path.GetFullPath(theme) };
        var legacy = Path.GetFullPath(Path.Combine(settings.LaunchBoxPath, "Video", platform, "Theme"));
        if (!explicitTheme && !explicitVideo && IsPlainDirectory(legacy)) result.Add(legacy);
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private string[] IndexedFiles(string folder)
    {
        if (!IsPlainDirectory(folder)) return [];
        var stamp = Directory.GetLastWriteTimeUtc(folder);
        if (fileLists.TryGetValue(folder, out var cached) && cached.Stamp == stamp) return cached.Files;
        var files = Directory.EnumerateFiles(folder).Where(p => VideoExtensions.Contains(Path.GetExtension(p)) && IsPlainFile(p)).Take(50001).ToArray();
        if (files.Length > 50000) throw new IOException("The theme folder contains too many files to index safely.");
        if (fileLists.Count >= 16) fileLists.Clear();
        fileLists[folder] = (stamp, files);
        return files;
    }

    private static MediaMatch? Match(string path, HashSet<string> names, int? databaseId)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        if (names.Contains(Normalize(stem))) return new(path, -1, false);
        var indexed = Regex.Match(stem, @"^(?<base>.+)-(?<index>\d{2})$", RegexOptions.CultureInvariant);
        var mediaBase = indexed.Success ? indexed.Groups["base"].Value : stem;
        var index = indexed.Success ? int.Parse(indexed.Groups["index"].Value, CultureInfo.InvariantCulture) : -1;
        if (indexed.Success && names.Contains(Normalize(mediaBase))) return new(path, index, false);
        if (databaseId is not > 0) return null;
        var tagged = Regex.Match(mediaBase, @"^(?<title>.+)-(?<id>[1-9]\d*)$", RegexOptions.CultureInvariant);
        return tagged.Success && int.TryParse(tagged.Groups["id"].Value, out var mediaId) && mediaId == databaseId
            && names.Contains(Normalize(tagged.Groups["title"].Value)) ? new(path, index, true) : null;
    }

    private static void AddName(HashSet<string> names, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        names.Add(Normalize(name));
        names.Add(Normalize(name.Replace('\u2019', '\'').Replace('\u2018', '\'').Replace('\'', '_')));
    }
    private static void AddUnqualifiedName(HashSet<string> names, string title)
    {
        var family = GameSelectionPolicy.FamilyKey(title);
        if (!family.Equals(GameSelectionPolicy.LoaderFamilyKey(title), StringComparison.Ordinal)) AddName(names, family);
    }
    private static bool ConflictingRegion(string target, string source)
    {
        static string Region(string name)
        {
            var tag = Regex.Match(name, @"[\(\[]\s*(USA|US|U\.S\.A\.?|North America|World|Worldwide|Japan|Japanese|Europe|European|Asia|Asian|Korea|Korean|China|Chinese)(?=$|[\s,;/)\]])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return tag.Success ? tag.Groups[1].Value.ToUpperInvariant() switch
            { "USA" or "US" or "U.S.A" or "U.S.A." or "NORTH AMERICA" => "USA", "WORLD" or "WORLDWIDE" => "WORLD", "JAPAN" or "JAPANESE" => "JAPAN", "EUROPE" or "EUROPEAN" => "EUROPE", "ASIA" or "ASIAN" => "ASIA", "KOREA" or "KOREAN" => "KOREA", "CHINA" or "CHINESE" => "CHINA", _ => "" } : "";
        }
        var left = Region(target); var right = Region(source);
        return left.Length > 0 && right.Length > 0 && left != right;
    }
    private static string ZipStem(string path) => Path.GetExtension(path.Trim().Trim('"')).Equals(".zip", StringComparison.OrdinalIgnoreCase) ? Normalize(FileStem(path)) : "";
    private static string FileStem(string path) => Path.GetFileNameWithoutExtension(path.Trim().Trim('"').Replace('\\', '/'));
    private static string Normalize(string value) => Regex.Replace(MediaService.SafeFileName(value, "").Normalize(), @"\s+", " ").Trim().ToUpperInvariant();
    private static int ExtensionRank(string path) => Path.GetExtension(path).ToLowerInvariant() switch { ".mp4" => 0, ".mkv" => 1, ".avi" => 2, _ => 3 };
    private static string Value(XElement node, string name) => node.Element(name)?.Value?.Trim() ?? "";
    private static XDocument ReadXml(string path)
    {
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 128 * 1024 * 1024 });
        return XDocument.Load(reader);
    }
    private static (bool Exists, DateTime Stamp, long Length) Stamp(string path)
    {
        var file = new FileInfo(path); return file.Exists ? (true, file.LastWriteTimeUtc, file.Length) : (false, default, 0);
    }
    private static bool IsPlainDirectory(string path) => Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
    private static bool IsPlainFile(string path) => File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
    private static bool NonemptyPlainFile(string path) => IsPlainFile(path) && new FileInfo(path).Length > 0;
}
