using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using ArcadeLibraryManager.Core;

public static class LaunchBoxTransactionChecks {
    static void Check(bool value,string reason) { if(!value) throw new Exception("LaunchBox transaction check failed: " + reason); }
    static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    static async Task Reject(Func<Task> action,string reason) { try { await action(); } catch(Exception ex) when(ex is InvalidOperationException or IOException or AggregateException) { return; } throw new Exception("Expected transaction rejection: " + reason); }
    sealed class Fixture {
        public string Platform = "", Playlist = "", Root = "";
        public byte[] PlatformBefore = [], PlaylistBefore = [];
        public AppSettings Settings = new(); public GameRecord Legacy = new(), Elf2 = new();
        public Action? ProcessCheck; public bool Running;
        public LaunchBoxService Service = null!;
        public Fixture(string root) {
            Root = root; var lb = Path.Combine(root,"LaunchBox"); var tp = Path.Combine(root,"TeknoParrot");
            foreach(var directory in new[] { Path.Combine(lb,"Data","Platforms"),Path.Combine(lb,"Data","Playlists"),Path.Combine(tp,"UserProfiles"),Path.Combine(tp,"GameProfiles"),Path.Combine(tp,"Metadata") }) Directory.CreateDirectory(directory);
            Settings = new() { LaunchBoxPath = lb,TeknoParrotPath = tp,PlatformName = "TeknoParrot",FillMissingMetadata = false };
            Platform = Path.Combine(lb,"Data","Platforms","TeknoParrot.xml"); Playlist = Path.Combine(lb,"Data","Playlists","Light Gun Games.xml");
            new XElement("LaunchBox",new XElement("Emulator",new XElement("ID","emu"),new XElement("ApplicationPath",Path.Combine(tp,"TeknoParrotUi.exe")),new XElement("CommandLine","--profile=%romfile%"))).Save(Path.Combine(lb,"Data","Emulators.xml"));
            Directory.CreateDirectory(tp); File.WriteAllText(Path.Combine(tp, "TeknoParrotUi.exe"), "inert executable fixture");
            GameRecord Profile(string id,string name,string emulator) {
                var payload = Path.Combine(root,id + ".bin"); File.WriteAllBytes(payload,new byte[] { 1 });
                var user = Path.Combine(tp,"UserProfiles",id + ".xml"); new XElement("GameProfile",new XElement("GamePath",payload)).Save(user);
                new XElement("GameProfile",new XElement("EmulatorType",emulator)).Save(Path.Combine(tp,"GameProfiles",id + ".xml"));
                File.WriteAllText(Path.Combine(tp,"Metadata",id + ".json"),JsonSerializer.Serialize(new { game_name = name }));
                return new() { Id = id,Name = name,Emulator = emulator,UserProfilePath = user,Installed = true,PathsValid = true };
            }
            Legacy = Profile("ExampleGame","Example Game","Lindbergh"); Elf2 = Profile("ExampleGameElf2","Example Game (ElfLoader2)","ElfLdr2");
            XElement Game(string id,GameRecord profile) => new("Game",new XElement("ID",id),new XElement("Title",profile.Name),new XElement("ApplicationPath",profile.UserProfilePath),new XElement("Emulator","emu"),new XElement("CommandLine",""),new XElement("PlayCount",1),new XElement("PlayTime",20),new XElement("Notes","Keep " + id));
            new XDocument(new XElement("LaunchBox",Game("legacy-guid",Legacy),Game("elf2-guid",Elf2))).Save(Platform);
            new XDocument(new XElement("LaunchBox",new XElement("Playlist",new XElement("PlaylistId","playlist-guid"),new XElement("Name","Light Gun Games")),new XElement("PlaylistGame",new XElement("GameId","elf2-guid"),new XElement("GameTitle",Elf2.Name),new XElement("GamePlatform","TeknoParrot"),new XElement("GameFileName",Path.GetFileName(Elf2.UserProfilePath)),new XElement("ManualOrder",7)))).Save(Playlist);
            PlatformBefore = File.ReadAllBytes(Platform); PlaylistBefore = File.ReadAllBytes(Playlist);
            Service = new(() => { ProcessCheck?.Invoke(); return Running; },VolumeHealthChecks.CleanProvider());
        }
        public async Task<LaunchBoxPlan> PreviewAsync() { var plan = await Service.PreviewAsync(Settings,new[] { Elf2 }); Check(plan.Items.Single().Action == "Combine" && plan.CanApply,"fixture offers playlist-aware loader combination"); return plan; }
        public bool PlatformChanged => !File.ReadAllBytes(Platform).SequenceEqual(PlatformBefore);
        public void OriginalFiles() { Check(File.ReadAllBytes(Platform).SequenceEqual(PlatformBefore),"original platform restored byte-for-byte"); Check(File.ReadAllBytes(Playlist).SequenceEqual(PlaylistBefore),"original playlist restored byte-for-byte"); }
        public static string Journal(LaunchBoxPlan plan) => Path.Combine(Path.GetDirectoryName(plan.BackupPath)!,"rollback.json");
        public static LaunchBoxRollback ReadJournal(LaunchBoxPlan plan) => JsonSerializer.Deserialize<LaunchBoxRollback>(File.ReadAllText(Journal(plan)))!;
    }
    public static async Task<object> RunAsync(string root) {
        var success = new Fixture(Path.Combine(root,"success")); var plan = await success.PreviewAsync(); await success.Service.ApplyAsync(plan);
        Check(XDocument.Load(success.Platform).Root!.Elements("Game").Count() == 1,"platform combines to one game");
        Check(XDocument.Load(success.Playlist).Root!.Element("PlaylistGame")!.Element("GameId")!.Value == "legacy-guid","playlist points to retained identity");
        var journal = Fixture.ReadJournal(plan); Check(journal.Files.Count == 2 && journal.State == "Committed","complete multi-file committed journal");
        foreach(var file in journal.Files) Check(Hash(file.BackupPath) == file.BeforeHash && Hash(file.SourcePath) == file.AfterHash,"exact before/after hashes recorded and backups verified");
        var immutableHashes = journal.Files.ToDictionary(f => f.BackupPath,f => Hash(f.BackupPath));
        await success.Service.RollbackAsync(Fixture.Journal(plan)); success.OriginalFiles();
        await success.Service.RollbackAsync(Fixture.Journal(plan)); success.OriginalFiles();
        Check(immutableHashes.All(p => Hash(p.Key) == p.Value),"undo never overwrites original backups");

        var stale = new Fixture(Path.Combine(root,"stale-preview")); var stalePlan = await stale.PreviewAsync(); File.AppendAllText(stale.Playlist,"\n<!-- external -->"); var external = File.ReadAllBytes(stale.Playlist);
        await Reject(() => stale.Service.ApplyAsync(stalePlan),"playlist changed after preview");
        Check(!stale.PlatformChanged && File.ReadAllBytes(stale.Playlist).SequenceEqual(external),"stale preview does not mutate either file");

        var newFile = new Fixture(Path.Combine(root,"new-external-file")); var newPlan = await newFile.PreviewAsync(); new XElement("LaunchBox",new XElement("PlaylistGame",new XElement("GameId","elf2-guid"))).Save(Path.Combine(Path.GetDirectoryName(newFile.Playlist)!,"New.xml"));
        await Reject(() => newFile.Service.ApplyAsync(newPlan),"new XML inventory after preview"); newFile.OriginalFiles();

        var failure = new Fixture(Path.Combine(root,"commit-failure")); var failurePlan = await failure.PreviewAsync(); var fired = false;
        failure.ProcessCheck = () => { if(!fired && failure.PlatformChanged) { fired = true; throw new IOException("Injected failure between XML replacements"); } };
        await Reject(() => failure.Service.ApplyAsync(failurePlan),"second-file failure"); Check(fired,"injection occurred after the first replacement"); failure.OriginalFiles();
        Check(Fixture.ReadJournal(failurePlan).State == "RolledBack","failed commit automatically rolls back and records recovery");

        var edit = new Fixture(Path.Combine(root,"external-edit-during-commit")); var editPlan = await edit.PreviewAsync(); fired = false; byte[] editedPlatform = [];
        edit.ProcessCheck = () => { if(!fired && edit.PlatformChanged) { fired = true; File.AppendAllText(edit.Platform,"\n<!-- preserve this external edit -->"); editedPlatform = File.ReadAllBytes(edit.Platform); } };
        await Reject(() => edit.Service.ApplyAsync(editPlan),"external edit after first replacement");
        Check(fired && File.ReadAllBytes(edit.Platform).SequenceEqual(editedPlatform),"automatic recovery never clobbers external edit");
        Check(File.ReadAllBytes(edit.Playlist).SequenceEqual(edit.PlaylistBefore) && Fixture.ReadJournal(editPlan).State == "RollbackRequired","untouched playlist preserved and mixed transaction recoverable in journal");
        await Reject(() => edit.Service.RollbackAsync(Fixture.Journal(editPlan)),"undo cannot clobber externally edited platform");

        var undo = new Fixture(Path.Combine(root,"undo-preflight")); var undoPlan = await undo.PreviewAsync(); await undo.Service.ApplyAsync(undoPlan); var platformAfter = File.ReadAllBytes(undo.Platform);
        File.AppendAllText(undo.Playlist,"\n<!-- keep external playlist edit -->"); external = File.ReadAllBytes(undo.Playlist);
        await Reject(() => undo.Service.RollbackAsync(Fixture.Journal(undoPlan)),"undo preflights every file before mutations");
        Check(File.ReadAllBytes(undo.Platform).SequenceEqual(platformAfter) && File.ReadAllBytes(undo.Playlist).SequenceEqual(external),"undo refuses all changes when any destination was edited");

        var undoFailure = new Fixture(Path.Combine(root,"undo-failure")); var undoFailurePlan = await undoFailure.PreviewAsync(); await undoFailure.Service.ApplyAsync(undoFailurePlan);
        platformAfter = File.ReadAllBytes(undoFailure.Platform); var playlistAfter = File.ReadAllBytes(undoFailure.Playlist); fired = false;
        undoFailure.ProcessCheck = () => { if(!fired && !undoFailure.PlatformChanged) { fired = true; throw new IOException("Injected failure between undo replacements"); } };
        await Reject(() => undoFailure.Service.RollbackAsync(Fixture.Journal(undoFailurePlan)),"undo failure rolls back completed undo writes");
        Check(fired && File.ReadAllBytes(undoFailure.Platform).SequenceEqual(platformAfter) && File.ReadAllBytes(undoFailure.Playlist).SequenceEqual(playlistAfter),"failed undo restores its exact starting state");
        undoFailure.ProcessCheck = null; await undoFailure.Service.RollbackAsync(Fixture.Journal(undoFailurePlan)); undoFailure.OriginalFiles();

        var crash = new Fixture(Path.Combine(root,"partial-crash")); var crashPlan = await crash.PreviewAsync(); await crash.Service.ApplyAsync(crashPlan); journal = Fixture.ReadJournal(crashPlan);
        var playlistRecord = journal.Files.Single(f => f.SourcePath == crash.Playlist); File.Copy(playlistRecord.BackupPath,playlistRecord.SourcePath,true);
        File.WriteAllText(Fixture.Journal(crashPlan),JsonSerializer.Serialize(journal with { State = "Prepared" }));
        await crash.Service.RollbackAsync(Fixture.Journal(crashPlan)); crash.OriginalFiles();

        var canceled = new Fixture(Path.Combine(root,"canceled-commit")); var canceledPlan = await canceled.PreviewAsync(); using var cts = new CancellationTokenSource(); fired = false;
        canceled.ProcessCheck = () => { if(!fired && canceled.PlatformChanged) { fired = true; cts.Cancel(); } };
        try { await canceled.Service.ApplyAsync(canceledPlan,ct:cts.Token); throw new Exception("Canceled multi-file commit was accepted"); } catch(OperationCanceledException) { }
        Check(fired,"cancellation occurred after first replacement"); canceled.OriginalFiles();
        Check(Fixture.ReadJournal(canceledPlan).State == "RolledBack","cancellation recovery ignores canceled work token");

        var dirty = new Fixture(Path.Combine(root,"dirty-volume")); var dirtyPlan = await dirty.PreviewAsync();
        var dirtyService = new LaunchBoxService(() => false,new VolumeHealthService((path,token) => Task.FromResult(new VolumeHealthResult(path,"fixture","NTFS",VolumeHealthState.Dirty,"Fixture dirty result"))));
        await Reject(() => dirtyService.ApplyAsync(dirtyPlan),"dirty volume guard"); dirty.OriginalFiles();
        var old = Path.Combine(root,"legacy-journal"); Directory.CreateDirectory(old); var oldSource = Path.Combine(old,"TeknoParrot.xml"); var oldBackup = Path.Combine(old,"backup.xml");
        File.WriteAllText(oldSource,"<LaunchBox><Game><ID>after</ID></Game></LaunchBox>"); File.WriteAllText(oldBackup,"<LaunchBox><Game><ID>before</ID></Game></LaunchBox>");
        var oldJournal = Path.Combine(old,"rollback.json"); File.WriteAllText(oldJournal,JsonSerializer.Serialize(new { SourcePath = oldSource,BackupPath = oldBackup,BeforeHash = Hash(oldBackup),AfterHash = Hash(oldSource),CommittedUtc = DateTimeOffset.UtcNow }));
        await LaunchBoxTransaction.RollbackAsync(oldJournal,() => false,VolumeHealthChecks.CleanProvider()); Check(File.ReadAllBytes(oldSource).SequenceEqual(File.ReadAllBytes(oldBackup)),"legacy five-field rollback journals remain compatible");

        var opened = new Fixture(Path.Combine(root,"open-process")); var openPlan = await opened.PreviewAsync(); opened.Running = true;
        await Reject(() => opened.Service.ApplyAsync(openPlan),"open LaunchBox"); opened.OriginalFiles();
        return new { Passed = true, Scenarios = "multi-file commit and undo; immutable backups; exact hashes; stale and new references; injected failure rollback; external-edit preservation; failed undo recovery; partial-crash recovery; legacy journals; open process" };
    }
}
