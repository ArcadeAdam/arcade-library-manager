using ArcadeLibraryManager.Core;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

public static class MediaDiscoveryChecks
{
    public static Task<List<string>> RunAsync(string fixtureRoot)
    {
        var root = Path.Combine(fixtureRoot, "theme-discovery-" + Guid.NewGuid().ToString("N"));
        var settings = new AppSettings { LaunchBoxPath = Path.Combine(root, "LaunchBox"), TeknoParrotPath = Path.Combine(root, "TeknoParrot"), PlatformName = "TeknoParrot" };
        var platformDirectory = Path.Combine(settings.LaunchBoxPath, "Data", "Platforms"); Directory.CreateDirectory(platformDirectory);
        XElement Game(string id, string profile, string title) => new("Game", new XElement("ID", id), new XElement("Title", title), new XElement("ApplicationPath", Path.Combine(settings.TeknoParrotPath, "UserProfiles", profile + ".xml")));
        XElement Additional(string id, string parent, string profile) => new("AdditionalApplication", new XElement("Id", id), new XElement("GameID", parent), new XElement("ApplicationPath", Path.Combine(settings.TeknoParrotPath, "UserProfiles", profile + ".xml")), new XElement("AutoRunBefore", false), new XElement("AutoRunAfter", false));
        var xml = new XDocument(new XElement("LaunchBox",
            Game("combined", "voyager2", "Star Trek Voyager"), Additional("old-loader", "combined", "voyager"),
            Game("usa", "vice_us", "Total Vice (USA)"), Game("world", "vice_world", "Total Vice (World)"),
            Game("uninstalled", "uninstalled", "Uninstalled Game"),
            Game("ambiguous1", "ambiguous", "Ambiguous"), Game("ambiguous2", "ambiguous", "Ambiguous"),
            Game("custom", "base", "Base Game"), Additional("custom-alt", "custom", "super")));
        xml.Save(Path.Combine(platformDirectory, "TeknoParrot.xml"));
        GameRecord Record(string id, string name, bool installed = true) => new() { Id = id, Name = name, Installed = installed, PathsValid = true };
        var games = new[] { Record("voyager2", "Star Trek Voyager (ELF Loader 2)"), Record("voyager", "Star Trek Voyager (ELF)"), Record("vice_us", "Total Vice (USA)"), Record("vice_world", "Total Vice (World)"), Record("orphan", "Unregistered Game"), Record("uninstalled", "Uninstalled Game", false), Record("ambiguous", "Ambiguous"), Record("base", "Base Game"), Record("super", "Super Base Game") };
        var selected = MediaService.SelectThemeGames(settings, games).Select(g => g.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Require(selected.SetEquals(new[] { "voyager2", "vice_us", "base" }), "Theme candidates must use one preferred real LaunchBox identity and exclude orphans/ambiguity.");
        var passed = new List<string> { "Theme rows retain the LaunchBox default, prefer USA, and exclude alternate-profile orphans and ambiguous identities" };

        var game = games[0];
        string Folder(string kind) { var folder = Path.Combine(settings.LaunchBoxPath, "Images", settings.PlatformName, kind); Directory.CreateDirectory(folder); return folder; }
        var fanartDirectory = Folder("Fanart - Background");
        var flyerDirectory = Path.Combine(Folder("Advertisement Flyer - Front"), "Japan"); Directory.CreateDirectory(flyerDirectory);
        var flyer = Path.Combine(flyerDirectory, "Star Trek Voyager-01.png"); File.WriteAllText(flyer, "matched flyer");
        var screenshot = Path.Combine(Folder("Screenshot - Gameplay"), "Star Trek Voyager-01.png"); File.WriteAllText(screenshot, "matched screenshot");
        var service = new MediaService(); var media = service.FindAssets(settings, game);
        Require(media.Background == flyer && media.BackgroundKind == "Flyer" && media.ArtworkSources.SequenceEqual(new[] { flyer }), "Flyer should be found in the region folder and offered for cutout extraction before screenshot fallback.");
        var fanart = Path.Combine(fanartDirectory, "Star Trek Voyager-01.jpg"); File.WriteAllText(fanart, "matched fanart"); service.ClearCache(); media = service.FindAssets(settings, game);
        Require(media.Background == fanart && media.BackgroundKind == "Fanart" && media.ArtworkSources[0] == flyer && !media.ArtworkSources.Contains(screenshot), "Fanart should be the background while flyers remain the first extraction source; screenshots are not cutout art.");
        passed.Add("Discovery prefers fanart backgrounds and uses region-folder flyers for extraction without treating gameplay screenshots as cutout art");

        var assetsRoot = Path.Combine(root, "Custom theme layers"); settings.ThemeAssetsPath = assetsRoot;
        var pack = Path.Combine(assetsRoot, "combined"); Directory.CreateDirectory(pack);
        var packBackground = Path.Combine(pack, "background.jpg"); File.WriteAllText(packBackground, "custom background");
        var packLogo = Path.Combine(pack, "logo.png"); File.WriteAllText(packLogo, "custom logo");
        var cutout = Path.Combine(pack, "cutout-character.png"); WritePng(cutout, true);
        WritePng(Path.Combine(pack, "cutout-opaque.png"), false);
        var other = Path.Combine(assetsRoot, "other-game"); Directory.CreateDirectory(other); WritePng(Path.Combine(other, "cutout-other.png"), true);
        service.ClearCache(); media = service.FindAssets(settings, game);
        Require(media.Background == packBackground && media.Logo == packLogo && media.BackgroundKind == "Theme asset" && media.ThemeAssetDirectory == pack, "A LaunchBox-ID theme pack must supply custom background/logo layers.");
        Require(media.Cutouts.SequenceEqual(new[] { cutout }), "Only the matched game's PNG with useful transparency may become a cutout.");
        passed.Add("Per-game theme packs override art and accept genuine transparent PNG cutouts while rejecting opaque and other-game files");
        var cutoutOnlyPack = Path.Combine(assetsRoot, "base"); Directory.CreateDirectory(cutoutOnlyPack);
        WritePng(Path.Combine(cutoutOnlyPack, "cutout-only.png"), true);
        service.ClearCache(); var cutoutOnly = service.FindAssets(settings, games[7]);
        Require(cutoutOnly.Background.Length == 0 && cutoutOnly.Logo.Length == 0 && cutoutOnly.Snap.Length == 0 && cutoutOnly.Cutouts.Count == 1 && games[7].ThemeStatus == "Missing video snap" && !MediaService.HasVideoSnap(cutoutOnly), "A transparent-cutout-only game must remain discoverable but cannot render without a video snap.");
        passed.Add("Transparent-cutout-only games retain their art but report a missing video snap");
        return Task.FromResult(passed);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void WritePng(string path, bool transparent)
    {
        using var stream = File.Create(path); stream.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), 64); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), 64); header[8] = 8; header[9] = 6; Chunk(stream, "IHDR", header);
        using var data = new MemoryStream();
        using (var zlib = new ZLibStream(data, CompressionLevel.Fastest, true))
            for (var y = 0; y < 64; y++) { zlib.WriteByte(0); for (var x = 0; x < 64; x++) { zlib.WriteByte(25); zlib.WriteByte(220); zlib.WriteByte(170); zlib.WriteByte(!transparent || (x >= 16 && x < 48 && y >= 8 && y < 56) ? (byte)255 : (byte)0); } }
        Chunk(stream, "IDAT", data.ToArray()); Chunk(stream, "IEND", []);
    }
    private static void Chunk(Stream stream, string kind, byte[] data)
    {
        var size = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(size, data.Length); stream.Write(size);
        var type = Encoding.ASCII.GetBytes(kind); stream.Write(type); stream.Write(data);
        uint crc = uint.MaxValue; foreach (var value in type.Concat(data)) { crc ^= value; for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0u); }
        BinaryPrimitives.WriteUInt32BigEndian(size, ~crc); stream.Write(size);
    }
}
