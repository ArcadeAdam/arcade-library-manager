# Third-party notices — Arcade Library Manager portable Windows x64

This distribution includes the components below. Keep this document and the accompanying `licenses` directory with copies of the application. This document covers third-party components; it does not assign a license to Arcade Library Manager's own source code or binaries.

The versions below were verified from the resolved NuGet dependency graph and the `.version` files of the bundled .NET runtime. The .NET SDK used for building is not part of the portable application.

| Component | Version | License / notice | Primary source |
| --- | --- | --- | --- |
| Microsoft .NET runtime, host, and base libraries (`Microsoft.NETCore.App`, Windows x64) | 10.0.12 | [Distribution license](licenses/dotnet-10.0.12-DISTRIBUTION-LICENSE.txt), [MIT source license](licenses/dotnet-runtime-LICENSE.TXT), [runtime notices](licenses/dotnet-runtime-THIRD-PARTY-NOTICES.TXT), [distribution notices](licenses/dotnet-10.0.12-THIRD-PARTY-NOTICES.txt) | [.NET runtime source at the shipped revision](https://github.com/dotnet/dotnet/tree/95017c711e6afc1085133d440e42b4bd78155701/src/runtime) |
| Microsoft Windows Desktop runtime (`Microsoft.WindowsDesktop.App`, Windows x64), including Windows Forms and WPF | 10.0.12 | [Distribution license](licenses/dotnet-10.0.12-DISTRIBUTION-LICENSE.txt); Windows Forms [MIT license](licenses/dotnet-winforms-LICENSE.TXT) and [notices](licenses/dotnet-winforms-THIRD-PARTY-NOTICES.TXT); WPF [MIT license](licenses/dotnet-wpf-LICENSE.TXT) and [notices](licenses/dotnet-wpf-THIRD-PARTY-NOTICES.TXT) | [Windows Forms source](https://github.com/dotnet/dotnet/tree/95017c711e6afc1085133d440e42b4bd78155701/src/winforms), [WPF source](https://github.com/dotnet/dotnet/tree/95017c711e6afc1085133d440e42b4bd78155701/src/wpf) |
| Microsoft.Data.Sqlite and Microsoft.Data.Sqlite.Core | 10.0.12 | [MIT license](licenses/dotnet-efcore-LICENSE.txt); © Microsoft Corporation. All rights reserved. | [Microsoft.Data.Sqlite package](https://www.nuget.org/packages/Microsoft.Data.Sqlite/10.0.12), [Microsoft.Data.Sqlite.Core package](https://www.nuget.org/packages/Microsoft.Data.Sqlite.Core/10.0.12), [package-declared source revision](https://github.com/dotnet/dotnet/tree/95017c711e6afc1085133d440e42b4bd78155701/src/efcore) |
| SQLitePCLRaw.bundle_e_sqlite3 | 3.0.5 | [Apache License 2.0](licenses/SQLitePCLRaw-APACHE-2.0-LICENSE.txt), [upstream notices](licenses/SQLitePCLRaw-NOTICE.txt); Copyright 2014–2026 SourceGear, LLC | [Package](https://www.nuget.org/packages/SQLitePCLRaw.bundle_e_sqlite3/3.0.5), [upstream project](https://github.com/ericsink/SQLitePCL.raw) |
| SQLitePCLRaw.config.e_sqlite3 | 3.0.5 | [Apache License 2.0](licenses/SQLitePCLRaw-APACHE-2.0-LICENSE.txt), [upstream notices](licenses/SQLitePCLRaw-NOTICE.txt); Copyright 2014–2026 SourceGear, LLC | [Package](https://www.nuget.org/packages/SQLitePCLRaw.config.e_sqlite3/3.0.5), [package-declared source revision](https://github.com/ericsink/SQLitePCL.raw/tree/96043b8cff323f21919df86a431c136655d81b4a) |
| SQLitePCLRaw.core and SQLitePCLRaw.provider.e_sqlite3 | 3.0.5 | [Apache License 2.0](licenses/SQLitePCLRaw-APACHE-2.0-LICENSE.txt), [upstream notices](licenses/SQLitePCLRaw-NOTICE.txt); Copyright 2014–2025 SourceGear, LLC | [Core package](https://www.nuget.org/packages/SQLitePCLRaw.core/3.0.5), [provider package](https://www.nuget.org/packages/SQLitePCLRaw.provider.e_sqlite3/3.0.5), [package-declared source revision](https://github.com/ericsink/SQLitePCL.raw/tree/ed046114d5a30534e13294d94d78eb73de896ad4) |
| SQLite native library (`e_sqlite3.dll`, SQLite NuGet package) | 3.53.4 | [Public-domain declaration supplied with the package](licenses/SQLite-3.53.4-LICENSE.txt). Package metadata: Copyright 2014–2026 SourceGear, LLC. | [SQLite package](https://www.nuget.org/packages/SQLite/3.53.4), [SQLite copyright statement](https://sqlite.org/copyright.html) |

| Microsoft.ML.OnnxRuntime and Microsoft.ML.OnnxRuntime.Managed (Windows x64 CPU inference) | 1.30.0 | [MIT license](licenses/ONNX-Runtime-1.30.0-LICENSE.txt), [complete third-party notices](licenses/ONNX-Runtime-1.30.0-ThirdPartyNotices.txt) | [Official NuGet package](https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime/1.30.0), [ONNX Runtime source](https://github.com/microsoft/onnxruntime) |

## Preserved license texts

The `.NET` distribution license and distribution third-party notices are unchanged copies supplied with the runtime installation used for this release. The runtime, Windows Forms, WPF, and Microsoft.Data.Sqlite source license/notice files are unchanged copies from the package/runtime-declared .NET source revision `95017c711e6afc1085133d440e42b4bd78155701`. The .NET umbrella [MIT license](licenses/dotnet-MIT-LICENSE.txt) is also included.

The SQLite public-domain declaration is copied directly from the SQLite 3.53.4 NuGet package. The full SQLitePCLRaw Apache license and NOTICE file were obtained from its package-declared source revision `ed046114d5a30534e13294d94d78eb73de896ad4`; the config package revision `96043b8cff323f21919df86a431c136655d81b4a` has matching license and NOTICE texts. These notices preserve upstream attributions, including SQLite and Microsoft Open Technologies.

The runtime notice files contain the additional third-party notices supplied by their publishers. They are retained in full, including notices for code that may not be exercised by this application. Package dependencies resolved for this Windows x64 build do not include the older SQLitePCLRaw 2.1.x packages.

## Optional tools and separately obtained content

FFmpeg and ffprobe are **not bundled**. The application can use a compatible installation supplied by the user or detected in their LaunchBox installation. Those tools retain the license terms and notices of the particular build supplied by the user.

TeknoParrot, LaunchBox, EmuMovies, game files, artwork, videos, and downloaded bezel packs are separate products or content sources. Their respective licenses and service terms remain applicable; this notice grants no additional rights in them.

Optional cutout model downloads are documented with upstream sources, full license texts, file sizes, and pinned SHA-256 hashes in [Local transparent theme cutouts](CUTOUT-MODELS.md). The model files are not bundled in the portable archive.
