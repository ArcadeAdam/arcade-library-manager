using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using ArcadeLibraryManager.Core;

public static class FanartChecks
{
    private const int Width = 640, Height = 360;
    private sealed record PixelBounds(int Left, int Top, int Right, int Bottom, int Count, double CenterX, double CenterY)
    {
        public int Width => Right - Left + 1;
        public int Height => Bottom - Top + 1;
    }
    private sealed record Frame(byte[] Pixels)
    {
        public (byte R, byte G, byte B) At(int x, int y)
        {
            var i = (Math.Clamp(y, 0, Height - 1) * Width + Math.Clamp(x, 0, Width - 1)) * 3;
            return (Pixels[i], Pixels[i + 1], Pixels[i + 2]);
        }
        public PixelBounds Find(Func<byte, byte, byte, bool> match)
        {
            var left = Width; var top = Height; var right = -1; var bottom = -1; var count = 0; long sumX = 0, sumY = 0;
            for (var y = 0; y < Height; y++) for (var x = 0; x < Width; x++)
            {
                var (r, g, b) = At(x, y); if (!match(r, g, b)) continue;
                left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                count++; sumX += x; sumY += y;
            }
            return new(left, top, right, bottom, count, count == 0 ? 0 : sumX / (double)count, count == 0 ? 0 : sumY / (double)count);
        }
    }
    // Chroma subsampling blends red gameplay markers into a few yellow edge pixels.
    // Track the largest connected subject, not isolated color matches elsewhere on the canvas.
    private static PixelBounds Subject(Frame frame) => Subject(frame, out _);
    private static PixelBounds Subject(Frame frame, out int[] points)
    {
        points = [];
        var mask = new bool[Width * Height]; var queue = new int[mask.Length];
        for (var y = 0; y < Height; y++) for (var x = 0; x < Width; x++) { var p = frame.At(x, y); mask[y * Width + x] = Yellow(p.R, p.G, p.B); }
        PixelBounds best = new(Width, Height, -1, -1, 0, 0, 0);
        for (var start = 0; start < mask.Length; start++)
        {
            if (!mask[start]) continue;
            var count = 0; var tail = 1; queue[0] = start; mask[start] = false;
            var left = Width; var top = Height; var right = -1; var bottom = -1; long sumX = 0, sumY = 0;
            for (var head = 0; head < tail; head++)
            {
                var index = queue[head]; var x = index % Width; var y = index / Width;
                count++; sumX += x; sumY += y; left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                void Add(int neighbor) { if (mask[neighbor]) { mask[neighbor] = false; queue[tail++] = neighbor; } }
                if (x > 0) Add(index - 1); if (x + 1 < Width) Add(index + 1); if (y > 0) Add(index - Width); if (y + 1 < Height) Add(index + Width);
            }
            if (count > best.Count) { best = new(left, top, right, bottom, count, sumX / (double)count, sumY / (double)count); points = queue.AsSpan(0, count).ToArray(); }
        }
        return best;
    }
    private static bool Green(byte r, byte g, byte b) => g > 145 && g > r + 65 && b < 125;
    private static bool Yellow(byte r, byte g, byte b) => r > 160 && g > 130 && b < 100 && Math.Abs(r - g) < 90;
    private static bool Blue(byte r, byte g, byte b) => b > r + 25 && b > g + 25;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("Fanart check failed: " + message); }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static double Movement(PixelBounds a, PixelBounds b) => Math.Sqrt(Math.Pow(a.CenterX - b.CenterX, 2) + Math.Pow(a.CenterY - b.CenterY, 2));

    public static async Task<List<string>> RunAsync(string fixtureRoot, string? ffmpegPath)
    {
        var passed = new List<string>();
        var defaults = new AppSettings();
        Check(defaults.ThemeLayout == "Fanart" && defaults.ThemeVideoSide == "Right", "new settings default to a right-side Fanart layout");
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
        {
            passed.Add("SKIP: Fanart pixel checks require ALM_TEST_FFMPEG with sibling ffprobe.exe"); return passed;
        }
        var root = Path.Combine(fixtureRoot, "fanart-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var art = Path.Combine(root, "blue backdrop.png"); var cutout = Path.Combine(root, "transparent yellow subject.png"); var snap = Path.Combine(root, "green native 4x3.mp4");
        await RunTool(ffmpegPath, ["-hide_banner", "-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=0x1420B0:s=640x360:r=15", "-frames:v", "1", "-threads", "1", art], root);
        await RunTool(ffmpegPath, ["-hide_banner", "-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=black:s=160x240,format=rgba,geq=r=255:g=220:b=32:a='if(between(X,40,119)*between(Y,40,199),255,0)'", "-frames:v", "1", "-threads", "1", "-pix_fmt", "rgba", cutout], root);
        var alpha = await RunTool(ffmpegPath, ["-hide_banner", "-nostdin", "-v", "error", "-i", cutout, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgba", "pipe:1"], root);
        Check(alpha.Length == 160 * 240 * 4 && alpha[3] == 0 && alpha[(120 * 160 + 80) * 4 + 3] == 255, "RGBA fixture contains transparent margins and an opaque subject");
        var snapFilter = "color=c=0x20E040:s=320x240:r=15,drawbox=x=0:y=0:w=20:h=20:color=red:t=fill,drawbox=x=300:y=0:w=20:h=20:color=red:t=fill,drawbox=x=0:y=220:w=20:h=20:color=red:t=fill,drawbox=x=300:y=220:w=20:h=20:color=red:t=fill";
        await RunTool(ffmpegPath, ["-hide_banner", "-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", snapFilter, "-t", "4", "-c:v", "libx264", "-threads", "2", "-pix_fmt", "yuv420p", snap], root);
        var sourceHashes = new[] { art, cutout, snap }.ToDictionary(path => path, Hash);
        var settings = new AppSettings { FfmpegPath = ffmpegPath, CachePath = Path.Combine(root, "cache"), ThemeAssetsPath = Path.Combine(root, "assets"), ThemeAutoCutouts = false, ThemeCutoutModelPath = "", ThemeLayout = "Fanart", ThemeVideoSide = "Right", ThemeMotionStrength = 100, ThemeAudio = false, VideoWidth = Width, VideoHeight = Height, VideoFps = 15, VideoSeconds = 4 };
        MediaAssets Assets(string name, bool gameplay = true) => new() { ProfileId = name, Title = "Fanart pixel fixture", Background = art, BackgroundKind = "Fanart", Snap = gameplay ? snap : "", Cutouts = new() { cutout }, ArtworkSources = new() { art }, ThemeAssetDirectory = Path.Combine(root, "assets", name), ThemeDestination = Path.Combine(root, name + ".mp4") };
        var renderer = new ThemeRenderer(new VolumeHealthService((path, token) => Task.FromResult(new VolumeHealthResult(path, "fixture", "NTFS", VolumeHealthState.Clean, "Isolated fixture"))));
        var rightAssets = Assets("right"); var right = await renderer.RenderAsync(settings, rightAssets);
        var rightInfo = await renderer.InspectReferenceAsync(settings, right);
        Check(Math.Abs(rightInfo.DurationSeconds - ThemeDuration.Normalize(settings.VideoSeconds)) < .15, "Fanart output length obeys the 25-33 second policy even with a 4-second source");
        CheckCorners(await ReadFrame(ffmpegPath, right, ThemeDuration.Normalize(settings.VideoSeconds) - 1, root), ReadScreen(right));
        var rightOne = await ReadFrame(ffmpegPath, right, 1.2, root); var rightTwo = await ReadFrame(ffmpegPath, right, 2.5, root);
        var rightScreen = rightOne.Find(Green); var rightSubject = Subject(rightOne); var laterSubject = Subject(rightTwo);
        await File.WriteAllTextAsync(Path.Combine(root, "right-pixel-diagnostics.json"), JsonSerializer.Serialize(new { Screen = rightScreen, AllYellowPixels = rightOne.Find(Yellow), Subject = rightSubject, LaterSubject = laterSubject, MotionPixels = Movement(rightSubject, laterSubject) }, new JsonSerializerOptions { WriteIndented = true }));
        CheckScreen(right, rightOne, rightScreen, "Right"); CheckSubject(rightOne, rightSubject, "Right");
        Check(Movement(rightSubject, laterSubject) >= 0.9, "transparent subject actually moves between decoded frames when motion is enabled");
        Check(laterSubject.Left > 2 && laterSubject.Top > 2 && laterSubject.Right < Width - 3 && laterSubject.Bottom < Height - 3, "moving subject remains inside the canvas");
        // A faster bounce must visibly rise and return within this short clip, rather than merely drift.
        var motionTimes = new[] { .9, 1.5, 2.2, 3.1 }; var motionSamples = new List<(double Seconds, PixelBounds Bounds)>();
        var actualScreen = ReadScreen(right);
        foreach (var seconds in motionTimes)
        {
            var frame = await ReadFrame(ffmpegPath, right, seconds, root); var subject = Subject(frame);
            motionSamples.Add((seconds, subject)); CheckSubject(frame, subject, "Right");
            Check(subject.Right < actualScreen.X - actualScreen.Border, "bouncing subject remains inside the art region at " + seconds.ToString(CultureInfo.InvariantCulture) + "s: " + JsonSerializer.Serialize(subject));
        }
        var verticalTravel = motionSamples.Max(s => s.Bounds.CenterY) - motionSamples.Min(s => s.Bounds.CenterY);
        var horizontalTravel = motionSamples.Max(s => s.Bounds.CenterX) - motionSamples.Min(s => s.Bounds.CenterX);
        var apexIndex = motionSamples.Select((sample, index) => (sample.Bounds.CenterY, Index: index)).MinBy(s => s.CenterY).Index;
        var totalTravel = motionSamples.Zip(motionSamples.Skip(1), (a, b) => Movement(a.Bounds, b.Bounds)).Sum();
        var motionReport = new { Samples = motionSamples.Select(s => new { s.Seconds, s.Bounds }).ToArray(), HorizontalExcursion = horizontalTravel, VerticalExcursion = verticalTravel, TraveledPixels = totalTravel, ApexAtSeconds = motionSamples[apexIndex].Seconds };
        await File.WriteAllTextAsync(Path.Combine(root, "bounce-pixel-diagnostics.json"), JsonSerializer.Serialize(motionReport, new JsonSerializerOptions { WriteIndented = true }));
        Check(verticalTravel >= 15 && totalTravel >= 30, "stronger bounce has visible vertical excursion and travel: " + JsonSerializer.Serialize(motionReport));
        Check(apexIndex > 0 && apexIndex < motionSamples.Count - 1 && motionSamples[0].Bounds.CenterY - motionSamples[apexIndex].Bounds.CenterY >= 8 && motionSamples[^1].Bounds.CenterY - motionSamples[apexIndex].Bounds.CenterY >= 8,
            "faster bounce rises and visibly returns within 2.2 seconds: " + JsonSerializer.Serialize(motionReport));
        Check(motionSamples.Min(s => s.Bounds.Count) >= motionSamples.Max(s => s.Bounds.Count) * .97, "bouncing keeps the full opaque subject visible rather than clipping it against the canvas or gameplay");
        passed.Add("Decoded motion shows a stronger up/down bounce within 2.2 seconds while the complete subject stays inside its art region");        CheckAnimatedBorder(rightOne, rightTwo, rightScreen);
        passed.Add("Decoded right-side gameplay preserves its native 4:3 shape and four corner markers, with no forced letterboxing");
        passed.Add("Transparent cutout is visible without an opaque matte, remains bounded, and moves in actual decoded frames");
        passed.Add("Gameplay border is visibly luminous and changes color over time");

        settings.ThemeLayout = "Artwork"; settings.ThemeVideoSide = "Left"; var left = await renderer.RenderAsync(settings, Assets("left")); var leftFrame = await ReadFrame(ffmpegPath, left, 1.2, root);
        var leftScreen = leftFrame.Find(Green); var leftSubject = Subject(leftFrame);
        CheckScreen(left, leftFrame, leftScreen, "Left"); CheckSubject(leftFrame, leftSubject, "Left");
        Check(rightScreen.CenterX - leftScreen.CenterX > Width * .35 && leftSubject.CenterX - rightSubject.CenterX > Width * .35, "side choice moves gameplay and art to opposite sides");
        using (var legacy = JsonDocument.Parse(File.ReadAllText(left + ".alm.json"))) Check(legacy.RootElement.GetProperty("Output").GetProperty("layout").GetString() == "Fanart", "stored Artwork layout normalizes to Fanart with visible gameplay");
        settings.ThemeLayout = "Fanart";
        passed.Add("Left/right setting moves gameplay and art to opposite sides; legacy Artwork layout retains gameplay as Fanart");

        settings.ThemeVideoSide = "Right"; settings.ThemeMotionStrength = 0; var still = await renderer.RenderAsync(settings, Assets("motion-off"));
        var stillOne = Subject(await ReadFrame(ffmpegPath, still, 1.2, root)); var stillTwo = Subject(await ReadFrame(ffmpegPath, still, 2.5, root));
        Check(stillOne.Count > 300 && Movement(stillOne, stillTwo) < 0.45, "motion strength zero keeps the subject stationary");
        var stillSamples = new List<PixelBounds>();
        foreach (var seconds in motionTimes) stillSamples.Add(Subject(await ReadFrame(ffmpegPath, still, seconds, root)));
        Check(stillSamples.All(sample => sample.Count > 300 && Movement(stillSamples[0], sample) < .45), "motion strength zero remains stationary at every bounce sample time");
        passed.Add("Motion strength zero stops cutout movement without suppressing the subject");

        var originalCache = settings.CachePath;
        settings.CachePath = Path.Combine(root, "must-not-prepare-art"); settings.ThemeAutoCutouts = true;
        var emptySnap = Path.Combine(root, "empty snap.mp4"); await File.WriteAllBytesAsync(emptySnap, []);
        foreach (var missing in new[] { "", emptySnap, Path.Combine(root, "missing snap.mp4") })
        {
            var noGameplay = Assets("requires-gameplay", false); noGameplay.Snap = missing;
            foreach (var preview in new[] { false, true })
            {
                var rejected = false;
                try { if (preview) await renderer.RenderPreviewAsync(settings, noGameplay); else await renderer.RenderAsync(settings, noGameplay); }
                catch (InvalidOperationException ex) { rejected = ex.Message.Contains("video snap", StringComparison.OrdinalIgnoreCase); }
                Check(rejected && !File.Exists(noGameplay.ThemeDestination), "real background and alpha cutouts never permit a theme without gameplay");
            }
        }
        Check(!Directory.Exists(settings.CachePath), "missing gameplay never preprocesses art, cutouts or previews in cache");
        settings.CachePath = originalCache; settings.ThemeAutoCutouts = false;
        passed.Add("Real background and transparent cutouts cannot render a theme or preview when the snap is missing or empty");

        // Cinema uses a wider panel; foreground placement must shrink its art region accordingly.
        var cinemaSnap = Path.Combine(root, "green cinema 16x9.mp4");
        await RunTool(ffmpegPath, ["-hide_banner", "-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", Pattern(320, 180), "-t", "4", "-c:v", "libx264", "-threads", "2", "-pix_fmt", "yuv420p", cinemaSnap], root);
        sourceHashes[cinemaSnap] = Hash(cinemaSnap);
        settings.ThemeLayout = "Cinema"; settings.ThemeVideoSide = "Right"; settings.ThemeMotionStrength = 100;
        var cinemaAssets = Assets("cinema"); cinemaAssets.Snap = cinemaSnap; var cinema = await renderer.RenderAsync(settings, cinemaAssets);
        var cinemaFrame = await ReadFrame(ffmpegPath, cinema, 2.5, root); var cinemaScreen = ReadScreen(cinema);
        var cinemaSubject = Subject(cinemaFrame, out var subjectPixels);
        var coveredPixels = subjectPixels.Count(index => index % Width >= cinemaScreen.X && index % Width < cinemaScreen.X + cinemaScreen.Width && index / Width >= cinemaScreen.Y && index / Width < cinemaScreen.Y + cinemaScreen.Height);
        Check(cinemaSubject.Count > 300 && coveredPixels == 0, "Cinema foreground does not cover gameplay: subject=" + JsonSerializer.Serialize(cinemaSubject) + ", screen=" + JsonSerializer.Serialize(cinemaScreen) + ", overlapPixels=" + coveredPixels);
        CheckCorners(cinemaFrame, cinemaScreen);
        passed.Add("Cinema's wider 16:9 screen keeps opaque foreground pixels outside gameplay and preserves all source corners");

        // Non-square sample pixels must be expanded into their display shape before composing square-pixel output.
        var anamorphicSnap = Path.Combine(root, "green anamorphic 2x1 SAR.mp4");
        await RunTool(ffmpegPath, ["-hide_banner", "-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", Pattern(320, 240) + ",setsar=2/1", "-t", "4", "-c:v", "libx264", "-threads", "2", "-pix_fmt", "yuv420p", anamorphicSnap], root);
        sourceHashes[anamorphicSnap] = Hash(anamorphicSnap);
        var anamorphicInfo = await renderer.InspectReferenceAsync(settings, anamorphicSnap);
        Check(anamorphicInfo.Width == 320 && anamorphicInfo.Height == 240 && Math.Abs(anamorphicInfo.PixelAspectRatio - 2) < .001, "probe retains the fixture's non-square sample pixels");
        settings.ThemeLayout = "Fanart"; var anamorphicAssets = Assets("anamorphic"); anamorphicAssets.Snap = anamorphicSnap;
        var anamorphic = await renderer.RenderAsync(settings, anamorphicAssets); var anamorphicFrame = await ReadFrame(ffmpegPath, anamorphic, 1.2, root);
        var anamorphicScreen = ReadScreen(anamorphic); var anamorphicPixels = anamorphicFrame.Find(Green);
        Check(Math.Abs(anamorphicScreen.Width / (double)anamorphicScreen.Height - 8d / 3) < .035 && Math.Abs(anamorphicPixels.Width / (double)anamorphicPixels.Height - 8d / 3) < .035,
            "320x240 with SAR2:1 displays as8:3, not coded4:3: screen=" + JsonSerializer.Serialize(anamorphicScreen) + ", pixels=" + JsonSerializer.Serialize(anamorphicPixels));
        Check(Math.Abs(anamorphicPixels.Width - anamorphicScreen.Width) <= 4 && Math.Abs(anamorphicPixels.Height - anamorphicScreen.Height) <= 4, "anamorphic gameplay fills its native-display-aspect panel without padding");
        CheckCorners(anamorphicFrame, anamorphicScreen);
        passed.Add("Non-square 2:1 sample pixels produce an unpadded 8:3 gameplay screen with every source corner visible");
        var outputHash = Hash(right); var provenanceHash = Hash(right + ".alm.json"); settings.ThemeVideoSide = "Left";
        var preserved = await renderer.RenderAsync(settings, rightAssets);
        Check(preserved == right && Hash(right) == outputHash && Hash(right + ".alm.json") == provenanceHash, "new layout settings never overwrite an existing theme or its provenance");
        Check(sourceHashes.All(pair => Hash(pair.Key) == pair.Value), "rendering leaves all source artwork, alpha cutout and gameplay bytes unchanged");
        passed.Add("Existing themes/provenance and every source asset remain unchanged");
        var report = new { Passed = true, Right = right, Left = left, MissingSnapBlocked = true, RightScreen = rightScreen, LeftScreen = leftScreen, FirstCutout = rightSubject, LaterCutout = laterSubject, MotionPixels = Movement(rightSubject, laterSubject), StillMotionPixels = Movement(stillOne, stillTwo), Bounce = motionReport, Cinema = cinema, CinemaScreen = cinemaScreen, CinemaSubject = cinemaSubject, CinemaOverlapPixels = coveredPixels, Anamorphic = anamorphic, AnamorphicScreen = anamorphicScreen };
        await File.WriteAllTextAsync(Path.Combine(root, "pixel-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return passed;
    }

    private static string Pattern(int width, int height) => $"color=c=0x20E040:s={width}x{height}:r=15,drawbox=x=0:y=0:w=20:h=20:color=red:t=fill,drawbox=x={width - 20}:y=0:w=20:h=20:color=red:t=fill,drawbox=x=0:y={height - 20}:w=20:h=20:color=red:t=fill,drawbox=x={width - 20}:y={height - 20}:w=20:h=20:color=red:t=fill";
    private static ThemeScreenBounds ReadScreen(string rendered)
    {
        using var provenance = JsonDocument.Parse(File.ReadAllText(rendered + ".alm.json"));
        return provenance.RootElement.GetProperty("Scene").GetProperty("Screen").Deserialize<ThemeScreenBounds>() ?? throw new Exception("Fanart screen bounds missing");
    }
    private static void CheckCorners(Frame frame, ThemeScreenBounds screen)
    {
        var xOffset = Math.Max(4, (int)(screen.Width * .025)); var yOffset = Math.Max(3, (int)(screen.Height * .025));
        foreach (var x in new[] { screen.X + xOffset, screen.X + screen.Width - 1 - xOffset }) foreach (var y in new[] { screen.Y + yOffset, screen.Y + screen.Height - 1 - yOffset })
        {
            var p = frame.At(x, y); Check(p.R > 160 && p.G < 110 && p.B < 110, $"source corner remains visible at({x},{y}), RGB=({p.R},{p.G},{p.B})");
        }
    }
    private static void CheckScreen(string rendered, Frame frame, PixelBounds screen, string side)
    {
        Check(screen.Count > 12_000 && screen.Width > 180 && screen.Width < Width * .54, "gameplay is a smaller visible screen");
        Check(Math.Abs(screen.Width / (double)screen.Height - 4d / 3) < .035, "decoded gameplay retains the 4:3 source aspect ratio");
        Check(side == "Right" ? screen.CenterX > Width * .58 : screen.CenterX < Width * .42, "gameplay appears on the selected side");
        using var provenance = JsonDocument.Parse(File.ReadAllText(rendered + ".alm.json"));
        var bounds = provenance.RootElement.GetProperty("Scene").GetProperty("Screen");
        var panelWidth = bounds.GetProperty("Width").GetInt32(); var panelHeight = bounds.GetProperty("Height").GetInt32();
        Check(Math.Abs(panelWidth / (double)panelHeight - 4d / 3) < .035 && Math.Abs(screen.Width - panelWidth) <= 4 && Math.Abs(screen.Height - panelHeight) <= 4, "reported panel is filled by native-aspect gameplay rather than black letterbox padding");
        // Red source markers remain at all four visible corners: gameplay was not cropped to force a wider rectangle.
        var offset = Math.Max(5, (int)(screen.Width * .025));
        foreach (var x in new[] { screen.Left + offset, screen.Right - offset }) foreach (var y in new[] { screen.Top + offset, screen.Bottom - offset })
        {
            var pixel = frame.At(x, y); Check(pixel.R > 160 && pixel.G < 110 && pixel.B < 110, "all four source corners survive screen placement");
        }
    }
    private static void CheckSubject(Frame frame, PixelBounds subject, string screenSide)
    {
        Check(subject.Count > 300 && subject.Width < Width * .30 && subject.Height < Height * .83, "cutout is visible at a bounded size: " + JsonSerializer.Serialize(subject));
        Check(subject.Left > 2 && subject.Top > 2 && subject.Right < Width - 3 && subject.Bottom < Height - 3, "subject is not clipped by canvas edges");
        Check(screenSide == "Right" ? subject.CenterX < Width * .46 : subject.CenterX > Width * .54, "cutout stays on the art side of the screen");
        // The opaque center occupies half the fixture's width; these points lie in its transparent margin.
        var x = screenSide == "Right" ? subject.Left - subject.Width / 4 : subject.Right + subject.Width / 4;
        foreach (var y in new[] { (int)subject.CenterY - subject.Height / 5, (int)subject.CenterY + subject.Height / 5 })
        {
            var p = frame.At(x, y); Check(Blue(p.R, p.G, p.B), "transparent subject margins reveal the blue artwork rather than a black/white rectangle");
        }
    }
    private static void CheckAnimatedBorder(Frame first, Frame second, PixelBounds screen)
    {
        var samples = new List<(int X, int Y)>();
        for (var step = 1; step < 8; step++)
        {
            var y = screen.Top + screen.Height * step / 8; samples.Add((screen.Left - 2, y)); samples.Add((screen.Right + 2, y));
        }
        var luminous = 0; double changed = 0;
        foreach (var (x, y) in samples)
        {
            var a = first.At(x, y); var b = second.At(x, y);
            if (Math.Max(a.R, Math.Max(a.G, a.B)) > 170 && a.R + a.G + a.B > 230) luminous++;
            changed += Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
        }
        Check(luminous >= samples.Count / 2 && changed / samples.Count > 15, "luminous border visibly animates in decoded frames; luminous=" + luminous + "/" + samples.Count + ", color change=" + (changed / samples.Count).ToString("0.##", CultureInfo.InvariantCulture));
    }
    private static async Task<Frame> ReadFrame(string ffmpeg, string source, double seconds, string root)
    {
        var pixels = await RunTool(ffmpeg, ["-hide_banner", "-nostdin", "-v", "error", "-ss", seconds.ToString(CultureInfo.InvariantCulture), "-i", source, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"], root);
        Check(pixels.Length == Width * Height * 3, "decoded video frame has the requested dimensions"); return new(pixels);
    }
    private static async Task<byte[]> RunTool(string executable, IEnumerable<string> arguments, string root)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start }; Check(process.Start(), "FFmpeg fixture tool starts");
        using var bytes = new MemoryStream(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var output = process.StandardOutput.BaseStream.CopyToAsync(bytes); var errors = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(); await Task.WhenAll(output, errors); throw new IOException("FFmpeg fixture tool exceeded its timeout.");
        }
        await Task.WhenAll(output, errors); Check(process.ExitCode == 0, "FFmpeg fixture/decode completed: " + await errors); return bytes.ToArray();
    }
}