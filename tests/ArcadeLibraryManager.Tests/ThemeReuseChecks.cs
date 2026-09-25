using System.Security.Cryptography;
using System.Xml.Linq;
using ArcadeLibraryManager.Core;

public static class ThemeReuseChecks {
    sealed record Fixture(AppSettings Settings, MediaAssets Assets, byte[] Bytes);
    sealed class InlineProgress(Action<JobEvent> action) : IProgress<JobEvent> { public void Report(JobEvent value) => action(value); }
    static readonly VolumeHealthService Clean = new((path, ct) => Task.FromResult(new VolumeHealthResult(path, Path.GetPathRoot(path)!, "NTFS", VolumeHealthState.Clean, "Fixture clean volume")));
    static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    static Fixture Create(string root, string name, string extension = ".mkv", int length = 32781) {
        var lb = Path.Combine(root, name, "LaunchBox"); var sourceFolder = Path.Combine(lb, "Videos", "Arcade", "Theme");
        Directory.CreateDirectory(sourceFolder); Directory.CreateDirectory(Path.Combine(lb, "Data"));
        var bytes = new byte[length]; new Random(71).NextBytes(bytes);
        var source = Path.Combine(sourceFolder, "Racing community theme" + extension); File.WriteAllBytes(source, bytes);
        var info = new FileInfo(source);
        return new(new AppSettings { LaunchBoxPath = lb, PlatformName = "TeknoParrot", FfmpegPath = Path.Combine(lb, "does-not-exist.exe") },
            new MediaAssets { ProfileId = "race", Title = "Racing: Game (USA)", ReusableTheme = source, ReusableThemePlatform = "Arcade", ReusableThemeLength = info.Length,
                ReusableThemeLastWriteUtc = info.LastWriteTimeUtc, ThemeReuseDestination = Path.Combine(lb, "Videos", "TeknoParrot", "Theme", "Racing_ Game (USA)" + extension),
                ThemeDestination = Path.Combine(lb, "Videos", "TeknoParrot", "Theme", "Racing_ Game (USA).mp4") }, bytes);
    }
    static void NoPartial(Fixture f) {
        var directory = Path.GetDirectoryName(f.Assets.ThemeReuseDestination)!;
        Check(!Directory.Exists(directory) || Directory.GetFiles(directory, ".alm-theme-reuse-*.tmp").Length == 0, "A temporary theme copy remained after the operation.");
    }
    static async Task Reject(Func<Task> operation, string label, bool requireRescan = true) {
        try { await operation(); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException) {
            if (requireRescan) Check(ex.Message.Contains("Scan local media", StringComparison.Ordinal), label + " did not request a fresh media scan.");
            return;
        }
        throw new InvalidOperationException(label + " unexpectedly succeeded.");
    }
    static async Task Canceled(Func<Task> operation) {
        try { await operation(); } catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Canceled theme reuse unexpectedly completed.");
    }
    public static async Task<List<string>> RunAsync(string root) {
        var passed = new List<string>(); var service = new ThemeReuseService(Clean);
        var copy = Create(root, "byte-copy"); var originalHash = Hash(copy.Assets.ReusableTheme); var originalStamp = File.GetLastWriteTimeUtc(copy.Assets.ReusableTheme);
        var events = new List<JobEvent>();
        var result = await service.ReuseAsync(copy.Settings, copy.Assets, new InlineProgress(events.Add));
        Check(result == copy.Assets.ThemeReuseDestination && Path.GetExtension(result) == ".mkv" && File.ReadAllBytes(result).SequenceEqual(copy.Bytes), "Theme reuse did not retain exact bytes, title filename or original MKV extension.");
        Check(Hash(copy.Assets.ReusableTheme) == originalHash && File.GetLastWriteTimeUtc(copy.Assets.ReusableTheme) == originalStamp, "Theme reuse altered source content or metadata.");
        Check(!File.Exists(copy.Assets.ThemeDestination) && string.IsNullOrEmpty(copy.Assets.Snap) && string.IsNullOrEmpty(copy.Assets.Background) && !File.Exists(copy.Settings.FfmpegPath), "The no-snap/tool fixture was not isolated from rendering.");
        Check(events.Any(e => e.Percent == 100 && e.Message.Contains("verified")), "Theme reuse did not report verified completion."); NoPartial(copy);
        Check(await service.ReuseAsync(copy.Settings, copy.Assets) == result && File.ReadAllBytes(result).SequenceEqual(copy.Bytes), "Repeated reuse replaced an existing theme.");
        passed.Add("Copies existing themes byte-for-byte without snaps, artwork or FFmpeg; retains MKV extension, source metadata and repeat safety");

        var collision = Create(root, "collision"); Directory.CreateDirectory(Path.GetDirectoryName(collision.Assets.ThemeReuseDestination)!);
        File.WriteAllText(collision.Assets.ThemeReuseDestination, "owner theme");
        Check(await service.ReuseAsync(collision.Settings, collision.Assets) == collision.Assets.ThemeReuseDestination && File.ReadAllText(collision.Assets.ThemeReuseDestination) == "owner theme", "An existing destination was overwritten."); NoPartial(collision);
        var alternate = Create(root, "alternate-extension-collision"); Directory.CreateDirectory(Path.GetDirectoryName(alternate.Assets.ThemeDestination)!); File.WriteAllText(alternate.Assets.ThemeDestination, "newly discovered MP4");
        Check(await service.ReuseAsync(alternate.Settings, alternate.Assets) == alternate.Assets.ThemeDestination && !File.Exists(alternate.Assets.ThemeReuseDestination), "An existing MP4 was duplicated by an MKV reuse copy."); NoPartial(alternate);
        var existing = Create(root, "existing-theme"); var owner = Path.Combine(Path.GetDirectoryName(existing.Assets.ThemeReuseDestination)!, "Racing_ Game (USA)-01.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(owner)!); File.WriteAllText(owner, "existing LaunchBox theme"); existing.Assets.Theme = owner;
        Check(await service.ReuseAsync(existing.Settings, existing.Assets) == owner && !File.Exists(existing.Assets.ThemeReuseDestination), "An existing theme with another media filename or extension was replaced.");
        var parallel = Create(root, "concurrent"); var both = await Task.WhenAll(service.ReuseAsync(parallel.Settings, parallel.Assets), service.ReuseAsync(parallel.Settings, parallel.Assets));
        Check(both.All(p => p == parallel.Assets.ThemeReuseDestination) && File.ReadAllBytes(both[0]).SequenceEqual(parallel.Bytes), "Concurrent reuse did not preserve one exact destination."); NoPartial(parallel);
        passed.Add("Preserves existing themes and destination collisions; serializes repeated concurrent copies without partial files");

        foreach (var mode in new[] { "wrong-folder", "wrong-title", "wrong-extension", "wrong-source-folder", "wrong-platform", "non-video", "nested-source", "missing", "zero", "changed-length", "changed-stamp" }) {
            var f = Create(root, "reject-" + mode); var source = f.Assets.ReusableTheme;
            switch (mode) {
                case "wrong-folder": f.Assets.ThemeReuseDestination = Path.Combine(f.Settings.LaunchBoxPath, "wrong", Path.GetFileName(f.Assets.ThemeReuseDestination)); break;
                case "wrong-title": f.Assets.ThemeReuseDestination = Path.Combine(Path.GetDirectoryName(f.Assets.ThemeReuseDestination)!, "Different game.mkv"); break;
                case "wrong-extension": f.Assets.ThemeReuseDestination = Path.ChangeExtension(f.Assets.ThemeReuseDestination, ".mp4"); break;
                case "wrong-source-folder": f.Assets.ReusableTheme = Path.Combine(f.Settings.LaunchBoxPath, "Videos", "Arcade", "snap.mkv"); File.Copy(source, f.Assets.ReusableTheme); break;
                case "wrong-platform": f.Assets.ReusableThemePlatform = "Other"; break;
                case "non-video": f.Assets.ReusableTheme = Path.ChangeExtension(source, ".png"); File.Copy(source, f.Assets.ReusableTheme); break;
                case "nested-source": var nested = Path.Combine(Path.GetDirectoryName(source)!, "subfolder"); Directory.CreateDirectory(nested); f.Assets.ReusableTheme = Path.Combine(nested, "nested.mkv"); File.Copy(source, f.Assets.ReusableTheme); break;
                case "missing": File.Delete(source); break;
                case "zero": File.WriteAllBytes(source, []); break;
                case "changed-length": File.AppendAllText(source, "changed"); break;
                case "changed-stamp": File.SetLastWriteTimeUtc(source, f.Assets.ReusableThemeLastWriteUtc.AddMinutes(2)); break;
            }
            await Reject(() => service.ReuseAsync(f.Settings, f.Assets), mode);
            Check(!File.Exists(f.Assets.ThemeReuseDestination), mode + " created a destination unexpectedly."); NoPartial(f);
        }
        passed.Add("Rejects wrong destination scope, title or extension, unapproved source folders, missing/empty/changed sources and requests a fresh scan");

        var overlap = Create(root, "overlap"); var overlappingFolder = Path.GetDirectoryName(overlap.Assets.ThemeReuseDestination)!; Directory.CreateDirectory(overlappingFolder);
        var sharedSource = Path.Combine(overlappingFolder, "shared-source.mkv"); File.Copy(overlap.Assets.ReusableTheme, sharedSource); overlap.Assets.ReusableTheme = sharedSource;
        new XDocument(new XElement("LaunchBox", new XElement("PlatformFolder", new XElement("Platform", "Arcade"), new XElement("MediaType", "Theme Video"), new XElement("FolderPath", overlappingFolder)))).Save(Path.Combine(overlap.Settings.LaunchBoxPath, "Data", "Platforms.xml"));
        await Reject(() => service.ReuseAsync(overlap.Settings, overlap.Assets), "overlapping source and destination");
        Check(File.ReadAllBytes(sharedSource).SequenceEqual(overlap.Bytes) && !File.Exists(overlap.Assets.ThemeReuseDestination), "Overlapping folders changed a theme."); NoPartial(overlap);
        passed.Add("Rejects overlapping source and target theme folders even when a platform override points both at the same directory");

        var linked = Create(root, "reparse"); var link = Path.Combine(Path.GetDirectoryName(linked.Assets.ReusableTheme)!, "linked-source.mkv"); var linkAvailable = false;
        try { File.CreateSymbolicLink(link, linked.Assets.ReusableTheme); linkAvailable = true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
        if (linkAvailable) {
            linked.Assets.ReusableTheme = link;
            await Reject(() => service.ReuseAsync(linked.Settings, linked.Assets), "reparse source");
            Check(!File.Exists(linked.Assets.ThemeReuseDestination), "A symbolic source theme was copied."); NoPartial(linked);
            passed.Add("Rejects a symbolic-link source theme before creating output");
        } else passed.Add("Symbolic-link fixture unavailable on this host; overlap and ordinary source-scope checks completed");

        var dirty = Create(root, "dirty");
        var blocked = new ThemeReuseService(new((path, ct) => Task.FromResult(new VolumeHealthResult(path, Path.GetPathRoot(path)!, "NTFS", VolumeHealthState.Dirty, "Fixture dirty volume"))));
        await Reject(() => blocked.ReuseAsync(dirty.Settings, dirty.Assets), "dirty volume", false);
        Check(!Directory.Exists(Path.GetDirectoryName(dirty.Assets.ThemeReuseDestination)) && File.ReadAllBytes(dirty.Assets.ReusableTheme).SequenceEqual(dirty.Bytes), "Dirty-volume protection created output or altered source.");
        var early = Create(root, "cancel-before"); using (var cancel = new CancellationTokenSource()) { cancel.Cancel(); await Canceled(() => service.ReuseAsync(early.Settings, early.Assets, ct: cancel.Token)); }
        Check(!Directory.Exists(Path.GetDirectoryName(early.Assets.ThemeReuseDestination)), "Pre-canceled reuse created a destination folder.");
        var during = Create(root, "cancel-during", length: 3 * 1024 * 1024 + 17); var writeDenied = false;
        using (var cancel = new CancellationTokenSource()) {
            var progress = new InlineProgress(e => {
                if (e.Percent is not > 0 or >= 100) return;
                try { using var writer = new FileStream(during.Assets.ReusableTheme, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); }
                catch (IOException) { writeDenied = true; }
                cancel.Cancel();
            });
            await Canceled(() => service.ReuseAsync(during.Settings, during.Assets, progress, cancel.Token));
        }
        Check(writeDenied && !File.Exists(during.Assets.ThemeReuseDestination) && File.ReadAllBytes(during.Assets.ReusableTheme).SequenceEqual(during.Bytes), "Copy cancellation or source read-lock protection failed."); NoPartial(during);
        passed.Add("Blocks unhealthy volumes before writes, honors cancellation before and during copying, locks source against writes and removes partial copies");

        var stale = Create(root, "changed-config"); var calls = 0;
        var changeConfig = new VolumeHealthService((path, ct) => {
            if (++calls == 3) new XDocument(new XElement("LaunchBox", new XElement("PlatformFolder", new XElement("Platform", "TeknoParrot"), new XElement("MediaType", "Theme Video"), new XElement("FolderPath", "Videos\\New Location")))).Save(Path.Combine(stale.Settings.LaunchBoxPath, "Data", "Platforms.xml"));
            return Task.FromResult(new VolumeHealthResult(path, Path.GetPathRoot(path)!, "NTFS", VolumeHealthState.Clean, "Fixture clean volume"));
        });
        await Reject(() => new ThemeReuseService(changeConfig).ReuseAsync(stale.Settings, stale.Assets), "changed destination configuration");
        Check(!File.Exists(stale.Assets.ThemeReuseDestination) && File.ReadAllBytes(stale.Assets.ReusableTheme).SequenceEqual(stale.Bytes), "Stale configured folder allowed commit or altered source."); NoPartial(stale);
        passed.Add("Revalidates configured theme-folder scope before commit and removes the staged copy when settings become stale");
        return passed;
    }
}
