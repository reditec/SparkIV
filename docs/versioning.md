# Versioning

SparkIV follows [Semantic Versioning](https://semver.org/): `MAJOR.MINOR.PATCH`, with an optional pre-release suffix.

## Where the version lives

One version for SparkIV and its libraries (RageLib, Scruff, Resoursaur), set in
[`src/Directory.Build.props`](../src/Directory.Build.props) as `<Version>`. The build turns it into:

- the file version, e.g. `0.9.0.0` (numbers only; the suffix is dropped);
- the product version, e.g. `0.9.0-alpha.1+<commit>`.

A release is tagged in Git as `v` + version, e.g. `v0.9.0-alpha.1`.

## When which number changes

| Change | Example | Meaning |
|---|---|---|
| PATCH | 0.9.0 → 0.9.1 | Bug fixes only, including rendering fixes from the roadmap that add no new capability. |
| MINOR | 0.9.x → 0.10.0 | A milestone: a new format or viewer, a new import/export, or a completed part of the 1.0 criteria below. |
| MAJOR | 0.x → 1.0.0 | GTA IV (PC) fully supported, see below. After that only for a real break. |

After 0.9 comes 0.10, 0.11 and so on; the numbers are compared one by one, so 0.10 is newer than 0.9.

## Pre-releases

- `-alpha.N`: incomplete, for testing; formats or features may still change.
- `-beta.N`: feature-complete for this version, only fixes until the release.
- Without a suffix: the release.

On GitHub, alpha and beta releases are marked as "Pre-release".

## Criteria for 1.0: GTA IV (PC) fully supported

1. **Every file opens.** A scan over all game files of GTA IV (PC) finds no file type that SparkIV cannot open and display.
2. **No open format data.** In [data-support.md](GtaIV/Pc/data-support.md) every GTA IV entry that affects the image or playback is **used**; none is left **unclear** or **ignored**.
3. **Cutscenes as in the game.** All 153 GTA IV cutscenes look like the game; the roadmap has no open item for GTA IV.
4. **Editing by export and import.** Formats that are reasonable to edit can be exported, edited in an external tool and imported again. Proposed set (to be confirmed):
   - textures (`.wtd`) to and from DDS/PNG;
   - audio (`.ivaud`) to and from WAV;
   - text and data files (`.dat`, `.ide`, `.ipl`, `.xml` …), edited in place;
   - models, collision and animations through openFormats (see 5).
5. **Full openFormats compatibility.** Every openFormats type that OpenIV offers for GTA IV (PC) can be exported and imported, and files exported by OpenIV load in SparkIV and the other way round. The list of types is still to be recorded (models `.odr`/`.odd`/`.oft`, collision `.obn`/`.obd`, animations `.onim` …).

The episodes (The Lost and Damned, The Ballad of Gay Tony) are not part of 1.0; their full support follows in 1.x.

## Updates

An installed SparkIV checks the GitHub releases at start ([Velopack](https://velopack.io/), `Services/Updates.cs`).
If there is a newer version it shows "Update available"; one click downloads it and restarts SparkIV.

- An alpha or beta installation also gets alpha and beta releases; a release installation only releases.
- The package id is `reditec.SparkIV`; it names the install folder `%LocalAppData%\reditec.SparkIV`. It must never
  change, or installed versions no longer find their updates. It is not `SparkIV`, because the settings are kept in
  `%LocalAppData%\SparkIV` and an uninstall removes the install folder.
- SparkIV run from its build folder (Visual Studio) doesn't check for updates.
- For testing, the environment variable `SPARKIV_UPDATE_SOURCE` names a local folder with packed releases
  (the output of `tools/release.ps1`) instead of GitHub.

## Release checklist

1. Set `<Version>` in `src/Directory.Build.props`.
2. Turn the newest changelog heading in the README into the version.
3. Commit and merge into `master`.
4. Run `tools/release.ps1`. It builds SparkIV (x64, self-contained) and packs it into `release/out`.
5. On GitHub, create a release with the tag `v<version>` on that commit (mark alpha and beta as "Pre-release")
   and upload every file from `release/out`. Users install with `reditec.SparkIV-win-Setup.exe`.
