using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ArcadeLibraryManager.Core;

public static class DependencyInstallChecks
{
    private const string DeviceSet = "fixturedevice";
    private const string DeviceMember = "fixture-boot.bin";
    private static readonly byte[] DeviceBytes = Encoding.UTF8.GetBytes("Synthetic shared device ROM; no arcade content.");

    public static async Task<List<string>> RunAsync(string root)
    {
        var passed = new List<string>();
        var resume = CreateFixture(Path.Combine(root, "resume"), "dep_resume", withDisk: true);
        var archiveHash = SafeFiles.Hash(resume.Archive);
        var templateHash = SafeFiles.Hash(resume.Template);
        var engine = Engine(resume);
        await engine.IndexRomsAsync(default);
        var plan = await engine.PlanAsync([resume.Id], default);
        Check(plan.Single().Action == "Extract local archive", "Missing shared device should leave the verified local game package available.");
        var failed = await engine.ExecuteAsync(plan, default);
        Check(failed.Succeeded == 0 && failed.Errors.Count == 1, "A missing device ROM was registered as a complete game.");
        Check(failed.Errors[0].Contains(DeviceMember, StringComparison.OrdinalIgnoreCase)
            && failed.Errors[0].Contains(DeviceSet + ".zip", StringComparison.OrdinalIgnoreCase), "Missing ROM diagnostics must name the member and dependency ZIP to supply.");
        Check(!File.Exists(resume.UserProfile) && !Directory.Exists(resume.Destination), "Missing dependency committed an incomplete destination/profile.");
        var stagedPrimary = Path.Combine(resume.Stage, "package", "roms", resume.Id + ".zip");
        var stagedDisk = Path.Combine(resume.Stage, "package", "roms", resume.Id, resume.Id + ".chd");
        Check(File.Exists(stagedPrimary) && File.Exists(stagedDisk)
            && File.Exists(Path.Combine(resume.Stage, ".alm-staging-owner.json")), "Failed install did not preserve its owned extraction checkpoint.");
        Check(engine.GetPendingPlan().Single().ProfileId == resume.Id && engine.GetJobs().Single().Stage == "Failed", "Missing dependency failure was not retained for retry.");
        Check(SafeFiles.Hash(resume.Archive) == archiveHash && SafeFiles.Hash(resume.Template) == templateHash, "Failed install changed its source archive or profile template.");

        var device = Path.Combine(resume.Roms, DeviceSet + ".zip");
        File.WriteAllBytes(device, ZipBytes((DeviceMember, DeviceBytes)));
        var deviceHash = SafeFiles.Hash(device);
        engine = Engine(resume);
        await engine.IndexRomsAsync(default);
        var retry = await engine.PlanAsync([resume.Id], default);
        Check(retry.Single().Action == "Use local ROMs", "Fresh preview did not reuse the retained game ZIP after adding its dependency.");
        Check(retry.Single().SecondaryPath.Equals(stagedDisk, StringComparison.OrdinalIgnoreCase), "Retry fixture did not select the CHD inside the retained staging directory.");
        var repaired = await engine.ExecuteAsync(retry, default);
        Check(repaired.Succeeded == 1 && repaired.Errors.Count == 0, "Dependency retry failed: " + string.Join("; ", repaired.Errors));
        var registered = CheckRegistered(resume, withDisk: true);
        var installedDevice = Path.Combine(Path.GetDirectoryName(registered.Primary)!, DeviceSet + ".zip");
        Check(File.Exists(installedDevice) && SafeFiles.Hash(installedDevice) == deviceHash, "Shared device ZIP was not copied beside the installed game ZIP.");
        Check(registered.Secondary.StartsWith(resume.Destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !registered.Secondary.Contains(".alm-staging", StringComparison.OrdinalIgnoreCase), "Retry retained a CHD launch path into the staging directory that was moved.");
        Check(!Directory.Exists(resume.Stage) && File.Exists(Path.Combine(resume.Destination, ".alm-install.json")), "Successful retry did not commit its owned stage.");
        Check(SafeFiles.Hash(resume.Archive) == archiveHash && SafeFiles.Hash(device) == deviceHash && SafeFiles.Hash(resume.Template) == templateHash, "Retry altered source archives or template controls.");
        Check(engine.GetPendingPlan().Count == 0 && engine.GetJobs().Single().Stage == "Complete", "Successful dependency retry remained pending.");
        passed.Add("Missing shared device keeps a durable extraction; fresh indexed retry reuses it, copies the device and relocates the staged CHD");

        foreach (var mismatch in new[] { "crc", "sha" })
        {
            var wrong = CreateFixture(Path.Combine(root, "wrong-" + mismatch), "dep_wrong" + mismatch);
            var wrongDevice = Path.Combine(wrong.Roms, DeviceSet + ".zip");
            var bytes = DeviceBytes.ToArray();
            if (mismatch == "crc") bytes[0] ^= 0xff;
            else
            {
                // Deliberately preserve matching size/CRC while supplying a different expected SHA1.
                // This verifies that execution does not trust the ZIP directory's CRC alone.
                wrong.Recipe.Roms.Single(r => r.Name == DeviceMember).Sha1 = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes("different expected SHA1")));
                SaveRecipe(wrong);
            }
            File.WriteAllBytes(wrongDevice, ZipBytes((DeviceMember, bytes)));
            var sourceHash = SafeFiles.Hash(wrong.Archive);
            var wrongDeviceHash = SafeFiles.Hash(wrongDevice);
            var invalidEngine = Engine(wrong);
            await invalidEngine.IndexRomsAsync(default);
            var invalidPlan = await invalidEngine.PlanAsync([wrong.Id], default);
            var rejected = await invalidEngine.ExecuteAsync(invalidPlan, default);
            Check(rejected.Succeeded == 0 && rejected.Errors.Count == 1 && rejected.Errors[0].Contains(DeviceMember, StringComparison.OrdinalIgnoreCase), "Incorrect shared-device " + mismatch + " passed registration.");
            Check(!File.Exists(wrong.UserProfile) && !Directory.Exists(wrong.Destination), "Incorrect shared-device " + mismatch + " committed the game.");
            Check(File.Exists(Path.Combine(wrong.Stage, ".alm-staging-owner.json")), "Incorrect shared-device failure lost the resumable game extraction.");
            Check(SafeFiles.Hash(wrong.Archive) == sourceHash && SafeFiles.Hash(wrongDevice) == wrongDeviceHash, "Rejecting a bad dependency modified source files.");
        }
        passed.Add("Incorrect shared-device CRC or SHA1 blocks registration without changing source archives");

        var embedded = CreateFixture(Path.Combine(root, "embedded"), "dep_embedded", embeddedDevice: true);
        var embeddedHash = SafeFiles.Hash(embedded.Archive);
        Directory.CreateDirectory(Path.GetDirectoryName(embedded.UserProfile)!);
        var ownerProfile = XDocument.Load(embedded.Template);
        ownerProfile.Root!.Element("GamePath")!.Value = Path.Combine(embedded.Root, "missing-old-game.zip");
        ownerProfile.Root.Element("JoystickButtons")!.Element("ButtonName")!.Value = "EXISTING OWNER CONTROL";
        ownerProfile.Root.Element("Custom")!.Value = "Preserve existing user field";
        ownerProfile.Save(embedded.UserProfile);
        var embeddedEngine = Engine(embedded);
        await embeddedEngine.IndexRomsAsync(default);
        var embeddedPlan = await embeddedEngine.PlanAsync([embedded.Id], default);
        Check(embeddedPlan.Single().Action == "Extract local archive", "Embedded dependency fixture did not choose its local package.");
        var installed = await embeddedEngine.ExecuteAsync(embeddedPlan, default);
        Check(installed.Succeeded == 1 && installed.Errors.Count == 0, "An embedded device ROM incorrectly required a separate dependency ZIP: " + string.Join("; ", installed.Errors));
        CheckRegistered(embedded, withDisk: false, existingOwner: true);
        Check(!Directory.EnumerateFiles(embedded.Root, DeviceSet + ".zip", SearchOption.AllDirectories).Any(), "Embedded-device installation unexpectedly produced a separate device archive.");
        Check(SafeFiles.Hash(embedded.Archive) == embeddedHash, "Embedded-device installation modified its source package.");
        passed.Add("An embedded device ROM installs without a separate ZIP and preserves existing user controls");

        var unowned = CreateFixture(Path.Combine(root, "unowned"), "dep_unowned");
        unowned.Recipe.Roms.Clear();
        unowned.Recipe.DependencySets.Clear();
        unowned.Recipe.RomSet = "";
        SaveRecipe(unowned);
        Directory.CreateDirectory(unowned.Destination);
        var ownerFile = Path.Combine(unowned.Destination, unowned.Id + ".zip");
        File.WriteAllText(ownerFile, "Existing owner files; deliberately not the verified payload.");
        var ownerHash = SafeFiles.Hash(ownerFile);
        var unownedArchiveHash = SafeFiles.Hash(unowned.Archive);
        var unownedEngine = Engine(unowned);
        await unownedEngine.IndexRomsAsync(default);
        var unownedPlan = await unownedEngine.PlanAsync([unowned.Id], default);
        Check(unownedPlan.Single().Action == "Needs review" && !unownedPlan.Single().Selected
            && unownedPlan.Single().Detail.Contains("destination", StringComparison.OrdinalIgnoreCase), "An existing unowned payload should be identified during planning, before a download or extraction is offered.");
        Check(SafeFiles.Hash(ownerFile) == ownerHash && SafeFiles.Hash(unowned.Archive) == unownedArchiveHash
            && !File.Exists(unowned.UserProfile) && !Directory.Exists(unowned.Stage), "Reviewing an existing unowned destination changed its files or created staging/profile data.");
        passed.Add("An existing unowned destination is reported during planning and retained without writes");

        var unownedRoms = CreateFixture(Path.Combine(root, "unowned-local-roms"), "dep_unownedroms");
        SafeFiles.ExtractZip(unownedRoms.Archive, unownedRoms.Roms, default);
        var localMain = Path.Combine(unownedRoms.Roms, "package", "roms", unownedRoms.Id + ".zip");
        var localDevice = Path.Combine(unownedRoms.Roms, DeviceSet + ".zip");
        File.WriteAllBytes(localDevice, ZipBytes((DeviceMember, DeviceBytes)));
        Directory.CreateDirectory(unownedRoms.Destination);
        var localOwner = Path.Combine(unownedRoms.Destination, "owner-file.txt");
        File.WriteAllText(localOwner, "Existing unowned destination; keep this file.");
        var localHashes = new[] { unownedRoms.Archive, localMain, localDevice, localOwner }.ToDictionary(p => p, SafeFiles.Hash);
        var localEngine = Engine(unownedRoms);
        await localEngine.IndexRomsAsync(default);
        var localPlan = await localEngine.PlanAsync([unownedRoms.Id], default);
        Check(localPlan.Single().Action == "Needs review" && !localPlan.Single().Selected
            && localPlan.Single().Detail.Contains("destination", StringComparison.OrdinalIgnoreCase), "Valid local ROMs bypassed the existing unowned destination review guard.");
        Check(localHashes.All(pair => SafeFiles.Hash(pair.Key) == pair.Value)
            && !Directory.Exists(unownedRoms.Stage) && !File.Exists(unownedRoms.UserProfile), "Planning local ROMs changed files or staged into an unowned destination.");
        passed.Add("Valid local game/device ZIPs cannot bypass the unowned destination planning guard");

        var committed = CreateFixture(Path.Combine(root, "already-committed"), "dep_committed", withDisk: true);
        SafeFiles.ExtractZip(committed.Archive, committed.Stage, default);
        File.WriteAllText(Path.Combine(committed.Stage, ".alm-staging-owner.json"), JsonSerializer.Serialize(committed.Recipe));
        var retainedDisk = Path.Combine(committed.Stage, "package", "roms", committed.Id, committed.Id + ".chd");
        Directory.CreateDirectory(committed.Destination);
        var completedMain = Path.Combine(committed.Destination, committed.Id + ".zip");
        File.Copy(Path.Combine(committed.Stage, "package", "roms", committed.Id + ".zip"), completedMain);
        var completedDevice = Path.Combine(committed.Destination, DeviceSet + ".zip");
        File.WriteAllBytes(completedDevice, ZipBytes((DeviceMember, DeviceBytes)));
        var completedMarker = Path.Combine(committed.Destination, ".alm-install.json");
        File.WriteAllText(completedMarker, JsonSerializer.Serialize(committed.Recipe));
        var completedHashes = new[] { committed.Archive, completedMain, completedDevice, completedMarker, retainedDisk }.ToDictionary(p => p, SafeFiles.Hash);
        var committedEngine = Engine(committed);
        await committedEngine.IndexRomsAsync(default);
        var committedPlan = await committedEngine.PlanAsync([committed.Id], default);
        Check(committedPlan.Single().Action == "Use local ROMs"
            && committedPlan.Single().SecondaryPath.Equals(retainedDisk, StringComparison.OrdinalIgnoreCase), "Existing completion marker fixture did not select its still-staged CHD.");
        var committedResult = await committedEngine.ExecuteAsync(committedPlan, default);
        Check(committedResult.Succeeded == 1 && committedResult.Errors.Count == 0, "Existing matching payload failed to reuse its available staged CHD: " + string.Join("; ", committedResult.Errors));
        var committedPaths = CheckRegistered(committed, withDisk: true);
        Check(Directory.Exists(committed.Stage) && committedPaths.Secondary.Equals(retainedDisk, StringComparison.OrdinalIgnoreCase), "CHD path was relocated despite the existing matching destination leaving staging unmoved.");
        Check(!File.Exists(Path.Combine(committed.Destination, "package", "roms", committed.Id, committed.Id + ".chd")), "Fixture unexpectedly supplied the nonexistent relocated CHD path.");
        Check(completedHashes.All(pair => SafeFiles.Hash(pair.Key) == pair.Value), "Reusing a matching completion marker altered existing payload/source files.");
        passed.Add("A matching committed destination leaves staging in place and retains its valid CHD path instead of inventing a moved path");
        return passed;
    }

    private static Fixture CreateFixture(string root, string id, bool withDisk = false, bool embeddedDevice = false)
    {
        var roms = Path.Combine(root, "roms");
        var tp = Path.Combine(root, "tp");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(roms);
        Directory.CreateDirectory(Path.Combine(tp, "GameProfiles"));
        Directory.CreateDirectory(data);
        var template = Path.Combine(tp, "GameProfiles", id + ".xml");
        new XDocument(new XElement("GameProfile", new XElement("GamePath", ""), new XElement("GamePath2", ""),
            new XElement("EmulatorType", "SyntheticTest"), new XElement("ExecutableName", id + ".zip"),
            new XElement("HasTwoExecutables", withDisk ? "true" : "false"),
            new XElement("JoystickButtons", new XElement("ButtonName", "TEMPLATE OWNER CONTROL")),
            new XElement("Custom", "Preserve template field"))).Save(template);
        var mainBytes = Encoding.UTF8.GetBytes("Synthetic game ROM for " + id + "; no arcade content.");
        var members = new List<(string Name, byte[] Bytes)> { ("game.rom", mainBytes) };
        if (embeddedDevice) members.Add((DeviceMember, DeviceBytes));
        var gameZip = ZipBytes(members.ToArray());
        var outerMembers = new List<(string Name, byte[] Bytes)> { ("package/roms/" + id + ".zip", gameZip) };
        var diskSha = SHA1.HashData(Encoding.UTF8.GetBytes("Synthetic CHD identity for " + id));
        if (withDisk)
        {
            var disk = new byte[124];
            Encoding.ASCII.GetBytes("MComprHD").CopyTo(disk, 0);
            BinaryPrimitives.WriteUInt32BigEndian(disk.AsSpan(8, 4), 124);
            BinaryPrimitives.WriteUInt32BigEndian(disk.AsSpan(12, 4), 5);
            diskSha.CopyTo(disk, 84);
            outerMembers.Add(("package/roms/" + id + "/" + id + ".chd", disk));
        }
        var archive = Path.Combine(roms, id + "-package.zip");
        File.WriteAllBytes(archive, ZipBytes(outerMembers.ToArray()));
        var recipe = new GameRecipe {
            Id = id, Name = id, PayloadId = id, RomSet = id, PrimaryPath = id + ".zip",
            SecondaryPath = withDisk ? id + "/" + id + ".chd" : "",
            ArchiveName = Path.GetFileName(archive), ArchiveSize = new FileInfo(archive).Length,
            ArchiveSha1 = Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(archive))),
            DependencySets = [id, DeviceSet],
            Roms = [Requirement("game.rom", mainBytes), Requirement(DeviceMember, DeviceBytes)],
            Disks = withDisk ? [new DiskRequirement { Name = id + ".chd", Sha1 = Convert.ToHexString(diskSha) }] : []
        };
        var fixture = new Fixture(root, id, tp, roms, data, archive, template, recipe,
            new AppSettings { TeknoParrotPath = tp, RomRoots = [roms], DestinationPath = Path.Combine(root, "installed"), CachePath = Path.Combine(root, "cache"), ArchiveUrl = "https://fixture.invalid/no-network" });
        SaveRecipe(fixture);
        return fixture;
    }

    private static (string Primary, string Secondary) CheckRegistered(Fixture fixture, bool withDisk, bool existingOwner = false)
    {
        var profile = XDocument.Load(fixture.UserProfile).Root!;
        var primary = profile.Element("GamePath")!.Value;
        var secondary = profile.Element("GamePath2")!.Value;
        Check(File.Exists(primary), "Registered game ZIP is missing.");
        if (withDisk) Check(File.Exists(secondary), "Registered CHD is missing after the staging directory moved.");
        Check(profile.Element("JoystickButtons")!.Element("ButtonName")!.Value == (existingOwner ? "EXISTING OWNER CONTROL" : "TEMPLATE OWNER CONTROL"), "Dependency installation changed user controls.");
        Check(profile.Element("Custom")!.Value == (existingOwner ? "Preserve existing user field" : "Preserve template field"), "Dependency installation changed unrelated profile data.");
        return (primary, secondary);
    }

    private static RomRequirement Requirement(string name, byte[] bytes)
    {
        using var stream = new MemoryStream(ZipBytes((name, bytes)));
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        return new() { Name = name, Size = bytes.Length, Crc = zip.Entries.Single().Crc32.ToString("x8"), Sha1 = Convert.ToHexString(SHA1.HashData(bytes)) };
    }

    private static byte[] ZipBytes(params (string Name, byte[] Bytes)[] files)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var file in files) { using var output = zip.CreateEntry(file.Name).Open(); output.Write(file.Bytes); }
        return memory.ToArray();
    }

    private static AppEngine Engine(Fixture fixture) => new(fixture.Settings, fixture.Data, volumeHealth: VolumeHealthChecks.CleanProvider());
    private static void SaveRecipe(Fixture fixture) => File.WriteAllText(Path.Combine(fixture.Data, "recipes.json"), JsonSerializer.Serialize(new[] { fixture.Recipe }));
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed record Fixture(string Root, string Id, string Tp, string Roms, string Data, string Archive, string Template, GameRecipe Recipe, AppSettings Settings)
    {
        public string Destination => Path.Combine(Settings.DestinationPath, Id);
        public string Stage => Destination + ".alm-staging";
        public string UserProfile => Path.Combine(Tp, "UserProfiles", Id + ".xml");
    }
}
