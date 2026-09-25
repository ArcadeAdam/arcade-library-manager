using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using ArcadeLibraryManager.Core;

public static class WorkflowChecks
{
    private static void Check(bool value, string reason) { if (!value) throw new Exception("Workflow check failed: " + reason); }
    private static async Task Reject(Func<Task> action, string reason)
    {
        try { await action(); } catch (InvalidOperationException) { return; }
        throw new Exception("Workflow expected rejection: " + reason);
    }
    public static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        var clean = VolumeHealthChecks.CleanProvider();
        var settings = new AppSettings { TeknoParrotPath = Path.Combine(root, "fake-tp"), LaunchBoxPath = Path.Combine(root, "fake-lb") };
        var options = new WorkflowOptions { InstallGames = true, SyncLaunchBox = true, GenerateMissingThemes = true, InstallBezels = true };
        Check(new WorkflowOptions().EnabledStages().Count == 0, "all mutating categories require explicit selection");
        var runner = new FakeRunner(); runner.Failures["a/Install"] = "Failed"; runner.Failures["b/LaunchBox"] = "Failed";
        var data = Path.Combine(root, "dependencies");
        var service = new LibraryWorkflowService(settings, data, runner, clean);
        var plan = await service.PreviewAsync(["a", "b", "b-v2"], options);
        var result = await service.ExecuteAsync(plan, settings);
        Check(!runner.Calls.Any(c => c.StartsWith("a/") && c != "a/Install"), "failed install blocks dependent work");
        Check(runner.Calls.Contains("b/Theme") && runner.Calls.Contains("b/Bezel"), "LaunchBox failure permits independent theme and bezel attempts");
        Check(result.CompletedGames == 1 && result.IncompleteGames == 2, "alternate profile has independent success and failures are not success");
        var firstJournal = await service.LoadJournalAsync(plan.Id);
        Check(firstJournal.Games.Count == 3 && firstJournal.Games.Single(g => g.ProfileId == "a").Stages.Count(s => s.Status == "Blocked") == 3, "durable exact IDs and blocked dependencies");
        Check(File.Exists(result.ReportPath), "human-readable saved report");
        runner.Calls.Clear(); runner.Failures.Clear();
        var resume = await service.PreviewResumeAsync(plan.Id);
        Check(resume.Rows.Select(r => r.ProfileId).Order().SequenceEqual(new[] { "a", "b" }), "resume excludes completed alternate version");
        Check(resume.Rows.Single(r => r.ProfileId == "b").StagePlans.Select(s => s.Stage).SequenceEqual(new[] { "LaunchBox" }), "resume excludes every completed category");
        var retried = await service.ExecuteAsync(resume, settings);
        Check(retried.CompletedGames == 2 && runner.Calls.Count(c => c == "b/Install") == 0, "retry does not reinstall completed games");
        Check((await service.LoadUnfinishedAsync()).Count == 0, "superseded completed continuation is absent from unfinished list");
        await Reject(() => service.ExecuteAsync(resume, settings), "same reviewed plan cannot run twice");

        var staleRunner = new FakeRunner();
        var staleService = new LibraryWorkflowService(settings, Path.Combine(root, "stale"), staleRunner, clean);
        var stale = await staleService.PreviewAsync(["a"], new() { InstallGames = true });
        var changed = new AppSettings { TeknoParrotPath = settings.TeknoParrotPath, LaunchBoxPath = "different" };
        await Reject(() => staleService.ExecuteAsync(stale, changed), "settings changed after review");
        Check(staleRunner.Calls.Count == 0, "stale settings made no changes");
        stale.Rows[0].ProfileId = "unreviewed";
        await Reject(() => staleService.ExecuteAsync(stale, settings), "selected profile ID cannot be replaced");
        stale = await staleService.PreviewAsync(["a"], new() { InstallGames = true }); stale.Options.InstallBezels = true;
        await Reject(() => staleService.ExecuteAsync(stale, settings), "category expansion requires new review");
        stale = await staleService.PreviewAsync(["a", "b"], new() { InstallGames = true }); stale.Rows.Single(r => r.ProfileId == "b").Selected = false;
        await staleService.ExecuteAsync(stale, settings);
        Check(staleRunner.Calls.SequenceEqual(new[] { "a/Install" }), "reviewed row deselection is allowed");

        var source = Path.Combine(root, "shared-source.xml"); File.WriteAllText(source, "original");
        var concurrentRunner = new FakeRunner { Source = source };
        var concurrentService = new LibraryWorkflowService(settings, Path.Combine(root, "concurrent"), concurrentRunner, clean);
        var concurrent = await concurrentService.PreviewAsync(["a"], new() { InstallGames = true });
        File.WriteAllText(source, "external edit");
        var rejected = await concurrentService.ExecuteAsync(concurrent, settings);
        Check(concurrentRunner.Calls.Count == 0 && rejected.Errors.Single().Contains("NeedsReview"), "changed file blocks its stage before runner executes");
        var ownRunner = new FakeRunner { Source = source, ChangeOwnSource = true };
        var ownService = new LibraryWorkflowService(settings, Path.Combine(root, "ownchanges"), ownRunner, clean);
        var ownPlan = await ownService.PreviewAsync(["a", "b"], new() { InstallGames = true, InstallBezels = true });
        var ownResult = await ownService.ExecuteAsync(ownPlan, settings);
        Check(ownResult.CompletedGames == 2, "known own commits update shared baselines for subsequent games");

        var partialRunner = new FakeRunner(); partialRunner.Failures["a/Install"] = "Failed"; partialRunner.Failures["b/Install"] = "Failed";
        var partialService = new LibraryWorkflowService(settings, Path.Combine(root, "partial-selection"), partialRunner, clean);
        var partial = await partialService.PreviewAsync(["a", "b"], new() { InstallGames = true }); await partialService.ExecuteAsync(partial, settings);
        partialRunner.Failures.Clear(); var onlyA = await partialService.PreviewResumeAsync(partial.Id); onlyA.Rows.Single(r => r.ProfileId == "b").Selected = false;
        await partialService.ExecuteAsync(onlyA, settings);
        var remaining = await partialService.PreviewResumeAsync(partial.Id);
        Check(remaining.Rows.Select(r => r.ProfileId).SequenceEqual(new[] { "b" }), "unchecked pending games remain resumable without repeating selected continuation");

        await RetiredStagesFixture(Path.Combine(root, "retired-stages"), settings, clean);

        var cancelledRunner = new FakeRunner(); using var cts = new CancellationTokenSource();
        var cancelData = Path.Combine(root, "interruption");
        cancelledRunner.BeforeExecute = async (id, stage, dir, ct) => {
            var path = Directory.GetFiles(Path.Combine(dir, "workflows"), "*.json").Single();
            var saved = JsonSerializer.Deserialize<WorkflowJournal>(await File.ReadAllTextAsync(path))!;
            Check(saved.Games.Single().Stages.Single().Status == "Running", "stage intent persisted before side effects");
            cts.Cancel(); ct.ThrowIfCancellationRequested();
        };
        var cancelService = new LibraryWorkflowService(settings, cancelData, cancelledRunner, clean);
        var cancelPlan = await cancelService.PreviewAsync(["a"], new() { InstallGames = true });
        var cancelled = await cancelService.ExecuteAsync(cancelPlan, settings, ct: cts.Token);
        Check(cancelled.IncompleteGames == 1 && (await cancelService.LoadJournalAsync(cancelPlan.Id)).Status == "Interrupted", "cancellation is durable and retryable");

        var dirtyRunner = new FakeRunner();
        var dirty = new VolumeHealthService((p, ct) => Task.FromResult(new VolumeHealthResult(p, "fixture", "NTFS", VolumeHealthState.Dirty, "Test only")));
        var dirtyPath = Path.Combine(root, "dirty-do-not-create");
        try { await new LibraryWorkflowService(settings, dirtyPath, dirtyRunner, dirty).PreviewAsync(["a"], new() { InstallGames = true }); throw new Exception("Dirty workflow preview accepted"); } catch (VolumeHealthException) { }
        Check(!Directory.Exists(dirtyPath) && dirtyRunner.PreviewCount == 0, "dirty volume is blocked before workflow storage or stage preview");

        await ResumeVersionFixture(Path.Combine(root, "resume-versions"), settings, clean, true);
        await ResumeVersionFixture(Path.Combine(root, "resume-fallback"), settings, clean, false);
        await RealLaunchBoxFixture(Path.Combine(root, "real-launchbox"), clean);
        await ProductionWorkflowFixture(Path.Combine(root, "production-workflow"), clean);
        await RecursiveBezelWorkflowFixture(Path.Combine(root, "recursive-bezel-workflow"), clean);
        Console.WriteLine("Workflow checks passed: dependency isolation, exact alternate IDs, settings/plan/source staleness, durable intent/cancellation, partial resume, stage-only retry, retired-stage migration, storage guard and real LaunchBox integration.");
    }

    private static async Task RetiredStagesFixture(string data, AppSettings settings, VolumeHealthService health)
    {
        var runner = new FakeRunner();
        var service = new LibraryWorkflowService(settings, data, runner, health);
        var retiredOptions = JsonSerializer.Deserialize<WorkflowOptions>("""{"DownloadEmuMoviesViaLaunchBox":true,"FetchArtwork":true}""")!;
        Check(retiredOptions.EnabledStages().Count == 0, "legacy download flags cannot enable any stage");
        await Reject(() => service.PreviewAsync(["a"], retiredOptions), "legacy download-only options require a new supported selection");
        Check(runner.PreviewCount == 0 && runner.Calls.Count == 0, "retired-only options never reach a runner");
        var mixedOptions = JsonSerializer.Deserialize<WorkflowOptions>("""{"InstallGames":true,"GenerateMissingThemes":true,"InstallBezels":true,"DownloadEmuMoviesViaLaunchBox":true,"FetchArtwork":true}""")!;
        Check(mixedOptions.EnabledStages().SequenceEqual(new[] { "Install", "Theme", "Bezel" }), "legacy options retain only supported requested work");

        foreach (var stage in new[] { "Artwork", "EmuMovies", "UnknownStage" })
        {
            runner.InjectStage = stage;
            try { await service.PreviewAsync(["a"], new() { InstallGames = true }); throw new Exception("Injected category accepted: " + stage); }
            catch (InvalidDataException) { }
        }
        runner.InjectStage = "";
        Check(runner.Calls.Count == 0, "unreviewed retired or unknown categories are rejected before execution");
        runner.Failures["a/Install"] = "Failed"; runner.Failures["b/Install"] = "Failed";
        var original = await service.PreviewAsync(["a", "b"], mixedOptions);
        var failed = await service.ExecuteAsync(original, settings);
        var journal = await service.LoadJournalAsync(original.Id);
        var artifact = Path.Combine(data, "original-media-checklist.md");
        File.WriteAllText(artifact, "Retained historical checklist; do not run.");
        var artifactBytes = File.ReadAllBytes(artifact);
        foreach (var game in journal.Games)
        {
            game.Stages.Insert(1, new() { Stage = "EmuMovies", Action = "Old media handoff", Status = "NeedsUserAction", Detail = "Historical pending download", Artifacts = [artifact] });
            game.Stages.Insert(2, new() { Stage = "Artwork", Action = "Old artwork fetch", Status = "Pending", Detail = "Historical unfinished artwork" });
        }
        // b has only retired work left; a still has installation and independent local work.
        foreach (var stage in journal.Games.Single(g => g.ProfileId == "b").Stages.Where(s => s.Stage is "Install" or "Theme" or "Bezel")) stage.Status = "Completed";
        var legacyJson = JsonSerializer.SerializeToNode(journal)!.AsObject();
        legacyJson["Options"]!["DownloadEmuMoviesViaLaunchBox"] = true;
        legacyJson["Options"]!["FetchArtwork"] = true;
        File.WriteAllText(failed.JournalPath, legacyJson.ToJsonString());
        var originalBytes = File.ReadAllBytes(failed.JournalPath);
        runner.Failures.Clear(); runner.Calls.Clear();
        var resumed = await service.PreviewResumeAsync(original.Id);
        Check(resumed.Rows.Select(r => r.ProfileId).SequenceEqual(new[] { "a" }), "retired-only games do not become runnable resume rows");
        Check(resumed.Rows.Single().StagePlans.Select(s => s.Stage).SequenceEqual(new[] { "Install", "Theme", "Bezel" }), "unfinished supported stages survive legacy migration in dependency order");
        Check(resumed.Rows.Single().Warnings.Contains("retired", StringComparison.OrdinalIgnoreCase), "resume explains preserved retired history");
        Check(File.ReadAllBytes(failed.JournalPath).SequenceEqual(originalBytes), "resume review leaves the original journal unchanged");
        var injected = new WorkflowStagePlan { Stage = "EmuMovies", Action = "Old handoff" };
        resumed.Rows.Single().StagePlans.Add(injected);
        await Reject(() => service.ExecuteAsync(resumed, settings), "inserting a retired stage invalidates an already reviewed plan");
        Check(runner.Calls.Count == 0, "tampered reviewed plan performs no stages");
        resumed.Rows.Single().StagePlans.Remove(injected);
        var result = await service.ExecuteAsync(resumed, settings);
        Check(result.CompletedGames == 1 && runner.Calls.SequenceEqual(new[] { "a/Install", "a/Theme", "a/Bezel" }), "supported work finishes without waiting for or executing removed downloads");
        var preserved = await service.LoadJournalAsync(original.Id);
        Check(preserved.Games.All(g => g.Stages.Single(s => s.Stage == "EmuMovies").Status == "NeedsUserAction"
            && g.Stages.Single(s => s.Stage == "Artwork").Status == "Pending"
            && g.Stages.Single(s => s.Stage == "EmuMovies").Artifacts.SequenceEqual(new[] { artifact })), "retired actions and their artifact references remain historical without false completion");
        Check(File.ReadAllBytes(artifact).SequenceEqual(artifactBytes), "legacy handoff artifacts remain untouched");
        Check((await service.LoadUnfinishedAsync()).Count == 0, "retired-only work is absent from actionable unfinished runs");

        var onlyRetired = new WorkflowJournal { Id = Guid.NewGuid().ToString("N"), SettingsHash = journal.SettingsHash, DataPath = data,
            Games = [new() { ProfileId = "retired", Name = "Retired-only fixture", Stages = [new() { Stage = "EmuMovies", Status = "NeedsUserAction" }, new() { Stage = "Artwork" }] }] };
        var retiredPath = Path.Combine(data, "workflows", onlyRetired.Id + ".json");
        File.WriteAllText(retiredPath, JsonSerializer.Serialize(onlyRetired));
        var retiredBytes = File.ReadAllBytes(retiredPath); var callsBefore = runner.Calls.Count; var previewsBefore = runner.PreviewCount;
        try { await service.PreviewResumeAsync(onlyRetired.Id); throw new Exception("Retired-only journal resumed"); }
        catch (InvalidOperationException ex) { Check(ex.Message.Contains("retired", StringComparison.OrdinalIgnoreCase) && ex.Message.Contains("new plan", StringComparison.OrdinalIgnoreCase), "retired-only resume offers a friendly new-plan path"); }
        Check((await service.LoadUnfinishedAsync()).Count == 0 && File.ReadAllBytes(retiredPath).SequenceEqual(retiredBytes), "retired-only journal is preserved without becoming actionable");
        Check(runner.Calls.Count == callsBefore && runner.PreviewCount == previewsBefore, "retired-only journal never invokes a runner or service preview");

        onlyRetired.Games[0].Stages.Add(new() { Stage = "Theme" }); onlyRetired.SettingsHash = "legacy-settings-format";
        File.WriteAllText(retiredPath, JsonSerializer.Serialize(onlyRetired)); retiredBytes = File.ReadAllBytes(retiredPath);
        try { await service.PreviewResumeAsync(onlyRetired.Id); throw new Exception("Changed settings were accepted"); }
        catch (InvalidOperationException ex) { Check(ex.Message.Contains("new explicit game selection") && ex.Message.Contains("checkpoints are preserved"), "old settings fingerprints retain the fresh-selection guard with migration guidance"); }
        Check(File.ReadAllBytes(retiredPath).SequenceEqual(retiredBytes) && runner.PreviewCount == previewsBefore && runner.Calls.Count == callsBefore,
            "incompatible saved settings cannot alter journals or perform work");
    }
    private static async Task ResumeVersionFixture(string data, AppSettings settings, VolumeHealthService health, bool usaAvailable)
    {
        var runner = new FakeRunner(); runner.Failures["a/Install"] = "Failed"; runner.Failures["b/Install"] = "Failed";
        var service = new LibraryWorkflowService(settings, data, runner, health);
        var original = await service.PreviewAsync(["a", "b"], new() { InstallGames = true, GenerateMissingThemes = true });
        var failed = await service.ExecuteAsync(original, settings);
        var journal = await service.LoadJournalAsync(original.Id);
        // Simulate an older multi-region run with different unfinished stages per version.
        journal.Games.Single(g => g.ProfileId == "b").Stages.RemoveAll(s => s.Stage == "Theme");
        File.WriteAllText(failed.JournalPath, JsonSerializer.Serialize(journal));
        runner.Failures.Clear(); runner.Calls.Clear();
        runner.Names["a"] = "Total Vice (USA)"; runner.Names["b"] = "Total Vice (World)";
        runner.InstallAvailability["a"] = usaAvailable; runner.InstallAvailability["b"] = true;
        var resumed = await service.PreviewResumeAsync(original.Id);
        var winner = usaAvailable ? "a" : "b";
        Check(resumed.Rows.Single(r => r.Selected).ProfileId == winner, "resumed stage groups share one preferred available version");
        var alternate = resumed.Rows.Single(r => r.ProfileId != winner);
        Check(alternate.ExcludedByVersionPolicy && alternate.StagePlans.All(s => s.Status == "NeedsReview"), "resumed alternate cannot be enabled or executed");
        alternate.Selected = true;
        await Reject(() => service.ExecuteAsync(resumed, settings), "re-enabling a resumed regional alternate is rejected");
        alternate.Selected = false;
        var completed = await service.ExecuteAsync(resumed, settings);
        Check(completed.CompletedGames == 1 && runner.Calls.All(c => c.StartsWith(winner + "/")), "resumed winner runs without duplicate-family rejection or alternate side effects");
    }
    private static async Task ProductionWorkflowFixture(string root, VolumeHealthService health)
    {
        var settings = new AppSettings { TeknoParrotPath = Path.Combine(root, "tp"), LaunchBoxPath = Path.Combine(root, "lb"),
            CachePath = Path.Combine(root, "cache"), FillMissingMetadata = false };
        var data = Path.Combine(root, "data"); var profiles = Path.Combine(settings.TeknoParrotPath, "UserProfiles");
        var templates = Path.Combine(settings.TeknoParrotPath, "GameProfiles"); var lbData = Path.Combine(settings.LaunchBoxPath, "Data");
        var playlists = Path.Combine(lbData, "Playlists"); var mediaFolder = Path.Combine(settings.LaunchBoxPath, "MyArtworkOverride");
        foreach (var dir in new[] { profiles, templates, Path.Combine(lbData, "Platforms"), playlists, mediaFolder }) Directory.CreateDirectory(dir);
        var platform = Path.Combine(lbData, "Platforms", "TeknoParrot.xml"); File.WriteAllText(platform, "<LaunchBox><UnrelatedData>preserve</UnrelatedData></LaunchBox>");
        new XDocument(new XElement("LaunchBox", new XElement("Emulator", new XElement("ID", Guid.NewGuid()),
            new XElement("ApplicationPath", Path.Combine(settings.TeknoParrotPath, "TeknoParrotUi.exe")), new XElement("CommandLine", "--profile=%romfile%.xml"), new XElement("FileNameWithoutExtensionAndPath", "true"), new XElement("AutoHotkeyScript", "preserved hook")))).Save(Path.Combine(lbData, "Emulators.xml"));
            Directory.CreateDirectory(settings.TeknoParrotPath); File.WriteAllText(Path.Combine(settings.TeknoParrotPath, "TeknoParrotUi.exe"), "inert executable fixture");
        new XDocument(new XElement("LaunchBox", new XElement("Platform", new XElement("Name", "TeknoParrot")),
            new XElement("PlatformFolder", new XElement("Platform", "TeknoParrot"), new XElement("MediaType", "Fanart - Background"), new XElement("FolderPath", "MyArtworkOverride")))).Save(Path.Combine(lbData, "Platforms.xml"));
        var curated = Path.Combine(playlists, "Curated.xml");
        new XDocument(new XElement("LaunchBox", new XElement("Playlist", new XElement("PlaylistId", Guid.NewGuid()), new XElement("Name", "Curated"), new XElement("AutoPopulate", "false"), new XElement("IncludeWithPlatforms", "true")))).Save(curated);
        var curatedBytes = File.ReadAllBytes(curated); var emulatorBytes = File.ReadAllBytes(Path.Combine(lbData, "Emulators.xml"));
        var ids = new[] { "production-a", "production-a-v2" };
        foreach (var id in ids)
        {
            var launch = Path.Combine(root, id + ".exe"); File.WriteAllBytes(launch, [1, 2, 3]);
            var xml = new XDocument(new XElement("GameProfile", new XElement("ExecutableName", id + ".exe"), new XElement("GamePath", launch),
                new XElement("HasTwoExecutables", "false"), new XElement("Controls", new XElement("Button", "curated binding"))));
            xml.Save(Path.Combine(profiles, id + ".xml")); xml.Save(Path.Combine(templates, id + ".xml"));
        }
        var originalProfiles = ids.ToDictionary(id => id, id => File.ReadAllBytes(Path.Combine(profiles, id + ".xml")));
        var platformBefore = File.ReadAllBytes(platform);
        var runningService = new LibraryWorkflowService(settings, Path.Combine(root, "running-data"), health: health, launchBoxRunning: () => true);
        var runningPlan = await runningService.PreviewAsync(ids, new() { SyncLaunchBox = true });
        var deferred = await runningService.ExecuteAsync(runningPlan, settings);
        var deferredJournal = await runningService.LoadJournalAsync(runningPlan.Id);
        Check(deferred.IncompleteGames == 2 && deferredJournal.Games.All(g => g.Stages.All(s => s.Status == "Deferred")), "injected running LaunchBox defers sync");
        Check(File.ReadAllBytes(platform).SequenceEqual(platformBefore) && Directory.GetFiles(playlists, "*.xml").Length == 1,
            "process guard preserves platform and playlists before any real service writes");
        // Only external health/process observations are injected; all production services remain real.
        var service = new LibraryWorkflowService(settings, data, health: health, launchBoxRunning: () => false);
        var options = new WorkflowOptions { InstallGames = true, SyncLaunchBox = true };
        var plan = await service.PreviewAsync(ids, options);
        Check(plan.Rows.All(r => r.OriginalInstallPlan?.Action == "Already installed"), "production preview recognizes installed files without source lookup or download");
        var first = await service.ExecuteAsync(plan, settings);
        var journal = await service.LoadJournalAsync(plan.Id);
        Check(first.CompletedGames == 2 && first.SucceededStages == 2 && first.UnchangedStages == 2, "production run completes installation checks and game sync without a media handoff");
        Check(journal.Games.All(g => g.Stages.Select(s => s.Stage).SequenceEqual(new[] { "Install", "LaunchBox" })), "production journal contains only requested supported stages");
        Check(ids.All(id => File.ReadAllBytes(Path.Combine(profiles, id + ".xml")).SequenceEqual(originalProfiles[id])) && File.ReadAllBytes(curated).SequenceEqual(curatedBytes)
            && File.ReadAllBytes(Path.Combine(lbData, "Emulators.xml")).SequenceEqual(emulatorBytes), "production integration preserves controls, emulator hooks and curated playlists");
        Check(XDocument.Load(platform).Root!.Element("UnrelatedData")?.Value == "preserve", "production integration preserves unrelated platform data");
        var repeat = await service.PreviewAsync(ids, options);
        var repeated = await service.ExecuteAsync(repeat, settings);
        Check(repeated.CompletedGames == 2 && repeated.UnchangedStages == 4, "production retry preserves completed installs and existing LaunchBox games");
        Check(Directory.GetFiles(playlists, "*.xml").Length == 1 && XDocument.Load(platform).Root!.Elements("Game").Count() == 2,
            "game sync creates neither media queues nor duplicate games");
        Check(Directory.GetFiles(mediaFolder).Length == 0, "game synchronization performs no artwork download");
        Check((await service.LoadUnfinishedAsync()).Count == 0, "completed production runs need no media acknowledgement or continuation");

        var themeGame = plan.Rows.Single(row => row.ProfileId == ids[0]).Game;
        var artwork = Path.Combine(mediaFolder, themeGame.Id + "-01.png");
        File.WriteAllText(artwork, "curated artwork is insufficient without gameplay");
        var videoFolder = Path.Combine(settings.LaunchBoxPath, "Videos", settings.PlatformName); Directory.CreateDirectory(videoFolder);
        var emptySnap = Path.Combine(videoFolder, themeGame.Id + ".mp4"); File.WriteAllBytes(emptySnap, []);
        var assets = new MediaService().FindAssets(settings, themeGame);
        Check(assets.Background == artwork && !MediaService.HasVideoSnap(assets), "production fixture discovers artwork while rejecting its empty snap");
        var themePlan = await service.PreviewAsync([ids[0]], new() { InstallGames = true, SyncLaunchBox = true, GenerateMissingThemes = true });
        var themeStage = themePlan.Rows.Single().StagePlans.Single(stage => stage.Stage == "Theme");
        Check(themeStage.Status == "NeedsReview" && themeStage.Action == "Missing video snap" && !themeStage.ConditionalAfterInstall
            && themeStage.Detail.Contains("Installing or syncing") && themeStage.Detail.Contains("video snap"),
            "theme planning requires a current nonempty snap instead of promising a static or future-media fallback");
        var noTheme = await service.ExecuteAsync(themePlan, settings); var noThemeJournal = await service.LoadJournalAsync(themePlan.Id);
        Check(noTheme.IncompleteGames == 1 && noThemeJournal.Games.Single().Stages.Single(stage => stage.Stage == "Theme").Status == "NeedsReview",
            "missing gameplay stays actionable in the journal without being marked as generated");
        Check(!File.Exists(assets.ThemeDestination) && !Directory.Exists(Path.GetDirectoryName(assets.ThemeDestination)) && !Directory.Exists(settings.CachePath),
            "production workflow creates no theme, staging or artwork cache for an empty video snap");
        Check(File.ReadAllText(artwork) == "curated artwork is insufficient without gameplay" && new FileInfo(emptySnap).Length == 0,
            "missing-snap workflow preserves local media sources");

        Directory.CreateDirectory(Path.GetDirectoryName(assets.ThemeDestination)!); File.WriteAllText(assets.ThemeDestination, "preserve existing curated theme");
        var preservePlan = await service.PreviewAsync([ids[0]], new() { GenerateMissingThemes = true });
        Check(preservePlan.Rows.Single().StagePlans.Single().Action == "Preserve existing theme", "existing themes remain preservable without a snap or FFmpeg");
        var preserveResult = await service.ExecuteAsync(preservePlan, settings);
        Check(preserveResult.CompletedGames == 1 && preserveResult.UnchangedStages == 1 && File.ReadAllText(assets.ThemeDestination) == "preserve existing curated theme",
            "production workflow preserves the existing theme without gameplay or tools");
    }

    private static async Task RecursiveBezelWorkflowFixture(string root, VolumeHealthService health)
    {
        var settings = new AppSettings { TeknoParrotPath = Path.Combine(root, "tp"), BezelImportPath = Path.Combine(root, "repository") };
        var profileDirectory = Path.Combine(settings.TeknoParrotPath, "UserProfiles"); var templateDirectory = Path.Combine(settings.TeknoParrotPath, "GameProfiles");
        Directory.CreateDirectory(profileDirectory); Directory.CreateDirectory(templateDirectory); Directory.CreateDirectory(settings.BezelImportPath);
        const string id = "recursive-bezel";
        var executable = Path.Combine(root, "fixture.exe"); File.WriteAllBytes(executable, [1, 2, 3]);
        var doc = new XDocument(new XElement("GameProfile", new XElement("ExecutableName", "fixture.exe"), new XElement("GamePath", executable),
            new XElement("Controls", "curated controls"), new XElement("ConfigValues", new XElement("FieldInformation", new XElement("CategoryName", "Video"),
                new XElement("FieldName", "Use Bezel"), new XElement("FieldValue", "0"), new XElement("Hint", "Use the TeknoS11/bezels folder for transparent overlays.")))));
        var profile = Path.Combine(profileDirectory, id + ".xml"); doc.Save(profile); doc.Save(Path.Combine(templateDirectory, id + ".xml"));
        var originalProfile = File.ReadAllBytes(profile); var destination = Path.Combine(settings.TeknoParrotPath, "TeknoS11", "bezels", id + ".png");
        var service = new LibraryWorkflowService(settings, Path.Combine(root, "workflow-data"), health: health, launchBoxRunning: () => false);
        var missing = await service.PreviewAsync([id], new() { InstallBezels = true });
        Check(missing.Rows.Single().StagePlans.Single().Action == "Missing" && missing.Rows.Single().StagePlans.Single().BezelSourceFingerprint.Length > 0,
            "a missing recursive bezel source still captures the reviewed candidate set");
        var nested = Path.Combine(settings.BezelImportPath, "downloaded-repository", "arcade", id + ".png"); Directory.CreateDirectory(Path.GetDirectoryName(nested)!); File.WriteAllBytes(nested, BezelChecks.Png(false));
        var firstResult = await service.ExecuteAsync(missing, settings); var firstJournal = await service.LoadJournalAsync(missing.Id);
        Check(firstResult.IncompleteGames == 1 && firstJournal.Games.Single().Stages.Single().Status == "NeedsReview"
            && firstJournal.Games.Single().Stages.Single().Detail.Contains("candidates changed")
            && firstJournal.Games.Single().Stages.Single().BezelSourceFingerprint == missing.Rows.Single().StagePlans.Single().BezelSourceFingerprint,
            "a new nested candidate cannot silently turn a reviewed missing source into an installation, and the snapshot survives journaling");
        Check(!File.Exists(destination) && File.ReadAllBytes(profile).SequenceEqual(originalProfile), "new-source rejection preserves profile and destination");
        var reviewed = await service.PreviewResumeAsync(missing.Id);
        Check(reviewed.Rows.Single().StagePlans.Single().Action == "Install", "a fresh resume review explicitly discovers the new nested overlay");
        var duplicate = Path.Combine(settings.BezelImportPath, "another-pack", id, "bezel.png"); Directory.CreateDirectory(Path.GetDirectoryName(duplicate)!); File.WriteAllBytes(duplicate, BezelChecks.Png(false));
        var duplicateResult = await service.ExecuteAsync(reviewed, settings); var duplicateJournal = await service.LoadJournalAsync(reviewed.Id);
        Check(duplicateResult.IncompleteGames == 1 && duplicateJournal.Games.Single().Stages.Single().Status == "NeedsReview" && !File.Exists(destination)
            && File.ReadAllBytes(profile).SequenceEqual(originalProfile), "adding a second nested source invalidates the reviewed resume before any bezel/profile writes");
        File.Delete(duplicate);
        var resolved = await service.PreviewResumeAsync(reviewed.Id);
        Check(resolved.Rows.Single().StagePlans.Single().Action == "Install", "removing the ambiguity permits a fresh explicit review");
    }

    private static async Task RealLaunchBoxFixture(string root, VolumeHealthService health)
    {
        var settings = new AppSettings { TeknoParrotPath = Path.Combine(root, "tp"), LaunchBoxPath = Path.Combine(root, "lb") };
        Directory.CreateDirectory(Path.Combine(settings.TeknoParrotPath, "UserProfiles"));
        Directory.CreateDirectory(Path.Combine(settings.TeknoParrotPath, "GameProfiles"));
        Directory.CreateDirectory(Path.Combine(settings.LaunchBoxPath, "Data", "Platforms"));
        var platform = Path.Combine(settings.LaunchBoxPath, "Data", "Platforms", "TeknoParrot.xml"); File.WriteAllText(platform, "<LaunchBox><Unknown>preserve</Unknown></LaunchBox>");
        new XDocument(new XElement("LaunchBox", new XElement("Emulator", new XElement("ID", "tp-emu"), new XElement("ApplicationPath", Path.Combine(settings.TeknoParrotPath, "TeknoParrotUi.exe")), new XElement("CommandLine", "--profile=%romfile%.xml"), new XElement("FileNameWithoutExtensionAndPath", "true")))).Save(Path.Combine(settings.LaunchBoxPath, "Data", "Emulators.xml"));
        Directory.CreateDirectory(settings.TeknoParrotPath); File.WriteAllText(Path.Combine(settings.TeknoParrotPath, "TeknoParrotUi.exe"), "inert executable fixture");
        File.WriteAllText(Path.Combine(settings.LaunchBoxPath, "Data", "Platforms.xml"), "<LaunchBox><Platform><Name>TeknoParrot</Name></Platform></LaunchBox>");
        var games = new List<GameRecord>();
        foreach (var id in new[] { "alpha", "alpha-v2" })
        {
            var launch = Path.Combine(root, id + ".exe"); File.WriteAllBytes(launch, [1, 2, 3]);
            var profile = Path.Combine(settings.TeknoParrotPath, "UserProfiles", id + ".xml");
            new XDocument(new XElement("GameProfile", new XElement("GamePath", launch), new XElement("Controls", "curated controls"))).Save(profile);
            var template = Path.Combine(settings.TeknoParrotPath, "GameProfiles", id + ".xml"); File.WriteAllText(template, "<GameProfile><ExecutableName>fixture.exe</ExecutableName></GameProfile>");
            games.Add(new() { Id = id, Name = id, UserProfilePath = profile, TemplatePath = template, GamePath = launch, PathsValid = true, Installed = true });
        }
        var sourceBefore = games.ToDictionary(g => g.UserProfilePath, g => File.ReadAllText(g.UserProfilePath));
        var service = new LibraryWorkflowService(settings, Path.Combine(root, "data"), new RealLbRunner(games, health), health);
        var plan = await service.PreviewAsync(games.Select(g => g.Id), new() { SyncLaunchBox = true }); var result = await service.ExecuteAsync(plan, settings);
        Check(result.CompletedGames == 2 && XDocument.Load(platform).Root!.Elements("Game").Count() == 2, "real services add separate exact-version identities");
        Check(XDocument.Load(platform).Root!.Element("Unknown")?.Value == "preserve" && sourceBefore.All(p => File.ReadAllText(p.Key) == p.Value), "real workflow preserves unrelated XML and controls");
        var repeat = await service.PreviewAsync(games.Select(g => g.Id), new() { SyncLaunchBox = true }); var again = await service.ExecuteAsync(repeat, settings);
        Check(again.UnchangedStages == 2 && XDocument.Load(platform).Root!.Elements("Game").Count() == 2, "real service retry is idempotent");
    }

    private sealed class FakeRunner : IWorkflowStageRunner
    {
        public Dictionary<string, bool> Ready { get; } = [];
        public Dictionary<string, string> Names { get; } = [];
        public Dictionary<string, bool> InstallAvailability { get; } = [];
        public Dictionary<string, string> Failures { get; } = [];
        public List<string> Calls { get; } = [];
        public string Source { get; set; } = "";
        public bool ChangeOwnSource { get; set; }
        public int PreviewCount { get; private set; }
        public string InjectStage { get; set; } = "";
        public Func<string, string, string, CancellationToken, Task>? BeforeExecute { get; set; }
        public Task<List<WorkflowGamePlan>> PreviewAsync(AppSettings settings, string dataDir, IReadOnlyList<string> ids, WorkflowOptions options, IProgress<JobEvent>? progress, CancellationToken ct)
        {
            PreviewCount++;
            return Task.FromResult(ids.Select(id => {
                var title = Names.GetValueOrDefault(id, id);
                var install = options.InstallGames && InstallAvailability.TryGetValue(id, out var available)
                    ? new InstallPlanItem { ProfileId = id, Name = title, Selected = available, Action = available ? "Use local files" : "Needs review" } : null;
                return new WorkflowGamePlan { ProfileId = id, Name = title, Game = new() { Id = id, Name = title, PathsValid = Ready.GetValueOrDefault(id) }, OriginalInstallPlan = install,
                    Stages = string.Join(", ", options.EnabledStages()), StagePlans = options.EnabledStages().Concat(InjectStage.Length == 0 ? Array.Empty<string>() : new[] { InjectStage }).Select(s => new WorkflowStagePlan { Stage = s, Action = "Fixture " + s,
                        Status = s == "Install" && install?.Selected == false ? "NeedsReview" : "Pending", RequiresReadyGame = s != "Install", SourceHashes = Source.Length > 0 ? new() { [Source] = SafeFiles.Hash(Source) } : [] }).ToList() };
            }).ToList());
        }
        public Task<bool> IsGameReadyAsync(AppSettings settings, WorkflowGamePlan game, CancellationToken ct) => Task.FromResult(Ready.GetValueOrDefault(game.ProfileId));
        public async Task<WorkflowStageOutcome> ExecuteStageAsync(AppSettings settings, string dataDir, WorkflowGamePlan game, WorkflowStagePlan stage, IProgress<JobEvent>? progress, CancellationToken ct)
        {
            Calls.Add(game.ProfileId + "/" + stage.Stage);
            if (BeforeExecute != null) await BeforeExecute(game.ProfileId, stage.Stage, dataDir, ct);
            if (Failures.TryGetValue(game.ProfileId + "/" + stage.Stage, out var failure)) return new(failure, "Fixture failure");
            if (stage.Stage == "Install") Ready[game.ProfileId] = true;
            if (ChangeOwnSource) { File.AppendAllText(Source, "own commit"); return new("Completed", "Fixture own commit", [Source]); }
            return new("Completed", "Fixture completed");
        }
    }

    private sealed class RealLbRunner(List<GameRecord> games, VolumeHealthService health) : IWorkflowStageRunner
    {
        private readonly LaunchBoxService lb = new(() => false, health);
        public async Task<List<WorkflowGamePlan>> PreviewAsync(AppSettings settings, string dataDir, IReadOnlyList<string> ids, WorkflowOptions options, IProgress<JobEvent>? progress, CancellationToken ct)
        {
            var plan = await lb.PreviewAsync(settings, games.Where(g => ids.Contains(g.Id)), progress, ct);
            var platform = Path.Combine(settings.LaunchBoxPath, "Data", "Platforms", settings.PlatformName + ".xml");
            return plan.Items.Select(i => new WorkflowGamePlan { ProfileId = i.ProfileId, Name = i.Name, Game = games.Single(g => g.Id == i.ProfileId), StagePlans = [new() { Stage = "LaunchBox", Action = i.Action, SourceHashes = new() { [platform] = SafeFiles.Hash(platform) } }] }).ToList();
        }
        public Task<bool> IsGameReadyAsync(AppSettings settings, WorkflowGamePlan game, CancellationToken ct) => Task.FromResult(game.Game.PathsValid);
        public async Task<WorkflowStageOutcome> ExecuteStageAsync(AppSettings settings, string dataDir, WorkflowGamePlan game, WorkflowStagePlan stage, IProgress<JobEvent>? progress, CancellationToken ct)
        {
            var plan = await lb.PreviewAsync(settings, [game.Game], progress, ct);
            if (plan.Items.Single().Action == "Unchanged") return new("Unchanged", "Existing exact entry preserved.");
            var result = await lb.ApplyAsync(plan, progress, ct);
            Check(result.Succeeded == 1 && result.Errors.Count == 0, "real LaunchBox service committed exactly one game");
            return new("Completed", "Real LaunchBox fixture committed.", [plan.SourcePath]);
        }
    }
}
