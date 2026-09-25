using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace ArcadeLibraryManager.Core;

public sealed class WorkflowOptions
{
    public bool InstallGames { get; set; }
    public bool SyncLaunchBox { get; set; }
    public bool GenerateMissingThemes { get; set; }
    public bool InstallBezels { get; set; }
    public List<string> EnabledStages() => new[] {
        (InstallGames, "Install"), (SyncLaunchBox, "LaunchBox"),
        (GenerateMissingThemes, "Theme"), (InstallBezels, "Bezel")
    }.Where(x => x.Item1).Select(x => x.Item2).ToList();
}

public sealed class WorkflowStagePlan
{
    public string Stage { get; set; } = "";
    public string Action { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public bool RequiresReadyGame { get; set; } = true;
    public bool ConditionalAfterInstall { get; set; }
    public string BezelSourceFingerprint { get; set; } = "";
    public Dictionary<string, string> SourceHashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Artifacts { get; set; } = [];
    [JsonIgnore] public object? RuntimePlan { get; set; }
}

public sealed class WorkflowGamePlan
{
    public bool Selected { get; set; } = true;
    public bool ExcludedByVersionPolicy { get; set; }
    public string ProfileId { get; set; } = "";
    public string Name { get; set; } = "";
    public string InstallAction { get; set; } = "Not requested";
    public string Stages { get; set; } = "";
    public string Warnings { get; set; } = "";
    public string Blockers { get; set; } = "";
    public string Status { get; set; } = "Reviewed";
    public string LaunchBoxGameGuid { get; set; } = "";
    public List<WorkflowStagePlan> StagePlans { get; set; } = [];
    public InstallPlanItem? OriginalInstallPlan { get; set; }
    [JsonIgnore] public GameRecord Game { get; set; } = new();
}

public sealed class WorkflowPlan
{
    public string Id { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string DataPath { get; set; } = "";
    public WorkflowOptions Options { get; set; } = new();
    public List<WorkflowGamePlan> Rows { get; set; } = [];
    public string ResumesWorkflowId { get; set; } = "";
    internal string Seal { get; set; } = "";
    internal string SettingsHash { get; set; } = "";
    internal string ResumeJournalHash { get; set; } = "";
    internal bool Executed { get; set; }
}

public sealed class WorkflowStageJournal
{
    public string Stage { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public string Action { get; set; } = "";
    public string Detail { get; set; } = "";
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<string> Artifacts { get; set; } = [];
    public Dictionary<string, string> ReviewedSourceHashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool ConditionalAfterInstall { get; set; }
    public string BezelSourceFingerprint { get; set; } = "";
}
public sealed class WorkflowGameJournal
{
    public string ProfileId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public string LaunchBoxGameGuid { get; set; } = "";
    public string SupersededBy { get; set; } = "";
    public InstallPlanItem? OriginalInstallPlan { get; set; }
    public List<WorkflowStageJournal> Stages { get; set; } = [];
}
public sealed class WorkflowJournal
{
    public string App { get; set; } = "ArcadeLibraryManager";
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string Status { get; set; } = "Pending";
    public string SettingsHash { get; set; } = "";
    public string DataPath { get; set; } = "";
    public string ResumesWorkflowId { get; set; } = "";
    public string SupersededBy { get; set; } = "";
    public WorkflowOptions Options { get; set; } = new();
    public List<WorkflowGameJournal> Games { get; set; } = [];
}
public sealed class WorkflowRunResult
{
    public int CompletedGames { get; set; }
    public int IncompleteGames { get; set; }
    public int SucceededStages { get; set; }
    public int UnchangedStages { get; set; }
    public List<string> Errors { get; set; } = [];
    public string ReportPath { get; set; } = "";
    public string JournalPath { get; set; } = "";
}
public sealed record WorkflowStageOutcome(string Status, string Detail,
    IReadOnlyList<string>? ChangedPaths = null, IReadOnlyList<string>? Artifacts = null);

/// <summary>Injectable boundary for fixture tests; production uses the existing independently guarded services.</summary>
public interface IWorkflowStageRunner
{
    Task<List<WorkflowGamePlan>> PreviewAsync(AppSettings settings, string dataDir, IReadOnlyList<string> gameIds,
        WorkflowOptions options, IProgress<JobEvent>? progress, CancellationToken ct);
    Task<bool> IsGameReadyAsync(AppSettings settings, WorkflowGamePlan game, CancellationToken ct);
    Task<WorkflowStageOutcome> ExecuteStageAsync(AppSettings settings, string dataDir, WorkflowGamePlan game,
        WorkflowStagePlan stage, IProgress<JobEvent>? progress, CancellationToken ct);
}

/// <summary>One reviewed selection, durable per-game stages, and fresh review for every retry.</summary>
public sealed class LibraryWorkflowService
{
    private readonly AppSettings settings;
    private readonly string data;
    private readonly IWorkflowStageRunner runner;
    private readonly VolumeHealthService health;
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public LibraryWorkflowService(AppSettings settings, string dataDir, IWorkflowStageRunner? runner = null, VolumeHealthService? health = null,
        Func<bool>? launchBoxRunning = null)
    {
        this.settings = Clone(settings); data = Path.GetFullPath(dataDir); this.health = health ?? new();
        this.runner = runner ?? new ExistingServicesWorkflowRunner(this.health, launchBoxRunning ?? LaunchBoxService.IsLaunchBoxRunning);
    }
    public async Task<WorkflowPlan> PreviewAsync(IEnumerable<string> gameIds, WorkflowOptions options,
        IProgress<JobEvent>? progress = null, CancellationToken ct = default)
    {
        var ids = gameIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.Count == 0) throw new InvalidOperationException("Select at least one game.");
        if (ids.Any(id => string.IsNullOrWhiteSpace(id) || Path.GetFileName(id) != id || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("A selected game has an invalid profile ID.");
        if (options.EnabledStages().Count == 0) throw new InvalidOperationException("Choose at least one workflow step.");
        await health.EnsureWritableAsync([data], ct);
        var copy = Clone(options);
        var rows = await runner.PreviewAsync(settings, data, ids, copy, progress, ct);
        if (rows.Count != ids.Count || !rows.Select(r => r.ProfileId).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(ids))
            throw new InvalidOperationException("The catalog changed or selected profiles were not found. Scan and select again.");
        var plan = new WorkflowPlan { Id = Guid.NewGuid().ToString("N"), DataPath = data, Options = copy, Rows = rows, SettingsHash = HashSettings(settings) };
        ValidateStages(plan); plan.Seal = Seal(plan); return plan;
    }

    public async Task<WorkflowPlan> PreviewResumeAsync(string workflowId, IProgress<JobEvent>? progress = null, CancellationToken ct = default)
    {
        var originalJournalHash = FileFingerprint(JournalPath(workflowId));
        var journal = await LoadJournalAsync(workflowId, ct);
        if (journal.SupersededBy.Length != 0) throw new InvalidOperationException("This run was superseded; resume its latest journal: " + journal.SupersededBy);
        if (journal.SettingsHash != HashSettings(settings) || !Path.GetFullPath(journal.DataPath).Equals(data, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Settings or the saved settings format changed since this run. Start a new explicit game selection and preview; the original journal and installation checkpoints are preserved.");
        var pending = journal.Games.Where(g => g.SupersededBy.Length == 0 && g.Stages.Any(Resumable)).ToList();
        if (pending.Count == 0) throw new InvalidOperationException(journal.Games.Any(g => g.Stages.Any(s => !Done(s.Status) && RetiredStage(s.Stage)))
            ? "This saved workflow only has retired artwork download or EmuMovies steps left. Its history is preserved. Create a new plan to generate themes from local artwork and gameplay snaps."
            : "This workflow has no supported unfinished stages. Its history is preserved; create a new plan for additional work.");
        // Re-preview supported unfinished work only. Retired stages remain in the old journal as history.
        var rows = new List<WorkflowGamePlan>();
        foreach (var group in pending.GroupBy(g => string.Join("|", g.Stages.Where(Resumable).Select(s => s.Stage))))
        {
            var stages = group.First().Stages.Where(Resumable).Select(s => s.Stage).ToHashSet();
            var options = OptionsFor(stages);
            var part = await PreviewAsync(group.Select(g => g.ProfileId), options, progress, ct);
            foreach (var row in part.Rows)
            {
                var previous = pending.Single(g => g.ProfileId == row.ProfileId);
                if (previous.Stages.Any(s => RetiredStage(s.Stage))) row.Warnings = "Artwork downloads and EmuMovies steps were retired; their original history is preserved. This review resumes supported work using local media. " + row.Warnings;
                foreach (var stage in row.StagePlans)
                {
                    var oldStage = previous.Stages.Single(s => s.Stage == stage.Stage);
                    stage.Artifacts = oldStage.Artifacts.ToList();
                }
            }
            rows.AddRange(part.Rows);
        }
        // Older runs may leave regions of one game in different unfinished-stage groups.
        // Reconcile all candidates together after those groups have been reviewed.
        foreach (var family in rows.Where(r => !r.ExcludedByVersionPolicy).GroupBy(r => GameSelectionPolicy.FamilyKey(r.Name)).Where(g => g.Count() > 1))
        {
            var ready = family.Where(r => r.Game.PathsValid).ToList();
            var actionable = family.Where(r => r.OriginalInstallPlan?.Selected == true && r.OriginalInstallPlan.Action is "Use local ROMs" or "Use local files" or "Extract local archive" or "Download and install").ToList();
            var candidates = ready.Count > 0 ? ready : actionable.Count > 0 ? actionable : family.ToList();
            var winnerId = GameSelectionPolicy.SelectPreferred(candidates.Select(r => new GameRecord {
                Id = r.ProfileId, Name = r.Name, Emulator = r.Game.Emulator, PathsValid = r.Game.PathsValid, Installed = r.Game.Installed
            })).Single().Id;
            var winner = family.Single(r => r.ProfileId.Equals(winnerId, StringComparison.OrdinalIgnoreCase));
            foreach (var row in family.Where(r => !r.ProfileId.Equals(winnerId, StringComparison.OrdinalIgnoreCase)))
            {
                row.ExcludedByVersionPolicy = true; row.Selected = false;
                if (row.OriginalInstallPlan != null) row.OriginalInstallPlan.Selected = false;
                var detail = $"One version per game: {winner.Name} ({winner.ProfileId}) is preferred for this resumed batch. USA, then World, then other regions; available sources and ready profiles take precedence over unavailable candidates.";
                foreach (var stage in row.StagePlans) { stage.Action = "Alternate version"; stage.Status = "NeedsReview"; stage.Detail = detail; stage.ConditionalAfterInstall = false; }
                row.Stages = string.Join(" → ", row.StagePlans.Select(s => s.Stage + ": " + s.Action));
                row.Blockers = string.Join("; ", row.StagePlans.Select(s => s.Stage + ": " + s.Detail));
            }
        }
        var result = new WorkflowPlan { Id = Guid.NewGuid().ToString("N"), DataPath = data, Options = OptionsFor(rows.SelectMany(r => r.StagePlans).Select(s => s.Stage).ToHashSet()),
            Rows = rows, SettingsHash = HashSettings(settings), ResumesWorkflowId = journal.Id, ResumeJournalHash = originalJournalHash };
        if (FileFingerprint(JournalPath(workflowId)) != originalJournalHash) throw new InvalidOperationException("Workflow progress changed during the resume review. Review its latest state again.");
        ValidateStages(result); result.Seal = Seal(result); return result;
    }

    public async Task<WorkflowRunResult> ExecuteAsync(WorkflowPlan plan, AppSettings currentSettings,
        IProgress<JobEvent>? progress = null, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (plan.Executed || plan.Seal.Length == 0 || plan.Seal != Seal(plan) || plan.SettingsHash != HashSettings(currentSettings)
                || plan.SettingsHash != HashSettings(settings) || plan.DataPath != data)
                throw new InvalidOperationException("The reviewed workflow or settings changed. Preview again before running.");
            ValidateStages(plan);
            var selected = plan.Rows.Where(r => r.Selected).ToList();
            if (selected.Count == 0) throw new InvalidOperationException("Check at least one reviewed game.");
            if (selected.Any(r => r.ExcludedByVersionPolicy)) throw new InvalidOperationException("An alternate version cannot be enabled. Keep only the preferred version shown in the preview.");
            if (selected.GroupBy(r => GameSelectionPolicy.FamilyKey(r.Name)).Any(g => g.Count() > 1))
                throw new InvalidOperationException("This workflow contains multiple versions of the same game. Review a fresh selection so only one preferred version runs.");
            await health.EnsureWritableAsync([data, JournalPath(plan.Id)], ct);
            Directory.CreateDirectory(Path.GetDirectoryName(JournalPath(plan.Id))!);
            await using var lease = new FileStream(Path.Combine(data, "workflows", ".run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (plan.ResumesWorkflowId.Length > 0 && FileFingerprint(JournalPath(plan.ResumesWorkflowId)) != plan.ResumeJournalHash)
                throw new InvalidOperationException("This workflow was already continued or changed after review. Review its latest unfinished state.");
            var journal = NewJournal(plan, selected);
            await SaveAsync(journal, ct); plan.Executed = true;
            if (plan.ResumesWorkflowId.Length > 0)
            {
                var previous = await LoadJournalAsync(plan.ResumesWorkflowId, ct);
                foreach (var game in previous.Games.Where(g => selected.Any(s => s.ProfileId.Equals(g.ProfileId, StringComparison.OrdinalIgnoreCase)))) game.SupersededBy = plan.Id;
                // Unchecked work remains in its old journal; selected work belongs exclusively to this continuation.
                if (previous.Games.Where(g => g.Stages.Any(Resumable)).All(g => g.SupersededBy.Length > 0)) previous.SupersededBy = plan.Id;
                await SaveAsync(previous, ct);
            }
            var result = new WorkflowRunResult { JournalPath = JournalPath(plan.Id), ReportPath = Path.Combine(data, "workflows", plan.Id + ".md") };
            var hashes = selected.SelectMany(r => r.StagePlans).SelectMany(s => s.SourceHashes).GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);
            bool cancelled = false;
            foreach (var row in selected)
            {
                var gameJournal = journal.Games.Single(g => g.ProfileId == row.ProfileId);
                foreach (var stage in row.StagePlans)
                {
                    var entry = gameJournal.Stages.Single(s => s.Stage == stage.Stage);
                    if (ct.IsCancellationRequested) { cancelled = true; break; }
                    try
                    {
                        foreach (var path in stage.SourceHashes.Keys)
                            if (FileFingerprint(path) != hashes[path]) throw new WorkflowNeedsReviewException("Source changed after review: " + path);
                        if (stage.RequiresReadyGame && !await runner.IsGameReadyAsync(settings, row, ct))
                        { entry.Status = "Blocked"; entry.Detail = "Installed primary/secondary game paths are not ready; dependent work was not performed."; }
                        else if (stage.Status is "Blocked" or "NeedsReview" or "Unsupported")
                        { entry.Status = stage.Status; entry.Detail = stage.Detail; }
                        else
                        {
                            entry.Status = "Running"; entry.Detail = stage.Detail; entry.UpdatedUtc = DateTimeOffset.UtcNow;
                            row.Status = stage.Stage + ": Running"; gameJournal.Status = "Running"; journal.Status = "Running";
                            await SaveAsync(journal, ct); progress?.Report(new(stage.Stage, row.Status, row.ProfileId));
                            stage.SourceHashes = stage.SourceHashes.Keys.ToDictionary(p => p, p => hashes[p], StringComparer.OrdinalIgnoreCase);
                            var outcome = await runner.ExecuteStageAsync(settings, data, row, stage, progress, ct);
                            entry.Status = outcome.Status; entry.Detail = outcome.Detail; entry.Artifacts = outcome.Artifacts?.ToList() ?? stage.Artifacts.ToList();
                            if (Done(outcome.Status))
                                foreach (var changedPath in outcome.ChangedPaths ?? []) { var path = Path.GetFullPath(changedPath); if (hashes.ContainsKey(path)) hashes[path] = FileFingerprint(path); }
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { entry.Status = "Interrupted"; entry.Detail = "Cancelled; retained service checkpoints require a fresh resume review."; cancelled = true; }
                    catch (WorkflowNeedsReviewException ex) { entry.Status = "NeedsReview"; entry.Detail = ex.Message; }
                    catch (VolumeHealthException) { throw; } // Never attempt further journal/media writes when storage becomes unsafe.
                    catch (Exception ex) { entry.Status = IsProcessDeferral(ex) ? "Deferred" : "Failed"; entry.Detail = ex.Message; }
                    entry.UpdatedUtc = DateTimeOffset.UtcNow;
                    row.Status = stage.Stage + ": " + entry.Status;
                    gameJournal.Status = gameJournal.Stages.All(s => Done(s.Status)) ? "Completed" : "Incomplete";
                    await SaveAsync(journal, CancellationToken.None);
                    progress?.Report(new(stage.Stage, entry.Status + ": " + entry.Detail, row.ProfileId));
                    if (cancelled) break;
                }
                row.Status = gameJournal.Stages.All(s => Done(s.Status)) ? "Completed" : cancelled ? "Interrupted" : "Incomplete";
                gameJournal.Status = row.Status;
                gameJournal.LaunchBoxGameGuid = row.LaunchBoxGameGuid;
                if (cancelled) break;
            }
            result.CompletedGames = journal.Games.Count(g => g.Stages.All(s => Done(s.Status)));
            result.IncompleteGames = journal.Games.Count - result.CompletedGames;
            result.SucceededStages = journal.Games.Sum(g => g.Stages.Count(s => s.Status == "Completed"));
            result.UnchangedStages = journal.Games.Sum(g => g.Stages.Count(s => s.Status == "Unchanged"));
            result.Errors = journal.Games.SelectMany(g => g.Stages.Where(s => !Done(s.Status)).Select(s => g.ProfileId + " / " + s.Stage + " — " + s.Status + ": " + s.Detail)).ToList();
            journal.Status = cancelled ? "Interrupted" : result.IncompleteGames == 0 ? "Completed" : "Incomplete";
            await SaveAsync(journal, CancellationToken.None);
            await health.EnsureWritableAsync([result.ReportPath], CancellationToken.None);
            SafeFiles.WriteText(result.ReportPath, Report(journal));
            return result;
        }
        finally { gate.Release(); }
    }

    public async Task<WorkflowJournal> LoadJournalAsync(string id, CancellationToken ct = default)
    {
        var path = JournalPath(id);
        var journal = JsonSerializer.Deserialize<WorkflowJournal>(await File.ReadAllTextAsync(path, ct), Json)
            ?? throw new InvalidDataException("Invalid workflow journal.");
        if (journal.App != "ArcadeLibraryManager" || journal.SchemaVersion != 1 || journal.Id != id)
            throw new InvalidDataException("Unrecognized workflow journal.");
        return journal;
    }
    public async Task<List<WorkflowJournal>> LoadUnfinishedAsync(CancellationToken ct = default)
    {
        var folder = Path.Combine(data, "workflows"); var result = new List<WorkflowJournal>();
        if (!Directory.Exists(folder)) return result;
        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            var journal = await LoadJournalAsync(Path.GetFileNameWithoutExtension(file), ct);
            if (journal.Status != "Completed" && journal.SupersededBy.Length == 0 && journal.Games.Any(g => g.SupersededBy.Length == 0 && g.Stages.Any(Resumable))) result.Add(journal);
        }
        return result.OrderByDescending(j => j.UpdatedUtc).ToList();
    }
    private async Task SaveAsync(WorkflowJournal journal, CancellationToken ct)
    {
        var path = JournalPath(journal.Id); await health.EnsureWritableAsync([path], ct);
        journal.UpdatedUtc = DateTimeOffset.UtcNow; SafeFiles.WriteText(path, JsonSerializer.Serialize(journal, Json));
    }
    private string JournalPath(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid workflow ID.");
        return SafeFiles.Child(data, "workflows/" + id + ".json");
    }
    private static WorkflowJournal NewJournal(WorkflowPlan p, List<WorkflowGamePlan> rows) => new() {
        Id = p.Id, CreatedUtc = p.CreatedUtc, SettingsHash = p.SettingsHash, DataPath = p.DataPath, Options = Clone(p.Options), ResumesWorkflowId = p.ResumesWorkflowId,
        Games = rows.Select(r => new WorkflowGameJournal { ProfileId = r.ProfileId, Name = r.Name, LaunchBoxGameGuid = r.LaunchBoxGameGuid,
            OriginalInstallPlan = r.OriginalInstallPlan, Stages = r.StagePlans.Select(s => new WorkflowStageJournal { Stage = s.Stage, Action = s.Action, Detail = s.Detail, Artifacts = s.Artifacts.ToList(), ReviewedSourceHashes = new(s.SourceHashes, StringComparer.OrdinalIgnoreCase), ConditionalAfterInstall = s.ConditionalAfterInstall, BezelSourceFingerprint = s.BezelSourceFingerprint }).ToList() }).ToList()
    };
    private static string Report(WorkflowJournal j)
    {
        var sb = new StringBuilder($"# Library workflow {j.Id}\n\nState: {j.Status}\n\nUpdated: {j.UpdatedUtc:O}\n\n");
        sb.AppendLine("Resume unfinished work through a fresh workflow review. Existing controls, artwork, alternate versions, and service rollback journals remain authoritative.\n");
        foreach (var g in j.Games) { sb.AppendLine($"## {g.Name} ({g.ProfileId})\n\nLaunchBox ID: {g.LaunchBoxGameGuid}\n"); foreach (var s in g.Stages) { sb.AppendLine($"- {s.Stage}: **{s.Status}** — {s.Detail}"); foreach (var artifact in s.Artifacts) sb.AppendLine("  - " + artifact); } sb.AppendLine(); }
        return sb.ToString();
    }
    private static bool IsProcessDeferral(Exception ex) => ex is InvalidOperationException &&
        (ex.Message.Contains("Close LaunchBox", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("Close TeknoParrot", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("opened during", StringComparison.OrdinalIgnoreCase));
    internal static bool Done(string status) => status is "Completed" or "Unchanged";
    internal static bool NonemptyFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { var info = new FileInfo(path); return info.Exists && info.Length > 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    internal static string FileFingerprint(string path) => File.Exists(path) ? SafeFiles.Hash(path) : "<missing>";
    private static string HashSettings(AppSettings s) => Hash(JsonSerializer.Serialize(s));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static T Clone<T>(T source) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(source))!;
    private static WorkflowOptions OptionsFor(ISet<string> s) => new() { InstallGames = s.Contains("Install"), SyncLaunchBox = s.Contains("LaunchBox"), GenerateMissingThemes = s.Contains("Theme"), InstallBezels = s.Contains("Bezel") };
    private static string Seal(WorkflowPlan p) => Hash(JsonSerializer.Serialize(new { p.Id, p.CreatedUtc, p.DataPath, p.Options, p.ResumesWorkflowId, p.ResumeJournalHash, Rows = p.Rows.Select(r => new { r.ProfileId, r.Name, r.ExcludedByVersionPolicy, r.InstallAction, r.Stages, r.Warnings, r.Blockers, r.LaunchBoxGameGuid, r.OriginalInstallPlan, r.StagePlans, Game = r.Game }) }));
    private static bool SupportedStage(string stage) => stage is "Install" or "LaunchBox" or "Theme" or "Bezel";
    private static bool RetiredStage(string stage) => stage is "Artwork" or "EmuMovies";
    private static bool Resumable(WorkflowStageJournal stage) => SupportedStage(stage.Stage) && !Done(stage.Status);
    private static void ValidateStages(WorkflowPlan p)
    {
        var allowed = p.Options.EnabledStages();
        foreach (var row in p.Rows)
            if (row.StagePlans.Count == 0 || row.StagePlans.Any(s => !SupportedStage(s.Stage) || !allowed.Contains(s.Stage)) || row.StagePlans.Select(s => s.Stage).Distinct().Count() != row.StagePlans.Count)
                throw new InvalidDataException("Workflow contains an unreviewed or duplicate category.");
    }
    private sealed class WorkflowNeedsReviewException(string message) : InvalidOperationException(message);
}

internal sealed class ExistingServicesWorkflowRunner(VolumeHealthService health, Func<bool> launchBoxRunning) : IWorkflowStageRunner
{
    private readonly MediaService media = new();
    public async Task<List<WorkflowGamePlan>> PreviewAsync(AppSettings settings, string dataDir, IReadOnlyList<string> gameIds, WorkflowOptions options, IProgress<JobEvent>? progress, CancellationToken ct)
    {
        var engine = new AppEngine(settings, dataDir, progress, health);
        var selected = gameIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var catalog = await engine.ScanAsync(ct);
        var games = catalog.Where(g => selected.Contains(g.Id)).ToList();
        var installs = options.InstallGames ? (await engine.PlanAsync(gameIds, ct)).ToDictionary(i => i.ProfileId, StringComparer.OrdinalIgnoreCase) : [];
        LaunchBoxPlan? lb = null; string lbError = "";
        if (options.SyncLaunchBox) try { lb = await new LaunchBoxService(processRunning: launchBoxRunning, healthService: health).PreviewAsync(settings, games, progress, ct); } catch (Exception ex) when (ex is not OperationCanceledException) { lbError = ex.Message; }
        BezelPlan? bezels = options.InstallBezels ? await new BezelService(healthService: health).PreviewAsync(settings, games, progress, ct) : null;
        var preferred = new Dictionary<string,GameRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var family in games.GroupBy(g => GameSelectionPolicy.FamilyKey(g.Name)))
        {
            var ready = catalog.Where(g => g.PathsValid && GameSelectionPolicy.FamilyKey(g.Name) == family.Key).ToList();
            var actionable = family.Where(g => installs.TryGetValue(g.Id, out var p) && p.Selected).ToList();
            var candidates = ready.Count > 0 ? ready : actionable.Count > 0 ? actionable : family.ToList();
            preferred[family.Key] = GameSelectionPolicy.SelectPreferred(candidates).Single();
        }
        var rows = new List<WorkflowGamePlan>();
        media.ClearCache();
        foreach (var game in games)
        {
            var row = new WorkflowGamePlan { ProfileId = game.Id, Name = game.Name, Game = game, OriginalInstallPlan = installs.GetValueOrDefault(game.Id) };
            row.InstallAction = row.OriginalInstallPlan?.Action ?? "Not requested";
            try { row.LaunchBoxGameGuid = string.IsNullOrWhiteSpace(settings.LaunchBoxPath) ? "" : LaunchBoxService.FindGameIdentity(settings, game)?.GameGuid ?? ""; } catch (Exception ex) when (ex is IOException or InvalidDataException) { row.Warnings = ex.Message; }
            bool conditional = !game.PathsValid && options.InstallGames && row.OriginalInstallPlan?.Selected == true;
            foreach (var name in options.EnabledStages())
            {
                var stage = new WorkflowStagePlan { Stage = name, RequiresReadyGame = name != "Install", ConditionalAfterInstall = name != "Install" && conditional };
                AddHash(stage, game.TemplatePath); AddHash(stage, game.UserProfilePath);
                if (name is "LaunchBox" or "Theme") foreach (var path in LaunchBoxSources(settings)) AddHash(stage, path);
                switch (name)
                {
                    case "Install":
                        stage.Action = row.InstallAction; stage.Detail = row.OriginalInstallPlan?.Detail ?? "Missing install plan.";
                        stage.Status = stage.Action == "Needs review" || row.OriginalInstallPlan == null ? "NeedsReview" : "Pending";
                        stage.RuntimePlan = row.OriginalInstallPlan; break;
                    case "LaunchBox":
                        var item = lb?.Items.SingleOrDefault(i => i.ProfileId == game.Id);
                        stage.Action = item?.Action ?? "NeedsReview"; stage.Detail = item?.Detail ?? lbError;
                        stage.Status = stage.Action is "NeedsReview" ? "NeedsReview" : "Pending";
                        if (item != null) { stage.RuntimePlan = item; row.LaunchBoxGameGuid = item.GameGuid; if (item.Action is "Combine" or "Consolidate") foreach (var pair in lb!.SourceHashes) stage.SourceHashes[pair.Key] = pair.Value; }
                        break;
                    case "Theme":
                        stage.Action = "Generate missing theme"; stage.Detail = "Preserve existing themes and reuse matching Arcade / Sega Model 2 themes first; rendering a new theme requires a local video snap.";
                        var themeAssets = media.FindAssets(settings, game);
                        stage.RuntimePlan = themeAssets;
                        if (themeAssets.Theme.Length > 0)
                        {
                            stage.Action = "Preserve existing theme";
                            if (!LibraryWorkflowService.NonemptyFile(themeAssets.Theme)) { stage.Status = "NeedsReview"; stage.Detail = "The existing theme is empty or unreadable. It is preserved for explicit repair."; }
                        }
                        else if (MediaService.HasReusableTheme(themeAssets))
                        {
                            stage.Action = "Reuse existing theme";
                            stage.Detail = $"Copy existing {themeAssets.ReusableThemePlatform} theme: {themeAssets.ReusableTheme} → {themeAssets.ThemeReuseDestination}. Source video preserved; no rendering required.";
                            AddHash(stage, themeAssets.ReusableTheme);
                            AddHash(stage, Path.Combine(settings.LaunchBoxPath, "Data", "Platforms", themeAssets.ReusableThemePlatform + ".xml"));
                        }
                        else if (!MediaService.HasVideoSnap(themeAssets))
                        {
                            stage.Status = "NeedsReview"; stage.Action = "Missing video snap"; stage.ConditionalAfterInstall = false;
                            stage.Detail = "A matching nonempty local video snap is required. Add it in LaunchBox, then review this stage again. Installing or syncing the game does not create a video snap.";
                        }
                        else if (ThemeRenderer.GetToolsError(settings) is { Length: > 0 } toolError) { stage.Status = "Blocked"; stage.Detail = toolError; }
                        if (!MediaService.HasReusableTheme(themeAssets) && themeAssets.Snap.Length > 0) AddHash(stage, themeAssets.Snap);
                        break;
                    case "Bezel":
                        var bezel = bezels!.Items.Single(i => i.ProfileId == game.Id);
                        stage.Action = bezel.Action; stage.Detail = bezel.Detail + " Approved display changes: enable bezel, fullscreen, and no stretch where supported; controls preserved.";
                        stage.Status = bezel.Action is "Unsupported" or "NeedsReview" ? bezel.Action : "Pending";
                        stage.RuntimePlan = bezel; stage.BezelSourceFingerprint = bezel.SourceSetFingerprint;
                        AddHash(stage, bezel.Source); AddHash(stage, bezel.Destination);
                        // Exact recursive candidate sets are fingerprinted above; retain file hashes for content changes and flat missing paths.
                        foreach (var root in new[] { settings.MameArtworkPath, settings.BezelImportPath }.Where(p => !string.IsNullOrWhiteSpace(p)))
                            foreach (var relative in new[] { game.Id + ".png", game.Id + ".zip", game.Id + "/bezel.png", game.Id + "/" + game.Id + ".png" }) AddHash(stage, Path.Combine(root, relative));
                        break;
                }
                if (stage.ConditionalAfterInstall) { stage.Action = "After install: " + stage.Action; stage.Detail = "Recheck this exact profile after its reviewed installation. " + stage.Detail; if (stage.Status == "NeedsReview" && name == "LaunchBox" && lbError.Length == 0) stage.Status = "Pending"; }
                row.StagePlans.Add(stage);
            }
            var winner = preferred[GameSelectionPolicy.FamilyKey(game.Name)];
            if (!winner.Id.Equals(game.Id, StringComparison.OrdinalIgnoreCase) || row.InstallAction == "Alternate version")
            {
                row.ExcludedByVersionPolicy = true; row.Selected = false;
                var detail = $"One version per game: {winner.Name} ({winner.Id}) is preferred. USA, then World, then other regions; existing ready profiles are retained.";
                foreach (var stage in row.StagePlans) { stage.Action = "Alternate version"; stage.Status = "NeedsReview"; stage.Detail = detail; stage.ConditionalAfterInstall = false; }
            }
            row.Stages = string.Join(" → ", row.StagePlans.Select(s => s.Stage + ": " + s.Action));
            row.Blockers = string.Join("; ", row.StagePlans.Where(s => s.Status is "Blocked" or "NeedsReview" or "Unsupported").Select(s => s.Stage + ": " + s.Detail));
            if (game.SubscriptionRequired) row.Warnings += " TeknoParrot subscription may be required. Paths present does not verify gameplay.";
            rows.Add(row);
        }
        return rows;
    }
    public Task<bool> IsGameReadyAsync(AppSettings settings, WorkflowGamePlan row, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var game = row.Game; game.Installed = File.Exists(game.UserProfilePath); game.PathsValid = false;
        if (!game.Installed) return Task.FromResult(false);
        var root = LaunchBoxService.ReadXml(game.UserProfilePath).Root!;
        string Resolve(string name) { var path = root.Element(name)?.Value ?? ""; return path.Length == 0 ? "" : LaunchBoxService.FullPath(settings.TeknoParrotPath, path); }
        game.GamePath = Resolve("GamePath"); game.GamePath2 = Resolve("GamePath2");
        game.PathsValid = File.Exists(game.GamePath) && (!game.HasTwoExecutables || File.Exists(game.GamePath2));
        return Task.FromResult(game.PathsValid);
    }
    public async Task<WorkflowStageOutcome> ExecuteStageAsync(AppSettings settings, string dataDir, WorkflowGamePlan row, WorkflowStagePlan stage, IProgress<JobEvent>? progress, CancellationToken ct)
    {
        if (stage.Stage is "LaunchBox" or "Theme" && launchBoxRunning())
            return new("Deferred", "Close LaunchBox and BigBox, then review resume for this game.");
        switch (stage.Stage)
        {
            case "Install":
                if (await IsGameReadyAsync(settings, row, ct)) return new("Unchanged", "Existing primary and secondary launch paths are ready; preserved.");
                var install = row.OriginalInstallPlan ?? throw new InvalidOperationException("Original reviewed installation plan is missing.");
                if (!install.Selected) return new("NeedsReview", install.Detail);
                var installed = await new AppEngine(settings, dataDir, progress, health).ExecuteAsync([install], ct);
                if (installed.Errors.Count > 0) return new("Failed", string.Join("; ", installed.Errors));
                if (!await IsGameReadyAsync(settings, row, ct)) return new("Blocked", "Installer did not produce valid primary and secondary launch paths. Inspect its retained checkpoint.");
                return new("Completed", "Installed profile paths verified; controls retained.", [row.Game.UserProfilePath]);
            case "LaunchBox":
                var lbService = new LaunchBoxService(processRunning: launchBoxRunning, healthService: health);
                var lb = await lbService.PreviewAsync(settings, [row.Game], progress, ct);
                AssertExpected(stage);
                var item = lb.Items.Single(i => i.ProfileId == row.ProfileId);
                if (item.Action == "Unchanged") return new("Unchanged", item.Detail);
                if (item.Action is not ("Add" or "Update" or "Combine" or "Consolidate")) return new(item.Action == "NotReady" ? "Blocked" : "NeedsReview", item.Detail);
                var synced = await lbService.ApplyAsync(lb, progress, ct);
                if (synced.Errors.Count > 0 || synced.Succeeded != 1) return new("Failed", string.Join("; ", synced.Errors.DefaultIfEmpty("LaunchBox did not commit this entry.")));
                row.LaunchBoxGameGuid = item.GameGuid;
                return new("Completed", item.Action + ": " + item.Detail, new[] { lb.SourcePath }.Concat(lb.RelatedDocuments.Keys).ToArray(), [Path.Combine(Path.GetDirectoryName(lb.BackupPath)!, "rollback.json")]);
            case "Theme":
                var themeIdentity = LaunchBoxService.FindGameIdentity(settings, row.Game);
                if (themeIdentity == null || themeIdentity.Duplicate) return new("Blocked", "A unique LaunchBox entry is required to name the theme safely.");
                media.ClearCache(); var assets = media.FindAssets(settings, row.Game);
                AssertExpected(stage);
                if (assets.Theme.Length > 0) return LibraryWorkflowService.NonemptyFile(assets.Theme)
                    ? new("Unchanged", "Existing nonempty theme preserved; playback was not tested.", Artifacts: [assets.Theme])
                    : new("NeedsReview", "The existing theme is empty or unreadable. It was preserved for explicit repair.", Artifacts: [assets.Theme]);
                if (MediaService.HasReusableTheme(assets))
                {
                    if (stage.RuntimePlan is not MediaAssets reviewedTheme ||
                        !assets.ReusableTheme.Equals(reviewedTheme.ReusableTheme, StringComparison.OrdinalIgnoreCase) ||
                        !assets.ThemeReuseDestination.Equals(reviewedTheme.ThemeReuseDestination, StringComparison.OrdinalIgnoreCase))
                        return new("NeedsReview", "The matching existing theme or its destination changed. Review the theme stage again before copying.");
                    var reusedOutput = await new ThemeRenderer(health).RenderAsync(settings, assets, progress, ct);
                    return new("Completed", $"Existing {assets.ReusableThemePlatform} theme copied and verified; source video preserved.", Artifacts: [reusedOutput]);
                }
                if (stage.RuntimePlan is MediaAssets priorTheme && MediaService.HasReusableTheme(priorTheme))
                    return new("NeedsReview", "The reviewed existing theme is no longer available. Review this stage again; no replacement was rendered.");
                if (!LibraryWorkflowService.NonemptyFile(assets.Background)) assets.Background = "";
                if (!LibraryWorkflowService.NonemptyFile(assets.Logo)) assets.Logo = "";
                if (!MediaService.HasVideoSnap(assets)) return new("NeedsReview", "A matching nonempty local video snap is required. No theme was created. Add the gameplay snap in LaunchBox, then review resume.");
                var output = await new ThemeRenderer(health).RenderAsync(settings, assets, progress, ct);
                return File.Exists(output) ? new("Completed", "Missing theme rendered and validated.", Artifacts: [output]) : new("Failed", "Renderer returned without a theme file.");
            case "Bezel":
                var bezelService = new BezelService(healthService: health);
                var bezel = await bezelService.PreviewAsync(settings, [row.Game], progress, ct);
                AssertExpected(stage);
                var b = bezel.Items.Single(i => i.ProfileId == row.ProfileId);
                if (b.Action is not ("Unchanged" or "Enable") && b.SourceSetFingerprint != stage.BezelSourceFingerprint)
                    return new("NeedsReview", "The exact bezel source candidates changed after review. Review the local repository again before installing; no bezel or profile was changed.");
                if (b.Action == "Unchanged") return new("Unchanged", b.Detail);
                if (b.Action is not ("Install" or "Enable")) return new(b.Action == "Missing" ? "NeedsUserAction" : b.Action == "NotReady" ? "Blocked" : b.Action, b.Detail);
                var applied = await bezelService.ApplyAsync(bezel, progress, ct);
                if (applied.Errors.Count > 0 || applied.Succeeded != 1) return new("Failed", string.Join("; ", applied.Errors.DefaultIfEmpty("Bezel was not applied.")));
                return new("Completed", b.Detail, [row.Game.UserProfilePath, b.Destination], bezel.BackupPaths);
            default: throw new InvalidDataException("Unknown workflow stage.");
        }
    }
    private static IEnumerable<string> LaunchBoxSources(AppSettings s) => string.IsNullOrWhiteSpace(s.LaunchBoxPath) ? [] : new[] { LaunchBoxService.PlatformFile(s), Path.Combine(s.LaunchBoxPath, "Data", "Emulators.xml"), Path.Combine(s.LaunchBoxPath, "Data", "Platforms.xml") };
    private static void AddHash(WorkflowStagePlan stage, string path) { if (!string.IsNullOrWhiteSpace(path)) { path = Path.GetFullPath(path); stage.SourceHashes[path] = LibraryWorkflowService.FileFingerprint(path); } }
    private static void AssertExpected(WorkflowStagePlan stage)
    {
        foreach (var source in stage.SourceHashes)
            if (LibraryWorkflowService.FileFingerprint(source.Key) != source.Value) throw new InvalidOperationException("Source changed during dependent preview; review again: " + source.Key);
    }
}
