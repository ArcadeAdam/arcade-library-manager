using System.Text.Json;
using System.Xml.Linq;
using ArcadeLibraryManager.Core;

public static class EmuMoviesChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("EmuMovies: " + message); }
    private static void Put(string path, string value) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value); }
    private static void Profile(string root, string stem, string title, string type) => Put(Path.Combine(root, stem + ".xml"), new XElement("GameProfile", new XElement("GameNameInternal", title), new XElement("EmulatorType", type)).ToString());
    public static async Task<List<string>> RunAsync(string root)
    {
        var results = new List<string>();
        var lb = Path.Combine(root, "LaunchBox");
        var profiles = Path.Combine(root, "profiles");
        var work = Path.Combine(root, "work");
        Profile(profiles, "GGS", "Guilty Gear Strive", "OpenParrot");
        Profile(profiles, "hangplt", "Hang Pilot (JAB)", "TeknoGClub");
        Profile(profiles, "unknownclassic", "Unknown Classic", "TeknoS22");
        Put(Path.Combine(lb, "Data", "Platforms.xml"), "<LaunchBox><Platform><Name>TeknoParrot</Name></Platform><PlatformFolder><Platform>TeknoParrot</Platform><MediaType>Video</MediaType><FolderPath>Videos/TP</FolderPath></PlatformFolder><PlatformFolder><Platform>TeknoParrot</Platform><MediaType>Theme Video</MediaType><FolderPath>Videos/TP/Theme</FolderPath></PlatformFolder></LaunchBox>");
        var games = new XElement("LaunchBox", new XElement("Game", new XElement("Title", "Hang Pilot"), new XElement("ApplicationPath", Path.Combine(profiles, "hangplt.xml")), new XElement("LaunchBoxDbId", "37146")), new XElement("Game", new XElement("Title", "Guilty Gear Strive"), new XElement("ApplicationPath", Path.Combine(profiles, "GGS.xml"))));
        Put(Path.Combine(lb, "Data", "Platforms", "TeknoParrot.xml"), games.ToString());
        Put(Path.Combine(lb, "Metadata", "MAME.xml"), "<LaunchBox><MameFile><FileName>hangplt</FileName><Name>Hang Pilot</Name><IsNonArcade>false</IsNonArcade></MameFile><MameFile><FileName>GGS</FileName><Name>Collision</Name><IsNonArcade>false</IsNonArcade></MameFile></LaunchBox>");
        Put(Path.Combine(lb, "Videos", "TP", "Hang Pilot-37146-01.mp4"), "existing-video-must-stay");
        Put(Path.Combine(lb, "Videos", "TP", "Theme", "GGS.mp4"), "theme-must-stay-separate");
        var choices = EmuMoviesService.GetPlatforms(lb);
        Check(choices.Count == 1 && choices[0].GameCount == 2 && choices[0].MatchFolder == profiles, "platform discovery failed");
        var service = new EmuMoviesService(Path.Combine(root, "data"));
        var request = new EmuMoviesVideoRequest { LaunchBoxPath = lb, PlatformName = "TeknoParrot", MatchFolder = profiles, Catalog = "ArcadePC", WorkDirectory = work, UseMameFallback = true };
        var preview = await service.RunAsync(request, false, false, default);
        Check(preview.Imported == 0 && preview.Downloaded == 0 && preview.Missing == 3, "preview did not remain local and plan-only");
        using (var selection = JsonDocument.Parse(File.ReadAllText(Path.Combine(preview.ReportDirectory, "Selection.json"))))
        {
            Check(selection.RootElement.GetProperty("MameInputs").GetInt32() == 2 && selection.RootElement.GetProperty("PrimaryInputs").GetInt32() == 1, "classic/modern catalog split is wrong");
        }
        foreach (var jobFile in Directory.EnumerateFiles(preview.ReportDirectory, "Job.json", SearchOption.AllDirectories))
        {
            using var config = JsonDocument.Parse(File.ReadAllText(jobFile));
            var catalog = config.RootElement.GetProperty("System").GetString()!;
            var destination = config.RootElement.GetProperty("DownloadFolder").GetString()!;
            var source = Path.Combine(destination, catalog, "Video_MP4_HI_QUAL");
            foreach (var stem in catalog == "MAME" ? new[] { "hangplt", "unknownclassic" } : new[] { "GGS" }) Put(Path.Combine(source, stem + ".MP4"), "fixture-video-" + stem);
        }
        var imported = await service.RunAsync(request, false, true, default);
        Check(imported.Imported == 1, "gap import should add only the modern GGS video");
        Check(File.ReadAllText(Path.Combine(lb, "Videos", "TP", "Hang Pilot-37146-01.mp4")) == "existing-video-must-stay", "existing title-named video was overwritten");
        Check(File.Exists(Path.Combine(lb, "Videos", "TP", "GGS.MP4")), "theme video incorrectly blocked normal video import");
        Check(!File.Exists(Path.Combine(lb, "Videos", "TP", "unknownclassic.MP4")), "unverified classic identity was imported");
        var repeated = await service.RunAsync(request, false, true, default);
        Check(repeated.Imported == 0, "repeat run copied existing videos");
        results.Add("Local preview, actual gap import, title-named preservation, theme separation and repeat behavior passed without downloading.");
        results.Add("Classic MAME routing excluded modern-name collisions; unknown classic identities remained quarantined; unimported profiles stayed in the input plan.");
        request.Catalog = "MAME";
        var mamePrimaryPreview = await service.RunAsync(request, false, false, default);
        var mameConfigs = Directory.EnumerateFiles(mamePrimaryPreview.ReportDirectory, "Job.json", SearchOption.AllDirectories).ToArray();
        Check(mameConfigs.Length == 1, "selected MAME plus fallback created duplicate catalog jobs");
        using(var mameConfig = JsonDocument.Parse(File.ReadAllText(mameConfigs[0])))
        {
            var include = File.ReadAllText(mameConfig.RootElement.GetProperty("IncludeStemsPath").GetString()!);
            var verified = File.ReadAllText(mameConfig.RootElement.GetProperty("VerifiedStemsPath").GetString()!);
            Check(!include.Contains("GGS") && verified.Contains("hangplt"), "same-catalog routing lost its identity scope or admitted a modern name collision");
        }
        request.Catalog = "ArcadePC";
        results.Add("Selecting MAME as the primary catalog creates one scoped job and retains exact identity approval without admitting modern PC-title collisions.");
        Profile(profiles, "daytona", "Daytona USA", "TeknoModel2");
        var legacyModel2 = Path.Combine(work, "staging", "SegaModel2");
        Put(Path.Combine(legacyModel2, "Sega_Model_2", "Video_MP4_HI_QUAL", "daytona.mp4"), "model2-video-fixture");
        Put(Path.Combine(legacyModel2, "Sega_Model_2", "Advert", "daytona.png"), "artwork-must-not-be-imported");
        var model2Preview = await service.RunAsync(request, false, false, default);
        var model2Jobs = Directory.EnumerateFiles(model2Preview.ReportDirectory, "Job.json", SearchOption.AllDirectories).Select(p => JsonDocument.Parse(File.ReadAllText(p))).ToList();
        try
        {
            var model2Job = model2Jobs.Single(j => j.RootElement.GetProperty("System").GetString() == "Sega Model 2");
            Check(model2Job.RootElement.GetProperty("DownloadFolder").GetString() == legacyModel2, "dedicated legacy Model2 cache was not reused");
            Check(File.ReadAllText(model2Job.RootElement.GetProperty("IncludeStemsPath").GetString()!).Contains("daytona"), "Model2 job lacks its hardware input");
            var mameJob = model2Jobs.Single(j => j.RootElement.GetProperty("System").GetString() == "MAME");
            Check(!File.ReadAllText(mameJob.RootElement.GetProperty("IncludeStemsPath").GetString()!).Contains("daytona"), "Model2 input was duplicated into MAME");
        }
        finally { foreach(var job in model2Jobs) job.Dispose(); }
        Check(!File.Exists(Path.Combine(lb,"Videos","TP","daytona.mp4")), "preview imported a legacy cached video");
        results.Add("Model2 routes once to its confirmed catalog and reuses only the dedicated legacy cache; preview leaves its media untouched.");
        var metadataCollisions = new[] { (Stem: "ps2collision", Type: "pcsx2x6"), (Stem: "dolphincollision", Type: "Dolphin"), (Stem: "chihirocollision", Type: "cxbxr"), (Stem: "unknowncollision", Type: "UnlistedHardware") };
        var mameMetadataPath = Path.Combine(lb, "Metadata", "MAME.xml");
        var mameMetadata = XDocument.Load(mameMetadataPath);
        foreach (var collision in metadataCollisions)
        {
            Profile(profiles, collision.Stem, collision.Stem, collision.Type);
            mameMetadata.Root!.Add(new XElement("MameFile", new XElement("FileName", collision.Stem), new XElement("Name", collision.Stem), new XElement("IsNonArcade", "false")));
        }
        mameMetadata.Save(mameMetadataPath);
        var collisionPreview = await service.RunAsync(request, false, false, default);
        using (var selection = JsonDocument.Parse(File.ReadAllText(Path.Combine(collisionPreview.ReportDirectory, "Selection.json"))))
        {
            Check(selection.RootElement.GetProperty("MameInputs").GetInt32() == 2 && selection.RootElement.GetProperty("Model2Inputs").GetInt32() == 1 && selection.RootElement.GetProperty("PrimaryInputs").GetInt32() == 5, "local MAME metadata expanded the approved hardware catalog scope");
            foreach (var collision in metadataCollisions)
            {
                var input = selection.RootElement.GetProperty("Inputs").EnumerateArray().Single(i => i.GetProperty("Stem").GetString() == collision.Stem);
                Check(input.GetProperty("ExactLocalMameIdentity").GetBoolean() && input.GetProperty("Catalog").GetString() == "ArcadePC", "metadata-only identity incorrectly rerouted " + collision.Type + " into MAME");
            }
        }
        results.Add("Exact MAME metadata names do not reroute PS2, Dolphin, Chihiro or unknown hardware profiles away from their selected primary catalog.");
        var genericRoms = Path.Combine(root, "saturn-roms");
        Put(Path.Combine(genericRoms, "example [EU].zip"), "rom-file-must-not-be-copied");
        Put(Path.Combine(genericRoms, "example [EU] (Track 01).bin"), "companion");
        Put(Path.Combine(genericRoms, "unimported-other-game.zip"), "unrelated");
        var platforms = XDocument.Load(Path.Combine(lb, "Data", "Platforms.xml"));
        platforms.Root!.Add(new XElement("Platform", new XElement("Name", "Sega Saturn")), new XElement("PlatformFolder", new XElement("Platform", "Sega Saturn"), new XElement("MediaType", "Video"), new XElement("FolderPath", "Videos/Saturn")));
        platforms.Save(Path.Combine(lb, "Data", "Platforms.xml"));
        Put(Path.Combine(lb, "Data", "Platforms", "Sega Saturn.xml"), new XElement("LaunchBox", new XElement("Game", new XElement("Title", "Time Crisis"), new XElement("DatabaseID", "2"), new XElement("ApplicationPath", Path.Combine(genericRoms, "example [EU].zip")))).ToString());
        var genericRequest = new EmuMoviesVideoRequest { LaunchBoxPath=lb, PlatformName="Sega Saturn", MatchFolder=genericRoms, Catalog="Sega Saturn", WorkDirectory=work };
        var genericPreview = await service.RunAsync(genericRequest, false, false, default);
        Check(genericPreview.Skipped == 2 && genericPreview.Missing == 1, "unrelated/companion files were not reported and excluded");
        var genericConfigPath = Directory.EnumerateFiles(genericPreview.ReportDirectory, "Job.json", SearchOption.AllDirectories).Single();
        using(var genericConfig = JsonDocument.Parse(File.ReadAllText(genericConfigPath)))
        {
            var target = genericConfig.RootElement.GetProperty("DownloadFolder").GetString()!;
            Put(Path.Combine(target, "Sega_Saturn", "Video_MP4_HI_QUAL", "example [EU].mp4"), "generic-video-fixture");
            Check(new FileInfo(Directory.EnumerateFiles(genericConfig.RootElement.GetProperty("MatchFolder").GetString()!).Single()).Length == 0, "ROM bytes were copied into the matching input");
        }
        Put(Path.Combine(lb,"Videos","Saturn","Time Crisis 2.mp4"), "sequel-video");
        Put(Path.Combine(lb,"Videos","Saturn","Time Crisis 2-01.mp4"), "numbered-sequel-video");
        var genericImport = await service.RunAsync(genericRequest, false, true, default);
        Check(genericImport.Imported == 1 && File.Exists(Path.Combine(lb,"Videos","Saturn","example [EU].mp4")), "generic platform import failed");
        Check(File.ReadAllText(Path.Combine(lb,"Videos","Saturn","Time Crisis 2.mp4")) == "sequel-video", "sequel video changed");
        results.Add("Generic known/unknown/companion inputs, sequel-number distinction (including a matching numeric database ID), and another platform import passed.");
        var before = Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories).Count();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await service.RunAsync(request, false, false, cancelled.Token); throw new Exception("cancelled request ran"); } catch (OperationCanceledException) { }
        Check(before == Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories).Count(), "pre-cancelled request wrote files");
        request.WorkDirectory = Path.Combine(profiles, "bad-output");
        try { await service.RunAsync(request, false, false, default); throw new Exception("overlap accepted"); } catch (ArgumentException) { }
        results.Add("Pre-cancellation and matching/output overlap rejection passed.");
        return results;
    }
}




