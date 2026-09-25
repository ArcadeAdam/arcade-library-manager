using System.ComponentModel;
using ArcadeLibraryManager.Core;

namespace ArcadeLibraryManager.Desktop;

public sealed partial class MainForm
{
    private readonly MediaService _mediaService = new();
    private List<MediaSelection> _media = new();
    private DataGridView _mediaGrid = null!;
    private Label _mediaSummary = null!, _referenceDetails = null!, _cutoutModelStatus = null!;
    private PictureBox _artPreview = null!;
    private TextBox _assetDetails = null!;
    private Panel _mediaDetails = null!;
    private FlowLayoutPanel _mediaScanActions = null!, _mediaRenderActions = null!;
    private MediaAssets? _displayedThemeAssets;
    private ThemeReferenceInfo? _reference;
    private string _previewVideo = "";

    private void BuildMediaPage()
    {
        var page = Page("Make themes");
        var tabs = new ThemeTabs { Dock = DockStyle.Fill, Font = Palette.Font(10), Padding = new Point(16, 8) };
        var assets = new TabPage("Games") { BackColor = Palette.Page, Padding = new Padding(12, 18, 12, 12) };
        var format = new TabPage("Theme settings") { BackColor = Palette.Page, Padding = new Padding(12, 18, 12, 12), AutoScroll = true };
        tabs.TabPages.AddRange([assets, format]); page.Controls.Add(tabs);
        _mediaSummary = new Label { Text = "Scan local media to find existing themes to reuse and games ready to render.", Dock = DockStyle.Top, Height = 36, Font = Palette.Font(10, FontStyle.Bold), ForeColor = Palette.Ink };
        var reuseHint = Hint("Matching Arcade / Sega Model 2 themes are copied first. Rendering a new theme requires a gameplay snap.", 38); reuseHint.Dock = DockStyle.Top;
        var scanActions = _mediaScanActions = Toolbar(WorkButton("Scan local media", async (_, _) => await ScanMediaAsync(), false, 155), WorkButton("Select missing themes", (_, _) => { if (_themeBatch?.IsRunning == true) return; ShowAllThemeGames(); _mediaGrid.EndEdit(); foreach (var row in _media) row.Selected = CanProcessMissingTheme(row.Assets); _mediaGrid.Refresh(); }, false, 194), WorkButton("Clear selection", (_, _) => { if (_themeBatch?.IsRunning == true) return; ShowAllThemeGames(); _mediaGrid.EndEdit(); foreach (var row in _media) row.Selected = false; _mediaGrid.Refresh(); }, false, 133));
        var renderActions = _mediaRenderActions = Toolbar(WorkButton("Render preview", async (_, _) => await RenderPreviewAsync(), false, 148), WorkButton("Open preview", (_, _) => { if (File.Exists(_previewVideo)) OpenLocal(_previewVideo); else SetStatus("Render a preview first."); }, false, 132), WorkButton("Generate selected themes", async (_, _) => await RenderThemesAsync(), true, 218));
        _mediaGrid = Palette.Grid(); ConfigureThemeGrid(false);
        _mediaGrid.SelectionChanged += (_, _) => DisplayMediaDetails();
        var details = _mediaDetails = new Panel { Dock = DockStyle.Bottom, Height = 168, Padding = new Padding(0, 10, 0, 0), BackColor = Palette.Page };
        assets.SizeChanged += (_, _) => details.Height = assets.ClientSize.Height < 500 ? 96 : 168;
        _artPreview = new PictureBox { Dock = DockStyle.Left, Width = 185, BackColor = Palette.Navy, SizeMode = PictureBoxSizeMode.Zoom };
        _assetDetails = new TextBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Palette.Soft, ForeColor = Palette.Muted, Font = Palette.Font(9), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Text = "Select a game to inspect matched asset paths." };
        details.Controls.Add(_assetDetails); details.Controls.Add(_artPreview);
        assets.Controls.Add(_mediaGrid); assets.Controls.Add(details); assets.Controls.Add(renderActions); assets.Controls.Add(scanActions); assets.Controls.Add(reuseHint); assets.Controls.Add(_mediaSummary);
        var stack = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 }; stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); format.Controls.Add(stack);
        var output = Section(stack, "Theme output", "Use a consistent cabinet format, then preview before creating the missing themes.");
        var presets = new ComboBox { BackColor = Palette.Input, ForeColor = Palette.Ink, FlatStyle = FlatStyle.Flat, DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, Font = Palette.Font() }; presets.Items.AddRange(["16:9 · 1280 × 720", "16:9 · 1920 × 1080", "4:3 · 960 × 720", "4:3 · 1440 × 1080"]); presets.SelectedIndex = 0;
        presets.SelectedIndexChanged += (_, _) => { var dims = presets.SelectedIndex switch { 1 => (1920, 1080), 2 => (960, 720), 3 => (1440, 1080), _ => (1280, 720) }; _videoWidth.Value = dims.Item1; _videoHeight.Value = dims.Item2; };
        AddField(output, "Cabinet preset", presets);
        _videoWidth = Number(320, 3840, 1280); _videoHeight = Number(240, 2160, 720); _videoFps = Number(24, 60, 30); _videoSeconds = Number(ThemeDuration.MinimumSeconds, ThemeDuration.MaximumSeconds, ThemeDuration.DefaultSeconds);
        AddField(output, "Output width", _videoWidth); AddField(output, "Output height", _videoHeight); AddField(output, "Frames per second", _videoFps); AddField(output, "Duration (25–33 seconds)", _videoSeconds);
        _layout = new ComboBox { BackColor = Palette.Input, ForeColor = Palette.Ink, FlatStyle = FlatStyle.Flat, DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, Font = Palette.Font() }; _layout.Items.AddRange(["Fanart", "Cinema"]); _layout.SelectedIndex = 0; AddField(output, "Composition", _layout);
        _themeAudio = new CheckBox { Text = "Use gameplay audio when available", AutoSize = true, Checked = true, ForeColor = Palette.Ink }; AddField(output, "Audio", _themeAudio);
        _videoSide = new ComboBox { BackColor = Palette.Input, ForeColor = Palette.Ink, FlatStyle = FlatStyle.Flat, DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, Font = Palette.Font() }; _videoSide.Items.AddRange(["Left", "Right"]); _videoSide.SelectedIndex = 1; AddField(output, "Gameplay position", _videoSide);
        _themeMotion = Number(0, 100, 60); AddField(output, "Movement (0–100)", _themeMotion);
        _fieldTips.SetToolTip(_layout, "Fanart places a smaller gameplay window to one side, with a glowing border, layered artwork and transparent cutouts. Cinema gives gameplay more space. A gameplay video is required for every theme.");
        _fieldTips.SetToolTip(_videoSide, "Choose the side for the gameplay window. The opposite side gives character cutouts and logos room to move.");
        _fieldTips.SetToolTip(_themeMotion, "0 keeps the composition still; higher values increase the character bounce and movement of the art layers.");
        var layers = Section(stack, "Fanart layers", "Transparent art moves independently over the background. Use your own PNG cutouts, or create them locally from flyers and box art.");
        AddPath(layers, "Theme assets folder", "ThemeAssetsPath");
        _fieldTips.SetToolTip(_paths["ThemeAssetsPath"], "Optional. Defaults to LaunchBox\\Images\\<platform>\\Theme Assets. Create a folder named for the TeknoParrot profile ID or LaunchBox game ID; place background.png/jpg, cutout*.png with real transparency, and optional logo.png inside. No ROM folders are scanned.");
        _autoCutouts = new CheckBox { Text = "Create missing cutouts from local artwork", AutoSize = true, Checked = true, ForeColor = Palette.Ink }; AddField(layers, "Automatic cutouts", _autoCutouts);
        _fieldTips.SetToolTip(_autoCutouts, "Uses the local cutout model during rendering. Extracted figures are cached; source art is unchanged. Results depend on the art and should be previewed. Missing or unsuitable cutouts never become opaque panels.");
        AddPath(layers, "Cutout model (.onnx)", "ThemeCutoutModelPath", true);
        _fieldTips.SetToolTip(_paths["ThemeCutoutModelPath"], "Select isnet-general-use.onnx to override the discovered model. The illustrated-character model beside it is used when available. Download cutout models installs both verified models in the download cache; artwork is processed locally.");
        var modelActions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        modelActions.Controls.Add(WorkButton("Download cutout models", async (_, _) => await InstallCutoutModelAsync(), false, 214));
        AddField(layers, "Local processing", modelActions, null, 51);
        _cutoutModelStatus = new Label { Dock = DockStyle.Top, Height = 58, ForeColor = Palette.Muted, Font = Palette.Font(9.3f) }; AddField(layers, "Model status", _cutoutModelStatus, null, 67);
        _paths["ThemeCutoutModelPath"].TextChanged += (_, _) => RefreshCutoutModelStatus();
        var layerNote = Hint("General and illustrated-character models download once (about 340 MiB), then run locally without an account. Handmade transparent cutouts work without models. Preview one game before a full batch.", 47);
        layers.Controls.Add(layerNote, 0, layers.RowCount); layers.SetColumnSpan(layerNote, 3); layers.RowCount++;

        var reference = Section(stack, "Learn from a reference", "Use an existing theme to choose aspect ratio, pacing, frame rate and audio. The composition uses your chosen template.");
        var refActions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        refActions.Controls.Add(WorkButton("Choose example…", async (_, _) => await InspectReferenceAsync(), false, 165));
        refActions.Controls.Add(WorkButton("Apply its format", (_, _) => { if (_reference == null) { SetStatus("Choose and inspect an example first."); return; } ReadSettingsFromControls(); ThemeRenderer.ApplyReferencePreset(_settings, _reference); LoadSettingsIntoControls(); SetStatus("Reference format applied. The output settings remain editable."); }, true, 153));
        AddField(reference, "Example theme", refActions, null, 52);
        _referenceDetails = new Label { Text = "No reference selected. Choose a video from your existing theme folder.", Dock = DockStyle.Top, Height = 67, ForeColor = Palette.Muted, Font = Palette.Font(9.4f) }; AddField(reference, "Reference properties", _referenceDetails, null, 78);
        BuildThemeProgressPanel(page);

    }
    private async Task ScanMediaAsync()
    {
        await RunWork("Matching local artwork, gameplay and theme videos…", async ct => {
            ReadSettingsFromControls(); SettingsStore.Save(_dataDirectory, _settings);
            if (string.IsNullOrWhiteSpace(_settings.LaunchBoxPath)) throw new InvalidOperationException("Choose the LaunchBox installation in Setup first.");
            _games = await Task.Run(() => Engine().ScanAsync(ct), ct); UpdateLibrary();
            await RefreshMediaRowsAsync(ct);
            SetStatus($"Media scan complete. {_media.Count(x => x.Assets.Theme.Length == 0):N0} games need themes; {_media.Count(x => MediaService.HasReusableTheme(x.Assets)):N0} can reuse Arcade / Model 2 themes.");
        });
    }
    private async Task RefreshMediaRowsAsync(CancellationToken ct)
    {
        _mediaService.ClearCache();
        _media = await Task.Run(() => {
            var result = new List<MediaSelection>();
            foreach (var game in MediaService.SelectThemeGames(_settings, _games)) { ct.ThrowIfCancellationRequested(); result.Add(new MediaSelection(game, _mediaService.FindAssets(_settings, game))); }
            return result;
        }, ct);
        ShowAllThemeGames();
    }
    private void DisplayMediaDetails()
    {
        var row = CurrentThemeGame();
        if (row == null || _artPreview == null || !_mediaDetails.Visible || ReferenceEquals(_displayedThemeAssets, row.Assets)) return;
        var a = row.Assets;
        var cutoutStatus = a.Cutouts.Count > 0 ? $"{a.Cutouts.Count} transparent PNG layer(s)" : a.ArtworkSources.Count > 0 ? "No prepared cutout; automatic extraction can use the artwork below when a model is installed." : "No prepared cutout or suitable flyer/box art found.";
        _assetDetails.Text = $"  {a.Title}\r\n  Background ({a.BackgroundKind}): {a.Background}\r\n  Cutouts: {cutoutStatus}\r\n  {string.Join("; ", a.Cutouts)}\r\n  Artwork for extraction: {string.Join("; ", a.ArtworkSources)}\r\n  Theme assets folder: {a.ThemeAssetDirectory}\r\n  Logo: {a.Logo}\r\n  Gameplay: {a.Snap}\r\n  Theme: {a.Theme}\r\n  Existing theme to reuse ({a.ReusableThemePlatform}): {a.ReusableTheme}\r\n  Theme matching: {a.ThemeReuseDetail}\r\n  Destination: {(MediaService.HasReusableTheme(a) ? a.ThemeReuseDestination : a.ThemeDestination)}";
        _displayedThemeAssets = a;
        var previous = _artPreview.Image; _artPreview.Image = null; previous?.Dispose();
        var image = File.Exists(a.Background) ? a.Background : a.Logo;
        if (File.Exists(image)) { try { using var stream = File.OpenRead(image); using var source = Image.FromStream(stream); _artPreview.Image = new Bitmap(source); } catch (Exception ex) when (ex is ArgumentException or IOException or OutOfMemoryException) { } }
    }
    private async Task RenderPreviewAsync()
    {
        if (_fixture) { SetStatus("Rendering is disabled in the UI fixture."); return; }
        var row = CurrentThemeGame();
        if (row == null) { SetStatus("Scan media and highlight a game first."); return; }
        await RunWork("Rendering a theme preview…", async ct => {
            ReadSettingsFromControls(); SettingsStore.Save(_dataDirectory, _settings);
            _mediaService.ClearCache();
            var assets = await Task.Run(() => _mediaService.FindAssets(_settings, row.Game), ct);
            if (MediaService.HasVideoSnap(assets)) ThemeRenderer.RequireTools(_settings);
            ct.ThrowIfCancellationRequested();
            row.Assets = assets; _previewVideo = "";
            var batch = BeginThemeBatch([row], true);
            try
            {
                var progress = StartThemeGame(batch, row);
                if (!MediaService.HasVideoSnap(assets))
                {
                    CompleteThemeGame(batch, row, "Skipped", "Missing video snap. Add a gameplay video before creating a theme.");
                    SetStatus("Preview skipped: this game is missing its video snap."); return;
                }
                _previewVideo = await new ThemeRenderer().RenderPreviewAsync(_settings, assets, progress, ct);
                CompleteThemeGame(batch, row, "Completed", "Preview ready. No library theme was changed.");
                SetStatus("Preview ready. Open preview to watch it; no library theme was changed.");
            }
            catch (OperationCanceledException) { StopThemeBatch(batch, true, "Cancelled by the user"); throw; }
            catch (Exception ex) { if (batch.CurrentId.Length > 0) CompleteThemeGame(batch, row, "Failed", ex.Message); StopThemeBatch(batch, false, ex.Message); throw; }
            finally { RefreshThemeProgressView(); }
        });
    }
    internal static bool CanProcessMissingTheme(MediaAssets assets) => string.IsNullOrWhiteSpace(assets.Theme) && (MediaService.HasReusableTheme(assets) || MediaService.HasVideoSnap(assets));

    internal void ShowThemeReuseFixture(string root)
    {
        if (!_fixture) throw new InvalidOperationException("Theme fixtures require fixture mode.");
        _media = new[] { ("crusnusa", "Cruis'n USA", "Arcade"), ("overrevb", "Over Rev", "Sega Model 2"), ("kartduel", "Kart Duel", ""), ("pocketrc", "Pocket Racer", "") }
            .Select(item => new MediaSelection(new GameRecord { Id = item.Item1, Name = item.Item2 }, new MediaAssets {
                ProfileId = item.Item1, Title = item.Item2,
                Theme = item.Item1 == "pocketrc" ? Path.Combine(root, "existing.mp4") : "",
                ReusableTheme = item.Item3.Length > 0 ? Path.Combine(root, "Videos", item.Item3, "Theme", item.Item2 + ".mp4") : "",
                ReusableThemePlatform = item.Item3,
                ThemeReuseDestination = Path.Combine(root, "Videos", "TeknoParrot", "Theme", item.Item2 + ".mp4"),
                ThemeReuseDetail = item.Item3.Length > 0 ? "Reuse existing theme; source video stays in place." : ""
            })).ToList();
        ShowAllThemeGames();
    }

    private async Task RenderThemesAsync()
    {
        if (_fixture) { SetStatus("Rendering is disabled in the UI fixture."); return; }
        _mediaGrid.EndEdit(); var selected = _media.Where(x => x.Selected).ToList();
        if (selected.Count == 0) { SetStatus("Select the games whose missing themes you want to generate."); return; }
        await RunWork("Preparing selected missing themes…", async ct => {
            ReadSettingsFromControls(); SettingsStore.Save(_dataDirectory, _settings);
            var allowed = await Task.Run(() => MediaService.SelectThemeGames(_settings, _games).Select(g => g.Id).ToHashSet(StringComparer.OrdinalIgnoreCase), ct);
            _mediaService.ClearCache();
            var prepared = await Task.Run(() => {
                var result = new Dictionary<string, (MediaAssets? Assets, Exception? Error)>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in selected.Where(row => allowed.Contains(row.Game.Id)))
                {
                    ct.ThrowIfCancellationRequested();
                    try { result[row.Game.Id] = (_mediaService.FindAssets(_settings, row.Game), null); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { result[row.Game.Id] = (null, ex); }
                }
                return result;
            }, ct);
            // Report a missing shared dependency once, before changing any selections or creating the queue.
            if (prepared.Values.Any(item => item.Assets is { } assets && !File.Exists(assets.Theme) && !MediaService.HasReusableTheme(assets) && MediaService.HasVideoSnap(assets))) ThemeRenderer.RequireTools(_settings);
            ct.ThrowIfCancellationRequested();
            var batch = BeginThemeBatch(selected, false); var errors = new List<string>(); var succeeded = 0; var skipped = 0;
            try
            {
                var renderer = new ThemeRenderer();
                foreach (var row in selected)
                {
                    ct.ThrowIfCancellationRequested();
                    await WaitForThemeResumeAsync(batch, ct);
                    var progress = StartThemeGame(batch, row);
                    try
                    {
                        if (!allowed.Contains(row.Game.Id)) { skipped++; CompleteThemeGame(batch, row, "Skipped", "No longer the preferred LaunchBox game; no theme was changed."); continue; }
                        var item = prepared[row.Game.Id];
                        if (item.Error is { } discoveryError) throw discoveryError;
                        var assets = item.Assets!; row.Assets = assets;
                        if (File.Exists(assets.Theme)) { skipped++; CompleteThemeGame(batch, row, "Skipped", "Existing theme preserved."); continue; }
                        if (!MediaService.HasReusableTheme(assets) && !MediaService.HasVideoSnap(assets)) { skipped++; CompleteThemeGame(batch, row, "Skipped", "No matching existing theme or video snap. Add a gameplay video before creating a theme."); continue; }
                        var reused = MediaService.HasReusableTheme(assets);
                        var output = await renderer.RenderAsync(_settings, assets, progress, ct);
                        row.Assets.Theme = output; succeeded++;
                        CompleteThemeGame(batch, row, "Completed", reused ? "Reused existing " + assets.ReusableThemePlatform + " theme: " + output : "Theme saved and validated: " + output);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { errors.Add(row.Title + ": " + ex.Message); CompleteThemeGame(batch, row, "Failed", ex.Message); }
                }
                ShowResult("Theme generation", new OperationResult(succeeded, skipped, errors));
            }
            catch (OperationCanceledException) { StopThemeBatch(batch, true, "Cancelled by the user; unfinished games stay selected for retry."); throw; }
            catch (Exception ex) { StopThemeBatch(batch, false, ex.Message); throw; }
            finally { RefreshThemeProgressView(); }
        });
    }
    private void RefreshCutoutModelStatus()
    {
        if (_cutoutModelStatus == null) return;
        var settings = new AppSettings { ThemeCutoutModelPath = _paths.GetValueOrDefault("ThemeCutoutModelPath")?.Text.Trim().Trim('"') ?? "", CachePath = _paths.GetValueOrDefault("CachePath")?.Text.Trim().Trim('"') ?? "" };
        try { _cutoutModelStatus.Text = ThemeCutoutService.GetModelStatus(settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { _cutoutModelStatus.Text = "The model path could not be read. Check the file location."; }
    }
    private async Task InstallCutoutModelAsync()
    {
        if (_fixture) { SetStatus("Model downloads are disabled in the UI fixture."); return; }
        await RunWork("Downloading the local cutout models…", async ct => {
            ReadSettingsFromControls();
            var path = await ThemeCutoutService.InstallModelAsync(_settings, _progress, ct);
            _settings.ThemeCutoutModelPath = path; _paths["ThemeCutoutModelPath"].Text = path;
            SettingsStore.Save(_dataDirectory, _settings); RefreshCutoutModelStatus();
            SetStatus("Cutout models ready. Render a preview to check the artwork extraction.");
        });
    }
    private async Task InspectReferenceAsync()
    {
        ReadSettingsFromControls(); using var dialog = new OpenFileDialog { Title = "Choose an existing theme video", Filter = "Video files|*.mp4;*.mkv;*.avi;*.webm;*.mov;*.wmv;*.mpg;*.mpeg|All files|*.*" };
        if (Directory.Exists(_settings.ThemeExamplesPath)) dialog.InitialDirectory = _settings.ThemeExamplesPath;
        else { var folders = LaunchBoxService.DiscoverMediaFolders(_settings); if (folders.TryGetValue("Theme Video", out var folder) && Directory.Exists(folder)) dialog.InitialDirectory = folder; }
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await RunWork("Inspecting the reference theme…", async ct => { _reference = await new ThemeRenderer().InspectReferenceAsync(_settings, dialog.FileName, ct); _referenceDetails.Text = $"{Path.GetFileName(_reference.Path)}\r\n{_reference.Width} × {_reference.Height}  ·  {_reference.Fps:N2} FPS  ·  {_reference.DurationSeconds:N1} seconds\r\n{_reference.Codec}  ·  {(_reference.HasAudio ? "Audio present" : "Silent")}"; SetStatus("Reference inspected. Apply its format to use an editable output preset."); });
    }
    private sealed class MediaSelection(GameRecord game, MediaAssets assets)
    {
        public GameRecord Game { get; } = game; public MediaAssets Assets { get; set; } = assets; public bool Selected { get; set; }
        public string Title => Assets.Title; public string Background => Assets.Background.Length > 0 ? "Found" : "Missing"; public string Logo => Assets.Logo.Length > 0 ? "Found" : "Missing";
        public string Cutouts => Assets.Cutouts.Count > 0 ? $"{Assets.Cutouts.Count} PNG layers" : Assets.ArtworkSources.Count > 0 ? "Artwork ready" : "None";
        public string Snap => MediaService.HasVideoSnap(Assets) ? "Found" : "Missing"; public string Theme => Assets.Theme.Length > 0 ? "Preserved" : MediaService.HasReusableTheme(Assets) ? "Reuse " + Assets.ReusableThemePlatform : MediaService.HasVideoSnap(Assets) ? "Can render" : "Missing video snap";
    }
}


