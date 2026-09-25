using ArcadeLibraryManager.Core;

public static class ThemeBatchProgressChecks
{
    public static List<string> Run()
    {
        ProgressAndClockChecks();
        EstimateChecks();
        CancellationChecks();
        PauseChecks();
        FailureAndEmptyChecks();
        return [
            "Batch progress is monotonic through GPU retries, clamps invalid percentages, and waits for explicit completion after validation",
            "Snapshots refresh active elapsed time without mutating prior snapshots, including durations beyond 24 hours",
            "ETA uses full successful theme durations while excluding skips, failures and instant no-ops",
            "Cancellation freezes elapsed/progress, leaves pending items not started and ignores stale callbacks",
            "Failures and empty batches report honest counts; invalid IDs and overlapping active work are rejected",
            "Pause waits for the current theme, freezes progress and timing, excludes paused time after resume, and cancels safely"
        ];
    }

    private static void ProgressAndClockChecks()
    {
        var now = TimeSpan.FromHours(72);
        var batch = new ThemeBatchProgress([("a", "First game"), ("b", "Second game")], () => now);
        Require(batch.Total == 2 && batch.IsRunning && batch.CurrentId == "" && batch.Elapsed == TimeSpan.Zero && batch.Percent == 0 && batch.Remaining is null,
            "New queues must begin without fabricated progress or ETA.");
        Require(batch.Items.Select(i => i.Title).SequenceEqual(new[] { "First game", "Second game" }) && batch.Items.All(i => i.Status == "Queued"), "Queue order and names must be retained.");
        now += TimeSpan.FromSeconds(2); batch.Start("a");
        var firstSnapshot = batch.Items;
        now += TimeSpan.FromSeconds(5);
        Require(batch.Elapsed == TimeSpan.FromSeconds(7) && batch.Items[0].Elapsed == TimeSpan.FromSeconds(5) && firstSnapshot[0].Elapsed == TimeSpan.Zero,
            "Timer snapshots must refresh current work while preserving earlier snapshots and excluding queue wait from item time.");
        batch.Report("a", new("Theme", "GPU encoding", "a", 80));
        Near(batch.Percent, 40, "Overall progress must include the active item's fraction.");
        batch.Report("a", new("Theme", "CPU retry", "a", 12));
        Near(batch.Items[0].Progress, 80, "GPU-to-CPU fallback must not move progress backwards.");
        batch.Report("a", new("Cutouts", "Preparing source art"));
        Require(batch.Items[0].Detail == "Preparing source art", "Indeterminate stage detail must remain visible.");
        Near(batch.Percent, 40, "An indeterminate stage must retain established overall progress.");
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -500d }) batch.Report("a", new("Theme", "Untrusted progress", Percent: invalid));
        Near(batch.Percent, 40, "Invalid or negative percentages cannot regress or poison progress.");
        batch.Report("a", new("Theme", "Validated callback pending", "a", 1000));
        Require(batch.Finished == 0 && batch.Items[0].Status == "Running", "Renderer percentage must not count a theme as complete.");
        Near(batch.Items[0].Progress, 99, "Active item percentage must stay below 100 through validation.");
        Near(batch.Percent, 49.5, "The batch must reserve completion credit until explicit completion.");
        var detail = batch.Items[0].Detail;
        batch.Report("a", new("Theme", "Wrong event identity", "b", 1));
        batch.Report("b", new("Theme", "Inactive row", "b", 1));
        batch.Report("unknown", new("Theme", "Unknown row", Percent: 100));
        Require(batch.Items[0].Detail == detail && batch.Items[1].Detail == "", "Stale or mismatched game events must not alter the active or pending rows.");
        Throws<InvalidOperationException>(() => batch.Start("b"), "A second active theme must not overwrite the first item's timing.");
        batch.Start("a");
        now += TimeSpan.FromSeconds(5);
        batch.Complete("a", "Completed", "Created and verified");
        Require(batch.Finished == 1 && batch.Succeeded == 1 && batch.CurrentId == "" && batch.Items[0].Elapsed == TimeSpan.FromSeconds(10), "Full item duration must include preparation and validation.");
        Near(batch.Percent, 50, "Completion must advance one full queue entry.");
        batch.Complete("a", "Failed", "Late duplicate completion");
        Require(batch.Succeeded == 1 && batch.Failed == 0, "Late completion must not reclassify or count a finished item twice.");
        batch.Start("b"); batch.Report("a", new("Theme", "Old item callback", "a", 100));
        Require(batch.Items[1].Detail == "Preparing theme" && batch.Items[1].Progress == 0, "A previous item's callbacks cannot affect a later item.");
        now += TimeSpan.FromHours(27) + TimeSpan.FromSeconds(3);
        Require(batch.Elapsed.TotalHours > 27 && batch.Items[1].Elapsed == TimeSpan.FromHours(27) + TimeSpan.FromSeconds(3), "Elapsed time must preserve days rather than wrap after 24 hours.");
        batch.Complete("b", "Completed"); var frozen = batch.Elapsed;
        now += TimeSpan.FromDays(2);
        Require(!batch.IsRunning && batch.Finished == 2 && batch.Percent == 100 && batch.Remaining == TimeSpan.Zero && batch.Elapsed == frozen,
            "A completed queue must freeze at full progress with zero remaining work.");
    }

    private static void EstimateChecks()
    {
        var now = TimeSpan.Zero;
        var batch = new ThemeBatchProgress([("a", "First"), ("b", "Second"), ("c", "Third")], () => now);
        batch.Start("a"); now += TimeSpan.FromSeconds(20); batch.Complete("a", "Completed");
        Require(batch.Remaining == TimeSpan.FromSeconds(40), "The first full completed theme should estimate the two remaining themes.");
        batch.Start("b"); now += TimeSpan.FromSeconds(5);
        Require(batch.Remaining == TimeSpan.FromSeconds(35), "ETA must update on timer reads during current work without new progress events.");
        now += TimeSpan.FromSeconds(25);
        Require(batch.Remaining == TimeSpan.FromSeconds(20), "An overrun on the current item must not erase the expected duration of later items.");
        now += TimeSpan.FromSeconds(20); batch.Complete("b", "Completed");
        Require(batch.Remaining == TimeSpan.FromSeconds(35), "ETA must average full successful durations rather than render-percent samples.");
        batch.Start("c"); now += TimeSpan.FromSeconds(5);
        Require(batch.Remaining == TimeSpan.FromSeconds(30), "The next theme must use updated successful-duration estimates.");
        now -= TimeSpan.FromSeconds(3);
        Require(batch.Items[2].Elapsed == TimeSpan.FromSeconds(5) && batch.Remaining == TimeSpan.FromSeconds(30), "A regressing injected clock must not reduce elapsed time or increase ETA.");
        batch.Stop(true);
        Require(batch.Remaining is null, "A stopped unfinished queue must not display a live countdown estimate.");

        now = TimeSpan.Zero;
        var filtered = new ThemeBatchProgress([("instant", "Instant"), ("skip", "Skipped"), ("fail", "Failed"), ("good", "Good"), ("remaining", "Remaining")], () => now);
        filtered.Start("instant"); filtered.Complete("instant", "Completed");
        Require(filtered.Remaining is null, "A zero-duration no-op is not evidence for an ETA.");
        filtered.Start("skip"); now += TimeSpan.FromHours(1); filtered.Complete("skip", "Skipped");
        Require(filtered.Remaining is null, "A skipped item must not train the successful-render estimate.");
        filtered.Start("fail"); now += TimeSpan.FromHours(1); filtered.Complete("fail", "Failed");
        Require(filtered.Remaining is null, "A failed item must not train the successful-render estimate.");
        filtered.Start("good"); now += TimeSpan.FromSeconds(12); filtered.Complete("good", "Completed");
        Require(filtered.Remaining == TimeSpan.FromSeconds(12) && filtered.Succeeded == 2 && filtered.Skipped == 1 && filtered.Failed == 1 && filtered.Finished == 4,
            "Only successful full work should set the estimate while every processed item has an honest count.");
        filtered.Complete("remaining", "Skipped", "Existing theme preserved");
        Require(filtered.Percent == 100 && !filtered.IsRunning && filtered.Items[^1].Elapsed == TimeSpan.Zero, "A preflight skip may finish a queued item without inventing execution time.");
    }

    private static void CancellationChecks()
    {
        var now = TimeSpan.Zero;
        var batch = new ThemeBatchProgress([("done", "Done"), ("active", "Active"), ("pending", "Pending")], () => now);
        batch.Start("done"); now += TimeSpan.FromSeconds(10); batch.Complete("done", "Completed");
        batch.Start("active"); now += TimeSpan.FromSeconds(5); batch.Report("active", new("Theme", "Rendering", "active", 75));
        var percent = batch.Percent; var elapsed = batch.Elapsed;
        batch.Stop(true, "Cancelled by user");
        Require(!batch.IsRunning && batch.CurrentId == "" && batch.Finished == 1 && batch.Succeeded == 1 && batch.Failed == 0 && batch.Skipped == 0,
            "Cancellation must not inflate finished or failed counts.");
        Require(batch.Items[1].Status == "Cancelled" && batch.Items[1].Elapsed == TimeSpan.FromSeconds(5) && batch.Items[2].Status == "Not started"
            && batch.Items[2].Elapsed == TimeSpan.Zero, "Cancellation must distinguish attempted current work from untouched queued work.");
        Near(batch.Percent, percent, "Cancellation must retain the last visible overall progress.");
        Require(batch.Percent < 100 && batch.Remaining is null, "An unfinished cancelled batch cannot claim full completion or an active ETA.");
        now += TimeSpan.FromDays(1);
        batch.Report("active", new("Theme", "Stale finished callback", "active", 100));
        batch.Complete("active", "Completed", "Late task callback"); batch.Stop();
        Require(batch.Elapsed == elapsed && batch.Percent == percent && batch.Finished == 1 && batch.Items[1].Status == "Cancelled",
            "Late reports or completions must not mutate a stopped batch or extend its elapsed time.");
        var queued = new ThemeBatchProgress([("a", "Queued")], () => now); queued.Stop(true);
        Require(queued.Finished == 0 && queued.Percent == 0 && queued.Items[0].Status == "Not started", "Cancellation before the first item must preserve a wholly unstarted queue.");
    }

    private static void PauseChecks()
    {
        var now = TimeSpan.FromDays(3);
        var batch = new ThemeBatchProgress([("a", "First"), ("b", "Second"), ("c", "Third")], () => now);
        batch.Start("a"); now += TimeSpan.FromSeconds(10);
        Throws<InvalidOperationException>(() => batch.Pause(), "A pause cannot interrupt an active render.");
        Require(!batch.IsPaused && batch.Items[0].Status == "Running", "A rejected mid-render pause must leave the active theme intact.");
        batch.Complete("a", "Completed"); batch.Pause(); batch.Pause();
        var frozen = batch.Elapsed; var percent = batch.Percent; var eta = batch.Remaining;
        now += TimeSpan.FromHours(27);
        Require(batch.IsRunning && batch.IsPaused && batch.Elapsed == frozen && batch.Percent == percent && batch.Remaining == eta && eta == TimeSpan.FromSeconds(20), "A paused queue must retain all progress and ETA while its clock is frozen beyond 24 hours.");
        Throws<InvalidOperationException>(() => batch.Start("b"), "No next theme can start while paused.");
        batch.Report("a", new("Theme", "Late render progress", "a", 100));
        Require(batch.Items[0].Status == "Completed" && batch.Items[1].Status == "Queued", "Late progress during pause must not alter queued work.");
        batch.Resume(); batch.Resume();
        Require(!batch.IsPaused && batch.IsRunning && batch.Elapsed == frozen, "Resume must exclude the full pause and be idempotent.");
        batch.Start("b"); now += TimeSpan.FromSeconds(5);
        Require(batch.Elapsed == TimeSpan.FromSeconds(15) && batch.Items[1].Elapsed == TimeSpan.FromSeconds(5) && batch.Remaining == TimeSpan.FromSeconds(15), "Resumed timing must count only active work.");
        now -= TimeSpan.FromSeconds(3);
        Require(batch.Elapsed == TimeSpan.FromSeconds(15), "A regressing clock after resume cannot reduce elapsed time.");
        now += TimeSpan.FromSeconds(8); batch.Complete("b", "Completed"); batch.Pause();
        now += TimeSpan.FromHours(2); batch.Resume(); batch.Start("c"); now += TimeSpan.FromSeconds(10); batch.Complete("c", "Completed");
        Require(!batch.IsRunning && !batch.IsPaused && batch.Elapsed == TimeSpan.FromSeconds(30) && batch.Percent == 100 && batch.Items.All(i => i.Elapsed == TimeSpan.FromSeconds(10)), "Multiple pauses must never inflate item durations or the finished batch timer.");
        batch.Pause(); batch.Resume();
        Require(!batch.IsPaused && batch.Elapsed == TimeSpan.FromSeconds(30), "A finished batch cannot be paused or restarted.");

        var cancelled = new ThemeBatchProgress([("a", "First"), ("b", "Second")], () => now);
        cancelled.Start("a"); now += TimeSpan.FromSeconds(8); cancelled.Complete("a", "Completed"); cancelled.Pause();
        now += TimeSpan.FromDays(2); cancelled.Stop(true); cancelled.Resume();
        Require(!cancelled.IsRunning && !cancelled.IsPaused && cancelled.Elapsed == TimeSpan.FromSeconds(8) && cancelled.Finished == 1 && cancelled.Percent == 50 && cancelled.Items[1].Status == "Not started" && cancelled.Remaining is null, "Cancellation while paused must preserve completed results and freeze at active elapsed time.");
        var unstarted = new ThemeBatchProgress([("a", "First")], () => now);
        unstarted.Pause(); now += TimeSpan.FromHours(1); unstarted.Resume(); unstarted.Start("a"); now += TimeSpan.FromSeconds(4); unstarted.Complete("a", "Completed");
        Require(unstarted.Elapsed == TimeSpan.FromSeconds(4), "Pausing before the first item must not add queue wait to processing time.");
    }

    private static void FailureAndEmptyChecks()
    {
        var now = TimeSpan.Zero;
        var batch = new ThemeBatchProgress([("a", "Active"), ("b", "Pending")], () => now);
        batch.Start("a"); now += TimeSpan.FromSeconds(3); batch.Stop(detail: "Unexpected batch error");
        Require(batch.Failed == 1 && batch.Finished == 1 && batch.Succeeded == 0 && batch.Items[0].Status == "Failed" && batch.Items[1].Status == "Not started"
            && batch.Items[0].Detail == "Unexpected batch error" && batch.Percent == 50, "An aborted batch must count the failed current item while preserving unstarted work.");
        var empty = new ThemeBatchProgress([], () => now); now += TimeSpan.FromHours(25);
        Require(empty.Total == 0 && empty.Finished == 0 && !empty.IsRunning && empty.Percent == 0 && empty.Elapsed == TimeSpan.Zero && empty.Remaining is null,
            "An empty batch must avoid divide-by-zero, fabricated completion and a running clock.");
        Throws<ArgumentException>(() => new ThemeBatchProgress([("a", "One"), ("A", "Duplicate")]), "Duplicate IDs must be rejected regardless of case.");
        Throws<ArgumentException>(() => new ThemeBatchProgress([("", "Missing ID")]), "Missing IDs must be rejected.");
        var valid = new ThemeBatchProgress([("a", "A")], () => now);
        Throws<ArgumentException>(() => valid.Start("unknown"), "Unknown Start IDs must be rejected.");
        Throws<InvalidOperationException>(() => valid.Complete("a", "Completed"), "An unstarted theme cannot claim successful generation.");
        valid.Start("a");
        Throws<ArgumentException>(() => valid.Complete("a", "Cancelled"), "Cancellation must pass through Stop to preserve honest counts.");
        valid.Complete("a", "Completed"); valid.Stop(true);
        Require(valid.Percent == 100 && valid.Succeeded == 1 && valid.Items[0].Status == "Completed", "Cancellation after all work finished must not rewrite successful history.");
    }

    private static void Near(double actual, double expected, string message) => Require(Math.Abs(actual - expected) < .000001, message);
    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Theme batch check failed: " + message);
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException("Theme batch check failed: " + message); }
}