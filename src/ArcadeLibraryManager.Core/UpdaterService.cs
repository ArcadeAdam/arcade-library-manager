using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArcadeLibraryManager.Core;

public sealed class ComponentUpdate
{
    public string Component { get; set; } = "";
    public string LocalVersion { get; set; } = "";
    public string AvailableVersion { get; set; } = "";
    public bool NeedsUpdate { get; set; }
    public string Url { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public string RelativeBinary { get; set; } = "";
    public string Folder { get; set; } = "";
    public bool ManualVersion { get; set; }
    public string Status { get; set; } = "";
}
public sealed class UpdateFileChange
{
    public string RelativePath { get; set; } = "";
    public string StagedPath { get; set; } = "";
    public string BeforeHash { get; set; } = "";
    public string AfterHash { get; set; } = "";
    public bool Existed { get; set; }
    public bool Committing { get; set; }
    public bool Committed { get; set; }
}
public sealed class UpdateTransaction
{
    public string App { get; set; } = "ArcadeLibraryManager";
    public string InstallPath { get; set; } = "";
    public string State { get; set; } = "Preparing";
    public string BackupPath { get; set; } = "";
    public List<UpdateFileChange> Files { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Updates only the known official components, using published SHA256 digests and recoverable file transactions.</summary>
public sealed class UpdaterService
{
    private const string ManifestUrl = "https://teknoparrot.com/api/updates/components";
    private static readonly HttpClient DefaultHttp = new() { Timeout = TimeSpan.FromSeconds(90) };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly AppSettings settings;
    private readonly string dataDir;
    private readonly IProgress<JobEvent>? progress;
    private readonly HttpClient http;
    private readonly Func<bool> running;
    private readonly VolumeHealthService health;
    private DateTime lastProcessCheck = DateTime.MinValue;
    private static readonly string[] Specs = [
        "TeknoParrotUI|TeknoParrotUi.exe||false", "OpenParrotWin32|OpenParrotWin32/OpenParrot.dll|OpenParrotWin32|false", "OpenParrotx64|OpenParrotx64/OpenParrot64.dll|OpenParrotx64|false",
        "SegaApi|TeknoParrot/SegaApi.dll|TeknoParrot|false", "TeknoParrot|TeknoParrot/TeknoParrot.dll|TeknoParrot|false", "TeknoParrotN2|N2/TeknoParrot.dll|N2|false",
        "OpenSndGaelco|TeknoParrot/OpenSndGaelco.dll|TeknoParrot|false", "OpenSndVoyager|TeknoParrot/OpenSndVoyager.dll|TeknoParrot|false", "ScoreSubmission|TeknoParrot/ScoreSubmission.dll|TeknoParrot|false", "TeknoDraw|TeknoParrot/TeknoDraw64.dll|TeknoParrot|false",
        "TeknoParrotElfLdr2|ElfLdr2/TeknoParrot.dll|ElfLdr2|true", "FFBBlaster|FFBBlaster/x64/FFBBlaster64.dll|FFBBlaster|false", "CrediarDolphin|CrediarDolphin/Dolphin.exe|CrediarDolphin|true", "Play|Play/Play.exe|Play|true", "RPCS3|RPCS3/rpcs3.exe|RPCS3|false", "cxbxr|cxbxr/cxbxr-ldr.exe|cxbxr|false", "pcsx2x6|pcsx2x6/pcsx2-qtx64.exe|pcsx2x6|false",
        "TeknoZeus|TeknoZeus/TeknoZeus.exe|TeknoZeus|false", "TeknoCobra|TeknoCobra/TeknoCobra.exe|TeknoCobra|false", "TeknoViper|TeknoViper/TeknoViper.exe|TeknoViper|false", "TeknoVegas|TeknoVegas/TeknoVegas.exe|TeknoVegas|false", "TeknoModel1|TeknoModel1/TeknoModel1.exe|TeknoModel1|false", "TeknoModel2|TeknoModel2/TeknoModel2.exe|TeknoModel2|false", "TeknoHNG64|TeknoHNG64/TeknoHNG64.exe|TeknoHNG64|false", "TeknoHornet|TeknoHornet/TeknoHornet.exe|TeknoHornet|false", "TeknoS22|TeknoS22/TeknoS22.exe|TeknoS22|false", "TeknoAGX|TeknoAGX/TeknoAGX.exe|TeknoAGX|false", "TeknoVUnit|TeknoVUnit/TeknoVUnit.exe|TeknoVUnit|false", "TeknoM2|TeknoM2/TeknoM2.exe|TeknoM2|false", "TeknoS23|TeknoS23/TeknoS23.exe|TeknoS23|false", "TeknoGClub|TeknoGClub/TeknoGClub.exe|TeknoGClub|false", "TeknoAir|TeknoAir/TeknoAir.exe|TeknoAir|false", "TeknoS11|TeknoS11/TeknoS11.exe|TeknoS11|false", "TeknoS21|TeknoS21/TeknoS21.exe|TeknoS21|false"
    ];
    public UpdaterService(AppSettings settings, string dataDir, IProgress<JobEvent>? progress = null, HttpClient? httpClient = null, Func<bool>? processRunning = null, VolumeHealthService? healthService = null)
    {
        this.settings = settings; this.dataDir = Path.GetFullPath(dataDir); this.progress = progress; http = httpClient ?? DefaultHttp; running = processRunning ?? IsUsingInstallation; health = healthService ?? new VolumeHealthService();
    }
    public async Task<List<ComponentUpdate>> CheckAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(settings.TeknoParrotPath) || !Directory.Exists(settings.TeknoParrotPath)) throw new DirectoryNotFoundException("Select an existing TeknoParrot installation first.");
        using var request = new HttpRequestMessage(HttpMethod.Get, ManifestUrl); request.Headers.UserAgent.ParseAdd("ArcadeLibraryManager/0.2");
        using var response = await http.SendAsync(request, ct); response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("The official update manifest format was not recognized.");
        var entries = document.RootElement.EnumerateArray().Where(e => e.TryGetProperty("component", out _)).ToDictionary(e => e.GetProperty("component").GetString()!, StringComparer.OrdinalIgnoreCase);
        var result = new List<ComponentUpdate>();
        foreach (var spec in Specs)
        {
            ct.ThrowIfCancellationRequested(); var parts = spec.Split('|');
            var update = new ComponentUpdate { Component = parts[0], RelativeBinary = parts[1], Folder = parts[2], ManualVersion = parts[3] == "true", AvailableVersion = "Unavailable" };
            try
            {
                update.LocalVersion = ReadVersion(update.RelativeBinary, update.ManualVersion);
                if (!entries.TryGetValue(parts[0], out var item) || !item.TryGetProperty("release", out var release) || release.ValueKind != JsonValueKind.Object) throw new InvalidDataException("No current official release was returned.");
                var version = Normalize(release.GetProperty("name").GetString() ?? "");
                if (!Version.TryParse(version, out _)) throw new InvalidDataException("The release version was not recognized.");
                update.AvailableVersion = version;
                var assets = release.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString()?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true).ToArray();
                if (assets.Length != 1) throw new InvalidDataException("A unique ZIP release asset was not found.");
                var asset = assets[0]; var url = asset.GetProperty("browser_download_url").GetString() ?? "";
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                    !(uri.AbsolutePath.StartsWith("/teknogods/", StringComparison.OrdinalIgnoreCase) || (parts[0] == "RPCS3" && uri.AbsolutePath.StartsWith("/ReaverTeknoGods/rpcs3/", StringComparison.OrdinalIgnoreCase))))
                    throw new InvalidDataException("The download publisher is not in this component's trusted official list.");
                var digest = asset.TryGetProperty("digest", out var hash) ? hash.GetString() ?? "" : "";
                update.Sha256 = Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$") ? digest[7..] : "";
                update.Url = url; update.Size = asset.GetProperty("size").GetInt64();
                update.NeedsUpdate = !Version.TryParse(Normalize(update.LocalVersion), out var local) || local < Version.Parse(version);
                if (update.Component == "SegaApi" && Normalize(ReadVersion("ElfLdr2/libs/SegaApi.dll", false)) != Normalize(update.LocalVersion)) update.NeedsUpdate = true;
                update.Status = !update.NeedsUpdate ? "Current" : update.Sha256.Length == 0 ? "Update blocked: published SHA256 missing" : "Verified update available";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                update.Status = "Unavailable: " + ex.Message;
                update.NeedsUpdate = false;
                update.Sha256 = "";
            }
            result.Add(update);
        }
        if (result.Count == 0) throw new InvalidDataException("No supported official components were returned.");
        progress?.Report(new("Updates", $"Checked {result.Count} official components; {result.Count(u => u.NeedsUpdate)} updates available; {result.Count(u => u.Status.StartsWith("Unavailable:", StringComparison.Ordinal))} unavailable."));
        return result;
    }

    public async Task<OperationResult> ApplyAsync(IEnumerable<ComponentUpdate> selected, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            EnsureClosed();
            await health.EnsureWritableAsync([settings.TeknoParrotPath, dataDir, Path.Combine(dataDir, "Updates")], ct);
            var wanted = selected.Where(c => c.NeedsUpdate).GroupBy(c => c.Component, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            if (wanted.Count == 0) return new(0, 0, []);
            // Automatic recovery touches only bytes which still match an interrupted app-owned commit.
            foreach (var interrupted in FindInterruptedTransactions()) await RollbackInternalAsync(interrupted, ct);
            var fresh = (await CheckAsync(ct)).ToDictionary(x => x.Component, StringComparer.OrdinalIgnoreCase);
            var updates = new List<ComponentUpdate>();
            foreach (var entry in wanted)
            {
                if (!fresh.TryGetValue(entry.Component, out var now) || now.AvailableVersion != entry.AvailableVersion || !now.Sha256.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase) || now.Url != entry.Url || now.Size != entry.Size) throw new InvalidOperationException("Official releases changed after preview. Check updates again.");
                if (now.NeedsUpdate && !Regex.IsMatch(now.Sha256, "^[a-fA-F0-9]{64}$")) throw new InvalidDataException("Published SHA256 is unavailable for " + now.Component + "; this update cannot be installed safely.");
                if (now.NeedsUpdate) updates.Add(now);
            }
            if (updates.Count == 0) return new(0, wanted.Count, []);
            var updateRoot = Path.Combine(dataDir, "Updates"); var cache = Path.Combine(updateRoot, "Downloads"); Directory.CreateDirectory(cache);
            var txRoot = Path.Combine(updateRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(txRoot);
            var journal = Path.Combine(txRoot, "transaction.json");
            var tx = new UpdateTransaction { InstallPath = Path.GetFullPath(settings.TeknoParrotPath), BackupPath = Path.Combine(txRoot, "Backup") };
            var changes = new Dictionary<string, UpdateFileChange>(StringComparer.OrdinalIgnoreCase);
            foreach (var component in updates)
            {
                ct.ThrowIfCancellationRequested(); var zip = Path.Combine(cache, component.Component + "-" + component.AvailableVersion + "-" + component.Sha256[..12] + ".zip");
                if (!File.Exists(zip)) await new DownloadService(settings.MaxDownloadMbps, progress).GetAsync(component.Url, zip, component.Size, ct);
                await VerifyPackage(zip, component, ct);
                var stage = Path.Combine(txRoot, "Stage", component.Component); SafeFiles.ExtractZip(zip, stage, ct, progress);
                foreach (var source in SafeFiles.Files(stage, ct))
                {
                    var relative = Path.Combine(component.Folder, Path.GetRelativePath(stage, source));
                    if (IsProtected(relative)) continue;
                    changes[relative.Replace('\\', '/')] = new() { RelativePath = relative.Replace('\\', '/'), StagedPath = source, AfterHash = SafeFiles.Hash(source) };
                }
                if (!changes.ContainsKey(component.RelativeBinary)) throw new InvalidDataException("Release asset did not contain its expected binary: " + component.Component);
                if (component.Component == "SegaApi")
                {
                    var source = changes[component.RelativeBinary]; changes["ElfLdr2/libs/SegaApi.dll"] = new() { RelativePath = "ElfLdr2/libs/SegaApi.dll", StagedPath = source.StagedPath, AfterHash = source.AfterHash };
                }
                if (component.ManualVersion)
                {
                    var marker = Path.Combine(stage, ".alm-version"); await File.WriteAllTextAsync(marker, component.AvailableVersion, ct);
                    var relative = Path.Combine(Path.GetDirectoryName(component.RelativeBinary)!, ".version").Replace('\\', '/'); changes[relative] = new() { RelativePath = relative, StagedPath = marker, AfterHash = SafeFiles.Hash(marker) };
                }
            }
            EnsureClosed(); tx.Files = changes.Values.ToList();
            await health.EnsureWritableAsync(tx.Files.Select(f => Target(f.RelativePath)).Append(tx.BackupPath).Append(journal), ct);
            Directory.CreateDirectory(tx.BackupPath);
            Save(journal, tx);
            foreach (var change in tx.Files)
            {
                ct.ThrowIfCancellationRequested(); var target = Target(change.RelativePath);
                if (File.Exists(target))
                {
                    RejectLink(target); change.Existed = true; change.BeforeHash = SafeFiles.Hash(target);
                    var backup = SafeFiles.Child(tx.BackupPath, change.RelativePath); Directory.CreateDirectory(Path.GetDirectoryName(backup)!); File.Copy(target, backup, false);
                    if (SafeFiles.Hash(backup) != change.BeforeHash || SafeFiles.Hash(target) != change.BeforeHash) throw new IOException("File changed while creating update backup: " + change.RelativePath);
                }
            }
            tx.State = "BackedUp"; Save(journal, tx);
            progress?.Report(new("Update backup", $"Verified backups for {tx.Files.Count(f => f.Existed)} files before replacement."));
            try
            {
                foreach (var change in tx.Files)
                {
                    ct.ThrowIfCancellationRequested(); EnsureClosed(false); var target = Target(change.RelativePath);
                    if (change.Existed ? !File.Exists(target) || SafeFiles.Hash(target) != change.BeforeHash : File.Exists(target)) throw new IOException("A file changed after update preview/backup: " + change.RelativePath);
                    RejectLink(target); change.Committing = true; tx.State = "Committing"; Save(journal, tx);
                    AtomicCopy(change.StagedPath, target, change.AfterHash, change.Existed ? change.BeforeHash : null);
                    change.Committed = true; Save(journal, tx);
                    progress?.Report(new("Updating", change.RelativePath, Percent: 100d * tx.Files.Count(f => f.Committed) / tx.Files.Count));
                }
                tx.State = "Complete"; Save(journal, tx); progress?.Report(new("Updates", $"Updated {updates.Count} components; backups and rollback journal saved."));
                return new(updates.Count, wanted.Count - updates.Count, []);
            }
            catch (VolumeHealthException) { throw; }
            catch (Exception original)
            {
                tx.Errors.Add(original.Message); Save(journal, tx);
                try { await RollbackInternalAsync(journal, CancellationToken.None); }
                catch (Exception recovery) { throw new IOException("Update stopped. Some files need review before rollback: " + recovery.Message + ". Journal: " + journal, original); }
                throw;
            }
        }
        finally { Gate.Release(); }
    }

    public List<string> FindInterruptedTransactions()
    {
        var root = Path.Combine(dataDir, "Updates"); if (!Directory.Exists(root)) return [];
        return Directory.EnumerateDirectories(root).Select(d => Path.Combine(d, "transaction.json")).Where(File.Exists).Where(p =>
        {
            var state = JsonSerializer.Deserialize<UpdateTransaction>(File.ReadAllText(p), SettingsStore.Json); return state?.State is "Committing" or "RollbackNeedsReview";
        }).ToList();
    }
    public async Task<OperationResult> RollbackAsync(string journalPath, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct); try { await RollbackInternalAsync(journalPath, ct); return new(1, 0, []); } finally { Gate.Release(); }
    }
    private Task RollbackInternalAsync(string journalPath, CancellationToken ct)
    {
        EnsureClosed();
        health.EnsureWritable([settings.TeknoParrotPath, dataDir, journalPath], ct);
        var root = Path.GetFullPath(Path.Combine(dataDir, "Updates")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(journalPath).StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update journal must belong to this application's update directory.");
        var tx = JsonSerializer.Deserialize<UpdateTransaction>(File.ReadAllText(journalPath), SettingsStore.Json) ?? throw new InvalidDataException("Invalid update journal.");
        if (tx.App != "ArcadeLibraryManager" || !tx.InstallPath.Equals(Path.GetFullPath(settings.TeknoParrotPath), StringComparison.OrdinalIgnoreCase) || !Path.GetFullPath(tx.BackupPath).StartsWith(Path.GetDirectoryName(Path.GetFullPath(journalPath))! + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update journal does not match this installation.");
        var conflicts = new List<string>();
        foreach (var change in tx.Files.Where(f => f.Committing || f.Committed).Reverse())
        {
            ct.ThrowIfCancellationRequested(); if (IsProtected(change.RelativePath)) { conflicts.Add(change.RelativePath); continue; }
            var target = Target(change.RelativePath); RejectLink(target);
            if (File.Exists(target))
            {
                var current = SafeFiles.Hash(target);
                if (change.Existed && current == change.BeforeHash) continue;
                if (current != change.AfterHash) { conflicts.Add(change.RelativePath); continue; }
            }
            else if (!change.Existed) continue;
            else { conflicts.Add(change.RelativePath); continue; }
            if (change.Existed)
            {
                var backup = SafeFiles.Child(tx.BackupPath, change.RelativePath); if (!File.Exists(backup) || SafeFiles.Hash(backup) != change.BeforeHash) { conflicts.Add(change.RelativePath); continue; }
                AtomicCopy(backup, target, change.BeforeHash, change.AfterHash);
            }
            else { health.EnsureWritable([target], ct); File.Delete(target); }
            change.Committed = false; change.Committing = false; Save(journalPath, tx);
        }
        tx.State = conflicts.Count > 0 ? "RollbackNeedsReview" : "RolledBack"; tx.Errors.AddRange(conflicts.Select(f => "Concurrent edit preserved: " + f)); Save(journalPath, tx);
        if (conflicts.Count > 0) throw new IOException(string.Join(", ", conflicts));
        progress?.Report(new("Rollback", "Restored the previous component files; user configuration was preserved.")); return Task.CompletedTask;
    }
    public static bool IsProtected(string relative)
    {
        var path = relative.Replace('\\', '/');
        if (Regex.IsMatch(path, @"^(UserProfiles/|ParrotData\.xml$|teknoparrot\.ini$|CrediarDolphin/User/|(Play|RPCS3|cxbxr|pcsx2x6)/TeknoParrot/)", RegexOptions.IgnoreCase)) return true;
        if (path.Split('/').Any(p => new[] { "saves", "save", "nvram", "memcards", "savestates", "screenshots", "screens", "logs", "cache", "user" }.Contains(p, StringComparer.OrdinalIgnoreCase))) return true;
        return Path.GetExtension(path).Equals(".ini", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path).Equals("settings.json", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path).Equals("config.yml", StringComparison.OrdinalIgnoreCase);
    }
    private string Target(string relative)
    {
        var root = Path.GetFullPath(settings.TeknoParrotPath); RejectLink(root); return SafeFiles.Child(root, relative);
    }
    private static void RejectLink(string path) { if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked update targets are not allowed: " + path); }
    private void Save(string path, UpdateTransaction tx) { health.EnsureWritable([path]); tx.UpdatedUtc = DateTimeOffset.UtcNow; SafeFiles.WriteText(path, JsonSerializer.Serialize(tx, SettingsStore.Json)); }
    private void AtomicCopy(string source, string target, string expected, string? baseline)
    {
        health.EnsureWritable([target, Path.GetDirectoryName(target)!]);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!); var temp = Path.Combine(Path.GetDirectoryName(target)!, ".alm-update-" + Guid.NewGuid().ToString("N") + ".tmp");
        File.Copy(source, temp, false); if (SafeFiles.Hash(temp) != expected) throw new IOException("Staged file checksum mismatch.");
        if (baseline is null ? File.Exists(target) : !File.Exists(target) || SafeFiles.Hash(target) != baseline) throw new IOException("Concurrent edit preserved while preparing atomic replacement: " + target);
        health.EnsureWritable([target, temp]);
        File.Move(temp, target, true);
        if (SafeFiles.Hash(target) != expected) throw new IOException("Installed file checksum mismatch.");
    }
    private static async Task VerifyPackage(string zip, ComponentUpdate update, CancellationToken ct)
    {
        if (new FileInfo(zip).Length != update.Size) throw new InvalidDataException("Component package size mismatch: " + update.Component);
        await using var stream = File.OpenRead(zip); if (!Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).Equals(update.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Component package SHA256 mismatch: " + update.Component);
    }
    private string ReadVersion(string relative, bool manual)
    {
        var file = SafeFiles.Child(settings.TeknoParrotPath, relative); if (!File.Exists(file)) return "Not installed";
        if (manual) { var marker = Path.Combine(Path.GetDirectoryName(file)!, ".version"); return File.Exists(marker) ? File.ReadAllText(marker).Trim() : "Unknown"; }
        return FileVersionInfo.GetVersionInfo(file).ProductVersion?.Trim() ?? "Unknown";
    }
    private static string Normalize(string value) { var m = Regex.Match(value, @"\d+\.\d+\.\d+\.\d+"); return m.Success ? m.Value : value; }
    private void EnsureClosed(bool force = true) { if (!force && DateTime.UtcNow - lastProcessCheck < TimeSpan.FromSeconds(1)) return; if (running()) throw new InvalidOperationException("Close TeknoParrot and running arcade games before applying or rolling back emulator updates."); lastProcessCheck = DateTime.UtcNow; }
    private bool IsUsingInstallation()
    {
        var root = Path.GetFullPath(settings.TeknoParrotPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                try
                {
                    if (Regex.IsMatch(process.ProcessName, @"^(Tekno|Budgie|OpenParrot|ParrotPatcher)", RegexOptions.IgnoreCase)) return true;
                    if (process.MainModule?.FileName?.StartsWith(root, StringComparison.OrdinalIgnoreCase) == true) return true;
                    foreach (ProcessModule module in process.Modules) if (module.FileName.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        return false;
    }
}





