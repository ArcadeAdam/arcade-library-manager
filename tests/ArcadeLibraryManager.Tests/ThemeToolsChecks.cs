using ArcadeLibraryManager.Core;

public static class ThemeToolsChecks
{
    public static Task<List<string>> RunAsync(string root)
    {
        root = Path.Combine(root, "tools-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var passed = new List<string>();
        var resolver = new ThemeToolsResolver(Path.Combine(root, "isolated application"));
        var configured = Pair(Path.Combine(root, "selected tools"));
        var settings = new AppSettings { FfmpegPath = configured.Ffmpeg };
        Check(resolver.Find(settings) == configured && resolver.Require(settings) == configured && resolver.GetError(settings) == "", "a complete selected pair resolves without diagnostics");
        Check(ThemeRenderer.FindTools(settings) == configured && ThemeRenderer.RequireTools(settings) == configured && ThemeRenderer.GetToolsError(settings) == "", "public renderer APIs resolve the same selected pair");
        settings.FfmpegPath = "  \"" + configured.Ffmpeg + "\"  ";
        Check(resolver.Require(settings) == configured, "a pasted quoted executable resolves");
        settings.FfmpegPath = "\"" + Path.GetDirectoryName(configured.Ffmpeg) + "\"";
        Check(resolver.Require(settings) == configured, "a quoted tools directory resolves");
        var extractedRoot = Path.Combine(root, "downloaded FFmpeg distribution"); var nested = Pair(Path.Combine(extractedRoot, "bin"));
        settings.FfmpegPath = extractedRoot;
        Check(resolver.Require(settings) == nested, "an extracted distribution root resolves its bin directory");
        settings.FfmpegPath = nested.Ffprobe;
        Check(resolver.Require(settings) == nested, "selecting the companion still locates the correct encoder rather than running ffprobe as FFmpeg");
        passed.Add("Executable, quoted paths, tool folders and extracted bin distributions resolve the correct colocated pair");

        var broken = Path.Combine(root, "encoder without probe"); Directory.CreateDirectory(broken);
        var encoder = Path.Combine(broken, "ffmpeg.exe"); File.WriteAllBytes(encoder, [1, 2, 3]);
        settings = new() { FfmpegPath = encoder };
        var missingProbe = resolver.GetError(settings);
        Check(resolver.Find(settings) == null && missingProbe.Contains("FFmpeg was found") && missingProbe.Contains(Path.Combine(broken, "ffprobe.exe")) && missingProbe.Contains("missing") && missingProbe.Contains("Open Setup") && missingProbe.Contains("retry Make themes"), "a missing companion names ffprobe and its expected path with the actual UI action");
        ThrowsSame(resolver, settings, missingProbe);
        File.WriteAllBytes(Path.Combine(broken, "ffprobe.exe"), []);
        Check(resolver.GetError(settings).Contains("is empty") && resolver.Find(settings) == null, "an empty ffprobe executable is not considered installed");
        File.WriteAllBytes(Path.Combine(broken, "ffprobe.exe"), [4, 5, 6]);
        Check(resolver.Require(settings).Ffmpeg == encoder && resolver.GetError(settings) == "", "installing the companion is detected immediately without stale failure caching");
        File.WriteAllBytes(encoder, []);
        Check(resolver.GetError(settings).Contains("ffmpeg.exe") && resolver.GetError(settings).Contains("is empty"), "an empty encoder receives its own precise error");
        passed.Add("Missing or empty companions produce actionable paths and recover immediately after the files are repaired");

        var probeOnly = Path.Combine(root, "probe without encoder"); Directory.CreateDirectory(probeOnly); File.WriteAllBytes(Path.Combine(probeOnly, "ffprobe.exe"), [1]);
        settings = new() { FfmpegPath = Path.Combine(probeOnly, "ffmpeg.exe") };
        var missingEncoder = resolver.GetError(settings);
        Check(missingEncoder.Contains("ffprobe.exe was found") && missingEncoder.Contains(settings.FfmpegPath) && missingEncoder.Contains("is missing"), "a missing encoder is distinguished from a missing probe");
        settings.FfmpegPath = Path.Combine(root, "missing distribution");
        Check(resolver.GetError(settings).Contains("Neither ffmpeg.exe nor ffprobe.exe") && resolver.GetError(settings).Contains(settings.FfmpegPath), "both missing tools identify the checked location");
        settings.FfmpegPath = "bad\0path";
        Check(resolver.Find(settings) == null && resolver.GetError(settings).Contains("invalid or inaccessible"), "an invalid configured path yields a usable diagnostic instead of a path exception");
        passed.Add("Missing FFmpeg, missing both tools and invalid configured paths remain distinct");

        var fallbackRoot = Path.Combine(root, "fallback"); var fallbackResolver = new ThemeToolsResolver(Path.Combine(fallbackRoot, "app"));
        var selectedDirectory = Path.Combine(fallbackRoot, "partial-selected"); Directory.CreateDirectory(selectedDirectory);
        var selectedEncoder = Path.Combine(selectedDirectory, "ffmpeg.exe"); File.WriteAllBytes(selectedEncoder, [1]);
        var lbRoot = Path.Combine(fallbackRoot, "LaunchBox"); var lbToolsDirectory = Path.Combine(lbRoot, "ThirdParty", "FFMPEG"); Directory.CreateDirectory(lbToolsDirectory);
        File.WriteAllBytes(Path.Combine(lbToolsDirectory, "ffprobe.exe"), [2]);
        settings = new() { FfmpegPath = selectedEncoder, LaunchBoxPath = lbRoot };
        Check(fallbackResolver.Find(settings) == null, "separate partial installations are never mixed into a pair");
        var fallback = Pair(lbToolsDirectory);
        Check(fallbackResolver.Require(settings) == fallback && settings.FfmpegPath == selectedEncoder, "a complete LaunchBox fallback works without rewriting the selected path");
        File.WriteAllBytes(Path.Combine(selectedDirectory, "ffprobe.exe"), [3]);
        Check(fallbackResolver.Require(settings).Ffmpeg == selectedEncoder, "a complete explicitly selected pair retains precedence");
        var bundledApp = Path.Combine(root, "portable app"); var bundled = Pair(Path.Combine(bundledApp, "Tools", "ffmpeg", "bin"));
        Check(new ThemeToolsResolver(bundledApp).Require(new AppSettings()) == bundled, "portable bundled bin tools resolve without LaunchBox");
        Check(File.ReadAllBytes(configured.Ffmpeg).SequenceEqual(new byte[] { 1, 2, 3 }) && File.ReadAllBytes(configured.Ffprobe).SequenceEqual(new byte[] { 1, 2, 3 }), "discovery preserves executable bytes");
        passed.Add("Fallback requires a complete sibling pair, preserves explicit precedence, supports portable tools and writes no settings");
        return Task.FromResult(passed);
    }

    private static MediaTools Pair(string folder)
    {
        Directory.CreateDirectory(folder); var ffmpeg = Path.GetFullPath(Path.Combine(folder, "ffmpeg.exe")); var probe = Path.GetFullPath(Path.Combine(folder, "ffprobe.exe"));
        File.WriteAllBytes(ffmpeg, [1, 2, 3]); File.WriteAllBytes(probe, [1, 2, 3]); return new(ffmpeg, probe);
    }
    private static void ThrowsSame(ThemeToolsResolver resolver, AppSettings settings, string expected)
    {
        try { resolver.Require(settings); throw new Exception("Missing tools were accepted"); }
        catch (InvalidOperationException ex) { Check(ex.Message == expected, "preflight and required-tool diagnostics agree"); }
    }
    private static void Check(bool condition, string detail) { if (!condition) throw new Exception("Theme tools check failed: " + detail); }
}
