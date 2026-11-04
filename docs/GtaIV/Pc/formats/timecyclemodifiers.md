# GTA IV (PC): Timecycle modifiers (`timecyclemodifiers*.dat`)

Applies only to **GTA IV, PC version** (with TLAD and TBoGT). Overview of all formats: [../data-support.md](../data-support.md).

The files `pc/data/timecyclemodifiers.dat` to `…4.dat` contain named modifiers. A room of an interior names its modifier via the name hash (see [ide-mlo.md](ide-mlo.md)); boxes in the world also use them. A line consists of the name and numbers; −1 means "unchanged".

From about the middle onwards, the column names in the file header no longer match the numbers. The following table counts the **numbers after the name from 0**, the way `TimeCycle.With` reads them.

## Columns

| Column | Name in header | Effect | Status | Source |
|---|---|---|---|---|
| 0, 1 | MinFarClip, MaxFarClip | View distance (1) | used (1) | |
| 2, 3 | MinFogStart, MaxFogStart | Fog start: 3, otherwise 2 | used | `GTAIV.3.trace`: c41.x = 3 at em_4 |
| 4–8 | AmbientLight0 RGBA, multiplier | Ambient light from above: RGB/255 × mult., alpha as mix share (assumed) | used | `GTAIV.2.trace` (bc_1): c37 exact |
| 9–13 | AmbientLight1 RGBA, multiplier | Ambient light from below | used | ibid. |
| 14–18 | DirectionalLight RGBA, multiplier | Sun (usually 0 indoors) | used | ibid. |
| **19** | **AmbientDownMult** | **Share of ambient light for characters and movable objects** (cutscene props, vehicles), not for the map. See below. | used (since 2026-10-22) | GTAIV.exe 0xAEF820 (field +0x48 of the modifier), `GTAIV.3.trace` |
| 20–23 | Fog RGBA | Colour of the far fog | ignored | |
| 24–27 | NearFog RGBA | Colour of the near fog: RGB/255 × column 66; alpha/255 = share | used | `GTAIV.3.trace`: fog colour (1.625, 1.425, 1.185) = (59, 52, 43)/255 × 7.0 |
| 32, 33 | Exposure, ExposureMultiplier | Exposure = 32 × 33 | used | |
| 34 | BrightPassThreshold | Bloom threshold | used | |
| 35 | MidGreyValue | Tone mapping | used | |
| 36 | IntensityBloom | Bloom | used | |
| 37–39, 40–42 | ColourCorrect, ColourAdd | Colour correction | used | |
| 43, 45 | Desaturation, Gamma | | used | |
| 51–53 | LumMin, LumMax, LumAdaptDelay | Eye adaptation | used | |
| **54** | **GlobEnvRefl** | **`globalScalars.w` = 0.6 × value:** strength of the reflected environment in the specular. em_4 and BernieCrane have 0, so no reflection indoors. | used (since 2026-10-22) | `GTAIV.3.trace`: c39.w = 0 in Elizabeta's apartment; bc_1 video: without the value, Bernie's hallway wallpaper showed bright stripes and was twice as bright (69 vs. 32) |
| 31 | PostFxStrength | Share of the post effect values (columns 32 to 53) for a box; 0 leaves them out | used for boxes | GTAIV.exe 0xAF2960 |
| 64 | AmbientOcclusionMult | `globalScalars2.z`: how much of the AO counts, see below | used | `GTAIV.3.trace` (c40) |
| 66 | (header: SkyMultiplier, shifted) | Factor of the near fog colour | used | `GTAIV.3.trace` |
| 67 | PedAmbOccHack | `globalScalars2.y`: the same for peds only | used | `GTAIV.3.trace` (c40) |
| others | Temperature, Contrast, Far and DOF columns, last column (em_4 255, BernieCrane 0) | | **unclear** or ignored | |

## Boxes in the world

A `.wpl` holds, after its placements and three more kinds of record, the time cycle modifier boxes: 44 bytes each (count at +20 of the header). Floats min x y z, max x y z, a value not read here, the range; then the start and end hour and the modifier's name hash.

GTAIV.exe works them out at the camera:
1. **By distance (0xAEF890):** 1 inside, then 1 − d / range, where the height counts three times and reaches a third of the range.
2. **By hour (0xAEF9E0):** 1 between start and end hour, fading in over the hour before the start and out over the end hour. Start equal to end: always.
3. **Per modifier (0xAEE7E0):** the strongest of its boxes, as distance × hour.
4. **Mixing (0xAF2960):** colours by their alpha × strength, colour and multiplier apart. So a modifier with a sun of 0 × 0 takes both down, and the light falls with the square of what is left.

**Example:** The harbour of the intro lies in `KsS1nomoon1` to `3` (`brook_s.wpl`, 21 to 5 o'clock, sun 0 × 0). At 05:15 the hour leaves 0.75. In `GTAIV.3.trace` the sun's colour and multiplier are both about 1/7 of the time cycle's; SparkIV gets 0.071 / 0.141 / 0.139 × 0.549 against the game's 0.067 / 0.133 / 0.133 × 0.534, and the light from around (c37) 2.02 / 2.24 / 2.70, as the game's.

**Interiors:** A room's modifier is mixed by how much of the screen its portals cover. With the camera in a room, 0xAEE7E0 has 0xA0E3C0 and 0xA0E490 make a list:
1. The camera's room, strength 1, with the room's blend value (the 10th number of its line in the `.ide`, at the room's +0x4C; 1 in the ship's rooms).
2. Each portal of that room on screen, its strength the portal's projected area over the screen's (0xB21250, at most 1). A portal into another room takes that room's blend value. A portal to the outside takes the outside.
3. Through a portal into another room, that room's portals once more, their share times the first one's (not the portal back).

Portals with flag 0x4 or 0x10 are left out, as for the ped light (0xA8F6A0, 0xA8F7D0). Then, in the list's order, the rooms ease as R = s × blend + (1 − s) × R and the outside as O = s + (1 − s) × O. The modifier goes in with strength (1 − O) × R, through the same function as the boxes (0xAF2960). With the camera outside, no room modifier is taken.

In the trace the share is 0.99 deep in a room, 0.83 near a door, and 0.06 on the ship's deck in front of its doors. SparkIV does the same each frame (the page's `roomStrength`). The sun and the light from around are mixed as colour and multiplier apart (the room message's `parts`). Doors in a portal are taken as open. Where rooms with different modifiers mix, the camera's room's modifier is used.

## AO under a modifier

The vertex programs read `globalScalars.z` (the AmbientDownMult, see below) and `globalScalars2`:
- Peds (`GTAIV.3.trace`, VS 0x2ca6a8a0): AO = 1 + y · z · (vertex AO · gs.z − 1).
- Everything else (VS 0x2c287000): AO = 1 + z · (vertex AO · gs.z − 1).

Without a modifier both are 1. On the intro's ship the rooms have `boatcabs`: AmbientDownMult 0, z 0.8, y 0.1. A ped there keeps 0.92 of the light from around, a prop 0.2.

## AmbientDownMult in detail

Found in GTAIV.exe and measured in the capture `GTAIV.3.trace` (TLAD, et01_aa):

1. **Value per object:** `Entity_UpdateAmbientScale` (0xB12370) stores, for an object in an interior, `Entity_AmbientScaleAt` (0xAEE600) × 255 in byte +0x5B; 0xFE stands for 1. In an interior this is `Room_AmbientScale` (0xA0D720), with the portal blending from [ide-mlo.md](ide-mlo.md).
2. **Passing to the shader:** The draw command of a cutscene character (0xC645A0) sets the byte /255 as `globalScalars.z` (0xAC6FB0).
3. **Effect in the vertex shader:**
   - `gta_ped`: AO = 1 + gs2.y · gs2.z · (vertex colour red · gs.z − 1).
   - `gta_normal_spec` (props): AO = 1 + gs2.z · (gs.z · (day factor.x · red + day factor.y · green) − 1).
   - With `globalScalars2` = 1 this is vertex AO × AmbientDownMult.
4. **Effect on lighting:** The AO multiplies the ambient light in the sun and ambient pass and, via sat(AO² + 0.5), the map lamps (see [deferred_lighting.md](deferred_lighting.md)).
5. **Measurement:** em_4 has 0.20. In the capture, `globalScalars.z` = 0.2 for all character draws and for Elizabeta's earrings; 0.996 for the map.

**Consequences in the image:**
- **et01_aa:** Together with the correct room assignment and the shadow lamp formula, the earring in the HDR buffer dropped from 3.5 to 1.3 (game 1.0); the skin is at 3.35 (game 3.46).
- **bc_1 (BernieCrane, 0.50):** In the living room, Bernie's face is 63 instead of 88 before (game 62).

In SparkIV: `TimeCycle.ActorAmbient`, sky field `actorAmbient`, uniform `actorAmbient` in `ModelView.html`.
