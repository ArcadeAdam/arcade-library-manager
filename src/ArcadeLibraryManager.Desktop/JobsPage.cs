using System.Text.Json;
using ArcadeLibraryManager.Core;

namespace ArcadeLibraryManager.Desktop;

public sealed partial class MainForm
{
    private void BuildJobsPage()
    {
        var page = Page("Jobs");
        var tabs = new ThemeTabs { Dock = DockStyle.Fill, Font = Palette.Font(10), Padding = new Point(16, 8) };
        var history = new TabPage("Saved job history") { BackColor = Palette.Page, Padding = new Padding(10) };
        var session = new TabPage("Session activity") { BackColor = Palette.Page, Padding = new Padding(10) };
        _jobsGrid = Palette.Grid(); Palette.TextColumn(_jobsGrid, "Name", "GAME / JOB", 170); Palette.TextColumn(_jobsGrid, "Stage", "STAGE", 105); Palette.TextColumn(_jobsGrid, "Message", "LATEST RESULT", 260); Palette.TextColumn(_jobsGrid, "UpdatedUtc", "UPDATED UTC", 120); history.Controls.Add(_jobsGrid);
        var feed = Palette.Grid(); Palette.TextColumn(feed, "Time", "TIME", 70); Palette.TextColumn(feed, "Stage", "STAGE", 95); Palette.TextColumn(feed, "Profile", "PROFILE", 100); Palette.TextColumn(feed, "Message", "ACTIVITY", 340); feed.DataSource = _logs;
        feed.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && feed.Rows[e.RowIndex].DataBoundItem is LogEntry log) ShowDetails(log.Stage, log.Message); };
        session.Controls.Add(feed); tabs.TabPages.AddRange([history, session]);
        var actions = Toolbar(WorkButton("Refresh history", (_, _) => RefreshJobHistory(), false, 140), WorkButton("Resume unfinished", async (_, _) => await ResumeUnfinishedAsync(), true, 165), WorkButton("Export report…", (_, _) => ExportReport(), false, 137), WorkButton("Open app data", (_, _) => { Directory.CreateDirectory(_dataDirectory); OpenLocal(_dataDirectory); }, false, 135));
        page.Controls.Add(tabs); page.Controls.Add(Hint("Work is journaled as it runs. After a restart, Resume unfinished returns pending items to a plan for review. Keep the app open while a batch is running.", 44)); page.Controls.Add(actions);
    }
    private void RefreshJobHistory()
    {
        if (_jobsGrid == null || _fixture) return;
        try { _jobsGrid.DataSource = Engine().GetJobs(); } catch (Exception ex) { if (!_busy) SetStatus("Job history unavailable: " + ex.Message); }
    }
    private async Task ResumeUnfinishedAsync()
    {
        await RunWork("Reviewing unfinished installation versions...", async ct => {
            ReadSettingsFromControls();
            _installPlan = await Engine().ReviewPendingPlanAsync(ct);
            _planSettings = JsonSerializer.Serialize(_settings); BindPlan();
            ShowPage("Install games"); SetStatus(_installPlan.Count == 0 ? "There are no unfinished installation items." : $"{_installPlan.Count:N0} unfinished candidates reviewed. Only one available version per game can run.");
        });
    }
    private void ExportReport()
    {
        using var dialog = new SaveFileDialog { Filter = "JSON report|*.json", FileName = "arcade-library-report.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        ReadSettingsFromControls();
        var report = new { Application = AppInfo.DisplayName, ApplicationVersion = AppInfo.Version, CreatedUtc = DateTime.UtcNow,
            Settings = ExportableSettings(_settings), Summary = new { Supported = _games.Count, Registered = _games.Count(g => g.Installed), Missing = _games.Count(g => !g.Installed), InvalidPaths = _games.Count(g => g.Installed && !g.PathsValid) },
            InvalidPathDetails = _games.Where(g => g.Installed && !g.PathsValid).Select(g => new { ProfileId = g.Id, g.Name, g.GamePath, g.GamePath2, g.HasTwoExecutables, g.Status }).ToList(),
            Jobs = Engine().GetJobs(), SessionActivity = _logs.ToList(), InstallPlan = _installPlan };
        File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })); SetStatus("Report exported without account credentials.");
    }
}

