using ArcadeLibraryManager.Core;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public static class UpdaterChecks
{
    public static async Task<List<string>> RunAsync(string fixtureRoot)
    {
        var passed = new List<string>();
        var root = Path.Combine(fixtureRoot, "updates-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var fixture = await CreateFixture(Path.Combine(root, "successful"));
        var updater = new UpdaterService(fixture.Settings, fixture.Data, httpClient: fixture.Http, processRunning: () => false, healthService: CleanHealth());
        var updates = await updater.CheckAsync();
        Require(updates.Count == 34 && updates.Single(u => u.Component == "TeknoParrotUI").NeedsUpdate, "Exact official component manifest was not parsed");
        var result = await updater.ApplyAsync(updates);
        Require(result.Succeeded == 1 && File.ReadAllText(Path.Combine(fixture.Install, "TeknoParrotUi.exe")) == "new binary", "Fixture update did not commit");
        Require(File.ReadAllText(Path.Combine(fixture.Install, "UserProfiles", "kept.xml")) == "custom controls", "User profile overwritten");
        Require(File.ReadAllText(Path.Combine(fixture.Install, "ParrotData.xml")) == "custom account settings", "Global settings overwritten");
        Require(File.ReadAllText(Path.Combine(fixture.Install, "RPCS3", "TeknoParrot", "save.dat")) == "custom save", "Emulator save overwritten");
        Require(File.ReadAllText(Path.Combine(fixture.Install, "TeknoParrot.ini")) == "custom ini", "INI overwritten");
        passed.Add("Updater accepts official SHA256 ZIPs and preserves controls, global settings, INI and emulator saves");
        var journal = Directory.EnumerateFiles(Path.Combine(fixture.Data, "Updates"), "transaction.json", SearchOption.AllDirectories).Single();
        await updater.RollbackAsync(journal);
        Require(File.ReadAllText(Path.Combine(fixture.Install, "TeknoParrotUi.exe")) == "old binary" && !File.Exists(Path.Combine(fixture.Install, "new.dll")), "Rollback did not restore prior files/remove only additions");
        passed.Add("Update rollback restores verified originals and removes app-owned additions");
        fixture = await CreateFixture(Path.Combine(root, "fault"));
        var threw = false;
        updater = new UpdaterService(fixture.Settings, fixture.Data, new ImmediateProgress(e => { if (e.Stage == "Updating") throw new IOException("Injected fixture commit failure"); }), fixture.Http, () => false, CleanHealth());
        try { await updater.ApplyAsync(await updater.CheckAsync()); } catch (IOException) { threw = true; }
        Require(threw && File.ReadAllText(Path.Combine(fixture.Install, "TeknoParrotUi.exe")) == "old binary" && !File.Exists(Path.Combine(fixture.Install, "new.dll")), "Failed batch left installed changes");
        passed.Add("A failure after replacement automatically rolls the batch back");
        fixture = await CreateFixture(Path.Combine(root, "concurrent"));
        updater = new UpdaterService(fixture.Settings, fixture.Data, new ImmediateProgress(e => { if (e.Stage == "Update backup") File.WriteAllText(Path.Combine(fixture.Install, "TeknoParrotUi.exe"), "external edit"); }), fixture.Http, () => false, CleanHealth());
        threw = false; try { await updater.ApplyAsync(await updater.CheckAsync()); } catch (IOException) { threw = true; }
        Require(threw && File.ReadAllText(Path.Combine(fixture.Install, "TeknoParrotUi.exe")) == "external edit", "Concurrent user edit was overwritten");
        passed.Add("Concurrent edits after backup stop update and remain untouched");
        fixture = await CreateFixture(Path.Combine(root, "busy")); updater = new UpdaterService(fixture.Settings, fixture.Data, httpClient: fixture.Http, processRunning: () => true, healthService: CleanHealth());
        threw = false; try { await updater.ApplyAsync(await updater.CheckAsync()); } catch (InvalidOperationException) { threw = true; }
        Require(threw && File.ReadAllText(Path.Combine(fixture.Install, "TeknoParrotUi.exe")) == "old binary", "Running emulator update was allowed");
        passed.Add("Running emulator defers binary replacement");
        fixture = await CreateFixture(Path.Combine(root, "digest"));
        var package = Directory.EnumerateFiles(Path.Combine(fixture.Data, "Updates", "Downloads"), "*.zip").Single(); await File.AppendAllTextAsync(package, "corrupt");
        updater = new UpdaterService(fixture.Settings, fixture.Data, httpClient: fixture.Http, processRunning: () => false, healthService: CleanHealth());
        threw = false; try { await updater.ApplyAsync(await updater.CheckAsync()); } catch (InvalidDataException) { threw = true; }
        Require(threw && File.ReadAllText(Path.Combine(fixture.Install, "TeknoParrotUi.exe")) == "old binary", "Corrupted release was installed");
        passed.Add("Corrupt release ZIP is rejected before any installation write");
        fixture = await CreateFixture(Path.Combine(root, "recovery")); updater = new UpdaterService(fixture.Settings, fixture.Data, httpClient: fixture.Http, processRunning: () => false, healthService: CleanHealth());
        await updater.ApplyAsync(await updater.CheckAsync());
        journal = Directory.EnumerateFiles(Path.Combine(fixture.Data, "Updates"), "transaction.json", SearchOption.AllDirectories).Single();
        var interrupted = JsonSerializer.Deserialize<UpdateTransaction>(File.ReadAllText(journal), SettingsStore.Json)!; interrupted.State = "Committing"; File.WriteAllText(journal, JsonSerializer.Serialize(interrupted, SettingsStore.Json));
        Require(updater.FindInterruptedTransactions().Single() == journal, "Interrupted journal was not detected");
        await updater.RollbackAsync(journal);
        Require(File.ReadAllText(Path.Combine(fixture.Install, "TeknoParrotUi.exe")) == "old binary", "Interrupted update recovery failed");
        passed.Add("Interrupted commit journals remain discoverable and recoverable");
        fixture = await CreateFixture(Path.Combine(root, "missing-digest"));
        var digestManifest = System.Text.Json.Nodes.JsonNode.Parse(fixture.Manifest)!;
        digestManifest[0]!["release"]!["assets"]![0]!["digest"] = null;
        updater = new UpdaterService(fixture.Settings, fixture.Data, httpClient: new HttpClient(new FixtureHandler(digestManifest.ToJsonString())), processRunning: () => false, healthService: CleanHealth());
        updates = await updater.CheckAsync();
        Require(updates.Single(u => u.Component == "TeknoParrotUI").Status.Contains("published SHA256 missing"), "Missing digest was not classified per component");
        threw = false; try { await updater.ApplyAsync(updates); } catch (InvalidDataException) { threw = true; }
        Require(threw && File.ReadAllText(Path.Combine(fixture.Install, "TeknoParrotUi.exe")) == "old binary", "Missing digest update was allowed");
        passed.Add("Missing published digest is visible per component and cannot be installed");
        var publisherManifest = System.Text.Json.Nodes.JsonNode.Parse(fixture.Manifest)!;
        publisherManifest[0]!["release"]!["assets"]![0]!["browser_download_url"] = "https://github.com/untrusted-publisher/anything/release.zip";
        updater = new UpdaterService(fixture.Settings, fixture.Data, httpClient: new HttpClient(new FixtureHandler(publisherManifest.ToJsonString())), processRunning: () => false, healthService: CleanHealth());
        updates = await updater.CheckAsync();
        Require(updates.Count == 34 && updates.Single(u => u.Component == "TeknoParrotUI").Status.StartsWith("Unavailable:") && !updates.Single(u => u.Component == "TeknoParrotUI").NeedsUpdate, "Unknown publisher was not isolated and blocked");
        passed.Add("Unsupported publisher is isolated to its component row instead of aborting the scan");
        publisherManifest[0]!["component"] = "RPCS3";
        publisherManifest[0]!["release"]!["assets"]![0]!["browser_download_url"] = "https://github.com/ReaverTeknoGods/rpcs3/releases/download/rpcs3x6-windows/RPCS3_1.0.0.8.zip";
        updater = new UpdaterService(fixture.Settings, fixture.Data, httpClient: new HttpClient(new FixtureHandler(publisherManifest.ToJsonString())), processRunning: () => false, healthService: CleanHealth());
        updates = await updater.CheckAsync();
        Require(updates.Single(u => u.Component == "RPCS3").Status == "Verified update available", "The official RPCS3 publisher was rejected");
        passed.Add("RPCS3's distinct official publisher is recognized explicitly");
        var blockedData = Path.Combine(root, "must-not-create");
        var dirtyVolume = new VolumeHealthService((path, token) => Task.FromResult(new VolumeHealthResult(path, Path.GetPathRoot(path)!, "NTFS", VolumeHealthState.Dirty, "Fixture dirty flag")));
        var healthBlockedUpdater = new UpdaterService(fixture.Settings, blockedData, httpClient: fixture.Http, processRunning: () => false, healthService: dirtyVolume);
        threw = false; try { await healthBlockedUpdater.ApplyAsync(updates); } catch (VolumeHealthException) { threw = true; }
        Require(threw && !Directory.Exists(blockedData), "Dirty volume was written before update readiness was checked");
        threw = false; try { await healthBlockedUpdater.RollbackAsync(Path.Combine(blockedData,"Updates","transaction.json")); } catch (VolumeHealthException) { threw = true; }
        Require(threw && !Directory.Exists(blockedData), "Dirty volume was written during rollback");
        passed.Add("Dirty volumes block update staging and rollback before any write");
        return passed;
    }
    private static async Task<Fixture> CreateFixture(string root)
    {
        var install = Path.Combine(root, "emulator"); var data = Path.Combine(root, "appdata"); Directory.CreateDirectory(install);
        foreach (var (path, value) in new[] { ("TeknoParrotUi.exe", "old binary"), ("UserProfiles/kept.xml", "custom controls"), ("ParrotData.xml", "custom account settings"), ("RPCS3/TeknoParrot/save.dat", "custom save"), ("TeknoParrot.ini", "custom ini") }) { var target = Path.Combine(install, path); Directory.CreateDirectory(Path.GetDirectoryName(target)!); await File.WriteAllTextAsync(target, value); }
        var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
            foreach (var (path, value) in new[] { ("TeknoParrotUi.exe", "new binary"), ("new.dll", "new addition"), ("GameProfiles/new.xml", "updated template"), ("UserProfiles/kept.xml", "bad default"), ("ParrotData.xml", "bad default"), ("RPCS3/TeknoParrot/save.dat", "bad default"), ("TeknoParrot.ini", "bad default") }) { using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false)); writer.Write(value); }
        var content = bytes.ToArray(); var digest = Convert.ToHexString(SHA256.HashData(content));
        var cache = Path.Combine(data, "Updates", "Downloads"); Directory.CreateDirectory(cache); await File.WriteAllBytesAsync(Path.Combine(cache, "TeknoParrotUI-1.0.0.9999-" + digest[..12] + ".zip"), content);
        var manifest = JsonSerializer.Serialize(new[] { new { component = "TeknoParrotUI", release = new { name = "1.0.0.9999", assets = new[] { new { name = "TeknoParrotUi.zip", browser_download_url = "https://github.com/teknogods/TeknoParrotUI/releases/download/TeknoParrotUI/TeknoParrotUi.zip", size = content.Length, digest = "sha256:" + digest } } } } });
        return new(new AppSettings { TeknoParrotPath = install }, install, data, new HttpClient(new FixtureHandler(manifest)), manifest);
    }
    private static VolumeHealthService CleanHealth() => new((path, token) => Task.FromResult(new VolumeHealthResult(path, Path.GetPathRoot(path)!, "NTFS", VolumeHealthState.Clean, "Isolated fixture provider")));
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed record Fixture(AppSettings Settings, string Install, string Data, HttpClient Http, string Manifest);
    private sealed class FixtureHandler(string manifest) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(manifest) }); }
    private sealed class ImmediateProgress(Action<JobEvent> action) : IProgress<JobEvent> { public void Report(JobEvent value) => action(value); }
}



