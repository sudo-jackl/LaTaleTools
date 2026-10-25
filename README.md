LaTale Tools
---

Requires the .NET 10 SDK (all projects target `net10.0`).

The app targets Windows, macOS, and Linux. SkiaSharp supplies Windows/macOS native libraries transitively; the explicit Linux native-assets package adds Linux support without replacing them.

This project provides utilities for extracting various file formats for the MMORPG LaTale.

Currently supported file formats:
* SPF: uncompressed archive file
* LDT: list file (items, achievements etc; typed)
* TBL: general descriptor of the game's sprites

The project also provides a web server for previewing the game's content. From this repository's directory, run:

```sh
git submodule update --init --recursive
dotnet build LaTaleTools.sln
LaTaleBinaryPath="$PWD/../binary" dotnet run --project LaTaleTools.WebApp --launch-profile http
```

In Windows PowerShell, set the environment variable before running:

```powershell
$env:LaTaleBinaryPath = (Resolve-Path ../binary).Path
dotnet run --project LaTaleTools.WebApp --launch-profile http
```

Open http://localhost:5126. Set `LaTaleBinaryPath` to your SPF directory (or configure the same key with .NET user secrets). Only that directory's immediate SPF files are loaded.

On Arch/CachyOS, the SDK also needs `aspnet-targeting-pack` installed. The web app includes Linux native SkiaSharp assets for sprite previews.

For automated checks, from this repository's directory:

```sh
dotnet test LaTaleTools.sln
# Build Debug first; runs the compiled library against local SPF samples.
dotnet fsi scripts/SmokeTest.fsx ../binary
# Also check previews/downloads against a running viewer using the same samples.
dotnet fsi scripts/SmokeTest.fsx ../binary http://localhost:5126
```

The smoke test reads samples without extracting or modifying them. HTTP checks use representative files from the supplied Chinese client set. Failures exit nonzero; missing TBL images and crops outside PNG bounds are reported separately because these inconsistencies already occur in the samples. No game assets are needed for the unit tests.
