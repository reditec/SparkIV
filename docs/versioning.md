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

## Release checklist

1. Set `<Version>` in `src/Directory.Build.props`.
2. Turn the "Unreleased" heading in the README changelog into the version.
3. Commit, tag `v<version>`, publish the release on GitHub (pre-release for alpha and beta).
