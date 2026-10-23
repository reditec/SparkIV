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
- zlib, through .NET's `System.IO.Compression`

## License

SparkIV is free software under the **GNU General Public License, version 3 or (at your option) any later version**. See [`LICENSE`](LICENSE).

The original copyright notices are kept in the source files. This version was **modified by reditec in 2026**; every change and its date is recorded in the Git history of this repository.

## Disclaimer

SparkIV is an unofficial fan project. It is not affiliated with, endorsed or sponsored by Rockstar Games or Take-Two Interactive. Grand Theft Auto, GTA and related names are trademarks of Take-Two Interactive Software, Inc.

SparkIV is provided without any warranty. Back up your game before you change its files.
