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
