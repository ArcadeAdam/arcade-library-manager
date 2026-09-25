using System.Text.RegularExpressions;

namespace ArcadeLibraryManager.Core;

/// <summary>Filters catalog metadata only; it never infers genres from a game's title or changes the record.</summary>
public static class LibraryGenreFilter
{
    public const string AllGenres = "All genres";
    public const string UnspecifiedGenre = "Unspecified";

    // Keep actual TeknoParrot categories distinct. Only the explicit racing aliases share a choice.
    private static readonly Dictionary<string, string> Labels = new[] {
        "Action", "Arcade", "Ball Shooter", "Beat'em up", "Blocks", "Bowling", "Card", "Compilation", "Dino Fighter",
        "Fighting", "Fishing", "Flying", "Interactive Movie", "Minigames", "Motion Shooter", "Multiplayer", "Other",
        "Pacman", "Platform", "Platformer", "Puzzle", "Quizz", "Racing", "Redemption", "Rhythm", "Runner",
        "Shoot 'Em Up", "Shoot'em Up'", "Shooter", "Simulation", "Sport", "Sports", "Strategy", "Sushi", "Touch"
    }.ToDictionary(label => label, label => label, StringComparer.OrdinalIgnoreCase);

    /// <summary>Splits comma, semicolon, slash, pipe or newline lists; blank/unknown-only metadata returns Unspecified.</summary>
    public static IReadOnlyList<string> GetGenres(GameRecord game)
    {
        ArgumentNullException.ThrowIfNull(game);
        var raw = Regex.Replace(game.Genre ?? "", @"\bN\s*/\s*A\b", UnspecifiedGenre, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var genres = Regex.Split(raw, @"[,;/|\r\n]+").Select(Normalize)
            .Where(label => label.Length > 0 && label != UnspecifiedGenre)
            .GroupBy(label => label, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Order(StringComparer.Ordinal).First())
            .Order(StringComparer.OrdinalIgnoreCase).ThenBy(label => label, StringComparer.Ordinal).ToArray();
        return genres.Length == 0 ? [UnspecifiedGenre] : genres;
    }

    /// <summary>All genres first, available categories alphabetically, and Unspecified last only when needed.</summary>
    public static IReadOnlyList<string> GetAvailableGenres(IEnumerable<GameRecord> games)
    {
        ArgumentNullException.ThrowIfNull(games);
        var labels = games.SelectMany(GetGenres).GroupBy(label => label, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Order(StringComparer.Ordinal).First()).ToArray();
        return new[] { AllGenres }.Concat(labels.Where(label => label != UnspecifiedGenre)
            .Order(StringComparer.OrdinalIgnoreCase).ThenBy(label => label, StringComparer.Ordinal))
            .Concat(labels.Contains(UnspecifiedGenre) ? [UnspecifiedGenre] : Array.Empty<string>()).ToArray();
    }

    /// <summary>Null, blank or All genres means no genre restriction. Other choices match a complete category, not a substring.</summary>
    public static bool Matches(GameRecord game, string? genre)
    {
        ArgumentNullException.ThrowIfNull(game);
        var selected = Regex.Replace(genre ?? "", @"\s+", " ").Trim();
        if (selected.Length == 0 || selected.Equals(AllGenres, StringComparison.OrdinalIgnoreCase)) return true;
        selected = Normalize(selected);
        return GetGenres(game).Contains(selected, StringComparer.OrdinalIgnoreCase);
    }

    private static string Normalize(string value)
    {
        var label = Regex.Replace(value, @"\s+", " ").Trim();
        if (label.Length == 0 || label.Equals("Unknown", StringComparison.OrdinalIgnoreCase) || label.Equals(UnspecifiedGenre, StringComparison.OrdinalIgnoreCase)
            || label.Equals("N/A", StringComparison.OrdinalIgnoreCase)) return UnspecifiedGenre;
        if (label.Equals("Racer", StringComparison.OrdinalIgnoreCase) || label.Equals("Driving", StringComparison.OrdinalIgnoreCase)) return "Racing";
        return Labels.GetValueOrDefault(label, label);
    }
}
