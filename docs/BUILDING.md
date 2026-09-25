# Build from source

The source archive contains application code, tests, public recipe data, and documentation. It does not contain game payloads, credentials, emulator binaries, or a working machine's settings.

Use Windows x64, PowerShell 7, and the .NET SDK recorded in `global.json`. Install that SDK yourself or run `scripts\bootstrap.ps1`, which downloads it from Microsoft's release manifest and verifies SHA512.

From the project directory:

```powershell
.\scripts\build.ps1 -FfmpegPath 'C:\YourLaunchBox\ThirdParty\FFMPEG\ffmpeg.exe'
```

The build runs isolated integration fixtures, publishes the self-contained Windows app, checks settings and all UI pages, and creates portable and source ZIPs plus SHA256 files in `artifacts`. Without an FFmpeg path, media fixture coverage requiring the real encoder is skipped and recorded as such. The app can still be built.

`-SkipTests` skips the integration suite for an already-tested source tree; published settings/UI checks still run. `-SkipPackage` publishes without creating the ZIP. A previous publish folder containing a `data` directory is refused so a used portable copy cannot accidentally be distributed.

The recipe generator is an optional developer tool, not required to build. It needs an independently prepared audit directory (the four files listed by `python scripts/build-recipes.py --help`) and an existing TeknoParrot directory. The bundled recipes can be used as supplied. No machine-specific audit files are included in the source archive.

See `docs/THIRD-PARTY-NOTICES.md` and `docs/licenses` for dependencies. FFmpeg is configured separately and is not redistributed here.

