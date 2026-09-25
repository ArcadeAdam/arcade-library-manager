using System.ComponentModel;
using System.Text.Json;
using ArcadeLibraryManager.Core;

namespace ArcadeLibraryManager.Desktop;

public sealed partial class MainForm
{
    private WorkflowPlan? _workflowPlan;
    private WorkflowRunResult? _workflowResult;
    private string _workflowOptionsReviewed = "";
    private readonly Dictionary<string, CheckBox> _workflowStages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _workflowLatest = new(StringComparer.OrdinalIgnoreCase);
    private readonly BindingList<WorkflowActivity> _workflowActivity = new();
    private readonly BindingList<WorkflowStageStatus> _workflowStageStatus = new();
    private DataGridView _workflowGrid = null!;
    private Label _workflowSelectionSummary = null!, _workflowPlanSummary = null!;
    private bool _workflowCapturing;
    private string _workflowReportPath = "";

    private void BuildWorkflowPage()
    {
        var page = Page("Maintenance");
        var choices = new Panel { Dock = DockStyle.Top, Height = 139, BackColor = Palette.Surface, Padding = new Padding(17, 12, 17, 8) };
        var title = Palette.Label("Choose the work for this batch", 12, Palette.Ink, true); title.Dock = DockStyle.Top; title.Height = 24;
        _workflowSelectionSummary = new Label { Text = "Select games in Library, then choose the stages to include.", Dock = DockStyle.Bottom, Height = 24, ForeColor = Palette.Muted, Font = Palette.Font(9.3f) };
        var stageChoices = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, Padding = new Padding(0, 3, 0, 0) };
        foreach (var (key, label) in new[] { ("InstallGames", "Install / repair games"), ("SyncLaunchBox", "Sync LaunchBox"), ("GenerateMissingThemes", "Create missing themes"), ("InstallBezels", "Set up bezels") }) {
            var check = new CheckBox { Text = label, AutoSize = true, Font = Palette.Font(9.4f), ForeColor = Palette.Ink, Margin = new Padding(0, 5, 19, 7), UseVisualStyleBackColor = true };
            _workflowStages[key] = check; stageChoices.Controls.Add(check);
        }
        choices.Controls.Add(stageChoices); choices.Controls.Add(_workflowSelectionSummary); choices.Controls.Add(title);
        var actions = Toolbar(WorkButton("Review maintenance", async (_, _) => await PreviewWorkflowAsync(), false, 178), WorkButton("Run selected plan", async (_, _) => await RunWorkflowAsync(), true, 166), WorkButton("Resume unfinished…", async (_, _) => await ResumeWorkflowAsync(), false, 178), WorkButton("Export report…", (_, _) => ExportWorkflowReport(), false, 135));
        actions.Padding = new Padding(0, 8, 0, 0); actions.Height = 56;
        _workflowPlanSummary = new Label { Text = "No maintenance plan yet. Review a batch before running it.", Dock = DockStyle.Top, Height = 43, Font = Palette.Font(10, FontStyle.Bold), ForeColor = Palette.Ink, Padding = new Padding(0, 3, 0, 0) };
        _workflowGrid = Palette.Grid(); Palette.CheckColumn(_workflowGrid); Palette.TextColumn(_workflowGrid, "Name", "GAME", 145); Palette.TextColumn(_workflowGrid, "InstallAction", "FILES", 108); Palette.TextColumn(_workflowGrid, "Stages", "SELECTED STAGES", 145); Palette.TextColumn(_workflowGrid, "Review", "REVIEW NOTES", 155); Palette.TextColumn(_workflowGrid, "Status", "LATEST STATUS", 170);
        _workflowGrid.CellBeginEdit += (_, e) => { if (e.ColumnIndex == 0 && _workflowGrid.Rows[e.RowIndex].DataBoundItem is WorkflowSelection row && !row.CanSelect) e.Cancel = true; };
        _workflowGrid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && _workflowGrid.Rows[e.RowIndex].DataBoundItem is WorkflowSelection row) ShowDetails(row.Name, $"Profile: {row.ProfileId}\r\nFiles: {row.InstallAction}\r\nStages: {row.Stages}\r\n\r\nReviewed stage actions:\r\n{row.StageDetails}\r\n\r\nReview notes:\r\n{row.Review}\r\n\r\nLatest status:\r\n{row.Status}"); };
        var tabs = new ThemeTabs { Dock = DockStyle.Fill, Font = Palette.Font(9.5f), Padding = new Point(14, 8) };
        var reviewTab = new TabPage("Reviewed games") { Padding = new Padding(0, 7, 0, 0), BackColor = Palette.Page }; reviewTab.Controls.Add(_workflowGrid);
        var activityTab = new TabPage("Stage activity") { Padding = new Padding(0, 7, 0, 0), BackColor = Palette.Page };
        var activity = Palette.Grid(); Palette.TextColumn(activity, "Time", "TIME", 65); Palette.TextColumn(activity, "Game", "PROFILE", 95); Palette.TextColumn(activity, "Stage", "STAGE", 100); Palette.TextColumn(activity, "Message", "RESULT / PROGRESS", 320); activity.DataSource = _workflowActivity;
        activity.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && activity.Rows[e.RowIndex].DataBoundItem is WorkflowActivity entry) ShowDetails(entry.Stage, entry.Message); }; activityTab.Controls.Add(activity);
                var stageTab = new TabPage("Saved stage status") { Padding = new Padding(0, 7, 0, 0), BackColor = Palette.Page };
        var stageTable = Palette.Grid(); Palette.TextColumn(stageTable, "Game", "GAME", 140); Palette.TextColumn(stageTable, "Stage", "STAGE", 90); Palette.TextColumn(stageTable, "Status", "STATE", 105); Palette.TextColumn(stageTable, "Detail", "RESULT / NEXT STEP", 270); stageTable.DataSource = _workflowStageStatus;
        stageTable.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && stageTable.Rows[e.RowIndex].DataBoundItem is WorkflowStageStatus stage) ShowDetails(stage.Game + " — " + stage.Stage, stage.Status + "\r\n\r\n" + stage.Detail); }; stageTab.Controls.Add(stageTable);
        tabs.TabPages.AddRange([reviewTab, stageTab, activityTab]);
        var resultActions = Toolbar(WorkButton("Open saved report", (_, _) => { if (File.Exists(_workflowReportPath)) OpenLocal(_workflowReportPath); else SetStatus("Run a maintenance batch to create its saved report."); }, false, 164), WorkButton("Choose games in Library", (_, _) => ShowPage("Library"), false, 201), WorkButton("Use all candidate profiles", (_, _) => { SelectAllLibraryCandidates(); UpdateWorkflowSelectionSummary(); }, false, 194)); resultActions.Dock = DockStyle.Bottom;
        var note = Hint("Stages follow the reviewed order. Theme videos use the artwork and gameplay snaps already in your library.", 45); note.Dock = DockStyle.Bottom;
        page.Controls.Add(tabs); page.Controls.Add(note); page.Controls.Add(resultActions); page.Controls.Add(_workflowPlanSummary); page.Controls.Add(actions); page.Controls.Add(choices);
    }
    private WorkflowOptions ReadWorkflowOptions() => new() {
        InstallGames = _workflowStages["InstallGames"].Checked, SyncLaunchBox = _workflowStages["SyncLaunchBox"].Checked,
        GenerateMissingThemes = _workflowStages["GenerateMissingThemes"].Checked,
        InstallBezels = _workflowStages["InstallBezels"].Checked
    };
    private void SetWorkflowOptions(WorkflowOptions options)
    {
        _workflowStages["InstallGames"].Checked = options.InstallGames; _workflowStages["SyncLaunchBox"].Checked = options.SyncLaunchBox;
        _workflowStages["GenerateMissingThemes"].Checked = options.GenerateMissingThemes;
        _workflowStages["InstallBezels"].Checked = options.InstallBezels;
    }
    private LibraryWorkflowService WorkflowService() => new(_settings, _dataDirectory);
    private void UpdateWorkflowSelectionSummary()
    {
        if (_workflowSelectionSummary == null) return;
        var count = SelectedLibraryGameIds().Count;
        _workflowSelectionSummary.Text = _fixture ? $"Fixture preview only · {count:N0} example games selected · no live library accessed" : $"{count:N0} filtered Library profiles selected. Review keeps one available version: USA, World, then other.";
    }
    private async Task PreviewWorkflowAsync()
    {
        _gameGrid.EndEdit(); var ids = SelectedLibraryGameIds();
        if (ids.Count == 0) { SetStatus("Select games in Library before reviewing maintenance."); return; }
        var options = ReadWorkflowOptions();
        if (!options.InstallGames && !options.SyncLaunchBox && !options.GenerateMissingThemes && !options.InstallBezels) { SetStatus("Choose at least one maintenance stage to review."); return; }
        await RunWork("Preparing the complete maintenance review…", async ct => {
            ReadSettingsFromControls(); SettingsStore.Save(_dataDirectory, _settings); _workflowCapturing = true; _workflowActivity.Clear(); _workflowLatest.Clear();
            try {
                _workflowPlan = await WorkflowService().PreviewAsync(ids, options, _progress, ct); _workflowResult = null; _workflowReportPath = ""; _workflowStageStatus.Clear();
                _workflowOptionsReviewed = JsonSerializer.Serialize(_workflowPlan.Options); BindWorkflowPlan();
                SetStatus("Maintenance plan ready. Inspect each game's stages and notes, then run the checked games.");
            } finally { _workflowCapturing = false; }
        });
    }
    private void BindWorkflowPlan()
    {
        if (_workflowPlan == null) { _workflowGrid.DataSource = null; _workflowPlanSummary.Text = "No maintenance plan yet."; return; }
        _workflowGrid.DataSource = new BindingList<WorkflowSelection>(_workflowPlan.Rows.Select(row => new WorkflowSelection(row, _workflowLatest)).ToList());
        _workflowPlanSummary.Text = $"{_workflowPlan.Rows.Count:N0} games reviewed   ·   {StageNames(_workflowPlan.Options)}";
        UpdateWorkflowSelectionSummary();
    }
    private async Task RunWorkflowAsync()
    {
        _workflowGrid.EndEdit(); ReadSettingsFromControls();
        if (_workflowPlan == null || !_workflowPlan.Rows.Any(r => r.Selected)) { SetStatus("Review maintenance and select one or more games first."); return; }
        if (JsonSerializer.Serialize(ReadWorkflowOptions()) != _workflowOptionsReviewed) { SetStatus("The selected stages changed. Review maintenance again before running."); return; }
        await RunWork("Running the reviewed maintenance stages…", async ct => {
            _workflowCapturing = true;
            foreach (var check in _workflowStages.Values) check.Enabled = false;
            try {
                _workflowResult = await WorkflowService().ExecuteAsync(_workflowPlan, _settings, _progress, ct);
                _workflowReportPath = _workflowResult.ReportPath;
                await RefreshWorkflowStageStatusAsync(_workflowPlan.Id, ct);
                _workflowGrid.Refresh();
                _workflowPlanSummary.Text = $"{_workflowResult.CompletedGames:N0} games complete   ·   {_workflowResult.IncompleteGames:N0} incomplete   ·   {_workflowResult.SucceededStages:N0} stages completed   ·   {_workflowResult.UnchangedStages:N0} unchanged";
                SetStatus(_workflowStageStatus.Any(s => s.Status.Equals("NeedsUserAction", StringComparison.OrdinalIgnoreCase)) ? "Maintenance needs your attention. Review Saved stage status before resuming this batch." : "Maintenance finished. Inspect saved stage status or open the report for results.");
                if (_workflowResult.Errors.Count > 0) ShowDetails("Maintenance items need attention", string.Join("\r\n", _workflowResult.Errors));
            } finally {
                try { await RefreshWorkflowStageStatusAsync(_workflowPlan.Id, CancellationToken.None); } catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
                _workflowCapturing = false; foreach (var check in _workflowStages.Values) check.Enabled = true;
            }
        });
    }
    private async Task ResumeWorkflowAsync()
    {
        List<WorkflowJournal>? unfinished = null;
        await RunWork("Finding unfinished maintenance batches…", async ct => { ReadSettingsFromControls(); unfinished = await WorkflowService().LoadUnfinishedAsync(ct); SetStatus($"Found {unfinished.Count:N0} unfinished maintenance batches."); });
        if (unfinished == null || unfinished.Count == 0) return;
        using var dialog = new Form { Text = "Resume an unfinished maintenance batch", ClientSize = new Size(850, 400), StartPosition = FormStartPosition.CenterParent, BackColor = Palette.Page, Padding = new Padding(20), Font = Palette.Font(), MinimizeBox = false, MaximizeBox = false };
        var table = Palette.Grid(); table.MultiSelect = false; Palette.TextColumn(table, "Id", "BATCH", 185); Palette.TextColumn(table, "Status", "STATUS", 105); Palette.TextColumn(table, "Updated", "UPDATED UTC", 135); Palette.TextColumn(table, "Games", "GAMES", 55);
        var rows = unfinished.Select(j => new WorkflowResumeSelection(j)).ToList(); table.DataSource = rows;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 55, Padding = new Padding(0, 9, 0, 0), FlowDirection = FlowDirection.RightToLeft };
        var review = Palette.Button("Review selected batch", true, 204); var close = Palette.Button("Cancel", false, 108); close.DialogResult = DialogResult.Cancel;
        string? selected = null; review.Click += (_, _) => { if (table.CurrentRow?.DataBoundItem is WorkflowResumeSelection row) { selected = row.Id; dialog.DialogResult = DialogResult.OK; } };
        buttons.Controls.AddRange([review, close]); dialog.CancelButton = close;
        dialog.Controls.Add(table); dialog.Controls.Add(buttons); dialog.Controls.Add(Hint("Resume rebuilds a review from current files. It reuses successful stages and leaves conflicts for review before any new writes.", 48));
        if (dialog.ShowDialog(this) != DialogResult.OK || selected == null) return;
        await RunWork("Rechecking unfinished maintenance work…", async ct => {
            ReadSettingsFromControls(); _workflowCapturing = true; _workflowActivity.Clear(); _workflowLatest.Clear();
            try {
                _workflowPlan = await WorkflowService().PreviewResumeAsync(selected, _progress, ct);
                SetWorkflowOptions(_workflowPlan.Options); _workflowOptionsReviewed = JsonSerializer.Serialize(_workflowPlan.Options); _workflowResult = null;
                _workflowReportPath = ""; _workflowStageStatus.Clear(); BindWorkflowPlan();
                await RefreshWorkflowStageStatusAsync(selected, ct);
                SetStatus("Unfinished batch restored to review. Run the checked games when the plan looks right.");
            } finally { _workflowCapturing = false; }
        });
    }
    private async Task RefreshWorkflowStageStatusAsync(string id, CancellationToken ct)
    {
        var journal = await WorkflowService().LoadJournalAsync(id, ct);
        if (journal == null) return;
        _workflowStageStatus.Clear();
        foreach (var game in journal.Games) {
            _workflowLatest[game.ProfileId] = game.Status;
            foreach (var stage in game.Stages) {
                _workflowStageStatus.Add(new WorkflowStageStatus(game.Name, stage.Stage, stage.Status, stage.Detail + (stage.Artifacts.Count > 0 ? "\r\n\r\nSaved files:\r\n" + string.Join("\r\n", stage.Artifacts) : "")));
            }
        }
        _workflowGrid.Refresh();
    }
    private void AppendWorkflowProgress(JobEvent item)
    {
        if (!_workflowCapturing) return;
        _workflowActivity.Insert(0, new WorkflowActivity(DateTime.Now.ToString("HH:mm:ss"), item.GameId, item.Stage, item.Message));
        if (_workflowActivity.Count > 2000) _workflowActivity.RemoveAt(_workflowActivity.Count - 1);
        if (!string.IsNullOrWhiteSpace(item.GameId)) { _workflowLatest[item.GameId] = item.Stage + ": " + item.Message; _workflowGrid?.Refresh(); }
    }
    private void ExportWorkflowReport()
    {
        if (_workflowPlan == null) { SetStatus("Review a maintenance batch before exporting its report."); return; }
        _workflowGrid.EndEdit(); var saved = File.Exists(_workflowReportPath);
        using var dialog = new SaveFileDialog { Title = "Export the reviewed maintenance plan and results", Filter = saved ? "Markdown report|*.md" : "JSON plan report|*.json", FileName = saved ? "arcade-maintenance-report.md" : "arcade-maintenance-plan.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (saved) { if (!Path.GetFullPath(_workflowReportPath).Equals(Path.GetFullPath(dialog.FileName), StringComparison.OrdinalIgnoreCase)) File.Copy(_workflowReportPath, dialog.FileName, true); }
        else File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(new { Application = AppInfo.DisplayName, ApplicationVersion = AppInfo.Version, CreatedUtc = DateTime.UtcNow, Options = _workflowPlan.Options, Games = _workflowPlan.Rows.Select(r => new { r.Selected, r.ProfileId, r.Name, r.InstallAction, r.Stages, r.Warnings, r.Blockers, r.Status }), Activity = _workflowActivity.ToList(), Result = _workflowResult }, SettingsStore.Json));
        SetStatus("Maintenance report exported.");
    }
    private static string StageNames(WorkflowOptions options) => string.Join(" → ", options.EnabledStages().Select(stage => stage switch { "Theme" => "Themes", "Bezel" => "Bezels", _ => stage }));
    private static string WorkflowText(object? value) => value is IEnumerable<string> list ? string.Join("; ", list) : value?.ToString() ?? "";
    private static int WorkflowCount(object? value) => value is System.Collections.ICollection collection ? collection.Count : value is System.Collections.IEnumerable sequence ? sequence.Cast<object>().Count() : 0;
    private sealed class WorkflowSelection(WorkflowGamePlan row, Dictionary<string, string> latest)
    {
        public bool Selected { get => row.Selected; set => row.Selected = value && !row.ExcludedByVersionPolicy; }
        public bool CanSelect => !row.ExcludedByVersionPolicy;
        public string ProfileId => row.ProfileId; public string Name => row.Name; public string InstallAction => row.InstallAction;
        public string StageDetails => string.Join("\r\n\r\n", row.StagePlans.Select(stage => $"{stage.Stage} — {stage.Action}\r\nStatus at review: {stage.Status}\r\n{stage.Detail}"));
        public string Stages => WorkflowText(row.Stages); public string Review => string.Join(" · ", new[] { WorkflowText(row.Blockers), WorkflowText(row.Warnings) }.Where(s => !string.IsNullOrWhiteSpace(s)));
        public string Status => latest.GetValueOrDefault(row.ProfileId, row.Status);
    }
    private sealed class WorkflowResumeSelection(WorkflowJournal journal)
    {
        public string Id => journal.Id; public string Status => journal.Status; public string Updated => journal.UpdatedUtc.ToString() ?? ""; public int Games => WorkflowCount(journal.Games);
    }
    private sealed record WorkflowActivity(string Time, string Game, string Stage, string Message);
    private sealed record WorkflowStageStatus(string Game, string Stage, string Status, string Detail);
    private void PopulateWorkflowFixture()
    {
        _workflowStages["InstallGames"].Checked = true; _workflowStages["SyncLaunchBox"].Checked = true; _workflowStages["GenerateMissingThemes"].Checked = true;
        _workflowGrid.DataSource = _games.Where(g => !g.Installed).Select(g => new { Selected = true, g.Name, InstallAction = "Fixture preview", Stages = "Install; LaunchBox; Theme", Review = "Example data only", Status = "No operation has run" }).ToList();
        _workflowPlanSummary.Text = "Fixture preview · an example of a reviewed maintenance batch";
    }
}






