using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace ArcadeLibraryManager.Core;

/// <summary>Runs official EmuMovies Sync and imports only missing LaunchBox video snaps.</summary>
public sealed class EmuMoviesService
{
    private readonly string dataDirectory;
    private readonly IProgress<JobEvent>? progress;
    private static readonly SemaphoreSlim SyncGate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static readonly HashSet<string> ClassicTypes = new(StringComparer.OrdinalIgnoreCase)
        { "TeknoS11", "TeknoS21", "TeknoS22", "TeknoS23", "TeknoModel1", "TeknoModel2", "TeknoM2", "TeknoHNG64", "TeknoGClub", "TeknoHornet", "TeknoViper", "TeknoCobra", "TeknoAir", "TeknoAGX", "TeknoVUnit", "TeknoZeus", "TeknoVegas" };
    private sealed record MatchInput(string Stem, string FileName, string EmulatorType, string Title);
    private sealed record CatalogJob(string Catalog, string InputDirectory, string IncludePath, string VerifiedPath, string DownloadDirectory, string ReportDirectory);

    public EmuMoviesService(string dataDirectory, IProgress<JobEvent>? progress = null)
    {
        this.dataDirectory = Path.GetFullPath(dataDirectory);
        this.progress = progress;
    }

    public static List<EmuMoviesPlatformChoice> GetPlatforms(string launchBoxRoot)
    {
        var root = Path.GetFullPath(launchBoxRoot);
        var directory = Path.Combine(root, "Data", "Platforms");
        if (!Directory.Exists(directory)) return [];
        var result = new List<EmuMoviesPlatformChoice>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.xml").Order(StringComparer.OrdinalIgnoreCase))
        {
            var document = ReadXml(path);
            var games = document.Root?.Elements("Game").ToList() ?? [];
            var name = Path.GetFileNameWithoutExtension(path);
            var folders = games.Select(g => ResolveApplication(root, Value(g, "ApplicationPath")))
                .Where(p => !string.IsNullOrWhiteSpace(p)).Select(Path.GetDirectoryName)
                .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p)).Cast<string>();
            var match = folders.GroupBy(p => p, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "";
            result.Add(new(name, match, games.Count));
        }
        return result;
    }

    public async Task<EmuMoviesVideoRunResult> RunAsync(EmuMoviesVideoRequest request, bool download, bool import, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await SyncGate.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("Another EmuMovies video operation is already running in this app.");
        try
        {
            return await RunCoreAsync(request, download, import, cancellationToken);
        }
        finally { SyncGate.Release(); }
    }

    private async Task<EmuMoviesVideoRunResult> RunCoreAsync(EmuMoviesVideoRequest request, bool download, bool import, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var launchBox = RequiredDirectory(request.LaunchBoxPath, "LaunchBox folder");
        var matchFolder = RequiredDirectory(request.MatchFolder, "Matching folder");
        ValidateName(request.PlatformName, "LaunchBox platform");
        if (string.IsNullOrWhiteSpace(request.Catalog) || request.Catalog.Contains('|') || request.Catalog.Any(char.IsControl)) throw new ArgumentException("Choose an exact EmuMovies catalog display name.");
        var platformXml = Path.Combine(launchBox, "Data", "Platforms", request.PlatformName + ".xml");
        if (!File.Exists(platformXml)) throw new FileNotFoundException("The chosen platform does not exist in this LaunchBox library.", platformXml);
        var work = Path.GetFullPath(string.IsNullOrWhiteSpace(request.WorkDirectory) ? Path.Combine(dataDirectory, "emumovies") : request.WorkDirectory);
        AssertNoReparse(work, "Work folder");
        if (Path.GetPathRoot(work) == work) throw new ArgumentException("Use a dedicated work directory, not a drive or share root.");
        if (Within(work, matchFolder) || Within(matchFolder, work) || Within(work, launchBox)) throw new ArgumentException("Use a work folder outside LaunchBox and separate from the matching folder.");
        if (download && !File.Exists(request.SyncExecutablePath)) throw new FileNotFoundException("Choose the installed official EmuMovies Sync executable, open it and sign in first.", request.SyncExecutablePath);
        var assets = FindAssets();
        Directory.CreateDirectory(work);
        var run = Path.Combine(work, "runs", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(run);
        var result = new EmuMoviesVideoRunResult { ReportDirectory = run };
        var isTekno = request.PlatformName.Equals("TeknoParrot", StringComparison.OrdinalIgnoreCase);
        var inputs = ReadInputs(matchFolder, isTekno);
        if (inputs.Count == 0) throw new InvalidOperationException(isTekno ? "No TeknoParrot XML profiles were found in the matching folder." : "No matching game filenames were found in the chosen folder.");
        if (!isTekno)
        {
            var importedStems = (ReadXml(platformXml).Root?.Elements("Game") ?? [])
                .Select(g => Value(g, "ApplicationPath")).Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var ignored = inputs.Where(i => !importedStems.Contains(i.Stem)).ToList();
            inputs = inputs.Where(i => importedStems.Contains(i.Stem)).ToList();
            result.Skipped += ignored.Count;
            await File.WriteAllTextAsync(Path.Combine(run, "ExcludedInputs.json"), JsonSerializer.Serialize(ignored.Select(i => new { i.Stem, i.FileName, Reason = "Not an imported main game application stem in this platform; unrelated files and track companions are excluded." }), Json), token);
            if (ignored.Count > 0) result.Details.Add($"Excluded {ignored.Count} unrelated matching files or companion tracks; {inputs.Count} imported game inputs remain. See ExcludedInputs.json.");
            if (inputs.Count == 0)
            {
                result.Details.Add("No matching filenames belong to imported games in the selected platform. No download or import was started.");
                await File.WriteAllTextAsync(Path.Combine(run, "Summary.json"), JsonSerializer.Serialize(result, Json), token);
                return result;
            }
        }
        var duplicates = inputs.GroupBy(i => i.Stem, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList();
        if (duplicates.Count > 0)
        {
            inputs = inputs.GroupBy(i => i.Stem, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            result.Details.Add($"Combined {duplicates.Count} repeated filename stems into one matching input each.");
        }
        var mamePrimary = isTekno && request.Catalog.Trim().Equals("MAME", StringComparison.OrdinalIgnoreCase);
        var model2Primary = isTekno && request.Catalog.Trim().Equals("Sega Model 2", StringComparison.OrdinalIgnoreCase);
        var model2 = isTekno && (request.UseMameFallback || model2Primary) ? inputs.Where(i => i.EmulatorType.Equals("TeknoModel2", StringComparison.OrdinalIgnoreCase)).ToList() : [];
        var model2Stems = model2.Select(i => i.Stem).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mame = isTekno && (request.UseMameFallback || mamePrimary) ? ReadMameNames(launchBox) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // MAME metadata also lists systems served by other EmuMovies catalogs. A matching
        // filename confirms identity only; it must not move unapproved hardware into MAME.
        var classic = isTekno && (request.UseMameFallback || mamePrimary)
            ? inputs.Where(i => !model2Stems.Contains(i.Stem) && ClassicTypes.Contains(i.EmulatorType)).ToList() : [];
        var classicStems = classic.Select(i => i.Stem).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var primary = inputs.Where(i => !classicStems.Contains(i.Stem) && !model2Stems.Contains(i.Stem)).ToList();
        if ((mamePrimary || model2Primary) && primary.Count > 0)
        {
            result.Skipped += primary.Count;
            result.Details.Add($"Excluded {primary.Count} modern/non-classic profiles from the selected hardware catalog to avoid PC-title collisions. Use ArcadePC for those profiles.");
            await File.WriteAllTextAsync(Path.Combine(run, "ExcludedMameInputs.json"), JsonSerializer.Serialize(primary.Select(i => new { i.Stem, i.EmulatorType, Reason = "Modern or non-classic profile is outside the MAME catalog scope." }), Json), token);
            primary = [];
        }
        var jobs = new List<CatalogJob>();
        if (model2.Count > 0) jobs.Add(PrepareJob("Sega Model 2", model2, [], work, run, request.PlatformName));
        if (classic.Count > 0) jobs.Add(PrepareJob("MAME", classic, classic.Where(i => mame.Contains(i.Stem)).Select(i => i.Stem), work, run, request.PlatformName));
        if (primary.Count > 0) jobs.Add(PrepareJob(request.Catalog.Trim(), primary, [], work, run, request.PlatformName));
        var selection = new
        {
            request.PlatformName, MatchFolder = matchFolder, InputCount = inputs.Count, MameInputs = classic.Count, Model2Inputs = model2.Count, PrimaryInputs = primary.Count,
            Scope = "Video_MP4_HI_QUAL only; existing LaunchBox videos are never overwritten.",
            MameSelection = "Only approved classic hardware profile types are routed to MAME. An exact local MAME metadata filename alone does not establish catalog suitability. Exact local arcade identity additionally gates fallback import. Sync performs media matching.",
            Inputs = inputs.Select(i => new { i.Stem, i.Title, i.EmulatorType, Catalog = model2Stems.Contains(i.Stem) ? "Sega Model 2" : classicStems.Contains(i.Stem) ? "MAME" : (mamePrimary || model2Primary) ? "Excluded" : request.Catalog, ExactLocalMameIdentity = mame.Contains(i.Stem) })
        };
        await File.WriteAllTextAsync(Path.Combine(run, "Selection.json"), JsonSerializer.Serialize(selection, Json), token);
        result.Details.Add($"Matching inputs: {inputs.Count}; MAME: {classic.Count}; Sega Model 2: {model2.Count}; {request.Catalog}: {primary.Count}. Only video snaps are selected.");
        if (classic.Count > 0) result.Details.Add($"MAME import scope: {classic.Count(i => mame.Contains(i.Stem))} exact local arcade identities. Sync performs media matching; this is not visual verification of every video.");
        progress?.Report(new("EmuMovies", result.Details[0]));
        foreach (var job in jobs)
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(job.ReportDirectory);
            result.Details.Add($"{job.Catalog} staging: {job.DownloadDirectory}");
            var configPath = Path.Combine(job.ReportDirectory, "Job.json");
            var resultPath = Path.Combine(job.ReportDirectory, "Result.json");
            var cancelPath = Path.Combine(job.ReportDirectory, "Cancel.request");
            var config = new
            {
                System = job.Catalog, MatchFolder = job.InputDirectory, DownloadFolder = job.DownloadDirectory,
                LaunchBoxRoot = launchBox, PlatformName = request.PlatformName, ProfilesRoot = isTekno ? matchFolder : "",
                IncludeStemsPath = job.IncludePath, VerifiedStemsPath = job.VerifiedPath, job.ReportDirectory,
                Download = download, Import = import, CancelRequestPath = cancelPath, OwnershipPath = Path.Combine(job.ReportDirectory, "SyncOwnership.json"), ResultPath = resultPath
            };
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config, Json), token);
            await RunPowerShellAsync(Path.Combine(assets, "Invoke-EmuMoviesVideoJob.ps1"), configPath, cancelPath, job.ReportDirectory, token);
            var completed = JsonSerializer.Deserialize<EmuMoviesVideoRunResult>(await File.ReadAllTextAsync(resultPath, token), Json) ?? throw new InvalidDataException("Video job returned no result.");
            result.Downloaded += completed.Downloaded;
            result.Imported += completed.Imported;
            result.Skipped += completed.Skipped;
            result.Missing += completed.Missing;
            result.Details.AddRange(completed.Details.Select(d => job.Catalog + ": " + d));
        }
        await File.WriteAllTextAsync(Path.Combine(run, "Summary.json"), JsonSerializer.Serialize(result, Json), token);
        progress?.Report(new("EmuMovies", $"Downloaded {result.Downloaded}; imported {result.Imported}; skipped {result.Skipped}; missing staged videos {result.Missing}. Reports: {run}", Percent: 100));
        return result;
    }

    private CatalogJob PrepareJob(string catalog, List<MatchInput> inputs, IEnumerable<string> verified, string work, string run, string platform)
    {
        var slug = Slug(catalog);
        var inputDirectory = Path.Combine(run, "inputs", slug);
        Directory.CreateDirectory(inputDirectory);
        // Sync matches filenames; empty placeholders avoid copying ROMs or profile contents.
        foreach (var input in inputs) File.WriteAllBytes(Path.Combine(inputDirectory, input.Stem + ".xml"), []);
        var includePath = Path.Combine(run, slug + "-inputs.csv");
        WriteStemCsv(includePath, inputs.Select(i => i.Stem));
        var verifiedPath = "";
        if (catalog.Equals("MAME", StringComparison.OrdinalIgnoreCase) && platform.Equals("TeknoParrot", StringComparison.OrdinalIgnoreCase))
        {
            verifiedPath = Path.Combine(run, slug + "-exact-local-identities.csv");
            WriteStemCsv(verifiedPath, verified);
        }
        return new(catalog, inputDirectory, includePath, verifiedPath, ResolveDownloadDirectory(work, platform, catalog), Path.Combine(run, "reports", slug));
    }

    private static string ResolveDownloadDirectory(string work, string platform, string catalog)
    {
        string? legacyName = platform.Equals("TeknoParrot", StringComparison.OrdinalIgnoreCase) ? catalog.ToUpperInvariant() switch
        {
            "ARCADEPC" => "ArcadePC", "MAME" => "Arcade", "SEGA MODEL 2" => "SegaModel2", _ => null
        } : null;
        var legacy = legacyName is null ? "" : Path.Combine(work, "staging", legacyName);
        var path = legacy.Length > 0 && Directory.Exists(legacy) ? legacy : Path.Combine(work, "downloads", Slug(platform), Slug(catalog));
        AssertNoReparse(path, "Video staging folder");
        return path;
    }
    private async Task RunPowerShellAsync(string script, string config, string cancelPath, string reportDirectory, CancellationToken token)
    {
        var start = new ProcessStartInfo { FileName = PowerShellPath(), UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-ConfigPath", config }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the EmuMovies video helper.");
        var errors = new StringBuilder();
        var logPath = Path.Combine(reportDirectory, "Job.log");
        using var log = new StreamWriter(logPath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        var logGate = new object();
        async Task ReadAsync(StreamReader reader, bool error)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                lock (logGate) { log.WriteLine(line); if (error && errors.Length < 16000) errors.AppendLine(line); }
                if (line.StartsWith("EMUMOVIES_PROGRESS ", StringComparison.Ordinal))
                {
                    try
                    {
                        using var json = JsonDocument.Parse(line[19..]);
                        var message = string.Join(" ", new[] { "Status", "Download", "Total" }.Select(k => json.RootElement.TryGetProperty(k, out var value) ? value.GetString() : null).Where(v => !string.IsNullOrWhiteSpace(v)));
                        progress?.Report(new("EmuMovies", message));
                    }
                    catch (JsonException) { progress?.Report(new("EmuMovies", "Official Sync is processing videos.")); }
                }
            }
        }
        var outputTask = ReadAsync(process.StandardOutput, false);
        var errorTask = ReadAsync(process.StandardError, true);
        using var registration = token.Register(() => { try { File.WriteAllText(cancelPath, "Cancel requested by Arcade Library Manager."); } catch { } });
        // The helper consumes the signal, verifies ownership, invokes Sync Cancel and waits for idle.
        // Do not kill the waiter on cancellation: that could leave official Sync downloading.
        await process.WaitForExitAsync(CancellationToken.None);
        await Task.WhenAll(outputTask, errorTask);
        if (token.IsCancellationRequested)
        {
            var ownership = Path.Combine(reportDirectory, "SyncOwnership.json");
            if (File.Exists(ownership))
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(ownership));
                var state = document.RootElement.GetProperty("State").GetString();
                if (state is not ("Cancelled" or "Complete" or "Failed")) throw new InvalidOperationException("Cancellation could not confirm that Sync stopped. Use Cancel in Sync before starting another run. " + errors);
            }
            throw new OperationCanceledException("EmuMovies video operation cancelled; staged files and reports were retained.", token);
        }
        if (process.ExitCode != 0) throw new InvalidOperationException($"EmuMovies video helper failed. Check Sync and {logPath}. {errors.ToString().Trim()}");
    }

    private static List<MatchInput> ReadInputs(string folder, bool profiles)
    {
        var result = new List<MatchInput>();
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp", ".mp4", ".mkv", ".avi", ".txt", ".json", ".csv", ".ps1", ".log", ".ini", ".nfo" };
        foreach (var file in Directory.EnumerateFiles(folder).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (profiles && !Path.GetExtension(file).Equals(".xml", StringComparison.OrdinalIgnoreCase)) continue;
            if (!profiles && ignored.Contains(Path.GetExtension(file))) continue;
            var stem = Path.GetFileNameWithoutExtension(file);
            ValidateName(stem, "Input filename");
            var xml = profiles ? ReadXml(file).Root : null;
            if (profiles && xml?.Name.LocalName != "GameProfile") continue;
            result.Add(new(stem, Path.GetFileName(file), xml is null ? "" : Value(xml, "EmulatorType"), xml is null ? stem : Value(xml, "GameNameInternal")));
        }
        return result;
    }

    private static HashSet<string> ReadMameNames(string launchBox)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(launchBox, "Metadata", "MAME.xml");
        if (!File.Exists(path)) return result;
        foreach (var group in (ReadXml(path).Root?.Elements("MameFile") ?? []).GroupBy(i => Value(i, "FileName"), StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() != 1) continue;
            var item = group.Single();
            var stem = Value(item, "FileName");
            if (stem.Length > 0 && !Value(item, "IsNonArcade").Equals("true", StringComparison.OrdinalIgnoreCase)) result.Add(Path.GetFileNameWithoutExtension(stem));
        }
        return result;
    }
    private static XDocument ReadXml(string path)
    {
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader);
    }
    private static string Value(XElement element, string name) => element.Element(name)?.Value ?? "";
    private static string ResolveApplication(string root, string path) => string.IsNullOrWhiteSpace(path) ? "" : Path.GetFullPath(path, root);
    private static string RequiredDirectory(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !Directory.Exists(path)) throw new DirectoryNotFoundException(description + " is missing or is not an absolute directory.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        AssertNoReparse(full, description);
        return full;
    }
    private static void AssertNoReparse(string path, string description)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException(description + " must not traverse symbolic links or junctions.");
    }
    private static void ValidateName(string name, string description)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ')) throw new ArgumentException(description + " is not a valid filename component.");
    }
    private static bool Within(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string Slug(string value)
    {
        var clean = new string(value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        return clean + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToUpperInvariant())))[..8].ToLowerInvariant();
    }
    private static void WriteStemCsv(string path, IEnumerable<string> stems) => File.WriteAllLines(path, new[] { "\"RomStem\"" }.Concat(stems.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Select(s => "\"" + s.Replace("\"", "\"\"") + "\"")), new UTF8Encoding(true));
    private static string FindAssets()
    {
        var candidates = new List<string>();
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                candidates.Add(Path.Combine(directory.FullName, "assets", "emumovies"));
        return candidates.FirstOrDefault(p => File.Exists(Path.Combine(p, "Invoke-EmuMoviesVideoJob.ps1"))) ?? throw new FileNotFoundException("Bundled EmuMovies helper scripts are missing from assets/emumovies.");
    }
    private static string PowerShellPath()
    {
        var pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        if (File.Exists(pwsh)) return pwsh;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
    }
}




