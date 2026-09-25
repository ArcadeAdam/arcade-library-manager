# Arcade Library Manager 0.2.8

Theme generation now switches the game table to the selected batch. Each row shows its status, progress, elapsed time, and current activity. Games outside the batch are hidden until the user returns to the full list.

An overall progress bar and timer sit above the queue. Elapsed time updates while preparing assets, rendering, and validating the output. Estimated remaining time uses completed successful renders and is labeled as an estimate. Current-task progress remains in the bottom status bar.

Completed and cancelled batches retain their results. Cancellation freezes elapsed time and leaves unfinished games available for another batch. Failed or skipped games have separate outcomes, and queued games are not reported as completed. GPU rendering, existing-theme preservation, and source artwork are unchanged.

Every new theme and preview now requires a nonempty local video snap. Missing snaps are skipped, the artwork-only composition is removed, and automatic selection excludes those games. Existing themes are preserved even when their source snap is no longer present.

Validation covers missing-snap prevention, timing and progress calculations, cancellation, stale progress events, ETA behavior, the focused desktop queue, and the existing integration suite.
