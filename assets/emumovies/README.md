# EmuMovies video backend

The app runs official EmuMovies Sync 2.71 through its existing Windows UI, then imports only video snaps. Users open Sync and sign in there. These helpers do not read or copy account credentials.

`Start-EmuMoviesSyncJob.ps1` verifies the live catalog, HQ video selection and folders before Go. It requires a new busy/status transition before accepting Complete. During an app-managed wait, a cancellation signal invokes Sync's Cancel control only after confirming process/catalog/folder ownership, then waits for idle. Failure to confirm cancellation is reported; killing only the waiter is deliberately avoided.

`Invoke-EmuMoviesVideoJob.ps1` takes a structured JSON config from the service, stages video downloads and invokes the importer with VideoOnly. Each run keeps selection, missing-file, import and process reports. Matching folders contain empty filename placeholders, never copied ROM content. Existing Sync options are preserved.

`Import-EmuMoviesMedia.ps1` fills missing video categories without overwriting existing ROM- or title-named assets. TeknoParrot MAME imports are scoped to exact, unique local MAME arcade metadata identities selected by the service; that scope is not a claim that every downloaded video was visually verified. Unknown identities remain in staging with reports. Staged artwork is never imported by this workflow.

Fixture checks are in tests/ArcadeLibraryManager.Tests/EmuMoviesChecks.cs. They exercise local preview, gap imports, repeat behavior, title-based existing media, theme separation, hardware routing, another platform, cancellation before work and output path guards. Live Sync operations require a signed-in desktop session and are not part of those fixtures.
