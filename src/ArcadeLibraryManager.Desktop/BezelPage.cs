using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using ArcadeLibraryManager.Core;

namespace ArcadeLibraryManager.Desktop;

public sealed partial class MainForm
{
    private BezelPlan? _bezelPlan;
    private string _bezelSettings = "";
    private DataGridView _bezelGrid = null!;
    private Label _bezelSummary = null!;
    private readonly List<string> _lastBezelJournals = new();

    private void BuildBezelPage()
    {
        var page = Page("Bezels");
        var repository = new TableLayoutPanel { Name = "BezelRepositoryCard", Dock = DockStyle.Top, Height = 113, ColumnCount = 2, RowCount = 3, BackColor = Palette.Surface, Padding = new Padding(14, 10, 14, 8), Margin = new Padding(0, 0, 0, 8) };
        repository.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); repository.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 122));
        repository.RowStyles.Add(new RowStyle(SizeType.Absolute, 25)); repository.RowStyles.Add(new RowStyle(SizeType.Absolute, 38)); repository.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var repositoryLabel = Palette.Label("Local bezel repository", 11, Palette.Cyan, true); repositoryLabel.Name = "BezelRepositoryLabel";
        repository.Controls.Add(repositoryLabel, 0, 0); repository.SetColumnSpan(repositoryLabel, 2);
        var repositoryPath = Palette.TextBox(); repositoryPath.Name = "BezelRepositoryPath"; repositoryPath.PlaceholderText = @"C:\Users\YourName\Downloads\TeknoParrot Bezels";
        repositoryPath.AccessibleName = "Local bezel repository"; repositoryPath.AccessibleDescription = "Folder containing local PNG or ZIP bezels, including repository subfolders.";
        repositoryPath.Margin = new Padding(0, 0, 10, 7); _paths["BezelImportPath"] = repositoryPath;
        var browseRepository = WorkButton("Browse…", (_, _) => BrowsePath(repositoryPath, false), false, 112); browseRepository.Name = "BezelRepositoryBrowse"; browseRepository.Margin = new Padding(0);
        repository.Controls.Add(repositoryPath, 0, 1); repository.Controls.Add(browseRepository, 1, 1);
        var repositoryHint = Palette.Label("Subfolders are included. Select a bezel collection or an extracted repository.", 8.7f, Palette.Muted); repositoryHint.AutoSize = false; repositoryHint.Dock = DockStyle.Fill;
        repository.Controls.Add(repositoryHint, 0, 2); repository.SetColumnSpan(repositoryHint, 2);
        _fieldTips.SetToolTip(repositoryPath, "Choose a local or network folder containing your bezel collection. Subfolders are searched for exact TeknoParrot profile names: profile.png, profile.zip, or profile/bezel.png. MAME artwork is checked first. Preview bezels shows the source and any ambiguous matches; source files are never modified.");
        repository.Paint += (_, e) => { using var border = new Pen(Palette.Cyan); e.Graphics.DrawRectangle(border, 0, 0, repository.Width - 1, repository.Height - 1); };

        _bezelSummary = new Label { Text = "Find matching bezel artwork and preview each game's setup.", Dock = DockStyle.Top, Height = 43, Font = Palette.Font(10, FontStyle.Bold), ForeColor = Palette.Ink };
        var actions = Toolbar(WorkButton("Preview bezels", async (_, _) => await PreviewBezelsAsync(), false, 146), WorkButton("Install & enable selected", async (_, _) => await ApplyBezelsAsync(), true, 215), WorkButton("Select ready", (_, _) => { if (_bezelPlan != null) { foreach (var item in _bezelPlan.Items) item.Selected = item.Action is "Install" or "Enable"; _bezelGrid.Refresh(); } }, false, 122), WorkButton("Open online source", (_, _) => OpenBezelSource(), false, 172));
        var recovery = Toolbar(WorkButton("Undo bezel change…", async (_, _) => await UndoBezelAsync(), false, 173));
        _bezelGrid = Palette.Grid(); Palette.CheckColumn(_bezelGrid); Palette.TextColumn(_bezelGrid, "Name", "GAME", 160); Palette.TextColumn(_bezelGrid, "Action", "ACTION", 80); Palette.TextColumn(_bezelGrid, "Detail", "SETUP / REVIEW", 230); Palette.TextColumn(_bezelGrid, "Source", "SOURCE ARTWORK", 155);
        _bezelGrid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && _bezelGrid.Rows[e.RowIndex].DataBoundItem is BezelPlanItem row) ShowDetails(row.Name, $"Action: {row.Action}\r\n{row.Detail}\r\n\r\nSource: {row.Source}\r\nDestination: {row.Destination}"); };
        var note = new TextBox { Dock = DockStyle.Bottom, Height = 91, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = Palette.Soft, ForeColor = Palette.Muted, Font = Palette.Font(9),
            Text = "  MAME artwork is checked first, followed by the local repository and its subfolders.\r\n  For Discord sources, download or extract the pack, then choose its folder above.\r\n  Only ready matches can be installed and enabled. Existing controls and unrelated settings stay intact.\r\n  Some games or artwork layouts need manual review; the preview explains each one." };
        page.Controls.Add(_bezelGrid); page.Controls.Add(note); page.Controls.Add(recovery); page.Controls.Add(actions); page.Controls.Add(_bezelSummary); page.Controls.Add(repository);
    }
    private async Task PreviewBezelsAsync()
    {
        await RunWork("Finding bezel artwork and preparing a setup preview…", async ct => {
            ReadSettingsFromControls(); SettingsStore.Save(_dataDirectory, _settings);
            _games = await Task.Run(() => Engine().ScanAsync(ct), ct); UpdateLibrary();
            _bezelPlan = await new BezelService().PreviewAsync(_settings, _games, _progress, ct);
            _bezelSettings = JsonSerializer.Serialize(_settings); _bezelGrid.DataSource = new BindingList<BezelPlanItem>(_bezelPlan.Items);
            _bezelSummary.Text = $"{_bezelPlan.Items.Count:N0} profiles checked   ·   {_bezelPlan.Items.Count(x => x.Action is "Install" or "Enable"):N0} ready to install or enable   ·   {_bezelPlan.Items.Count(x => x.Action == "NeedsReview"):N0} for review";
            SetStatus("Bezel preview ready. Review checked items before installing and enabling them.");
        });
    }
    private async Task ApplyBezelsAsync()
    {
        _bezelGrid.EndEdit(); ReadSettingsFromControls();
        if (_bezelPlan == null || !_bezelPlan.CanApply) { SetStatus("Preview bezels and select one or more ready actions first."); return; }
        if (_bezelSettings != JsonSerializer.Serialize(_settings)) { SetStatus("Setup changed after the bezel preview. Preview again before applying."); return; }
        await RunWork("Installing and enabling the reviewed bezels…", async ct => { var result = await new BezelService().ApplyAsync(_bezelPlan, _progress, ct); _lastBezelJournals.AddRange(_bezelPlan.BackupPaths); ShowResult("Bezel setup", result); _bezelSettings = ""; });
    }
    private async Task UndoBezelAsync()
    {
        using var dialog = new OpenFileDialog { Title = "Choose the bezel change to undo", Filter = "Bezel rollback journal|rollback.json|JSON files|*.json" };
        if (_lastBezelJournals.Count > 0) dialog.InitialDirectory = Path.GetDirectoryName(_lastBezelJournals.Last());
        else if (Directory.Exists(_settings.TeknoParrotPath)) dialog.InitialDirectory = Path.Combine(_settings.TeknoParrotPath, "Backups", "ArcadeLibraryManager", "Bezels");
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try {
            var journal = JsonSerializer.Deserialize<BezelRollbackJournal>(File.ReadAllText(dialog.FileName)) ?? throw new InvalidDataException("Invalid bezel rollback journal.");
            if (MessageBox.Show(this, $"Undo this bezel change from {journal.CommittedUtc.LocalDateTime:g}?\n\n" + string.Join("\n", journal.Entries.Select(x => x.Path)) + "\n\nOnly files unchanged since installation can be restored.", "Undo bezel setup", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            await RunWork("Restoring the bezel and profile backup…", async ct => { await new BezelService().RollbackAsync(dialog.FileName, ct); _bezelSettings = ""; SetStatus("The selected bezel change was undone."); });
        } catch (Exception ex) { ShowDetails("Could not undo bezel setup", ex.Message); }
    }
    private void OpenBezelSource()
    {
        ReadSettingsFromControls();
        if (!Uri.TryCreate(_settings.BezelSourceUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) { SetStatus("Enter a valid online bezel source URL in Setup."); return; }
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}

