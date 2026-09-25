using ArcadeLibraryManager.Core;

public static class LibraryGenreFilterChecks
{
    public static List<string> Run()
    {
        var racing = new GameRecord { Id = "500gp", Name = "500 GP", Genre = "Racing" };
        var racer = new GameRecord { Id = "acedriv3", Name = "Ace Driver 3", Genre = "Racer" };
        var driving = new GameRecord { Id = "driving", Genre = "Driving" };
        var flightSimulation = new GameRecord { Id = "ainferno", Name = "Air Inferno", Genre = "Simulation" };
        var topLanding = new GameRecord { Id = "topland", Name = "Top Landing", Genre = "Simulation" };
        var misleadingTitle = new GameRecord { Id = "title-only", Name = "Super Racing Driver Simulator", Genre = "Action" };
        foreach (var game in new[] { racing, racer, driving })
            Check(LibraryGenreFilter.Matches(game, "Racing") && LibraryGenreFilter.Matches(game, " racer ") && LibraryGenreFilter.GetGenres(game).SequenceEqual(new[] { "Racing" }), "exact Racing/Racer/Driving aliases share the Racing choice");
        Check(!LibraryGenreFilter.Matches(flightSimulation, "Racing") && !LibraryGenreFilter.Matches(topLanding, "Driving") && !LibraryGenreFilter.Matches(misleadingTitle, "Racing"), "Simulation and racing words in titles never imply the Racing genre");
        Check(racer.Genre == "Racer" && driving.Genre == "Driving" && misleadingTitle.Genre == "Action", "filtering never rewrites the catalog metadata");

        var compound = new GameRecord { Genre = "  action ; RACER /  Driving, motion   shooter | Racing\r\nPuzzle  " };
        Check(LibraryGenreFilter.GetGenres(compound).SequenceEqual(new[] { "Action", "Motion Shooter", "Puzzle", "Racing" }), "compound lists normalize spacing, casing and racing aliases without duplicate categories");
        foreach (var category in new[] { "action", " motion  SHOOTER ", "Racing", "Puzzle" }) Check(LibraryGenreFilter.Matches(compound, category), "compound genre matches its full category: " + category);
        Check(!LibraryGenreFilter.Matches(compound, "Shooter") && !LibraryGenreFilter.Matches(new() { Genre = "Dino Fighter" }, "Fighting"), "compound labels match exact categories instead of substrings or related words");
        foreach (var observed in new[] { "Shoot 'Em Up", "Shoot'em Up'", "Beat'em up", "Sport", "Sports", "Platform", "Platformer", "Sushi", "Other" })
            Check(LibraryGenreFilter.GetGenres(new() { Genre = observed }).SequenceEqual(new[] { observed }), "observed non-racing categories retain their exact meaning: " + observed);
        Check(!LibraryGenreFilter.Matches(new() { Genre = "Sport" }, "Sports") && !LibraryGenreFilter.Matches(new() { Genre = "Platform" }, "Platformer"), "unrequested category aliases are not merged");
        Check(LibraryGenreFilter.GetGenres(new() { Genre = "Rhythm & Music" }).Single() == "Rhythm & Music", "an ampersand inside a genre label is retained rather than guessed to be a list");

        var unspecified = new[] { new GameRecord(), new() { Genre = " \t\r\n " }, new() { Genre = "Unknown" }, new() { Genre = "Unspecified" }, new() { Genre = "N/A" }, new() { Genre = "; / |" } };
        foreach (var game in unspecified)
            Check(LibraryGenreFilter.GetGenres(game).SequenceEqual(new[] { LibraryGenreFilter.UnspecifiedGenre }) && LibraryGenreFilter.Matches(game, "Unspecified") && LibraryGenreFilter.Matches(game, "unknown"), "missing or unknown-only metadata is available through Unspecified");
        Check(!LibraryGenreFilter.Matches(new() { Genre = "Other" }, "Unspecified"), "Other is a real catalog category, not missing metadata");
        var partlyKnown = new GameRecord { Genre = "Unknown; Racing | N/A" };
        Check(LibraryGenreFilter.GetGenres(partlyKnown).SequenceEqual(new[] { "Racing" }) && !LibraryGenreFilter.Matches(partlyKnown, "Unspecified"), "unknown placeholders do not obscure a known category");
        foreach (var filter in new string?[] { null, "", " \t", "All genres", " all   GENRES " })
            Check(LibraryGenreFilter.Matches(compound, filter) && LibraryGenreFilter.Matches(unspecified[0], filter), "All genres includes known and missing genres");

        var games = new[] { new GameRecord { Genre = " custom   Genre " }, flightSimulation, racing, racer, compound, new() { Genre = "CUSTOM GENRE" }, unspecified[0] };
        var before = games.Select(game => (game.Name, game.Genre)).ToArray();
        var choices = LibraryGenreFilter.GetAvailableGenres(games);
        Check(choices.SequenceEqual(new[] { "All genres", "Action", "CUSTOM GENRE", "Motion Shooter", "Puzzle", "Racing", "Simulation", "Unspecified" }), "choices are deterministic, deduplicated and ordered with All first and Unspecified last");
        Check(choices.SequenceEqual(LibraryGenreFilter.GetAvailableGenres(games.Reverse())), "catalog enumeration order cannot change the available genre choices");
        Check(before.SequenceEqual(games.Select(game => (game.Name, game.Genre))), "choice generation leaves all record text unchanged");
        Check(LibraryGenreFilter.GetAvailableGenres(Array.Empty<GameRecord>()).SequenceEqual(new[] { "All genres" }), "an empty library has only the unfiltered choice");
        Check(!LibraryGenreFilter.GetAvailableGenres(new[] { racing, racer }).Contains("Unspecified"), "a known-only catalog does not invent missing-genre games");
        return ["Observed Racing/Racer labels and explicit Driving alias match without including flight simulation or guessing from titles",
            "Explicit multi-genre separators, case, spacing and duplicates normalize while distinct categories remain exact",
            "Missing metadata has an Unspecified choice and All genres remains inclusive",
            "Available choices are deterministic, stable across catalog order and leave game metadata untouched"];
    }

    private static void Check(bool value, string reason) { if (!value) throw new Exception("Library genre filter check failed: " + reason); }
}
