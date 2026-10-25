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
| Intensity | Additional factor | |
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
| 0x100000 | Light flag 0x10: the light volume's passes 4–7 instead of 0–3 (see [Light volumes](#light-volumes)) | used | ibid., 0xAC2270 |

The fields at +0x30 and +0x34 (RageLib's `Unknown2C`, `Unknown30`) are the light volume's strength (times Range) and size (times the radius); see [Light volumes](#light-volumes).

### Flags in the game's light list

`Light_Add` (0xABCCD0) creates the lights of the frame (structure of 0x80 bytes: +0x20 position, +0x44 type, +0x48 flags, +0x54 radius):

- **Interior/exterior:** Bit 0x20 means interior, 0x40 exterior, 0x60 both.
- **Cutscene lights:** 0x80. If their name starts with `env`, 0x100 is added; with `shd`, 4 is also added (0x9CA7E0).
- **Corona** (`CoronaSize`, `CoronaHash`): is read but **not supported**; SparkIV draws no coronas. (The bright opening of bc_1's ceiling spots is not a corona but their lens, a gta_emissive material: see #9.)
- **Cutscene lights and rooms** (observation, 2026-10-23; not confirmed in the exe):
  - A cut light affects characters regardless of the room it is in. Evidence: In et01_aa (section 4, frame 484), `FillLight1_3` is 1.2 m behind the camera and thus behind the wall in the neighbouring room. In the game video it lights the characters in front of the camera.
  - No per-light room check has been found in the game. The stencil rule is confirmed: cut lights hit **only characters**, indoors and outdoors.
    - Indoors: trace GTAIV.3, et01_aa.
    - Outdoors: trace GTAIV.4, et01_ba frame 9350, read out with d3dretrace on 2026-10-23. After `BoothLight_0` (call 145552474) the guard's face rose from 1.72 to 2.69; the booth is unchanged (panel 0.52/0.88/0.94).
  - Since 2026-10-23, SparkIV no longer applies cut lights to any map or prop meshes (`ModelView.html`, `marked < 0 && baked`).

## Which program draws a lamp

| Lamp | Program in `deferred_lighting.fxc` | Formula |
|---|---|---|
| normal | p31 (stencil ≥ 8: interior objects and characters), p32 (stencil < 8), p30 (without stencil) | Falloff 1.5 · ((1 − d²/r²)² − 1/3); × sat((n·l − 0.2) · 1.25); no AO, no specular |
| with shadow (flag 4) | p35 | Falloff 1.5 · ((1 − d²/r²)² − 1/3); × sat(1.5 · (n·l − 1/3)); no AO, no specular; × shadow from 4 samples of a paraboloid shadow map (see [Lamp shadows](#lamp-shadows-p35)) |

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

**Open:** In the game, Malc's shirt shows contour bands with dark edges in the ceiling lamp's light (self-shadowing artefacts). SparkIV does not show them, although the map matches. Our lookup point lies 1.9 cm in front of the stored surface. Suspected but **unconfirmed** cause: the game's G-buffer normal, rounded down to 1/64 and bent by the normal map, when offsetting by 0.1 m. Because of this, the shirt is still about 7 % too bright in our render.

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
