# Arcade Library Manager 0.2.12

The Bezels page now includes a local repository folder picker. It retains the existing downloaded-bezel path and searches subfolders for exact profile PNG/ZIP matches, after checking MAME artwork. Ambiguous matches require review; source artwork and existing overlays remain preserved. A changed repository invalidates an older preview, including resumed maintenance jobs.

Theme batches now have Pause/Resume. A pause request lets the current theme finish and pauses before the next game. Processing time and estimated remaining time freeze while paused and exclude the pause when resumed. Cancellation wakes a paused queue, completed themes remain saved, and the last theme cannot leave a finished batch paused. Pause is for the open app session; closing the app still ends its active work.

The desktop styling is closer to FFB Blaster MASTER: cyan and magenta accents, luminous buttons and headings, stronger active-navigation and panel borders, and matching gradient progress bars on dark tracks. Busy and disabled states retain readable text.

Validation includes nested bezel matches and ambiguity, cancellation, source preservation and rollback, stale-source review on apply/resume, pause timing and asynchronous resume/cancel, desktop fixtures at full and minimum window sizes, and the existing integration suite.
