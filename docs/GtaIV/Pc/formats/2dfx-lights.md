# GTA IV (PC): Lights in models (2dfx light effects)

Applies only to **GTA IV, PC version** (with TLAD and TBoGT). Overview of all formats: [../data-support.md](../data-support.md).

Models (`.wdr`, `.wdd`, `.wft`) can contain light effects, i.e. lamps and wall lights. SparkIV reads them in `ModelScene` (`Lights`) and sends them to the view via `CutsceneLocation.LightsMessage`.

## Fields

| Field | Meaning | Source |
|---|---|---|
| Position, direction | Placement in the model | |
| Colour (RGB, bytes) | Colour | |
| `Distance` (+0x38) | **Radius** of the light | GTAIV.exe 0xC1E200 → 0xABCCD0 |
| `Range` (+0x3C) | **Strength** | ibid. |
| Falloff, HotSpot | Outer and inner cone angle of a spot | |
| `CoronaSize` (+0x40) | Size of its corona, see [Coronas](#coronas) | |
| Intensity (+0x5C) | Strength of its corona (0.4 for the track spots in Bernie's flat). The light itself doesn't use it | GTAIV.exe 0xC1E200 |
| Flags (+0x4C) | see below | |

## Flags

| Flag | Meaning | Status | Source |
|---|---|---|---|
| 0x20 | on by day | used | 0xC1F9A0 |
| 0x40 (without 0x20) | night only | used | ibid. |
| 0x80 | Flicker | ignored | 0xC1E200 |
| 0x200, 0x1000 | Blinking | ignored | ibid. |
| 0x4000 | Light flag 2, only if the object (instance flag 0x80) allows it; effect open | **unclear** | ibid. |
| **0x8000** | **Shadow lamp** (light flag 4), only if the object's instance has flag 0x100 (see [wpl.md](wpl.md)) | used (since 2026-10-22) | ibid. |
| 0x10000 | Separate path (light colour from 0xC1DAA0) | ignored | ibid. |
| 0x80000 | **Light volume** (light flag 8), see [Light volumes](#light-volumes) | used (since 2026-10-25) | ibid., 0xAC2270 |
| 0x100000 | Light flag 0x10: the lamp is drawn as a "filler" (`fillerVolumePoint`, see [Which program draws a lamp](#which-program-draws-a-lamp)); the light volume's passes 4–7 instead of 0–3 (see [Light volumes](#light-volumes)) | used | ibid., 0xAC1030, 0xAC2270 |

The fields at +0x30 and +0x34 (RageLib's `Unknown2C`, `Unknown30`) are the light volume's strength (times Range) and size (times the radius); see [Light volumes](#light-volumes).

### Flags in the game's light list

`Light_Add` (0xABCCD0) creates the lights of the frame (structure of 0x80 bytes: +0x20 position, +0x44 type, +0x48 flags, +0x54 radius):

- **Interior/exterior:** Bit 0x20 means interior, 0x40 exterior, 0x60 both.
- **Cutscene lights:** 0x80. If their name starts with `env`, 0x100 is added; with `shd`, 4 is also added (0x9CA7E0).
- **Corona:** see [Coronas](#coronas). `CoronaHash` is read but not used: every corona seen uses the sprite `corona`. (The bright opening of bc_1's ceiling spots is not a corona but their lens, a gta_emissive material: see #9.)
- **Cutscene lights and rooms** (observation, 2026-10-23; not confirmed in the exe):
  - A cut light affects characters regardless of the room it is in. Evidence: In et01_aa (section 4, frame 484), `FillLight1_3` is 1.2 m behind the camera and thus behind the wall in the neighbouring room. In the game video it lights the characters in front of the camera.
  - No per-light room check has been found in the game. The stencil rule is confirmed: cut lights hit **only characters**, indoors and outdoors.
    - Indoors: trace GTAIV.3, et01_aa.
    - Outdoors: trace GTAIV.4, et01_ba frame 9350, read out with d3dretrace on 2026-10-23. After `BoothLight_0` (call 145552474) the guard's face rose from 1.72 to 2.69; the booth is unchanged (panel 0.52/0.88/0.94).
  - Since 2026-10-23, SparkIV no longer applies cut lights to any map or prop meshes (`ModelView.html`, `marked < 0 && baked`).

## Coronas

Every 2dfx light also hands in a corona: a soft sprite facing the camera, added to the picture (#18). They can cover much of the frame: in bc_1 the one 1.2 m from the camera lay over the whole upper picture, which made Bernie's flat look 8–9 % too dark "without lights". With the corona draw left out of the game's frame, the game's picture equals SparkIV's across the room.

| What | Value | Source |
|---|---|---|
| List | `Light_FromEffect` 0xC1E200 → 0xC1F020, kept per frame by 0xC1EE40, drawn by the code at 0xC1F170 | GTAIV.exe |
| Sprite | `corona` of `pc\textures\coronas.wtd` (128 × 128 DXT5, clamped) | `platform:/textures/coronas`; `GTAIV.trace`, call 168093 |
| Colour | The light's bytes | ibid. |
| Strength | `Intensity` (+0x5C) × the object's fading alpha (+0x63); outside an interior also × SprBght (time cycle column 22) | 0xC1E200, 0xA32640 |
| Size | `CoronaSize` (+0x40) × 5000 × 0.0005, half a side in metres. 0 draws nothing | 0xC1E200, 0xC1EE40 |
| Place | Moved towards the camera from the lamp: 1 m for an object in an interior, its size otherwise (× 1 − the strength at 0x103F9A4, taken as 0) | 0xC1E200, 0xC1F5B3; `GTAIV.trace`: 16 of 16 to 1 mm; `GTAIV.1.trace`: 2.003 m and 1.001 m |
| When | With the light's day/night flags, whether or not the light lights anything. No limit of distance (`GTAIV.1.trace`: 234 m) | 0xC1F9A0 |

The draw (`deferred_lighting` technique `corona`, in `GTAIV.trace` draw 114115754) is ONE / ONE without depth test. The vertex program decides what shows:

- Camera nearer than the corona's size: no test. Faded by sat(0.5 (f / size + 1)), f the distance along the view, and moved out to 0.1 m when closer.
- Otherwise 12 Poisson points within 2 pixels of its middle are tested against the depth texture, and the corona is scaled by the share in front. At 1 % or less it collapses to a point.

That depth holds only the solid scene: the forward draws before it write to another surface (`0x2cb64c80`, the texture's surface being `0x2db36a00`). So a corona shines through glass. In bc_1 the one in the hall lies behind the flat's glass and still shows.

Not in SparkIV: the object's fading alpha (in `GTAIV.1.trace` only a few far objects, at 0.09–0.40 of 0.5), the types of light that blink by time (+0x58), the strength at 0x103F9A4 and the mirrored `paraboloid_corona`.

## Which program draws a lamp

`FUN_00AC1030` draws the frame's lights and picks the technique by the light's flags (+0x48); `FUN_00AD3560` maps the choice to the technique handles that `FUN_00AD1AD0` registers (resolved on 2026-10-27, #11):

- **Light flag 0x10** (the 2dfx flag 0x100000) set: **`fillerVolumePoint`**, with a shadow **`fillerVolumeShadowPoint`**.
- Not set: **`lightVolumePoint`**, with a shadow **`lightVolumeShadowPoint`**. These are the cutscene lights' programs too.
- Within a technique the pass follows the light flags 0x20 (interior) and 0x40 (exterior): both or neither, only 0x20, only 0x40; cutscene lights (0x80 without 0x100) take their own pass.
- These two light flags don't follow the 2dfx flags 0x20/0x40. bc_1's lamps in Bernie's flat have 2dfx 0x60, but the game draws them with the "only 0x20" pass (ps07, see below), so it sets them by where the lamp is. In SparkIV: a lamp in an interior's room takes ps07, any other ps06/ps08.

The program numbers below are the pixel programs of `deferred_lighting.fxc` counted alone (ps00 …, as `fxdis.py` writes them); the older p-numbers of this page count vertex and pixel programs together (p30–p32 = ps10–ps12, p35 = ps15).

| Lamp | Technique, pixel program | Formula |
|---|---|---|
| filler (flag 0x100000) | `fillerVolumePoint`: ps10 (without stencil), ps11 (stencil ≥ 8: interior objects and characters), ps12 (stencil < 8) | Falloff 1.5 · ((1 − d²/r²)² − 1/3); × sat((n·l − 0.2) · 1.25); no AO, no specular |
| filler with shadow (flag 4) | `fillerVolumeShadowPoint`: ps15 | Falloff 1.5 · ((1 − d²/r²)² − 1/3); × sat(1.5 · (n·l − 1/3)); no AO, no specular; × shadow from 4 samples of a paraboloid shadow map (see [Lamp shadows](#lamp-shadows-p35)) |
| light (without 0x100000) | `lightVolumePoint`: ps06 (0x20 and 0x40), ps07 (0x20 only, stencil ≥ 8), ps08 (0x40 only, stencil < 8), ps09 (cutscene lights) | Falloff (sat(1 − d²/r²)⁴ − 0.1) / 0.9; × cone; albedo × sat(n·l) + 2 · GB2.x · sat(L·R)^(GB2.y² · 512) (the shine); ps06 and ps08 all × sat(AO² + 0.5), ps07 not. In SparkIV (`ModelView.html`, `sceneLightKind`) since 2026-10-27; the stencil conditions of ps07/ps08 are not modelled |
| light with shadow | `lightVolumeShadowPoint`: ps14 | as ps06, × the shadow (as in ps15), and where the stencil is under 8 × sat(0.5 · AO⁴ + AO²) instead of sat(AO² + 0.5) |

- **Evidence (light):** bc_3's wall lamps by the steps (2dfx flags 0xac060, orange, strength 8, radius 8 m). The GTA IV trace `GTAIV.1`, frame 2792 (bc_3 s1 f293.25), draws the two nearest with shadow (calls 84175355 and 84175371, PS 0x24762b60 = ps14, instruction for instruction). Their positions match SparkIV's lamps to 1 cm (scene offset 501.10 / −499.38 / 0.40).
  - Between the dumps before and after them, they add +0.83/+0.43/+0.14 (above the window lamp) and +1.22/+0.64/+0.20 (below it) to the granite wall's HDR. That is all the warmth the game's wall has over SparkIV's.
  - The lamp sits in the wall, so the light comes in flat. The filler's sat(1.5 · (n·l − 1/3)) gave next to nothing there (+0.15 without the shadow).
  - Without the lamps, SparkIV's wall matches the game's sun pass within 1–2 % (3.49/3.45/3.04 against 3.46/3.39/2.96).
  - With the light programs, SparkIV's wall is within 2–3 % of the game's after all lamps.
- **Evidence (both families in one frame):** the GTA IV trace `GTAIV.2`, bc_1, frame of call 84490042 (Bernie's flat). Its 12 lamps run through two pixel shaders.
  - PS 0x26b121c0 is ps07 (state dump at call 84488319): 5 lamps, all with 2dfx flags 0x60.
  - PS 0x26b129e0 is a filler (state dump at call 84488395): 7 lamps, all with 0x100060.
  - Matched to SparkIV's lamps by colour, strength and radius; none breaks the rule.
- **Visual run** (2026-10-27, against the state before):
  - bc_3 s1 f293.25 −2.38, s2 f1307 −0.85.
  - bc_1 s3 f750.75 −1.77, s2 f1174 −1.40, s3 f990.75 −1.36, s1 f939.5 −1.23.
  - Sum −12.36 over bc_1 and bc_3; TLAD unchanged (its lamps in the references are fillers).
  - Worse: bc_3 s3 f137.5 +0.18, f971 +0.05.

Evidence from `GTAIV.3.trace`, et01_aa, frame 10210:
- **Normal lamps:** The table and wall lamps run through PS 0x25d41c80 (draws 102287887/905/923). Its code is p31, instruction for instruction. This section used to say p26 – that was **wrong**: p26–p29 (falloff ((a²)² − 0.1)/0.9, with specular) are the programs of the cut lights.
- **Shadow lamp:** The ceiling lamp in Elizabeta's living room (lamp 0x108060, instance 384) runs through p35 (draw 102287953). Malc's shirt gets all of its lamp light from it (+2.6 to +4.8, with shadow stripes); the other lamps contribute nothing there.
- **Measurement method:** `d3dretrace -D N` saves the state **after** call N, reliably only when N is not a draw call. At a draw call it is not dependable: on 2026-10-25, the dump at draw 120953037 (GTAIV.3) showed the state **before** that draw. Measure at the next call that draws nothing.

### Lamp shadows (p35)

Derived from the shader code of p35 and the constants of this draw (calls 102287940–102287951):

| Register | Name | Value (ceiling lamp) | Meaning |
|---|---|---|---|
| c72–c75 | `dShadowMatrix` | Rows (0,0,1), (1,0,0), (0,−1,0), offset (−y, z, −x − 256) of the lamp | Light space: axis = world X, across = world Y and −world Z |
| c76 | `dShadowParam891113` | (0; 5; 256; 5.2) | z: the 256 that the matrix subtracts; w: radius |
| c66 | `dShadowParam0123` | (−0.025; 0; 0; −0.1923) | w = −1/radius |
| c77 | `dShadowOffsetScale` | (0.2500244; 0.5000244; 0.0029297; 0.25) | xy: position of the tile in the atlas, z: sample spacing, w: tile size |
| s14 | `gShadowZSamplerCache` | 4096 × 4096, G16R16F | Atlas of the lamp shadow maps |

Steps in the shader:
1. **Point:** World point from depth, shifted 0.1 m along the normal, then transformed with `dShadowMatrix` into light space (L).
2. **Hemisphere:** Side of the axis via the sign of L.z (+256 added back). L.z ≤ 0 reads channel R, otherwise G.
3. **Projection:** uv = L.xy / (|L.z| + |L|) · (0.5; −0.5) + 0.5 (dual paraboloid).
4. **Samples:** 4 of them, rotated by a per-pixel angle (frac(vPos · (3; 4.272)) of a full turn). Distances 0.75 / 0.25 / 0.5 / 1 × c77.z. Each is clamped to the tile and then placed into the atlas with c77.w and c77.xy.
5. **Comparison:** lit if stored value + |L| · c66.w ≥ 0, i.e. stored distance ≥ own distance / radius. The shadow is the mean of the 4 samples.

**Content of the shadow map** (from the state dump of draw 102287887, `PS_RESOURCE_14`):
- The ceiling lamp's tile (1024 × 1024, position 0.25/0.5) contains **only the characters**: 6 silhouettes with values 0.23–1.0.
- Walls and furniture are not in it. Empty is 1.
- Occupied pixels: R half 6013, G half 45225.
- The apitrace file (PFM) stores the rows **from top to bottom**.

**How the game draws the characters into it** (every frame anew; calls 102284209–102284772):
1. Target: a 1024×1024 texture G16R16F (0x42f1ae60), set to (1, 1) by a quad (PS 0x25d439e0, c66 = (1, 1, 0, 1)).
2. Pass R: write red only, `gShadowParam14151617` c56 = (0, 0, 0, 0), CULL_CCW. Pass G: green only, c56 = (0, 1, 0, 0), CULL_CW. 43 draws each (all character meshes, skinned, VS 0x25afd220 / 0x25afd9c0, PS 0x25d42d60).
3. Vertex shader (branch c56.x = 0): L = `gShadowMatrix` (c60–c63) · P, a = L.z + c59.z, s = +1 (R) or −1 (G). Position = (L.xy / (|L| − s·a), |L|/R, 1) – **for all vertices**, including those of the other side (they are stretched far out). Passed on: (s·a/R, |L|/R).
4. Pixel shader: discards where s·a/R > 0.01; writes the value |L|/R **interpolated between the vertices**.
5. A quad (PS 0x25d42900) copies the result to the tile location in the 4096 atlas (102284772).

Rebuilt in SparkIV (`lampShadowVertex`, `lampShadowFragment`). The culling had to be set the other way round in the WebGL implementation (R: front faces culled, G: back faces culled). With the literal port, Malc's **back** was in the map (0.61 instead of 0.55). With the current culling, the stored values on Malc's shirt match the game atlas to 4 decimal places.

**SparkIV** (since 2026-10-22) draws, every frame, the characters and props of the scene into such a map for up to the 4 nearest shadow lamps (`lampShadowTarget`, `lampShadowVertex`, `lampShadow` in `gtaiv-pc.js`). The lookup works as in p35. Differences from the game:
- R32F with 1024 per hemisphere instead of a G16R16F atlas.
- The selection of lamps by distance to the camera is **estimated**.
- Our map is similar to the game's: R 6484, G 36098 pixels, smallest value 0.223 vs. 0.235.

Effect in et01_aa, measured against game snapshots of the same moment:
- Sofa bottom left: 46.9 → 37.6, game 37.1.
- Armchair bottom left: 36.2 → 31.8, game 31.4.

**Contour bands on Malc's shirt** (#7): in the ceiling lamp's light, the game shows dark contour bands along the folds of the shirt. These are self-shadowing edges of the shadow test.

- **Cause:** the normal the 0.1 m offset uses. Recomputed over the shirt with the game's depth and shadow atlas (state dumps at calls 102287952/954), the geometric normal gives no bands at all, so they come from the G-buffer normal.
- **How the peds' G-buffer pass makes that normal** (gta_ped, gta_ped_skin, gta_ped_reflect, gta_ped_skin_blendshape; technique `deferred_draw`, vs04 + ps05; skinned peds `deferred_drawskinned`, vs05 + ps05):
  - The normal map's x and y are taken from (w, y) of the texture where 1 − w − x ≥ 0, otherwise from (x, y).
  - They tilt by (xy − 0.5) × bumpiness along the vertex tangent T and the binormal B = T × N × w (both vertex programs compute `cross(T, N)`).
  - z = √|1 − x² − y²| of the raw values (`rsq` takes the absolute value). Where x² + y² comes near 1, z is near 0 and the normal turns far over.
  - vs04 normalises N and T; vs05 (skinned) does not, and ps05 only normalises the sum N z + T kx + B ky. Blended between bones turned apart (elbows), N and T come out shorter, each by its own amount.
  - The result is written to GBuffer1 as floor(64 (n + 1)) / 128 (A2R10G10B10; the finer remainder goes into the alpha). The TLAD lamps read it back as rgb × 2 − 0.99999, i.e. rounded down to 1/64. GTA IV's cutscene lights (GTAIV.2, PS 0x26b13980) also read the remainder from the alpha; SparkIV keeps the 1/64 rounding for all lights.
- **In SparkIV** since 2026-10-25: RageLib reads the vertex tangents, `ModelScene` sends them for peds with a normal map, and `pedBumped` in `ModelView.html` builds this normal. The bands now lie on the same folds as in the game, but they are still narrower (see #7).
- **Bumps were off** from 57a4f88 (2026-10-05) to #8 (2026-10-26): the `bumpiness` uniform was never set, so it was 0 for every material and no normal map tilted anything (peds and map objects). So the #7 bands above came from the 1/64 rounding alone. Found on Bernie's tracksuit (#8, bc_1 s2, GTAIV.2 frame 84490042):
  - The dark patches on his sleeves come only from the cutscene light spt_rm2_1 (lamp draw 84488245): there it adds 0, beside them +3.8. The surface faces the light there (n.l 0.96 from the depth buffer), so the G-buffer normal must be turned more than 70°.
  - Replays with Bernie's other draws, the blended G-buffer draws or all map draws left out (`apitrace trim`, `d3dretrace --ignore-calls`) leave the patches as they are: his torso draw 84486442 writes that normal itself.
  - Recomputed per vertex from the game's vertex buffer (call 55890979), bone matrices (84486437) and the formula above: a visible forearm vertex with map values (0.65, 0.73) has z ≈ 0.21, and n.l drops from 0.51 to −0.16.
  - In SparkIV the patches appear only with all four fixes together: bumpiness set again, B = T × N × w, z from the absolute value, skinned N and T not normalised.
  - Bumpiness is set again for peds only. The other materials' normal maps (`bumped`, tangent space from screen derivatives, not checked against the game's shaders) stay off: switched on, they made bc_3's harbour wall further from the game (s3 f506.75 +0.53, s2 f1307 +0.43).

## Light volumes

Some lamps show a cone of light in the air. GTA IV draws it after the deferred lights (`FUN_00AC2270`).

- **Which lamps:** light flag 8 (2dfx flag 0x80000), type point or spot, volume strength and size not 0, and visible (`FUN_00431E40` with radius × size).
- **Values** (0xC1E200 → `Light_Add` 0xABCCD0 → 0x5B8580):
  - strength (light +0x70) = 2dfx +0x30 × Range;
  - size (light +0x74) = 2dfx +0x34;
  - radius = the lamp's radius × size; colour = the lamp's colour (not × its intensity); cone = the spot's outer angle.
- **Mesh** (made at the game's start, `FUN_00ABE3A0`; drawn by `FUN_00AC09A0`, one mesh per type):
  - spot: `FUN_00AC1C00` with n = 5. The lower half of an octahedron (|x|+|y|+|z| = 1, z ≤ 0) in steps of 1/n, plus a fan in z = 0 from the middle to the rim. A triangle strip of 256 vertices; equal to the vertex buffer the game filled (GTAIV.3, call 287065) in every vertex;
  - point lamp: `FUN_00AC0B20` with n = 4, the whole octahedron (all eight eighths), 288 vertices.
  - Constants read from the exe: −1, 1, π/2, 1/3.
- **Vertex program** (vs07, `deferred_lighting.fxc`):
  - point lamp: normalize(v);
  - spot: angle = outer angle × (1 + z) for z from −1 (on the axis) to 0 (the outer angle); across = (x, y)/|xy| × sin × c213, along tangent and tangent × direction; along the axis cos; xy = 0 and z = 0 is the lamp itself;
  - everything × c213 × radius × 0.662.
- **c213** (`gDeferredVolumeRadiusScale`) is computed by `FUN_00ABE3A0` from the mesh's n: a = (π/2)/n, c213 = 1 / (((2 cos a + 1)/3)² + 2 (sin a / 3)²). That gives 1.044903 for spots (n = 5) and 1.071094 for point lamps (n = 4), the values in all four traces, whatever the cone angle.
- **Pixel program** (TLAD trace GTAIV.3, frame 13868, et01_aa's spot: draws 120953037 and 120953053, ONE/ONE, no depth test):
  - per pixel along the eye's ray to the face, cut at the scene's depth: within a sphere of 0.66 × radius round the lamp, f = (clamp(s at the face) − clamp(s at the eye))² / (3 · (0.66 r)²), s measured from the ray's point nearest the lamp, clamped to the half-chord;
  - × colour × strength × 0.25 (c216, c217.x).
- **Passes** (technique `lightShafts` in `deferred_lighting.fxc`, read from the file):

  | Passes | Programs | Used for |
  |---|---|---|
  | 0/1 | vs07 + ps18/ps19 | normal lights |
  | 2/3 | vs08 + ps18/ps19 | with shadow (+2) |
  | 4/5 | vs07 + ps20/ps21 | flag 0x100000 (+4) |
  | 6/7 | vs08 + ps20/ps21 | flag 0x100000 with shadow |
  | 8–11 | ps22/ps23 (= ps20/ps21 × 3) | not used by `FUN_00AC2270` |

  - The first pass of each pair culls CCW (states 6 = 3), the second CW (6 = 2). ps20 and ps21 differ only in the sign of the output.
  - Measured with state dumps at calls 120953036, 038 and 054, the first pass added +0.833 at pixel 912/253 and the second took off 0.092. Recomputed with the game's mesh, f there is 0.093 at the near face, so the light is f(far face) − f(near face).
  - The faces the passes cover match the mesh projected (rows 250, 377 and 410 within 4 pixels).
- **Shadow variant** (vs08, +2): `FUN_00925DB0` (with +0x60 = 0 and +0x64 = −1) returns −1 for every lamp added by `Light_Add` (0x5B8580 sets them so). Map lamps therefore never take it.
- **Without flag 0x100000** the game draws ps18/ps19 (passes 0/1). This model reads a lookup texture, `gDeferredLightSampler2` (s0).
  - **Which texture:** the handle `0x0154E218` is set by `FUN_00AD2B20` with `FUN_00AD1A90()`. That is the texture "volume" from the dictionary `platform:/textures/lights_occluders` (`FUN_00ABF130`). In `pc\textures\lights_occluders.wtd` it is `pack:/volume.dds`, 32 × 512 L8. Its 16 384 bytes equal those uploaded in the bc_1 trace GTAIV.2 (call 1539809). The left half falls from 255 to 0 like acos(2v − 1)/π (within 5 levels); the right half is another table (255 to 127).
  - **Formula**, instruction by instruction:
    - P = (eye − lamp) + ray × t;
    - K = min(16 / r², 16), r being c66 (radius × size);
    - both P and (eye − lamp) are scaled by √K / √(1 + K h²);
    - the cosine of the angle between them gives the lookup position (u 0.125, v 0.5 + 0.5 cos);
    - result = the texture × π / (1.5 √(1 + K h²)) × colour × strength × 0.25.
  - **Checked** in GTAIV.2 at draw 1539854 (ps18, a spot outside bc_1, the eye inside its volume), with state dumps at calls 1539853, 1539856 and 1539871. Recomputed with the game's mesh and this texture:

    | Pixel | recomputed | measured |
    |---|---|---|
    | 1919/535 | 0.1264 | 0.1265 |
    | 960/540 | 0.0219 | 0.0218 |
    | 400/300 | 0.0 | 0.0 |
    | 1500/900 | 0.0581 | 0.0581 |

    The second pass (ps19) changed nothing, because its near faces lie behind the eye.
  - **Sampler:** CLAMP and linear filtering (`deferred_lighting.fxc`'s sampler state for deferredLightTexture2; MINFILTER LINEAR at the trace's draws).
- c217 = (strength, a global value, size, a global value), as `FUN_00AD2F00` is handed them. Traced: (4.6, 1, 1, 1) for et01_aa's spot; (0.6, 1, 0.4, 1) and (2.22, 1, 0.65, 1) in GTAIV.2.
- Values of the et01_aa spot: strength 4.6 (0.2 × Range 23), size 1, radius 5, outer angle 64° (c215 1.117 rad). They match SparkIV's reading of the 2dfx exactly.

In SparkIV: `gameLightVolumes` and `gameLightVolumeMesh` in `gtaiv-pc.js` draw the light volumes into the light after the scene. They use the ported meshes, the vertex program as vs07 (with the 2dfx tangent) and the scene depth from the resolved frame. The sign comes from the faces' winding: in WebGL the game's strips show their near faces as front faces, which take f off. The lamps are chosen by `lightVolumesNow` in `ModelView.html`, by the same rules as the lamps themselves. Lamps with flag 0x100000 take ps20/ps21. Lamps without it take ps18/ps19, with the texture "volume" read from `lights_occluders.wtd` (`CutsceneLocation.LightVolume`, sent with the lamps). Status: **used**.

bc_1's `ws_sublight` lamps (0x8C060) lie in other rooms than the camera's, so neither the game nor SparkIV draws their volumes in the cutscene. None of the reference pictures shows a volume of that kind.

## Lights for forward-rendered objects

Transparent map parts, such as a sheer curtain, are drawn individually after the deferred lights. They get at most 8 lights (shader registers c19–c31, `LightPosition*`, `LightCol*`, `LightFallOff`, `LightCone*`):

- **Selection:** `Lights_ChooseForEntity` (0xAC5670) scores each light with `Light_ScoreForEntity` (0xABD8A0).
  - A light only counts if its **radius is greater than 4.0 m** (constant 0xFE8AB8).
  - Its score is (1 − d²/(r + extent)²)⁴ × luma(colour) × intensity.
  - The 8 best are taken.
- **Excluded:**
  - Cutscene lights (0x80, without 0x100) are dropped.
  - An object in an interior gets no pure exterior lights.
- **Upload:** `Lights_SetForwardConstants` (0xAC30F0).
- **Evidence:** et01_aa: The curtain got only the ceiling lamp (5.2 m) and the spot (5 m), not the table lamps (2–3.5 m).

In SparkIV: uniform `forwardLit` in `ModelView.html`.

## Which lamps a frame draws: the camera's interior

With the camera inside an interior, the game draws the deferred lamps of that interior only, not those of other interiors in reach (other floors).

- **Evidence:** TLAD trace GTAIV.3, frame 10210 (et01_aa section 2, frame 119.5, camera in `E1elizaLounge_MloRoom`). The frame draws 7 lamps (pixel shaders 0x25d41c80 and the shadow lamp's 0x25d413c0), all in E1_Elizabeta's lounge. Their positions match SparkIV's lamps to 0.01 m (scene offset 353.82 / 1518.52 / 24.69).
- Within reach SparkIV had 35 lamps, 20 of them in GTA IV's interiors below (`RM3a`, `ProjectF1`, `ProjectF1B`). None of these is drawn by the game; here they lit through the floor.
- Not explained yet: 6 further lamps of the same interior (hall, kitchen, two in the lounge) are not drawn in that frame either. Probably visibility culling of the light volumes (frustum, portals); **unconfirmed**, the exe's selection is not traced.

In SparkIV: `sceneLightsNow` in `ModelView.html` skips lamps whose room (`roomOf`) belongs to another interior than the camera's room. Status: **used** (the interior rule); the further culling is **unclear**.
