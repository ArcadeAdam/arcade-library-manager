using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ArcadeLibraryManager.Core;

/// <summary>Local, sequential subject extraction. Model installation is an explicit separate action.</summary>
public sealed class ThemeCutoutService
{
    private readonly VolumeHealthService health;
    public ThemeCutoutService(VolumeHealthService? healthService = null) => health = healthService ?? new VolumeHealthService();
    public const string ModelFileName = "isnet-general-use.onnx";
    public const string ModelDownloadUrl = "https://github.com/danielgatis/rembg/releases/download/v0.0.0/isnet-general-use.onnx";
    public const string ModelSha256 = "60920e99c45464f2ba57bee2ad08c919a52bbf852739e96947fbb4358c0d964a";
    public const long ModelSize = 178648008;
    public const string AnimeModelFileName = "isnet-anime.onnx";
    public const string AnimeModelSha256 = "f15622d853e8260172812b657053460e20806f04b9e05147d49af7bed31a6e99";
    public const long AnimeModelSize = 176069933;
    private const string CacheVersion = "isnet-cutouts-4";
    private static readonly SemaphoreSlim InferenceGate = new(1, 1);
    private static readonly SemaphoreSlim InstallGate = new(1, 1);
    private static readonly ConcurrentDictionary<string, string> VerifiedModels = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    public static string? FindModel(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ThemeCutoutModelPath))
        {
            var path = Directory.Exists(settings.ThemeCutoutModelPath) ? Path.Combine(settings.ThemeCutoutModelPath, ModelFileName) : settings.ThemeCutoutModelPath;
            return File.Exists(path) ? Path.GetFullPath(path) : null;
        }
        string[] candidates = [Path.Combine(AppContext.BaseDirectory, "Tools", "Models", ModelFileName), Path.Combine(CacheDirectory(settings), "Models", ModelFileName)];
        return candidates.FirstOrDefault(File.Exists);
    }

    public static string GetModelStatus(AppSettings settings)
    {
        var path = FindModel(settings);
        if (path is null) return "Automatic cutout models are not installed (about 340 MiB download). Imported transparent PNGs still work.";
        if (new FileInfo(path).Length != ModelSize) return "Selected model has an unexpected size. Select isnet-general-use.onnx or install the supported model pack.";
        var anime = Path.Combine(Path.GetDirectoryName(path)!, AnimeModelFileName);
        return File.Exists(anime) && new FileInfo(anime).Length == AnimeModelSize
            ? "General and illustrated-character models found; checksums are verified before use. Cutouts run locally on the CPU."
            : "General model found. Download the model pack to add illustrated-character extraction (about 170 MiB more).";
    }

    public static async Task<string> InstallModelAsync(AppSettings settings, IProgress<JobEvent>? progress = null, CancellationToken ct = default)
    {
        await InstallGate.WaitAsync(ct);
        try
        {
            var directory = Path.Combine(CacheDirectory(settings), "Models");
            await new VolumeHealthService().EnsureWritableAsync([directory], ct);
            Directory.CreateDirectory(directory);
            progress?.Report(new("Cutouts", "Installing general and illustrated-character models (about 340 MiB total). Artwork is never uploaded."));
            var general = await InstallOneAsync(directory, ModelFileName, ModelSize, progress, ct);
            await InstallOneAsync(directory, AnimeModelFileName, AnimeModelSize, progress, ct);
            progress?.Report(new("Cutouts", "Both cutout models are installed and SHA-256 verified. Ready for local automatic extraction.", Percent: 100));
            return general;
        }
        finally { InstallGate.Release(); }
    }

    private static async Task<string> InstallOneAsync(string directory, string name, long size, IProgress<JobEvent>? progress, CancellationToken ct)
    {
        var destination = Path.Combine(directory, name);
        if (File.Exists(destination))
        {
            try { await ValidateModelAsync(destination, ct); return destination; }
            catch (InvalidDataException) { progress?.Report(new("Cutouts", $"Replacing the damaged {name} with a checksum-verified download.")); }
        }
        var staging = destination + "." + Guid.NewGuid().ToString("N") + ".download";
        try
        {
            var url = "https://github.com/danielgatis/rembg/releases/download/v0.0.0/" + name;
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous))
            {
                byte[] buffer = new byte[131072]; long written = 0; var report = Stopwatch.StartNew();
                for (int read; (read = await input.ReadAsync(buffer, ct)) != 0;)
                {
                    written += read;
                    if (written > size) throw new InvalidDataException("Model download exceeded its expected size.");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    if (report.ElapsedMilliseconds >= 500) { progress?.Report(new("Cutouts", $"Downloading {name}: {written / 1048576d:F0} / {size / 1048576d:F0} MiB", Percent: written * 100d / size)); report.Restart(); }
                }
                await output.FlushAsync(ct);
            }
            await ValidateModelAsync(staging, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(staging, destination, true);
            return destination;
        }
        finally { if (File.Exists(staging)) try { File.Delete(staging); } catch { } }
    }

    public async Task<IReadOnlyList<string>> PrepareAsync(AppSettings settings, IEnumerable<string> sourcePaths, string outputDirectory, IProgress<JobEvent>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var tools = ThemeRenderer.RequireTools(settings);
        var sources = sourcePaths.Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
        if (sources.Count == 0) return [];
        await health.EnsureWritableAsync([outputDirectory], ct);
        Directory.CreateDirectory(outputDirectory);
        var model = settings.ThemeAutoCutouts ? FindModel(settings) : null;
        if (settings.ThemeAutoCutouts && model is null) progress?.Report(new("Cutouts", "Automatic subject model is not installed. Using imported transparent art; install IS-Net in Make themes for automatic extraction."));
        await InferenceGate.WaitAsync(ct);
        InferenceSession? session = null;
        InferenceSession? animeSession = null;
        var animeModel = model is null ? null : Path.Combine(Path.GetDirectoryName(model)!, AnimeModelFileName);
        if (animeModel is not null && !File.Exists(animeModel)) animeModel = null;
        var results = new List<string>();
        try
        {
            if (model is not null) await ValidateModelAsync(model, ct);
            if (animeModel is not null) await ValidateModelAsync(animeModel, ct);
            foreach (var source in sources)
            {
                ct.ThrowIfCancellationRequested();
                if (results.Count >= 4) break;
                try
                {
                    await using var sourceFile = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous);
                    var sourceHash = Convert.ToHexString(await SHA256.HashDataAsync(sourceFile, ct));
                    var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CacheVersion + sourceHash + (model is null ? "alpha-only" : ModelSha256 + (animeModel is null ? "" : AnimeModelSha256))))).ToLowerInvariant();
                    var manifest = Path.Combine(outputDirectory, key + ".json");
                    if (File.Exists(manifest))
                    {
                        var cached = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(manifest, ct));
                        if (cached is not null && cached.All(p => p == Path.GetFileName(p) && p.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(outputDirectory, p))))
                        { results.AddRange(cached.Select(p => Path.Combine(outputDirectory, p))); continue; }
                    }
                    var frame = await DecodeAsync(tools, source, ct);
                    var transparent = UsableAlpha(frame.Pixels, frame.Width, frame.Height);
                    if (!transparent && model is null) continue;
                    var mode = transparent ? "Trimming transparent art" : "Extracting foreground locally";
                    progress?.Report(new("Cutouts", $"{mode}: {Path.GetFileName(source)}"));
                    if (!transparent)
                    {
                        session ??= await Task.Run(() => CreateSession(model!), ct);
                        var mask = await InferAsync(session, tools, source, new FileInfo(model!).Length == AnimeModelSize, ct);
                        ApplyMask(frame, mask);
                    }
                    var cutouts = ExtractSubjects(frame, transparent);
                    if (!transparent && cutouts.Count == 0 && animeModel is not null)
                    {
                        progress?.Report(new("Cutouts", $"Trying illustrated-character model: {Path.GetFileName(source)}"));
                        animeSession ??= await Task.Run(() => CreateSession(animeModel), ct);
                        frame = await DecodeAsync(tools, source, ct);
                        ApplyMask(frame, await InferAsync(animeSession, tools, source, true, ct));
                        cutouts = ExtractSubjects(frame, false);
                    }
                    var names = new List<string>();
                    foreach (var cutout in cutouts.Take(4 - results.Count))
                    {
                        var name = key + "-" + names.Count + ".png";
                        var path = Path.Combine(outputDirectory, name);
                        await EncodeAsync(tools, cutout, path, ct);
                        names.Add(name); results.Add(path);
                    }
                    var temp = manifest + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try { await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(names), ct); File.Move(temp, manifest, true); }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                    if (cutouts.Count == 0) progress?.Report(new("Cutouts", $"No clean foreground found in {Path.GetFileName(source)}. Skipped the mask; add a transparent PNG for this game."));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or OnnxRuntimeException or InvalidOperationException)
                { progress?.Report(new("Cutouts", $"Could not prepare {Path.GetFileName(source)}: {ex.Message}")); }
            }
            return results.Take(4).ToArray();
        }
        finally { session?.Dispose(); animeSession?.Dispose(); InferenceGate.Release(); }
    }

    private static string CacheDirectory(AppSettings settings) => Path.GetFullPath(string.IsNullOrWhiteSpace(settings.CachePath) ? Path.Combine(AppContext.BaseDirectory, "Data", "Cache") : settings.CachePath);

    private static async Task ValidateModelAsync(string path, CancellationToken ct)
    {
        var info = new FileInfo(path);
        var anime = info.Length == AnimeModelSize;
        if (info.Length != ModelSize && !anime) throw new InvalidDataException("Only the supported IS-Net general-use ONNX model is accepted. Install it from Make themes.");
        var key = info.Length + ":" + info.LastWriteTimeUtc.Ticks;
        if (VerifiedModels.TryGetValue(info.FullName, out var cached) && cached == key) return;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
        if (!hash.Equals(anime ? AnimeModelSha256 : ModelSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("IS-Net model checksum did not match. The model was not loaded.");
        VerifiedModels[info.FullName] = key;
    }

    private static InferenceSession CreateSession(string path)
    {
        try
        {
            using var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL, InterOpNumThreads = 1, IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4), EnableCpuMemArena = false };
            options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
            return new InferenceSession(path, options);
        }
        catch (Exception ex) when (ex.GetBaseException() is DllNotFoundException or BadImageFormatException)
        {
            throw new InvalidOperationException("Automatic cutouts could not load the Windows inference runtime. Install Microsoft's Visual C++ v14 Redistributable (x64), then restart the app. Imported transparent PNGs still work. See docs/CUTOUT-MODELS.md.", ex);
        }
    }

    private static async Task<float[]> InferAsync(InferenceSession session, MediaTools tools, string source, bool anime, CancellationToken ct)
    {
        const int size = 1024;
        var rgb = await RunAsync(tools.Ffmpeg, ["-hide_banner", "-loglevel", "error", "-i", source, "-vf", "scale=1024:1024:flags=lanczos", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"], null, size * size * 3, ct);
        if (rgb.Length != size * size * 3) throw new InvalidDataException("Could not decode artwork for the cutout model.");
        var pixels = size * size; var data = new float[pixels * 3]; var maximum = Math.Max(1, (int)rgb.Max());
        for (int i = 0; i < pixels; i++) { data[i] = rgb[i * 3] / (float)maximum - (anime ? .485f : .5f); data[pixels + i] = rgb[i * 3 + 1] / (float)maximum - (anime ? .456f : .5f); data[2 * pixels + i] = rgb[i * 3 + 2] / (float)maximum - (anime ? .406f : .5f); }
        using var options = new RunOptions();
        using var registration = ct.Register(() => options.Terminate = true);
        try
        {
            var tensor = new DenseTensor<float>(data, [1, 3, size, size]);
            using var outputs = await Task.Run(() => session.Run([NamedOnnxValue.CreateFromTensor(session.InputMetadata.Keys.First(), tensor)], [session.OutputMetadata.Keys.First()], options), ct);
            var mask = outputs.First().AsTensor<float>().ToArray();
            if (mask.Length != pixels) throw new InvalidDataException("The cutout model returned an unexpected mask size.");
            var min = mask.Min(); var max = mask.Max();
            if (!float.IsFinite(min) || !float.IsFinite(max) || max - min < .00001f) return new float[pixels];
            for (int i = 0; i < mask.Length; i++) mask[i] = Math.Clamp((mask[i] - min) / (max - min), 0, 1);
            return mask;
        }
        catch (OnnxRuntimeException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
    }

    private sealed record Frame(int Width, int Height, byte[] Pixels);
    private static async Task<Frame> DecodeAsync(MediaTools tools, string source, CancellationToken ct)
    {
        var probe = await RunAsync(tools.Ffprobe, ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "json", source], null, 65536, ct);
        using var json = JsonDocument.Parse(probe); var stream = json.RootElement.GetProperty("streams")[0];
        var width = stream.GetProperty("width").GetInt32(); var height = stream.GetProperty("height").GetInt32();
        if (width < 16 || height < 16 || width > 50000 || height > 50000) throw new InvalidDataException("Artwork dimensions are not suitable for a cutout.");
        double scale = Math.Min(1, 1800d / Math.Max(width, height)); width = Math.Max(16, (int)Math.Round(width * scale)); height = Math.Max(16, (int)Math.Round(height * scale));
        var bytes = await RunAsync(tools.Ffmpeg, ["-hide_banner", "-loglevel", "error", "-i", source, "-vf", $"scale={width}:{height}:flags=lanczos", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgba", "pipe:1"], null, width * height * 4, ct);
        if (bytes.Length != width * height * 4) throw new InvalidDataException("Artwork did not decode to a complete RGBA frame.");
        return new(width, height, bytes);
    }

    private static bool UsableAlpha(byte[] rgba, int width, int height)
    {
        int clear = 0, solid = 0;
        for (int i = 3; i < rgba.Length; i += 4) { if (rgba[i] < 32) clear++; if (rgba[i] > 128) solid++; }
        return clear > width * height * .025 && solid > width * height * .005;
    }

    private static void ApplyMask(Frame frame, float[] mask)
    {
        for (int y = 0; y < frame.Height; y++)
        {
            var my = y * 1023d / Math.Max(1, frame.Height - 1); int y0 = (int)my, y1 = Math.Min(1023, y0 + 1); float dy = (float)(my - y0);
            for (int x = 0; x < frame.Width; x++)
            {
                var mx = x * 1023d / Math.Max(1, frame.Width - 1); int x0 = (int)mx, x1 = Math.Min(1023, x0 + 1); float dx = (float)(mx - x0);
                var a = (mask[y0 * 1024 + x0] * (1 - dx) + mask[y0 * 1024 + x1] * dx) * (1 - dy) + (mask[y1 * 1024 + x0] * (1 - dx) + mask[y1 * 1024 + x1] * dx) * dy;
                // Small confidence cleanup keeps soft edges, suppresses low-confidence background haze.
                a = Math.Clamp((a - .12f) / .78f, 0, 1);
                int offset = (y * frame.Width + x) * 4 + 3; frame.Pixels[offset] = (byte)Math.Round(frame.Pixels[offset] * a);
            }
        }
    }

    private static List<Frame> ExtractSubjects(Frame frame, bool originalAlpha)
    {
        int width = frame.Width, height = frame.Height, total = width * height;
        int solid = 0; for (int i = 3; i < frame.Pixels.Length; i += 4) if (frame.Pixels[i] > 128) solid++;
        if (solid < total * .008 || (!originalAlpha && solid > total * .78)) return [];
        // Imported alpha already represents the artist's composition; trim it without separating letters or parts.
        if (originalAlpha) return [Crop(frame, Enumerable.Range(0, total).Where(i => frame.Pixels[i * 4 + 3] > 12).ToArray())];
        int[] labels = new int[total]; int[] queue = new int[total]; var groups = new List<List<int>>();
        for (int start = 0; start < total; start++)
        {
            if (labels[start] != 0 || frame.Pixels[start * 4 + 3] < 96) continue;
            int label = groups.Count + 1, head = 0, tail = 0; labels[start] = label; queue[tail++] = start; var pixels = new List<int>();
            while (head < tail)
            {
                int i = queue[head++]; pixels.Add(i); int x = i % width, y = i / width;
                void Visit(int n) { if (n >= 0 && labels[n] == 0 && frame.Pixels[n * 4 + 3] >= 96) { labels[n] = label; queue[tail++] = n; } }
                Visit(x > 0 ? i - 1 : -1); Visit(x + 1 < width ? i + 1 : -1); Visit(y > 0 ? i - width : -1); Visit(y + 1 < height ? i + width : -1);
            }
            groups.Add(pixels);
        }
        var chosen = groups.Where(g => g.Count >= Math.Max(300, total * .012)).OrderByDescending(g => g.Count).Take(3).ToList();
        var result = new List<Frame>();
        foreach (var group in chosen)
        {
            int left = group.Min(i => i % width), right = group.Max(i => i % width), top = group.Min(i => i / width), bottom = group.Max(i => i / width);
            int box = (right - left + 1) * (bottom - top + 1);
            if (right - left < 40 || bottom - top < 40 || group.Count < chosen[0].Count * .12) continue;
            // A model selecting a flyer panel or the entire background is not a usable subject.
            int edges = (left < width * .02 ? 1 : 0) + (right > width * .98 ? 1 : 0) + (top < height * .02 ? 1 : 0) + (bottom > height * .98 ? 1 : 0);
            if (group.Count / (double)box > .9 || (edges >= 3 && box > total * .4) || (right - left > width * .93 && bottom - top > height * .7)) continue;
            var isolated = (byte[])frame.Pixels.Clone(); int label = labels[group[0]];
            for (int i = 0; i < total; i++) if (labels[i] != label) isolated[i * 4 + 3] = 0;
            result.Add(Crop(new(width, height, isolated), group));
        }
        return result;
    }

    private static Frame Crop(Frame frame, IReadOnlyCollection<int> pixels)
    {
        if (pixels.Count == 0) return new(1, 1, new byte[4]);
        int left = Math.Max(0, pixels.Min(i => i % frame.Width) - 3), top = Math.Max(0, pixels.Min(i => i / frame.Width) - 3);
        int right = Math.Min(frame.Width - 1, pixels.Max(i => i % frame.Width) + 3), bottom = Math.Min(frame.Height - 1, pixels.Max(i => i / frame.Width) + 3);
        int width = right - left + 1, height = bottom - top + 1; byte[] output = new byte[width * height * 4];
        for (int y = 0; y < height; y++) Buffer.BlockCopy(frame.Pixels, ((top + y) * frame.Width + left) * 4, output, y * width * 4, width * 4);
        return new(width, height, output);
    }

    private static async Task EncodeAsync(MediaTools tools, Frame frame, string destination, CancellationToken ct)
    {
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await RunAsync(tools.Ffmpeg, ["-hide_banner", "-loglevel", "error", "-f", "rawvideo", "-pix_fmt", "rgba", "-s", $"{frame.Width}x{frame.Height}", "-i", "pipe:0", "-frames:v", "1", "-c:v", "png", "-f", "image2", "-y", temp], frame.Pixels, 65536, ct);
            ct.ThrowIfCancellationRequested(); File.Move(temp, destination, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static async Task<byte[]> RunAsync(string executable, IEnumerable<string> args, byte[]? input, int maxBytes, CancellationToken ct)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input is not null };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Could not start image processing.");
        using var cancellation = ct.Register(() => { try { process.Kill(true); } catch { } });
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        async Task WriteInput() { if (input is not null) { await process.StandardInput.BaseStream.WriteAsync(input, ct); process.StandardInput.Close(); } }
        var writeTask = WriteInput();
        using var output = new MemoryStream(); byte[] buffer = new byte[65536];
        try
        {
            for (int read; (read = await process.StandardOutput.BaseStream.ReadAsync(buffer, ct)) != 0;)
            { if (output.Length + read > maxBytes) throw new InvalidDataException("Image processing exceeded the expected output size."); await output.WriteAsync(buffer.AsMemory(0, read), ct); }
            await writeTask; await process.WaitForExitAsync(ct); var error = await errorTask;
            if (process.ExitCode != 0) throw new InvalidDataException("Image processing failed: " + error.Trim());
            return output.ToArray();
        }
        finally { if (!process.HasExited) try { process.Kill(true); } catch { } }
    }

    /// <summary>Fast conservative check for real transparency in ordinary non-interlaced 8-bit PNG assets.</summary>
    public static bool HasUsableTransparency(string path)
    {
        try
        {
            using var file = File.OpenRead(path); using var reader = new BinaryReader(file);
            if (!reader.ReadBytes(8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10})) return false;
            int width = 0, height = 0, type = 0, channels = 0; byte[]? paletteAlpha = null; using var compressed = new MemoryStream();
            while (file.Position + 12 <= file.Length)
            {
                int length = BinaryPrimitives.ReadInt32BigEndian(reader.ReadBytes(4)); if (length < 0 || length > 64 * 1024 * 1024 || file.Position + 8L + length > file.Length) return false;
                var kind = Encoding.ASCII.GetString(reader.ReadBytes(4)); var bytes = reader.ReadBytes(length); reader.ReadUInt32();
                if (kind == "IHDR")
                { width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(0,4)); height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(4,4)); type = bytes[9]; channels = type == 6 ? 4 : type == 4 ? 2 : type == 3 ? 1 : 0; if (bytes[8] != 8 || bytes[12] != 0 || channels == 0 || width < 1 || height < 1 || (long)width * height > 16000000) return false; }
                if (kind == "tRNS") paletteAlpha = bytes;
                if (kind == "IDAT") { if (compressed.Length + bytes.Length > 64 * 1024 * 1024) return false; compressed.Write(bytes); }
                if (kind == "IEND") break;
            }
            if (channels == 0 || (type == 3 && paletteAlpha is null)) return false;
            compressed.Position = 0; using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
            byte[] previous = new byte[width * channels], row = new byte[width * channels]; long clear = 0, solid = 0;
            for (int y = 0; y < height; y++)
            {
                int filter = zlib.ReadByte(); zlib.ReadExactly(row);
                for (int i = 0; i < row.Length; i++)
                { int a = i >= channels ? row[i - channels] : 0, b = previous[i], c = i >= channels ? previous[i - channels] : 0; row[i] = unchecked((byte)(row[i] + (filter switch {0 => 0, 1 => a, 2 => b, 3 => (a+b)/2, 4 => Paeth(a,b,c), _ => throw new InvalidDataException()}))); }
                for (int x = 0; x < width; x++) { int value = type == 3 ? (row[x] < paletteAlpha!.Length ? paletteAlpha[row[x]] : 255) : row[x * channels + channels - 1]; if (value < 32) clear++; if (value > 128) solid++; }
                (previous, row) = (row, previous);
            }
            return clear > width * (double)height * .025 && solid > width * (double)height * .005;
        }
        catch { return false; }
    }
    private static int Paeth(int a, int b, int c) { int p = a + b - c, pa = Math.Abs(p-a), pb = Math.Abs(p-b), pc = Math.Abs(p-c); return pa <= pb && pa <= pc ? a : pb <= pc ? b : c; }
}
