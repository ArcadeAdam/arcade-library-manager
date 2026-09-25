using System.Text.Json;
using ArcadeLibraryManager.Core;

namespace ArcadeLibraryManager.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--self-test")) return SelfTest();
        if (args.Contains("--smoke-test")) return SmokeTest(args);
        var dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
        var dataArg = Array.IndexOf(args, "--data-dir");
        if (dataArg >= 0 && dataArg + 1 < args.Length) dataDirectory = Path.GetFullPath(args[dataArg + 1]);
        try
        {
            Directory.CreateDirectory(dataDirectory);
            var settings = SettingsStore.Load(dataDirectory);
            Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message, "Arcade Library Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Application.Run(new MainForm(settings, dataDirectory));
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message + "\n\nUnzip the app into a folder your Windows account can write to.", "Unable to start Arcade Library Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static int SelfTest()
    {
        var temporary = Path.Combine(Path.GetTempPath(), "ArcadeLibraryManager-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temporary);
            var expected = new AppSettings { TeknoParrotPath = "X:\\Fixture\\TeknoParrot", RomRoots = new() { "X:\\Fixture\\ROMs", "Y:\\Second source" }, VideoWidth = 1920, VideoSeconds = 31 };
            SettingsStore.Save(temporary, expected);
            var actual = SettingsStore.Load(temporary);
            if (JsonSerializer.Serialize(expected) != JsonSerializer.Serialize(actual)) throw new Exception("Settings persistence mismatch.");
            var nativeDatabase = new JobStore(temporary);
            if (nativeDatabase.List().Count != 0) throw new Exception("Portable SQLite database check failed.");
            var legacy = JsonSerializer.Deserialize<AppSettings>("{\"VideoSeconds\":30,\"EmuMoviesUsername\":\"old-account\",\"QuietMode\":true}")!;
            SettingsStore.Save(temporary, legacy);
            if (File.ReadAllText(Path.Combine(temporary, "settings.json")).Contains("EmuMovies", StringComparison.OrdinalIgnoreCase) || File.ReadAllText(Path.Combine(temporary, "settings.json")).Contains("QuietMode")) throw new Exception("Retired account settings were saved.");
            expected.ArchiveUrl = "https://user:secret@example.com/archive?token=private#fragment";
            var safe = MainForm.ExportableSettings(expected);
            if (safe.ArchiveUrl != "https://example.com/archive") throw new Exception("Shareable settings retained URL credentials.");
            File.WriteAllText(Path.Combine(temporary, "PASS.txt"), "Settings round trip, legacy account removal, native SQLite database, and safe preset export passed.");
            return 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(Path.GetTempPath(), "ArcadeLibraryManager-self-test-error.txt"), ex.ToString()); return 1; }
    }

    private static int SmokeTest(string[] args)
    {
        var temporary = Path.Combine(Path.GetTempPath(), "ArcadeLibraryManager-ui-" + Guid.NewGuid().ToString("N"));
        var index = Array.IndexOf(args, "--smoke-test");
        var output = index + 1 < args.Length && !args[index + 1].StartsWith("--") ? Path.GetFullPath(args[index + 1]) : Path.Combine(temporary, "desktop-smoke.png");
        try
        {
            Directory.CreateDirectory(temporary);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using var form = new MainForm(new AppSettings { TeknoParrotPath = @"X:\Fixture\TeknoParrot", BezelImportPath = @"N:\Emulators\Bezel Repository" }, temporary, fixture: true);
            static IEnumerable<Control> Descendants(Control parent) {
                foreach (Control child in parent.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; }
            }
            var controls = Descendants(form).ToList();
            var removedLabels = new[] { "Fill selected artwork", "Refresh metadata", "Import Metadata.zip", "Media providers", "Prepare selected media queue", "Forget password", "Save securely", "Quiet mode" };
            if (controls.Any(c => removedLabels.Any(label => c.Text.Contains(label, StringComparison.OrdinalIgnoreCase)))) throw new Exception("A removed provider control remains in the desktop UI.");
            foreach (var required in new[] { "Make themes", "Bezels", "Generate selected themes", "Render preview", "Install & enable selected", "Emumovies snap scrapper", "Preview import", "Download & import missing videos", "Import downloaded videos", "Open Sync / login", "Download EmuMovies Sync", "Open reports" })
                if (!controls.OfType<Button>().Any(c => c.Text == required)) throw new Exception("A required action is missing: " + required);
            if (controls.OfType<TabPage>().Count(t => t.Text is "Games" or "Theme settings") != 2) throw new Exception("The theme page must contain Games and Theme settings.");
            var composition = controls.OfType<ComboBox>().Single(c => c.Items.Contains("Fanart"));
            if (composition.Items.Count != 2 || composition.Items.Contains("Artwork")) throw new Exception("An artwork-only theme composition remains available.");
            form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-32000, -32000); form.Opacity = 0; form.Show(); Application.DoEvents();
            form.PerformLayout();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
            bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Png);
            foreach (var page in new[] { "Setup", "Maintenance", "Install games", "LaunchBox", "Emumovies snap scrapper", "Make themes", "Bezels", "Jobs" }) {
                form.ShowPage(page); form.PerformLayout(); Application.DoEvents();
                using var pageBitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(pageBitmap, new Rectangle(0, 0, pageBitmap.Width, pageBitmap.Height));
                pageBitmap.Save(Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + "-" + page.Replace(" ", "-") + ".png"), System.Drawing.Imaging.ImageFormat.Png);
            }
                        foreach (var pair in new[] { (1, "Theme-settings") }) {
                form.ShowMediaTab(pair.Item1); form.PerformLayout(); Application.DoEvents();
                using var tabBitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(tabBitmap, new Rectangle(0, 0, tabBitmap.Width, tabBitmap.Height));
                tabBitmap.Save(Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + "-" + pair.Item2 + ".png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            T Named<T>(string name) where T : Control => Descendants(form).OfType<T>().Single(c => c.Name == name);
            form.ShowPage("Setup");
            form.ShowLaunchBoxSetupFixture("Needs repair");
            var launchStatus = Named<Label>("LaunchBoxSetupStatus");
            var launchDetails = Named<TextBox>("LaunchBoxSetupDetails");
            var launchReview = Named<Button>("LaunchBoxSetupReview");
            if (launchStatus.Text != "Needs repair" || !launchReview.Enabled
                || !launchDetails.Text.Contains(@"X:\Fixture\LaunchBox\Data\Emulators.xml")
                || !launchDetails.Text.Contains("Effective command: --profile=%ROMNAME%")
                || !launchDetails.Text.Contains("Proposed command: --profile=%romfile%.xml")) throw new Exception("Launch setup must show the inspected source, effective command, and available repair.");
            using (var launchRepair = form.CreateLaunchBoxSetupReviewFixture()) {
                launchRepair.ShowInTaskbar = false; launchRepair.StartPosition = FormStartPosition.Manual; launchRepair.Location = new Point(-32000, -32000); launchRepair.Opacity = 0;
                launchRepair.Show(); launchRepair.PerformLayout(); Application.DoEvents();
                var reviewControls = Descendants(launchRepair).ToList();
                var launchChanges = reviewControls.OfType<DataGridView>().Single(c => c.Name == "LaunchBoxSetupChanges");
                if (launchChanges.Rows.Count != 1 || launchChanges.Rows[0].Cells[1].Value?.ToString() != "--profile=%ROMNAME%"
                    || launchChanges.Rows[0].Cells[2].Value?.ToString() != "--profile=%romfile%.xml") throw new Exception("Launch repair review must show concrete before and after values.");
                var backupNotice = reviewControls.Single(c => c.Name == "LaunchBoxSetupBackupNotice").Text;
                if (!backupNotice.Contains(@"LaunchBox\Backups\ArcadeLibraryManager") || !backupNotice.Contains("must be closed")) throw new Exception("Launch repair must explain the backup and closed-LaunchBox requirement before applying.");
                if (reviewControls.Single(c => c.Name == "LaunchBoxSetupApply").Enabled) throw new Exception("The isolated launch setup fixture must never allow applying to a live library.");
                launchRepair.Close();
            }
            var launchEmulatorFolder = Named<TextBox>("SetupTeknoParrotPath");
            var originalEmulatorFolder = launchEmulatorFolder.Text;
            launchEmulatorFolder.Text = @"X:\Different\TeknoParrot";
            if (launchReview.Enabled || !launchStatus.Text.Contains("Setup changed")) throw new Exception("Changing the configured emulator path must invalidate the launch repair preview.");
            launchEmulatorFolder.Text = originalEmulatorFolder;
            form.ShowLaunchBoxSetupFixture("Needs review");
            if (launchReview.Enabled || !launchDetails.Text.Contains("Several emulator entries match")) throw new Exception("An ambiguous emulator must expose its reason and must not offer a repair.");
            form.ShowLaunchBoxSetupFixture("Ready");
            if (launchReview.Enabled || launchStatus.Text != "Ready") throw new Exception("A ready launch setup must not offer a repair.");
            form.ShowLaunchBoxSetupFixture("Needs repair");
            var setupScroll = Named<Panel>("SetupScroll");
            foreach (var small in new[] { false, true }) {
                if (small) form.Size = new Size(1040, 710); else form.ClientSize = new Size(1270, 845);
                form.PerformLayout(); Application.DoEvents(); setupScroll.ScrollControlIntoView(Named<TableLayoutPanel>("LaunchBoxSetupCard"));
                form.PerformLayout(); Application.DoEvents();
                if (setupScroll.HorizontalScroll.Visible) throw new Exception("Launch setup causes horizontal scrolling at the supported window size.");
                var launchActions = Named<FlowLayoutPanel>("LaunchBoxSetupActions");
                foreach (Control action in launchActions.Controls)
                    if (!launchActions.ClientRectangle.Contains(action.Bounds)) throw new Exception("A launch setup action is clipped.");
                using var launchSnapshot = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(launchSnapshot, new Rectangle(0, 0, launchSnapshot.Width, launchSnapshot.Height));
                launchSnapshot.Save(Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + "-LaunchBox-setup" + (small ? "-small" : "") + ".png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            setupScroll.AutoScrollPosition = Point.Empty; form.ClientSize = new Size(1270, 845);
            form.ShowPage("Library"); form.PerformLayout(); Application.DoEvents();
            var librarySearch = Named<TextBox>("LibrarySearch");
            var libraryStatus = Named<ComboBox>("LibraryStatusFilter");
            var libraryGenre = Named<Button>("LibraryGenre");
            var libraryGrid = Named<DataGridView>("LibraryGameGrid");
            var selectMissingShown = Named<Button>("LibrarySelectMissing");
            var selectAllShown = Named<Button>("LibrarySelectShown");
            var clearLibrarySelection = Named<Button>("LibraryClearSelection");
            if (selectMissingShown.Text != "Select missing shown" || selectAllShown.Text != "Select all shown" || !libraryGenre.Text.StartsWith("Genre: All", StringComparison.Ordinal)) throw new Exception("Library filters or selection actions are mislabeled.");
            string[] VisibleLibraryIds() {
                var column = libraryGrid.Columns.Cast<DataGridViewColumn>().Single(c => c.DataPropertyName == "Id");
                return libraryGrid.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow).Select(r => r.Cells[column.Index].Value?.ToString() ?? "").ToArray();
            }
            void RequireLibraryIds(IEnumerable<string> actual, IEnumerable<string> expected, string message) {
                if (!actual.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(expected)) throw new Exception(message);
            }
            void ChooseLibraryGenre(string genre) {
                libraryGenre.PerformClick(); Application.DoEvents();
                var menu = libraryGenre.ContextMenuStrip ?? throw new Exception("The Genre button did not expose its context menu.");
                var choices = menu.Items.OfType<ToolStripMenuItem>().ToArray();
                var genreNames = choices.Select(item => item.Tag?.ToString() ?? "").Where(name => name is not ("All genres" or "Unspecified")).ToArray();
                if (!genreNames.SequenceEqual(genreNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))) throw new Exception("Genre menu choices are not alphabetical.");
                var choice = choices.Single(item => string.Equals(item.Tag?.ToString(), genre == "All" ? "All genres" : genre, StringComparison.OrdinalIgnoreCase));
                if (!(choice.Text ?? "").Any(char.IsDigit)) throw new Exception("Genre choices must show their game counts.");
                choice.PerformClick(); menu.Close(); Application.DoEvents();
            }
            void ClearLibraryFilters() { librarySearch.Clear(); libraryStatus.SelectedItem = "All games"; ChooseLibraryGenre("All"); Application.DoEvents(); }
            void SaveLibrarySnapshot(string suffix) {
                form.PerformLayout(); Application.DoEvents();
                using var snapshot = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(snapshot, new Rectangle(0, 0, snapshot.Width, snapshot.Height));
                snapshot.Save(Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + "-Library-" + suffix + ".png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            var allLibraryIds = VisibleLibraryIds();
            if (allLibraryIds.Length != 11) throw new Exception("The Library genre fixture must contain eleven games.");
            var initialLibrarySelection = form.SelectedLibraryGameIds();
            if (!initialLibrarySelection.Contains("DenshaDeGo") || !initialLibrarySelection.Contains("taikogreen") || !initialLibrarySelection.Contains("SegaRacingClassic")) throw new Exception("The genre fixture must begin with checked missing games across multiple genres.");
            ChooseLibraryGenre("Racing");
            var racingIds = VisibleLibraryIds();
            if (racingIds.Length != 6 || !racingIds.Contains("Daytona3") || !racingIds.Contains("SegaRacingClassic") || racingIds.Contains("DenshaDeGo") || racingIds.Contains("taikogreen") || racingIds.Contains("StreetFighterVTypeArcade")) throw new Exception("Racing genre filtering included unrelated games or excluded recognized racer aliases.");
            RequireLibraryIds(form.SelectedLibraryGameIds(), ["SegaRacingClassic"], "Checked games hidden by the genre filter entered the selected plan scope.");
            libraryStatus.SelectedItem = "Missing";
            RequireLibraryIds(VisibleLibraryIds(), ["SegaRacingClassic"], "Missing status and Racing genre did not intersect.");
            selectMissingShown.PerformClick();
            RequireLibraryIds(form.SelectedLibraryGameIds(), ["SegaRacingClassic"], "Select missing shown did not select the filtered missing racer.");
            ClearLibraryFilters();
            RequireLibraryIds(form.SelectedLibraryGameIds(), ["SegaRacingClassic"], "Select missing shown retained hidden selections outside its scope.");
            form.SelectAllLibraryCandidates(); ChooseLibraryGenre("Racing"); libraryStatus.SelectedItem = "Missing";
            clearLibrarySelection.PerformClick();
            RequireLibraryIds(form.SelectedLibraryGameIds(), [], "Clear selection left a visible game checked.");
            ClearLibraryFilters();
            RequireLibraryIds(form.SelectedLibraryGameIds(), [], "Clear selection left hidden games checked.");
            form.SelectAllLibraryCandidates(); ChooseLibraryGenre("Racing"); libraryStatus.SelectedItem = "Missing";
            selectAllShown.PerformClick(); ClearLibraryFilters();
            RequireLibraryIds(form.SelectedLibraryGameIds(), ["SegaRacingClassic"], "Select all shown retained hidden selections outside its scope.");
            ChooseLibraryGenre("Racing"); selectAllShown.PerformClick();
            RequireLibraryIds(form.SelectedLibraryGameIds(), racingIds, "Select all shown did not select all visible racing games.");
            librarySearch.Text = "Daytona"; libraryStatus.SelectedItem = "Missing";
            RequireLibraryIds(VisibleLibraryIds(), [], "Search, status and genre must combine rather than replace one another.");
            RequireLibraryIds(form.SelectedLibraryGameIds(), [], "Selections hidden by search or status leaked into the selected plan scope.");
            libraryStatus.SelectedItem = "Registered";
            RequireLibraryIds(VisibleLibraryIds(), ["Daytona3"], "Search and Registered status did not find the matching racing game.");
            RequireLibraryIds(form.SelectedLibraryGameIds(), ["Daytona3"], "The selected plan scope ignored the active search.");
            libraryStatus.SelectedItem = "All games"; librarySearch.Text = "Densha";
            RequireLibraryIds(VisibleLibraryIds(), [], "A search match bypassed the Racing genre filter.");
            ChooseLibraryGenre("All");
            RequireLibraryIds(VisibleLibraryIds(), ["DenshaDeGo"], "Clearing genre unexpectedly cleared the active search.");
            librarySearch.Clear(); libraryStatus.SelectedItem = "Missing"; ChooseLibraryGenre("Unspecified");
            RequireLibraryIds(VisibleLibraryIds(), ["taikogreen"], "Unspecified genre did not show the missing game without genre metadata.");
            ChooseLibraryGenre("Racing"); librarySearch.Text = "Sega";
            form.SelectAllLibraryCandidates(); Application.DoEvents();
            RequireLibraryIds(VisibleLibraryIds(), allLibraryIds, "Maintenance all candidates did not clear the Library filters.");
            RequireLibraryIds(form.SelectedLibraryGameIds(), allLibraryIds, "Maintenance all candidates failed to select the entire catalog.");
            if (librarySearch.Text.Length != 0 || libraryStatus.SelectedItem?.ToString() != "All games" || !libraryGenre.Text.StartsWith("Genre: All", StringComparison.Ordinal)) throw new Exception("Maintenance all candidates left an active filter behind.");
            ChooseLibraryGenre("Racing"); selectAllShown.PerformClick(); SaveLibrarySnapshot("racing");
            form.Size = new Size(1040, 710); form.PerformLayout(); Application.DoEvents();
            if (form.Width != 1040 || form.Height != 710) throw new Exception("The Library minimum-size fixture must use a 1040 by 710 window.");
            foreach (var control in new Control[] { librarySearch, libraryStatus, libraryGenre, selectMissingShown, selectAllShown, clearLibrarySelection }) {
                if (!control.Visible || control.Width <= 0 || control.Height <= 0 || control.Parent == null || !control.Parent.ClientRectangle.Contains(control.Bounds)) throw new Exception("A Library toolbar control is clipped at minimum window size: " + control.Name);
                for (Control? ancestor = control.Parent; ancestor != null && ancestor != form; ancestor = ancestor.Parent) {
                    if (!ancestor.ClientRectangle.Contains(ancestor.RectangleToClient(control.RectangleToScreen(control.ClientRectangle)))) throw new Exception("The Library toolbar exceeds its visible page at minimum window size: " + control.Name);
                    if (ancestor is ScrollableControl scroll && scroll.HorizontalScroll.Visible) throw new Exception("The Library toolbar requires horizontal scrolling at minimum window size.");
                }
            }
            if (Descendants(libraryGrid).OfType<HScrollBar>().Any(scroll => scroll.Visible)) throw new Exception("The Library grid requires horizontal scrolling at minimum window size.");
            SaveLibrarySnapshot("racing-small");
            form.ClientSize = new Size(1270, 845); ClearLibraryFilters(); selectMissingShown.PerformClick();
            RequireLibraryIds(form.SelectedLibraryGameIds(), initialLibrarySelection, "The Library smoke checks did not restore the original missing-game selection.");
            form.ShowPage("Bezels"); form.PerformLayout(); Application.DoEvents();
            var repositoryInput = Named<TextBox>("BezelRepositoryPath");
            if (!repositoryInput.Visible || repositoryInput.Text != @"N:\Emulators\Bezel Repository" || !Named<Button>("BezelRepositoryBrowse").Visible) throw new Exception("The bezel page did not expose the saved local repository and folder picker.");
            form.Size = form.MinimumSize; form.PerformLayout(); Application.DoEvents();
            var repositoryCard = Named<TableLayoutPanel>("BezelRepositoryCard");
            if (repositoryInput.Width < 300 || repositoryInput.Right > repositoryCard.ClientSize.Width || Named<Button>("BezelRepositoryBrowse").Right > repositoryCard.ClientSize.Width) throw new Exception("The repository picker is clipped at minimum window size.");
            using (var bezelSnapshot = new Bitmap(form.Width, form.Height)) {
                form.DrawToBitmap(bezelSnapshot, new Rectangle(0, 0, bezelSnapshot.Width, bezelSnapshot.Height));
                bezelSnapshot.Save(Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + "-Bezels-small.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            form.ClientSize = new Size(1270, 845);
            form.ShowPage("Emumovies snap scrapper"); form.PerformLayout(); Application.DoEvents();
            var emuPage = Named<Panel>("EmuMoviesPage");
            var emuControls = Descendants(emuPage).ToList();
            if (!emuPage.Visible) throw new Exception("The EmuMovies page did not open from navigation.");
            var emuNav = controls.OfType<Button>().Single(c => c.Text == "Emumovies snap scrapper");
            var sidebarButtons = emuNav.Parent!.Controls.OfType<Button>().OrderBy(c => c.Top).ToList();
            var emuNavIndex = sidebarButtons.IndexOf(emuNav);
            if (emuNavIndex < 0 || emuNavIndex + 1 >= sidebarButtons.Count || sidebarButtons[emuNavIndex + 1].Text != "Make themes") throw new Exception("Emumovies snap scrapper must appear immediately above Make themes.");
            var emuPlatform = Named<ComboBox>("EmuMoviesPlatform");
            if (emuPlatform.DropDownStyle != ComboBoxStyle.DropDownList || emuPlatform.Items.Count != 2 || emuPlatform.SelectedItem is not EmuMoviesPlatformChoice) throw new Exception("The EmuMovies platform selector did not load fixture platforms.");
            var emuMatch = Named<TextBox>("EmuMoviesMatchFolder");
            var teknoParrotChoice = emuPlatform.Items.Cast<EmuMoviesPlatformChoice>().Single(c => c.Name == "TeknoParrot");
            if (emuMatch.Text != @"X:\Fixture\TeknoParrot\UserProfiles" || emuMatch.Text == teknoParrotChoice.MatchFolder) throw new Exception("An unsaved TeknoParrot match folder must prefer the configured emulator's UserProfiles over LaunchBox ROM folders.");
            if (MainForm.ResolveEmuMoviesMatchFolder(teknoParrotChoice, @"X:\Fixture\TeknoParrot", @"Y:\Custom match folder") != @"Y:\Custom match folder"
                || MainForm.ResolveEmuMoviesMatchFolder(teknoParrotChoice, @"X:\Fixture\TeknoParrot", "") != "") throw new Exception("A saved match folder, including an intentionally empty value, must be retained.");
            if (MainForm.ResolveEmuMoviesMatchFolder(teknoParrotChoice, "", null) != teknoParrotChoice.MatchFolder) throw new Exception("An unconfigured emulator path must retain the platform's existing folder suggestion.");
            if (Named<ComboBox>("EmuMoviesCatalog").DropDownStyle != ComboBoxStyle.DropDown) throw new Exception("The EmuMovies catalog must allow a catalog ID to be entered.");
            var emuCatalog = Named<ComboBox>("EmuMoviesCatalog");
            emuCatalog.SelectedItem = emuCatalog.Items.Cast<object>().Single(c => c.ToString() == "Sega Saturn");
            if (form.SelectedEmuMoviesCatalog() != "Sega Saturn") throw new Exception("Selected catalogs must pass their display name to official Sync.");
            emuCatalog.SelectedIndex = -1; emuCatalog.Text = "Sega_Saturn";
            if (form.SelectedEmuMoviesCatalog() != "Sega Saturn") throw new Exception("Typed or saved catalog IDs must resolve to the official Sync display name.");
            emuCatalog.SelectedIndex = -1; emuCatalog.Text = "Sega_Model_2";
            if (form.SelectedEmuMoviesCatalog() != "Sega Model 2") throw new Exception("A Model 2 catalog ID bypassed hardware catalog routing.");
            foreach (var field in new[] { "EmuMoviesMatchFolder", "EmuMoviesWorkDirectory", "EmuMoviesSyncExecutable" })
                if (!emuControls.Contains(Named<TextBox>(field))) throw new Exception("An EmuMovies folder/app selector is missing: " + field);
            foreach (var action in new[] { ("EmuMoviesPreviewImport", "Preview import"), ("EmuMoviesDownloadAndImport", "Download & import missing videos"), ("EmuMoviesImportDownloaded", "Import downloaded videos"), ("EmuMoviesOpenSync", "Open Sync / login"), ("EmuMoviesDownloadSync", "Download EmuMovies Sync"), ("EmuMoviesOpenReports", "Open reports") })
                if (!emuControls.Contains(Named<Button>(action.Item1)) || Named<Button>(action.Item1).Text != action.Item2) throw new Exception("An EmuMovies action is missing or mislabeled: " + action.Item2);
            if (Named<Button>("EmuMoviesDownloadSync").Tag as string != "https://emumovies.com/files/file/321-emumovies-sync/") throw new Exception("The Sync download action must point to the official EmuMovies page.");
            var mediaScope = Named<Label>("EmuMoviesMediaScope").Text;
            if (!mediaScope.Contains("MP4 only", StringComparison.Ordinal) || !mediaScope.Contains("No artwork downloads", StringComparison.Ordinal)) throw new Exception("The EmuMovies page must clearly state its video-only scope.");
            if (emuControls.OfType<CheckBox>().Any(c => c.Name != "EmuMoviesMameFallback") || emuControls.OfType<TextBox>().Any(c => c.UseSystemPasswordChar || c.PasswordChar != '\0' || c.Name.Contains("password", StringComparison.OrdinalIgnoreCase) || c.Name.Contains("username", StringComparison.OrdinalIgnoreCase))) throw new Exception("The video-only page must not expose artwork options or account credential fields.");
            var emuFallback = Named<CheckBox>("EmuMoviesMameFallback");
            emuPlatform.SelectedItem = emuPlatform.Items.Cast<EmuMoviesPlatformChoice>().Single(c => c.Name == "Nintendo Entertainment System");
            if (emuFallback.Enabled || emuFallback.Checked) throw new Exception("MAME fallback must be disabled outside TeknoParrot.");
            if (emuMatch.Text != @"D:\Games\NES") throw new Exception("The TeknoParrot match folder default changed another platform's ROM folder.");
            emuPlatform.SelectedItem = emuPlatform.Items.Cast<EmuMoviesPlatformChoice>().Single(c => c.Name == "TeknoParrot");
            if (!emuFallback.Enabled || !emuFallback.Checked) throw new Exception("TeknoParrot must offer its MAME fallback.");
            form.Size = new Size(1040, 710); form.PerformLayout(); Application.DoEvents();
            if (form.Width != 1040 || form.Height != 710) throw new Exception("The EmuMovies minimum-size smoke fixture must use a 1040 by 710 window.");
            var emuScroll = Named<Panel>("EmuMoviesScroll");
            if (emuScroll.HorizontalScroll.Visible) throw new Exception("The EmuMovies page clips horizontally at minimum window size.");
            foreach (var control in emuControls.Where(c => c is TextBox or ComboBox or Button or CheckBox)) {
                if (control.Width <= 0 || control.Height <= 0 || control.Parent == null) throw new Exception("An EmuMovies control has no usable size: " + control.Name);
                if (control.Right > control.Parent.ClientSize.Width - control.Parent.Padding.Right + 1) throw new Exception("An EmuMovies control extends beyond its row: " + control.Name);
            }
            var syncActions = Named<FlowLayoutPanel>("EmuMoviesSecondaryActions");
            var syncActionControls = syncActions.Controls.Cast<Control>().Where(control => control.Visible).ToArray();
            foreach (var control in syncActionControls)
                if (!syncActions.ClientRectangle.Contains(control.Bounds)) throw new Exception("An EmuMovies Sync action is clipped at minimum window size: " + control.Name);
            for (var first = 0; first < syncActionControls.Length; first++)
                for (var second = first + 1; second < syncActionControls.Length; second++)
                    if (syncActionControls[first].Bounds.IntersectsWith(syncActionControls[second].Bounds)) throw new Exception("EmuMovies Sync actions overlap at minimum window size.");
            if (sidebarButtons.Zip(sidebarButtons.Skip(1)).Any(pair => pair.First.Bottom > pair.Second.Top)) throw new Exception("Sidebar buttons overlap at minimum window size.");
            void SaveEmuMoviesSnapshot(string suffix) {
                form.PerformLayout(); Application.DoEvents();
                using var snapshot = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(snapshot, new Rectangle(0, 0, snapshot.Width, snapshot.Height));
                snapshot.Save(Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + "-Emumovies-snap-scrapper-" + suffix + ".png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            SaveEmuMoviesSnapshot("small");
            emuScroll.ScrollControlIntoView(Named<TextBox>("EmuMoviesRunDetails"));
            SaveEmuMoviesSnapshot("small-reports");
            emuScroll.AutoScrollPosition = Point.Empty;
            form.ClientSize = new Size(1270, 845);
            var releaseEmuPreview = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var busyEmuPreview = form.RunEmuMoviesBusyFixtureAsync(releaseEmuPreview.Task);
            Application.DoEvents();
            if (busyEmuPreview.IsCompleted || !Named<TableLayoutPanel>("EmuMoviesFields").Enabled) throw new Exception("The busy preview fixture must keep the field container enabled while work is pending.");
            foreach (var label in Descendants(Named<TableLayoutPanel>("EmuMoviesFields")).OfType<Label>())
                if (!label.Enabled || (label.ForeColor != Palette.Ink && label.ForeColor != Palette.Muted)) throw new Exception("A busy EmuMovies heading or field label lost its readable theme color.");
            foreach (var field in new[] { "EmuMoviesMatchFolder", "EmuMoviesWorkDirectory", "EmuMoviesSyncExecutable" })
                if (!Named<TextBox>(field).ReadOnly || !Named<TextBox>(field).Enabled || Named<TextBox>(field).ForeColor != Palette.Ink) throw new Exception("Busy path fields must be locked but readable: " + field);
            foreach (var selector in new[] { emuPlatform, emuCatalog })
                if (selector.Enabled || selector.DrawMode != DrawMode.OwnerDrawFixed || selector.BackColor != Palette.Input || selector.ForeColor != Palette.Ink) throw new Exception("Busy selectors must be locked and use the dark selection theme.");
            if (emuFallback.Enabled || Named<Button>("EmuMoviesPreviewImport").Enabled) throw new Exception("The active preview still permits request changes or a second operation.");
            SaveEmuMoviesSnapshot("busy");
            form.Size = new Size(1040, 710); SaveEmuMoviesSnapshot("busy-small");
            emuScroll.ScrollControlIntoView(Named<TextBox>("EmuMoviesRunDetails")); SaveEmuMoviesSnapshot("busy-small-reports");
            releaseEmuPreview.SetResult();
            var resumeDeadline = DateTime.UtcNow.AddSeconds(10);
            while (!busyEmuPreview.IsCompleted && DateTime.UtcNow < resumeDeadline) Application.DoEvents();
            if (!busyEmuPreview.IsCompleted) throw new Exception("The preview fixture failed to finish and unlock its controls.");
            busyEmuPreview.GetAwaiter().GetResult();
            if (!emuPlatform.Enabled || !emuCatalog.Enabled || !emuFallback.Enabled || Named<TextBox>("EmuMoviesMatchFolder").ReadOnly || !Named<Button>("EmuMoviesPreviewImport").Enabled) throw new Exception("EmuMovies inputs did not unlock after the preview finished.");
            emuScroll.AutoScrollPosition = Point.Empty; form.ClientSize = new Size(1270, 845);
            void SaveThemeProgress(string suffix) {
                form.PerformLayout(); Application.DoEvents();
                using var snapshot = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(snapshot, new Rectangle(0, 0, snapshot.Width, snapshot.Height));
                snapshot.Save(Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + "-Theme-" + suffix + ".png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            form.ShowThemeProgressFixture("Running"); Application.DoEvents();
            var queue = Named<DataGridView>("ThemeQueueGrid");
            if (queue.Rows.Count != 3 || !queue.ReadOnly || queue.Columns.OfType<DataGridViewCheckBoxColumn>().Any()) throw new Exception("Active themes must show only the immutable selected queue.");
            if (queue.Rows.Cast<DataGridViewRow>().Any(r => r.Cells[0].Value?.ToString()?.Contains("Unselected") == true)) throw new Exception("A game outside the batch appeared in the queue.");
            var overall = Named<NeonProgressBar>("ThemeOverallProgress"); var taskProgress = Named<NeonProgressBar>("CurrentTaskProgress");
            if (overall.Value != 417 || taskProgress.Value != 25) throw new Exception("Overall and current-task progress were not independent.");
            if (Named<Label>("ThemeElapsed").Text != "Elapsed 0:01:30" || !Named<Label>("ThemeRemaining").Text.Contains("0:01:30")) throw new Exception("Theme elapsed or estimated remaining time is incorrect.");
            if (Named<Button>("ThemeBackToAllGames").Enabled) throw new Exception("An active batch allowed returning to the editable library.");
            SaveThemeProgress("progress");
            form.Size = form.MinimumSize; SaveThemeProgress("progress-small"); form.ClientSize = new Size(1270, 845);
            form.AdvanceThemeProgressFixture(TimeSpan.FromHours(25));
            if (!Named<Label>("ThemeElapsed").Text.Contains("25:01:30")) throw new Exception("Long batch duration wrapped after 24 hours.");
            form.ShowThemeProgressFixture("Running"); Application.DoEvents();
            var pauseButton = Named<Button>("ThemePauseResume");
            if (!pauseButton.Visible || !pauseButton.Enabled || pauseButton.Text != "Pause batch") throw new Exception("An active theme batch has no usable Pause button.");
            pauseButton.PerformClick(); Application.DoEvents();
            if (!Named<Label>("ThemeBatchSummary").Text.Contains("Pausing") || pauseButton.Text != "Resume batch") throw new Exception("Pause must wait for the active render and allow cancelling a pending pause.");
            form.AdvanceThemeProgressFixture(TimeSpan.FromSeconds(30)); form.CompleteCurrentThemeProgressFixture();
            var pausedGate = form.WaitThemeProgressFixtureBoundaryAsync(CancellationToken.None); Application.DoEvents();
            if (pausedGate.IsCompleted || !Named<Label>("ThemeBatchSummary").Text.Contains("Paused")) throw new Exception("The theme queue did not wait asynchronously at the pause boundary.");
            var pausedElapsed = Named<Label>("ThemeElapsed").Text; var pausedEta = Named<Label>("ThemeRemaining").Text;
            form.AdvanceThemeProgressFixture(TimeSpan.FromMinutes(10));
            if (Named<Label>("ThemeElapsed").Text != pausedElapsed || pausedElapsed != "Elapsed 0:02:00" || Named<Label>("ThemeRemaining").Text != pausedEta) throw new Exception("Paused processing time or ETA continued advancing.");
            SaveThemeProgress("paused");
            form.Size = form.MinimumSize; SaveThemeProgress("paused-small"); form.ClientSize = new Size(1270, 845);
            void PumpUntilComplete(Task task) {
                var wait = System.Diagnostics.Stopwatch.StartNew();
                while (!task.IsCompleted && wait.Elapsed < TimeSpan.FromSeconds(5)) { Application.DoEvents(); Thread.Sleep(5); }
                if (!task.IsCompleted) throw new Exception("The paused theme queue did not wake.");
            }
            pauseButton.PerformClick(); PumpUntilComplete(pausedGate); pausedGate.GetAwaiter().GetResult();
            if (!Named<Label>("ThemeBatchSummary").Text.Contains("Running") || Named<Label>("ThemeElapsed").Text != pausedElapsed) throw new Exception("Resume included paused time or left the queue paused.");
            form.ShowThemeProgressFixture("Resumed");
            if (Named<Label>("ThemeElapsed").Text != "Elapsed 0:02:30") throw new Exception("Resumed work included paused time.");
            form.ShowThemeProgressFixture("Running"); pauseButton.PerformClick(); form.AdvanceThemeProgressFixture(TimeSpan.FromSeconds(30)); form.CompleteCurrentThemeProgressFixture();
            using (var pauseCancellation = new CancellationTokenSource()) {
                var cancellationGate = form.WaitThemeProgressFixtureBoundaryAsync(pauseCancellation.Token); pauseCancellation.Cancel(); PumpUntilComplete(cancellationGate);
                try { cancellationGate.GetAwaiter().GetResult(); throw new Exception("Cancelling a paused batch unexpectedly completed normally."); } catch (OperationCanceledException) { }
            }
            if (!Named<Label>("ThemeBatchSummary").Text.Contains("Cancelled") || pauseButton.Visible || !Named<Button>("ThemeBackToAllGames").Enabled) throw new Exception("Cancellation while paused did not restore stopped-batch controls.");
            form.ShowThemeProgressFixture("NearComplete"); Application.DoEvents();
            if (Named<DataGridView>("ThemeQueueGrid").Rows.Count != 2 || overall.Value != 995 || overall.Value >= overall.Maximum || Named<Label>("ThemeBatchSummary").Text.Contains("100%")) throw new Exception("An unfinished theme batch rounded up to complete before validation finished.");
            form.ShowThemeProgressFixture("Cancelled"); var cancelledElapsed = Named<Label>("ThemeElapsed").Text;
            form.AdvanceThemeProgressFixture(TimeSpan.FromMinutes(10));
            if (Named<Label>("ThemeElapsed").Text != cancelledElapsed || overall.Value >= overall.Maximum || !Named<Label>("ThemeBatchSummary").Text.Contains("Cancelled")) throw new Exception("Cancelled progress must freeze without claiming completion.");
            if (!Named<Button>("ThemeBackToAllGames").Enabled) throw new Exception("Cancelled batch cannot return to all games.");
            SaveThemeProgress("cancelled");
            Named<Button>("ThemeBackToAllGames").PerformClick(); Application.DoEvents();
            if (Named<DataGridView>("ThemeGameGrid").Rows.Count != 4) throw new Exception("Returning to all games lost library rows.");
            form.ShowThemeProgressFixture("Completed"); var completedElapsed = Named<Label>("ThemeElapsed").Text;
            form.AdvanceThemeProgressFixture(TimeSpan.FromMinutes(10));
            if (Named<Label>("ThemeElapsed").Text != completedElapsed || overall.Value != overall.Maximum || !Named<Label>("ThemeBatchSummary").Text.Contains("3 / 3 finished")) throw new Exception("Completed progress must show the full batch and a frozen timer.");
            SaveThemeProgress("completed");
            form.ShowThemeReuseFixture(temporary); Application.DoEvents();
            var selectMissingThemes = Descendants(form).OfType<Button>().Single(b => b.Text == "Select missing themes");
            selectMissingThemes.PerformClick(); Application.DoEvents();
            var reuseGrid = Named<DataGridView>("ThemeGameGrid");
            if (reuseGrid.Rows.Cast<DataGridViewRow>().Count(row => Equals(row.Cells[0].Value, true)) != 2)
                throw new Exception("Select missing themes must include reusable videos without snaps, while preserving existing themes and skipping games with no video.");
            if (!reuseGrid.Rows.Cast<DataGridViewRow>().Any(row => row.Cells.Cast<DataGridViewCell>().Any(cell => cell.Value?.ToString() == "Reuse Sega Model 2")))
                throw new Exception("The theme table must identify its Model 2 reuse action.");
            SaveThemeProgress("reuse");
            form.Size = form.MinimumSize; SaveThemeProgress("reuse-small");
            if (Named<Panel>("ThemeBatchPanel").Visible || reuseGrid.Height < reuseGrid.ColumnHeadersHeight + 2 * reuseGrid.RowTemplate.Height)
                throw new Exception("Returning to the theme library must leave at least two game rows visible at minimum window size.");
            form.ClientSize = new Size(1270, 845);
            using (var emptySetup = new MainForm(new AppSettings(), temporary, fixture: false)) {
                emptySetup.ShowInTaskbar = false; emptySetup.StartPosition = FormStartPosition.Manual; emptySetup.Location = new Point(-32000, -32000); emptySetup.Opacity = 0; emptySetup.Show(); Application.DoEvents();
                Button? HeaderAction(Control parent) {
                    foreach (Control child in parent.Controls) { if (child is Button button && button.Text == "Scan library") return button; var nested = HeaderAction(child); if (nested != null) return nested; }
                    return null;
                }
                HeaderAction(emptySetup)?.Select(); Application.DoEvents();
                emptySetup.PerformLayout();
                using var setupBitmap = new Bitmap(emptySetup.Width, emptySetup.Height);
                emptySetup.DrawToBitmap(setupBitmap, new Rectangle(0, 0, setupBitmap.Width, setupBitmap.Height));
                setupBitmap.Save(Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + "-Setup-empty.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            File.WriteAllText(Path.ChangeExtension(output, ".json"), JsonSerializer.Serialize(new { Passed = true, Width = bitmap.Width, Height = bitmap.Height, FixtureOnly = true, SimplifiedUiPassed = true, LaunchBoxSetupUiPassed = true, LibraryGenreUiPassed = true, EmuMoviesVideoUiPassed = true, EmuMoviesSyncDownloadLinkUiPassed = true, EmuMoviesBusyUiPassed = true, EmuMoviesMinimumWidth = 1040, EmuMoviesMinimumHeight = 710, ThemeProgressUiPassed = true, ThemePauseUiPassed = true, ThemeReuseUiPassed = true, BezelRepositoryUiPassed = true, DataDirectory = temporary }));
            return 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(temporary, "error.txt"), ex.ToString()); return 1; }
    }
}







