using System.ComponentModel;
using System.Text.Json;
using ArcadeLibraryManager.Core;

namespace ArcadeLibraryManager.Desktop;

public sealed partial class MainForm
{
    private async Task DiscoverSetupAsync()
    {
        if (_fixture) { ShowLaunchBoxSetupFixture("Needs repair"); SetStatus("Discovery fixture ready; no locations or library files were read or changed."); return; }
        SetupDiscoveryResult? discovered = null;
        var reviewedSettings = "";
        await RunWork("Looking for related locations in the selected folders…", async ct => {
            ReadSettingsFromControls(); reviewedSettings = JsonSerializer.Serialize(_settings);
            var snapshot = JsonSerializer.Deserialize<AppSettings>(reviewedSettings)!;
            discovered = await new SetupDiscoveryService().DiscoverAsync(snapshot, ct);
            SetStatus($"Discovery found {discovered.Suggestions.Count:N0} suggestions. Nothing has been applied yet.");
        });
        if (discovered == null) return;
        var launchSetup = discovered.LaunchBoxEmulatorSetup;
        if (launchSetup != null) {
            var checkedSettings = JsonSerializer.Deserialize<AppSettings>(reviewedSettings)!;
            var fingerprint = LaunchBoxSetupFingerprint(checkedSettings);
            var needsLocations = discovered.Suggestions.Any(s => s.PropertyName is nameof(AppSettings.TeknoParrotPath) or nameof(AppSettings.LaunchBoxPath) or nameof(AppSettings.PlatformName));
            var changed = fingerprint != LaunchBoxSetupFingerprint(LaunchBoxSetupSettings());
            ShowLaunchBoxSetupPlan(launchSetup, fingerprint, needsLocations || changed,
                needsLocations ? "Discovery checked proposed locations. Apply or select those folders, then check current launch setup before repairing." : changed ? "Setup changed during discovery. Check current launch setup before repairing." : "Discovery only inspected the configuration. Review any repair separately in LaunchBox launch setup.");
        }
        if (discovered.Suggestions.Count == 0) {
            ShowDetails("Setup discovery", "No missing settings could be inferred from the selected folders."
                + (launchSetup != null ? "\r\n\r\nLaunchBox launch setup: " + launchSetup.Status + "\r\n" + LaunchBoxSetupDescription(launchSetup) + "\r\n\r\nUse the LaunchBox launch setup card to review an available repair." : "")
                + (discovered.Warnings.Count > 0 ? "\r\n\r\n" + string.Join("\r\n", discovered.Warnings) : "\r\n\r\nSelect a TeknoParrot, LaunchBox, or ROM source folder and try again."));
            return;
        }
        using var dialog = new Form { Text = "Review discovered locations", ClientSize = new Size(950, 530), MinimumSize = new Size(750, 400), StartPosition = FormStartPosition.CenterParent, BackColor = Palette.Page, Font = Palette.Font(), Padding = new Padding(22), MinimizeBox = false, MaximizeBox = false };
        var summary = Hint("Review the proposed locations. Applying suggestions fills empty settings only; your existing choices stay in place.", 47);
        var table = Palette.Grid(); Palette.CheckColumn(table); Palette.TextColumn(table, "Setting", "SETTING", 130); Palette.TextColumn(table, "Value", "PROPOSED LOCATION", 260); Palette.TextColumn(table, "Reason", "FOUND FROM", 180);
        var rows = new BindingList<DiscoverySelection>(discovered.Suggestions.Select(s => new DiscoverySelection(s)).ToList()); table.DataSource = rows;
        var warnings = new TextBox { Dock = DockStyle.Bottom, Height = 90, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = Palette.Soft, ForeColor = Palette.Muted, Font = Palette.Font(9), Text = (launchSetup != null ? "LaunchBox launch setup: " + launchSetup.Status + ". " + launchSetup.Detail + "\r\nAfter applying locations, launch setup is checked again. Repairs are reviewed separately.\r\n" : "") + (discovered.Warnings.Count > 0 ? string.Join("\r\n", discovered.Warnings) : "Only selected folders and known nearby locations were checked. No full ROM walk or internet search was performed.") };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(0, 9, 0, 0), FlowDirection = FlowDirection.RightToLeft };
        var apply = Palette.Button("Apply selected suggestions", true, 232); var close = Palette.Button("Cancel", false, 102); close.DialogResult = DialogResult.Cancel;
        apply.Click += (_, _) => {
            table.EndEdit(); ReadSettingsFromControls();
            if (JsonSerializer.Serialize(_settings) != reviewedSettings) { ShowDetails("Setup changed", "The selected folders changed while discovery was running. Discover again to review suggestions for the current setup."); dialog.Close(); return; }
            var selected = rows.Where(r => r.Selected).Select(r => r.Suggestion).ToList();
            if (selected.Count == 0) { SetStatus("No discovery suggestions selected."); return; }
            try {
                _settings = SetupDiscoveryService.ApplySuggestions(_settings, selected, fillOnlyEmpty: true);
                LoadSettingsIntoControls(); if (!_fixture) SettingsStore.Save(_dataDirectory, _settings);
                SetStatus("Selected suggestions applied to empty settings and saved. Review your setup before scanning.");
                dialog.DialogResult = DialogResult.OK;
            } catch (Exception ex) { ShowDetails("Could not apply suggestions", ex.Message); }
        };
        buttons.Controls.Add(apply); buttons.Controls.Add(close); dialog.CancelButton = close;
        dialog.Controls.Add(table); dialog.Controls.Add(warnings); dialog.Controls.Add(buttons); dialog.Controls.Add(summary);
        if (dialog.ShowDialog(this) == DialogResult.OK) await CheckLaunchBoxSetupAsync();
    }
    private sealed class DiscoverySelection(SetupSuggestion suggestion)
    {
        public SetupSuggestion Suggestion { get; } = suggestion;
        public bool Selected { get; set; } = true;
        public string Setting => Suggestion.PropertyName switch { "RomRoots" => "ROM source folders", "TeknoParrotPath" => "Teknoparrot emulator installed location", "LaunchBoxPath" => "LaunchBox", "DestinationPath" => "New-game destination", "ThemeExamplesPath" => "Theme examples", "FfmpegPath" => "FFmpeg", "SevenZipPath" => "7-Zip", "MameArtworkPath" => "MAME artwork", "BezelImportPath" => "Local bezel repository", "CachePath" => "Download cache", "PlatformName" => "LaunchBox platform", _ => Suggestion.PropertyName };
        public string Value => Suggestion.Value;
        public string Reason => Suggestion.Reason;
    }
}
