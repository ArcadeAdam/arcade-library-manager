using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;

namespace ArcadeLibraryManager.Core;

public sealed record MediaTools(string Ffmpeg, string Ffprobe);
public sealed record ThemeReferenceInfo(string Path, int Width, int Height, double Fps, double DurationSeconds, bool HasAudio, string Codec, double PixelAspectRatio = 1);

/// <summary>Creates missing themes using explicit FFmpeg arguments and an app-owned staging journal.</summary>
public sealed class ThemeRenderer
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private const string TemplateVersion = "fanart-4";
    private readonly VolumeHealthService health;
    private readonly ThemeEncoding encodingService;
    public ThemeRenderer(VolumeHealthService? healthService = null, ThemeEncoding? encodingService = null) { health = healthService ?? new VolumeHealthService(); this.encodingService = encodingService ?? ThemeEncoding.Shared; }

    private static readonly ThemeToolsResolver ToolsResolver = new();
    public static MediaTools? FindTools(AppSettings settings) => ToolsResolver.Find(settings);
    public static MediaTools RequireTools(AppSettings settings) => ToolsResolver.Require(settings);
    public static string GetToolsError(AppSettings settings) => ToolsResolver.GetError(settings);

    public async Task<ThemeReferenceInfo> InspectReferenceAsync(AppSettings settings, string path, CancellationToken ct = default)
    {
        var tools = RequireTools(settings);
        return await ProbeAsync(tools, path, ct);
    }

    public static void ApplyReferencePreset(AppSettings settings, ThemeReferenceInfo reference)
    {
        var ratio = reference.Height > 0 ? reference.Width * reference.PixelAspectRatio / reference.Height : 16d / 9;
        // Adopt the reference's display shape, using an editable, efficient output preset.
        settings.VideoHeight = 720;
        settings.VideoWidth = Math.Clamp((int)Math.Round(720 * ratio / 2) * 2, 640, 1920);
        settings.VideoFps = reference.Fps > 45 ? 60 : 30;
        settings.VideoSeconds = ThemeDuration.FromReference(reference.DurationSeconds);
        settings.ThemeAudio = reference.HasAudio;
    }

    public Task<string> RenderAsync(AppSettings settings, MediaAssets assets, IProgress<JobEvent>? progress = null, CancellationToken ct = default)
        => RenderCoreAsync(settings, assets, false, progress, ct);

    public Task<string> RenderPreviewAsync(AppSettings settings, MediaAssets assets, IProgress<JobEvent>? progress = null, CancellationToken ct = default)
        => RenderCoreAsync(settings, assets, true, progress, ct);

    private async Task<string> RenderCoreAsync(AppSettings settings, MediaAssets assets, bool preview, IProgress<JobEvent>? progress, CancellationToken ct)
    {
        if (!preview && !string.IsNullOrWhiteSpace(assets.Theme) && File.Exists(assets.Theme))
        {
            progress?.Report(new("Theme", "Preserved existing theme", assets.ProfileId)); return assets.Theme;
        }
        if (!preview && MediaService.HasReusableTheme(assets))
            return await new ThemeReuseService(health).ReuseAsync(settings, assets, progress, ct);
        if (!preview && string.IsNullOrWhiteSpace(assets.ThemeDestination)) throw new InvalidOperationException("No theme destination was discovered. Select a LaunchBox installation.");
        var cache = string.IsNullOrWhiteSpace(settings.CachePath) ? Path.Combine(AppContext.BaseDirectory, "Data", "Cache") : settings.CachePath;
        var destination = Path.GetFullPath(preview ? Path.Combine(cache, "ThemePreviews", MediaService.SafeFileName(assets.ProfileId, "game") + "-" + Guid.NewGuid().ToString("N") + ".mp4") : assets.ThemeDestination);
        var gate = Gates.GetOrAdd(destination, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        string? staging = null;
        try
        {
            if (!preview && File.Exists(destination))
            {
                progress?.Report(new("Theme", "Preserved existing theme at destination", assets.ProfileId)); return destination;
            }
            if (!MediaService.HasVideoSnap(assets)) throw new InvalidOperationException("A nonempty local video snap is required to create a theme or preview. Add the gameplay snap in LaunchBox, then scan local media again.");
            await health.EnsureWritableAsync([destination, Path.GetDirectoryName(destination)!], ct);
            var tools = RequireTools(settings);
            var background = ExistingFile(assets.Background);
            var logo = ExistingFile(assets.Logo);
            var snap = Path.GetFullPath(assets.Snap);
            var width = Even(Math.Clamp(settings.VideoWidth, 320, 3840));
            var height = Even(Math.Clamp(settings.VideoHeight, 240, 2160));
            var fps = Math.Clamp(settings.VideoFps, 15, 60);
            var duration = ThemeDuration.Normalize(settings.VideoSeconds);
            progress?.Report(new("Theme", "Preparing theme assets", assets.ProfileId, 0));
            var snapInfo = await ProbeAsync(tools, snap, ct);
            if (snapInfo.Width <= 0 || snapInfo.Height <= 0 || snapInfo.DurationSeconds <= 0 || snapInfo.Fps <= 0)
                throw new InvalidDataException("The video snap must contain a playable video stream with a positive duration and frame rate.");
            var useAudio = settings.ThemeAudio && snapInfo.HasAudio;
            var layout = settings.ThemeLayout == "Cinema" ? "Cinema" : "Fanart";
            var backgroundInfo = background.Length > 0 ? await ProbeAsync(tools, background, ct) : null;
            var cutoutSources = assets.Cutouts.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (settings.ThemeAutoCutouts)
                cutoutSources.AddRange(assets.ArtworkSources.Where(File.Exists).Except(cutoutSources,StringComparer.OrdinalIgnoreCase).Take(2));
            var cutoutDirectory = Path.Combine(cache,"ThemeCutouts");
            var cutouts = cutoutSources.Count == 0 ? new List<string>() : (await new ThemeCutoutService(health).PrepareAsync(settings,cutoutSources,cutoutDirectory,progress,ct)).Take(2).ToList();
            if (cutouts.Count == 0 && settings.ThemeAutoCutouts && ThemeCutoutService.FindModel(settings) == null)
                progress?.Report(new("Theme", "No cutout model installed. Using artwork composition; install the model or import transparent cutouts for foreground animation.", assets.ProfileId));
            var encoding = await encodingService.ResolveAsync(tools, progress, ct);
            await health.EnsureWritableAsync([destination, Path.GetDirectoryName(destination)!], ct);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            staging = Path.Combine(Path.GetDirectoryName(destination)!, ".alm-staging", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var renderPath = Path.Combine(staging, "render.mp4");
            var sourceHashes = new Dictionary<string, string>();
            foreach (var source in new[] { background, logo, snap }.Concat(cutouts).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)) sourceHashes[Path.GetFullPath(source)] = await HashAsync(source, ct);
            var fingerprintData = new { TemplateVersion, ProfileId = assets.ProfileId, Title = assets.Title, width, height, fps, duration, layout, useAudio, RenderMode = encoding.RenderMode, settings.ThemeVideoSide, settings.ThemeMotionStrength, Sources = sourceHashes };
            var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(fingerprintData)));
            var journal = Path.Combine(staging, "job.json");
            async Task Journal(string state, string? detail = null) { await health.EnsureWritableAsync([journal], CancellationToken.None); await File.WriteAllTextAsync(journal, JsonSerializer.Serialize(new { App = "ArcadeLibraryManager", Version = TemplateVersion, State = state, Detail = detail, ProfileId = assets.ProfileId, Destination = destination, Fingerprint = fingerprint, UpdatedUtc = DateTimeOffset.UtcNow }, JsonOptions), CancellationToken.None); }
            await Journal("Rendering");
            progress?.Report(new("Theme", "Rendering artwork and gameplay", assets.ProfileId, 0));
            var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "warning", "-y" };
            var input = 0; var bgInput = -1; var snapInput = -1; var logoInput = -1;
            if (background.Length > 0) { bgInput = input++; args.AddRange(["-loop", "1", "-framerate", fps.ToString(CultureInfo.InvariantCulture), "-threads", encoding.DecodeThreads.ToString(CultureInfo.InvariantCulture), "-i", background]); }
            snapInput = input++; args.AddRange(["-stream_loop", "-1", "-threads", encoding.DecodeThreads.ToString(CultureInfo.InvariantCulture), "-i", snap]);
            if (logo.Length > 0) { logoInput = input++; args.AddRange(["-loop", "1", "-framerate", fps.ToString(CultureInfo.InvariantCulture), "-threads", encoding.DecodeThreads.ToString(CultureInfo.InvariantCulture), "-i", logo]); }
            var cutoutInputs = new List<int>();
            foreach (var path in cutouts) { cutoutInputs.Add(input++); args.AddRange(["-loop", "1", "-framerate", fps.ToString(CultureInfo.InvariantCulture), "-threads", encoding.DecodeThreads.ToString(CultureInfo.InvariantCulture), "-i", path]); }
            var scene = ThemeComposition.Build(settings, assets, width, height, fps, duration, bgInput, backgroundInfo, snapInput, snapInfo, logoInput, cutoutInputs, staging);
            var graph = new StringBuilder(scene.Graph);
            var fade = Math.Min(.35, duration / 8d).ToString("0.###", CultureInfo.InvariantCulture);
            var fadeOut = (duration - double.Parse(fade, CultureInfo.InvariantCulture)).ToString("0.###", CultureInfo.InvariantCulture);
            if (useAudio) graph.Append($";[{snapInput}:a:0]aresample=48000,atrim=duration={duration},asetpts=PTS-STARTPTS,volume=0.7,afade=t=in:st=0:d={fade},afade=t=out:st={fadeOut}:d={fade}[outa]");
            args.AddRange(["-filter_complex_threads", encoding.FilterThreads.ToString(CultureInfo.InvariantCulture), "-filter_complex", graph.ToString(), "-map", "[outv]"]);
            if (useAudio) args.AddRange(["-map", "[outa]", "-c:a", "aac", "-b:a", "160k"]); else args.Add("-an");
            args.AddRange(["-t", duration.ToString(CultureInfo.InvariantCulture), "-r", fps.ToString(CultureInfo.InvariantCulture)]);
            var rendering = Stopwatch.StartNew();
            progress?.Report(new("Theme", $"Rendering with {encoding.Encoder}; {encoding.FilterThreads} composition threads. GPU encoding accelerates compression; art composition remains on the CPU.", assets.ProfileId));
            try
            {
                encoding = await encodingService.ExecuteWithFallbackAsync(tools, encoding, async (choice, token) =>
                {
                    var attempt = new List<string>(args); attempt.AddRange(choice.EncoderArguments);
                    attempt.AddRange(["-movflags", "+faststart", "-progress", "pipe:1", renderPath]);
                    await RunAsync(tools.Ffmpeg, attempt, staging, token, line =>
                    {
                        if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && long.TryParse(line.AsSpan(12), out var microseconds))
                            progress?.Report(new("Theme", "Rendering theme", assets.ProfileId, Math.Clamp(microseconds / (duration * 10000d), 0, 99)));
                    });
                }, progress, assets.ProfileId, ct);
                rendering.Stop();
                await Journal("Validating");
                progress?.Report(new("Theme", "Validating theme", assets.ProfileId, 99));
                var info = await ProbeAsync(tools, renderPath, ct);
                if (info.Width != width || info.Height != height || Math.Abs(info.DurationSeconds - duration) > .35 || Math.Abs(info.Fps - fps) > .1 || (useAudio && !info.HasAudio))
                    throw new InvalidDataException("Rendered theme does not match its requested dimensions, frame rate, duration, or audio.");
                await RunAsync(tools.Ffmpeg, ["-hide_banner", "-nostdin", "-v", "error", "-xerror", "-i", renderPath, "-f", "null", "-"], staging, ct);
                var thumbnail = Path.Combine(staging, "thumbnail.jpg");
                await RunAsync(tools.Ffmpeg, ["-hide_banner", "-nostdin", "-v", "error", "-y", "-ss", "1", "-i", renderPath, "-frames:v", "1", "-vf", "scale=640:-2", thumbnail], staging, ct);
                var outputHash = await HashAsync(renderPath, ct);
                ct.ThrowIfCancellationRequested();
                if (File.Exists(destination)) { await Journal("PreservedExisting", "A theme appeared while this render was running."); return destination; }
                await health.EnsureWritableAsync([destination, staging, destination + ".alm.json"], ct);
                // Same-directory staging makes the final move atomic and refuses concurrent overwrite.
                try { File.Move(renderPath, destination, false); }
                catch (IOException) when (File.Exists(destination)) { await Journal("PreservedExisting", "Another render committed first."); return destination; }
                if (File.Exists(thumbnail) && !File.Exists(destination + ".alm.jpg")) File.Move(thumbnail, destination + ".alm.jpg", false);
                var provenance = new { App = "ArcadeLibraryManager", TemplateVersion, ProfileId = assets.ProfileId, Title = assets.Title, Fingerprint = fingerprint, OutputSha256 = outputHash, Encoder = encoding.Encoder, RenderMode = encoding.RenderMode, FilterThreads = encoding.FilterThreads, EncodeThreads = encoding.EncodeThreads, DecodeThreads = encoding.DecodeThreads, RenderingElapsedSeconds = rendering.Elapsed.TotalSeconds, StaticArtworkFallback = false, CreatedUtc = DateTimeOffset.UtcNow, Sources = sourceHashes, Cutouts = cutouts, Scene = new { scene.Screen, scene.BackgroundTreatment, scene.CutoutCount, settings.ThemeVideoSide, settings.ThemeMotionStrength }, Output = new { width, height, fps, duration, useAudio, layout } };
                await File.WriteAllTextAsync(destination + ".alm.json", JsonSerializer.Serialize(provenance, JsonOptions), CancellationToken.None);
                await Journal("Complete");
                progress?.Report(new("Theme", preview ? "Preview ready" : "Missing theme created and verified", assets.ProfileId, 100));
                return destination;
            }
            catch (VolumeHealthException) { throw; }
            catch (OperationCanceledException) { await Journal("Cancelled", "Child process stopped; staging is retained for diagnostics."); throw; }
            catch (Exception ex) { await Journal("Failed", ex.Message); throw; }
        }
        finally { gate.Release(); }
    }

    private static int Even(int value) => value - value % 2;
    private static string ExistingFile(string path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? Path.GetFullPath(path) : "";
    private static string? FindFont()
    {
        var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        return new[] { Path.Combine(fonts, "segoeui.ttf"), Path.Combine(fonts, "arial.ttf"), "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf" }.FirstOrDefault(File.Exists);
    }
    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
    }
    private static async Task<ThemeReferenceInfo> ProbeAsync(MediaTools tools, string path, CancellationToken ct)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Media file not found", path);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(45));
        string result;
        try
        {
            result = await RunAsync(tools.Ffprobe, ["-v", "error", "-show_entries", "stream=codec_type,codec_name,width,height,sample_aspect_ratio,avg_frame_rate,duration:format=duration", "-of", "json", Path.GetFullPath(path)], Path.GetDirectoryName(Path.GetFullPath(path))!, timeout.Token);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Media probe timed out after 45 seconds for '{Path.GetFileName(path)}'.", ex);
        }
        using var json = JsonDocument.Parse(result);
        var streams = json.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.FirstOrDefault(s => s.TryGetProperty("codec_type", out var type) && type.GetString() == "video");
        if (video.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("The file contains no video stream.");
        int Integer(string key) => video.TryGetProperty(key, out var prop) && prop.TryGetInt32(out var number) ? number : 0;
        var fraction = video.TryGetProperty("avg_frame_rate", out var rate) ? rate.GetString()?.Split('/') : null;
        double fps = fraction?.Length == 2 && double.TryParse(fraction[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) && double.TryParse(fraction[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) && denominator > 0 ? numerator / denominator : 0;
        string? durationValue = null;
        if (json.RootElement.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var duration)) durationValue = duration.GetString();
        else if (video.TryGetProperty("duration", out duration)) durationValue = duration.GetString();
        double.TryParse(durationValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds);
        var sarParts = video.TryGetProperty("sample_aspect_ratio", out var sarProperty) ? sarProperty.GetString()?.Split(':') : null;
        var sar = sarParts?.Length == 2 && double.TryParse(sarParts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var sarN) && double.TryParse(sarParts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var sarD) && sarN > 0 && sarD > 0 ? sarN / sarD : 1;
        return new(Path.GetFullPath(path), Integer("width"), Integer("height"), fps, seconds, streams.Any(s => s.TryGetProperty("codec_type", out var type) && type.GetString() == "audio"), video.TryGetProperty("codec_name", out var codec) ? codec.GetString() ?? "" : "", sar);
    }

    internal static async Task<string> RunAsync(string executable, IEnumerable<string> arguments, string directory, CancellationToken ct, Action<string>? progress = null)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new IOException("Could not start media tool.");
        var output = new StringBuilder(); var errors = new StringBuilder();
        async Task ReadLines(StreamReader reader, StringBuilder buffer, Action<string>? callback)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (buffer.Length > 48_000) buffer.Remove(0, 24_000);
                buffer.AppendLine(line); callback?.Invoke(line);
            }
        }
        var stdout = ReadLines(process.StandardOutput, output, progress);
        var stderr = ReadLines(process.StandardError, errors, null);
        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None); await Task.WhenAll(stdout, stderr); throw;
        }
        await Task.WhenAll(stdout, stderr);
        if (process.ExitCode != 0) throw new IOException($"{Path.GetFileName(executable)} failed ({process.ExitCode}): {errors.ToString().Trim()}");
        return output.ToString();
    }
}



