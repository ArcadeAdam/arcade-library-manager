using ArcadeLibraryManager.Core;

public static class ThemeEncodingChecks
{
    public static async Task<List<string>> RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        var passed = new List<string>();
        await SelectionChecks(Path.Combine(root, "selection"));
        passed.Add("Automatic encoder selection tries real probe arguments in GPU priority order and caches results per FFmpeg binary");
        await ProbeCancellationChecks(Path.Combine(root, "probe-cancellation"));
        passed.Add("Cancellation interrupts GPU probing without caching an unavailable result or poisoning later selection");
        await FallbackChecks(Path.Combine(root, "fallback"));
        passed.Add("A failed hardware encode retries CPU once, records the actual choice and avoids repeating the failed GPU");
        await NonFallbackChecks(Path.Combine(root, "no-fallback"));
        passed.Add("Cancellation, storage and filter errors propagate without a CPU retry or a false GPU downgrade");
        await PreservationChecks(Path.Combine(root, "preservation"));
        passed.Add("Existing themes and destination collisions bypass encoder probing and preserve source/output files");
        ThreadBudgetChecks();
        passed.Add("Automatic CPU thread budgets remain positive and bounded on single-core and large machines");
        return passed;
    }

    private static async Task SelectionChecks(string root)
    {
        var tools = FixtureTools(root); var calls = new List<string>();
        var selector = new ThemeEncoding((path, args, ct) =>
        {
            Require(path == tools.Ffmpeg, "The configured FFmpeg binary must be probed.");
            ct.ThrowIfCancellationRequested();
            var codec = Encoder(args); calls.Add(codec);
            Require(args.Contains("-i") && args.Contains("-frames:v") && args.Contains("-pix_fmt") && !args.Contains("-encoders"),
                "Capability detection must attempt a bounded video encode, not merely list compiled encoders.");
            return Task.FromResult(codec == "h264_qsv");
        });
        var first = await selector.ResolveAsync(tools);
        Require(first.IsHardware && first.Encoder == "h264_qsv", "QSV should be selected when NVENC fails and QSV encodes successfully.");
        Require(calls.SequenceEqual(new[] { "h264_nvenc", "h264_qsv" }), "GPU choices must honor priority and stop at the first working encoder.");
        var repeated = await selector.ResolveAsync(tools);
        Require(repeated.Encoder == first.Encoder && calls.Count == 2, "A successful capability result should be reused for the same binary.");
        await File.AppendAllTextAsync(tools.Ffmpeg, "changed binary");
        await selector.ResolveAsync(tools);
        Require(calls.Count == 4, "A replaced FFmpeg binary must not reuse its earlier capability result.");

        var cpuTools = FixtureTools(Path.Combine(root, "no-working-gpu")); var unavailable = new List<string>();
        var cpuSelector = new ThemeEncoding((_, args, _) => { unavailable.Add(Encoder(args)); return Task.FromResult(false); });
        var cpu = await cpuSelector.ResolveAsync(cpuTools);
        Require(!cpu.IsHardware && cpu.Encoder == "libx264", "No working hardware encoder must choose the CPU encoder.");
        Require(unavailable.SequenceEqual(new[] { "h264_nvenc", "h264_qsv", "h264_amf" }), "An unavailable machine must check each supported GPU encoder before CPU fallback.");
        await cpuSelector.ResolveAsync(cpuTools);
        Require(unavailable.Count == 3, "Unavailable GPU results should not be probed again for every theme.");

        var preferredTools = FixtureTools(Path.Combine(root, "preferred-gpu")); var preferredCalls = 0;
        var preferred = new ThemeEncoding((_, _, _) => { preferredCalls++; return Task.FromResult(true); });
        var nvenc = await preferred.ResolveAsync(preferredTools);
        Require(nvenc.IsHardware && nvenc.Encoder == "h264_nvenc" && preferredCalls == 1, "A working NVENC encoder should avoid probing slower-priority alternatives.");
    }

    private static async Task ProbeCancellationChecks(string root)
    {
        var tools = FixtureTools(root); var calls = 0; using var cancellation = new CancellationTokenSource();
        var selector = new ThemeEncoding((_, _, ct) =>
        {
            calls++;
            if (calls == 1) { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); }
            return Task.FromResult(true);
        });
        await Throws<OperationCanceledException>(() => selector.ResolveAsync(tools, ct: cancellation.Token), "Cancellation during capability probing must reach the caller.");
        Require(calls == 1, "Cancellation must stop probing the remaining encoders.");
        var resumed = await selector.ResolveAsync(tools);
        Require(resumed.IsHardware && resumed.Encoder == "h264_nvenc" && calls == 2, "A cancelled probe must be retried by a later operation.");
        await selector.ResolveAsync(tools);
        Require(calls == 2, "The later successful probe should be cached normally.");
        await Throws<OperationCanceledException>(() => selector.ResolveAsync(tools, ct: cancellation.Token), "A pre-cancelled request must remain cancelled even with a cached result.");
        Require(calls == 2, "A cancelled cache lookup must not launch new probes.");
    }

    private static async Task FallbackChecks(string root)
    {
        var tools = FixtureTools(root); var probeCount = 0;
        var selector = new ThemeEncoding((_, _, _) => { probeCount++; return Task.FromResult(true); });
        var hardware = await selector.ResolveAsync(tools); var attempts = new List<string>();
        var source = Path.Combine(root, "source-art.dat"); var existing = Path.Combine(root, "curated-theme.mp4");
        await File.WriteAllTextAsync(source, "unchanged local source"); await File.WriteAllTextAsync(existing, "unchanged curated output");
        var sourceHash = SafeFiles.Hash(source); var existingHash = SafeFiles.Hash(existing);
        var staging = Path.Combine(root, "staging-render.mp4");
        var actual = await selector.ExecuteWithFallbackAsync(tools, hardware, async (choice, ct) =>
        {
            attempts.Add(choice.Encoder); ct.ThrowIfCancellationRequested();
            if (choice.IsHardware) { await File.WriteAllTextAsync(staging, "partial GPU output", ct); throw HardwareFailure(); }
            Require(await File.ReadAllTextAsync(staging, ct) == "partial GPU output", "CPU retry must continue the same render operation after a partial GPU failure.");
            await File.WriteAllTextAsync(staging, "complete CPU output", ct);
        });
        Require(attempts.SequenceEqual(new[] { "h264_nvenc", "libx264" }) && !actual.IsHardware && actual.Encoder == "libx264",
            "Hardware initialization failure must retry CPU exactly once and report CPU as the actual encoder.");
        Require(await File.ReadAllTextAsync(staging) == "complete CPU output", "CPU fallback must replace the partial staging result.");
        Require(SafeFiles.Hash(source) == sourceHash && SafeFiles.Hash(existing) == existingHash, "Fallback must leave unrelated sources and existing output untouched.");
        var next = await selector.ResolveAsync(tools);
        Require(!next.IsHardware && probeCount == 1, "A failed full hardware encode must downgrade the cached choice to CPU.");
        attempts.Clear();
        var cpuFailure = new IOException("CPU encode failed with a full output drive.");
        var thrown = await Throws<IOException>(() => selector.ExecuteWithFallbackAsync(tools, hardware, (choice, _) =>
        {
            attempts.Add(choice.Encoder);
            throw choice.IsHardware ? HardwareFailure() : cpuFailure;
        }), "CPU retry failures must propagate.");
        Require(ReferenceEquals(thrown, cpuFailure) && attempts.SequenceEqual(new[] { "h264_nvenc", "libx264" }), "A failed CPU retry must not start a third attempt or hide its error.");

        attempts.Clear();
        var directCpuError = new IOException("libx264 could not open encoder.");
        thrown = await Throws<IOException>(() => selector.ExecuteWithFallbackAsync(tools, ThemeEncoding.Cpu(), (choice, _) =>
        { attempts.Add(choice.Encoder); throw directCpuError; }), "An initial CPU failure must propagate.");
        Require(ReferenceEquals(thrown, directCpuError) && attempts.SequenceEqual(new[] { "libx264" }), "CPU encoding must never retry itself.");
    }

    private static async Task NonFallbackChecks(string root)
    {
        var tools = FixtureTools(root); var selector = new ThemeEncoding((_, _, _) => Task.FromResult(true));
        var hardware = await selector.ResolveAsync(tools); var calls = 0; using var cancellation = new CancellationTokenSource();
        await Throws<OperationCanceledException>(() => selector.ExecuteWithFallbackAsync(tools, hardware, (_, ct) =>
        {
            calls++; cancellation.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask;
        }, ct: cancellation.Token), "Cancelling an active hardware encode must propagate.");
        Require(calls == 1 && (await selector.ResolveAsync(tools)).IsHardware, "Cancellation must neither retry CPU nor poison the working GPU choice.");
        calls = 0;
        await Throws<OperationCanceledException>(() => selector.ExecuteWithFallbackAsync(tools, hardware, (_, _) =>
        { calls++; return Task.CompletedTask; }, ct: cancellation.Token), "A pre-cancelled encode must not run even with a valid selection.");
        Require(calls == 0, "A cancelled request must not invoke the encoder callback.");

        foreach (var message in new[] { "No space left on device while writing output", "[Parsed_overlay] Error reinitializing filters: invalid input dimensions" })
        {
            calls = 0; var failure = new IOException(message);
            var thrown = await Throws<IOException>(() => selector.ExecuteWithFallbackAsync(tools, hardware, (_, _) =>
            { calls++; throw failure; }), "Non-encoder errors must propagate directly.");
            Require(ReferenceEquals(thrown, failure) && calls == 1, "Unrelated IO/filter failures must not cause a CPU retry.");
            Require((await selector.ResolveAsync(tools)).IsHardware, "An unrelated IO/filter failure must not downgrade a working GPU.");
        }
    }

    private static async Task PreservationChecks(string root)
    {
        Directory.CreateDirectory(root);
        var existing = Path.Combine(root, "curated-theme.mp4"); var source = Path.Combine(root, "local-source.png");
        await File.WriteAllTextAsync(existing, "curated theme must survive"); await File.WriteAllTextAsync(source, "local source must survive");
        var existingHash = SafeFiles.Hash(existing); var sourceHash = SafeFiles.Hash(source); var probes = 0;
        var selector = new ThemeEncoding((_, _, _) => { probes++; throw new InvalidOperationException("A preserved theme must not probe encoders."); });
        var renderer = new ThemeRenderer(VolumeHealthChecks.CleanProvider(), selector);
        var settings = new AppSettings { FfmpegPath = Path.Combine(root, "missing-ffmpeg.exe"), CachePath = Path.Combine(root, "unused-cache") };
        var first = await renderer.RenderAsync(settings, new() { ProfileId = "existing-theme", Theme = existing, Background = source, ThemeDestination = existing });
        var collision = await renderer.RenderAsync(settings, new() { ProfileId = "destination-collision", Background = source, ThemeDestination = existing });
        Require(first == existing && collision == existing && probes == 0, "Existing themes and destination collisions must be returned without probing or starting encoders.");
        Require(SafeFiles.Hash(existing) == existingHash && SafeFiles.Hash(source) == sourceHash && !Directory.Exists(Path.Combine(root, ".alm-staging"))
            && !Directory.Exists(settings.CachePath), "Automatic acceleration must preserve existing files without creating staging or cache outputs.");
    }
    private static void ThreadBudgetChecks()
    {
        var single = ThemeEncoding.Cpu(1);
        Require(!single.IsHardware && single.Encoder == "libx264" && single.FilterThreads == 1 && single.DecodeThreads == 1 && single.EncodeThreads == 1,
            "Single-core CPU budgets must avoid oversubscribing each processing stage.");
        foreach (var processors in new[] { 2, 8, 256 })
        {
            var cpu = ThemeEncoding.Cpu(processors);
            Require(cpu.FilterThreads > 0 && cpu.DecodeThreads > 0 && cpu.EncodeThreads > 0
                && cpu.FilterThreads <= processors && cpu.DecodeThreads <= processors && cpu.EncodeThreads <= processors,
                "Automatic CPU budgets must be positive and no larger than available processors.");
            if (processors == 256) Require(cpu.EncodeThreads < processors && cpu.FilterThreads < processors && cpu.DecodeThreads < processors,
                "Large machines must use bounded thread pools instead of unbounded per-stage threads.");
        }
    }

    private static MediaTools FixtureTools(string root)
    {
        Directory.CreateDirectory(root);
        var ffmpeg = Path.Combine(root, "fixture-ffmpeg.exe"); var ffprobe = Path.Combine(root, "fixture-ffprobe.exe");
        File.WriteAllBytes(ffmpeg, [1]); File.WriteAllBytes(ffprobe, [1]);
        return new(Path.GetFullPath(ffmpeg), Path.GetFullPath(ffprobe));
    }
    private static string Encoder(IReadOnlyList<string> args)
    {
        for (var i = 0; i + 1 < args.Count; i++) if (args[i] == "-c:v") return args[i + 1];
        throw new InvalidOperationException("The hardware probe did not specify a video encoder.");
    }
    private static IOException HardwareFailure() => new("[h264_nvenc] Cannot load nvcuda.dll; Error while opening encoder for output stream #0:0");
    private static async Task<T> Throws<T>(Func<Task> action, string message) where T : Exception
    {
        try { await action(); } catch (T ex) { return ex; }
        throw new InvalidOperationException(message);
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException("Theme encoding check failed: " + message); }
}