using System.Xml.Linq;
using ArcadeLibraryManager.Core;

public static class LaunchBoxEmulatorSetupChecks {
    const string Id = "7672a790-b147-462c-bc74-56be96c41421";
    const string OtherId = "87d344bf-dea3-4b56-9e8e-40bf6993fe4c";
    sealed record Fixture(AppSettings Settings, string Source, string Executable);
    static readonly VolumeHealthService Clean = new((path, ct) => Task.FromResult(new VolumeHealthResult(path, Path.GetPathRoot(path)!, "NTFS", VolumeHealthState.Clean, "Fixture clean volume")));
    static LaunchBoxEmulatorSetupService Service(Func<bool>? running = null) => new(running ?? (() => false), Clean);
    static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    static async Task Rejected(Func<Task> action, string message) {
        try { await action(); } catch (Exception ex) when (ex is InvalidOperationException or VolumeHealthException) { return; }
        throw new InvalidOperationException(message);
    }
    static XElement Emulator(Fixture f, string command = LaunchBoxEmulatorSetupService.CanonicalCommand, string id = Id, bool strip = true, bool noSpace = false, bool noQuotes = false) => new("Emulator",
        new XElement("ID", id), new XElement("Title", "TeknoParrot"), new XElement("ApplicationPath", f.Executable), new XElement("CommandLine", command),
        new XElement("NoQuotes", noQuotes), new XElement("NoSpace", noSpace), new XElement("FileNameWithoutExtensionAndPath", strip), new XElement("HideConsole", true),
        new XElement("AutoHotkeyScript", "WinWait, title\nSend, {Escape}"), new XElement("CustomUnknown", new XElement("Keep", "owner value")));
    static XElement Association(string command = "", string id = Id, string platform = "TeknoParrot", bool isDefault = true) => new("EmulatorPlatform", new XElement("Emulator", id), new XElement("Platform", platform), new XElement("CommandLine", command), new XElement("Default", isDefault), new XElement("CustomAssociation", "retain"));
    static Fixture Create(string root, string name) {
        var directory = Path.Combine(root, name); var lb = Path.Combine(directory, "LaunchBox"); var tp = Path.Combine(directory, "Tekno Parrot");
        Directory.CreateDirectory(Path.Combine(lb, "Data")); Directory.CreateDirectory(Path.Combine(tp, "UserProfiles"));
        var exe = Path.Combine(tp, "TeknoParrotUi.exe"); File.WriteAllBytes(exe, Array.Empty<byte>());
        return new(new AppSettings { LaunchBoxPath = lb, TeknoParrotPath = tp, PlatformName = "TeknoParrot" }, Path.Combine(lb, "Data", "Emulators.xml"), exe);
    }
    static void Save(Fixture f, params XElement[] elements) => new XDocument(new XElement("LaunchBox", elements)).Save(f.Source);
    static XElement ReadEmulator(Fixture f, string id = Id) => XDocument.Load(f.Source).Root!.Elements("Emulator").Single(e => e.Element("ID")!.Value == id);
    public static async Task<List<string>> RunAsync(string root) {
        var passed = new List<string>();
        var service = Service();
        foreach (var variant in new[] { "canonical", "implicit", "fullpath", "filename", "quoted" }) {
            var f = Create(root, "valid-" + variant);
            var command = variant switch { "implicit" => "--profile=", "fullpath" => "--profile=%romfile%", "filename" => "--profile=%romfilename%.xml %noromfile%", "quoted" => "--profile=\"%romfile%.xml\"", _ => LaunchBoxEmulatorSetupService.CanonicalCommand };
            Save(f, Emulator(f, command, strip: variant is not ("implicit" or "fullpath"), noSpace: variant == "implicit", noQuotes: variant == "quoted"));
            var before = SafeFiles.Hash(f.Source); var plan = await service.InspectAsync(f.Settings);
            Check(plan.IsReady && !plan.CanRepair && plan.EmulatorId == Id && plan.Changes.Count == 0, "A working " + variant + " profile command was incorrectly rejected: " + plan.Detail);
            var result = await service.ApplyAsync(plan, f.Settings);
            Check(!result.Changed && SafeFiles.Hash(f.Source) == before && !Directory.Exists(Path.Combine(f.Settings.LaunchBoxPath, "Backups")), "A ready emulator check modified configuration or created a backup.");
        }
        passed.Add("Recognizes canonical, full XML path, implicit --profile= append, filename token and quoted commands without rewriting working settings");

        foreach (var variant in new[] { "missing-nospace", "stripped-implicit", "trailing-space", "uppercase", "unquoted-spaces", "wrong-quotes", "unsupported-variable" }) {
            var f = Create(root, "invalid-" + variant);
            var command = variant switch { "trailing-space" => "--profile= ", "uppercase" => "--PROFILE=", "wrong-quotes" => "--\"profile\"=%romfile%", "unsupported-variable" => "--profile=%ROMNAME%", _ => "--profile=" };
            Save(f, Emulator(f, command, strip: variant == "stripped-implicit", noSpace: variant != "missing-nospace", noQuotes: variant is "unquoted-spaces" or "wrong-quotes"));
            var plan = service.Inspect(f.Settings);
            Check(plan.CanRepair && !plan.IsReady, "Unsafe " + variant + " command was considered ready: " + plan.Detail);
            if (variant == "unsupported-variable") Check(plan.Detail.Contains("%ROMNAME%"), "Unsupported variable diagnosis omitted the literal variable.");
            await service.ApplyAsync(plan, f.Settings);
            Check(service.Inspect(f.Settings).IsReady, "Repair did not produce a ready configuration: " + variant);
        }
        passed.Add("Flags broken implicit append, whitespace, case, unquoted full paths and unsupported ROMNAME, then repairs each to a valid profile command");

        var repair = Create(root, "preservation");
        var owner = new XElement("OwnerMetadata", new XAttribute("version", "future"), new XElement("Keep", "untouched"));
        var other = new XElement("Emulator", new XElement("ID", OtherId), new XElement("Title", "Unrelated"), new XElement("ApplicationPath", "other.exe"), new XElement("CommandLine", "custom flags"));
        var unrelatedAssociation = Association("--custom", OtherId, "Other platform");
        Save(repair, Emulator(repair, "--startMinimized --profile=%ROMNAME% --verbose", strip: false), Association(), other, unrelatedAssociation, owner);
        var original = File.ReadAllBytes(repair.Source); var ownerEmulator = ReadEmulator(repair); var planRepair = service.Inspect(repair.Settings);
        Check(planRepair.CanRepair && planRepair.ProposedCommand == "--startMinimized --profile=%romfile%.xml --verbose", "Repair lost unrelated command flags.");
        var applied = await service.ApplyAsync(planRepair, repair.Settings);
        Check(applied.Changed && File.ReadAllBytes(applied.BackupPath).SequenceEqual(original), "Repair did not retain an exact original backup.");
        var changed = ReadEmulator(repair); var document = XDocument.Load(repair.Source);
        Check(changed.Element("ID")!.Value == Id && XNode.DeepEquals(changed.Element("AutoHotkeyScript"), ownerEmulator.Element("AutoHotkeyScript")) && XNode.DeepEquals(changed.Element("CustomUnknown"), ownerEmulator.Element("CustomUnknown")) && changed.Element("NoSpace")!.Value == "false", "Repair changed ID, scripts, owner fields or unrelated launch flags.");
        Check(XNode.DeepEquals(document.Root!.Element("OwnerMetadata"), owner) && XNode.DeepEquals(document.Root.Elements("Emulator").Single(e => e.Element("ID")!.Value == OtherId), other) && XNode.DeepEquals(document.Root.Elements("EmulatorPlatform").Single(e => e.Element("Emulator")!.Value == OtherId), unrelatedAssociation), "Repair changed unrelated XML records.");
        var repeat = await service.ApplyAsync(planRepair, repair.Settings);
        Check(!repeat.Changed && repeat.BackupPath == applied.BackupPath, "Repeating the same repair was not idempotent.");
        passed.Add("Reviewed repair preserves IDs, scripts, extra flags, unknown XML and siblings, with byte-exact backup and idempotent repeat");

        var platform = Create(root, "platform-override");
        Save(platform, Emulator(platform), Association("--profile=%ROMNAME%"));
        var platformPlan = service.Inspect(platform.Settings);
        Check(platformPlan.CanRepair && platformPlan.CurrentCommand == "--profile=%ROMNAME%" && platformPlan.Changes.Any(c => c.Field == "Platform.CommandLine"), "A working global command hid a broken platform override.");
        await service.ApplyAsync(platformPlan, platform.Settings);
        Check(service.Inspect(platform.Settings).IsReady && ReadEmulator(platform).Element("CommandLine")!.Value == LaunchBoxEmulatorSetupService.CanonicalCommand, "Platform repair changed working global command.");
        Save(platform, Emulator(platform, "--profile=owner.xml --profile=custom.xml"), Association(LaunchBoxEmulatorSetupService.CanonicalCommand));
        Check(service.Inspect(platform.Settings).IsReady, "Unused custom global command overrode the valid associated-platform command.");
        var fullPathGlobal = Emulator(platform, "--profile=%romfile%", strip: false);
        Save(platform, fullPathGlobal, Association("--profile=%ROMNAME%"));
        Check(service.Inspect(platform.Settings).Status == "Needs review", "Repair would silently break the global fullpath command by changing shared filename flags.");
        passed.Add("Checks effective platform override, preserves unused global behavior, and blocks flag changes that would break other commands");

        var create = Create(root, "create"); Save(create, other);
        var createPlan = service.Inspect(create.Settings);
        Check(createPlan.CanRepair && createPlan.EmulatorId.Length > 0 && createPlan.EmulatorId != Id, "Missing emulator did not produce a reviewed creation plan.");
        await service.ApplyAsync(createPlan, create.Settings);
        Check(service.Inspect(create.Settings).IsReady && XDocument.Load(create.Source).Root!.Elements("Emulator").Count() == 2, "New emulator creation replaced existing emulator.");
        var missingFile = Create(root, "missing-xml"); var missingPlan = service.Inspect(missingFile.Settings);
        var createdFile = await service.ApplyAsync(missingPlan, missingFile.Settings);
        Check(createdFile.Changed && createdFile.BackupPath.Length == 0 && service.Inspect(missingFile.Settings).IsReady, "Missing Emulators.xml was not safely created.");
        var wrongPath = Create(root, "wrong-path"); var wrong = Emulator(wrongPath); wrong.Element("ApplicationPath")!.Value = "old-location\\TeknoParrotUi.exe"; Save(wrongPath, wrong);
        var pathPlan = service.Inspect(wrongPath.Settings);
        Check(pathPlan.CanRepair && pathPlan.Changes.Count == 1 && pathPlan.Changes[0].Field == "Emulator.ApplicationPath", "Unique titled emulator path could not be reviewed independently.");
        await service.ApplyAsync(pathPlan, wrongPath.Settings);
        Check(ReadEmulator(wrongPath).Element("ApplicationPath")!.Value == wrongPath.Executable, "Executable path repair did not use the configured installation.");
        passed.Add("Creates missing emulator/XML without replacing other emulators, and previews a unique titled emulator's moved path");

        foreach (var issue in new[] { "duplicate-emulator", "duplicate-id", "duplicate-field", "duplicate-association", "multiple-profile", "malformed", "wrong-root", "dtd", "invalid-boolean", "missing-executable" }) {
            var f = Create(root, "review-" + issue); var emulator = Emulator(f, "--profile=%ROMNAME%");
            if (issue == "duplicate-field") emulator.Add(new XElement("CommandLine", "--profile=owner.xml"));
            if (issue == "multiple-profile") emulator.Element("CommandLine")!.Value = "--profile=%romfile%.xml --profile=owner.xml";
            if (issue == "invalid-boolean") emulator.Element("NoQuotes")!.Value = "banana";
            if (issue == "missing-executable") { emulator.Element("CommandLine")!.Value = LaunchBoxEmulatorSetupService.CanonicalCommand; File.Delete(f.Executable); }
            Save(f, emulator);
            if (issue is "duplicate-emulator" or "duplicate-id") Save(f, emulator, Emulator(f, id: issue == "duplicate-id" ? Id : OtherId));
            if (issue == "duplicate-association") Save(f, emulator, Association(), Association());
            if (issue == "malformed") File.WriteAllText(f.Source, "<LaunchBox><Emulator>");
            if (issue == "wrong-root") File.WriteAllText(f.Source, "<Other />");
            if (issue == "dtd") File.WriteAllText(f.Source, "<!DOCTYPE LaunchBox [<!ENTITY value 'test'>]><LaunchBox>&value;</LaunchBox>");
            var before = SafeFiles.Hash(f.Source); var review = service.Inspect(f.Settings);
            Check(review.Status == "Needs review" && !review.CanRepair && SafeFiles.Hash(f.Source) == before, "Unsafe XML/command did not produce a read-only review: " + issue);
        }
        passed.Add("Malformed XML, DTDs, duplicate scalar fields/IDs/associations, ambiguous emulators and multiple profile arguments require review");

        var duplicates = Create(root, "duplicates-resolved");
        Save(duplicates, Emulator(duplicates), Emulator(duplicates, id: OtherId), Association(id: OtherId));
        Check(service.Inspect(duplicates.Settings).EmulatorId == OtherId, "Unique associated-platform default failed to disambiguate matching executables.");
        Save(duplicates, Emulator(duplicates), Emulator(duplicates, id: OtherId));
        Directory.CreateDirectory(Path.Combine(duplicates.Settings.LaunchBoxPath, "Data", "Platforms"));
        var platformPath = Path.Combine(duplicates.Settings.LaunchBoxPath, "Data", "Platforms", "TeknoParrot.xml");
        new XDocument(new XElement("LaunchBox", new XElement("Game", new XElement("ID", "fixture-game"), new XElement("ApplicationPath", Path.Combine(duplicates.Settings.TeknoParrotPath, "UserProfiles", "fixture.xml")), new XElement("Emulator", OtherId)))).Save(platformPath);
        var usedPlan = service.Inspect(duplicates.Settings);
        Check(usedPlan.IsReady && usedPlan.EmulatorId == OtherId, "Unique emulator referenced by the platform's profile games was not retained.");
        File.AppendAllText(platformPath, "\n<!-- changed -->");
        await Rejected(() => service.ApplyAsync(usedPlan, duplicates.Settings), "Changed emulator-selection evidence was not guarded.");
        passed.Add("Disambiguates exact paths by platform defaults or existing profile-game references and guards selection evidence against changes");

        foreach (var guard in new[] { "running", "dirty", "changed-file", "changed-settings", "missing-exe", "forged-plan" }) {
            var f = Create(root, "guard-" + guard); Save(f, Emulator(f, "--profile=%ROMNAME%")); var preview = service.Inspect(f.Settings);
            var actor = guard == "running" ? Service(() => true) : guard == "dirty" ? new LaunchBoxEmulatorSetupService(() => false, new((path, ct) => Task.FromResult(new VolumeHealthResult(path, Path.GetPathRoot(path)!, "NTFS", VolumeHealthState.Dirty, "Fixture dirty volume")))) : service;
            if (guard == "changed-file") File.AppendAllText(f.Source, "\n<!-- external change -->");
            if (guard == "changed-settings") f.Settings.PlatformName = "Different platform";
            if (guard == "missing-exe") File.Delete(f.Executable);
            if (guard == "forged-plan") preview = new() { Status = "Needs repair", SourcePath = f.Source, Changes = new[] { new LaunchBoxEmulatorFieldChange("CommandLine", "before", "after") } };
            var before = SafeFiles.Hash(f.Source);
            await Rejected(() => actor.ApplyAsync(preview, f.Settings), "Repair guard allowed mutation: " + guard);
            Check(SafeFiles.Hash(f.Source) == before && !Directory.Exists(Path.Combine(f.Settings.LaunchBoxPath, "Backups")), "Rejected repair wrote a file or created a backup: " + guard);
        }
        passed.Add("Running LaunchBox, dirty volumes, stale files/settings, missing executables and forged UI plans block writes before backups");
        return passed;
    }
}
