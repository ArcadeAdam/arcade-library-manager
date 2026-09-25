# Arcade Library Manager — portable Windows application

Status: original design specification and roadmap. A working portable Windows preview now exists. See [IMPLEMENTATION.md](IMPLEMENTATION.md) for shipped behavior and limitations, and [README.md](../README.md) for setup. Requirements below include future work and are not all implemented in version 0.2.

## Intended outcome

A portable Windows app that updates TeknoParrot, identifies and installs every available missing supported game/version, registers valid launch paths, synchronizes an existing LaunchBox TeknoParrot platform, creates missing theme videos from the owner's artwork and gameplay snaps. Controls remain user-managed.

A single reviewed operation plan authorizes the selected batch. Routine download, validation, extraction, registration, media acquisition and rendering do not ask again. Ambiguous identity or a conflicting user edit pauses only the affected item while independent work continues.

## Setup UI

| Setting | Behavior |
| --- | --- |
| Existing ROM locations | Multiple local folders, Downloads folders, removable drives and UNC paths; enable/disable each independently. |
| TeknoParrot location | Validate installation and discover GameProfiles, UserProfiles, Metadata and component versions. |
| New-game destination | Separate from existing ROM sources; do not require duplicating already usable files. |
| LaunchBox location | Discover existing TeknoParrot platform name, emulator GUID, data layout and per-media folder overrides. |
| Online ROM archive | One or more source URLs; Archive.org item/collection adapter first, with linked-item discovery and explicit source priorities. |
| Cache and staging | Optional separate locations; show compressed, extracted and temporary peak space per volume. |
| Media preferences | Provider priority, preferred regions/languages, image categories, missing-only/update mode, video quality and download limits. |
| Theme references | Discover LaunchBox's configured theme folder; allow selecting examples or an editable template. |
| Theme output | Resolution, aspect ratio, duration, FPS, audio/volume, encoding quality and CPU/GPU preference. |
| Job limits | Download count, bandwidth, per-drive I/O concurrency, automatic rendering acceleration and optional keep-awake while jobs run. |

Use folder pickers, validation and remembered settings. A shareable preset excludes credentials and account identifiers. Settings can travel with the portable app; saved credentials remain bound to the Windows user who stored them.

## Main workflow

1. Scan: load the supported TeknoParrot catalog, existing profile data and cached local inventory.
2. Match: identify exact editions and dependencies locally before consulting online sources.
3. Review: show add/update/reuse/skip/needs-review, source, confidence, transfer size and disk impact.
4. Run: update emulator, download, verify, extract, validate, register, synchronize and acquire/render media as dependency-ordered jobs.
5. Results: show what changed, what was reused, missing files, additional setup, errors, backups and per-item retry/rollback.

The game grid has separate status columns for files, TeknoParrot registration, LaunchBox entry, artwork, snap and theme. A launch-file check is not described as play-testing. Search/filter supports missing games, failed jobs, variants, subscriptions and manual setup.

## Catalog, matching and installation

- TeknoParrot profile ID is the stable key. Keep title, region, release revision, hardware, loader and emulator family separate.
- Map a profile to a payload recipe; several profiles may legitimately share a payload. Never download the same payload twice merely because two loaders use it.
- Versioned recipes specify accepted hashes, archive members, primary/secondary launch files, parent ROMs, BIOS, CHDs, manifests, dongles and any additional data. Recipes contain data, not executable remote scripts.
- Distinguish filename matches from verified content matches. Wrong editions must not be accepted because their title or executable name matches.
- Different CHD sector formats can be incompatible even with matching raw SHA1. Validate the media properties required by each emulator family.
- Index incrementally using cached paths, size and modification data, then hash relevant candidates. Avoid repeated recursive scans of all files on 28 TB drives. Skip reparse-point loops and app-owned staging/cache.
- Default to released profiles; exclude DevOnly entries. Show subscription/manual-setup requirements separately from installation success.
- Preserve ZIPs when the emulator expects a ROM ZIP. Extract only the relevant outer distribution package.
- Resume HTTP transfers with validators such as ETag/Last-Modified where supplied; handle servers without range support and completed partial files correctly.
- Validate expected size/checksum, archive paths, links and extraction result. Never run an executable found in an archive as part of discovery.
- Use app-owned extraction staging and a completion journal. Commit only verified results; maintain ownership records so rollback never deletes pre-existing user files. Cross-volume commits require copy plus verification before completion.
- Jobs survive app restart through a persistent SQLite journal, worker leases and bounded retries. Track download, hash, extraction, verification and registration as separate phases.
- Classify network/authentication failures, disk-space failures, missing dependencies and ambiguous matches; avoid blind infinite retries.

## Emulator update and profile preservation

Use TeknoParrot's official component manifest and release assets, verify published checksums and back up the affected installation/configuration first. Defer binary replacement while TeknoParrot or a game using it is running.

Create missing profiles from the current templates and fill validated paths. Preserve existing controls, saves, account settings and custom fields. Changing a managed path uses a before/after comparison and checks for concurrent edits. Keep originals and a rollback journal.

If new games need an account/card identifier, reuse only an explicitly configured compatible local account value or leave the item marked additional setup required. Never invent identifiers or copy another person's account into a shared preset.

## LaunchBox synchronization

- Operate on the existing platform and emulator association; do not create a second TeknoParrot platform by default.
- Build an identity crosswalk between TeknoParrot profile ID, LaunchBox game GUID and Games Database ID. These identifiers are different and serve different purposes.
- Preserve LaunchBox GUIDs, favorites, play count/history, ratings, playlists, custom fields, manual notes and curated metadata/art.
- Detect both Game and AdditionalApplication entries. Preserve DemulShooter and other before/after-launch hooks, including their execution order and wait flags. Provide a variant policy: preserve current structure by default; optionally separate entries or grouped alternate applications for new variants.
- Flag existing duplicate profile identities for review. Do not merge or delete duplicates silently.
- Default update policy: correct managed launch paths and fill missing game metadata from the local catalog. Refresh selected existing fields only under the user's chosen overwrite policy.
- Prefer a documented in-process plugin API for supported operations. If direct XML updates are used, queue the commit while LaunchBox/Big Box is open, take a backup, verify input files have not changed, write atomically and validate the result.
- Respect platform folder overrides, relative paths, emulator flags and command formatting. Support capability/version detection for metadata cache formats instead of assuming one XML or SQLite schema forever.
- Media naming follows the current LaunchBox association rules; retain source IDs and hashes internally to prevent duplicate generated outputs.

### Findings from this machine

The existing platform has 448 Game records, 36 AdditionalApplication records and 196 AlternateName records, with ScrapeAs=Arcade. Of those additional applications, 34 are alternate TeknoParrot profile versions and 2 are DemulShooter BAT hooks. 444 main games already have Games Database IDs. Main games and versions represent 465 unique TeknoParrot profiles; synchronization must consider the entire installed library, not only the latest download batch. Game paths use ../Emulators/TeknoParrot/UserProfiles/*.xml. The shared emulator uses --profile=%romfile%.xml with filename-only handling. One existing profile identity, ChaosCodeNSOC103, occurs twice and must be preserved/reviewed.

The configured theme folder resolves to N:\LaunchBox\Videos\TeknoParrot\Theme; snaps resolve to N:\LaunchBox\Videos\TeknoParrot. Folder overrides must be read from LaunchBox rather than guessed from an example path. These are observations for tests, not hardcoded defaults in a shared build.

## Local metadata and media

LaunchBox handles artwork downloads. The separate Emumovies snap scrapper page controls official Sync for gameplay-video downloads and imports only missing videos. The app reads its existing local metadata for game synchronization, and matches local art, logos, snaps, cutouts, and theme videos for generation. EmuMovies credentials remain in official Sync. There is no artwork downloader or metadata package import. Source assets remain unchanged. Bezels retain their separate local setup workflow.

## Generated theme videos

Only generate a theme when a matching existing/downloaded theme is absent, unless the user explicitly chooses to replace an app-generated theme.

Select an example/template, inspect technical properties, and use an editable composition built from available artwork, clear logos and gameplay snaps. The first useful renderer is FFmpeg-based: background, subtle pan/zoom, gameplay window, logo/title, fades, loop transition and controlled audio. Generation here is composition from real assets; it does not require generative-AI video.

Reference videos can guide format, pacing and template layout. Do not promise exact automatic recreation of arbitrary edited videos. Allow a preview and template choice, then apply that template to the remaining batch without repeated prompts.

- Preserve source aspect ratio; support both 4:3 and widescreen cabinets. Use crop/fit/letterbox intentionally.
- Choose an output preset, rather than inheriting inconsistent old codecs from every example.
- Require a nonempty gameplay video snap for every generated theme and preview. Skip missing snaps; never fall back to an artwork-only theme.
- Use deterministic input/template fingerprints; rerender only when relevant assets or template settings change.
- Never overwrite curated themes by default. Mark app-generated outputs and allow a better downloaded theme to supersede them under the media policy.
- Validate duration, video/audio streams, dimensions and decodability before linking the output in LaunchBox. Generate a thumbnail/preview and use temporary files for failed or cancelled renders.
- Keep rendering concurrency separate from downloads and disk extraction; provide a play mode with lower resource use.

### Observed reference formats

The configured theme folder has 546 files and the snap folder has 583 files; these are not counts of uniquely matched games. Three sample themes span 1272x952/29.97fps/~40s/MPEG4, 1280x720/30fps/~46s/H.264, and 1920x1080/60fps/~31s/H.264. This confirms that output aspect ratio, quality and duration need explicit presets.

## Architecture and delivery

Portable Windows x64 desktop UI on a currently supported .NET LTS release, published self-contained. Separate UI, worker engine and provider adapters. Use SQLite for inventory, identity crosswalks and durable jobs. Use FFmpeg/ffprobe for video work and an archive library or controlled extraction tool for supported archive formats.

Suggested modules: CatalogService, LocalInventory, SourceProviders, MatchPlanner, DependencyResolver, DownloadManager, ArchiveInstaller, TeknoParrotUpdater, TeknoParrotProfileWriter, LaunchBoxAdapter, LocalMediaDiscovery, ThemeRenderer, JobStore, Diagnostics.

Distribute an unconfigured app with third-party notices and the applicable runtime/tool redistribution materials. Do not include the owner's ROMs, credentials, saved card IDs or private configuration in the friend-shareable package. Provide a versioned data-recipe update mechanism and app-update checks, with validation and rollback.

## Implementation order and acceptance gates

1. Core desktop settings, inventory and review plan; durable jobs; exact-version/dependency validation; official updater and safe TeknoParrot registration.
2. LaunchBox identity matching and preview; backup/atomic merge; preservation of history, alternate applications and media overrides; provider integration for metadata/art/snaps.
3. Reference/template selection, theme preview and batch rendering; diagnostics, recovery, portable release packaging and friend-machine testing.

Acceptance tests use temporary fixture libraries first. They must demonstrate: rerunning creates no duplicates; interruption resumes safely; a missing flash or dongle cannot be marked ready; wrong editions are rejected; controls/history survive; changed files are not overwritten after an outdated preview; an open LaunchBox commit is deferred; secrets never enter exports; wrong media matches are reviewable; rendered videos decode; rollback removes only app-owned additions.

The current machine's ongoing download workers remain separate and must not be interrupted or absorbed into a prototype.

## References

- Official TeknoParrot components: https://teknoparrot.com/api/updates/components
- LaunchBox metadata/media workflow: https://feedback.launchbox-app.com/en/help/articles/7997304-using-games-database-media-in-launchbox
- LaunchBox plugin API: https://pluginapi.launchbox-app.com/
- LaunchBox developer response on metadata/images: https://forums.launchbox-app.com/topic/54163-is-there-a-public-way-to-get-images-from-the-launchbox-games-database/
- FFmpeg filters: https://ffmpeg.org/ffmpeg-filters.html
- .NET portable/self-contained publishing: https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview

Implementation evidence: N:\Emulators\TeknoParrot-audit. The installation validated 479 original game profiles and 510 protected configuration files as unchanged, and demonstrated game-specific cases including shared Densha payloads, alternate loaders, PCSX2 manifests and the missing Dead Eye flash ROM.



