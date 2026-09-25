# Arcade Library Manager 0.2.9

Fixes misleading FFmpeg discovery failures when ffmpeg.exe is present but ffprobe.exe is missing. Errors name the missing executable and its expected folder. Setup accepts an executable, its containing folder, or an extracted build folder with a bin subfolder, including quoted paths.

Make themes checks the tool pair before starting a batch or preview. A shared missing dependency produces one actionable error and leaves selections available for retry. Existing themes and games without video snaps retain their preservation and skip behavior.

Use a complete FFmpeg build containing both ffmpeg.exe and ffprobe.exe. LaunchBox installations containing only ffmpeg.exe cannot provide the required pair. A separate tools folder avoids depending on files managed by LaunchBox updates.

Validation covers tool discovery and diagnostics, the existing rendering and integration suite, desktop smoke checks, and a gameplay-backed preview using the repaired local tool installation.
