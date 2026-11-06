# GTA IV (PC): Lighting shaders (`deferred_lighting.fxc` and related)

Applies only to **GTA IV, PC version** (with TLAD and TBoGT). Overview of all formats: [../data-support.md](../data-support.md).

The shaders are in `common/shaders/win32_30/`. Disassembled with `d3dcompiler_47` (script `fxlist.py`). The programs are numbered from 0 in file order, vertex and pixel programs together. Measured in the D3D captures `GTAIV.3.trace` (TLAD, et01_aa) and `GTAIV.1.trace` (bc_3).

## Sequence of a frame (interior, et01_aa)

1. **G-buffer:** colour (GB0), normal (GB1), specular/AO (GB2), depth; also a **stencil value** per object (see below).
2. **Sun and ambient** (p21): albedo × ((c37 + c38 · sat(0.5 − 0.5 n.z)) × AO + sun) + reflection · `globalScalars.w`.
3. **Cutscene lights** (p29), then **lamps** (p31 in interiors, p32 outside; PS 0x25d41c80 in `GTAIV.3.trace`) and **lamps with shadow** (p35); formulas in [2dfx-lights.md](2dfx-lights.md).
4. **Glow of transparent emissives** (light bulbs, shades, PS 0x24B6EE60):
   - Colour: texture × vertex colour × `emissiveMultiplier` × `colorize`; alpha: texture.a × vertex.a × `globalScalars.x`.
   - State: SRCALPHA/ONE, front faces only (CULL CW), writes depth.
   - Comes **before** the fog and all forward-rendered parts.
5. **Fog** (PS 0x2581A220): share = w · sat(z/start) + (1 − w) · sat((z − start)/(end − start)) (c41), with the view depth z. Before it the depth effect `gDepthFxParams` (c16 = DesaturationFar, GammaFar, DepthFxNear, DepthFxFar: time cycle columns 48, 50, 51, 52): from near to far the colour towards its luma by DesaturationFar and × luma^(GammaFar − 1); see [../data-support.md](../data-support.md), the water.
6. **Forward-rendered transparent parts:**
   - Sheer curtain etc. with `gta_normal` forward (PS 0x2493AC20): up to 4 lights, lights without AO, then fog.
   - Draw state: front faces only (CULL CW).
7. **Tone mapping.**

## Stencil value per object

`Entity_StencilValue` (GTAIV.exe 0x5D4E40):

| Bit | Meaning |
|---|---|
| 8 | Object in an interior (+0x24 bit 0x8000000) |
| 1 | Character |
| 2 | Vehicle |
| 128 | Additional bit, subtracted by the light shaders (measured on some characters: 137) |

Measured in et01_aa: map and props 8, characters 9 or 137.

## Cutscene lights and stencil

| Program | lights | Condition (s = stencil, without 128) |
|---|---|---|
| p27 | Objects in interiors, including the map | s ≥ 8 |
| p28 | Objects outside | s < 8 |
| p29 | Characters only | (s mod 8) = 1 |

None of the three programs reads AO from the G-buffer. sat(AO² + 0.5) appears only in p26, and that draws no light in the examined captures. The lamps run through p30–p32 (without AO). This section used to say the lamps used p26 – that was wrong.

### G-buffer of characters (gta_ped, PS 0x25ee22a0)

From the shader code (`GTAIV.3.trace`, Malc's body at call 102287002):
- **rim** = max((v·n ≤ 0 ? 1 + 2·v·n : 1) · v4.y, 0). Here v is the view direction to the point, n the normal after the normal map, v4.y = `globalScalars2`.w · vertex colour green (vertex shader; gs2.w at runtime 0.725).
- **GB2.z (AO)** = v4.x + rim², with v4.x = 1 + gs2.y · gs2.z · (red · gs.z − 1).
- **GB2.x (specular)** = 0.25 · specular value + 0.5 · rim².
- **Normal:** stored as floor((n + 1) · 64)/128, with extra bits in GB1.w. The light shaders read only the coarse 7 bits per component.

SparkIV added rim on 2026-10-23 (`pedRim` in `gtaiv-pc.js`) and **switched it off** again the same day (`PedRimScale = 0`).
- **Reason:** Compared with the game videos (bc_3 s2 f437, s1 f854.75, s2 f108; bc_1 s3 f750.75), rim produces a bright fringe on hair and silhouette that the game does not show. Without rim these frames are 0.2 to 1.45 closer to the game; et01_aa stays unchanged.
- **Open:** Where the derivation is wrong, e.g. the sign of v (point → camera instead of camera → point), or how GB2.z is read in the light pass.

**Measured:**
- **et01_aa** (camera in an interior, no `env` lights): All 5 lights run only through p29. Walls, curtain and earrings get nothing, Malc's arm +2.4.
- **bc_3 (outside):** several passes per light, including "s < 8".
- When the game uses p27, presumably for lights with `env`, has not been checked.

In SparkIV: cutscene lights in a room hit only characters; without AO factor.

### Which program draws a cutscene light; vehicles (#116, 2026-11-06)

- **The stencil test is in the program, not in the draw state.** `lightVolumePoint` ps 7–9 read the stencil through
  `GBufferStencilTextureSampler` (s5): s = 255 × the texel, less 128 when it's 128 or more, less 8 when it's 7.9 or more.
  ps 7 keeps s ≥ 8, ps 8 s < 8 (that one × sat(AO² + 0.5), as ps 6, which has no test), ps 9 only |s − 1| ≤ 0.1:
  **characters only**. A vehicle writes 2 (`GTAIV.3.trace`, intro s4 f420.5: `STENCILREF` 2 before the car on the
  crane's G-buffer draws, call 85653355; draws 85653384–85653473), so a cut light drawn by ps 9 never lights a vehicle.
  During the light draws the device's stencil test is off (`STENCILENABLE` FALSE from call 85658719 in f420.5, from
  52562700 in s3 f569.75).
- **A light named env… (flag 0x100) is drawn by the lamps' program**, not by ps 9. f420.5 draws three of the section's
  lights: `filcar_0` by PS 0x2d5928e0 (ps 9, call 85658885), `env_title_0` and `env_car_0` by PS 0x2d592840, the program
  the harbour's lamps use (calls 85658910, 85658934). `env_car_0`: at (741.39, −237.36, 22.29), colour (0.734, 0.931, 1)
  × 65, radius 21.5, cone cos 0.5, scale 2.73, pointing along (−0.326, 0.727, −0.604) - at the car 7.5 m away
  (its G-buffer world place (738.82, −231.23, 18.70)). So the car's bright rims and body in the game are this lamp's
  light and the vehicle's shine under it.
- **Vehicles' shine under the lamps:** the deferred vehicle shaders' GB2.x (0.5 × strength × (1 − dirt)) and GB2.y
  (the power) are read by the lamps' 2 · GB2.x · sat(L·R)^(GB2.y² · 512) as any surface's.
- Which program a shd… light (flag 4 too) takes: the shadowed lamps' ps 14 (`shd_env_kit` in s3 f569.75, PS 0x2d5918a0,
  call 52562883). Not checked why the cook gets nothing from it (its shadow is the likely reason; SparkIV draws no
  shadow for a cut light).

In SparkIV (`CutsceneLights.CutLightsNow`, `MeshRenderer` PSLight; since 2026-11-06): a cut light without env/shd
lights only peds (the G-buffer's place.w marks vehicles 3); an env… light takes the lamp's formula (×
sat(AO² + 0.5) outside, its shine) on everything and, as a cut light, isn't handed to forward-lit materials; a shd…
light stays on the location only. Vehicles write their shine amount and power for the lamps. cutgen
`D3D12CUTLIGHTSBEFORE116=1` gives the old masks back. Status: **used**.

## Measured constants (et01_aa)

| Register | Value | Meaning |
|---|---|---|
| c39 (PS) | (1, 1, 0.996, 0) | `globalScalars`: w = 0 indoors (GlobEnvRefl, see [timecyclemodifiers.md](timecyclemodifiers.md)) |
| c39 (VS) | z = 0.2 for characters and props, 0.996 for the map | AO factor (AmbientDownMult) |
| c41 | (3, 1500, 0, 0.784) | Fog: start, end, –, near share |
| c45 (VS) | (0.94, 0.06, 0.94, 0.06) | Day factor |
| c51 | (1, 1, 1, 1) | `colorize` |

## The sun's direction in the sun pass (c17)

`gDirectionalLight` (PS c17): xyz the direction the light travels (minus the direction towards the sun), w the shine
scale. The direction is GTAIV.exe 0xAEF0E0's (0x103FBE0), including its turn towards normalize(0.05, −0.5, 1) at night;
formula in [../data-support.md](../data-support.md) ("Two suns").

- **Measured:** `GTAIV.3.trace`, intro s2 f863 (05:15): c17 = (−0.8332, 0.2196, −0.5074, 1.9719), set at call 34227404,
  read by the sun pass PS 0x2d593060 (draws 34238693, 34238698; G-buffer 34236494–34238573). The day formula alone would
  give a sun 13° lower and lit the inside of the ship's bulwark that the game leaves in the sun's shadow (#115).
- In SparkIV: `TimeCycle.SunDirection`. Status: **used**.

## The mirrored surroundings' map (paraboloid map) and its levels

The sun pass (program 21) and the forward vehicle shaders read the map of the surroundings that the game draws each frame (the sky first, then map objects above the camera). Its smaller levels are not plain averages.

- **The map:** 1024 × 1024 A16B16G16R16F with **four levels only** (1024, 512, 256, 128). The sun pass reads it with `texldl` at lod = 4 − 4·saturate(power·512·dReflectionParams.x), dReflectionParams.x = 1/230; a lod above 3 is held at level 3.
- **Its levels** (TLAD trace GTAIV.4 frame 10070, draws 158466112, 158466153 and the next): a full-screen quad per level, vertex shader 0x24770cc0, pixel shader 0x23c2bd80:
  - one filtered read of the level above (`texldl` at lod `gDeferredLightColourAndIntensity.z`), i.e. the mean of 2 × 2;
  - times |c66.w|^0.25, with c66 = (1, 1, 0, 1/2) for level 1, (1, 1, 1, 1/3) for level 2 and, by the sequence, 1/4 for level 3.
  - So level n = mean × (1/(n+1)!)^0.25: **1, 0.841, 0.639, 0.452**. Measured in the dump's levels: the sky 7.4 / 6.2 / 4.8 / 3.4 at the same place.
- **Evidence:** et01_ba f906 (= trace frame 10070), the toll booth's green panel in shade (gta_spec, GB2.x ≈ 0.085, lod 2.78): with plain means SparkIV mirrored twice the game's sky there (7.3/10.8/14.4 against 3.7/5.2/6.7) and the panel came out 10–30 % too bright; with the factors it matches the game's HDR after the sun pass within ~3 %.

In SparkIV: `mirroredAround` in `gtaiv-pc.js` (the plain means from generateMipmap times these factors, lod held at 3). Status: **used**.