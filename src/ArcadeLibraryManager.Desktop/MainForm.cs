using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using ArcadeLibraryManager.Core;

namespace ArcadeLibraryManager.Desktop;

public sealed partial class MainForm : Form
{
    private const string DonationUrl = "https://www.paypal.com/paypalme/acbauer12/9.99";
    private AppSettings _settings;
    private readonly string _dataDirectory;
    private readonly bool _fixture;
    private readonly Panel _pageHost = new() { Dock = DockStyle.Fill, Padding = new Padding(30, 22, 30, 20), BackColor = Palette.Page };
    private readonly Dictionary<string, Control> _pages = new();
    private readonly Dictionary<string, Button> _navigation = new();
    private readonly List<Button> _workButtons = new();
    private readonly Label _status = Palette.Label("Ready. Set your locations, then scan the library.", 9.3f, Color.White);
    private readonly Label _headline = new NeonHeading { Text = "Setup", AutoSize = true, Font = Palette.Font(25, FontStyle.Bold), ForeColor = Color.White, Margin = new Padding(0, 0, 0, 8) };
    private readonly Label _subhead = Palette.Label("Connect your library. Review every batch before it runs.", 10, Palette.Muted);
    private readonly Button _cancel = Palette.Button("Cancel job", false, 108);
    private readonly NeonProgressBar _progressBar = new() { Name = "CurrentTaskProgress", Width = 165, Height = 7, Minimum = 0, Maximum = 100, Style = ProgressBarStyle.Continuous, Margin = new Padding(12, 15, 0, 0) };
    private readonly BindingList<LogEntry> _logs = new();
    private readonly IProgress<JobEvent> _progress;
    private CancellationTokenSource? _cancellation;
    private bool _busy;
    private List<GameRecord> _games = new();
    private List<InstallPlanItem> _installPlan = new();
    private string _planSettings = "";
    private List<ComponentUpdate> _componentUpdates = new();
    private string _updatesSettings = "";
    private string _currentPage = "Setup";
    private TextBox _search = null!;
    private ComboBox _libraryFilter = null!;
    private DataGridView _gameGrid = null!, _planGrid = null!, _updatesGrid = null!, _jobsGrid = null!;
    private TextBox _gameDetails = null!;
    private Label _planSummary = null!, _updateSummary = null!;
    private MetricCard _supported = null!, _installed = null!, _attention = null!, _missing = null!;
    private readonly Dictionary<string, bool> _gameSelections = new(StringComparer.OrdinalIgnoreCase);

    public MainForm(AppSettings settings, string dataDirectory, bool fixture = false)
    {
        _settings = settings; _dataDirectory = dataDirectory; _fixture = fixture;
        _progress = new UiProgress(this);
        Text = "Arcade Library Manager";
        var iconName = typeof(MainForm).Assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(".app.ico", StringComparison.Ordinal));
        if (iconName != null) { using var stream = typeof(MainForm).Assembly.GetManifestResourceStream(iconName); if (stream != null) Icon = new Icon(stream); }
        Font = Palette.Font(); BackColor = Palette.Page; ForeColor = Palette.Ink;
        ClientSize = new Size(1270, 845); MinimumSize = new Size(1040, 710); StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;
        BuildShell();
        BuildSetupPage(); BuildLibraryPage(); BuildWorkflowPage(); BuildInstallPage(); BuildLaunchBoxPage(); BuildEmuMoviesPage(); BuildMediaPage(); BuildBezelPage(); BuildJobsPage();
        LoadSettingsIntoControls();
        ShowPage("Setup");
        if (fixture) { PopulateFixture(); PopulateWorkflowFixture(); }
        FormClosing += OnClosing;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeWindowTheme.ApplyBlackCaption(Handle);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { DisposeThemeProgress(); _genreMenu?.Dispose(); }
        base.Dispose(disposing);
    }

    private void BuildShell()
    {
        var sidebar = new Panel { Dock = DockStyle.Left, Width = 220, BackColor = Palette.Navy, Padding = new Padding(20) };
        sidebar.Paint += (_, e) => {
            using var glow = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, sidebar.Width, Math.Max(1, sidebar.Height)), Palette.Accent, Palette.Cyan, 90f);
            e.Graphics.FillRectangle(glow, sidebar.Width - 2, 0, 2, sidebar.Height);
        };
        var brand = new BrandMark { Location = new Point(25, 27) };
        var name = new NeonHeading { Text = "ARCADE\nLIBRARY MANAGER", AutoSize = true, Font = Palette.Font(11, FontStyle.Bold), ForeColor = Color.White }; name.Location = new Point(26, 89);
        sidebar.Controls.AddRange([brand, name]);
        var navLabels = new[] { ("Setup", "Setup"), ("Library", "Library"), ("Maintenance", "Maintenance"), ("Install games", "Install games"), ("LaunchBox", "LaunchBox"), (EmuMoviesPageName, EmuMoviesPageName), ("Make themes", "Make themes"), ("Bezels", "Bezels"), ("Jobs", "Jobs & history") };
        var y = 146;
        foreach (var (key, label) in navLabels)
        {
            var button = new Button { Text = label, Location = new Point(12, y), Size = new Size(196, key == EmuMoviesPageName ? 45 : 43), FlatStyle = FlatStyle.Flat,
                ForeColor = Palette.Muted, BackColor = Palette.Navy, TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(12, 0, 0, 0), Font = Palette.Font(key == EmuMoviesPageName ? 9.3f : 10, FontStyle.Bold), Cursor = Cursors.Hand, TabStop = true };
            button.FlatAppearance.BorderSize = 0; button.FlatAppearance.MouseOverBackColor = Palette.Hover;
            button.Paint += (_, e) => {
                if (_currentPage != key) return;
                using var glow = new Pen(Color.FromArgb(105, Palette.Cyan)); e.Graphics.DrawRectangle(glow, 1, 1, button.Width - 3, button.Height - 3);
                using var marker = new SolidBrush(Palette.Cyan); e.Graphics.FillRectangle(marker, 0, 0, 3, button.Height);
            };
            button.Click += (_, _) => ShowPage(key); _navigation[key] = button; sidebar.Controls.Add(button); y += key == EmuMoviesPageName ? 47 : 45;
        }
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 90 };
        var donate = Palette.Button("Buy me a coffee", false, 180);
        donate.Name = "DonationButton"; donate.Tag = DonationUrl; donate.Location = new Point(0, 0); donate.Height = 30;
        donate.AccessibleDescription = "Open the developer's PayPal donation page in your browser.";
        _fieldTips.SetToolTip(donate, "Opens PayPal in your browser with 9.99 prefilled. Review the amount and currency there.");
        donate.Click += (_, _) => OpenDonationPage();
        var portable = Palette.Label("PORTABLE / WINDOWS", 8, Palette.Muted, true); portable.Location = new Point(6, 38);
        var version = Palette.Label("Preview 0.2.20  ·  About this app", 8, Palette.Muted); version.Location = new Point(6, 58); version.Cursor = Cursors.Hand;
        version.Click += (_, _) => ShowDetails("About Arcade Library Manager", "Arcade Library Manager 0.2.20 — Portable Windows preview\r\n\r\nInstall TeknoParrot games, synchronize your LaunchBox platform, make missing theme videos, and set up bezels.\r\n\r\nUse Emumovies snap scrapper for gameplay videos and LaunchBox for artwork, then use Make themes with those local files. New themes default to 30 seconds, adjustable from 25 to 33 seconds.\r\n\r\nMaintenance combines your selected install, LaunchBox, theme, and bezel steps. Keep the app open while work runs. Use Resume unfinished to review an interrupted batch, or Jobs for standalone installs.\r\n\r\nYour settings stay with the portable app. Controls and actual play-testing are up to you.");
        footer.Controls.AddRange([donate, portable, version]); sidebar.Controls.Add(footer);

        var right = new Panel { Dock = DockStyle.Fill, BackColor = Palette.Page };
        var header = new TableLayoutPanel { Dock = DockStyle.Top, Height = 115, Padding = new Padding(30, 20, 30, 0), ColumnCount = 2, RowCount = 1, BackColor = Color.Black };
        header.Paint += (_, e) => {
            using var line = new System.Drawing.Drawing2D.LinearGradientBrush(header.ClientRectangle, Palette.Accent, Palette.Cyan, 0f); e.Graphics.FillRectangle(line, 0, header.Height - 2, header.Width, 2);
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 307));
        var headings = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent };
        headings.Controls.Add(_headline); headings.Controls.Add(_subhead); header.Controls.Add(headings, 0, 0);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 12, 0, 0), WrapContents = false, BackColor = Color.Transparent };
        var save = WorkButton("Save setup", (_, _) => SaveSettings(), false, 125);
        var scan = WorkButton("Scan library", async (_, _) => await ScanLibraryAsync(), true, 158);
        actions.Controls.AddRange([save, scan]); header.Controls.Add(actions, 1, 0);
        var statusPanel = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 48, BackColor = Palette.Navy, ColumnCount = 3, Padding = new Padding(20, 5, 15, 0) };
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 122)); statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        _status.AutoSize = false; _status.Dock = DockStyle.Fill; _status.TextAlign = ContentAlignment.MiddleLeft; _status.AutoEllipsis = true;
        _cancel.Height = 32; _cancel.Enabled = false; _cancel.Click += (_, _) => { _cancellation?.Cancel(); SetStatus("Cancellation requested. Finishing the current safe checkpoint…"); _cancel.Enabled = false; };
        statusPanel.Controls.Add(_status, 0, 0); statusPanel.Controls.Add(_cancel, 1, 0); statusPanel.Controls.Add(_progressBar, 2, 0);
        right.Controls.Add(_pageHost); right.Controls.Add(header); right.Controls.Add(statusPanel);
        Controls.Add(right); Controls.Add(sidebar);
    }

    internal void ShowMediaTab(int index) { ShowPage("Make themes"); _pages["Make themes"].Controls.OfType<TabControl>().First().SelectedIndex = index; }
    private void OpenDonationPage()
    {
        if (_fixture) { SetStatus("The PayPal donation link is disabled in the UI fixture."); return; }
        try { Process.Start(new ProcessStartInfo(DonationUrl) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { SetStatus("Could not open the PayPal donation page: " + ex.Message); }
    }
    private Button WorkButton(string text, EventHandler click, bool primary = false, int width = 140)
    {
        var button = Palette.Button(text, primary, width); button.Click += click; _workButtons.Add(button); return button;
    }
    private FlowLayoutPanel Toolbar(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, WrapContents = false, AutoScroll = true, Margin = new Padding(0) };
        panel.Controls.AddRange(controls); return panel;
    }
    private Panel Page(string name)
    {
        var panel = new Panel { Dock = DockStyle.Fill, Visible = false, BackColor = Palette.Page };
        _pages.Add(name, panel); _pageHost.Controls.Add(panel); return panel;
    }
    private static Label Hint(string text, int height = 45) => new() { Text = text, Dock = DockStyle.Top, Height = height, Font = Palette.Font(9), ForeColor = Palette.Muted, Padding = new Padding(0, 2, 0, 9) };
    internal void ShowPage(string name)
    {
        _currentPage = name;
        foreach (var (key, page) in _pages) page.Visible = key == name;
        if (_pages.TryGetValue(name, out var current)) current.BringToFront();
        foreach (var (key, button) in _navigation) { button.BackColor = key == name ? Palette.Selection : Palette.Navy; button.ForeColor = key == name ? Palette.Cyan : Palette.Muted; button.Invalidate(); }
        _headline.Text = name == "Jobs" ? "Jobs & history" : name == EmuMoviesPageName ? "Emumovies snaps" : name;
        _subhead.Text = name switch {
            "Setup" => "Connect your library. Review every batch before it runs.",
            "Library" => "Every supported game, with a clear view of what is ready.",
            "Maintenance" => "Choose the stages. Review once. Follow each game to completion.",
            "Install games" => "Local files first. Verified downloads where needed.",
            "LaunchBox" => "One existing platform. Favorites, history and hooks preserved.",
            EmuMoviesPageName => "Download missing gameplay videos with official EmuMovies Sync.",
            "Make themes" => "Create missing themes from your local artwork and gameplay snaps.",
            "Bezels" => "Find local bezel packs and review each game’s setup.",
            _ => "Follow the work, inspect results and resume with confidence." };
        if (name == "Jobs") RefreshJobHistory();
        if (name == "Maintenance") UpdateWorkflowSelectionSummary();
    }

    private async Task RunWork(string status, Func<CancellationToken, Task> action)
    {
        if (_busy) return;
        _busy = true; _cancellation = new CancellationTokenSource();
        foreach (var button in _workButtons) button.Enabled = false;
        _cancel.Enabled = true; _progressBar.Style = ProgressBarStyle.Marquee; SetStatus(status);
        try { await action(_cancellation.Token); }
        catch (OperationCanceledException) { SetStatus("Stopped at a safe checkpoint. A new scan and plan can resume unfinished work."); AppendProgress(new JobEvent("Cancelled", "The current batch was cancelled by the user.")); }
        catch (Exception ex) { SetStatus(ex.Message); AppendProgress(new JobEvent("Error", ex.Message)); ShowDetails("The operation needs attention", ex.Message); }
        finally {
            _busy = false; _cancellation.Dispose(); _cancellation = null; _cancel.Enabled = false;
            _progressBar.Style = ProgressBarStyle.Continuous; _progressBar.Value = 0;
            foreach (var button in _workButtons) button.Enabled = true;
            RefreshThemeProgressView();
            RefreshJobHistory();
        }
    }
    private AppEngine Engine() => new(_settings, _dataDirectory, _progress);
    private void SetStatus(string text) => _status.Text = text;
    private void AppendProgress(JobEvent item)
    {
        AppendWorkflowProgress(item);
        _logs.Insert(0, new LogEntry(DateTime.Now.ToString("HH:mm:ss"), item.Stage, item.GameId, item.Message));
        if (_logs.Count > 1500) _logs.RemoveAt(_logs.Count - 1);
        SetStatus(item.Message);
        if (item.Percent.HasValue) { _progressBar.Style = ProgressBarStyle.Continuous; _progressBar.Value = (int)Math.Clamp(item.Percent.Value, 0, 100); }
        else if (_busy) _progressBar.Style = ProgressBarStyle.Marquee;
    }
    private void ShowResult(string name, OperationResult result)
    {
        var text = $"{name}: {result.Succeeded:N0} completed, {result.Skipped:N0} skipped, {result.Errors.Count:N0} need attention.";
        SetStatus(text); AppendProgress(new JobEvent(result.Errors.Count == 0 ? "Completed" : "Attention", text));
        if (result.Errors.Count > 0) ShowDetails(name, text + "\r\n\r\n" + string.Join("\r\n", result.Errors));
    }
    private static void ShowDetails(string title, string content)
    {
        using var dialog = new Form { Text = title, Size = new Size(740, 420), StartPosition = FormStartPosition.CenterParent, BackColor = Palette.Page, Font = Palette.Font(), MinimizeBox = false, MaximizeBox = false };
        var box = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = true, Text = content, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Palette.Surface, ForeColor = Palette.Ink, Font = Palette.Font(), Margin = new Padding(20) };
        var outer = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20) }; outer.Controls.Add(box); dialog.Controls.Add(outer);
        dialog.ShowDialog();
    }
    private static void OpenLocal(string path)
    {
        if (File.Exists(path) || Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else throw new FileNotFoundException("The file or folder does not exist yet.", path);
    }
    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_busy) return;
        if (MessageBox.Show(this, "A batch is running. Cancel it at the next safe checkpoint and keep this window open until it stops?", "Work is still running", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) {
            _cancellation?.Cancel(); _cancel.Enabled = false; SetStatus("Stopping safely. Close the app after the job has stopped.");
        }
        e.Cancel = true;
    }
    public static AppSettings ExportableSettings(AppSettings source)
    {
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(source))!;
        if (Uri.TryCreate(copy.ArchiveUrl, UriKind.Absolute, out var archive)) copy.ArchiveUrl = new UriBuilder(archive) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.AbsoluteUri;
        return copy;
    }
    private sealed class UiProgress(MainForm form) : IProgress<JobEvent>
    {
        public void Report(JobEvent value)
        {
            if (form.IsDisposed || form.Disposing) return;
            if (form.InvokeRequired) { try { form.BeginInvoke(() => form.AppendProgress(value)); } catch (InvalidOperationException) { } }
            else form.AppendProgress(value);
        }
    }
    private sealed record LogEntry(string Time, string Stage, string Profile, string Message);
}







