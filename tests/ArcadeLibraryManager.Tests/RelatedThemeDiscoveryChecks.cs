using System.Xml.Linq;
using ArcadeLibraryManager.Core;

public static class RelatedThemeDiscoveryChecks
{
    public static List<string> Run(string root)
    {
        Directory.CreateDirectory(root);
        var passed = new List<string>();
        var rom = Fixture(root, "rom", "crusnusa", "Cruis'n USA", "TeknoVUnit", 42);
        Source(rom, "Arcade", ("source-us", "Cruis'n USA", "crusnusa", 42), ("source-jp", "Cruis'n USA (Japan)", "crusnusj", 42));
        var exact = Video(rom, "Arcade", "Cruis_n USA.mp4");
        Video(rom, "Arcade", "Cruis_n USA-01.mp4");
        Video(rom, "Arcade", "Cruis_n USA-02.mp4");
        var original = Snapshot(rom.Settings.LaunchBoxPath);
        var assets = new MediaService().FindAssets(rom.Settings, rom.Game);
        Check(assets.ReusableTheme == exact && assets.Theme == "" && assets.ReusableThemePlatform == "Arcade", "Exact ROM identity must beat a shared database ID with regional alternatives, and select the bare same-game video.");
        Check(assets.ReusableThemeLength == 3 && assets.ReusableThemeLastWriteUtc == File.GetLastWriteTimeUtc(exact)
            && assets.ThemeReuseDestination == Path.Combine(rom.Settings.LaunchBoxPath, "Videos", "TeknoParrot", "Theme", "Cruis'n USA.mp4"), "Reuse metadata and target naming must preserve the source identity and extension.");
        Check(MediaService.HasReusableTheme(assets) && rom.Game.ThemeStatus == "Existing theme from Arcade", "Reusable themes must be reported independently from already installed themes.");
        Check(SameSnapshot(original, Snapshot(rom.Settings.LaunchBoxPath)), "Theme discovery must not write any file or create any destination directory.");
        passed.Add("Exact ROM identity, apostrophe media aliases, deterministic bare/indexed variants and read-only discovery");

        var db = Fixture(root, "database", "different-profile", "Cruis'n USA", "TeknoVUnit", 42);
        Source(db, "Arcade", ("source", "Cruis'n USA", "different-rom", 42));
        var tagged = Video(db, "Arcade", "Cruis_n USA-42-01.mkv");
        var byDb = new MediaService().FindAssets(db.Settings, db.Game);
        Check(byDb.ReusableTheme == tagged && byDb.ThemeReuseDetail.Contains("shared LaunchBox database ID") && byDb.ThemeReuseDestination.EndsWith(".mkv"), "A shared positive database ID and correctly indexed source title should reuse the actual source format.");
        var wrong = Fixture(root, "wrong-id", "target", "Some Game", "TeknoS11", 42);
        Source(wrong, "Arcade", ("source", "Some Game", "other-rom", 42));
        Video(wrong, "Arcade", "Some Game-999-01.mp4"); Video(wrong, "Arcade", "Unrelated Game-42-01.mp4"); Video(wrong, "Arcade", "42-01.mp4");
        Check(!MediaService.HasReusableTheme(new MediaService().FindAssets(wrong.Settings, wrong.Game)), "Wrong database suffixes, unrelated title prefixes and naked database filenames must not become theme matches.");
        var guid = Fixture(root, "source-guid", "target", "A Title", "TeknoS11");
        const string sourceGuid = "115280e9-dfa3-4722-b451-604d34335ffd";
        Source(guid, "Arcade", (sourceGuid, "Source Title", "target", 1));
        var guidTheme = Video(guid, "Arcade", sourceGuid + "-01.mp4");
        Check(new MediaService().FindAssets(guid.Settings, guid.Game).ReusableTheme == guidTheme, "A verified source game's GUID may identify its indexed theme filename.");
        passed.Add("Shared database identity, indexed title/source-GUID filenames, extension preservation and rejection of unrelated numeric filenames");

        var ambiguous = Fixture(root, "ambiguous-db", "target", "Shared Game", "TeknoS11", 42);
        Source(ambiguous, "Arcade", ("one", "Shared Game", "one", 42), ("two", "Shared Game alternate", "two", 42));
        Video(ambiguous, "Arcade", "Shared Game.mp4");
        var amb = new MediaService().FindAssets(ambiguous.Settings, ambiguous.Game);
        Check(!MediaService.HasReusableTheme(amb) && amb.ThemeReuseDetail.Contains("more than one source game"), "Multiple source identities sharing a database ID require review even if only one currently has a video.");
        var duplicateRom = Fixture(root, "ambiguous-rom", "target", "Shared Game", "TeknoS11");
        Source(duplicateRom, "Arcade", ("one", "Shared Game", "target", 42), ("two", "Shared Game alternate", "target", 43));
        Video(duplicateRom, "Arcade", "Shared Game.mp4");
        Check(new MediaService().FindAssets(duplicateRom.Settings, duplicateRom.Game).ThemeReuseDetail.Contains("more than one source game"), "Duplicate exact source ROM identities must not be guessed.");
        var regional = Fixture(root, "regional", "target", "Total Vice (USA)", "TeknoM2", 42);
        Source(regional, "Sega Model 2", ("other", "Total Vice (Japan)", "other-rom", 42));
        Video(regional, "Sega Model 2", "Total Vice (Japan).mp4");
        var conflict = new MediaService().FindAssets(regional.Settings, regional.Game);
        Check(!MediaService.HasReusableTheme(conflict) && conflict.ThemeReuseDetail.Contains("different region"), "A shared database ID must not override explicit conflicting regions.");
        passed.Add("Ambiguous database/ROM identities and conflicting source regions remain reviewable instead of guessing");

        var model2 = Fixture(root, "model2-no-xml", "overrevb", "Over Rev (Model 2B, Revision B)", "TeknoModel2");
        var modelTheme = Video(model2, "Sega Model 2", "Over Rev-01.mp4");
        Video(model2, "Arcade", "overrevb.mp4");
        var modelAssets = new MediaService().FindAssets(model2.Settings, model2.Game);
        Check(modelAssets.ReusableTheme == modelTheme && modelAssets.ReusableThemePlatform == "Sega Model 2", "An absent Model 2 platform XML must still allow a known descriptor to match an unqualified exact-title theme in the Model 2 folder.");
        model2.Game.Emulator = "TeknoM2";
        Check(new MediaService().FindAssets(model2.Settings, model2.Game).ReusableTheme == modelTheme, "TeknoM2 must route to Sega Model 2 too.");
        var edition = Fixture(root, "edition", "race-special", "Race (Special Edition)", "TeknoModel2");
        Video(edition, "Sega Model 2", "Race-01.mp4");
        Check(!MediaService.HasReusableTheme(new MediaService().FindAssets(edition.Settings, edition.Game)), "Meaningful edition qualifiers must not be removed to manufacture a folder-only match.");
        var qualified = Fixture(root, "qualified-source", "race-usa", "Race (USA)", "TeknoS22");
        Video(qualified, "Arcade", "Race (Japan)-01.mp4");
        Check(!MediaService.HasReusableTheme(new MediaService().FindAssets(qualified.Settings, qualified.Game)), "Qualified source filenames must not be reduced to a generic title that erases their region.");
        var pc = Fixture(root, "pc", "same", "Same Name", "OpenParrot");
        Video(pc, "Arcade", "Same Name.mp4"); Video(pc, "Sega Model 2", "same.mp4");
        foreach (var emulator in new[] { "OpenParrot", "ElfLdr2", "Unknown", "" })
        {
            pc.Game.Emulator = emulator;
            Check(!MediaService.HasReusableTheme(new MediaService().FindAssets(pc.Settings, pc.Game)), "PC and unknown emulator types must not reuse same-name classic media.");
        }
        passed.Add("Model 2 folder-only reuse, known revision descriptors, retained edition/region qualifiers and explicit classic-emulator routing");

        var priority = Fixture(root, "profile-priority", "overrevb", "Over Rev (Model 2B, Revision B)", "TeknoModel2");
        priority.Game.ExecutableName = "overrev.zip"; priority.Game.GamePath = @"R:\Roms\overrev.zip";
        Source(priority, "Sega Model 2", ("clone", "Over Rev (Model 2B, Revision B)", "overrevb", 12), ("parent", "Over Rev (Model 2C, Revision A)", "overrev", 12));
        var cloneVideo = Video(priority, "Sega Model 2", "Over Rev (Model 2B, Revision B).mp4");
        Video(priority, "Sega Model 2", "Over Rev (Model 2C, Revision A).mp4");
        Check(new MediaService().FindAssets(priority.Settings, priority.Game).ReusableTheme == cloneVideo, "An exact clone profile identity must beat its merged parent container alias.");
        var executable = Fixture(root, "zip-alias", "different", "A Game", "TeknoS11");
        executable.Game.ExecutableName = "actual.zip";
        Source(executable, "Arcade", ("source", "A Game", "actual", 1));
        var aliasTheme = Video(executable, "Arcade", "actual.mp4");
        Check(new MediaService().FindAssets(executable.Settings, executable.Game).ReusableTheme == aliasTheme, "A known ZIP executable stem must provide an exact source ROM identity.");
        executable.Game.ExecutableName = ""; executable.Game.GamePath = @"R:\Roms\actual.zip";
        Check(new MediaService().FindAssets(executable.Settings, executable.Game).ReusableTheme == aliasTheme, "An installed ZIP path must provide the same exact source ROM identity.");
        passed.Add("Profile identity outranks merged parent aliases, with executable and installed ZIP stems as precise fallbacks");

        var themeOverride = Fixture(root, "theme-override", "target", "Theme Override", "TeknoS11");
        var customTheme = Path.Combine(themeOverride.Settings.LaunchBoxPath, "Custom Theme Media");
        Configure(themeOverride, new XElement("PlatformFolder", new XElement("Platform", "Arcade"), new XElement("MediaType", "Theme Video"), new XElement("FolderPath", "Custom Theme Media")));
        var customFile = WriteVideo(Path.Combine(customTheme, "Theme Override.mp4"));
        Video(themeOverride, "Arcade", "Theme Override.mp4");
        WriteVideo(Path.Combine(themeOverride.Settings.LaunchBoxPath, "Video", "Arcade", "Theme", "Theme Override.mp4"));
        Check(new MediaService().FindAssets(themeOverride.Settings, themeOverride.Game).ReusableTheme == customFile, "An explicit Theme Video folder must take precedence over defaults and legacy folders.");
        var videoOverride = Fixture(root, "video-override", "target", "Video Override", "TeknoS11");
        Configure(videoOverride, new XElement("PlatformFolder", new XElement("Platform", "Arcade"), new XElement("MediaType", "Video"), new XElement("FolderPath", "Alternate Videos")));
        var videoFile = WriteVideo(Path.Combine(videoOverride.Settings.LaunchBoxPath, "Alternate Videos", "Theme", "Video Override.mp4"));
        Video(videoOverride, "Arcade", "Video Override.mp4");
        Check(new MediaService().FindAssets(videoOverride.Settings, videoOverride.Game).ReusableTheme == videoFile, "A configured Video folder must supply its Theme subfolder unless a theme override exists.");
        var legacyOverride = Fixture(root, "legacy-override", "target", "Old Setting", "TeknoS11");
        Configure(legacyOverride, new XElement("Platform", new XElement("Name", "Arcade"), new XElement("VideosFolder", "Old Videos")));
        var oldFile = WriteVideo(Path.Combine(legacyOverride.Settings.LaunchBoxPath, "Old Videos", "Theme", "Old Setting.mp4"));
        Check(new MediaService().FindAssets(legacyOverride.Settings, legacyOverride.Game).ReusableTheme == oldFile, "Legacy LaunchBox VideosFolder settings must be honored.");
        var singular = Fixture(root, "singular", "target", "Singular Folder", "TeknoS11");
        var singularVideo = WriteVideo(Path.Combine(singular.Settings.LaunchBoxPath, "Video", "Arcade", "Theme", "Singular Folder.avi"));
        Check(new MediaService().FindAssets(singular.Settings, singular.Game).ReusableTheme == singularVideo, "An existing singular Video folder is a valid compatibility source without configured overrides.");
        passed.Add("Configured Theme Video and Video paths, legacy VideosFolder settings and existing singular Video compatibility");

        var empty = Fixture(root, "empty", "target", "Empty Theme", "TeknoS11");
        Check(!MediaService.HasReusableTheme(new MediaService().FindAssets(empty.Settings, empty.Game)), "Missing theme folders must be harmless.");
        var emptyVideo = Video(empty, "Arcade", "Empty Theme.mp4"); File.WriteAllBytes(emptyVideo, []);
        var nonempty = Video(empty, "Arcade", "Empty Theme-02.mp4");
        Check(new MediaService().FindAssets(empty.Settings, empty.Game).ReusableTheme == nonempty, "A zero-byte bare candidate must not hide a usable indexed video.");
        var collision = Fixture(root, "collision", "target", "Collision", "TeknoS11");
        Video(collision, "Arcade", "Collision.mkv");
        var localTheme = Video(collision, "TeknoParrot", "Collision.mp4");
        var local = new MediaService().FindAssets(collision.Settings, collision.Game);
        Check(local.Theme == localTheme && !MediaService.HasReusableTheme(local), "Any existing TeknoParrot theme must remain preferred and untouched.");
        File.Delete(localTheme);
        Directory.CreateDirectory(Path.Combine(collision.Settings.LaunchBoxPath, "Videos", "TeknoParrot", "Theme", "Collision.mkv"));
        var occupied = new MediaService().FindAssets(collision.Settings, collision.Game);
        Check(!MediaService.HasReusableTheme(occupied) && occupied.ThemeReuseDetail.Contains("occupied"), "A destination directory collision must be preserved rather than offered as a copy target.");
        passed.Add("Missing/empty media handling and preservation of existing target themes and destination collisions");

        var refresh = Fixture(root, "refresh", "target", "Initial Name", "TeknoS11");
        Source(refresh, "Arcade", ("source", "Initial Name", "target", 1));
        var initial = Video(refresh, "Arcade", "Initial Name.mp4");
        var service = new MediaService();
        Check(service.FindAssets(refresh.Settings, refresh.Game).ReusableTheme == initial, "Initial cached discovery failed.");
        var refreshDirectory = Path.GetDirectoryName(initial)!;
        var previousDirectoryStamp = Directory.GetLastWriteTimeUtc(refreshDirectory);
        var changed = Video(refresh, "Arcade", "Replacement Title.mkv");
        // Reproduce deferred/coarse directory timestamps independently of the filesystem's timing.
        Directory.SetLastWriteTimeUtc(refreshDirectory, previousDirectoryStamp);
        Source(refresh, "Arcade", ("source", "Replacement Title", "target", 1));
        Check(service.FindAssets(refresh.Settings, refresh.Game).ReusableTheme == changed, "Source XML changes must refresh the cached source identity.");
        File.Delete(changed); service.ClearCache();
        Check(!MediaService.HasReusableTheme(service.FindAssets(refresh.Settings, refresh.Game)), "Clearing caches must discard removed source candidates.");
        var legacyAdded = WriteVideo(Path.Combine(refresh.Settings.LaunchBoxPath, "Video", "Arcade", "Theme", "Replacement Title.mp4"));
        service.ClearCache();
        Check(service.FindAssets(refresh.Settings, refresh.Game).ReusableTheme == legacyAdded, "Clearing caches must discover newly added compatibility folders.");
        passed.Add("Cached metadata refreshes after source XML changes, and ClearCache discards files and folder resolution");
        return passed;
    }

    private sealed record TestFixture(AppSettings Settings, GameRecord Game);
    private static TestFixture Fixture(string root, string name, string id, string title, string emulator, int? databaseId = null)
    {
        var directory = Path.Combine(root, name); var lb = Path.Combine(directory, "LaunchBox"); var tp = Path.Combine(directory, "TeknoParrot");
        Directory.CreateDirectory(Path.Combine(lb, "Data", "Platforms"));
        var settings = new AppSettings { LaunchBoxPath = lb, TeknoParrotPath = tp };
        var game = new GameRecord { Id = id, Name = title, Emulator = emulator, ExecutableName = id + ".zip", Installed = true, PathsValid = true };
        var node = new XElement("Game", new XElement("ID", Guid.NewGuid()), new XElement("Title", title), new XElement("ApplicationPath", Path.Combine(tp, "UserProfiles", id + ".xml")));
        if (databaseId != null) node.Add(new XElement("DatabaseID", databaseId));
        new XDocument(new XElement("LaunchBox", node)).Save(Path.Combine(lb, "Data", "Platforms", "TeknoParrot.xml"));
        return new(settings, game);
    }
    private static void Source(TestFixture fixture, string platform, params (string Id, string Title, string Rom, int DatabaseId)[] games)
    {
        new XDocument(new XElement("LaunchBox", games.Select(g => new XElement("Game", new XElement("ID", g.Id), new XElement("Title", g.Title),
            new XElement("ApplicationPath", @"R:\Original Roms\" + g.Rom + ".zip"), new XElement("DatabaseID", g.DatabaseId))))).Save(Path.Combine(fixture.Settings.LaunchBoxPath, "Data", "Platforms", platform + ".xml"));
    }
    private static void Configure(TestFixture fixture, params XElement[] elements) => new XDocument(new XElement("LaunchBox", elements)).Save(Path.Combine(fixture.Settings.LaunchBoxPath, "Data", "Platforms.xml"));
    private static string Video(TestFixture fixture, string platform, string name) => WriteVideo(Path.Combine(fixture.Settings.LaunchBoxPath, "Videos", platform, "Theme", name));
    private static string WriteVideo(string path) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, [1, 2, 3]); return path; }
    private static Dictionary<string, string> Snapshot(string root) => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).ToDictionary(p => p, p => File.Exists(p) ? SafeFiles.Hash(p) : "directory", StringComparer.OrdinalIgnoreCase);
    private static bool SameSnapshot(Dictionary<string, string> before, Dictionary<string, string> after) => before.Count == after.Count && before.All(pair => after.TryGetValue(pair.Key, out var value) && value == pair.Value);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("Related-theme discovery: " + message); }
}
