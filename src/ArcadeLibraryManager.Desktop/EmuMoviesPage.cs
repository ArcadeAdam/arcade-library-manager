using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ArcadeLibraryManager.Core;

namespace ArcadeLibraryManager.Desktop;

public sealed partial class MainForm
{
    private const string EmuMoviesPageName = "Emumovies snap scrapper";
    private const string EmuMoviesSyncDownloadUrl = "https://emumovies.com/files/file/321-emumovies-sync/";
    private ComboBox _emuMoviesPlatform = null!, _emuMoviesCatalog = null!;
    private TextBox _emuMoviesMatchFolder = null!, _emuMoviesWorkDirectory = null!, _emuMoviesSyncExecutable = null!, _emuMoviesDetails = null!;
    private CheckBox _emuMoviesMameFallback = null!;
    private Label _emuMoviesSummary = null!, _emuMoviesPlatformInfo = null!;
    private TableLayoutPanel _emuMoviesFields = null!;
    private readonly System.Windows.Forms.Timer _emuMoviesSaveTimer = new() { Interval = 600 };
    private EmuMoviesPageSettings _emuMoviesPageSettings = new();
    private string _emuMoviesActivePlatform = "", _emuMoviesLoadedRoot = "", _emuMoviesLastReportDirectory = "";
    private bool _emuMoviesLoading, _emuMoviesInputsBusy;
    private List<EmuMoviesCatalogChoice> _emuMoviesCatalogChoices = [];

    private void BuildEmuMoviesPage()
    {
        var page = Page(EmuMoviesPageName);
        page.Name = "EmuMoviesPage";
        var scroll = new Panel { Name = "EmuMoviesScroll", Dock = DockStyle.Fill, AutoScroll = true, BackColor = Palette.Page };
        var stack = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(0, 0, 14, 12) };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scroll.Controls.Add(stack); page.Controls.Add(scroll);
        _emuMoviesFields = Section(stack, "Gameplay video snaps", "Gameplay MP4 only through official EmuMovies Sync. No artwork downloads. Existing library videos are preserved.");
        _emuMoviesFields.Name = "EmuMoviesFields";
        _emuMoviesFields.Controls.OfType<Label>().Single(c => c.Text.StartsWith("Gameplay MP4 only", StringComparison.Ordinal)).Name = "EmuMoviesMediaScope";
        _emuMoviesFields.ColumnStyles[0].Width = 158;
        _emuMoviesPlatform = EmuMoviesCombo(false, "EmuMoviesPlatform");
        _emuMoviesPlatform.DisplayMember = nameof(EmuMoviesPlatformChoice.Name);
        var refresh = WorkButton("Refresh", async (_, _) => await RefreshEmuMoviesPlatformsAsync(true), false, 115);
        refresh.Name = "EmuMoviesRefreshPlatforms";
        AddField(_emuMoviesFields, "LaunchBox platform", _emuMoviesPlatform, refresh);
        _emuMoviesCatalog = EmuMoviesCombo(true, "EmuMoviesCatalog");
        _emuMoviesCatalogChoices = LoadEmuMoviesCatalogChoices();
        _emuMoviesCatalog.Items.AddRange(_emuMoviesCatalogChoices.Cast<object>().ToArray());
        AddField(_emuMoviesFields, "EmuMovies catalog", _emuMoviesCatalog);
        _emuMoviesMatchFolder = Palette.TextBox(); _emuMoviesMatchFolder.Name = "EmuMoviesMatchFolder";
        _emuMoviesMatchFolder.PlaceholderText = @"Use TeknoParrot\UserProfiles";
        AddEmuMoviesPathField("Match folder", _emuMoviesMatchFolder, false);
        _emuMoviesWorkDirectory = Palette.TextBox(); _emuMoviesWorkDirectory.Name = "EmuMoviesWorkDirectory";
        _emuMoviesWorkDirectory.PlaceholderText = "Folder for downloaded videos and reports";
        AddEmuMoviesPathField("Download / work folder", _emuMoviesWorkDirectory, false);
        _emuMoviesSyncExecutable = Palette.TextBox(); _emuMoviesSyncExecutable.Name = "EmuMoviesSyncExecutable";
        _emuMoviesSyncExecutable.PlaceholderText = "Choose Sync Utility.exe";
        AddEmuMoviesPathField("Official Sync app", _emuMoviesSyncExecutable, true);
        _emuMoviesMameFallback = new EmuMoviesCheckBox { Name = "EmuMoviesMameFallback", Text = "Use MAME / Model 2 for classic arcade games", AutoSize = true, ForeColor = Palette.Ink, Checked = true };
        AddField(_emuMoviesFields, "TeknoParrot fallback", _emuMoviesMameFallback, null, 36);

        var primary = EmuMoviesActionRow(
            WorkButton("Preview import", async (_, _) => await RunEmuMoviesAsync(false, false), false, 132),
            WorkButton("Download & import missing videos", async (_, _) => await RunEmuMoviesAsync(true, true), true, 300),
            WorkButton("Import downloaded videos", async (_, _) => await RunEmuMoviesAsync(false, true), false, 224));
        primary.Controls[0].Name = "EmuMoviesPreviewImport";
        primary.Controls[1].Name = "EmuMoviesDownloadAndImport";
        primary.Controls[2].Name = "EmuMoviesImportDownloaded";
        stack.Controls.Add(primary, 0, stack.RowCount++);
        var secondary = EmuMoviesActionRow(
            WorkButton("Open Sync / login", (_, _) => OpenEmuMoviesSync(), false, 166),
            WorkButton("Download EmuMovies Sync", (_, _) => OpenEmuMoviesSyncDownload(), false, 228),
            WorkButton("Open reports", (_, _) => OpenEmuMoviesReports(), false, 135));
        secondary.Name = "EmuMoviesSecondaryActions";
        secondary.Controls[0].Name = "EmuMoviesOpenSync";
        secondary.Controls[1].Name = "EmuMoviesDownloadSync";
        secondary.Controls[1].Tag = EmuMoviesSyncDownloadUrl;
        _fieldTips.SetToolTip(secondary.Controls[1], "Opens the official EmuMovies download page in your browser. Sign in on the website to download Sync, then install it and choose Sync Utility.exe above.");
        secondary.Controls[2].Name = "EmuMoviesOpenReports";
        _emuMoviesPlatformInfo = new Label { AutoSize = true, ForeColor = Palette.Muted, Font = Palette.Font(9), Margin = new Padding(4, 10, 0, 8), Text = "Select a platform to begin." };
        secondary.Controls.Add(_emuMoviesPlatformInfo);
        stack.Controls.Add(secondary, 0, stack.RowCount++);
        _emuMoviesSummary = new Label { Name = "EmuMoviesRunSummary", Text = "Preview the import or download the missing gameplay videos.", Dock = DockStyle.Fill, ForeColor = Palette.Ink, Font = Palette.Font(10, FontStyle.Bold), Padding = new Padding(0, 5, 0, 4), AutoEllipsis = true };
        var summaryRow = stack.RowCount++;
        while (stack.RowStyles.Count <= summaryRow) stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.RowStyles[summaryRow] = new RowStyle(SizeType.Absolute, 42);
        stack.Controls.Add(_emuMoviesSummary, 0, summaryRow);
        _emuMoviesDetails = Palette.TextBox(true); _emuMoviesDetails.Name = "EmuMoviesRunDetails";
        _emuMoviesDetails.ReadOnly = true; _emuMoviesDetails.ScrollBars = ScrollBars.Vertical;
        _emuMoviesDetails.BackColor = Palette.Surface; _emuMoviesDetails.Font = Palette.Font(9.2f);
        _emuMoviesDetails.Text = "Download & import: use Open Sync / login, sign in, and leave Sync open.\r\nImport downloaded videos and Preview import work with Sync closed.";
        var detailRow = stack.RowCount++;
        while (stack.RowStyles.Count <= detailRow) stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.RowStyles[summaryRow] = new RowStyle(SizeType.Absolute, 42);
        stack.RowStyles[detailRow] = new RowStyle(SizeType.Absolute, 142);
        stack.Controls.Add(_emuMoviesDetails, 0, detailRow);

        _fieldTips.SetToolTip(_emuMoviesMatchFolder, @"For TeknoParrot, use TeknoParrot\UserProfiles inside the emulator install folder selected in Setup. Other platforms use their ROM folder. Saved match folders are retained.");
        _fieldTips.SetToolTip(_emuMoviesWorkDirectory, "Downloaded gameplay videos and reports stay here so interrupted work can be continued.");
        _fieldTips.SetToolTip(_emuMoviesCatalog, "Select an EmuMovies catalog or type its system ID. Catalog choices are separate from LaunchBox platform names.");
        _fieldTips.SetToolTip(_emuMoviesMameFallback, "Also search the MAME catalog for supported classic arcade profiles in TeknoParrot.");
        LoadEmuMoviesPageSettings();
        _emuMoviesPlatform.SelectedIndexChanged += (_, _) => SelectEmuMoviesPlatform();
        foreach (var field in new Control[] { _emuMoviesCatalog, _emuMoviesMatchFolder, _emuMoviesWorkDirectory, _emuMoviesSyncExecutable })
            field.TextChanged += (_, _) => ScheduleEmuMoviesSettingsSave();
        _emuMoviesMameFallback.CheckedChanged += (_, _) => ScheduleEmuMoviesSettingsSave();
        _emuMoviesSaveTimer.Tick += (_, _) => { _emuMoviesSaveTimer.Stop(); TrySaveEmuMoviesPageSettings(); };
        page.Disposed += (_, _) => _emuMoviesSaveTimer.Dispose();
        FormClosing += (_, _) => TrySaveEmuMoviesPageSettings();
        page.VisibleChanged += async (_, _) => { if (page.Visible) await RefreshEmuMoviesPlatformsAsync(false); };
    }

    private static ComboBox EmuMoviesCombo(bool editable, string name) => new EmuMoviesComboBox()
    {
        Name = name, BackColor = Palette.Input, ForeColor = Palette.Ink, FlatStyle = FlatStyle.Flat,
        DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList,
        Font = Palette.Font(), IntegralHeight = false, DropDownHeight = 320, DrawMode = DrawMode.OwnerDrawFixed,
        AutoCompleteMode = editable ? AutoCompleteMode.SuggestAppend : AutoCompleteMode.None,
        AutoCompleteSource = editable ? AutoCompleteSource.ListItems : AutoCompleteSource.None
    };

    private static FlowLayoutPanel EmuMoviesActionRow(params Control[] controls)
    {
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0), Padding = new Padding(0), MinimumSize = new Size(0, 46) };
        row.Controls.AddRange(controls); return row;
    }

    private void AddEmuMoviesPathField(string caption, TextBox input, bool executable)
    {
        var browse = WorkButton("Browse…", (_, _) => { BrowsePath(input, executable); ScheduleEmuMoviesSettingsSave(); }, false, 115);
        AddField(_emuMoviesFields, caption, input, browse);
    }

    private string EmuMoviesLaunchBoxRoot() => _paths.TryGetValue("LaunchBoxPath", out var box) ? box.Text.Trim().Trim('"') : _settings.LaunchBoxPath;
    private string EmuMoviesSettingsKey(string platform) => _emuMoviesLoadedRoot.TrimEnd('\\', '/') + "|" + platform;

    private async Task RefreshEmuMoviesPlatformsAsync(bool force)
    {
        if (_busy) return;
        var launchBoxRoot = EmuMoviesLaunchBoxRoot();
        if (!force && _emuMoviesPlatform.Items.Count > 0 && string.Equals(_emuMoviesLoadedRoot, launchBoxRoot, StringComparison.OrdinalIgnoreCase)) return;
        if (string.IsNullOrWhiteSpace(launchBoxRoot) && !_fixture) { _emuMoviesSummary.Text = "Choose your LaunchBox folder in Setup, then refresh the platform list."; return; }
        await RunWork("Reading LaunchBox platforms for EmuMovies…", async ct =>
        {
            TrySaveEmuMoviesPageSettings();
            var choices = _fixture ? new List<EmuMoviesPlatformChoice> { new("TeknoParrot", @"D:\Games\TeknoParrot", 759), new("Nintendo Entertainment System", @"D:\Games\NES", 300) }
                : await Task.Run(() => EmuMoviesService.GetPlatforms(launchBoxRoot), ct);
            _emuMoviesLoading = true;
            try
            {
                _emuMoviesLoadedRoot = launchBoxRoot;
                _emuMoviesActivePlatform = "";
                _emuMoviesPlatform.Items.Clear(); _emuMoviesPlatform.Items.AddRange(choices.Cast<object>().ToArray());
                var preferred = choices.FirstOrDefault(x => x.Name.Equals(_emuMoviesPageSettings.LastPlatform, StringComparison.OrdinalIgnoreCase))
                    ?? choices.FirstOrDefault(x => x.Name.Equals(_settings.PlatformName, StringComparison.OrdinalIgnoreCase)) ?? choices.FirstOrDefault();
                if (preferred != null) _emuMoviesPlatform.SelectedItem = preferred;
            }
            finally { _emuMoviesLoading = false; }
            SelectEmuMoviesPlatform();
            if (choices.Count == 0) _emuMoviesSummary.Text = "No platforms were found in this LaunchBox installation.";
            SetStatus($"EmuMovies: {choices.Count:N0} LaunchBox platforms available.");
        });
    }

    private void SelectEmuMoviesPlatform()
    {
        if (_emuMoviesLoading || _emuMoviesInputsBusy || _emuMoviesPlatform.SelectedItem is not EmuMoviesPlatformChoice platform) return;
        TrySaveEmuMoviesPageSettings();
        _emuMoviesSaveTimer.Stop(); _emuMoviesLoading = true;
        try
        {
            _emuMoviesActivePlatform = platform.Name;
            _emuMoviesPageSettings.LastPlatform = platform.Name;
            _emuMoviesPageSettings.Platforms.TryGetValue(EmuMoviesSettingsKey(platform.Name), out var saved);
            var catalog = saved?.Catalog ?? GuessEmuMoviesCatalog(platform.Name);
            var catalogChoice = _emuMoviesCatalogChoices.FirstOrDefault(x => x.SystemId.Equals(catalog, StringComparison.OrdinalIgnoreCase) || x.DisplayName.Equals(catalog, StringComparison.OrdinalIgnoreCase));
            if (catalogChoice != null) _emuMoviesCatalog.SelectedItem = catalogChoice;
            else { _emuMoviesCatalog.SelectedIndex = -1; _emuMoviesCatalog.Text = catalog; }
            var teknoParrotPath = _paths.TryGetValue("TeknoParrotPath", out var emulatorFolder) ? emulatorFolder.Text : _settings.TeknoParrotPath;
            _emuMoviesMatchFolder.Text = ResolveEmuMoviesMatchFolder(platform, teknoParrotPath, saved?.MatchFolder);
            _emuMoviesWorkDirectory.Text = saved?.WorkDirectory ?? Path.Combine(_dataDirectory, "EmuMovies", SafeEmuMoviesFolderName(platform.Name));
            _emuMoviesSyncExecutable.Text = _emuMoviesPageSettings.SyncExecutablePath.Length > 0 ? _emuMoviesPageSettings.SyncExecutablePath : DefaultEmuMoviesSyncExecutable();
            var teknoParrot = platform.Name.Equals("TeknoParrot", StringComparison.OrdinalIgnoreCase);
            _emuMoviesMameFallback.Enabled = teknoParrot;
            _emuMoviesMameFallback.Checked = teknoParrot && (saved?.UseMameFallback ?? true);
            _emuMoviesLastReportDirectory = saved?.LastReportDirectory ?? "";
            _emuMoviesPlatformInfo.Text = $"{platform.GameCount:N0} imported games · Choices saved automatically";
            _emuMoviesSummary.Text = "Preview the import or download the missing gameplay videos.";
            _emuMoviesDetails.Text = "Download & import: use Open Sync / login, sign in, and leave Sync open.\r\nImport downloaded videos and Preview import work with Sync closed.\r\n" +
                (teknoParrot ? "ArcadePC covers PC arcade games. Classic fallback uses MAME or Sega Model 2 for supported hardware profiles." : "Confirm the EmuMovies catalog and match folder for this platform.");
        }
        finally { _emuMoviesLoading = false; }
    }

    internal static string ResolveEmuMoviesMatchFolder(EmuMoviesPlatformChoice platform, string teknoParrotPath, string? savedMatchFolder)
    {
        if (savedMatchFolder != null) return savedMatchFolder;
        var install = teknoParrotPath.Trim().Trim('"');
        return platform.Name.Equals("TeknoParrot", StringComparison.OrdinalIgnoreCase) && install.Length > 0
            ? Path.Combine(install, "UserProfiles") : platform.MatchFolder;
    }

    internal string SelectedEmuMoviesCatalog()
    {
        if (_emuMoviesCatalog.SelectedItem is EmuMoviesCatalogChoice selected && _emuMoviesCatalog.Text.Equals(selected.ToString(), StringComparison.Ordinal)) return selected.DisplayName;
        var typed = _emuMoviesCatalog.Text.Trim();
        return _emuMoviesCatalogChoices.FirstOrDefault(x => x.SystemId.Equals(typed, StringComparison.OrdinalIgnoreCase) || x.DisplayName.Equals(typed, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? typed;
    }

    private async Task RunEmuMoviesAsync(bool download, bool import)
    {
        if (_fixture) { SetStatus("EmuMovies downloads and imports are disabled in the UI fixture."); return; }
        await RunWork(download ? "Downloading missing EmuMovies gameplay videos…" : import ? "Importing downloaded gameplay videos…" : "Preparing the gameplay video import preview…", async ct =>
        {
            if (_emuMoviesPlatform.SelectedItem is not EmuMoviesPlatformChoice platform) throw new InvalidOperationException("Choose a LaunchBox platform first.");
            var catalog = SelectedEmuMoviesCatalog();
            if (catalog.Length == 0) throw new InvalidOperationException("Choose an EmuMovies catalog first.");
            var request = new EmuMoviesVideoRequest
            {
                LaunchBoxPath = EmuMoviesLaunchBoxRoot(), PlatformName = platform.Name,
                MatchFolder = _emuMoviesMatchFolder.Text.Trim().Trim('"'), Catalog = catalog,
                WorkDirectory = _emuMoviesWorkDirectory.Text.Trim().Trim('"'), SyncExecutablePath = _emuMoviesSyncExecutable.Text.Trim().Trim('"'),
                UseMameFallback = platform.Name.Equals("TeknoParrot", StringComparison.OrdinalIgnoreCase) && _emuMoviesMameFallback.Checked
            };
            if (request.WorkDirectory.Length == 0) throw new InvalidOperationException("Choose a download / work folder first.");
            SaveEmuMoviesPageSettings();
            SetEmuMoviesInputsBusy(true);
            try
            {
                _emuMoviesDetails.Text = download ? "Official Sync will download gameplay videos. Progress appears here and in Jobs & history." : "Checking staged videos against the existing LaunchBox library…";
                var progress = new Progress<JobEvent>(item => { _progress.Report(item); _emuMoviesSummary.Text = item.Message; });
                var service = new EmuMoviesService(_dataDirectory, progress);
                var result = await Task.Run(() => service.RunAsync(request, download, import, ct), ct);
                _emuMoviesLastReportDirectory = result.ReportDirectory;
                _emuMoviesSummary.Text = $"{(download ? "Download finished" : import ? "Import finished" : "Preview ready")} · {result.Downloaded:N0} downloaded · {result.Imported:N0} imported · {result.Skipped:N0} skipped · {result.Missing:N0} missing";
                _emuMoviesDetails.Text = string.Join(Environment.NewLine, result.Details.Take(1000));
                if (result.Details.Count > 1000) _emuMoviesDetails.AppendText(Environment.NewLine + "Open reports to see every result.");
                SetStatus(_emuMoviesSummary.Text); SaveEmuMoviesPageSettings();
            }
            finally { SetEmuMoviesInputsBusy(false); }
        });
    }

    private void SetEmuMoviesInputsBusy(bool busy)
    {
        _emuMoviesInputsBusy = busy;
        // Keep the container enabled: Windows paints disabled labels black on this dark theme.
        foreach (var text in new[] { _emuMoviesMatchFolder, _emuMoviesWorkDirectory, _emuMoviesSyncExecutable })
            text.ReadOnly = busy;
        _emuMoviesPlatform.Enabled = !busy;
        _emuMoviesCatalog.Enabled = !busy;
        _emuMoviesMameFallback.Enabled = !busy && _emuMoviesActivePlatform.Equals("TeknoParrot", StringComparison.OrdinalIgnoreCase);
    }

    internal Task RunEmuMoviesBusyFixtureAsync(Task release)
    {
        if (!_fixture) throw new InvalidOperationException("The busy-state fixture is available only in the isolated UI test.");
        return RunWork("Preparing the gameplay video import preview…", async _ =>
        {
            SetEmuMoviesInputsBusy(true);
            try
            {
                _emuMoviesSummary.Text = "Preview in progress · checking staged gameplay videos…";
                _emuMoviesDetails.Text = "Checking staged videos against the existing LaunchBox library…\r\nThe selected platform, catalog and folders stay locked until the preview finishes.";
                await release;
            }
            finally { SetEmuMoviesInputsBusy(false); }
        });
    }

    private void OpenEmuMoviesSyncDownload()
    {
        if (_fixture) { SetStatus("Opening the download page is disabled in the UI fixture."); return; }
        try
        {
            Process.Start(new ProcessStartInfo(EmuMoviesSyncDownloadUrl) { UseShellExecute = true });
            SetStatus("Official download page opened. Install Sync, choose its executable above, then use Open Sync / login.");
        }
        catch (Exception ex) { ShowDetails("Open EmuMovies download page", ex.Message); }
    }

    private void OpenEmuMoviesSync()
    {
        if (_fixture) { SetStatus("Opening external apps is disabled in the UI fixture."); return; }
        try
        {
            var executable = _emuMoviesSyncExecutable.Text.Trim().Trim('"');
            if (!File.Exists(executable)) throw new FileNotFoundException("Choose the official EmuMovies Sync executable with Browse first. If it is not installed, use Download EmuMovies Sync.");
            SaveEmuMoviesPageSettings();
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(executable)! });
            SetStatus("Sign in to official EmuMovies Sync, then return here to download gameplay videos.");
        }
        catch (Exception ex) { ShowDetails("Open EmuMovies Sync", ex.Message); }
    }

    private void OpenEmuMoviesReports()
    {
        if (_fixture) { SetStatus("Opening reports is disabled in the UI fixture."); return; }
        try
        {
            if (!Directory.Exists(_emuMoviesLastReportDirectory)) { SetStatus("Run Preview import or a video download to create reports first."); return; }
            OpenLocal(_emuMoviesLastReportDirectory);
        }
        catch (Exception ex) { ShowDetails("Open EmuMovies reports", ex.Message); }
    }

    private void ScheduleEmuMoviesSettingsSave()
    {
        if (_emuMoviesLoading || _emuMoviesInputsBusy || _fixture || _emuMoviesActivePlatform.Length == 0) return;
        _emuMoviesSaveTimer.Stop(); _emuMoviesSaveTimer.Start();
    }

    private void LoadEmuMoviesPageSettings()
    {
        if (_fixture) return;
        try
        {
            var path = Path.Combine(_dataDirectory, "emumovies-settings.json");
            if (File.Exists(path)) _emuMoviesPageSettings = JsonSerializer.Deserialize<EmuMoviesPageSettings>(File.ReadAllText(path)) ?? new();
            _emuMoviesPageSettings.Platforms = new(_emuMoviesPageSettings.Platforms, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { _emuMoviesPageSettings = new(); _emuMoviesDetails.Text = "Saved EmuMovies choices could not be read. Choose the platform and folders again.\r\n" + ex.Message; }
    }

    private void TrySaveEmuMoviesPageSettings()
    {
        try { SaveEmuMoviesPageSettings(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { SetStatus("EmuMovies choices could not be saved: " + ex.Message); }
    }

    private void SaveEmuMoviesPageSettings()
    {
        if (_fixture || _emuMoviesLoading || _emuMoviesActivePlatform.Length == 0) return;
        _emuMoviesPageSettings.Platforms[EmuMoviesSettingsKey(_emuMoviesActivePlatform)] = new()
        {
            Catalog = SelectedEmuMoviesCatalog(), MatchFolder = _emuMoviesMatchFolder.Text.Trim().Trim('"'),
            WorkDirectory = _emuMoviesWorkDirectory.Text.Trim().Trim('"'), UseMameFallback = _emuMoviesMameFallback.Checked,
            LastReportDirectory = _emuMoviesLastReportDirectory
        };
        _emuMoviesPageSettings.LastPlatform = _emuMoviesActivePlatform;
        _emuMoviesPageSettings.SyncExecutablePath = _emuMoviesSyncExecutable.Text.Trim().Trim('"');
        Directory.CreateDirectory(_dataDirectory);
        var path = Path.Combine(_dataDirectory, "emumovies-settings.json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_emuMoviesPageSettings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }

    private string GuessEmuMoviesCatalog(string platform)
    {
        if (platform.Equals("TeknoParrot", StringComparison.OrdinalIgnoreCase)) return "ArcadePC";
        if (platform.Equals("Arcade", StringComparison.OrdinalIgnoreCase)) return "MAME";
        var key = new string(platform.Where(char.IsLetterOrDigit).ToArray());
        return _emuMoviesCatalogChoices.FirstOrDefault(x => new string(x.DisplayName.Where(char.IsLetterOrDigit).ToArray()).Equals(key, StringComparison.OrdinalIgnoreCase))?.SystemId ?? platform;
    }

    private static string SafeEmuMoviesFolderName(string platform) => string.Concat(platform.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    private static string DefaultEmuMoviesSyncExecutable()
    {
        var candidates = new[] { @"N:\Emulators\EmuMovies Sync\Sync Utility.exe", Path.Combine(AppContext.BaseDirectory, "EmuMovies Sync", "Sync Utility.exe") };
        return candidates.FirstOrDefault(File.Exists) ?? "";
    }

    private static List<EmuMoviesCatalogChoice> LoadEmuMoviesCatalogChoices()
    {
        var paths = new[] { Path.Combine(AppContext.BaseDirectory, "assets", "emumovies", "EmuMovies-Catalogs.csv"), Path.Combine(AppContext.BaseDirectory, "assets", "EmuMovies-Catalogs.csv"), Path.Combine(AppContext.BaseDirectory, "EmuMovies-Catalogs.csv"), @"N:\Emulators\EmuMovies-TeknoParrot\EmuMovies-Catalogs.csv" };
        foreach (var path in paths.Where(File.Exists))
        {
            try
            {
                var rows = File.ReadLines(path).Skip(1).Select(ParseEmuMoviesCsvLine).Where(x => x.Count >= 2 && x[1].Length > 0)
                    .Select(x => new EmuMoviesCatalogChoice(x[0], x[1])).DistinctBy(x => x.SystemId, StringComparer.OrdinalIgnoreCase).OrderBy(x => x.DisplayName).ToList();
                if (rows.Count > 0) return rows;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return [new("ArcadePC", "ArcadePC"), new("MAME", "MAME")];
    }

    private static List<string> ParseEmuMoviesCsvLine(string line)
    {
        var result = new List<string>(); var value = new StringBuilder(); var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') { if (quoted && i + 1 < line.Length && line[i + 1] == '"') { value.Append('"'); i++; } else quoted = !quoted; }
            else if (line[i] == ',' && !quoted) { result.Add(value.ToString()); value.Clear(); }
            else value.Append(line[i]);
        }
        result.Add(value.ToString()); return result;
    }

    private sealed record EmuMoviesCatalogChoice(string DisplayName, string SystemId)
    { public override string ToString() => DisplayName; }
    private sealed class EmuMoviesComboBox : ComboBox
    {
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            var selected = e.State.HasFlag(DrawItemState.Selected);
            using var background = new SolidBrush(selected ? Palette.Selection : Palette.Input);
            e.Graphics.FillRectangle(background, e.Bounds);
            var text = e.Index >= 0 && e.Index < Items.Count ? GetItemText(Items[e.Index]) : Text;
            TextRenderer.DrawText(e.Graphics, text, Font, Rectangle.Inflate(e.Bounds, -3, 0), Palette.Ink,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (e.State.HasFlag(DrawItemState.Focus)) e.DrawFocusRectangle();
        }

        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            if (Enabled || message.Msg is not (0x000F or 0x0317 or 0x0318)) return;
            if (message.Msg == 0x000F) { using var graphics = CreateGraphics(); DrawLockedSelection(graphics); }
            else if (message.WParam != IntPtr.Zero) { using var graphics = Graphics.FromHdc(message.WParam); DrawLockedSelection(graphics); }
        }

        private void DrawLockedSelection(Graphics graphics)
        {
            var visibleBounds = ClientRectangle;
            for (var ancestor = Parent; ancestor != null; ancestor = ancestor.Parent)
                visibleBounds.Intersect(RectangleToClient(ancestor.RectangleToScreen(ancestor.ClientRectangle)));
            if (visibleBounds.IsEmpty) return;
            graphics.SetClip(visibleBounds, System.Drawing.Drawing2D.CombineMode.Intersect);
            using var fill = new SolidBrush(Palette.Input); graphics.FillRectangle(fill, ClientRectangle);
            using var border = new Pen(Palette.Line); graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            TextRenderer.DrawText(graphics, Text, Font, new Rectangle(4, 0, Math.Max(0, Width - 24), Height), Palette.Ink,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            using var arrow = new SolidBrush(Palette.Muted);
            var center = new Point(Width - 11, Height / 2);
            graphics.FillPolygon(arrow, new Point[] { new(center.X - 4, center.Y - 2), new(center.X + 4, center.Y - 2), new(center.X, center.Y + 2) });
        }
    }

    private sealed class EmuMoviesCheckBox : CheckBox
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            if (Enabled) { base.OnPaint(e); return; }
            e.Graphics.Clear(BackColor);
            var box = new Rectangle(0, Math.Max(0, (Height - 13) / 2), 13, 13);
            using var fill = new SolidBrush(Palette.Input); e.Graphics.FillRectangle(fill, box);
            using var border = new Pen(Palette.Muted); e.Graphics.DrawRectangle(border, box);
            if (Checked)
            {
                using var tick = new Pen(Palette.Cyan, 2);
                e.Graphics.DrawLines(tick, new Point[] { new(box.Left + 3, box.Top + 6), new(box.Left + 5, box.Top + 9), new(box.Left + 10, box.Top + 3) });
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(17, 0, Math.Max(0, Width - 17), Height), Palette.Ink,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }
    private sealed class EmuMoviesPageSettings
    {
        public string LastPlatform { get; set; } = "";
        public string SyncExecutablePath { get; set; } = "";
        public Dictionary<string, EmuMoviesSavedChoice> Platforms { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
    private sealed class EmuMoviesSavedChoice
    {
        public string Catalog { get; set; } = "";
        public string MatchFolder { get; set; } = "";
        public string WorkDirectory { get; set; } = "";
        public bool UseMameFallback { get; set; } = true;
        public string LastReportDirectory { get; set; } = "";
    }
}
