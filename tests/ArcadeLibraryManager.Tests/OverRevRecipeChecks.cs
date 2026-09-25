using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArcadeLibraryManager.Core;

public static class OverRevRecipeChecks
{
    public static List<string> Run(string root)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "assets", "recipes.json");
        Check(File.Exists(path), "The test output must contain the actual bundled recipe catalog.");
        var catalog = JsonSerializer.Deserialize<List<GameRecipe>>(File.ReadAllText(path), SettingsStore.Json)!;
        var bundled = catalog.Single(recipe => recipe.Id == "overrevb");
        Check(bundled.RomSet == "overrev" && bundled.PrimaryPath == "overrev.zip", "Over Rev must retain the merged container expected by TeknoParrot.");
        Check(bundled.ArchiveItem == "tp-roms_1"
            && bundled.ArchiveName == "TeknoParrot Collection/Over Rev (Model 2B, Rev B) (1997)[Sega Model 2][TP].zip"
            && bundled.ArchiveSize == 15110321
            && bundled.ArchiveSha1.Equals("2a0e7d6ecb1c1df242d094c439c50c67b3bded43", StringComparison.OrdinalIgnoreCase), "Over Rev's verified archive mapping was lost or changed without an audited fixture update.");
        foreach (var dependency in new[] { "overrev", "overrevb", "segabill" })
            Check(bundled.DependencySets.Contains(dependency, StringComparer.OrdinalIgnoreCase), "Over Rev recipe is missing the container/default clone/device source: " + dependency);
        Require(bundled, "epr-19992b.15", 524288, "6d3e78d5", "40d18ee284ea2e038f7e3d04db56e793ab3e3dd5");
        Require(bundled, "epr-19993b.16", 524288, "765dc9ce", "a718c32ca27ec1fb5ed2d7d3797ea7e906510a04");
        Require(bundled, "epr-18022.ic2", 65536, "0ca70f80", "edf5ade72d9fa2f4d5f83f9f89e6cecfadd77f56");
        Require(bundled, "epr-20124a.15", 524288, "74beb8d7", "c65c641138ecd7312c4930702d1498b8a346175a");
        Require(bundled, "epr-20125a.16", 524288, "def64456", "cedb64d2d99a73301ef45c2f5f860a9b87faf6a7");

        // Synthetic content exercises the same parent/clone/device topology without any game ROMs.
        var parent = new[] { Member("epr-20124a.15", "parent program one"), Member("epr-20125a.16", "parent program two"), Member("mpr-shared.11", "shared graphics") };
        var clone = new[] { Member("epr-19992b.15", "default RevB program one"), Member("epr-19993b.16", "default RevB program two") };
        var device = Member("epr-18022.ic2", "shared billboard device");
        var recipe = new GameRecipe {
            Id = bundled.Id, RomSet = bundled.RomSet, PrimaryPath = bundled.PrimaryPath,
            DependencySets = bundled.DependencySets.ToList(),
            Roms = parent.Concat(clone).Append(device).Select(Requirement).ToList()
        };
        var split = Path.Combine(root, "split");
        WriteZip(split, "overrev.zip", parent);
        WriteZip(split, "overrevb.zip", clone);
        WriteZip(split, "segabill.zip", device);
        var merged = Path.Combine(root, "merged");
        var mergedMembers = parent.Concat(clone.Select(member => (Name: "overrevb/" + member.Name, member.Bytes))).ToArray();
        WriteZip(merged, "overrev.zip", mergedMembers);
        WriteZip(merged, "segabill.zip", device);
        var parentOnly = Path.Combine(root, "parent-only");
        WriteZip(parentOnly, "overrev.zip", parent);
        WriteZip(parentOnly, "segabill.zip", device);
        var missingDevice = Path.Combine(root, "missing-device");
        WriteZip(missingDevice, "overrev.zip", mergedMembers);
        var originals = Directory.EnumerateFiles(root, "*.zip", SearchOption.AllDirectories).ToDictionary(file => file, SafeFiles.Hash);

        foreach (var contents in new[] { false, true })
        {
            var splitResult = Validate(recipe, split, contents);
            Check(splitResult.Success && splitResult.ZipPaths.Count == 3, "Split parent/default-clone/device ZIPs failed validation.");
            var mergedResult = Validate(recipe, merged, contents);
            Check(mergedResult.Success && mergedResult.ZipPaths.Count == 2, "Merged default-clone members plus device ZIP failed validation.");
            var parentResult = Validate(recipe, parentOnly, contents);
            Check(!parentResult.Success && clone.All(member => parentResult.Message.Contains(member.Name, StringComparison.OrdinalIgnoreCase)), "Parent-only media was accepted despite missing the profile's default RevB programs.");
            var deviceResult = Validate(recipe, missingDevice, contents);
            Check(!deviceResult.Success && deviceResult.Message.Contains(device.Name, StringComparison.OrdinalIgnoreCase)
                && deviceResult.Message.Contains("segabill.zip", StringComparison.OrdinalIgnoreCase), "Missing shared device was accepted or its required ZIP was not identified.");
        }
        Check(originals.All(pair => SafeFiles.Hash(pair.Key) == pair.Value), "ROM validation changed a source ZIP.");
        return ["Bundled Over Rev recipe retains the verified archive, merged container, default RevB ROM identities and shared device",
            "Synthetic split and merged parent/clone/device sets validate using both directory metadata and full content hashes",
            "Parent-only and missing-device sets are rejected without modifying source media"];
    }

    private static void Require(GameRecipe recipe, string name, long size, string crc, string sha1) =>
        Check(recipe.Roms.Any(rom => rom.Name == name && rom.Size == size && rom.Crc.Equals(crc, StringComparison.OrdinalIgnoreCase)
            && rom.Sha1.Equals(sha1, StringComparison.OrdinalIgnoreCase)), "Missing or incorrect audited ROM identity: " + name);

    private static ValidationResult Validate(GameRecipe recipe, string folder, bool contents) =>
        GameValidation.RomSet(recipe, name => File.Exists(Path.Combine(folder, name)) ? [Path.Combine(folder, name)] : [], contents, default);

    private static (string Name, byte[] Bytes) Member(string name, string content) => (name, Encoding.UTF8.GetBytes("Synthetic fixture: " + content));

    private static RomRequirement Requirement((string Name, byte[] Bytes) member)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        { using var output = zip.CreateEntry(member.Name).Open(); output.Write(member.Bytes); }
        memory.Position = 0;
        using var read = new ZipArchive(memory, ZipArchiveMode.Read);
        return new() { Name = member.Name, Size = member.Bytes.Length, Crc = read.Entries.Single().Crc32.ToString("x8"), Sha1 = Convert.ToHexString(SHA1.HashData(member.Bytes)) };
    }

    private static void WriteZip(string folder, string name, params (string Name, byte[] Bytes)[] members)
    {
        Directory.CreateDirectory(folder);
        using var zip = ZipFile.Open(Path.Combine(folder, name), ZipArchiveMode.Create);
        foreach (var member in members) { using var output = zip.CreateEntry(member.Name).Open(); output.Write(member.Bytes); }
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception("Over Rev recipe check failed: " + message); }
}
