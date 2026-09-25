using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ArcadeLibraryManager.Core;

public static class MissingArchiveMappingChecks
{
    private const string MappingNotice = "No verified download mapping is configured for this game; nothing has been downloaded by this plan.";

    public static async Task<List<string>> RunAsync(string root)
    {
        const string id = "unmapped_fixture";
        var tp = Path.Combine(root, "tp");
        var roms = Path.Combine(root, "roms");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(Path.Combine(tp, "GameProfiles"));
        Directory.CreateDirectory(roms);
        Directory.CreateDirectory(data);
        var template = Path.Combine(tp, "GameProfiles", id + ".xml");
        new XDocument(new XElement("GameProfile", new XElement("GamePath", ""), new XElement("GamePath2", ""),
            new XElement("EmulatorType", "SyntheticTest"), new XElement("ExecutableName", id + ".zip"),
            new XElement("HasTwoExecutables", "false"), new XElement("JoystickButtons", "OWNER CONTROL"),
            new XElement("Custom", "Keep fixture metadata"))).Save(template);
        var firstBytes = Encoding.UTF8.GetBytes("First synthetic ROM; no arcade content.");
        var missingBytes = Encoding.UTF8.GetBytes("Second synthetic ROM; no arcade content.");
        var archive = Path.Combine(roms, id + ".zip");
        File.WriteAllBytes(archive, ZipBytes(("first.rom", firstBytes)));
        var unrelated = Path.Combine(roms, "owner-file.txt");
        File.WriteAllText(unrelated, "Unrelated existing owner file.");
        var recipe = new GameRecipe {
            Id = id, Name = id, PayloadId = id, RomSet = id, PrimaryPath = id + ".zip",
            DependencySets = [id], Roms = [Requirement("first.rom", firstBytes), Requirement("missing.rom", missingBytes)]
        };
        var recipePath = Path.Combine(data, "recipes.json");
        File.WriteAllText(recipePath, JsonSerializer.Serialize(new[] { recipe }));
        // An unsupported local-only URI fails before HTTP if archive discovery is accidentally reached.
        var settings = new AppSettings {
            TeknoParrotPath = tp, RomRoots = [roms], DestinationPath = Path.Combine(root, "installed"),
            CachePath = Path.Combine(root, "cache"), ArchiveUrl = "offline-fixture:unmapped"
        };
        var events = new Events();
        var engine = new AppEngine(settings, data, events, VolumeHealthChecks.CleanProvider());
        var hashes = new[] { template, archive, unrelated, recipePath }.ToDictionary(p => p, SafeFiles.Hash);
        await engine.IndexRomsAsync(default);
        var expectedValidation = GameValidation.RomSet(recipe, name => name == id + ".zip" ? [archive] : [], false, default);
        Check(!expectedValidation.Success && expectedValidation.Message.Contains("missing.rom"), "Missing-member fixture unexpectedly validates.");
        var missingPlan = await engine.PlanAsync([id], default);
        var missing = missingPlan.Single();
        Check(missing.Action == "Needs review" && !missing.Selected, "An unmapped incomplete game must remain unchecked and need review.");
        Check(missing.DownloadBytes == 0 && missing.DownloadUrl == "" && missing.ArchivePath == "" && missing.PrimaryPath == "", "An unmapped incomplete game advertised a download or source.");
        Check(missing.Detail.StartsWith(MappingNotice, StringComparison.Ordinal)
            && missing.Detail.Contains(expectedValidation.Message, StringComparison.Ordinal), "The plan must explain the absent mapping and preserve the complete ROM validation diagnostic.");
        var skipped = await engine.ExecuteAsync(missingPlan, default);
        Check(skipped.Succeeded == 0 && skipped.Errors.Count == 0 && engine.GetJobs().Count == 0, "An unchecked needs-review plan must not start an install job.");
        Check(hashes.All(pair => SafeFiles.Hash(pair.Key) == pair.Value)
            && !Directory.Exists(settings.DestinationPath) && !Directory.Exists(settings.CachePath)
            && !Directory.Exists(Path.Combine(tp, "UserProfiles")), "Planning or executing an unchecked unmapped game changed existing files or created install data.");

        // The same unmapped recipe remains installable once its verified local ZIP is complete.
        File.WriteAllBytes(archive, ZipBytes(("first.rom", firstBytes), ("missing.rom", missingBytes)));
        var completeSourceHash = SafeFiles.Hash(archive);
        await engine.IndexRomsAsync(default);
        var localPlan = await engine.PlanAsync([id], default);
        var local = localPlan.Single();
        Check(local.Action == "Use local ROMs" && local.Selected && local.PrimaryPath.Equals(archive, StringComparison.OrdinalIgnoreCase), "Missing online mapping incorrectly blocks complete local ROMs.");
        Check(local.DownloadBytes == 0 && local.DownloadUrl == "" && local.ArchivePath == "", "Complete local ROMs incorrectly advertised an online download.");
        var installed = await engine.ExecuteAsync(localPlan, default);
        Check(installed.Succeeded == 1 && installed.Errors.Count == 0, "Complete local unmapped ROMs failed to install: " + string.Join("; ", installed.Errors));
        var profile = XDocument.Load(Path.Combine(tp, "UserProfiles", id + ".xml")).Root!;
        var registered = profile.Element("GamePath")!.Value;
        Check(File.Exists(registered) && SafeFiles.Hash(registered) == completeSourceHash, "Local unmapped installation registered a missing or different game ZIP.");
        Check(profile.Element("JoystickButtons")!.Value == "OWNER CONTROL" && profile.Element("Custom")!.Value == "Keep fixture metadata", "Local installation changed controls or unrelated template fields.");
        Check(SafeFiles.Hash(archive) == completeSourceHash && SafeFiles.Hash(template) == hashes[template]
            && SafeFiles.Hash(unrelated) == hashes[unrelated] && !Directory.Exists(settings.CachePath), "Local installation changed source files or created a download cache.");
        Check(!events.Items.Any(e => e.Stage is "Archive catalog" or "Downloading"), "An unmapped local-only fixture attempted archive discovery or downloading.");

        return [
            "Unmapped incomplete ROMs show a clear no-download notice plus full validation details, remain unchecked with no source/download, and preserve existing files",
            "The same unmapped recipe installs from a complete verified local ZIP, preserves source bytes and profile controls, and never discovers or downloads an online archive"
        ];
    }

    private static RomRequirement Requirement(string name, byte[] bytes)
    {
        using var memory = new MemoryStream(ZipBytes((name, bytes)));
        using var zip = new ZipArchive(memory, ZipArchiveMode.Read);
        return new() { Name = name, Size = bytes.Length, Crc = zip.Entries.Single().Crc32.ToString("x8"), Sha1 = Convert.ToHexString(SHA1.HashData(bytes)) };
    }

    private static byte[] ZipBytes(params (string Name, byte[] Bytes)[] members)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var member in members) { using var output = zip.CreateEntry(member.Name).Open(); output.Write(member.Bytes); }
        return memory.ToArray();
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class Events : IProgress<JobEvent>
    {
        internal List<JobEvent> Items { get; } = [];
        public void Report(JobEvent value) => Items.Add(value);
    }
}
