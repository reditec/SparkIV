# GTA IV (PC): Placements (`.wpl`, binary)

Applies only to **GTA IV, PC version** (with TLAD and TBoGT). Overview of all formats: [../data-support.md](../data-support.md).

A `.wpl` defines which model stands where in the world. The loose files are under `pc/data/maps/**`; more are inside the map's `.img` archives.

## Header

| Offset | Type | Content | Source |
|---|---|---|---|
| 0 | uint32 | Version, always 3 | `CutsceneLocation.ReadPlacements` |
| 4 | uint32 | Number of instances | ibid. |
| 68 | – | Start of the instances, 48 bytes each | ibid. |

## Instance (48 bytes)

| Offset | Type | Content | Status | Source |
|---|---|---|---|---|
| 0 | 3 × float | Position x, y, z | used | |
| 12 | 4 × float | Rotation as quaternion x, y, z, w. The game reads it **inverted**: the model's rotation is the conjugate. | used | Comparison with game frames |
| 28 | uint32 | Hash of the model name (`GxtFile.Hash`, Jenkins one-at-a-time, lower case) | used | |
| 32 | uint32 | **Instance flags**, see below | partly used | GTAIV.exe 0x9D76C0 (field 8 of the entry) |
| 36 | int32 | Index of the distant version (LOD) in the loose `.wpl` of the same name; −1 = none | used | |
| 40, 44 | – | unknown | **unclear** | |

### Instance flags (offset 32)

When creating the object, the game copies three bits into its flag field (+0x28). Found in `Entity_CreateFromInstance` (GTAIV.exe 0x9D76C0, name from `ApplyCutsceneFindings.java`):

| Instance bit | Object +0x28 | Meaning | Status |
|---|---|---|---|
| 0x80 | \|= 4 | The lamp flag 0x4000 of the model lights takes effect. The exact effect is open. | ignored |
| 0x100 | \|= 8 | **Lamps of this object may cast shadows.** Only together with the lamp flag 0x8000 (see [2dfx-lights.md](2dfx-lights.md)) is a lamp drawn as a shadow lamp. | used (since 2026-10-22) |
| 0x200 | \|= 2 | unknown | **unclear** |
| 1, 4, 0x20, 0x40 | Bits in the object field +0x24 (0x1000000, 0x100000, clears 0x20000000, clears 0x80000000) | unknown | **unclear** |

Passing to the lights: `Entity_AddModelLights` (0xC1DB60) passes bits 1, 2 and 3 of +0x28 on to `Light_FromEffect` (0xC1E200).

**Evidence in the image:** In bc_3, the wall lamp at the pier has the lamp flag 0x8000, but its instance does not have 0x100. In the game video it has the orange glow of the normal lamp. With the shadow program it disappeared in SparkIV; with the instance condition it is back.

In SparkIV: `MapIndex.LampShadows`, `LocationObject.LampShadows`, in the light record `shadows`.

## How far a distant version is drawn

The game draws a distant version (LOD, its own LOD in turn, a district's `slod…`) as far as its `.ide` distance (the
third field of its `.ide` line) **times the LOD view distance** of the graphics settings:

- **Setting:** `[QUALITY SETTINGS] Set LOD view distance (0-99)` (command line `viewdistance`), stored at GTAIV.exe
  0x01160E94.
- **Factor:** 0x59EEF0 and 0x59F200 set 0x0118D838 = setting / 99 × 1.275 + 0.725. That is 0.725 at the lowest
  setting and **2.0 at the highest** (99, shown as 100 in the menu).
- **Not found:** the code that reads the factor; it lies in code Ghidra hasn't disassembled.
- The detail distance (0x01160E98) gives another value, setting / 99 × 100 + 10 (0x0103F714, via 0xAEBD70); what it
  scales is not checked.

**Evidence:** trace `GTAIV.1.trace` (bc_3), section 2, frame 786.
- The game shows the roofs of the tower blocks 752 m off across the water. Their distant version `lod_bs_ten01` has
  an `.ide` distance of 550 m, so it is drawn only with a factor of at least 1.37.
- With the factor 2.0, SparkIV draws them too. The reference's difference drops from 11.00 to 10.66; the other 22 of
  bc_3 and et01_ba stay within 0.03.
- The traces were recorded at the highest view distance (the user's setting), so the factor is 2.0.
- **Open:** in the same frame `LOD_mhb_cablebas` (also 550 m) 843 m off is not drawn, its district's super LOD
  instead. With 2.0 it would be in reach (1100 m), so another rule decides there (possibly streaming).

### What the cutscene camera cuts off, and which version of a group is drawn

Found by leaving out draws of the same frame (`d3dretrace --ignore-calls`): the buildings behind the cranes that
SparkIV missed are the district super LODs `SupLod_BkN_03` and `SupLod_BkN_08` (draws 136285579 and 136285573).
Their centres are 844 and 779 m off.

- **Far plane:** the cutscene's draw distance is the camera's far plane. The trace's projection gives 780 m there:
  P22 = −1.0000641, P32 = −0.05, near 0.05 m. A far plane cuts by **depth along the view**, not by distance. At the
  picture's sides that reaches up to a quarter farther.
- **Groups:** a distant version counts if any of it (its `.ide` bounding sphere) lies within the draw distance, not
  only its centre.
- **Parents:** a parent whose children are only partly in reach stays drawn, and only the children in reach are
  drawn with it.

**In SparkIV:**
- `CutsceneLocation.LodViewDistance` = 2.0, the highest setting, for the distant versions only.
- `Wanted` and `Choose` (groups and parents) and the page's `drawReach` (the depth along the view) follow the rules
  above.
- bc_3 s2 f786 10.66 → 10.59, s1 f78.75 −0.03, s3 f357.25 +0.09 (only the sea's mirror); et01_ba unchanged.
- **Open:** with the factor also on the models themselves (and without the 150 m limit `OutsideModels`), bc_3's pier
  roof came back (s2 f436.75, s3 f506.5), but et01_ba's toll booth lost its tinted panes.
