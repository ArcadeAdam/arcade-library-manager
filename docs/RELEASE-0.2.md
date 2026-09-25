# Version 0.2.0

This portable preview adds a connected maintenance workflow to version 0.1.

- Select games once, review their installation, LaunchBox, media, theme, and bezel steps, and run the selected work.
- Resume unfinished steps from durable journals without repeating completed steps. Source and settings changes require a fresh review.
- Discover related installation and media folders without scanning entire drives; review suggestions before filling empty settings.
- Prepare a LaunchBox media playlist using exact profile identities, including alternate applications. Existing playlists are retained. A checklist is produced if the installed playlist format is unsupported.
- Use an EmuMovies subscription through LaunchBox, then acknowledge the finished download during resume. Matching nonempty local media is required before that handoff completes.
- Generate missing themes after the media handoff. Empty existing themes are preserved and reported for repair.

## Using the EmuMovies handoff

Close LaunchBox when creating its queue. Then open the named playlist in LaunchBox, select its games, and run **Tools > Download > Update Metadata and Media for Selected Games**. Sign in to EmuMovies there. After downloads finish, close LaunchBox and choose **Maintenance > Resume unfinished** in this app.

The LaunchBox download wizard is a manual step. Direct EmuMovies API login/downloads and automatically starting that wizard are not implemented. The app does not need your password for this workflow.

## Updating a portable installation

Extract this release to its own folder. Keep your old installation and data until you have verified the new version. To reuse an existing app data directory, close the old app first, keep a backup, and launch the new executable with `--data-dir` pointing to that directory. Do not run both versions against the same data simultaneously.

Read [README.md](../README.md) for setup and [IMPLEMENTATION.md](IMPLEMENTATION.md) for supported formats and remaining limits. Game files, saved accounts, and FFmpeg are not included in the distribution.