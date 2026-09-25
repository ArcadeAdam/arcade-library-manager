using System.Text;
using System.Xml.Linq;
using ArcadeLibraryManager.Core;

public static class ThemeReuseIntegrationChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Theme reuse integration: " + message);
    }

    private static void Put(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private sealed class Fixture
    {
        public const string Id = "fixture-classic-racer";
        public AppSettings Settings { get; }
        public GameRecord Game { get; }
        public string Data { get; }
        public string Source { get; }
        public string SourceXml { get; }
        public string PlatformsXml { get; }
        public string TargetXml { get; }
        public byte[] SourceBytes { get; } = Encoding.UTF8.GetBytes("synthetic existing theme; copy these bytes without decoding");

        public Fixture(string root)
        {
            Settings = new AppSettings
            {
                TeknoParrotPath = Path.Combine(root, "TeknoParrot"),
                LaunchBoxPath = Path.Combine(root, "LaunchBox"),
                CachePath = Path.Combine(root, "unused-render-cache"),
                FfmpegPath = Path.Combine(root, "tools-not-installed", "ffmpeg.exe"),
                FillMissingMetadata = false
            };
            Data = Path.Combine(root, "workflow-data");
            var profile = Path.Combine(Settings.TeknoParrotPath, "UserProfiles", Id + ".xml");
            var template = Path.Combine(Settings.TeknoParrotPath, "GameProfiles", Id + ".xml");
            var gamePath = Path.Combine(root, Id + ".zip");
            Put(gamePath, "inert fixture payload, never launched");
            var doc = new XElement("GameProfile", new XElement("EmulatorType", "TeknoVUnit"),
                new XElement("ExecutableName", Id + ".zip"), new XElement("GamePath", gamePath),
                new XElement("HasTwoExecutables", "false"), new XElement("Controls", "preserve user controls"));
            Put(profile, doc.ToString()); Put(template, doc.ToString());
            Game = new GameRecord { Id = Id, Name = "Fixture Racer (USA)", Emulator = "TeknoVUnit", UserProfilePath = profile,
                TemplatePath = template, ExecutableName = Id + ".zip", GamePath = gamePath, Installed = true, PathsValid = true };
            PlatformsXml = Path.Combine(Settings.LaunchBoxPath, "Data", "Platforms.xml");
            SourceXml = Path.Combine(Settings.LaunchBoxPath, "Data", "Platforms", "Arcade.xml");
            TargetXml = Path.Combine(Settings.LaunchBoxPath, "Data", "Platforms", "TeknoParrot.xml");
            Put(TargetXml, new XElement("LaunchBox", new XElement("Game",
                new XElement("ID", "19d3fdc2-f512-45e0-ac08-5aeb10722070"), new XElement("Title", Game.Name),
                new XElement("Platform", "TeknoParrot"), new XElement("ApplicationPath", profile), new XElement("DatabaseID", "424242"))).ToString());
            Put(SourceXml, new XElement("LaunchBox", new XElement("Game",
                new XElement("ID", "e695a24f-df54-4c03-8955-d6ba9cd3b29c"), new XElement("Title", "Fixture Racer"),
                new XElement("Platform", "Arcade"), new XElement("ApplicationPath", "roms/" + Id + ".zip"), new XElement("DatabaseID", "424242"))).ToString());
            WriteFolders("Videos/Arcade/Theme");
            Source = Path.Combine(Settings.LaunchBoxPath, "Videos", "Arcade", "Theme", "Fixture Racer-01.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(Source)!); File.WriteAllBytes(Source, SourceBytes);
        }

        public void WriteFolders(string sourceFolder)
        {
            Put(PlatformsXml, new XElement("LaunchBox",
                new XElement("Platform", new XElement("Name", "TeknoParrot")),
                new XElement("Platform", new XElement("Name", "Arcade")),
                Folder("TeknoParrot", "Videos/TeknoParrot/Theme"), Folder("Arcade", sourceFolder)).ToString());
        }

        private static XElement Folder(string platform, string path) => new("PlatformFolder",
            new XElement("Platform", platform), new XElement("MediaType", "Theme Video"), new XElement("FolderPath", path));
        public MediaAssets Assets() => new MediaService().FindAssets(Settings, Game);
        public LibraryWorkflowService Workflow() => new(Settings, Data, health: VolumeHealthChecks.CleanProvider(), launchBoxRunning: () => false);
        public void CheckPreservedSources(byte[] profile, byte[] platform)
        {
            Check(File.ReadAllBytes(Source).SequenceEqual(SourceBytes), "source video bytes were changed");
            Check(File.ReadAllBytes(Game.UserProfilePath).SequenceEqual(profile), "profile or controls changed during theme reuse");
            Check(File.ReadAllBytes(TargetXml).SequenceEqual(platform), "theme reuse changed the LaunchBox game XML");
            Check(!Directory.Exists(Settings.CachePath), "copy-only reuse created renderer cache or previews");
        }
    }

    public static async Task<List<string>> RunAsync(string root)
    {
        var results = new List<string>();
        var direct = new Fixture(Path.Combine(root, "renderer"));
        var assets = direct.Assets();
        Check(MediaService.HasReusableTheme(assets) && assets.ReusableTheme == direct.Source && !MediaService.HasVideoSnap(assets),
            "classic fixture must discover its Arcade theme without a gameplay snap");
        Check(!File.Exists(direct.Settings.FfmpegPath), "fixture unexpectedly has its configured encoder");
        var profileBefore = File.ReadAllBytes(direct.Game.UserProfilePath); var platformBefore = File.ReadAllBytes(direct.TargetXml);
        var renderer = new ThemeRenderer(VolumeHealthChecks.CleanProvider());
        var previewRejected = false;
        try { await renderer.RenderPreviewAsync(direct.Settings, assets); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("video snap", StringComparison.OrdinalIgnoreCase)) { previewRejected = true; }
        Check(previewRejected && !Directory.Exists(direct.Settings.CachePath) && !File.Exists(assets.ThemeReuseDestination),
            "creative preview must still reject missing gameplay before creating output or cache");
        var output = await renderer.RenderAsync(direct.Settings, assets);
        Check(output == assets.ThemeReuseDestination && File.ReadAllBytes(output).SequenceEqual(direct.SourceBytes),
            "central renderer did not copy the reusable theme without video tools or a snap");
        direct.CheckPreservedSources(profileBefore, platformBefore);
        results.Add("Central renderer reused the verified source bytes without a snap or configured tools; creative preview still required gameplay and produced no files.");

        var stale = new Fixture(Path.Combine(root, "stale-discovery"));
        var staleAssets = stale.Assets();
        File.SetLastWriteTimeUtc(stale.Source, staleAssets.ReusableThemeLastWriteUtc.AddSeconds(5));
        var staleRejected = false;
        try { await renderer.RenderAsync(stale.Settings, staleAssets); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("changed", StringComparison.OrdinalIgnoreCase)) { staleRejected = true; }
        Check(staleRejected && !File.Exists(staleAssets.ThemeReuseDestination) && !Directory.Exists(stale.Settings.CachePath),
            "direct renderer must reject a source whose discovered metadata changed");
        results.Add("Stale source metadata was rejected before publishing a theme.");

        var workflow = new Fixture(Path.Combine(root, "workflow")); var service = workflow.Workflow();
        var plan = await service.PreviewAsync([Fixture.Id], new() { GenerateMissingThemes = true });
        var stage = plan.Rows.Single().StagePlans.Single();
        Check(stage.Action == "Reuse existing theme" && stage.Status == "Pending" && stage.RuntimePlan is MediaAssets,
            "production workflow must offer reuse before missing-snap or missing-tools guards");
        Check(stage.SourceHashes.ContainsKey(workflow.Source) && stage.SourceHashes.ContainsKey(workflow.SourceXml)
            && stage.SourceHashes.ContainsKey(workflow.PlatformsXml), "preview must capture the source video, identity XML and configured folders");
        profileBefore = File.ReadAllBytes(workflow.Game.UserProfilePath); platformBefore = File.ReadAllBytes(workflow.TargetXml);
        var workflowAssets = workflow.Assets();
        var result = await service.ExecuteAsync(plan, workflow.Settings);
        var journal = await service.LoadJournalAsync(plan.Id);
        Check(result.CompletedGames == 1 && result.SucceededStages == 1 && journal.Games.Single().Stages.Single().Status == "Completed"
            && File.ReadAllBytes(workflowAssets.ThemeReuseDestination).SequenceEqual(workflow.SourceBytes),
            "production workflow failed to copy and journal a reusable theme");
        workflow.CheckPreservedSources(profileBefore, platformBefore);
        results.Add("Production maintenance preview and execution reused an Arcade theme, journaled completion, and preserved profile, game XML and source video.");

        foreach (var mutation in new[] { "video-content", "identity-xml", "configured-folder" })
        {
            var changed = new Fixture(Path.Combine(root, mutation)); var changedService = changed.Workflow();
            var changedPlan = await changedService.PreviewAsync([Fixture.Id], new() { GenerateMissingThemes = true });
            var changedAssets = changed.Assets();
            if (mutation == "video-content")
            {
                // Preserve length and timestamp so reviewed content hashing, rather than cheap metadata, must catch this change.
                var timestamp = File.GetLastWriteTimeUtc(changed.Source); var bytes = File.ReadAllBytes(changed.Source);
                bytes[0] ^= 1; File.WriteAllBytes(changed.Source, bytes); File.SetLastWriteTimeUtc(changed.Source, timestamp);
            }
            else if (mutation == "identity-xml") File.AppendAllText(changed.SourceXml, "\n<!-- source identity edited after preview -->");
            else changed.WriteFolders("Other Arcade Themes");
            var rejected = await changedService.ExecuteAsync(changedPlan, changed.Settings);
            var rejectedJournal = await changedService.LoadJournalAsync(changedPlan.Id);
            Check(rejected.IncompleteGames == 1 && rejectedJournal.Games.Single().Stages.Single().Status == "NeedsReview"
                && !File.Exists(changedAssets.ThemeReuseDestination) && !Directory.Exists(changed.Settings.CachePath),
                "production workflow accepted an unreviewed change: " + mutation);
        }
        results.Add("Post-preview source content, source identity XML and configured-folder changes all required a new review and wrote no theme.");
        return results;
    }
}
