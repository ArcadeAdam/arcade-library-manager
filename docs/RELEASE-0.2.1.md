# Version 0.2.1

- Renamed the TeknoParrot installation field to **Teknoparrot emulator installed location**.
- Added ghosted example paths throughout Setup, including multiline ROM sources. These examples match the current test layout but are display hints only; saved values are preserved and blank fields remain blank until edited.

## Testing media alongside a separate installation job

Use **Media studio** with games that are already installed. Local scans and reference inspection are read-only. Theme previews use the configured cache; missing artwork and themes use LaunchBox's configured media folders. Start with one or a few games and keep Quiet mode enabled to limit rendering work.

Close LaunchBox while this app commits media or prepares its playlist. Open LaunchBox for the EmuMovies wizard, complete its download, and close it before resuming work here.

Wait for the separate installer before using this app to install/repair games, update TeknoParrot, or apply bezels. Those operations can share emulator, profile, and ROM files. The app's job lock does not coordinate with a separate PowerShell installer. A complete LaunchBox synchronization is best performed after the remaining profiles have been installed.

The interface uses the FFB Blaster MASTER beta.16 palette: near-black purple panels, magenta/cyan accents, and lavender text. Colors were read from the supplied portable package without launching it.