using System.ComponentModel;
using System.Text.Json;
using ArcadeLibraryManager.Core;

namespace ArcadeLibraryManager.Desktop;

public sealed partial class MainForm
{
    private Button _genreButton = null!;
    private ContextMenuStrip _genreMenu = null!;
    private string _libraryGenre = LibraryGenreFilter.AllGenres;
    private Label _librarySelectionSummary = null!;

    private void BuildLibraryPage()
    {
        var page = Page("Library");
        var metrics = new TableLayoutPanel { Dock = DockStyle.Top, Height = 121, ColumnCount = 4 };
        for (var i = 0; i < 4; i++) metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        _supported = new MetricCard("Supported", "—", "Released profiles"); _installed = new MetricCard("Registered", "—", "In TeknoParrot");
        _attention = new MetricCard("Need attention", "—", "Invalid launch paths"); _missing = new MetricCard("Missing", "—", "Available to plan");
        var cards = new[] { _supported, _installed, _attention, _missing };
        for (var i = 0; i < cards.Length; i++) { cards[i].Dock = DockStyle.Fill; metrics.Controls.Add(cards[i], i, 0); }
        _search = Palette.TextBox(); _search.Name = "LibrarySearch"; _search.Dock = DockStyle.None; _search.Width = 220; _search.Height = 34; _search.PlaceholderText = "Search title, profile or hardware"; _search.Margin = new Padding(0, 3, 10, 7);
        _libraryFilter = new ComboBox { Name = "LibraryStatusFilter", BackColor = Palette.Input, ForeColor = Palette.Ink, FlatStyle = FlatStyle.Flat, DropDownStyle = ComboBoxStyle.DropDownList, Width = 145, Height = 34, Font = Palette.Font(), Margin = new Padding(0, 3, 10, 7) };
        _libraryFilter.Items.AddRange(["All games", "Missing", "Registered", "Invalid paths", "Subscription"]); _libraryFilter.SelectedIndex = 0;
        _search.TextChanged += (_, _) => FilterGames(); _libraryFilter.SelectedIndexChanged += (_, _) => FilterGames();
        _genreMenu = new ContextMenuStrip { BackColor = Palette.Surface, ForeColor = Palette.Ink, Font = Palette.Font(), ShowImageMargin = false, ShowCheckMargin = true, Renderer = new ToolStripProfessionalRenderer(new GenreMenuColors()) };
        _genreButton = Palette.Button("Genre: All ▾", false, 185); _genreButton.Name = "LibraryGenre"; _genreButton.AccessibleName = "Filter library by genre"; _genreButton.ContextMenuStrip = _genreMenu;
        _genreButton.Click += (_, _) => _genreMenu.Show(_genreButton, new Point(0, _genreButton.Height));
        var clear = WorkButton("Clear selection", (_, _) => SelectGames(_ => false), false, 135); clear.Name = "LibraryClearSelection";
        var filters = Toolbar(_search, _libraryFilter, _genreButton, clear);
        var selectMissing = WorkButton("Select missing shown", (_, _) => SelectVisibleGames(true), false, 168); selectMissing.Name = "LibrarySelectMissing";
        var selectShown = WorkButton("Select all shown", (_, _) => SelectVisibleGames(false), false, 142); selectShown.Name = "LibrarySelectShown";
        var actions = Toolbar(WorkButton("Index ROM sources", async (_, _) => await IndexRomsAsync(), false, 165), selectMissing, selectShown, WorkButton("Plan selected games", async (_, _) => await PlanSelectedAsync(), true, 185));
        _gameGrid = Palette.Grid(); _gameGrid.Name = "LibraryGameGrid"; Palette.CheckColumn(_gameGrid); Palette.TextColumn(_gameGrid, "Name", "GAME / CANDIDATE VERSION", 220); Palette.TextColumn(_gameGrid, "Genre", "GENRE", 110); Palette.TextColumn(_gameGrid, "Emulator", "HARDWARE", 95); Palette.TextColumn(_gameGrid, "Status", "FILES & PROFILE", 140); Palette.TextColumn(_gameGrid, "Id", "PROFILE ID", 100);
        _gameGrid.CellValueChanged += (_, e) => { if (e.RowIndex >= 0 && _gameGrid.Rows[e.RowIndex].DataBoundItem is GameSelection row) { _gameSelections[row.Id] = row.Selected; UpdateLibrarySelectionSummary(); } };
        _gameGrid.SelectionChanged += (_, _) => UpdateGameDetails();
        _gameDetails = new TextBox { Dock = DockStyle.Bottom, Height = 72, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = Palette.Soft, ForeColor = Palette.Muted, Font = Palette.Font(9), Text = "Scan a TeknoParrot installation to see supported games and validate registered launch paths." };
        _librarySelectionSummary = Hint("Scan your library, then choose a genre or status to narrow your installation plan.", 48); _librarySelectionSummary.Name = "LibrarySelectionSummary";
        page.Controls.Add(_gameGrid); page.Controls.Add(_gameDetails); page.Controls.Add(_librarySelectionSummary); page.Controls.Add(actions); page.Controls.Add(filters); page.Controls.Add(metrics);
        RefreshGenreMenu();
    }
    private async Task ScanLibraryAsync()
    {
        await RunWork("Scanning TeknoParrot profiles…", async ct => {
            ReadSettingsFromControls(); SettingsStore.Save(_dataDirectory, _settings);
            _games = await Task.Run(() => Engine().ScanAsync(ct), ct);
            foreach (var game in _games) if (!_gameSelections.ContainsKey(game.Id)) _gameSelections[game.Id] = !game.Installed;
            UpdateLibrary(); ShowPage("Library"); SetStatus($"Found {_games.Count:N0} released profiles. {_games.Count(g => g.Installed):N0} registered; {_games.Count(g => !g.Installed):N0} missing.");
        });
    }
    private async Task IndexRomsAsync()
    {
        await RunWork("Indexing configured ROM sources…", async ct => {
            ReadSettingsFromControls(); SettingsStore.Save(_dataDirectory, _settings);
            if (_settings.RomRoots.Count == 0) throw new InvalidOperationException("Add one or more existing ROM source folders in Setup first.");
            await Task.Run(() => Engine().IndexRomsAsync(ct), ct);
            SetStatus("ROM index updated. Select games and build an installation plan.");
        });
    }
    private void UpdateLibrary()
    {
        _supported.SetValue(_games.Count); _installed.SetValue(_games.Count(g => g.Installed));
        _attention.SetValue(_games.Count(g => g.Installed && !g.PathsValid)); _missing.SetValue(_games.Count(g => !g.Installed)); RefreshGenreMenu(); FilterGames();
    }
    private void RefreshGenreMenu()
    {
        var genres = LibraryGenreFilter.GetAvailableGenres(_games);
        if (!genres.Contains(_libraryGenre, StringComparer.OrdinalIgnoreCase)) _libraryGenre = LibraryGenreFilter.AllGenres;
        foreach (var old in _genreMenu.Items.Cast<ToolStripItem>().ToArray()) old.Dispose();
        _genreMenu.Items.Clear();
        foreach (var genre in genres)
        {
            var count = _games.Count(g => LibraryGenreFilter.Matches(g, genre));
            var item = new ToolStripMenuItem($"{genre} ({count:N0})") { Tag = genre, Checked = genre == _libraryGenre, ForeColor = Palette.Ink, BackColor = Palette.Surface };
            item.Click += (_, _) => { _libraryGenre = genre; UpdateGenreButton(); FilterGames(); };
            _genreMenu.Items.Add(item);
        }
        UpdateGenreButton();
    }
    private void UpdateGenreButton()
    {
        _genreButton.Text = "Genre: " + (_libraryGenre == LibraryGenreFilter.AllGenres ? "All" : _libraryGenre) + " ▾";
        foreach (ToolStripMenuItem item in _genreMenu.Items) item.Checked = string.Equals(item.Tag as string, _libraryGenre, StringComparison.OrdinalIgnoreCase);
    }
    private IEnumerable<GameRecord> FilteredLibraryGames()
    {
        var search = _search.Text.Trim();
        IEnumerable<GameRecord> filtered = _games.Where(g => LibraryGenreFilter.Matches(g, _libraryGenre));
        if (search.Length > 0) filtered = filtered.Where(g => (g.Name + " " + g.Id + " " + g.Emulator).Contains(search, StringComparison.OrdinalIgnoreCase));
        return _libraryFilter.SelectedItem?.ToString() switch {
            "Missing" => filtered.Where(g => !g.Installed), "Registered" => filtered.Where(g => g.Installed),
            "Invalid paths" => filtered.Where(g => g.Installed && !g.PathsValid), "Subscription" => filtered.Where(g => g.SubscriptionRequired), _ => filtered };
    }
    internal List<string> SelectedLibraryGameIds() => FilteredLibraryGames().Where(g => _gameSelections.GetValueOrDefault(g.Id)).Select(g => g.Id).ToList();
    private void FilterGames()
    {
        if (_gameGrid == null) return;
        _gameGrid.EndEdit();
        _gameGrid.DataSource = new BindingList<GameSelection>(FilteredLibraryGames().OrderBy(g => g.Name).Select(g => new GameSelection(g, _gameSelections.GetValueOrDefault(g.Id))).ToList());
        UpdateLibrarySelectionSummary();
    }
    private void UpdateLibrarySelectionSummary()
    {
        if (_librarySelectionSummary == null) return;
        _librarySelectionSummary.Text = $"{FilteredLibraryGames().Count():N0} shown · {SelectedLibraryGameIds().Count:N0} checked. Plans use only checked games shown by these filters.\r\nOne version per game: USA, then World, then another available region.";
        UpdateWorkflowSelectionSummary();
    }
    private void SelectVisibleGames(bool missingOnly)
    {
        _gameGrid.EndEdit();
        var ids = FilteredLibraryGames().Where(g => !missingOnly || !g.Installed).Select(g => g.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        SelectGames(g => ids.Contains(g.Id));
    }
    private void SelectGames(Func<GameRecord, bool> select)
    {
        _gameGrid.EndEdit();
        _gameSelections.Clear();
        foreach (var game in _games) _gameSelections[game.Id] = select(game);
        FilterGames(); SetStatus($"{SelectedLibraryGameIds().Count:N0} shown candidate profiles selected. The plan keeps one available version per game.");
    }
    internal void SelectAllLibraryCandidates()
    {
        _libraryGenre = LibraryGenreFilter.AllGenres; UpdateGenreButton(); _search.Clear(); _libraryFilter.SelectedIndex = 0;
        SelectGames(_ => true);
    }
    private void UpdateGameDetails()
    {
        if (_gameDetails == null) return;
        if (_gameGrid.CurrentRow?.DataBoundItem is not GameSelection selected) { _gameDetails.Text = "No games match the current filters."; return; }
        var game = selected.Game;
        _gameDetails.Text = $"  {game.Name}  •  {game.Id}\r\n  {(string.IsNullOrWhiteSpace(game.GamePath) ? "No launch path registered." : game.GamePath)}" + (game.HasTwoExecutables ? "\r\n  Secondary: " + game.GamePath2 : "") + (string.IsNullOrWhiteSpace(game.Notes) ? "" : "\r\n  " + game.Notes) + (game.SubscriptionRequired ? "\r\n  TeknoParrot subscription may be required." : "");
    }
    private async Task PlanSelectedAsync()
    {
        _gameGrid.EndEdit();
        var ids = SelectedLibraryGameIds();
        if (ids.Count == 0) { SetStatus("Select at least one shown game in Library to build a plan."); return; }
        await RunWork("Choosing one available version per game and checking dependencies…", async ct => {
            ReadSettingsFromControls(); SettingsStore.Save(_dataDirectory, _settings);
            _installPlan = await Task.Run(() => Engine().PlanAsync(ids, ct), ct);
            _planSettings = JsonSerializer.Serialize(_settings); BindPlan(); ShowPage("Install games");
            SetStatus("Plan ready. Review actions and select the items you want to run.");
        });
    }
    private void BuildInstallPage()
    {
        var page = Page("Install games");
        var tabs = new ThemeTabs { Dock = DockStyle.Fill, Font = Palette.Font(10), Padding = new Point(18, 9) };
        var install = new TabPage("Game installation") { BackColor = Palette.Page, Padding = new Padding(12, 18, 12, 12) };
        var update = new TabPage("Official emulator updates") { BackColor = Palette.Page, Padding = new Padding(12, 18, 12, 12) };
        tabs.TabPages.AddRange([install, update]); page.Controls.Add(tabs);
        _planSummary = new Label { Text = "No plan yet. Select games in Library, then choose Plan selected games.", Dock = DockStyle.Top, Height = 48, Font = Palette.Font(10, FontStyle.Bold), ForeColor = Palette.Ink };
        _planGrid = Palette.Grid(); Palette.CheckColumn(_planGrid); Palette.TextColumn(_planGrid, "Name", "GAME", 175); Palette.TextColumn(_planGrid, "Action", "ACTION", 100); Palette.TextColumn(_planGrid, "Detail", "WHY / SOURCE", 265);
        _planGrid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && _planGrid.Rows[e.RowIndex].DataBoundItem is InstallPlanItem item) ShowDetails(item.Name, $"Action: {item.Action}\r\n{item.Detail}\r\n\r\nPrimary: {item.PrimaryPath}\r\nSecondary: {item.SecondaryPath}\r\nArchive: {item.ArchivePath}\r\nOnline source: {item.DownloadUrl}\r\nDestination: {item.Destination}\r\nDownload: {FormatBytes(item.DownloadBytes)}"); };
        var actionBar = Toolbar(WorkButton("Run selected plan", async (_, _) => await ExecutePlanAsync(), true, 174), WorkButton("Select ready", (_, _) => { foreach (var row in _installPlan) row.Selected = CanSelectInstallItem(row); _planGrid.Refresh(); }, false, 115), WorkButton("Select none", (_, _) => { foreach (var row in _installPlan) row.Selected = false; _planGrid.Refresh(); }, false, 115), WorkButton("Export plan…", (_, _) => ExportPlan(), false, 128));
        _planGrid.CellBeginEdit += (_, e) => { if (e.ColumnIndex == 0 && _planGrid.Rows[e.RowIndex].DataBoundItem is InstallPlanItem item && !CanSelectInstallItem(item)) e.Cancel = true; };
        var note = Hint("Only the selected available version can run: USA, then World, then another region. An existing ready installation is retained. Alternate versions stay unchecked; double-click for the reason.", 48);
        install.Controls.Add(_planGrid); install.Controls.Add(note); install.Controls.Add(actionBar); install.Controls.Add(_planSummary);
        _updatesGrid = Palette.Grid(); Palette.CheckColumn(_updatesGrid); Palette.TextColumn(_updatesGrid, "Component", "COMPONENT", 200); Palette.TextColumn(_updatesGrid, "LocalVersion", "INSTALLED", 120); Palette.TextColumn(_updatesGrid, "AvailableVersion", "AVAILABLE", 120); Palette.TextColumn(_updatesGrid, "State", "STATUS", 115);
        _updatesGrid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && _updatesGrid.Rows[e.RowIndex].DataBoundItem is UpdateSelection row) ShowDetails(row.Component, $"{row.State}\r\n\r\nInstalled: {row.LocalVersion}\r\nAvailable: {row.AvailableVersion}\r\nDownload: {FormatBytes(row.Update.Size)}\r\nOfficial source: {row.Update.Url}"); };
        _updateSummary = new Label { Text = "Check the official TeknoParrot component feed before updating.", Dock = DockStyle.Top, Height = 45, Font = Palette.Font(10, FontStyle.Bold), ForeColor = Palette.Ink };
        var updateActions = Toolbar(WorkButton("Check components", async (_, _) => await CheckUpdatesAsync(), false, 167), WorkButton("Apply reviewed updates", async (_, _) => await ApplyUpdatesAsync(), true, 205));
        update.Controls.Add(_updatesGrid); update.Controls.Add(Hint("The updater verifies official checksums and creates a backup. Close TeknoParrot and games before applying an update.", 42)); update.Controls.Add(updateActions); update.Controls.Add(_updateSummary);
    }
    private static bool CanSelectInstallItem(InstallPlanItem item) => item.Action is "Use local ROMs" or "Use local files" or "Extract local archive" or "Download and install";
    private void BindPlan()
    {
        foreach (var item in _installPlan.Where(item => !CanSelectInstallItem(item))) item.Selected = false;
        _planGrid.DataSource = new BindingList<InstallPlanItem>(_installPlan);
        _planSummary.Text = _installPlan.Count == 0 ? "No plan yet. Select games in Library, then choose Plan selected games." : $"{_installPlan.Count:N0} profiles reviewed   ·   {_installPlan.Count(CanSelectInstallItem):N0} versions ready   ·   {FormatBytes(_installPlan.Where(CanSelectInstallItem).Sum(x => x.DownloadBytes))} potential download";
    }
    private async Task ExecutePlanAsync()
    {
        _planGrid.EndEdit(); ReadSettingsFromControls();
        if (_planSettings != JsonSerializer.Serialize(_settings)) { SetStatus("Setup has changed. Build a fresh plan before running it."); return; }
        var selected = _installPlan.Where(x => x.Selected).ToList();
        if (selected.Count == 0) { SetStatus("Select one or more plan items first."); return; }
        await RunWork("Running the reviewed installation plan…", async ct => {
            var result = await Task.Run(() => Engine().ExecuteAsync(selected, ct), ct);
            _games = await Task.Run(() => Engine().ScanAsync(ct), ct); UpdateLibrary();
            ShowResult("Game installation", result);
        });
    }
    private async Task CheckUpdatesAsync()
    {
        await RunWork("Checking the official TeknoParrot release feed…", async ct => {
            ReadSettingsFromControls(); SettingsStore.Save(_dataDirectory, _settings);
            _componentUpdates = await Task.Run(() => new UpdaterService(_settings, _dataDirectory, _progress).CheckAsync(ct), ct);
            _updatesSettings = JsonSerializer.Serialize(_settings);
            _updatesGrid.DataSource = new BindingList<UpdateSelection>(_componentUpdates.Select(u => new UpdateSelection(u)).ToList());
            var count = _componentUpdates.Count(x => x.NeedsUpdate); _updateSummary.Text = $"{_componentUpdates.Count:N0} components checked   ·   {count:N0} updates available";
            SetStatus(count == 0 ? "TeknoParrot components are up to date." : "Official updates ready to review.");
        });
    }
    private async Task ApplyUpdatesAsync()
    {
        if (_updatesGrid.DataSource is not BindingList<UpdateSelection> rows) { SetStatus("Check official components first."); return; }
        _updatesGrid.EndEdit(); ReadSettingsFromControls();
        if (_updatesSettings != JsonSerializer.Serialize(_settings)) { SetStatus("Setup changed after the update check. Check components again."); return; }
        var updates = rows.Where(x => x.Selected && x.Update.NeedsUpdate).Select(x => x.Update).ToList();
        if (updates.Count == 0) { SetStatus("No component updates selected."); return; }
        await RunWork("Applying the reviewed official updates…", async ct => {
            var result = await Task.Run(() => new UpdaterService(_settings, _dataDirectory, _progress).ApplyAsync(updates, ct), ct);
            ShowResult("TeknoParrot update", result);
        });
    }
    private void ExportPlan()
    {
        using var dialog = new SaveFileDialog { Filter = "JSON plan|*.json", FileName = "arcade-install-plan.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _planGrid.EndEdit(); File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(_installPlan, new JsonSerializerOptions { WriteIndented = true })); SetStatus("Reviewed plan exported.");
    }
    private static string FormatBytes(long bytes) => bytes >= 1_073_741_824 ? $"{bytes / 1_073_741_824d:N1} GB" : bytes >= 1_048_576 ? $"{bytes / 1_048_576d:N1} MB" : $"{bytes:N0} bytes";
    private sealed class GenreMenuColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected => Palette.Hover;
        public override Color MenuItemBorder => Palette.Cyan;
        public override Color MenuBorder => Palette.Cyan;
        public override Color ToolStripDropDownBackground => Palette.Surface;
        public override Color ImageMarginGradientBegin => Palette.Surface;
        public override Color ImageMarginGradientMiddle => Palette.Surface;
        public override Color ImageMarginGradientEnd => Palette.Surface;
        public override Color CheckBackground => Palette.Selection;
        public override Color CheckSelectedBackground => Palette.Selection;
    }
    private sealed class GameSelection(GameRecord game, bool selected)
    {
        public GameRecord Game { get; } = game;
        public bool Selected { get; set; } = selected;
        public string Id => Game.Id; public string Name => Game.Name; public string Genre => string.Join(", ", LibraryGenreFilter.GetGenres(Game)); public string Emulator => Game.Emulator;
        public string Status => string.IsNullOrWhiteSpace(Game.Status) ? Game.Installed ? Game.PathsValid ? "Paths verified" : "Missing launch files" : "Not registered" : Game.Status;
    }
    private sealed class UpdateSelection(ComponentUpdate update)
    {
        public ComponentUpdate Update { get; } = update; public bool Selected { get; set; } = update.NeedsUpdate;
        public string Component => Update.Component; public string LocalVersion => Update.LocalVersion; public string AvailableVersion => Update.AvailableVersion;
        public string State => Update.Status;
    }
    private void PopulateFixture()
    {
        _games = new List<GameRecord> {
            new() { Id = "DariusBurstAC", Genre = "Shmup", Name = "Dariusburst Another Chronicle", Emulator = "Taito Type X", Installed = true, PathsValid = true, Status = "Paths verified" },
            new() { Id = "Daytona3", Genre = "Racing", Name = "Daytona Championship USA", Emulator = "Sega PC", Installed = true, PathsValid = true, Status = "Paths verified" },
            new() { Id = "DenshaDeGo", Genre = "Train Simulator", Name = "Densha de GO!!", Emulator = "Taito Type X", Status = "Not registered" },
            new() { Id = "HOTD4", Genre = "Shooter", Name = "The House of the Dead 4", Emulator = "Sega Lindbergh", Installed = true, PathsValid = true, Status = "Paths verified" },
            new() { Id = "IDZTP", Genre = "Racing", Name = "Initial D Arcade Stage Zero", Emulator = "Sega Nu", Installed = true, PathsValid = true, Status = "Paths verified" },
            new() { Id = "MarioKartDX", Genre = "Racing", Name = "Mario Kart Arcade GP DX", Emulator = "Namco ES3", Installed = true, PathsValid = true, Status = "Paths verified" },
            new() { Id = "Outrun2SP", Genre = "Racing", Name = "OutRun 2 SP SDX", Emulator = "Sega Lindbergh", Installed = true, Status = "Missing launch files" },
            new() { Id = "SegaRacingClassic", Name = "Sega Racing Classic", Genre = "Racer", Emulator = "Sega RingWide", Status = "Not registered" },
            new() { Id = "StreetFighterVTypeArcade", Genre = "Fighting", Name = "Street Fighter V: Type Arcade", Emulator = "Taito Type X", Status = "Not registered" },
            new() { Id = "taikogreen", Name = "Taiko no Tatsujin — Green", Emulator = "Namco 357", Status = "Not registered" },
            new() { Id = "WMMT5DX", Genre = "Racing", Name = "Wangan Midnight Maximum Tune 5DX", Emulator = "Namco ES3", Installed = true, PathsValid = true, Status = "Paths verified" }
        };
        foreach (var game in _games) _gameSelections[game.Id] = !game.Installed;
        UpdateLibrary(); ShowPage("Library"); _subhead.Text = "Design preview · fixture data only · no library files accessed"; SetStatus("DEMO / Local fixture. The app has not scanned or changed a real library.");
    }
}


