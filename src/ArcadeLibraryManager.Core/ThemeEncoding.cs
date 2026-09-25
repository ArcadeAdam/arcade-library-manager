using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace ArcadeLibraryManager.Core;

public sealed record ThemeEncodingPlan(string Encoder, string RenderMode, int FilterThreads, int DecodeThreads, int EncodeThreads, IReadOnlyList<string> EncoderArguments)
{
    public bool IsHardware => Encoder != "libx264";
}

/// <summary>Checks real encoder initialization before selecting hardware, and retries a failed hardware initialization once on the CPU.</summary>
public sealed class ThemeEncoding
{
    public static ThemeEncoding Shared { get; } = new();
    private const int MaximumCacheEntries = 32;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(15);
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task<bool>> probe;
    private readonly SemaphoreSlim probeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, (string Encoder, DateTimeOffset Checked)> cache = new(StringComparer.OrdinalIgnoreCase);

    public ThemeEncoding(Func<string, IReadOnlyList<string>, CancellationToken, Task<bool>>? probe = null) => this.probe = probe ?? ProbeAsync;

    public static ThemeEncodingPlan Cpu(int? processorCount = null)
    {
        var processors = Math.Max(1, processorCount ?? Environment.ProcessorCount);
        var encodeThreads = Math.Clamp(processors, 1, 32);
        return new("libx264", "Fast", Math.Clamp(processors, 1, 8), Math.Clamp(processors / 4, 1, 4), encodeThreads,
            ["-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-threads", encodeThreads.ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p"]);
    }

    private static ThemeEncodingPlan Hardware(string encoder)
    {
        var cpu = Cpu();
        IReadOnlyList<string> args = encoder switch
        {
            "h264_nvenc" => ["-c:v", encoder, "-preset", "p4", "-rc", "vbr", "-cq", "20", "-b:v", "0", "-pix_fmt", "yuv420p"],
            "h264_qsv" => ["-c:v", encoder, "-preset", "medium", "-global_quality", "20", "-look_ahead", "0", "-pix_fmt", "nv12"],
            "h264_amf" => ["-c:v", encoder, "-usage", "transcoding", "-quality", "balanced", "-rc", "cqp", "-qp_i", "20", "-qp_p", "20", "-qp_b", "20", "-pix_fmt", "yuv420p"],
            _ => throw new ArgumentOutOfRangeException(nameof(encoder))
        };
        return cpu with { Encoder = encoder, EncodeThreads = 0, EncoderArguments = args };
    }

    public async Task<ThemeEncodingPlan> ResolveAsync(MediaTools tools, IProgress<JobEvent>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var key = CacheKey(tools.Ffmpeg);
        if (TryCached(key, out var known)) return Plan(known);
        await probeGate.WaitAsync(ct);
        try
        {
            if (TryCached(key, out known)) return Plan(known);
            progress?.Report(new("Theme", "Checking working GPU video encoders…"));
            foreach (var encoder in new[] { "h264_nvenc", "h264_qsv", "h264_amf" })
            {
                ct.ThrowIfCancellationRequested();
                var selected = Hardware(encoder);
                var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=black:s=160x96:r=30", "-frames:v", "4", "-an" };
                args.AddRange(selected.EncoderArguments); args.AddRange(["-f", "null", "-"]);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(8));
                bool available;
                try { available = await probe(tools.Ffmpeg, args, timeout.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { available = false; }
                ct.ThrowIfCancellationRequested();
                if (!available) continue;
                Remember(key, encoder);
                return selected;
            }
            Remember(key, "libx264");
            return Cpu();
        }
        finally { probeGate.Release(); }
    }

    public async Task<ThemeEncodingPlan> ExecuteWithFallbackAsync(MediaTools tools, ThemeEncodingPlan initial, Func<ThemeEncodingPlan, CancellationToken, Task> render,
        IProgress<JobEvent>? progress = null, string gameId = "", CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try { await render(initial, ct); return initial; }
        catch (IOException ex) when (initial.IsHardware && !ct.IsCancellationRequested && IsHardwareInitializationFailure(ex.Message, initial.Encoder))
        {
            var cpu = Cpu();
            Remember(CacheKey(tools.Ffmpeg), cpu.Encoder);
            progress?.Report(new("Theme", $"{initial.Encoder} could not initialize for this theme. Retrying once with CPU encoding ({cpu.EncodeThreads} threads).", gameId));
            ct.ThrowIfCancellationRequested();
            await render(cpu, ct);
            return cpu;
        }
    }

    public static bool IsHardwareInitializationFailure(string message, string encoder)
    {
        var hardwareContext = message.Contains(encoder, StringComparison.OrdinalIgnoreCase) ||
            new[] { "nvenc", "CUDA", "MFX session", "AMF", "Quick Sync" }.Any(x => message.Contains(x, StringComparison.OrdinalIgnoreCase));
        if (!hardwareContext) return false;
        return new[] { "Cannot load", "No capable devices", "OpenEncodeSessionEx failed", "InitializeEncoder failed", "Driver does not support", "Error while opening encoder", "Error initializing output stream", "Error initializing an internal MFX session", "Error creating a MFX session", "Failed to initialise", "Failed to initialize", "Failed to create", "unsupported device", "No device available" }
            .Any(x => message.Contains(x, StringComparison.OrdinalIgnoreCase));
    }

    private bool TryCached(string key, out string encoder)
    {
        if (cache.TryGetValue(key, out var entry) && DateTimeOffset.UtcNow - entry.Checked < CacheLifetime) { encoder = entry.Encoder; return true; }
        cache.TryRemove(key, out _); encoder = ""; return false;
    }
    private void Remember(string key, string encoder)
    {
        cache[key] = (encoder, DateTimeOffset.UtcNow);
        foreach (var stale in cache.OrderBy(x => x.Value.Checked).Take(Math.Max(0, cache.Count - MaximumCacheEntries))) cache.TryRemove(stale.Key, out _);
    }
    private static ThemeEncodingPlan Plan(string encoder) => encoder == "libx264" ? Cpu() : Hardware(encoder);
    private static string CacheKey(string executable)
    {
        var info = new FileInfo(Path.GetFullPath(executable));
        return info.FullName + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
    }
    private static async Task<bool> ProbeAsync(string executable, IReadOnlyList<string> args, CancellationToken ct)
    {
        try { await ThemeRenderer.RunAsync(executable, args, Path.GetDirectoryName(Path.GetFullPath(executable))!, ct); return true; }
        catch (IOException) { return false; }
    }
}
