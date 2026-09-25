using ArcadeLibraryManager.Core;

public static class GameSelectionChecks {
    static void Check(bool value, string reason) { if(!value) throw new Exception("Game selection check failed: " + reason); }
    static GameRecord Game(string id, string title, bool ready = false, bool installed = false) => new() { Id = id, Name = title, PathsValid = ready, Installed = installed };
    public static object Run() {
        var totalVice = new[] { Game("totlvicj", "Total Vice (Japan)", true, true), Game("totlvice", "Total Vice (Europe)", true, true), Game("totlvica", "Total Vice (Asia)", true, true), Game("totlvicu", "Total Vice (USA)") };
        Check(GameSelectionPolicy.SelectPreferred(totalVice).Single().Id == "totlvicu", "USA wins over installed other locales");
        var choices = totalVice.Append(Game("world", "Total Vice (World)")).ToArray();
        Check(GameSelectionPolicy.SelectPreferred(choices.Where(g => g.Id != "totlvicu")).Single().Id == "world", "World fallback after unavailable USA is filtered");
        Check(GameSelectionPolicy.SelectPreferred(totalVice.Where(g => g.Id != "totlvicu")).Single().Id == "totlvica", "stable other-locale fallback");
        Check(GameSelectionPolicy.RegionRank("500 GP (US, 5GP3 Ver. C)") == 0, "US board code recognizes USA rank");
        Check(GameSelectionPolicy.RegionRank("Dead Or Alive ++ (Japan/USA/Export)") == 0, "multi-region USA wins");
        Check(GameSelectionPolicy.RegionRank("Ace Driver (World, AD2)") == 1, "World board annotation");
        Check(GameSelectionPolicy.RegionRank("Cruis'n World (v2.5 / v2.4)") == 2, "World in actual game name is not a region");
        Check(GameSelectionPolicy.RegionRank("Daytona USA (Revision A)") == 2, "USA in actual game name is not a region");
        Check(GameSelectionPolicy.RegionRank("Truck Kyosokyoku (US?, TKK2/VER.A)") == 2, "uncertain region does not receive USA preference");
        var revisions = new[] { Game("vf5", "Virtua Fighter 5"), Game("vf5b", "Virtua Fighter 5 (Version B)"), Game("vf5c", "Virtua Fighter 5 (Version C) ElfLoader 2", true, true), Game("vf5elf", "Virtua Fighter 5 (Version B) (ElfLoader 2)") };
        Check(GameSelectionPolicy.SelectPreferred(revisions).Single().Id == "vf5c", "minor revision and loader variants collapse with ready preference");
        var ready = new[] { Game("new", "Game (World, Revision A)"), Game("installed", "Game (World, Revision B)", false, true), Game("ready", "Game (World, Revision C)", true, true) };
        Check(GameSelectionPolicy.SelectPreferred(ready).Single().Id == "ready", "ready tie-break");
        Check(GameSelectionPolicy.SelectPreferred(ready.Take(2)).Single().Id == "installed", "installed tie-break");
        foreach(var (variant, plain) in new[] {
            ("Initial D: Arcade Stage Zero Ver.2", "Initial D: Arcade Stage Zero"),
            ("Street Fighter V: Type Arcade Version 3.53 / 4.12", "Street Fighter V: Type Arcade"),
            ("Tekken 2 Ver.B (World, TES2/VER.D)", "Tekken 2"),
            ("Aero Fighters Special (VER 1.00G)", "Aero Fighters Special (USA)"),
            ("The House of the Dead 4 (Elfloader2)", "The House of the Dead 4")
        }) Check(GameSelectionPolicy.FamilyKey(variant) == GameSelectionPolicy.FamilyKey(plain), "recognized release suffix: " + variant);
        foreach(var (first, second) in new[] {
            ("Tekken", "Tekken 2"), ("Tekken", "Tekken 5.1"), ("The House of the Dead 4", "The House of the Dead 4 Special"),
            ("Virtua Fighter 5", "Virtua Fighter 5 Final Showdown"), ("Street Fighter EX", "Street Fighter EX Plus"),
            ("Moto GP (2015)", "Moto GP (2000)"), ("Initial D: The Arcade (Season 3)", "Initial D: The Arcade (Season 5)"),
            ("Melty Blood", "Melty Blood (RE2)"), ("Densha de Go!!", "Densha de Go!! (DDG 1 & DDG 2 Retro)"),
            ("Guilty Gear Xrd", "Guilty Gear Xrd REV2"), ("RayStorm", "Ray Storm"),
            ("A Game (USA Special Edition)", "A Game (USA)")
        }) Check(GameSelectionPolicy.FamilyKey(first) != GameSelectionPolicy.FamilyKey(second), "distinct edition/sequel preserved: " + second);
        var voyager = new[] { Game("old", "Star Trek Voyager", true, true), Game("new", "Star Trek Voyager (ElfLoader 2)") };
        Check(GameSelectionPolicy.SelectPreferred(voyager).Single().Id == "new", "ELF Loader 2 preferred over ready original");
        Check(GameSelectionPolicy.IsElfLoader2(new GameRecord { Name = "A Game", Emulator = "ElfLdr2" }), "ELF2 emulator enum recognized without title marker");
        Check(GameSelectionPolicy.IsElfLoader2(Game("elf", "A Game (ELF Loader 2)")), "spaced ELF title recognized");
        Check(!GameSelectionPolicy.IsElfLoader2(Game("old", "Star Trek Voyager (ElfLoader 1)")), "original loader not classified as ELF2");
        Check(GameSelectionPolicy.LoaderFamilyKey("Star Trek Voyager (ELF)") == GameSelectionPolicy.LoaderFamilyKey("Star Trek Voyager (ELF Loader)"), "original ELF title forms pair consistently");
        Check(GameSelectionPolicy.FamilyKey("Star Trek Voyager (ELF)") == GameSelectionPolicy.FamilyKey("Star Trek Voyager (ELF Loader 2)"), "ELF forms share game family");
        Check(GameSelectionPolicy.LoaderFamilyKey(voyager[0].Name) == GameSelectionPolicy.LoaderFamilyKey(voyager[1].Name), "Voyager loader pair keys match");
        Check(GameSelectionPolicy.LoaderFamilyKey("Initial D 4 (Japan) (ElfLoader 2)") == GameSelectionPolicy.LoaderFamilyKey("Initial D 4 (Japan)"), "loader key retains matching region");
        Check(GameSelectionPolicy.LoaderFamilyKey("Initial D 4 (Japan) (ElfLoader 2)") != GameSelectionPolicy.LoaderFamilyKey("Initial D 4 (USA)"), "loader key never crosses locales");
        Check(GameSelectionPolicy.LoaderFamilyKey("Virtua Fighter 5 (Version B) (ElfLoader 2)") != GameSelectionPolicy.LoaderFamilyKey("Virtua Fighter 5 (Version C)"), "loader key never crosses revisions");
        Check(GameSelectionPolicy.SelectPreferred(new[] { Game("us", "Sample (USA)"), Game("worldElf", "Sample (World) (ElfLoader 2)") }).Single().Id == "us", "region preference precedes loader preference");
        var all = choices.Concat(revisions).Concat(ready).Concat(voyager).ToArray();
        var expected = GameSelectionPolicy.SelectPreferred(all).Select(g => g.Id).ToArray();
        Check(expected.SequenceEqual(GameSelectionPolicy.SelectPreferred(all.Reverse()).Select(g => g.Id)), "input-order independence");
        Check(GameSelectionPolicy.PreferredByFamily(totalVice)[GameSelectionPolicy.FamilyKey("Total Vice")].Id == "totlvicu", "family dictionary uses public key");
        Check(GameSelectionPolicy.FamilyKey("  TOTAL   VICE (Japan) ") == GameSelectionPolicy.FamilyKey("Total Vice (USA)"), "case/spacing normalization");
        return new { Passed = true, Scenarios = "USA, World and other fallback; revisions/loaders; readiness tie-break; stable ordering; distinct editions and sequels" };
    }
}