using ArcadeLibraryManager.Core;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

public static class MediaChecks
{
    public static async Task<List<string>> RunAsync(string fixtureRoot, string? ffmpegPath = null)
    {
        var passed = new List<string>();
        DurationPolicyChecks(); passed.Add("Theme lengths normalize to 25-33 seconds with 30-second default and safe reference handling");
        var root = Path.Combine(fixtureRoot, "media-" + Guid.NewGuid().ToString("N"));
        var lb = Path.Combine(root, "Launch Box");
        var images = Path.Combine(root, "custom art");
        var themes = Path.Combine(root, "custom themes");
        var snaps = Path.Combine(root, "custom snaps");
        Directory.CreateDirectory(Path.Combine(lb, "Data", "Platforms")); Directory.CreateDirectory(images); Directory.CreateDirectory(themes); Directory.CreateDirectory(snaps);
        var settings = new AppSettings { LaunchBoxPath = lb, PlatformName = "Arcade Test", TeknoParrotPath = Path.Combine(root, "Emulator"), CachePath = Path.Combine(root, "cache"), VideoWidth = 640, VideoHeight = 360, VideoFps = 30, VideoSeconds = 2 };
        var folders = new XDocument(new XElement("LaunchBox", new[] { ("Theme Video", themes), ("Video", snaps), ("Fanart - Background", images) }.Select(pair => new XElement("PlatformFolder", new XElement("Platform", settings.PlatformName), new XElement("MediaType", pair.Item1), new XElement("FolderPath", pair.Item2)))));
        folders.Save(Path.Combine(lb, "Data", "Platforms.xml"));
        var platform = new XDocument(new XElement("LaunchBox", new XElement("Game", new XElement("ID", "test-guid"), new XElement("Title", "Exact Game Rev B"), new XElement("DatabaseID", "321"), new XElement("ApplicationPath", "../Emulator/UserProfiles/exact_b.xml"))));
        platform.Save(Path.Combine(lb, "Data", "Platforms", settings.PlatformName + ".xml"));
        var game = new GameRecord { Id = "exact_b", Name = "Exact Game Rev B" };
        await File.WriteAllTextAsync(Path.Combine(images, "Exact Game Rev A-01.png"), "wrong edition");
        await File.WriteAllTextAsync(Path.Combine(themes, "Exact Game Rev A.mp4"), "wrong edition");
        var service = new MediaService();
        var media = service.FindAssets(settings, game);
        Require(media.Background.Length == 0 && media.Theme.Length == 0, "Wrong edition was accepted");
        passed.Add("Media matching rejects a different revision");
        await File.WriteAllTextAsync(Path.Combine(images, "321-01.png"), "database id art"); service.ClearCache();
        media = service.FindAssets(settings, game);
        Require(media.Background == Path.Combine(images, "321-01.png"), "Exact database identity or media folder override failed");
        Require(Path.GetDirectoryName(media.ThemeDestination) == themes, "Theme folder override failed");
        passed.Add("Exact database identity and platform media overrides resolve");
        var curated = Path.Combine(themes, "curated.mp4"); await File.WriteAllTextAsync(curated, "preserve custom bytes");
        var before = SHA256.HashData(await File.ReadAllBytesAsync(curated));
        var renderer = new ThemeRenderer(CleanHealth());
        var returned = await renderer.RenderAsync(settings, new MediaAssets { ProfileId = game.Id, Theme = curated, ThemeDestination = curated });
        var after = SHA256.HashData(await File.ReadAllBytesAsync(curated)); Require(returned == curated && before.SequenceEqual(after), "Curated theme changed");
        returned = await renderer.RenderAsync(settings, new MediaAssets { ProfileId = game.Id, ThemeDestination = curated });
        after = SHA256.HashData(await File.ReadAllBytesAsync(curated)); Require(returned == curated && before.SequenceEqual(after), "Destination collision overwritten");
        passed.Add("Curated themes and existing destination collisions are preserved without running tools");
        var missingSnapArt = Path.Combine(root, "art without gameplay.ppm"); WritePpm(missingSnapArt, 32, 32);
        var emptySnap = Path.Combine(snaps, "empty.mp4"); await File.WriteAllBytesAsync(emptySnap, []);
        var mustNotProbe = new ThemeEncoding((_, _, _) => throw new Exception("Missing snap attempted a hardware probe"));
        var missingSnapRenderer = new ThemeRenderer(CleanHealth(), mustNotProbe);
        foreach (var candidate in new[] { "", Path.Combine(snaps, "does-not-exist.mp4"), emptySnap })
        {
            var absentAssets = new MediaAssets { ProfileId = "missing-snap", Background = missingSnapArt, Snap = candidate, ThemeDestination = Path.Combine(root, "missing-snap-output", "theme.mp4") };
            Require(!MediaService.HasVideoSnap(absentAssets), "Missing or empty snap reported ready");
            foreach (var isPreview in new[] { false, true })
            {
                var rejected = false;
                try { if (isPreview) await missingSnapRenderer.RenderPreviewAsync(settings, absentAssets); else await missingSnapRenderer.RenderAsync(settings, absentAssets); }
                catch (InvalidOperationException ex) { rejected = ex.Message.Contains("video snap", StringComparison.OrdinalIgnoreCase); }
                Require(rejected, "Missing gameplay did not produce an actionable error before tools");
            }
        }
        Require(!Directory.Exists(Path.Combine(root, "missing-snap-output")) && !Directory.Exists(settings.CachePath), "Missing snap created cache, staging or an output directory");
        passed.Add("Missing, nonexistent and zero-byte snaps block themes and previews before tools, artwork or cache writes");
        var healthSnap = Path.Combine(root, "health-check-snap.mp4"); await File.WriteAllBytesAsync(healthSnap, [1]);
        var dirtyVolume = new VolumeHealthService((path, token) => Task.FromResult(new VolumeHealthResult(path, Path.GetPathRoot(path)!, "NTFS", VolumeHealthState.Dirty, "Fixture dirty flag")));
        var blockedDestination = Path.Combine(root, "must-not-create", "theme.mp4");
        var wasBlocked = false;
        try { await new ThemeRenderer(dirtyVolume).RenderAsync(settings, new MediaAssets { ProfileId = "blocked", Snap = healthSnap, ThemeDestination = blockedDestination }); } catch (VolumeHealthException) { wasBlocked = true; }
        Require(wasBlocked && !Directory.Exists(Path.GetDirectoryName(blockedDestination)), "Dirty output volume was written before rendering");
        passed.Add("Dirty output volumes block rendering before creating destination or staging");
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath)) { passed.Add("SKIP: Real video checks need ALM_TEST_FFMPEG (ffmpeg.exe with sibling ffprobe.exe)"); return passed; }
        settings.FfmpegPath = ffmpegPath;
        var art = Path.Combine(root, "asset's [50%]; art.ppm"); WritePpm(art, 640, 360);
        var invalidSnapAssets = new MediaAssets { ProfileId = "still-image-as-snap", Background = art, Snap = art, ThemeDestination = Path.Combine(root, "invalid-snap-output", "theme.mp4") };
        foreach (var previewMode in new[] { false, true })
        {
            var rejected = false;
            try { if (previewMode) await missingSnapRenderer.RenderPreviewAsync(settings, invalidSnapAssets); else await missingSnapRenderer.RenderAsync(settings, invalidSnapAssets); }
            catch (InvalidDataException ex) { rejected = ex.Message.Contains("video", StringComparison.OrdinalIgnoreCase); }
            Require(rejected, "A nonempty still image was accepted as a playable video snap");
        }
        Require(!Directory.Exists(Path.Combine(root, "invalid-snap-output")) && !Directory.Exists(settings.CachePath), "Invalid video content prepared artwork or output");
        passed.Add("The video probe rejects a still image posing as a snap before processing artwork or staging output");
        var snap = Path.Combine(root, "game's [50%] & snap.mp4");
        await Tool(ffmpegPath, ["-hide_banner", "-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=30", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "2", "-c:v", "libx264", "-threads", "2", "-pix_fmt", "yuv420p", "-c:a", "aac", snap], root);
        var renderAssets = new MediaAssets { ProfileId = "quote_test", Title = "Theme: [test] 'quotes' 50% %{safe}", Background = art, Snap = snap, ThemeDestination = Path.Combine(themes, "quoted output's [50%].mp4") };
        var rendered = await renderer.RenderAsync(settings, renderAssets);
        var info = await renderer.InspectReferenceAsync(settings, rendered);
        Require(info.Width == 640 && info.Height == 360 && Math.Abs(info.DurationSeconds - ThemeDuration.Normalize(settings.VideoSeconds)) < .1 && info.HasAudio, "Rendered sample has incorrect streams");
        Require(File.Exists(rendered + ".alm.json"), "Provenance is missing");
        var sample = Path.Combine(fixtureRoot, "theme-smoke.mp4"); File.Copy(rendered, sample, true);
        passed.Add("Real FFmpeg artwork/gameplay/audio render decodes with punctuation-rich filenames and literal title text");
        Require(info.DurationSeconds >= 25 && info.DurationSeconds <= 25.1, "A 2-second request was not extended to the 25-second minimum");
        passed.Add("A 2-second source/request renders a complete 25-second theme by looping the source");
        settings.VideoSeconds = ThemeDuration.DefaultSeconds;
        settings.ThemeAudio = false;
        var defaultAssets = new MediaAssets { ProfileId = "default-duration", Title = "Default Duration", Background = art, Snap = snap, ThemeDestination = Path.Combine(themes, "default-duration.mp4") };
        await renderer.RenderAsync(settings, defaultAssets);
        var defaultInfo = await renderer.InspectReferenceAsync(settings, defaultAssets.ThemeDestination);
        Require(Math.Abs(defaultInfo.DurationSeconds - ThemeDuration.Normalize(settings.VideoSeconds)) < .1 && settings.VideoSeconds == 30, "Default gameplay theme did not render for 30 seconds");
        Require(!defaultInfo.HasAudio && File.ReadAllText(defaultAssets.ThemeDestination + ".alm.json").Contains("\"StaticArtworkFallback\": false"), "Gameplay provenance or disabled audio is incorrect");
        passed.Add("The default gameplay theme renders for 30 seconds and records provenance with audio disabled");
        settings.VideoSeconds = 120;
        var preview = await renderer.RenderPreviewAsync(settings, renderAssets);
        var previewInfo = await renderer.InspectReferenceAsync(settings, preview);
        Require(Math.Abs(previewInfo.DurationSeconds - ThemeDuration.Normalize(settings.VideoSeconds)) < .1 && previewInfo.DurationSeconds >= 32.9 && previewInfo.DurationSeconds <= 33.1, "A 120-second preview request did not clamp to 33 seconds");
        Require(File.Exists(preview) && preview.StartsWith(Path.GetFullPath(settings.CachePath), StringComparison.OrdinalIgnoreCase), "Preview wrote to the library");
        passed.Add("Preview is rendered to app-owned cache and a 120-second request clamps to 33 seconds");
        settings.VideoSeconds = 120;
        var cancelAssets = new MediaAssets { ProfileId = "cancel", Title = "Cancellation", Background = art, Snap = snap, ThemeDestination = Path.Combine(themes, "cancel.mp4") };
        using var cancel = new CancellationTokenSource();
        var cancelled = false;
        try { await renderer.RenderAsync(settings, cancelAssets, new ImmediateProgress(e => { if (e.Message == "Rendering theme") cancel.Cancel(); }), cancel.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Require(cancelled && !File.Exists(cancelAssets.ThemeDestination), "Cancellation committed an unfinished theme");
        Require(Directory.EnumerateFiles(Path.Combine(themes, ".alm-staging"), "job.json", SearchOption.AllDirectories).Any(f => File.ReadAllText(f).Contains("\"State\": \"Cancelled\"")), "Cancelled render journal missing");
        passed.Add("Cancellation stops FFmpeg and retains a diagnostic journal without committing a theme");
        return passed;
    }

    private static void DurationPolicyChecks()
    {
        Require(ThemeDuration.MinimumSeconds == 25 && ThemeDuration.MaximumSeconds == 33 && ThemeDuration.DefaultSeconds == 30, "Theme duration constants differ from the 25-33 second requirement");
        Require(new AppSettings().VideoSeconds == 30, "New settings do not default to 30 seconds");
        foreach (var (requested, expected) in new[] { (int.MinValue, 25), (0, 25), (2, 25), (25, 25), (30, 30), (33, 33), (120, 33), (int.MaxValue, 33) })
            Require(ThemeDuration.Normalize(requested) == expected, "Duration normalization failed for " + requested);
        foreach (var (reference, expected) in new[] { (-7d, 25), (0d, 25), (2d, 25), (25d, 25), (30d, 30), (31.2, 31), (31.8, 32), (33d, 33), (120d, 33), (double.MaxValue, 33), (double.NaN, 30), (double.PositiveInfinity, 30), (double.NegativeInfinity, 30) })
        {
            Require(ThemeDuration.FromReference(reference) == expected, "Reference duration normalization failed for " + reference);
            var settings = new AppSettings();
            ThemeRenderer.ApplyReferencePreset(settings, new ThemeReferenceInfo("fixture", 320, 240, 30, reference, false, "fixture"));
            Require(settings.VideoSeconds == expected, "Reference preset bypassed duration policy for " + reference);
        }
    }
    public static async Task<List<ThemeReferenceInfo>> InspectReferencesAsync(string ffmpegPath, string referenceDirectory, string reportPath)
    {
        var settings = new AppSettings { FfmpegPath = ffmpegPath };
        var renderer = new ThemeRenderer(CleanHealth()); var results = new List<ThemeReferenceInfo>();
        foreach (var file in Directory.EnumerateFiles(referenceDirectory).Where(f => new[] { ".mp4", ".avi", ".mkv" }.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)).Take(3)) results.Add(await renderer.InspectReferenceAsync(settings, file));
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true })); return results;
    }
    private static VolumeHealthService CleanHealth() => new((path, token) => Task.FromResult(new VolumeHealthResult(path, Path.GetPathRoot(path)!, "NTFS", VolumeHealthState.Clean, "Isolated fixture provider")));
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void WritePpm(string path, int width, int height)
    {
        using var stream = File.Create(path); stream.Write(Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n"));
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++) { stream.WriteByte((byte)(30 + 80 * x / width)); stream.WriteByte((byte)(35 + 100 * y / height)); stream.WriteByte((byte)(90 + 100 * x / width)); }
    }
    private static async Task Tool(string executable, IEnumerable<string> args, string directory)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; var stderr = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); var errors = await stderr;
        Require(process.ExitCode == 0, "Fixture generation failed: " + errors);
    }
    private sealed class ImmediateProgress(Action<JobEvent> action) : IProgress<JobEvent> { public void Report(JobEvent value) => action(value); }
}



