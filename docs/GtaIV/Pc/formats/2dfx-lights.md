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
| 0x80000 | Light flag 8 | **unclear** | ibid. |
| 0x100000 | Light flag 0x10 | **unclear** | ibid. |

### Flags in the game's light list

`Light_Add` (0xABCCD0) creates the lights of the frame (structure of 0x80 bytes: +0x20 position, +0x44 type, +0x48 flags, +0x54 radius):

- **Interior/exterior:** Bit 0x20 means interior, 0x40 exterior, 0x60 both.
- **Cutscene lights:** 0x80. If their name starts with `env`, 0x100 is added; with `shd`, 4 is also added (0x9CA7E0).
- **Corona** (`CoronaSize`, `CoronaHash`): is read but **not supported**; SparkIV draws no coronas. This is probably why the inside of the ceiling spots in bc_1 is dark; in the game it glows bright white.
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
- **Measurement method:** `d3dretrace -D N` saves the state **after** call N.

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
