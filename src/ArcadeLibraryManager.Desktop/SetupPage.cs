using System.Text.Json;
using ArcadeLibraryManager.Core;

namespace ArcadeLibraryManager.Desktop;

public sealed partial class MainForm
{
    private readonly Dictionary<string, TextBox> _paths = new();
    private TextBox _romRoots = null!, _platform = null!, _archiveUrl = null!;
    private CheckBox _fillMetadata = null!, _themeAudio = null!, _autoCutouts = null!;
    private NumericUpDown _bandwidth = null!, _videoWidth = null!, _videoHeight = null!, _videoFps = null!, _videoSeconds = null!;
    private ComboBox _layout = null!, _videoSide = null!;
    private NumericUpDown _themeMotion = null!;
    private readonly ToolTip _fieldTips = new() { AutoPopDelay = 20000, InitialDelay = 450, ReshowDelay = 100, ShowAlways = true };

    private void BuildSetupPage()
    {
        var page = Page("Setup");
        var scroll = new Panel { Name = "SetupScroll", Dock = DockStyle.Fill, AutoScroll = true, BackColor = Palette.Page };
        var stack = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(0, 0, 14, 15) };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scroll.Controls.Add(stack); page.Controls.Add(scroll);
        var intro = Section(stack, "Library locations", "Ghosted paths are examples only. Choose your own folders; usable games stay where they already live.");
        _romRoots = Palette.TextBox(true); _romRoots.ScrollBars = ScrollBars.Vertical; _romRoots.Height = 76;
        _romRoots.PlaceholderText = @"D:\Arcade\System roms\TeknoParrot\Teknoparrot" + Environment.NewLine + @"C:\Users\YourName\Downloads";
        var rootsBrowse = Palette.Button("Add folder…", false, 115);
        rootsBrowse.Click += (_, _) => { using var dialog = new FolderBrowserDialog { Description = "Add an existing ROM or Downloads folder", UseDescriptionForTitle = true }; if (dialog.ShowDialog(this) == DialogResult.OK) _romRoots.Text = string.Join(Environment.NewLine, _romRoots.Lines.Append(dialog.SelectedPath).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase)); };
        AddField(intro, "Existing ROM sources", _romRoots, rootsBrowse, 88);
        intro.Controls.Add(Hint("One folder per line. Include Downloads if it contains game archives. Only index folders you want searched.", 32), 1, intro.RowCount++);
        AddPath(intro, "Teknoparrot emulator installed location", "TeknoParrotPath", height: 56);
        AddPath(intro, "New-game destination", "DestinationPath");
        AddPath(intro, "LaunchBox folder", "LaunchBoxPath");
        _platform = Palette.TextBox(); _platform.Name = "SetupPlatform"; _platform.PlaceholderText = "TeknoParrot"; AddField(intro, "Existing platform name", _platform);
        _archiveUrl = Palette.TextBox(); _archiveUrl.PlaceholderText = "https://archive.org/details/teknoparrot-collection_"; AddField(intro, "Online archive URL", _archiveUrl);
        AddPath(intro, "Download cache", "CachePath");
        var discoverActions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        discoverActions.Controls.Add(WorkButton("Discover from selected folders", async (_, _) => await DiscoverSetupAsync(), false, 282));
        AddField(intro, "Find related locations", discoverActions, null, 53);
        BuildLaunchBoxSetupCard(stack);
        var options = Section(stack, "Playback & tools", "Optional tool paths can be discovered from a configured LaunchBox installation.");
        AddPath(options, "Theme examples folder", "ThemeExamplesPath");
        AddPath(options, "MAME artwork folder", "MameArtworkPath");

        var bezelSource = Palette.TextBox(); bezelSource.PlaceholderText = "https://discord.com/channels/284830696860680192/1033793318460588053"; _paths["BezelSourceUrl"] = bezelSource; AddField(options, "Online bezel source", bezelSource);
        AddPath(options, "FFmpeg executable", "FfmpegPath", true);
        _fieldTips.SetToolTip(_paths["FfmpegPath"], "Select ffmpeg.exe from a complete FFmpeg build. Keep ffprobe.exe beside it, along with any DLLs supplied by that build. A folder containing only ffmpeg.exe cannot generate themes.");
        AddPath(options, "7-Zip executable", "SevenZipPath", true);
        _bandwidth = Number(0, 10000, 0); AddField(options, "Download limit (Mbps)", _bandwidth);
        options.Controls.Add(Hint("0 is unlimited. Mbps means megabits per second.", 28), 1, options.RowCount++);
        _fillMetadata = new CheckBox { Text = "Fill missing metadata; preserve existing curated values", AutoSize = true, Checked = true, ForeColor = Palette.Ink };
        AddField(options, "LaunchBox policy", _fillMetadata);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 8, 0, 10) };
        actions.Controls.Add(WorkButton("Save setup", (_, _) => SaveSettings(), true, 145));
        actions.Controls.Add(WorkButton("Export preset…", (_, _) => ExportPreset(), false, 145));
        actions.Controls.Add(WorkButton("Import preset…", (_, _) => ImportPreset(), false, 145));
        stack.Controls.Add(actions, 0, stack.RowCount++);
    }
    private static TableLayoutPanel Section(TableLayoutPanel parent, string title, string description)
    {
        var card = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, BackColor = Palette.Surface, Padding = new Padding(22, 19, 22, 12), Margin = new Padding(0, 0, 0, 18), ColumnCount = 3 };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 185)); card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 122));
        var heading = Palette.Label(title, 13, Palette.Ink, true); heading.Margin = new Padding(0, 0, 0, 8);
        card.Controls.Add(heading, 0, card.RowCount); card.SetColumnSpan(heading, 3); card.RowCount++;
        var note = new Label { Text = description, AutoSize = false, Dock = DockStyle.Top, Height = 38, ForeColor = Palette.Muted, Font = Palette.Font(9.3f) };
        card.Controls.Add(note, 0, card.RowCount); card.SetColumnSpan(note, 3); card.RowCount++;
        card.Paint += (_, e) => {
            using var edge = new Pen(Palette.Line); e.Graphics.DrawRectangle(edge, 0, 0, card.Width - 1, card.Height - 1);
            using var strip = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, Math.Max(1, card.Width), 3), Palette.Accent, Palette.Cyan, 0f);
            e.Graphics.FillRectangle(strip, 0, 0, card.Width, 2);
        };
        parent.Controls.Add(card, 0, parent.RowCount++); return card;
    }
    private static void AddField(TableLayoutPanel table, string caption, Control input, Control? trailing = null, int height = 43)
    {
        var row = table.RowCount++; while (table.RowStyles.Count <= row) table.RowStyles.Add(new RowStyle(SizeType.AutoSize)); table.RowStyles[row] = new RowStyle(SizeType.Absolute, height);
        var label = Palette.Label(caption, 9.3f, Palette.Ink); label.Margin = new Padding(0, 6, 12, 0); table.Controls.Add(label, 0, row);
        if (input is TextBox textBox && !string.IsNullOrWhiteSpace(textBox.PlaceholderText)) textBox.AccessibleDescription = "Example only: " + textBox.PlaceholderText.Replace(Environment.NewLine, "; ");
        input.Margin = new Padding(0, 3, 10, 7); input.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        if (input is not NumericUpDown && input is not CheckBox && input is not Label) input.Dock = DockStyle.Fill;
        table.Controls.Add(input, 1, row);
        if (trailing != null) { trailing.Margin = new Padding(0, 0, 0, 8); table.Controls.Add(trailing, 2, row); }
        else table.SetColumnSpan(input, 2);
    }
    private void AddPath(TableLayoutPanel panel, string caption, string key, bool file = false, int height = 43)
    {
        var input = Palette.TextBox(); input.Name = "Setup" + key; _paths[key] = input;
        input.PlaceholderText = key switch {
            "TeknoParrotPath" => @"N:\Emulators\TeknoParrot",
            "DestinationPath" => @"D:\Arcade\System roms\TeknoParrot\Teknoparrot",
            "LaunchBoxPath" => @"N:\LaunchBox",
            "CachePath" => @"E:\temp",
            "ThemeExamplesPath" => @"N:\LaunchBox\Videos\TeknoParrot\Theme",
            "ThemeAssetsPath" => @"N:\LaunchBox\Images\TeknoParrot\Theme Assets",
            "ThemeCutoutModelPath" => @"E:\temp\Models\isnet-general-use.onnx",
            "MameArtworkPath" => @"N:\Emulators\MAME\artwork",
            "BezelImportPath" => @"Example: C:\Users\YourName\Downloads\TeknoParrot Bezels",
            "FfmpegPath" => @"N:\LaunchBox\ThirdParty\FFMPEG\ffmpeg.exe",
            "SevenZipPath" => @"C:\Program Files\7-Zip\7z.exe",
            _ => ""
        };
        var browse = Palette.Button("Browse…", false, 115); browse.Click += (_, _) => BrowsePath(input, file, key == "ThemeCutoutModelPath");
        AddField(panel, caption, input, browse, height);
    }
    private void BrowsePath(TextBox input, bool file, bool model = false)
    {
        if (file) { using var dialog = new OpenFileDialog { Title = model ? "Choose the cutout model" : "Choose an executable", Filter = model ? "ONNX models (*.onnx)|*.onnx|All files (*.*)|*.*" : "Executable files (*.exe)|*.exe|All files (*.*)|*.*", CheckFileExists = true }; if (File.Exists(input.Text)) dialog.FileName = input.Text; if (dialog.ShowDialog(this) == DialogResult.OK) input.Text = dialog.FileName; }
        else { using var dialog = new FolderBrowserDialog { Description = "Choose a folder", UseDescriptionForTitle = true }; if (Directory.Exists(input.Text)) dialog.InitialDirectory = input.Text; if (dialog.ShowDialog(this) == DialogResult.OK) input.Text = dialog.SelectedPath; }
    }
    private static NumericUpDown Number(int min, int max, int value) => new() { Minimum = min, Maximum = max, Value = value, Width = 105, Height = 29, Font = Palette.Font(), ThousandsSeparator = true, BackColor = Palette.Input, ForeColor = Palette.Ink };
    private void LoadSettingsIntoControls()
    {
        _romRoots.Lines = _settings.RomRoots.ToArray();
        foreach (var (key, input) in _paths) input.Text = typeof(AppSettings).GetProperty(key)?.GetValue(_settings)?.ToString() ?? "";
        _platform.Text = _settings.PlatformName; _archiveUrl.Text = _settings.ArchiveUrl;
        _fillMetadata.Checked = _settings.FillMissingMetadata;
        _bandwidth.Value = Math.Clamp(_settings.MaxDownloadMbps, 0, 10000);
        _videoWidth.Value = Math.Clamp(_settings.VideoWidth, 320, 3840); _videoHeight.Value = Math.Clamp(_settings.VideoHeight, 240, 2160);
        _videoFps.Value = Math.Clamp(_settings.VideoFps, 24, 60); _videoSeconds.Value = ThemeDuration.Normalize(_settings.VideoSeconds);
        _themeAudio.Checked = _settings.ThemeAudio;
        _layout.SelectedItem = string.Equals(_settings.ThemeLayout, "Showcase", StringComparison.OrdinalIgnoreCase) ? "Fanart" : _settings.ThemeLayout; if (_layout.SelectedIndex < 0) _layout.SelectedIndex = 0;
        _videoSide.SelectedItem = _settings.ThemeVideoSide; if (_videoSide.SelectedIndex < 0) _videoSide.SelectedIndex = 1;
        _themeMotion.Value = Math.Clamp(_settings.ThemeMotionStrength, 0, 100); _autoCutouts.Checked = _settings.ThemeAutoCutouts;
        RefreshCutoutModelStatus();
    }
    private void ReadSettingsFromControls()
    {
        _settings.RomRoots = _romRoots.Lines.Select(x => x.Trim().Trim('"')).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var (key, input) in _paths) typeof(AppSettings).GetProperty(key)?.SetValue(_settings, input.Text.Trim().Trim('"'));
        _settings.PlatformName = string.IsNullOrWhiteSpace(_platform.Text) ? "TeknoParrot" : _platform.Text.Trim();
        _settings.ArchiveUrl = _archiveUrl.Text.Trim();
        _settings.FillMissingMetadata = _fillMetadata.Checked; _settings.MaxDownloadMbps = (int)_bandwidth.Value;
        _settings.VideoWidth = (int)_videoWidth.Value; _settings.VideoHeight = (int)_videoHeight.Value;
        _settings.VideoFps = (int)_videoFps.Value; _settings.VideoSeconds = (int)_videoSeconds.Value;
        _settings.ThemeLayout = _layout.SelectedItem?.ToString() ?? "Fanart"; _settings.ThemeAudio = _themeAudio.Checked;
        _settings.ThemeVideoSide = _videoSide.SelectedItem?.ToString() ?? "Right";
        _settings.ThemeMotionStrength = (int)_themeMotion.Value; _settings.ThemeAutoCutouts = _autoCutouts.Checked;
    }
    private async void SaveSettings()
    {
        try { ReadSettingsFromControls(); if (!_fixture) SettingsStore.Save(_dataDirectory, _settings); SetStatus("Setup saved. No library files have been changed."); await CheckLaunchBoxSetupAsync(); }
        catch (Exception ex) { ShowDetails("Could not save setup", ex.Message); }
    }
    private void ExportPreset()
    {
        ReadSettingsFromControls(); using var dialog = new SaveFileDialog { Title = "Export a shareable preset", Filter = "JSON preset|*.json", FileName = "arcade-library-preset.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(ExportableSettings(_settings), new JsonSerializerOptions { WriteIndented = true }));
        SetStatus("Preset exported.");
    }
    private void ImportPreset()
    {
        using var dialog = new OpenFileDialog { Title = "Import an Arcade Library Manager preset", Filter = "JSON preset|*.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { var imported = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(dialog.FileName)) ?? throw new InvalidDataException("This file does not contain app settings."); _settings = imported; LoadSettingsIntoControls(); _installPlan.Clear(); _planSettings = ""; BindPlan(); SetStatus("Preset imported. Check your folder locations, then save setup."); }
        catch (Exception ex) { ShowDetails("Could not import preset", ex.Message); }
    }
}





