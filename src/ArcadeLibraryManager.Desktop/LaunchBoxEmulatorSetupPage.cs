using System.Text;
using System.Text.Json;
using ArcadeLibraryManager.Core;

namespace ArcadeLibraryManager.Desktop;

public sealed partial class MainForm
{
    private Label _launchBoxSetupStatus = null!;
    private TextBox _launchBoxSetupDetails = null!;
    private Button _launchBoxSetupCheck = null!, _launchBoxSetupReview = null!;
    private LaunchBoxEmulatorSetupPlan? _launchBoxSetupPlan;
    private string _launchBoxSetupSettings = "";
    private bool _launchBoxSetupNeedsCheck;

    private void BuildLaunchBoxSetupCard(TableLayoutPanel stack)
    {
        var card = Section(stack, "LaunchBox launch setup", "Check TeknoParrot's emulator command and filename options. Inspection is read-only; review any repair before applying it.");
        card.Name = "LaunchBoxSetupCard";
        _launchBoxSetupStatus = new Label { Name = "LaunchBoxSetupStatus", Text = "Not checked", Dock = DockStyle.Top, Height = 28, ForeColor = Palette.Cyan, Font = Palette.Font(10, FontStyle.Bold), AutoEllipsis = true };
        card.Controls.Add(_launchBoxSetupStatus, 0, card.RowCount); card.SetColumnSpan(_launchBoxSetupStatus, 3); card.RowCount++;
        _launchBoxSetupDetails = Palette.TextBox(true);
        _launchBoxSetupDetails.Name = "LaunchBoxSetupDetails";
        _launchBoxSetupDetails.ReadOnly = true; _launchBoxSetupDetails.ScrollBars = ScrollBars.Vertical;
        _launchBoxSetupDetails.Height = 124; _launchBoxSetupDetails.Dock = DockStyle.Fill;
        _launchBoxSetupDetails.Text = "Choose the TeknoParrot and LaunchBox folders above, then check launch setup. The check also runs when you save setup or discover related locations.";
        card.Controls.Add(_launchBoxSetupDetails, 0, card.RowCount); card.SetColumnSpan(_launchBoxSetupDetails, 3); card.RowCount++;
        var actions = new FlowLayoutPanel { Name = "LaunchBoxSetupActions", Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(0, 7, 0, 0) };
        _launchBoxSetupCheck = WorkButton("Check launch setup", async (_, _) => await CheckLaunchBoxSetupAsync(), false, 188);
        _launchBoxSetupCheck.Name = "LaunchBoxSetupCheck";
        _launchBoxSetupReview = Palette.Button("Review launch repair", true, 195);
        _launchBoxSetupReview.Name = "LaunchBoxSetupReview";
        _launchBoxSetupReview.Enabled = false;
        _launchBoxSetupReview.Click += async (_, _) => await ReviewLaunchBoxSetupAsync();
        _launchBoxSetupCheck.EnabledChanged += (_, _) => RefreshLaunchBoxSetupActions();
        actions.Controls.Add(_launchBoxSetupCheck); actions.Controls.Add(_launchBoxSetupReview);
        card.Controls.Add(actions, 0, card.RowCount); card.SetColumnSpan(actions, 3); card.RowCount++;
        foreach (var control in new TextBox[] { _paths["LaunchBoxPath"], _paths["TeknoParrotPath"], _platform })
            control.TextChanged += (_, _) => InvalidateLaunchBoxSetupPreview();
    }

    private AppSettings LaunchBoxSetupSettings() => new()
    {
        LaunchBoxPath = _paths["LaunchBoxPath"].Text.Trim().Trim('"'),
        TeknoParrotPath = _paths["TeknoParrotPath"].Text.Trim().Trim('"'),
        PlatformName = string.IsNullOrWhiteSpace(_platform.Text) ? "TeknoParrot" : _platform.Text.Trim()
    };

    private static string LaunchBoxSetupFingerprint(AppSettings settings) => JsonSerializer.Serialize(new { settings.LaunchBoxPath, settings.TeknoParrotPath, settings.PlatformName });

    private void InvalidateLaunchBoxSetupPreview()
    {
        if (_launchBoxSetupPlan == null || LaunchBoxSetupFingerprint(LaunchBoxSetupSettings()) == _launchBoxSetupSettings) return;
        _launchBoxSetupNeedsCheck = true;
        _launchBoxSetupStatus.Text = "Setup changed — check launch setup again";
        RefreshLaunchBoxSetupActions();
    }

    private void RefreshLaunchBoxSetupActions()
    {
        if (_launchBoxSetupReview == null) return;
        _launchBoxSetupReview.Enabled = _launchBoxSetupCheck.Enabled && !_busy && !_launchBoxSetupNeedsCheck && _launchBoxSetupPlan?.CanRepair == true;
    }

    private void ShowLaunchBoxSetupPlan(LaunchBoxEmulatorSetupPlan plan, string settingsFingerprint, bool needsCheck = false, string note = "")
    {
        _launchBoxSetupPlan = plan; _launchBoxSetupSettings = settingsFingerprint; _launchBoxSetupNeedsCheck = needsCheck;
        _launchBoxSetupStatus.Text = needsCheck ? plan.Status + " — check current setup before repairing" : plan.Status;
        _launchBoxSetupStatus.ForeColor = plan.IsReady && !needsCheck ? Palette.Cyan : Palette.Ink;
        _launchBoxSetupDetails.Text = LaunchBoxSetupDescription(plan) + (note.Length > 0 ? "\r\n\r\n" + note : "");
        RefreshLaunchBoxSetupActions();
    }

    private static string LaunchBoxSetupDescription(LaunchBoxEmulatorSetupPlan plan)
    {
        var text = new StringBuilder(plan.Detail);
        text.Append("\r\nConfiguration: ").Append(plan.SourcePath.Length > 0 ? plan.SourcePath : "No configuration selected");
        if (plan.EmulatorName.Length > 0) text.Append("\r\nEmulator: ").Append(plan.EmulatorName).Append(" (").Append(plan.EmulatorId).Append(')');
        text.Append("\r\nEffective command: ").Append(plan.CurrentCommand.Length > 0 ? plan.CurrentCommand : "(empty)");
        if (plan.ProposedCommand.Length > 0) text.Append("\r\nProposed command: ").Append(plan.ProposedCommand);
        return text.ToString();
    }

    private async Task CheckLaunchBoxSetupAsync()
    {
        if (_busy) return;
        if (_fixture) { ShowLaunchBoxSetupFixture("Ready"); SetStatus("Launch setup fixture inspected; no library files were read or changed."); return; }
        var snapshot = LaunchBoxSetupSettings();
        var fingerprint = LaunchBoxSetupFingerprint(snapshot);
        await RunWork("Checking LaunchBox's TeknoParrot launch setup…", async ct =>
        {
            var plan = await new LaunchBoxEmulatorSetupService().InspectAsync(snapshot, ct);
            var stale = LaunchBoxSetupFingerprint(LaunchBoxSetupSettings()) != fingerprint;
            ShowLaunchBoxSetupPlan(plan, fingerprint, stale, stale ? "The folders or platform changed during inspection. Check again for the current setup." : "");
            SetStatus("LaunchBox launch setup: " + plan.Status + ". No library files were changed.");
        });
        RefreshLaunchBoxSetupActions();
    }

    private async Task ReviewLaunchBoxSetupAsync()
    {
        if (_busy || _launchBoxSetupPlan?.CanRepair != true) return;
        InvalidateLaunchBoxSetupPreview();
        if (_launchBoxSetupNeedsCheck) { SetStatus("Check launch setup again before reviewing a repair."); return; }
        var plan = _launchBoxSetupPlan;
        var fingerprint = _launchBoxSetupSettings;
        using var dialog = BuildLaunchBoxSetupReviewDialog(plan);
        if (dialog.ShowDialog(this) != DialogResult.OK || _fixture) return;
        await RunWork("Applying the reviewed LaunchBox launch repair…", async ct =>
        {
            var current = LaunchBoxSetupSettings();
            if (LaunchBoxSetupFingerprint(current) != fingerprint) throw new InvalidOperationException("The folders or platform changed after this preview. Check launch setup again before applying a repair.");
            var service = new LaunchBoxEmulatorSetupService();
            var result = await service.ApplyAsync(plan, current, ct);
            var refreshed = await service.InspectAsync(current, ct);
            ShowLaunchBoxSetupPlan(refreshed, fingerprint, LaunchBoxSetupFingerprint(LaunchBoxSetupSettings()) != fingerprint,
                result.Detail + (result.BackupPath.Length > 0 ? "\r\nBackup: " + result.BackupPath : ""));
            SetStatus(result.Detail);
            AppendProgress(new JobEvent(result.Changed ? "LaunchBox repaired" : "LaunchBox checked", result.Detail + (result.BackupPath.Length > 0 ? " Backup: " + result.BackupPath : "")));
        });
        RefreshLaunchBoxSetupActions();
    }

    private Form BuildLaunchBoxSetupReviewDialog(LaunchBoxEmulatorSetupPlan plan)
    {
        var dialog = new Form { Name = "LaunchBoxSetupRepairDialog", Text = "Review LaunchBox launch repair", ClientSize = new Size(920, 540), MinimumSize = new Size(760, 440), StartPosition = FormStartPosition.CenterParent, BackColor = Palette.Page, Font = Palette.Font(), Padding = new Padding(18), MinimizeBox = false, MaximizeBox = false };
        var details = Palette.TextBox(true); details.Name = "LaunchBoxSetupRepairDetails"; details.Dock = DockStyle.Top; details.Height = 140; details.ReadOnly = true; details.ScrollBars = ScrollBars.Vertical; details.Text = LaunchBoxSetupDescription(plan);
        var changes = Palette.Grid(); changes.Name = "LaunchBoxSetupChanges"; changes.ReadOnly = true;
        Palette.TextColumn(changes, "Field", "SETTING", 110); Palette.TextColumn(changes, "Before", "CURRENT", 150); Palette.TextColumn(changes, "After", "PROPOSED", 150);
        changes.DefaultCellStyle.WrapMode = DataGridViewTriState.True; changes.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        changes.DataSource = plan.Changes.ToList();
        var backup = Hint("LaunchBox and Big Box must be closed. Existing configuration is backed up under LaunchBox\\Backups\\ArcadeLibraryManager before the update. Creating a new configuration records a receipt instead. Only the listed settings will change.", 72);
        backup.Name = "LaunchBoxSetupBackupNotice"; backup.Dock = DockStyle.Bottom;
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 55, Padding = new Padding(0, 8, 0, 0), FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var apply = Palette.Button("Back up and apply repair", true, 235); apply.Name = "LaunchBoxSetupApply"; apply.Enabled = !_fixture && plan.CanRepair;
        apply.DialogResult = DialogResult.OK;
        var cancel = Palette.Button("Cancel", false, 108); cancel.DialogResult = DialogResult.Cancel;
        actions.Controls.Add(apply); actions.Controls.Add(cancel); dialog.CancelButton = cancel;
        dialog.Controls.Add(changes); dialog.Controls.Add(backup); dialog.Controls.Add(actions); dialog.Controls.Add(details);
        return dialog;
    }

    internal void ShowLaunchBoxSetupFixture(string status)
    {
        if (!_fixture) throw new InvalidOperationException("Launch setup fixtures are available only in the isolated UI test.");
        var snapshot = LaunchBoxSetupSettings();
        var repair = status == "Needs repair";
        var plan = new LaunchBoxEmulatorSetupPlan {
            Status = status, Detail = status == "Needs review" ? "Several emulator entries match; select the correct association before repairing." : repair ? "The effective profile command needs repair." : "The effective profile command and filename options are ready.",
            EmulatorName = "TeknoParrot", EmulatorId = "fixture-emulator", SourcePath = @"X:\Fixture\LaunchBox\Data\Emulators.xml",
            CurrentCommand = repair ? "--profile=%ROMNAME%" : "--profile=%romfile%.xml", ProposedCommand = "--profile=%romfile%.xml",
            Changes = repair ? [new LaunchBoxEmulatorFieldChange("CommandLine", "--profile=%ROMNAME%", "--profile=%romfile%.xml")] : []
        };
        ShowLaunchBoxSetupPlan(plan, LaunchBoxSetupFingerprint(snapshot));
    }

    internal Form CreateLaunchBoxSetupReviewFixture()
    {
        if (!_fixture || _launchBoxSetupPlan == null) throw new InvalidOperationException("Create a launch setup fixture first.");
        return BuildLaunchBoxSetupReviewDialog(_launchBoxSetupPlan);
    }
}
