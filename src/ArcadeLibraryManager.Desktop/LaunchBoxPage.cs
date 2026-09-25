using System.ComponentModel;
using System.Text.Json;
using ArcadeLibraryManager.Core;

namespace ArcadeLibraryManager.Desktop;

public sealed partial class MainForm
{
    private LaunchBoxPlan? _launchBoxPlan;
    private string _launchBoxSettings = "";
    private DataGridView _launchBoxGrid = null!;
    private Label _launchBoxSummary = null!;
    private string _lastLaunchBoxJournal = "";
    private void BuildLaunchBoxPage()
    {
        var page = Page("LaunchBox");
        _launchBoxSummary = new Label { Text = "Preview synchronization against your existing TeknoParrot platform.", Dock = DockStyle.Top, Height = 49, Font = Palette.Font(11, FontStyle.Bold), ForeColor = Palette.Ink };
        var actions = Toolbar(WorkButton("Preview sync", async (_, _) => await PreviewLaunchBoxAsync(), false, 135), WorkButton("Apply selected changes", async (_, _) => await ApplyLaunchBoxAsync(), true, 202), WorkButton("Select changes", (_, _) => { if (_launchBoxPlan != null) { foreach (var item in _launchBoxPlan.Items) item.Selected = item.Action is "Add" or "Update" or "Combine" or "Consolidate"; _launchBoxGrid.Refresh(); } }, false, 140), WorkButton("Undo last sync", async (_, _) => await UndoLaunchBoxAsync(), false, 138));
        _launchBoxGrid = Palette.Grid(); Palette.CheckColumn(_launchBoxGrid); Palette.TextColumn(_launchBoxGrid, "Name", "GAME", 185); Palette.TextColumn(_launchBoxGrid, "Action", "ACTION", 95); Palette.TextColumn(_launchBoxGrid, "Detail", "CHANGES / PRESERVATION", 260);
        _launchBoxGrid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && _launchBoxGrid.Rows[e.RowIndex].DataBoundItem is LaunchBoxPlanItem row) ShowDetails(row.Name, $"Profile: {row.ProfileId}\r\nAction: {row.Action}\r\n{row.Detail}\r\n\r\nLaunchBox game ID: {row.GameGuid}\r\nDatabase ID: {row.DatabaseId}"); };
        _launchBoxGrid.CellBeginEdit += (_, e) => { if (e.ColumnIndex == 0 && _launchBoxGrid.Rows[e.RowIndex].DataBoundItem is LaunchBoxPlanItem row && row.Action is not ("Add" or "Update" or "Combine" or "Consolidate")) e.Cancel = true; };
        var bottom = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Bottom, Height = 76, BorderStyle = BorderStyle.None, BackColor = Palette.Soft, ForeColor = Palette.Muted, Font = Palette.Font(9),
            Text = "  One entry per game: USA, then World, then another ready region; prefer ELFLoader2 when ready.\r\n  Consolidation backs up duplicate records and keeps one entry; ROM files are unchanged.\r\n  Close LaunchBox and Big Box before committing. A verified backup supports undo." };
        page.Controls.Add(_launchBoxGrid); page.Controls.Add(bottom); page.Controls.Add(Hint("Preview consolidates verified regional/version duplicates and combines ELF/ELFLoader2 pairs. Older ELF loaders remain additional applications. Ambiguous entries need review.", 42)); page.Controls.Add(actions); page.Controls.Add(_launchBoxSummary);
    }
    private async Task PreviewLaunchBoxAsync()
    {
        await RunWork("Preparing the LaunchBox synchronization preview…", async ct => {
            ReadSettingsFromControls(); SettingsStore.Save(_dataDirectory, _settings);
            _games = await Task.Run(() => Engine().ScanAsync(ct), ct); UpdateLibrary();
            _launchBoxPlan = await new LaunchBoxService().PreviewAsync(_settings, _games, _progress, ct);
            _launchBoxSettings = JsonSerializer.Serialize(_settings);
            _launchBoxGrid.DataSource = new BindingList<LaunchBoxPlanItem>(_launchBoxPlan.Items);
            var add = _launchBoxPlan.Items.Count(x => x.Action == "Add"); var update = _launchBoxPlan.Items.Count(x => x.Action == "Update"); var combine = _launchBoxPlan.Items.Count(x => x.Action == "Combine"); var consolidate = _launchBoxPlan.Items.Count(x => x.Action == "Consolidate");
            _launchBoxSummary.Text = $"{_launchBoxPlan.PlatformName}   ·   {add:N0} new entries   ·   {update:N0} updates   ·   {combine:N0} loader pairs   ·   {consolidate:N0} duplicate groups   ·   {_launchBoxPlan.Items.Count(x => x.Action == "NeedsReview"):N0} for review   ·   {_launchBoxPlan.Items.Count(x => x.Action == "SkippedVariant"):N0} alternate versions skipped";
            SetStatus("LaunchBox preview ready. Review the checked changes, then apply.");
        });
    }
    private async Task ApplyLaunchBoxAsync()
    {
        _launchBoxGrid.EndEdit(); ReadSettingsFromControls();
        if (_launchBoxPlan == null || !_launchBoxPlan.CanApply) { SetStatus("Preview a sync and select one or more Add, Update, Combine or Consolidate actions first."); return; }
        if (_launchBoxSettings != JsonSerializer.Serialize(_settings)) { SetStatus("Setup changed after the preview. Preview LaunchBox synchronization again."); return; }
        await RunWork("Backing up and synchronizing LaunchBox…", async ct => {
            var result = await new LaunchBoxService().ApplyAsync(_launchBoxPlan, _progress, ct);
            if (!string.IsNullOrWhiteSpace(_launchBoxPlan.BackupPath)) _lastLaunchBoxJournal = Path.Combine(Path.GetDirectoryName(_launchBoxPlan.BackupPath)!, "rollback.json");
            ShowResult("LaunchBox synchronization", result);
            _launchBoxSettings = "";
        });
    }
    private async Task UndoLaunchBoxAsync()
    {
        var journal = _lastLaunchBoxJournal;
        if (string.IsNullOrWhiteSpace(journal)) {
            using var dialog = new OpenFileDialog { Title = "Choose a LaunchBox rollback journal", Filter = "Arcade Library Manager rollback|rollback.json|JSON files|*.json" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return; journal = dialog.FileName;
        }
        var preview = JsonSerializer.Deserialize<LaunchBoxRollback>(File.ReadAllText(journal));
        if (preview == null) { SetStatus("The selected rollback journal is invalid."); return; }
        if (MessageBox.Show(this, $"Restore the LaunchBox platform backup from {preview.CommittedUtc.LocalDateTime:g}?\n\n{preview.SourcePath}\n\nRestore is allowed only if the platform has not changed since that sync.", "Undo this LaunchBox synchronization?", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        await RunWork("Restoring the LaunchBox platform backup…", async ct => { await new LaunchBoxService().RollbackAsync(journal, ct); _launchBoxPlan = null; _launchBoxSettings = ""; _launchBoxGrid.DataSource = null; _lastLaunchBoxJournal = ""; SetStatus("LaunchBox platform restored from its verified backup."); });
    }
}
