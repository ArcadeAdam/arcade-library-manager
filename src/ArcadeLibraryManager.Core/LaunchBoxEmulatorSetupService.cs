using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ArcadeLibraryManager.Core;

public sealed record LaunchBoxEmulatorFieldChange(string Field, string Before, string After);
public sealed class LaunchBoxEmulatorSetupPlan {
    public string Status { get; init; } = "Needs review";
    public string Detail { get; init; } = "";
    public string EmulatorName { get; init; } = "";
    public string EmulatorId { get; init; } = "";
    public string CurrentCommand { get; init; } = "";
    public string ProposedCommand { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public IReadOnlyList<LaunchBoxEmulatorFieldChange> Changes { get; init; } = Array.Empty<LaunchBoxEmulatorFieldChange>();
    public bool IsReady => Status == "Ready";
    public bool CanRepair => Status == "Needs repair" && Changes.Count > 0;
    internal string Identity { get; init; } = "";
    internal string OriginalHash { get; init; } = "";
    internal bool OriginalExists { get; init; }
    internal XDocument? ProposedDocument { get; init; }
    internal string AppliedHash { get; set; } = "";
    internal string BackupPath { get; set; } = "";
    internal IReadOnlyDictionary<string, string> RelatedHashes { get; init; } = new Dictionary<string, string>();
}
public sealed record LaunchBoxEmulatorSetupResult(bool Changed, string Detail, string BackupPath);

/// <summary>Read-only detection and a separately reviewed, single-file emulator configuration repair.</summary>
public sealed class LaunchBoxEmulatorSetupService {
    public const string CanonicalCommand = "--profile=%romfile%.xml";
    static readonly SemaphoreSlim Gate = new(1, 1);
    readonly Func<bool> isRunning;
    readonly VolumeHealthService health;
    public LaunchBoxEmulatorSetupService(Func<bool>? processRunning = null, VolumeHealthService? health = null) {
        isRunning = processRunning ?? LaunchBoxService.IsLaunchBoxRunning;
        this.health = health ?? new();
    }
    static string Value(XElement? e, string name) => LaunchBoxService.Value(e, name);
    static bool Flag(XElement? e, string name) => Value(e, name).Equals("true", StringComparison.OrdinalIgnoreCase);
    static string Full(string value) => Path.GetFullPath(value.Trim().Trim('"'));
    static string Identity(AppSettings s) => string.Join("\n", Full(s.LaunchBoxPath).TrimEnd('\\', '/'), Full(s.TeknoParrotPath).TrimEnd('\\', '/'), s.PlatformName.Trim());
    static string Executable(AppSettings s) => Path.Combine(Full(s.TeknoParrotPath), "TeknoParrotUi.exe");
    static string ApplicationPath(AppSettings s, XElement e) {
        var value = Value(e, "ApplicationPath").Trim().Trim('"');
        if (value.Length == 0) return "";
        try { return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(Full(s.LaunchBoxPath), value)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return ""; }
    }
    static void Scalars(XElement element) {
        if (element.Elements().GroupBy(e => e.Name).Any(g => g.Count() > 1))
            throw new InvalidDataException("The selected emulator or platform association contains duplicate fields; review its XML before changing it.");
        foreach (var field in new[] { "ID", "Title", "ApplicationPath", "CommandLine", "NoQuotes", "NoSpace", "FileNameWithoutExtensionAndPath", "Emulator", "Platform", "Default" })
            if (element.Element(field)?.HasElements == true) throw new InvalidDataException("The selected emulator field is not a scalar value: " + field);
        foreach (var field in new[] { "NoQuotes", "NoSpace", "FileNameWithoutExtensionAndPath", "Default" }) {
            var value = Value(element, field);
            if (!string.IsNullOrEmpty(value) && !bool.TryParse(value, out _)) throw new InvalidDataException("The selected emulator field is not a valid true/false value: " + field);
        }
    }

    static List<XElement> Associations(XElement root, string id, string platform) => root.Elements("EmulatorPlatform")
        .Where(e => Value(e, "Emulator").Trim().Equals(id, StringComparison.OrdinalIgnoreCase) && Value(e, "Platform").Trim().Equals(platform.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
    static XElement Select(XElement root, List<XElement> candidates, AppSettings settings, Dictionary<string, string> related) {
        var platform = settings.PlatformName;
        if (candidates.Count == 1) return candidates[0];
        var defaults = candidates.Where(e => Associations(root, Value(e, "ID").Trim(), platform).Any(a => Flag(a, "Default"))).ToList();
        if (defaults.Count == 1) return defaults[0];
        if (defaults.Count == 0) {
            var associated = candidates.Where(e => Associations(root, Value(e, "ID").Trim(), platform).Count > 0).ToList();
            if (associated.Count == 1) return associated[0];
        }
        if (defaults.Count > 1) throw new InvalidDataException("Multiple TeknoParrot emulators are marked as the default for this platform. Review them in LaunchBox.");
        var platformPath = LaunchBoxService.PlatformFile(settings);
        if (File.Exists(platformPath)) {
            var hash = LaunchBoxService.Hash(platformPath); var games = LaunchBoxService.ReadXml(platformPath);
            if (games.Root?.Name != "LaunchBox" || LaunchBoxService.Hash(platformPath) != hash) throw new InvalidDataException("The LaunchBox platform XML changed or is invalid; detect again.");
            related[platformPath] = hash;
            var used = games.Root.Elements().Where(e => e.Name == "Game" || e.Name == "AdditionalApplication")
                .Where(e => LaunchBoxService.ProfileId(e, settings.LaunchBoxPath, settings.TeknoParrotPath) != null)
                .Select(e => Value(e, e.Name == "Game" ? "Emulator" : "EmulatorId").Trim()).Where(id => id.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var referenced = candidates.Where(e => used.Contains(Value(e, "ID").Trim())).ToList();
            if (referenced.Count == 1) return referenced[0];
        }
        throw new InvalidDataException("More than one TeknoParrot emulator matches this setup. Choose one unambiguous associated-platform default in LaunchBox before repairing it.");
    }
    readonly record struct Token(int Start, int Length, string Text);
    static List<Token> Tokens(string command) {
        if (command.IndexOfAny(new[] { '\r', '\n', '\0', '&', '|', '<', '>' }) >= 0 || command.Contains("\\\"", StringComparison.Ordinal))
            throw new InvalidDataException("The command contains custom syntax that cannot be changed safely; review it in LaunchBox.");
        var result = new List<Token>();
        for (var i = 0; i < command.Length;) {
            if (char.IsWhiteSpace(command[i])) { i++; continue; }
            var start = i; var quoted = false;
            while (i < command.Length && (quoted || !char.IsWhiteSpace(command[i]))) { if (command[i] == '"') quoted = !quoted; i++; }
            if (quoted) throw new InvalidDataException("The command has unmatched quotation marks; review it in LaunchBox.");
            result.Add(new(start, i - start, command[start..i]));
        }
        return result;
    }
    static string Unquoted(string value) => value.Replace("\"", "", StringComparison.Ordinal);
    static bool CommandWorks(string command, XElement emulator, string executable) {
        var tokens = Tokens(command);
        var profiles = tokens.Where(t => Unquoted(t.Text).StartsWith("--profile", StringComparison.OrdinalIgnoreCase)).ToList();
        if (profiles.Count > 1) throw new InvalidDataException("The command contains multiple profile arguments; review which one should be used in LaunchBox.");
        if (profiles.Count == 0) return false;
        var profile = profiles[0]; var text = Unquoted(profile.Text);
        if (!text.StartsWith("--profile=", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The profile argument uses custom syntax; review it in LaunchBox.");
        if (!text.StartsWith("--profile=", StringComparison.Ordinal)) return false;
        var argument = text[10..];
        var otherTokens = tokens.Where(t => t.Start != profile.Start).ToList();
        if (otherTokens.Any(t => t.Text.Contains("%romfile%", StringComparison.OrdinalIgnoreCase) || t.Text.Contains("%romfilename%", StringComparison.OrdinalIgnoreCase) || t.Text.Contains("%ROMNAME%", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("ROM placeholders also occur outside the profile argument; review this custom command in LaunchBox.");
        var strip = Flag(emulator, "FileNameWithoutExtensionAndPath");
        var hasPathSpaces = Path.GetDirectoryName(executable)!.Any(char.IsWhiteSpace);
        var explicitlyQuotedValue = profile.Text.Equals("--profile=\"%romfile%\"", StringComparison.OrdinalIgnoreCase) || profile.Text.Equals("\"--profile=%romfile%\"", StringComparison.OrdinalIgnoreCase);
        var quotesSafe = !hasPathSpaces || (Flag(emulator, "NoQuotes") ? explicitlyQuotedValue : !profile.Text.Contains('"'));
        if (argument.Length == 0)
            return profile.Start == tokens.Last().Start && profile.Start + profile.Length == command.Length && Flag(emulator, "NoSpace") && !strip && (!Flag(emulator, "NoQuotes") || !Path.GetDirectoryName(executable)!.Any(char.IsWhiteSpace)) && !otherTokens.Any(t => Unquoted(t.Text).Equals("%noromfile%", StringComparison.OrdinalIgnoreCase));
        if (argument.Equals("%romfile%.xml", StringComparison.OrdinalIgnoreCase)) return strip;
        if (argument.Equals("%romfile%", StringComparison.OrdinalIgnoreCase)) return !strip && quotesSafe;
        if (argument.Equals("%romfilename%.xml", StringComparison.OrdinalIgnoreCase))
            return otherTokens.Any(t => Unquoted(t.Text).Equals("%noromfile%", StringComparison.OrdinalIgnoreCase));
        return false;
    }
    static string RepairCommand(string command) {
        var tokens = Tokens(command);
        var profiles = tokens.Where(t => Unquoted(t.Text).StartsWith("--profile", StringComparison.OrdinalIgnoreCase)).ToList();
        if (profiles.Count > 1) throw new InvalidDataException("The command contains multiple profile arguments; review it in LaunchBox.");
        if (profiles.Count == 0) {
            if (tokens.Any(t => t.Text.Contains('%') && !Unquoted(t.Text).Equals("%noromfile%", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("The command has custom substitution variables; review it in LaunchBox.");
            return string.IsNullOrWhiteSpace(command) ? CanonicalCommand : command.TrimEnd() + " " + CanonicalCommand;
        }
        var token = profiles[0];
        if (!Unquoted(token.Text).StartsWith("--profile=", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The profile argument uses custom syntax; review it in LaunchBox.");
        return command[..token.Start] + CanonicalCommand + command[(token.Start + token.Length)..];
    }
    static void Set(XElement element, string name, string value, string prefix, List<LaunchBoxEmulatorFieldChange> changes) {
        var existing = element.Element(name); var before = existing?.Value ?? "";
        if (existing != null && before == value) return;
        changes.Add(new(prefix + name, before, value)); element.SetElementValue(name, value);
    }
    public Task<LaunchBoxEmulatorSetupPlan> InspectAsync(AppSettings settings, CancellationToken ct = default) => Task.Run(() => Inspect(settings, ct), ct);
    public LaunchBoxEmulatorSetupPlan Inspect(AppSettings settings, CancellationToken ct = default) {
        var source = ""; var emulatorName = ""; var emulatorId = ""; var current = "";
        try {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(settings.LaunchBoxPath) || string.IsNullOrWhiteSpace(settings.TeknoParrotPath) || string.IsNullOrWhiteSpace(settings.PlatformName))
                throw new InvalidDataException("Choose the LaunchBox location, TeknoParrot emulator installed location, and platform name first.");
            var identity = Identity(settings); var executable = Executable(settings);
            source = Path.Combine(Full(settings.LaunchBoxPath), "Data", "Emulators.xml");
            if (!Directory.Exists(Path.GetDirectoryName(source))) throw new InvalidDataException("The selected LaunchBox folder has no Data directory. Open LaunchBox once or choose its existing installation.");
            var exists = File.Exists(source); var hash = exists ? LaunchBoxService.Hash(source) : "";
            var document = exists ? LaunchBoxService.ReadXml(source) : new XDocument(new XElement("LaunchBox"));
            if (document.Root?.Name != "LaunchBox") throw new InvalidDataException("Emulators.xml does not have a LaunchBox root; it was left unchanged.");
            if (exists && LaunchBoxService.Hash(source) != hash) throw new InvalidDataException("Emulators.xml changed during detection. Detect again.");
            var root = document.Root!; var all = root.Elements("Emulator").ToList();
            if (all.Any(e => e.Elements("ID").Count() > 1 || e.Element("ID")?.HasElements == true)) throw new InvalidDataException("An emulator has duplicate or invalid ID fields. Review Emulators.xml before making changes.");
            var exact = all.Where(e => ApplicationPath(settings, e).Equals(executable, StringComparison.OrdinalIgnoreCase)).ToList();
            var related = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            XElement? emulator = exact.Count > 0 ? Select(root, exact, settings, related) : null;
            if (emulator == null) {
                var titled = all.Where(e => string.Concat(Value(e, "Title").Where(char.IsLetterOrDigit)).Equals("TeknoParrot", StringComparison.OrdinalIgnoreCase)).ToList();
                if (titled.Count > 1) throw new InvalidDataException("Multiple TeknoParrot entries have a different executable path. Choose the intended emulator in LaunchBox before repairing it.");
                emulator = titled.SingleOrDefault();
            }
            var changes = new List<LaunchBoxEmulatorFieldChange>();
            var created = emulator == null;
            if (emulator == null) {
                emulator = new XElement("Emulator", new XElement("ID", Guid.NewGuid().ToString()), new XElement("Title", "TeknoParrot"));
                root.Add(emulator); changes.Add(new("Emulator", "(missing)", "Create TeknoParrot"));
            }
            Scalars(emulator!); emulatorId = Value(emulator, "ID").Trim(); emulatorName = Value(emulator, "Title");
            if (emulatorId.Length == 0 || root.Elements("Emulator").Count(e => Value(e, "ID").Trim().Equals(emulatorId, StringComparison.OrdinalIgnoreCase)) != 1)
                throw new InvalidDataException("The selected TeknoParrot emulator needs a unique, nonempty ID. Review Emulators.xml before repairing it.");
            var associations = Associations(root, emulatorId, settings.PlatformName);
            if (associations.Count > 1) throw new InvalidDataException("The selected emulator has duplicate associations for this platform. Review them in LaunchBox before repairing it.");
            var association = associations.SingleOrDefault(); if (association != null) Scalars(association);
            var global = Value(emulator, "CommandLine"); var platform = Value(association, "CommandLine");
            current = string.IsNullOrWhiteSpace(platform) ? global : platform;
            var globalWorks = !string.IsNullOrWhiteSpace(platform) || CommandWorks(global, emulator, executable);
            var platformWorks = string.IsNullOrWhiteSpace(platform) || CommandWorks(platform, emulator, executable);
            if (!ApplicationPath(settings, emulator).Equals(executable, StringComparison.OrdinalIgnoreCase)) Set(emulator, "ApplicationPath", executable, "Emulator.", changes);
            if (string.IsNullOrWhiteSpace(platform) ? !globalWorks : !platformWorks) {
                if (string.IsNullOrWhiteSpace(platform)) Set(emulator, "CommandLine", RepairCommand(global), "Emulator.", changes);
                else Set(association!, "CommandLine", RepairCommand(platform), "Platform.", changes);
                Set(emulator, "FileNameWithoutExtensionAndPath", "true", "Emulator.", changes);
                if (created) Set(emulator, "NoQuotes", "true", "Emulator.", changes);
            }
            if (created) { Set(emulator, "NoSpace", "true", "Emulator.", changes); Set(emulator, "AutoExtract", "false", "Emulator.", changes); }
            if (association == null && created) {
                // Existing game records refer to the emulator ID. Do not replace another emulator's chosen platform default.
                var otherDefault = root.Elements("EmulatorPlatform").Any(e => Value(e, "Platform").Trim().Equals(settings.PlatformName.Trim(), StringComparison.OrdinalIgnoreCase) && Flag(e, "Default"));
                association = new XElement("EmulatorPlatform", new XElement("Emulator", emulatorId), new XElement("Platform", settings.PlatformName.Trim()), new XElement("CommandLine", ""), new XElement("Default", otherDefault ? "false" : "true"));
                root.Add(association); changes.Add(new("Platform association", "(missing)", settings.PlatformName.Trim()));
            }
            if (!string.IsNullOrWhiteSpace(platform) && !string.IsNullOrWhiteSpace(global) && changes.Any(c => c.Field == "Emulator.FileNameWithoutExtensionAndPath")) {
                if (!CommandWorks(global, emulator, executable)) throw new InvalidDataException("Repairing this platform command would change filename handling for the default command. Review the custom emulator commands in LaunchBox first.");
            }
            var proposed = string.IsNullOrWhiteSpace(Value(association, "CommandLine")) ? Value(emulator, "CommandLine") : Value(association, "CommandLine");
            foreach (var other in root.Elements("EmulatorPlatform").Where(e => e != association && Value(e, "Emulator").Trim().Equals(emulatorId, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(Value(e, "CommandLine")))) {
                if (changes.Any(c => c.Field == "Emulator.FileNameWithoutExtensionAndPath") && !CommandWorks(Value(other, "CommandLine"), emulator, executable))
                    throw new InvalidDataException("This repair would affect a custom command for another associated platform. Review the emulator flags in LaunchBox first.");
            }
            if (!CommandWorks(proposed, emulator, executable)) throw new InvalidDataException("This command could not be repaired without changing its custom behavior. Review the emulator settings in LaunchBox.");
            if (!File.Exists(executable)) throw new InvalidDataException("The configured TeknoParrotUi.exe is missing. Choose the installed emulator location, then detect again.");
            return new() { Status = changes.Count == 0 ? "Ready" : "Needs repair", Detail = changes.Count == 0 ? "TeknoParrot has a working profile command for this LaunchBox platform." : (current.Contains("%ROMNAME%", StringComparison.OrdinalIgnoreCase) ? "%ROMNAME% is not a supported LaunchBox ROM variable. " : "") + "Proposed repair: " + string.Join(", ", changes.Select(c => c.Field)) + ". Review these changes, then close LaunchBox and Big Box before applying them.", EmulatorName = emulatorName, EmulatorId = emulatorId, CurrentCommand = current, ProposedCommand = proposed, SourcePath = source, Changes = changes.AsReadOnly(), Identity = identity, OriginalHash = hash, OriginalExists = exists, ProposedDocument = document, RelatedHashes = related };
        } catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or XmlException or ArgumentException or NotSupportedException) {
            return new() { Status = "Needs review", Detail = ex.Message, EmulatorName = emulatorName, EmulatorId = emulatorId, CurrentCommand = current, SourcePath = source };
        }
    }
    void Closed() { if (isRunning()) throw new InvalidOperationException("Close LaunchBox and Big Box before changing their emulator configuration."); }
    static void Unchanged(LaunchBoxEmulatorSetupPlan plan) {
        foreach (var pair in plan.RelatedHashes)
            if (!File.Exists(pair.Key) || LaunchBoxService.Hash(pair.Key) != pair.Value) throw new InvalidOperationException("The platform used to identify this emulator changed. Detect again before repairing; external edits were preserved.");
        if (File.Exists(plan.SourcePath) != plan.OriginalExists || plan.OriginalExists && LaunchBoxService.Hash(plan.SourcePath) != plan.OriginalHash)
            throw new InvalidOperationException("Emulators.xml changed after detection. Detect again before applying a repair; external edits were preserved.");
    }
    static void SaveXml(string path, XDocument document) {
        var copy = new XDocument(document); LaunchBoxService.NormalizeFormatting(copy);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
            using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, IndentChars = "  ", NewLineChars = "\r\n", NewLineHandling = NewLineHandling.Entitize, CloseOutput = false })) copy.Save(writer);
            stream.Flush(true);
        }
        if (LaunchBoxService.ReadXml(path).Root?.Name != "LaunchBox") throw new InvalidDataException("The staged emulator XML failed validation.");
    }
    public async Task<LaunchBoxEmulatorSetupResult> ApplyAsync(LaunchBoxEmulatorSetupPlan plan, AppSettings currentSettings, CancellationToken ct = default) {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        string? temp = null;
        try {
            ct.ThrowIfCancellationRequested();
            if (plan.ProposedDocument == null || plan.Identity != Identity(currentSettings)) throw new InvalidOperationException("Setup paths or the platform changed after detection. Detect again before applying a repair.");
            if (plan.AppliedHash.Length > 0) {
                if (!File.Exists(plan.SourcePath) || LaunchBoxService.Hash(plan.SourcePath) != plan.AppliedHash) throw new InvalidOperationException("Emulators.xml changed after the repair. Detect again.");
                return new(false, "This emulator repair has already been applied.", plan.BackupPath);
            }
            if (plan.IsReady) { Unchanged(plan); return new(false, "The TeknoParrot launch configuration is already ready.", ""); }
            if (!plan.CanRepair) throw new InvalidOperationException("This emulator setup needs review before it can be repaired.");
            Closed(); Unchanged(plan);
            if (!File.Exists(Executable(currentSettings))) throw new InvalidOperationException("The configured TeknoParrotUi.exe is missing; no LaunchBox changes were made.");
            var backupDirectory = Path.Combine(Full(currentSettings.LaunchBoxPath), "Backups", "ArcadeLibraryManager", DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-emulator-" + Guid.NewGuid().ToString("N")[..8]);
            await health.EnsureWritableAsync(new[] { plan.SourcePath, backupDirectory }, ct).ConfigureAwait(false);
            Closed(); Unchanged(plan); Directory.CreateDirectory(backupDirectory);
            var backup = plan.OriginalExists ? Path.Combine(backupDirectory, "Emulators.xml") : "";
            if (plan.OriginalExists) {
                File.Copy(plan.SourcePath, backup, false);
                if (LaunchBoxService.Hash(backup) != plan.OriginalHash) throw new IOException("The emulator backup did not match the preview; no changes were made.");
            } else File.WriteAllText(Path.Combine(backupDirectory, "created-file.txt"), "There was no Emulators.xml before this repair. Created path: " + plan.SourcePath);
            temp = plan.SourcePath + ".alm-" + Guid.NewGuid().ToString("N") + ".tmp";
            SaveXml(temp, plan.ProposedDocument); var proposedHash = LaunchBoxService.Hash(temp);
            ct.ThrowIfCancellationRequested(); Closed(); Unchanged(plan);
            await health.EnsureWritableAsync(new[] { plan.SourcePath }, ct).ConfigureAwait(false);
            Closed(); Unchanged(plan);
            if (plan.OriginalExists) File.Replace(temp, plan.SourcePath, null, true); else File.Move(temp, plan.SourcePath, false);
            temp = null;
            if (LaunchBoxService.Hash(plan.SourcePath) != proposedHash || LaunchBoxService.ReadXml(plan.SourcePath).Root?.Name != "LaunchBox") throw new IOException("The written emulator configuration could not be verified. Original backup: " + backup);
            plan.AppliedHash = proposedHash; plan.BackupPath = backup;
            return new(true, plan.OriginalExists ? "TeknoParrot emulator settings repaired. Original backup: " + backup : "Created the TeknoParrot emulator configuration; no previous Emulators.xml existed.", backup);
        } finally { try { if (temp != null && File.Exists(temp)) File.Delete(temp); } finally { Gate.Release(); } }
    }
}
