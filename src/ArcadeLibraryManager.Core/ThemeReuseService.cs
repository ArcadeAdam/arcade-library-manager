using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace ArcadeLibraryManager.Core;

/// <summary>Copies a discovered local theme without rendering or changing the source media.</summary>
public sealed class ThemeReuseService {
    static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".avi", ".webm", ".mov", ".flv", ".wmv", ".mpg", ".mpeg" };
    readonly VolumeHealthService health;
    public ThemeReuseService(VolumeHealthService? health = null) => this.health = health ?? new();

    static InvalidOperationException Rescan(string detail) => new(detail + " Scan local media again before reusing this theme.");
    static string Full(string path) {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw Rescan("A complete local media path is required.");
        return Path.GetFullPath(path);
    }
    static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
    static bool Within(string file, string folder) => file.StartsWith(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    static void PlainPath(string path) {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current)) {
            try {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw Rescan("Theme paths cannot traverse symbolic links or junctions.");
            } catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        }
    }
    static string ThemeFolder(AppSettings settings) {
        if (string.IsNullOrWhiteSpace(settings.PlatformName) || settings.PlatformName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || settings.PlatformName is "." or "..")
            throw Rescan("Choose a valid LaunchBox platform.");
        _ = Full(settings.LaunchBoxPath);
        var folders = LaunchBoxService.DiscoverMediaFolders(settings);
        return Full(folders["Theme Video"]);
    }
    static void VideoPath(string path) {
        var name = Path.GetFileName(path);
        if (!Videos.Contains(Path.GetExtension(path)) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw Rescan("The reusable theme must be a regular video file with its original extension.");
    }
    static string Existing(string path, string folder) {
        path = Full(path); VideoPath(path);
        if (!Same(Path.GetDirectoryName(path)!, folder)) throw Rescan("The existing theme is outside this platform's configured theme folder.");
        PlainPath(path);
        if (!File.Exists(path)) throw Rescan("The previously discovered theme is missing.");
        return path;
    }
    static string? ExistingDestination(string destination, string folder) {
        foreach (var candidate in new[] { destination }.Concat(Videos.Select(extension => Path.ChangeExtension(destination, extension))).Distinct(StringComparer.OrdinalIgnoreCase))
            if (File.Exists(candidate)) return Existing(candidate, folder);
        return null;
    }
    static void SourceMetadata(string source, MediaAssets assets) {
        var info = new FileInfo(source);
        if (!info.Exists || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 || info.Length <= 0)
            throw Rescan("The reusable theme is missing, empty, or is not a regular file.");
        if (assets.ReusableThemeLength <= 0 || info.Length != assets.ReusableThemeLength || info.LastWriteTimeUtc != assets.ReusableThemeLastWriteUtc)
            throw Rescan("The reusable theme changed after discovery.");
    }
    static void Scope(AppSettings settings, MediaAssets assets, string source, string destination, string folder) {
        if (!Same(ThemeFolder(settings), folder)) throw Rescan("The configured destination theme folder changed.");
        if (!assets.ReusableThemePlatform.Equals("Arcade", StringComparison.OrdinalIgnoreCase) && !assets.ReusableThemePlatform.Equals("Sega Model 2", StringComparison.OrdinalIgnoreCase))
            throw Rescan("Choose a discovered Arcade or Sega Model 2 theme.");
        var sourceFolders = MediaService.RelatedThemeFolders(settings, assets.ReusableThemePlatform).Select(Full);
        if (!sourceFolders.Any(root => Same(Path.GetDirectoryName(source)!, root)))
            throw Rescan("The reusable theme is outside the source platform's configured theme folders.");
        var stem = MediaService.SafeFileName(assets.Title, MediaService.SafeFileName(assets.ProfileId, "game"));
        if (!Same(destination, Path.Combine(folder, stem + Path.GetExtension(source))))
            throw Rescan("The reuse destination no longer matches this game's configured LaunchBox theme path and source extension.");
        if (Same(source, destination) || Within(source, folder) || Within(destination, Path.GetDirectoryName(source)!))
            throw Rescan("Source and destination theme folders overlap.");
        PlainPath(source); PlainPath(destination);
    }
    static FileStream OpenSource(string source) {
        try { return new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan); }
        catch (IOException) { throw Rescan("The reusable theme is unavailable or changed while opening it."); }
    }

    public async Task<string> ReuseAsync(AppSettings settings, MediaAssets assets, IProgress<JobEvent>? progress = null, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(settings); ArgumentNullException.ThrowIfNull(assets); ct.ThrowIfCancellationRequested();
        var folder = ThemeFolder(settings); PlainPath(folder);
        if (!string.IsNullOrWhiteSpace(assets.Theme)) {
            var existing = Existing(assets.Theme, folder);
            progress?.Report(new("Theme", "Preserved existing theme", assets.ProfileId, 100)); return existing;
        }
        var source = Full(assets.ReusableTheme); VideoPath(source);
        var destination = Full(assets.ThemeReuseDestination); VideoPath(destination);
        Scope(settings, assets, source, destination, folder);
        var gate = Gates.GetOrAdd(Path.ChangeExtension(destination, null), _ => new(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        string? staging = null;
        try {
            ct.ThrowIfCancellationRequested();
            if (ExistingDestination(destination, folder) is { } existing) {
                progress?.Report(new("Theme", "Preserved existing theme at destination", assets.ProfileId, 100)); return existing;
            }
            SourceMetadata(source, assets);
            await using var input = OpenSource(source);
            SourceMetadata(source, assets);
            if (input.Length != assets.ReusableThemeLength) throw Rescan("The reusable theme changed while opening it.");
            await health.EnsureWritableAsync([folder, destination], ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested(); Scope(settings, assets, source, destination, folder);
            Directory.CreateDirectory(folder); PlainPath(folder);
            staging = Path.Combine(folder, ".alm-theme-reuse-" + Guid.NewGuid().ToString("N") + ".tmp");
            progress?.Report(new("Theme", "Copying existing theme from " + assets.ReusableThemePlatform, assets.ProfileId, 0));
            byte[] sourceHash;
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)) {
                await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan)) {
                    var buffer = new byte[1024 * 1024]; long copied = 0;
                    while (true) {
                        var count = await input.ReadAsync(buffer, ct).ConfigureAwait(false); if (count == 0) break;
                        hash.AppendData(buffer, 0, count);
                        await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false); copied += count;
                        progress?.Report(new("Theme", "Copying existing theme", assets.ProfileId, Math.Min(95, copied * 95d / input.Length)));
                    }
                    if (copied != assets.ReusableThemeLength) throw Rescan("The reusable theme changed during copying.");
                    await output.FlushAsync(ct).ConfigureAwait(false); output.Flush(true);
                }
                sourceHash = hash.GetHashAndReset();
            }
            ct.ThrowIfCancellationRequested();
            await using (var verify = new FileStream(staging, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan)) {
                var copiedHash = await SHA256.HashDataAsync(verify, ct).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(sourceHash, copiedHash)) throw new IOException("The copied theme failed verification; the source and existing themes were preserved.");
            }
            SourceMetadata(source, assets);
            await health.EnsureWritableAsync([destination], ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested(); Scope(settings, assets, source, destination, folder);
            // An independently added theme always wins; File.Move never overwrites a collision.
            if (ExistingDestination(destination, folder) is { } collision) return collision;
            try { File.Move(staging, destination, false); }
            catch (IOException) when (File.Exists(destination)) { return Existing(destination, folder); }
            staging = null;
            progress?.Report(new("Theme", "Existing theme copied and verified", assets.ProfileId, 100)); return destination;
        } finally {
            try { if (staging != null && File.Exists(staging)) { PlainPath(staging); File.Delete(staging); } }
            finally { gate.Release(); }
        }
    }
}
