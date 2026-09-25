using System.ComponentModel;
using ArcadeLibraryManager.Core;

namespace ArcadeLibraryManager.Desktop;

public sealed partial class MainForm
{
    private ThemeBatchProgress? _themeBatch;
    private readonly BindingList<ThemeQueueRow> _themeQueue = new();
    private Panel _themeBatchPanel = null!;
    private Label _themeBatchSummary = null!, _themeElapsed = null!, _themeRemaining = null!;
    private NeonProgressBar _themeOverallProgress = null!;
    private Button _themeBackToGames = null!, _themePauseResume = null!;
    private readonly System.Windows.Forms.Timer _themeBatchTimer = new() { Interval = 1000 };
    private bool _themeQueueVisible, _themeBatchPreview, _themeBatchCancelled;
    private TimeSpan _themeFixtureElapsed;
    private bool _themePauseRequested;
    private TaskCompletionSource<bool>? _themePauseGate;
    private ThemeBatchProgress? _themePauseGateBatch;

    private void BuildThemeProgressPanel(Control page)
    {
        _themeBatchPanel = new Panel { Name = "ThemeBatchPanel", Dock = DockStyle.Top, Height = 113, BackColor = Palette.Surface, Padding = new Padding(15, 11, 15, 10), Visible = false };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, BackColor = Palette.Surface };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 183));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        _themeBatchSummary = new Label { Name = "ThemeBatchSummary", Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Palette.Ink, Font = Palette.Font(11, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
        _themeBackToGames = Palette.Button("Back to all games", false, 174); _themeBackToGames.Name = "ThemeBackToAllGames"; _themeBackToGames.Height = 34;
        _themeBackToGames.Click += (_, _) => { if (!_busy && _themeBatch?.IsRunning != true) ShowAllThemeGames(); };
        _themePauseResume = Palette.Button("Pause batch", false, 174); _themePauseResume.Name = "ThemePauseResume"; _themePauseResume.Height = 34;
        _themePauseResume.Click += (_, _) => ToggleThemePause();
        _fieldTips.SetToolTip(_themePauseResume, "Finish the current theme, then pause before the next game. Resume also cancels a pending pause. Cancel remains available while paused.");
        var batchActions = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
        _themeBackToGames.Location = new Point(3, 0); _themePauseResume.Location = new Point(3, 0);
        batchActions.Controls.AddRange([_themeBackToGames, _themePauseResume]);
        var times = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
        _themeElapsed = Palette.Label("Elapsed 0:00:00", 9.4f, Palette.Cyan); _themeElapsed.Name = "ThemeElapsed"; _themeElapsed.Margin = new Padding(0, 2, 24, 0);
        _themeRemaining = Palette.Label("Estimated remaining: estimating…", 9.4f, Palette.Muted); _themeRemaining.Name = "ThemeRemaining"; _themeRemaining.Margin = new Padding(0, 2, 0, 0);
        times.Controls.AddRange([_themeElapsed, _themeRemaining]);
        _themeOverallProgress = new NeonProgressBar { Name = "ThemeOverallProgress", Dock = DockStyle.Fill, Minimum = 0, Maximum = 1000, Style = ProgressBarStyle.Continuous, Margin = new Padding(0, 3, 0, 4) };
        layout.Controls.Add(_themeBatchSummary, 0, 0); layout.Controls.Add(batchActions, 1, 0); layout.SetRowSpan(batchActions, 2);
        layout.Controls.Add(times, 0, 1); layout.Controls.Add(_themeOverallProgress, 0, 2); layout.SetColumnSpan(_themeOverallProgress, 2);
        _themeBatchPanel.Controls.Add(layout); page.Controls.Add(_themeBatchPanel);
        _themeBatchTimer.Tick += (_, _) => RefreshThemeProgressView();
    }

    private void ConfigureThemeGrid(bool queue)
    {
        _mediaGrid.DataSource = null; _mediaGrid.Columns.Clear(); _mediaGrid.ReadOnly = queue; _mediaGrid.Name = queue ? "ThemeQueueGrid" : "ThemeGameGrid";
        if (queue)
        {
            Palette.TextColumn(_mediaGrid, "Title", "GAME", 180); Palette.TextColumn(_mediaGrid, "Status", "STATUS", 92);
            Palette.TextColumn(_mediaGrid, "Progress", "PROGRESS", 82); Palette.TextColumn(_mediaGrid, "Elapsed", "ELAPSED", 76); Palette.TextColumn(_mediaGrid, "Detail", "CURRENT TASK / RESULT", 260);
            foreach (DataGridViewColumn column in _mediaGrid.Columns) column.SortMode = DataGridViewColumnSortMode.NotSortable;
        }
        else
        {
            Palette.CheckColumn(_mediaGrid); Palette.TextColumn(_mediaGrid, "Title", "GAME", 205); Palette.TextColumn(_mediaGrid, "Background", "ART", 60);
            Palette.TextColumn(_mediaGrid, "Logo", "LOGO", 60); Palette.TextColumn(_mediaGrid, "Cutouts", "CUTOUTS", 105); Palette.TextColumn(_mediaGrid, "Snap", "SNAP", 70); Palette.TextColumn(_mediaGrid, "Theme", "THEME", 145);
        }
    }

    private MediaSelection? CurrentThemeGame() => _mediaGrid.CurrentRow?.DataBoundItem switch { MediaSelection game => game, ThemeQueueRow queued => queued.Game, _ => null };

    private void ShowAllThemeGames()
    {
        if (_themeBatch?.IsRunning == true) return;
        _themeQueueVisible = false; _mediaDetails.Visible = true; _displayedThemeAssets = null; ConfigureThemeGrid(false);
        _mediaGrid.DataSource = new BindingList<MediaSelection>(_media);
        RefreshMediaSummary(); RefreshThemeProgressView();
    }

    private void RefreshMediaSummary()
    {
        _mediaSummary.Text = _themeQueueVisible ? $"Only the {_themeQueue.Count:N0} game{(_themeQueue.Count == 1 ? "" : "s")} in this {(_themeBatchPreview ? "preview" : "batch")} are shown." :
            $"{_media.Count:N0} LaunchBox games   ·   {_media.Count(x => x.Assets.Theme.Length > 0):N0} existing themes   ·   {_media.Count(x => CanProcessMissingTheme(x.Assets) && MediaService.HasReusableTheme(x.Assets)):N0} reusable themes   ·   {_media.Count(x => MediaService.HasVideoSnap(x.Assets)):N0} gameplay snaps";
    }

    private ThemeBatchProgress BeginThemeBatch(IReadOnlyList<MediaSelection> games, bool preview, Func<TimeSpan>? elapsed = null)
    {
        ReleaseThemePauseGate(cancelled: true); _themePauseRequested = false;
        _themeBatchTimer.Stop(); _themeBatch = new ThemeBatchProgress(games.Select(g => (g.Game.Id, g.Title)), elapsed);
        _themeBatchPreview = preview; _themeBatchCancelled = false; _themeQueueVisible = true; _mediaDetails.Visible = false;
        _themeQueue.Clear(); foreach (var game in games) _themeQueue.Add(new ThemeQueueRow(game));
        ConfigureThemeGrid(true); _mediaGrid.DataSource = _themeQueue;
        _themeBatchPanel.Visible = true; _themeBatchTimer.Start();
        if (_pages.TryGetValue("Make themes", out var page)) page.Controls.OfType<TabControl>().First().SelectedIndex = 0;
        RefreshMediaSummary(); RefreshThemeProgressView(); return _themeBatch;
    }

    private IProgress<JobEvent> StartThemeGame(ThemeBatchProgress batch, MediaSelection game)
    {
        batch.Start(game.Game.Id); RefreshThemeProgressView();
        var progress = new ThemeRowProgress(this, batch, game.Game.Id);
        progress.Report(new JobEvent("Theme", !_themeBatchPreview && MediaService.HasReusableTheme(game.Assets) ? "Preparing existing theme copy" : "Preparing local artwork and gameplay", game.Game.Id, 0));
        return progress;
    }

    private void CompleteThemeGame(ThemeBatchProgress batch, MediaSelection game, string status, string detail)
    {
        batch.Complete(game.Game.Id, status, detail);
        if (!_themeBatchPreview && status is ("Completed" or "Skipped")) game.Selected = false;
        if (!batch.IsRunning && ReferenceEquals(_themeBatch, batch)) { _themePauseRequested = false; ReleaseThemePauseGate(batch); }
        RefreshThemeProgressView();
    }

    private void StopThemeBatch(ThemeBatchProgress batch, bool cancelled, string detail)
    {
        if (!ReferenceEquals(_themeBatch, batch)) return;
        _themeBatchCancelled = cancelled; _themePauseRequested = false;
        batch.Stop(cancelled, detail); ReleaseThemePauseGate(batch, cancelled: true); RefreshThemeProgressView();
    }

    private void ToggleThemePause()
    {
        var batch = _themeBatch;
        if (batch == null || !batch.IsRunning || _themeBatchPreview) return;
        if (_themePauseRequested || batch.IsPaused)
        {
            _themePauseRequested = false; batch.Resume(); ReleaseThemePauseGate(batch);
            SetStatus("Theme batch resumed.");
        }
        else
        {
            _themePauseRequested = true;
            SetStatus("Pause requested. The current theme will finish before the batch pauses.");
        }
        RefreshThemeProgressView();
    }

    private async Task WaitForThemeResumeAsync(ThemeBatchProgress batch, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!ReferenceEquals(_themeBatch, batch)) throw new OperationCanceledException("The theme batch changed.");
        while (_themePauseRequested && batch.IsRunning)
        {
            batch.Pause();
            if (!batch.IsPaused) return;
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _themePauseGate = gate; _themePauseGateBatch = batch;
            RefreshThemeProgressView(); SetStatus("Theme batch paused. Resume to continue with the next game, or cancel to stop.");
            try { await gate.Task.WaitAsync(ct); }
            finally
            {
                if (ReferenceEquals(_themePauseGate, gate)) { _themePauseGate = null; _themePauseGateBatch = null; }
            }
            ct.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_themeBatch, batch) || !batch.IsRunning) throw new OperationCanceledException("The theme batch stopped.");
            // A new pause request can arrive before the resume continuation runs.
        }
    }

    private void ReleaseThemePauseGate(ThemeBatchProgress? batch = null, bool cancelled = false)
    {
        if (batch != null && !ReferenceEquals(_themePauseGateBatch, batch)) return;
        var gate = _themePauseGate; _themePauseGate = null; _themePauseGateBatch = null;
        if (cancelled) gate?.TrySetCanceled(); else gate?.TrySetResult(true);
    }

    private void RefreshThemeProgressView()
    {
        if (_themeBatch == null || _themeBatchPanel == null || IsDisposed || Disposing) return;
        var batch = _themeBatch;
        _themeBatchPanel.Visible = _themeQueueVisible;
        var items = batch.Items.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < _themeQueue.Count; index++)
        {
            var row = _themeQueue[index];
            if (!items.TryGetValue(row.Game.Game.Id, out var item) || row.Item == item) continue;
            row.Item = item;
            if (_themeQueueVisible) _themeQueue.ResetItem(index);
        }
        _mediaGrid.ReadOnly = _themeQueueVisible || batch.IsRunning;
        _mediaScanActions.Enabled = !batch.IsRunning; _mediaRenderActions.Enabled = !batch.IsRunning;
        _mediaScanActions.Visible = !_themeQueueVisible;
        _mediaRenderActions.Visible = !_themeQueueVisible || !batch.IsRunning;
        var title = _themeBatchPreview ? "Theme preview" : "Theme batch";
        var state = batch.IsRunning ? batch.IsPaused ? "Paused" : _themePauseRequested ? "Pausing" : "Running" : _themeBatchCancelled ? "Cancelled" : batch.Finished < batch.Total ? "Stopped" : batch.Failed > 0 ? "Finished with errors" : "Complete";
        var unfinished = batch.Finished < batch.Total;
        var displayedPercent = Math.Clamp(Math.Round(batch.Percent), 0, unfinished ? 99 : 100);
        _themeBatchSummary.Text = $"{title} · {state} · {batch.Finished:N0} / {batch.Total:N0} finished · {displayedPercent:0}%";
        _themeOverallProgress.Value = (int)Math.Clamp(Math.Round(batch.Percent * 10), 0, unfinished ? 999 : 1000);
        _themeElapsed.Text = "Elapsed " + ThemeTime(batch.Elapsed);
        _themeRemaining.Text = !batch.IsRunning ? $"{batch.Succeeded:N0} completed · {batch.Skipped:N0} skipped · {batch.Failed:N0} failed" :
            batch.Remaining is not { } remaining ? "Estimated remaining: estimating…" : remaining <= TimeSpan.Zero ? "Estimated remaining: recalculating…" : "Estimated remaining: " + ThemeTime(remaining);
        _themeBackToGames.Visible = _themeQueueVisible && !batch.IsRunning; _themeBackToGames.Enabled = !batch.IsRunning && !_busy;
        _themePauseResume.Visible = _themeQueueVisible && batch.IsRunning && !_themeBatchPreview;
        _themePauseResume.Enabled = batch.IsRunning;
        _themePauseResume.Text = _themePauseRequested || batch.IsPaused ? "Resume batch" : "Pause batch";
        if (!batch.IsRunning) _themeBatchTimer.Stop();
        _mediaGrid.Invalidate();
    }

    private static string ThemeTime(TimeSpan value) => $"{(long)Math.Max(0, value.TotalHours)}:{Math.Max(0, value.Minutes):00}:{Math.Max(0, value.Seconds):00}";

    private void DisposeThemeProgress()
    {
        _themePauseRequested = false; ReleaseThemePauseGate(cancelled: true);
        _themeBatchTimer.Stop(); _themeBatchTimer.Dispose();
        var previous = _artPreview?.Image; if (_artPreview != null) _artPreview.Image = null; previous?.Dispose();
    }

    private sealed class ThemeRowProgress(MainForm form, ThemeBatchProgress batch, string profileId) : IProgress<JobEvent>
    {
        public void Report(JobEvent value)
        {
            if (form.IsDisposed || form.Disposing) return;
            void Apply()
            {
                if (form.IsDisposed || form.Disposing || !ReferenceEquals(form._themeBatch, batch) || !batch.IsRunning || !batch.CurrentId.Equals(profileId, StringComparison.OrdinalIgnoreCase)) return;
                var current = value with { GameId = profileId };
                batch.Report(profileId, current); form.RefreshThemeProgressView();
                form._progress.Report(current);
            }
            if (form.InvokeRequired) { try { form.BeginInvoke(Apply); } catch (InvalidOperationException) { } }
            else Apply();
        }
    }

    private sealed class ThemeQueueRow(MediaSelection game)
    {
        public MediaSelection Game { get; } = game;
        public ThemeBatchItem? Item { get; set; }
        public string Title => Game.Title;
        public string Status => Item?.Status == "Running" ? ActiveStatus(Item.Detail) : Item?.Status ?? "Queued";
        public string Progress => Item is null || Item.Status is "Queued" or "Not started" ? "—" : $"{Item.Progress:0}%";
        public string Elapsed => Item is null || Item.Status is "Queued" or "Not started" ? "—" : ThemeTime(Item.Elapsed);
        public string Detail => Item?.Detail ?? "Waiting to start";
        private static string ActiveStatus(string detail) => detail.Contains("validat", StringComparison.OrdinalIgnoreCase) ? "Validating" : detail.Contains("render", StringComparison.OrdinalIgnoreCase) || detail.Contains("encod", StringComparison.OrdinalIgnoreCase) ? "Rendering" : "Preparing";
    }

    internal void ShowThemeProgressFixture(string state)
    {
        if (!_fixture) throw new InvalidOperationException("Theme progress fixtures cannot run against a live library.");
        ShowPage("Make themes");
        _themeFixtureElapsed = TimeSpan.Zero;
        var nearComplete = state.Equals("NearComplete", StringComparison.OrdinalIgnoreCase);
        var queueCount = nearComplete ? 2 : 3;
        _media = Enumerable.Range(1, 4).Select(index => new MediaSelection(new GameRecord { Id = "fixture-theme-" + index, Name = index switch { 1 => "Anime Champ", 2 => "Star Trek Voyager", 3 => "Total Vice (USA)", _ => "Unselected library game" }, Installed = true }, new MediaAssets { ProfileId = "fixture-theme-" + index, Title = index switch { 1 => "Anime Champ", 2 => "Star Trek Voyager", 3 => "Total Vice (USA)", _ => "Unselected library game" } }) { Selected = index <= queueCount }).ToList();
        var batch = BeginThemeBatch(_media.Take(queueCount).ToList(), false, () => _themeFixtureElapsed);
        StartThemeGame(batch, _media[0]); _themeFixtureElapsed = TimeSpan.FromSeconds(60); CompleteThemeGame(batch, _media[0], "Completed", "Theme saved and validated");
        var active = StartThemeGame(batch, _media[1]); _themeFixtureElapsed = TimeSpan.FromSeconds(90); active.Report(new JobEvent("Theme", "Rendering theme with GPU acceleration", _media[1].Game.Id, nearComplete ? 99 : 25));
        if (state.Equals("Cancelled", StringComparison.OrdinalIgnoreCase)) StopThemeBatch(batch, true, "Cancelled by the user");
        else if (state.Equals("Completed", StringComparison.OrdinalIgnoreCase))
        {
            _themeFixtureElapsed = TimeSpan.FromSeconds(120); CompleteThemeGame(batch, _media[1], "Completed", "Theme saved and validated");
            StartThemeGame(batch, _media[2]); _themeFixtureElapsed = TimeSpan.FromSeconds(180); CompleteThemeGame(batch, _media[2], "Completed", "Theme saved and validated");
        }
        if (state is "Pausing" or "Paused" or "Resumed" or "PausedCancelled")
        {
            ToggleThemePause();
            if (state != "Pausing")
            {
                _themeFixtureElapsed = TimeSpan.FromSeconds(120);
                CompleteThemeGame(batch, _media[1], "Completed", "Theme saved and validated");
                batch.Pause();
                if (state == "Resumed")
                {
                    _themeFixtureElapsed += TimeSpan.FromMinutes(10); ToggleThemePause();
                    var resumed = StartThemeGame(batch, _media[2]);
                    _themeFixtureElapsed += TimeSpan.FromSeconds(30);
                    resumed.Report(new JobEvent("Theme", "Rendering theme after resume", _media[2].Game.Id, 25));
                }
                else if (state == "PausedCancelled") StopThemeBatch(batch, true, "Cancelled while paused; unfinished games stay selected.");
            }
        }
        RefreshThemeProgressView();
    }

    internal void CompleteCurrentThemeProgressFixture()
    {
        if (!_fixture || _themeBatch == null) throw new InvalidOperationException("An active theme progress fixture is required.");
        var game = _media.Single(row => row.Game.Id == _themeBatch.CurrentId);
        CompleteThemeGame(_themeBatch, game, "Completed", "Theme saved and validated");
    }

    internal async Task WaitThemeProgressFixtureBoundaryAsync(CancellationToken ct)
    {
        if (!_fixture || _themeBatch == null) throw new InvalidOperationException("An active theme progress fixture is required.");
        var batch = _themeBatch;
        try { await WaitForThemeResumeAsync(batch, ct); }
        catch (OperationCanceledException) { StopThemeBatch(batch, true, "Cancelled while paused; unfinished games stay selected."); throw; }
    }

    internal void AdvanceThemeProgressFixture(TimeSpan amount)
    {
        if (!_fixture) throw new InvalidOperationException("Theme progress fixtures cannot run against a live library.");
        _themeFixtureElapsed += amount; RefreshThemeProgressView();
    }
}
