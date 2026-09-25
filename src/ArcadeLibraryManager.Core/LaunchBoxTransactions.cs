using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace ArcadeLibraryManager.Core;

public sealed record LaunchBoxRollbackFile(string SourcePath, string BackupPath, string BeforeHash, string AfterHash);

/// <summary>Stages all XML and immutable backups before replacing any live file.</summary>
public static class LaunchBoxTransaction {
    sealed record StagedFile(LaunchBoxRollbackFile File, string TempPath);
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static void Closed(Func<bool> isRunning) { if(isRunning()) throw new InvalidOperationException("Close LaunchBox and Big Box before changing their library files."); }
    static string Hash(string path) => LaunchBoxService.Hash(path);
    static void SameHash(string path, string expected) {
        if(!File.Exists(path) || !string.Equals(Hash(path), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A LaunchBox file changed; no external edits will be overwritten: " + path);
    }
    static void ValidateHashes(IReadOnlyDictionary<string,string> hashes) { foreach(var pair in hashes) SameHash(pair.Key, pair.Value); }
    static void SaveJournal(string path, LaunchBoxRollback journal) {
        var temp = path + ".writing-" + Guid.NewGuid().ToString("N");
        try {
            using(var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                JsonSerializer.Serialize(stream, journal, Json); stream.Flush(true);
            }
            if(File.Exists(path)) File.Replace(temp, path, null, true); else File.Move(temp, path);
        } finally { if(File.Exists(temp)) File.Delete(temp); }
    }
    static void SaveXml(string path, XDocument document) {
        var copy = new XDocument(document); LaunchBoxService.NormalizeFormatting(copy);
        using(var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
            using(var writer = XmlWriter.Create(stream, new XmlWriterSettings { Indent = true, IndentChars = "  ", NewLineChars = "\r\n", NewLineHandling = NewLineHandling.Entitize, Encoding = new UTF8Encoding(false), CloseOutput = false })) copy.Save(writer);
            stream.Flush(true);
        }
        LaunchBoxService.ReadXml(path);
    }
    static void DeleteTemps(IEnumerable<string> paths) {
        foreach(var path in paths) try { if(File.Exists(path)) File.Delete(path); } catch(IOException) { } catch(UnauthorizedAccessException) { }
    }

    public static async Task<OperationResult> CommitAsync(LaunchBoxPlan plan, XDocument document, Func<bool> isRunning, VolumeHealthService health, IProgress<JobEvent>? progress = null, CancellationToken ct = default) {
        var selected = plan.Items.Where(i => i.Selected && i.Action is "Add" or "Update" or "Combine" or "Consolidate").ToList();
        if(selected.Count == 0) return new(0, plan.Items.Count, new());
        ct.ThrowIfCancellationRequested(); Closed(isRunning);
        var related = LaunchBoxRelatedFiles.PrepareCommit(plan, selected);
        var documents = new Dictionary<string,XDocument>(StringComparer.OrdinalIgnoreCase) { [Path.GetFullPath(plan.SourcePath)] = document };
        foreach(var pair in related) { var path = Path.GetFullPath(pair.Key); if(!documents.TryAdd(path,pair.Value)) throw new InvalidDataException("Duplicate transaction destination: " + path); }
        var expected = new Dictionary<string,string>(plan.SourceHashes, StringComparer.OrdinalIgnoreCase);
        foreach(var path in documents.Keys) if(!expected.ContainsKey(path)) throw new InvalidDataException("A transaction file was not hashed during preview: " + path);
        ValidateHashes(expected);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..8];
        var backupDirectory = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(plan.SourcePath)!, "..", "..", "Backups", "ArcadeLibraryManager", stamp));
        await health.EnsureWritableAsync(documents.Keys.Append(backupDirectory), ct);
        Closed(isRunning); ValidateHashes(expected);
        Directory.CreateDirectory(backupDirectory);
        var staged = new List<StagedFile>(); var replaced = new List<LaunchBoxRollbackFile>();
        var journalPath = Path.Combine(backupDirectory, "rollback.json");
        LaunchBoxRollback? journal = null;
        try {
            var ordered = documents.OrderBy(p => p.Key.Equals(Path.GetFullPath(plan.SourcePath),StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(p => p.Key,StringComparer.OrdinalIgnoreCase).ToList();
            for(var index = 0; index < ordered.Count; index++) {
                ct.ThrowIfCancellationRequested(); Closed(isRunning); ValidateHashes(expected);
                var pair = ordered[index];
                var backup = Path.Combine(backupDirectory, index == 0 ? Path.GetFileName(pair.Key) : $"{index:0000}-" + Path.GetFileName(pair.Key));
                File.Copy(pair.Key, backup, false); SameHash(backup, expected[pair.Key]);
                var temp = pair.Key + ".alm-" + Guid.NewGuid().ToString("N") + ".tmp";
                // Track the path before writing so failed serialization cannot leave an abandoned stage.
                var placeholder = new LaunchBoxRollbackFile(pair.Key, backup, expected[pair.Key], "");
                staged.Add(new(placeholder, temp)); SaveXml(temp, pair.Value);
                staged[^1] = new(placeholder with { AfterHash = Hash(temp) }, temp);
            }
            var platform = staged[0].File; plan.BackupPath = platform.BackupPath;
            journal = new(platform.SourcePath, platform.BackupPath, platform.BeforeHash, platform.AfterHash, DateTimeOffset.UtcNow) { Files = staged.Select(s => s.File).ToList(), State = "Prepared" };
            ValidateHashes(expected); Closed(isRunning);
            // Re-evaluate selected remaps and the full XML inventory after staging, before any replacement.
            LaunchBoxRelatedFiles.PrepareCommit(plan, selected);
            SaveJournal(journalPath, journal);
            foreach(var stage in staged) {
                ct.ThrowIfCancellationRequested(); Closed(isRunning);
                await health.EnsureWritableAsync(new[] { stage.File.SourcePath }, ct);
                ValidateHashes(expected); LaunchBoxRelatedFiles.AssertInventory(plan);
                SameHash(stage.File.BackupPath, stage.File.BeforeHash); SameHash(stage.TempPath, stage.File.AfterHash);
                File.Replace(stage.TempPath, stage.File.SourcePath, null, true);
                replaced.Add(stage.File); expected[stage.File.SourcePath] = stage.File.AfterHash;
                SameHash(stage.File.SourcePath, stage.File.AfterHash);
            }
            Closed(isRunning); ValidateHashes(expected); LaunchBoxRelatedFiles.AssertInventory(plan);
            foreach(var stage in staged) SameHash(stage.File.BackupPath, stage.File.BeforeHash);
            SaveJournal(journalPath, journal with { State = "Committed" });
            progress?.Report(new("LaunchBox", $"Committed {selected.Count} changes across {staged.Count} XML files. Backup: {plan.BackupPath}"));
            return new(selected.Count, plan.Items.Count - selected.Count, new());
        } catch(Exception failure) {
            if(journal != null && replaced.Count > 0) {
                try {
                    await RestoreOriginalsAsync(replaced, isRunning, health, CancellationToken.None);
                    SaveJournal(journalPath, journal with { State = "RolledBack" });
                } catch(Exception recovery) {
                    try { SaveJournal(journalPath, journal with { State = "RollbackRequired" }); } catch { }
                    throw new InvalidOperationException("LaunchBox's multi-file update stopped and automatic rollback could not finish safely. Close LaunchBox, then use Undo with " + journalPath + ". The saved journal identifies every original backup; external edits were preserved.", new AggregateException(failure,recovery));
                }
            }
            throw;
        } finally { DeleteTemps(staged.Select(s => s.TempPath)); }
    }

    static List<LaunchBoxRollbackFile> JournalFiles(LaunchBoxRollback journal) {
        var files = journal.Files?.Count > 0 ? journal.Files : new() { new(journal.SourcePath,journal.BackupPath,journal.BeforeHash,journal.AfterHash) };
        if(files.Count == 0 || files.Select(f => Path.GetFullPath(f.SourcePath)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count)
            throw new InvalidDataException("Invalid or duplicate destinations in the rollback journal.");
        var sources = files.Select(f => Path.GetFullPath(f.SourcePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var file in files) if(sources.Contains(Path.GetFullPath(file.BackupPath)) || file.BeforeHash.Length != 64 || file.AfterHash.Length != 64)
            throw new InvalidDataException("Invalid backup path or hash in the rollback journal.");
        return files;
    }

    // Current files may be either before or after a recorded change, allowing recovery after a crash
    // between replacements. Every destination and backup is checked before the first undo mutation.
    static async Task RestoreOriginalsAsync(IReadOnlyList<LaunchBoxRollbackFile> files, Func<bool> isRunning, VolumeHealthService health, CancellationToken ct) {
        ct.ThrowIfCancellationRequested(); Closed(isRunning);
        var expected = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var file in files) {
            SameHash(file.BackupPath,file.BeforeHash);
            if(!File.Exists(file.SourcePath)) throw new InvalidOperationException("A rollback destination is missing: " + file.SourcePath);
            var current = Hash(file.SourcePath);
            if(!current.Equals(file.BeforeHash,StringComparison.OrdinalIgnoreCase) && !current.Equals(file.AfterHash,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A file changed outside this transaction; rollback was not applied: " + file.SourcePath);
            expected.Add(file.SourcePath,current);
        }
        var restore = files.Where(f => !expected[f.SourcePath].Equals(f.BeforeHash,StringComparison.OrdinalIgnoreCase)).ToList();
        if(restore.Count == 0) return;
        await health.EnsureWritableAsync(restore.Select(f => f.SourcePath), ct);
        var stages = new List<(LaunchBoxRollbackFile File,string BeforeTemp,string CurrentTemp)>(); var restored = new List<(LaunchBoxRollbackFile File,string BeforeTemp,string CurrentTemp)>();
        try {
            foreach(var file in restore) {
                ct.ThrowIfCancellationRequested(); Closed(isRunning); ValidateHashes(expected);
                var token = Guid.NewGuid().ToString("N"); var beforeTemp = file.SourcePath + ".undo-" + token; var currentTemp = file.SourcePath + ".undo-current-" + token;
                stages.Add((file,beforeTemp,currentTemp)); File.Copy(file.BackupPath,beforeTemp,false); File.Copy(file.SourcePath,currentTemp,false);
                SameHash(beforeTemp,file.BeforeHash); SameHash(currentTemp,expected[file.SourcePath]);
            }
            foreach(var stage in stages) {
                ct.ThrowIfCancellationRequested(); Closed(isRunning); await health.EnsureWritableAsync(new[] { stage.File.SourcePath }, ct);
                ValidateHashes(expected); SameHash(stage.File.BackupPath,stage.File.BeforeHash); SameHash(stage.BeforeTemp,stage.File.BeforeHash);
                File.Replace(stage.BeforeTemp,stage.File.SourcePath,null,true); restored.Add(stage); expected[stage.File.SourcePath] = stage.File.BeforeHash;
                SameHash(stage.File.SourcePath,stage.File.BeforeHash);
            }
            Closed(isRunning); ValidateHashes(expected);
        } catch(Exception failure) {
            // If undo itself fails, return files already changed by undo to their exact pre-undo bytes.
            // A process reopening or any external edit blocks this recovery, leaving the original journal usable.
            if(restored.Count > 0) try {
                Closed(isRunning); ValidateHashes(expected);
                await health.EnsureWritableAsync(restored.Select(s => s.File.SourcePath),CancellationToken.None);
                foreach(var stage in restored.AsEnumerable().Reverse()) {
                    Closed(isRunning); ValidateHashes(expected);
                    var previous = Hash(stage.CurrentTemp);
                    if(!previous.Equals(stage.File.AfterHash,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Undo recovery copy is invalid: " + stage.CurrentTemp);
                    File.Replace(stage.CurrentTemp,stage.File.SourcePath,null,true); expected[stage.File.SourcePath] = previous; SameHash(stage.File.SourcePath,previous);
                }
            } catch(Exception recovery) { throw new AggregateException("Undo stopped and could not restore its starting state safely; the original journal and backups remain available.",failure,recovery); }
            throw;
        } finally { DeleteTemps(stages.SelectMany(s => new[] { s.BeforeTemp,s.CurrentTemp })); }
    }

    public static async Task RollbackAsync(string journalPath, Func<bool> isRunning, VolumeHealthService health, CancellationToken ct = default) {
        Closed(isRunning);
        var journal = JsonSerializer.Deserialize<LaunchBoxRollback>(await File.ReadAllTextAsync(journalPath,ct)) ?? throw new InvalidDataException("Invalid LaunchBox rollback journal.");
        var files = JournalFiles(journal);
        await RestoreOriginalsAsync(files,isRunning,health,ct);
        SaveJournal(journalPath,journal with { State = "RolledBack" });
    }
}