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
| 66 | (header: SkyMultiplier, shifted) | Factor of the near fog colour | used | `GTAIV.3.trace` |
| others | Temperature, PostFxStrength, Contrast, Far and DOF columns, AmbientOcclusionMult, PedAmbOccHack, last column (em_4 255, BernieCrane 0) | | **unclear** or ignored | |

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
