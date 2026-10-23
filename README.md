# SparkIV

A file browser, viewer and editor for **Grand Theft Auto IV** and **Episodes from Liberty City** (PC).

This fork rebuilds SparkIV as a **WinUI 3 app on .NET 10**. Its viewers render the game's data the way the game does. The cutscene viewer, for example, reproduces the game's lighting, shadows and post effects. Findings about the file formats and the renderer are documented under [`docs/`](docs/GtaIV/Pc/data-support.md).

## Features

- Browse the game's IMG and RPF archives of GTA IV, The Lost and Damned and The Ballad of Gay Tony.
- Viewers:
  - **textures** (`.wtd`);
  - **models** (`.wdr`, `.wdd`, `.wft`), in 3D;
  - **animations** (`.wad`);
  - **audio banks** (`.ivaud`), with playback and export;
  - **scripts** (`.sco`), decompiled or disassembled;
  - **cutscenes** (`.cut`), with the cast, camera, lights, sound and their location;
  - **collision** (`.wbn`, `.wbd`);
  - text and data files, XML and a hex view.
- An edit mode for changing game files. It has to be switched on after a warning. The original of every file is backed up before SparkIV first writes to it.

## Requirements

- Windows 10 (1809) or later, x64.
- A **legally owned copy** of GTA IV and/or Episodes from Liberty City for PC.
  - SparkIV reads the game's own files.
  - The key the game uses to encrypt its archives is read at run time from your game's executable.
  - No game data and no key is part of this repository.

## Installing

Download `reditec.SparkIV-win-Setup.exe` from the [releases](https://github.com/reditec/SparkIV/releases) and run it. SparkIV checks for new versions at start and updates itself with one click.

## Building

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download). Visual Studio with the Windows App SDK (WinUI) tools is optional.
2. Build the solution:

   ```
   dotnet build SparkIV.sln
   ```

   or open `SparkIV.sln` in Visual Studio and run the `SparkIV` project (x64).

## History and credits

- **aru** (Arushan) wrote SparkIV and RageLib in 2008–2009.
- **Ahmed Walid** ([ahmed605](https://github.com/ahmed605/SparkIV)) continued it.
- **Silent Spud** ([SilentSpud](https://github.com/SilentSpud/SparkIV)) moved it to modern .NET.
- **reditec** has rebuilt it as a WinUI app with new viewers since 2026.

Third-party components:
- [SharpZipLib](https://github.com/icsharpcode/SharpZipLib) (MIT)
- [Ookii.Dialogs](https://github.com/ookii-dialogs/ookii-dialogs-winforms) (BSD-3-Clause)
- [Windows App SDK](https://github.com/microsoft/WindowsAppSDK) (MIT)
- [Velopack](https://github.com/velopack/velopack) (MIT), installer and updates
- zlib, through .NET's `System.IO.Compression`

## Changelog

Versions follow [Semantic Versioning](docs/versioning.md); 1.0 will be the first version that fully supports GTA IV (PC).

### v0.9.0-alpha.1 (WinUI version, reditec)

* New WinUI 3 app on .NET 10.

* Installer; SparkIV updates itself from the GitHub releases.

* New viewers for models, animations, cutscenes and collision; the audio viewer can play and export multichannel banks.

* Removed components that were closed or licensed incompatibly with the GPL (ragezip.dll, Model3DViewer, the old WaveOut player).

### What's new in v0.8.56 (Beta)

* Fix Update checker returning old version.

### What's new in v0.8.52 (Beta)

* Added the support for GTA IV (Complete Edition) v1.2.0.32 (for Steam and non-Steam versions).

### What's New In V0.8.1 (Beta)

* Added partial Darg & Drop support.

* New update system.

* Improved support for **GTA IV** ***1.0.8.0*** & **EFLC** ***1.1.3.0***.

* Added fallback folder browser dialog for selecting game folder if the ***registry path*** was deleted.

* Added file deleting ability by clicking (***Delete Key***) in the keyboard.

### What's New In V0.8.0 (Beta)

* New icons

* Support for GTA IV 1.0.8.0 & GTA EFLC 1.1.3.0

* Windows 10+ Support 

* New style

* Fixed some bugs

* Initial drag & drop support (WIP)

* Fix checking for updates not working

## License

SparkIV is free software under the **GNU General Public License, version 3 or (at your option) any later version**. See [`LICENSE`](LICENSE).

The original copyright notices are kept in the source files. This version was **modified by reditec in 2026**; every change and its date is recorded in the Git history of this repository.

## Disclaimer

SparkIV is an unofficial fan project. It is not affiliated with, endorsed or sponsored by Rockstar Games or Take-Two Interactive. Grand Theft Auto, GTA and related names are trademarks of Take-Two Interactive Software, Inc.

SparkIV is provided without any warranty. Back up your game before you change its files.
