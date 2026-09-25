using System.Text;
using System.Text.RegularExpressions;

namespace ArcadeLibraryManager.Core;

/// <summary>Selects one eligible profile per game without guessing at sequel or edition names.</summary>
public static class GameSelectionPolicy {
    const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant);
    static readonly Regex TrailingTag = new(@"\s*[\(\[](?<tag>[^()\[\]]+)[\)\]]\s*$", RegexOptions.CultureInvariant);
    static readonly Regex LoaderSuffix = new(@"\s+(?:Elf\s*(?:Loader|Ldr)(?:\s*[12])?)\s*$", Options);
    static readonly Regex ElfAnnotation = new(@"\s*[\(\[]\s*Elf(?:\s*(?:Loader|Ldr)(?:\s*[12])?)?\s*[\)\]]", Options);
    static readonly Regex Elf2Annotation = new(@"(?:[\(\[]\s*Elf\s*(?:Loader|Ldr)\s*2\s*[\)\]]|\s+Elf\s*(?:Loader|Ldr)\s*2\s*$)", Options);
    static readonly Regex VersionSuffix = new(@"\s+(?:version\s+|ver(?:\s+|\.\s*)|revision\s+|rev(?:\s+|\.\s*))(?:\d+(?:\.\d+)*[a-z]?|[a-z])(?:\s*/\s*\d+(?:\.\d+)*[a-z]?)*\s*$", Options);
    static readonly Regex Usa = new(@"(?<![a-z])(?:USA|U\.S\.A\.?|US|U\.S\.?|United States(?: of America)?|North America)(?![a-z?])", Options);
    static readonly Regex World = new(@"(?<![a-z])(?:World|Worldwide)(?![a-z])", Options);
    static readonly Regex RegionPrefix = new(@"^(?:USA|U\.S\.A\.?|US|U\.S\.?|United States(?: of America)?|North America|World|Worldwide|International|Export|Europe|European|Asia|Asian|Japan|Japanese|English|Korea|Korean|China|Chinese|Hong Kong|Taiwan|Australia|Brazil|France|French|Germany|German|Italy|Italian|Spain|Spanish|UK|United Kingdom)(?:\??)(?=$|[\s,;/])", Options);
    static readonly Regex VersionTag = new(@"^(?:(?:HD|HDD)\s+)?(?:revision|rev\.?|version|ver\.?|v)(?:\s|\.|(?=\d))", Options);
    static readonly Regex RomVersionTag = new(@"^[a-z0-9*._/-]+\s*(?:/|\s)\s*(?:\d{2,4}/\d{1,2}/\d{1,2}\s+)?(?:ver\.?|version|rev\.?|revision)\s*\.?\s*[a-z0-9]", Options);
    static readonly Regex DecimalTag = new(@"^\d+\.\d+[a-z]?(?:\s*/\s*(?:v?\d+\.\d+[a-z]?|OF))*$", Options);
    static readonly Regex BuildDate = new(@"^(?:\d{6,8}[a-z]?|\d{1,4}/\d{1,2}/\d{1,4})(?:\s.*)?$", Options);
    static readonly Regex KonamiCode = new(@"^(?:G[A-Z*][A-Z0-9]*\s+)?[UEJAK][A-Z]{1,2}(?:\d{2}|:[A-Z])?$", Options);
    static readonly Regex MeaningfulEdition = new(@"\b(?:edition|season|remake|special|retro|prototype|power-up|club|link|mix)\b", Options);

    public static bool IsElfLoader2(GameRecord game) {
        ArgumentNullException.ThrowIfNull(game);
        return string.Equals(game.Emulator?.Trim(), "ElfLdr2", StringComparison.OrdinalIgnoreCase)
            || string.Equals(game.Emulator?.Trim(), "ElfLoader2", StringComparison.OrdinalIgnoreCase)
            || Elf2Annotation.IsMatch(game.Name ?? "");
    }

    public static int LoaderRank(GameRecord game) => IsElfLoader2(game) ? 0 : 1;

    /// <summary>Loader pairing key retains region, revision and edition qualifiers.</summary>
    public static string LoaderFamilyKey(string title) {
        var name = ElfAnnotation.Replace((title ?? "").Trim(), "");
        name = LoaderSuffix.Replace(name, "");
        return NormalizeKey(name);
    }

    static string NormalizeKey(string name) => Whitespace.Replace(name.Normalize(NormalizationForm.FormKC).Replace('\u2019', '\'').Replace('\u2018', '\'').Trim(), " ").ToUpperInvariant();

    public static string FamilyKey(string title) {
        var (name, _) = Parse(title);
        return NormalizeKey(name);
    }

    /// <summary>USA/US is preferred, then World; other and unspecified regions tie.</summary>
    public static int RegionRank(string title) {
        var (_, tags) = Parse(title);
        if(tags.Any(t => Usa.IsMatch(t))) return 0;
        if(tags.Any(t => World.IsMatch(t))) return 1;
        return 2;
    }

    /// <summary>Callers filter unavailable/ineligible profiles before selection, so a missing USA release can fall back.</summary>
    public static IReadOnlyList<GameRecord> SelectPreferred(IEnumerable<GameRecord> games) {
        ArgumentNullException.ThrowIfNull(games);
        return games.GroupBy(g => FamilyKey(g.Name), StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.OrderBy(g => RegionRank(g.Name))
                .ThenBy(LoaderRank).ThenByDescending(g => g.PathsValid).ThenByDescending(g => g.Installed)
                .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Name, StringComparer.Ordinal)
                .ThenBy(g => g.Id, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Id, StringComparer.Ordinal)
                .ThenBy(g => g.UserProfilePath, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.UserProfilePath, StringComparer.Ordinal)
                .ThenBy(g => g.TemplatePath, StringComparer.Ordinal).First())
            .ToArray();
    }

    public static IReadOnlyDictionary<string, GameRecord> PreferredByFamily(IEnumerable<GameRecord> games) =>
        SelectPreferred(games).ToDictionary(g => FamilyKey(g.Name), StringComparer.Ordinal);

    static (string Name, List<string> Tags) Parse(string title) {
        var name = (title ?? "").Trim();
        var tags = new List<string>();
        while(name.Length > 0) {
            var match = TrailingTag.Match(name);
            if(match.Success && IsDescriptor(match.Groups["tag"].Value)) {
                tags.Add(match.Groups["tag"].Value);
                name = name[..match.Index].TrimEnd();
                continue;
            }
            match = LoaderSuffix.Match(name);
            if(!match.Success) match = VersionSuffix.Match(name);
            if(!match.Success) break;
            tags.Add(match.Value.Trim());
            name = name[..match.Index].TrimEnd();
        }
        // An annotation-only or blank name must not erase its identity.
        return (name.Length == 0 ? (title ?? "").Trim() : name, tags);
    }

    static bool IsDescriptor(string text) {
        var tag = Whitespace.Replace(text.Trim(), " ");
        if(MeaningfulEdition.IsMatch(tag)) return false;
        if(Regex.IsMatch(tag, @"^(?:Elf(?:\s*(?:Loader|Ldr)(?:\s*[12])?)?|APM3|eX-Board|Nesica|Third-Party Emulator|Hornet)$", Options)) return true;
        if(VersionTag.IsMatch(tag) || RomVersionTag.IsMatch(tag) || DecimalTag.IsMatch(tag) || KonamiCode.IsMatch(tag)) return true;
        if(Regex.IsMatch(tag, @"^(?:Model 2[AB],\s*Revision [A-Z]|newer)$", Options)) return true;
        var region = RegionPrefix.Match(tag);
        if(!region.Success) return false;
        var rest = tag[region.Length..].TrimStart(' ', ',', '/', ';');
        if(rest.Length == 0) return true;
        if(RegionPrefix.IsMatch(rest)) return IsDescriptor(rest);
        // ROM board/build descriptions after an explicit region are not title subtitles.
        return VersionTag.IsMatch(rest) || RomVersionTag.IsMatch(rest) || DecimalTag.IsMatch(rest)
            || BuildDate.IsMatch(rest) || Regex.IsMatch(rest, @"^[A-Z]{1,5}\d{1,3}$", Options)
            || Regex.IsMatch(rest, @"^Model 2[AB](?:,\s*Revision [A-Z])?$", Options)
            || Regex.IsMatch(rest, @"^(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\.? \d{1,2} \d{4}$", Options);
    }
}