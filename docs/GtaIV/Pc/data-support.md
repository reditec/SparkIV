# GTA IV (PC): Which game data SparkIV supports

This file applies only to **GTA IV, PC version**. Other games and platforms get their own file under
`docs/<Game>/<Platform>/`.

For every file format and every tool it records what SparkIV **uses** from the game data, what it
**reads but ignores**, and what is **unclear in meaning**. The goal is to resolve all items under
"ignored" and "unclear". Where the files alone are not enough, Ghidra (GTAIV.exe), x32dbg
(the running game) or an apitrace capture (Direct3D 9) help.

## Status

Every row carries a status:

| Status | Meaning |
|---|---|
| **used** | SparkIV reads the data and applies it to the image or the playback. |
| **ignored** | The meaning is known or obvious, but SparkIV does not apply it. |
| **unclear** | The data is present, its meaning is not resolved. |
| **game logic only** | The data does not affect the image, only the game; SparkIV does not need it. |

Each entry also states **where** the finding comes from: file, exe address, shader or capture.
Assumptions are marked as such.

Detailed descriptions of individual formats with all findings are under [formats/](formats/):

- [wpl.md](formats/wpl.md): placements, instance flags
- [ide-mlo.md](formats/ide-mlo.md): interiors, rooms, portals
- [timecyclemodifiers.md](formats/timecyclemodifiers.md): modifier columns, AmbientDownMult, GlobEnvRefl
- [2dfx-lights.md](formats/2dfx-lights.md): lights in models, shadow lamps, lights for forward-drawn objects
- [deferred_lighting.md](formats/deferred_lighting.md): lighting shaders, stencil, frame sequence
- [wbs.md](formats/wbs.md): a cutscene ped's face shapes (blend shapes) and how the face clip weights them

The functions found in GTAIV.exe are named in the Ghidra project (script `ApplyCutsceneFindings.java`).

---

## Cutscenes

As of: 2026-10-11. Based on all 153 `.cut` files from `pc/anim/cuts.img` and 6030 clips from an
openFormats export (`.onim`) of all 357 cutscene dictionaries.

### `.cut` (text)

| Block | Status | Content / source |
|---|---|---|
| `[ANIM]` | used | Animation dictionary of the section (`BC_3_0` -> `bc_3_0.wad`). |
| `[AUDIO]` | used | Name of the sound. |
| `[CAMERA]` | used | Camera clip in the dictionary (`camera_0`). |
| `[DRAW_DISTANCE]` | used, **timing unclear** | Per shot: time (ms), number, a value (bc_3 always 0, meaning unclear), view distance (m). Read in `Cut_ParseFile` 0x9C3840, state 0x13 (`"%d %*d %f %f"`), table 0x1294760. The times lie 0.1 to 2 frames **before** the camera cuts. The game video shows the same as SparkIV at the first cut of bc_3: in the last frame before the cut the distant coast is already missing. Where the game reads the table per frame has not been found (apart from parsing and cleanup, no code accesses 0x1294760). |
| `[DURATION]` | used | Length of the section (ms). |
| `[FLAGS]` | used (partly) | Of these, only fade-in and fade-out (`LONG_FADE_OUT` etc.); the fade lengths are assumed. The remaining flags have not been checked individually. |
| `[LIGHTS]` | used | Light clips of the section. |
| `[MISSION_TEXT_NAME]`, `[TEXT]` | used | Subtitles: table, start time, duration, key. |
| `[MODELS]` | used (partly) | Number, model, clip, face clip. A **fifth field** (`0`, only in rows with a face clip) is **unclear**. |
| `[OFFSET]`, `[ORIENT]` | used | Position and rotation of the scene in the world. |
| `[PROPS]`, `[VARIATION]` | used | Props and clothing variations. |
| `[REMOVE]` | used | World objects hidden during the scene. |
| `[VEHICLE_DETAILS]`, `[VEHICLE_REMOVAL]` | used | Vehicle colours and dirt; removed vehicle parts. |
| `[CUTSCENE_HEADER]` | **unclear** | bc_3: `44 1143 2578 3615`, the section boundaries in an overall frame count starting at 44. Possibly an offset for sound or subtitles (44 frames ≈ 1.5 s). The synchronisation in SparkIV still has to be checked. |
| `[ANIMRANGE]` | **unclear** | `range on 44.0 3615.0`; matches `[CUTSCENE_HEADER]`. |
| `[COMPRESSION]` | **unclear** | `1 A` everywhere. |
| `[EFFECTS]` | **ignored** | Particle effects, e.g. `FX_blood_gun_entry:effect2_1 5 1205 71900 71900 <6 numbers> 0`: effect name, clip, numbers for object, bone, time, position and rotation (assumption). The associated `effect` clips have only a `LightDirection` track. |
| `[CAMCORDER]` | **ignored** | Time spans (ms) in 134 files, presumably the handheld-camera look. |
| `[BLOCKING_BOUNDS]` | game logic only (assumption) | Four corner points and a radius, probably the blocked area. |
| `[PLAYER_START]` | game logic only | Position of the player after the scene. |
| `[MAX_CARS]`, `[MAX_PEDS]` | game logic only (assumption) | In 4 files. |
| `[TIME]` | – | Empty everywhere. |
| Bytes after the last `[SECTION_END]` | – | Padding bytes; the files are padded to 2048 bytes. |

### Animation clips (`.wad`, track types)

| Track type | Status | Content / source |
|---|---|---|
| `BonePosition` (0), `BoneRotation` (1) | used | Bones of characters, objects and faces. |
| `ModelPosition` (5), `ModelRotation` (6) | used | Root of the model. |
| `CameraPosition` (7), `CameraRotation` (8), `CameraFov` (136) | used | Camera ([cutscene-camera.md](formats/cutscene-camera.md)). |
| `CameraDof` (137) | used (since 2026-10-11) | (near, far, 0) in metres. **Measured with x32dbg** (`dof_dump.txt`, breakpoint 0x5E16F0): the game passes exactly these values to its depth of field. In cutscene mode (`Cut_StartSection` 0x9C9180 sets Postfx +0x264) the shader gets dofDist = (0.5, far − near, 0.5, (near + far) / 2): everything from "near" to "far" is sharp; towards "near − 0.5 m" and "far + 0.5 m" it blends with strength b = M², using a 4×4 blur (rage_postfx's last pass; corrected on 2026-10-27, see "Depth of field and the G-buffer's softening" below: the near side blurs too). **M is measured as 0.5** (`dof_blur.txt`, breakpoint 0x930580: DOF_BLUR = (0.5, 0, 0.5, 0)), i.e. `misc.DOFBlurMultiplier.SD` from `visualSettings.dat`, b at most 0.25. Why the SD value applies is open: the check function 0x424000 is not readable in Ghidra. |
| `CameraMatrixRotateFactor` (138) | **unclear** | Camera, one value per frame. |
| `CameraControl` (144) | **unclear** | Camera, −1 or 1. |
| `LightColor` (139), `LightDirection` (143) | used | Colour and direction of the scene lights. |
| `LightRange` (140), `LightIntensity1` (141), `LightIntensity2` (142) | used (correctly assigned since 2026-10-11; brightness resolved since 2026-10-12: outdoors colour × intensity × exposure) | **The openFormats names are misleading.** According to the exe (0x9CA7E0 -> 0xABCC50 -> 0xABCCD0), 140 is the **intensity**, 141 the **radius** in metres (the 11th parameter; the sphere is tested against the view frustum with it) and 142 the outer cone angle from the axis in degrees (at most 89°); the inner one is half of the track's angle as it is, not of the 89° (0x9CA7E0 hands 0xABCC50 the value × 0.5 and the value; the TLAD trace GTAIV.4's light on Jim's face, et01_ba s1 f385, call 140390932: cone offset cos 89°, scale 2.07 = 1 / (cos 60° − cos 89°), the track 120°. With half of the 89°, SparkIV's light there came out a third too weak; now the face is within 3 % of the game's HDR, 2.84 / 1.90 / 1.50 against 2.83 / 1.84 / 1.45). SparkIV had swapped 140 and 141. The spot shader (deferred_lighting program 26) computes albedo × colour × intensity × (a⁴ − 0.1)/0.9 × cone × n·l × saturate(GB2.z² + 0.5), with no further factor. **Measured with x32dbg** (`cutlight_dump.txt`, 0xABCCD0 and 0xAD30E0) for bc_3 `filler_0`: intensity 3.5, radius 7, cone 42.5°/85°, colour (1, 0.927, 0.763), position as in SparkIV; the values arrive at the shader exactly like this. Open: with these values Bernie's face is too bright in SparkIV, but not in the game. So the cause lies in SparkIV's computation (normals, tone values), not in the data. |
| `Type21` (21) | used (since 2026-10-29) | In every face clip (`*_head`), e.g. 85 tracks for Bernie: the weights (0 to 1) of the ped's **face shapes**. The track's bone id is the shape's key in the ped's `.wbs` (`pc\anim\cutsprops.img`, beside the ped). The game adds weight × change of every shape to the head's vertices before skinning (measured in trace GTAIV.3; scale 1/4096 for positions, 1/128 for normals and tangents). Details: [formats/wbs.md](formats/wbs.md). |
| `Type145` (145) | **unclear** | Only in 3 clips of `cs_vlad`. |
| `ActionFlags` (128), `UV0`/`UV1` (130/131), `FacialAnimation` (134) | – | Not present in cutscenes; used in the animation viewer. |

---

### Model materials (excerpt, relevant for cutscenes)

| Shader / parameter | Status | Content / source |
|---|---|---|
| `gta_emissive`, `gta_emissivestrong`, `gta_emissivenight` | used (as in the game since 2026-10-12) | Program 14: a normally lit surface, the texture as albedo. Program 12: in addition texture × vertex colour × `emissiveMultiplier`, for `emissivenight` × `gDayNightEffects.w` = 1 − day factor. Outdoors additionally × exposure, like all lights.
- **Inside an interior room** (since 2026-10-23, from trace and videos):
  - Solid emissives in the G-buffer do **not** glow, they are only lit (et01_aa trace, program 14).
  - Transparent or glass emissives (forward-drawn, PS 0x24B6EE60) glow **without** the day/night factor (bc_1 by day: glass bowl glows, fabric shade does not).
  - What exactly the game bases this on (bucket, pass) is not confirmed in the exe. |
| `gta_ped_skin(_blendshape)`: `SubColor`, `SubScatWrap`, `SubScatWidth` | used (since 2026-10-12) | Program 10: albedo + 0.5 × SubColor × texture alpha in a band at the shadow boundary (formula in the code). Stencil value 1 in the GBuffer; pass 1 of the spot lights lights only this value. |

### Outdoor lighting (measured in the D3D capture `GTAIV.1.trace`, bc_3)

- **Sun (c18):** (1, 0.851, 0.714) × 10.25, as in SparkIV.
- **Ambient:** c37 = Amb0 and c38 = Amb1 − Amb0. This gives a blend from top (Amb0) to bottom (Amb1), as in SparkIV.
- **Day factor:** `gDayNightEffects` = (f, 1 − f, f, 1 − f); `globalScalars2` = (1, 1, 1, 0.725).
- **Cutscene scene lights** (fill lights): colour × intensity, with no further factor; outdoors × exposure. With this, Bernie's face in the HDR buffer matches the game (nose 2.68 vs. 2.68).

### Indoor lighting (measured in the D3D capture `GTAIV.2.trace`, bc_1, frames 4613–13049)

- **Origin of the values:** Indoors, the room's time cycle modifier replaces the values from `timecyc.dat`. After that the game computes exactly as outdoors.
  - Assignment: the room row in the `mlo` section of the `.ide` contains the hash of the modifier name in the 11th field (`GxtFile.Hash`).
  - Bernie's apartment: the hash 1720238244 stands for `BernieCrane` from `timecyclemodifiers3.dat`.
- **Measured:**
  - c18 sun = 0, because the directional light multiplier is 0.
  - c37 = Amb0 RGB/255 × 4.5 = (4.447, 4.182, 3.741), exact.
  - c37 + c38 = Amb1 × 4.5, exact.
  - c40.w = 0.6 (outdoors 0.725).
  - c45 ≈ 0.78.
- **Supported:** `TimeCycle.LoadModifiers` and `TimeCycle.With` apply light, exposure, tone mapping values, fog start and view distance. The value −1 means "unchanged".
- **Open or unresolved:**
  - The alpha value of the colours; assumed to be a blend fraction, measured only at 255.
  - `AmbientDownMult` and `GlobEnvRefl` are used since 2026-10-22, details in [formats/timecyclemodifiers.md](formats/timecyclemodifiers.md).
  - These columns are not used: `Temperature`, `PostFxStrength`, `BrightPassThreshold`, `Contrast`, the `Far` columns, the `DOF` columns, `AmbientOcclusionMult`, `SkyMultiplier` (presumably c40.w) and `PedAmbOccHack`.
  - As in the game, the modifier of the room the camera is in applies. If it is in no room, `timecyc.dat` alone applies. For this, SparkIV sends the modified time cycle per room, and the view selects it every frame. Whether the game blends on a room change has not been checked.
  - The indoor tone mapping constants have not yet been checked against the capture.

### Reflection, sun shadows, film grain (D3D capture `GTAIV.4.trace`, TLAD `et01_ba`, frame 9350, 12:50)

- **Environment reflection map** (`ParabSampler` in the sun pass, `EnvironmentSampler` in vehicle glass):
  - The game redraws it every frame: 1024 × 1024 HDR, centred on the camera. The sky comes first, then the map objects, without people and vehicles.
  - It then downsamples it to 512, 256 and 128.
  - Shaders from the capture:
    - Vertex shader 0x22EC8A80: position = direction.xy / (distance + direction.z), depth 1 − 1/(distance + 1).
    - Pixel shader 0x1A37E8E0: discards everything below the camera. Light = ambient light × AO + sun, without shadows and lamps, with the distant fog on top.
  - It is read at 1 − (R.xy/(2(1 + sat R.z)) + 0.5), at level 4 − 4·sat(strength × 512 × `dReflectionParams.x`); the value is 1/230 in both captures.
  - **In SparkIV:** `parabolicTarget`, `mirroredAround` in `gtaiv-pc.js`. The smaller levels are mipmaps; I have not rebuilt the game's downsampling shader.
- **Sun shadows:**
  - 4 cascades up to 9.5 / 20 / 50 / 175 m view depth (`gFacetCentre`).
  - Each is drawn at 4096 × 4096 pixels (R32F) and copied, 2 × 2 averaged, into an atlas of 8192 × 2048 pixels (R16F).
  - What is stored is the height along the sun axis. Axis u is the camera direction, laid flat onto the plane perpendicular to the sun; axis v = u × sun.
  - Bias per cascade: 0.14 / 0.24 / 0.5 / 0.75 m.
  - The sun pass reads 12 samples. Result: light = sat(fraction/12 + 1.5·(distance/175)²).
  - The shadow affects direct sunlight and sun specular, not the reflection.
  - **In SparkIV:** `sunShadowCascades`, `shadowTarget`, `gameSunShadow`. Deviation: the cascades are drawn directly at 2048 pixels in R32F, without averaging, and read unfiltered.
- **Film grain (TLAD only):**
  - The source is column 10 of `timecyc.dat`, as an integer. The column header wrongly says "Sky top". It is read in the parser 0xAEFEB0.
  - `NoiseParams.z` = value/64 (0x930268); under a room modifier the value is halved.
  - The noise comes from a texture of 1024 × 1024 random bytes that the exe generates itself at startup. It is read at frac(uv·(3.2, 1.8) + random), and (blue − 0.5)·z·1.1 is added before exposure.
  - GTA IV and TBoGT have 0 there everywhere.
  - **In SparkIV:** `TimeCycle.Noise`, `gameGrainTexture`.
- **Yellowish look of TLAD:** It comes from its own `timecyc.dat`, at noon ColorCorrect (161, 117, 74) versus (115, 122, 113) in the base game. SparkIV already reads the TLAD file.

### Sky and clouds (`gta_atmoscatt_clouds`, captures `GTAIV.4.trace` and `GTAIV.1.trace`, resolved on 2026-10-16)

- **Dome:** There is no model in the game files. The exe builds it itself. In GTAIV.4 it is vertex buffer 0x248B51C0 (call 147965, 992 triangles).
  - 17 rings with radius 2·cos(k·π/32) and height 1.636·sin(k·π/32). The texture coordinates lie radially around (0.5, 0.5).
  - It is drawn with scaling (10000, 2000, 10000) around the camera. Dome (x, y, z) corresponds to world (x, z, −y).
- **Sky colour:** SkyColor + azimuth colour (columns 64–72, 77, 78) and sunshine (103–105, 119).
  - HDRExposure = SkyLightMult (column 60) × 2.85 (0xDBC049), clamped to 30. After that everything is tone-mapped like the rest.
- **Cloud noise:** The shader `rage_perlinnoise` (0x23C2D220) recomputes it every frame into a 256² texture. The basis is `baseperlinnoise3channel` from `pc\textures\skydome.wtd`, 6 octaves.
  - The fine structure comes from `noise16_p`, not from `detailbump2`. In the capture the textures are two levels smaller; this is due to the texture quality.
  - Tried and taken back (2026-10-27/28): the textures from their third level, as in the trace. in `GTAIV.1` the noise draw 74260495's Noise1Sampler is 32 × 32, byte for byte the third level of the 128 × 128 `baseperlinnoise3channel`; the sky draw 74265456's HighDetailNoiseSampler is 64 × 64, the third level of the 256 × 256 detail texture (within DXT rounding). From the full texture the noise's spread was 0.20, the game's 0.11 (now 0.110). With twice the contrast fewer places passed the threshold: blue sky where the game's lies overcast, and the water mirroring it too blue. Visual run: bc_3 sum −5.46 (s3 f828.25 −1.96, s2 f1307 −1.23, s2 f1055.25 −0.35, s1 f854.75 −0.35); worse s3 f357.5 +0.09, f137.5 +0.05; TLAD unchanged. Taken back: the reference videos were recorded with HQ textures (the user, 2026-10-28), so there the game samples the full noise; the better match came from more cloud cover by chance. The videos' cloud cover is chance as far as known: bc_3's weather is SUNNY (bernie3: `FORCE_WEATHER_NOW(1)`), and where the clouds lie comes from the phase, which grows with all the game time played in the save (`GTAIV.1`: 4501.13; SparkIV starts from TLAD's trace's 593.25), so no video's can be known.
- **Cloud columns** (matched in both captures):
  - CloudColor 81–83, SunsetColor 73–75 × column 89, upper cloud colour 99–101 + 73–75, TopCloud 95–98.
  - Threshold 85, bias 86, shadow strength 84, shadow offset 87, inscattering 88, detail scale 90, detail strength 91, thickness 92, fade towards the horizon 94.
- **Movement:** The phase grows per game hour by column 107 (parser 0xAEFEB0 -> +0x1B4, 0x62E110).
- **Cloud cover and sun glow** (#5, checked on GTA IV's trace `GTAIV.1`, sky draw 74963380, PS 0x246411a0 = program 8 of `common\shaders\win32_30\gta_atmoscatt_clouds.fxc`, instruction for instruction):
  - Cover = amount^thickness × saturate(amount × EdgeSmooth). GTAIV.exe 0x615D98 sets EdgeSmooth = 1 / column 89 (the sky's +0x184, next to the detail's scale, strength and the thickness at +0x188 to +0x190). The traces agree: 1 in GTA IV's `GTAIV.1`/`.2` (column 89 = 1), infinite in TLAD's `GTAIV.3`/`.4` (column 89 = 0, so the factor is 1 there). The cover drives the light on the clouds, their inscattering, the fade of the upper clouds and the total.
  - The sun's glow adds saturate(0.5 cos − 0.0625) × SunSize (cos to the sun), not saturate(0.5 + 0.5 cos).
  - Recomputed per pixel with the trace's own constants and noise textures: before, clouds came out up to 40 % too bright and reddish (measured 7.7 / 7.9 / 7.8, computed 10.4 / 9.4 / 8.7); after, the mean is within 4 % of the game.
  - The trace's sky is SUNNY at 10:39 (all 11 time cycle values within 0.0001), not 12:00. The references' 12:00 for bc_3 is not confirmed.
- **Clock in a trace:** every frame sets gDayNightEffects (VS/PS c45) = seconds of the day / 43200, rising until noon and falling after it. Rising or falling over the neighbouring frames gives morning or afternoon. The traces: `GTAIV.1` 10:11–10:57, `GTAIV.2` 08:51–10:17, `GTAIV.3` 11:54–13:20, `GTAIV.4` 11:54–13:07. The clock runs on through the cutscenes; they don't set it.
- **Two suns** (GTAIV.exe):
  - The sky's (0xDBBDF0 → the shader's SunDirection): the angle from the hour (before 6 t × π/12, to 20 π/2 + (t − 6) × π/14, after it 3π/2 + (t − 20) × π/8), then normalize(sin a − cos a × (0, −0.342, 0.940)). Six sky draws in three traces (09:35 to 13:12, clock from c45) match it to 0.0001.
  - The light's (0xAEF0E0 → 0x103FBE0, read by 0xAC51F0 into the directional light): the same angle kept within π/2 to 3π/2, then 0.8 × (a − π/2) + 0.6π. Lower than the sky's sun in the morning (at 10:39 by about 6°). Not yet seen in a trace.
- **In SparkIV:** `SKY_FRAGMENT`, `PERLIN_FRAGMENT`, `perlinNoise` in `ModelView.html`; the values are in `TimeCycle.cs` as `skyHdr`.
- **Open:**
  - One game hour is assumed to be 120 s of playback time; this is not read from the exe.
  - `DetailOffset` moves along in the game; in SparkIV it is fixed.
  - Stars, moon and galaxy (at night) are still missing, as is the blend to the water colour below the horizon.
  - The time of the bc_3 and bc_1 reference videos (12:00): the videos don't show the clock; it was around noon when they were recorded, the exact hour isn't known.
  - The light's sun direction (0x103FBE0) has not been found in a trace frame yet.

### Depth of field and the G-buffer's softening (rage_postfx's last pass, resolved on 2026-10-27, #4)

- **The pass:** GTA IV's PS 0x23e5e6a0 (`GTAIV.1`, call 74970168), TLAD's 0x25819460 with the film grain (`GTAIV.3`, call 120953415). It does the depth of field, the motion blur and the tone mapping in one, into the backbuffer.
- **Checked:** recomputed per pixel from the pass's inputs (state dumps right before it) against the backbuffer right after it. `GTAIV.1` (bc_3, outside): 99.8 % of the pixels within one step. `GTAIV.3` frame 13868 (et01_aa, DOF near 5.9 m, far 7.5 m): 100 % within two steps in every depth band, the mean difference 0.25 (rounding).
- **Depth of field:** b = min(1, max(near − z, z − far, 0) / 0.5)², near and far from dofDist (see `CameraDof`), the distance z from the depth with dofProj (near clip 0.05). The near side blurs too. Without it, the pixels nearer than 2 m in `GTAIV.3` miss (mean 0.53 instead of 0.25).
- **The G-buffer's softening:** where GB2.w ≠ 0 the point first goes halfway to the filtered read at (−1.5, 0.5) texels.
  - The pass picks one of the four reads by GB2.w × 256 in (0, 2], (2, 4], (4, 8], (8, 256]. GB2 is A2R10G10B10, so a set GB2.w always picks the last.
  - The G-buffer is cleared to 0 (`GTAIV.1` call 74953082), and every program that fills it writes its alpha there: gta_default and gta_normal_spec alpha × globalScalars.x (kept only from 0.176, so at least 1/3), gta_ped 1. So it applies to everything drawn, not to the sky.
  - Without it only 79 % of `GTAIV.1`'s pixels come out within one step; with it, 3862 of the pixels on geometry fit only with the softening and 6 only without it.
- **Outside cutscenes** dofBlur.w (the toggle at 0x11A28F0) switches the whole block off, the softening with it.
- **Motion blur:** reprojection with motionBlurMatrix (c72) × gDirectionalMotionBlurLength (c80), up to 7 reads, not where the stencil copy has the player's mask. c80 is 0 in all dumped frames of the four traces, so it does nothing there.
- **In SparkIV:** the composite in `gamePostEnd` (`gtaiv-pc.js`). Where the game's G-buffer has GB2.w, SparkIV has depth < 1 (it has no G-buffer).
- **Open:** Whether the reference videos' remaining softness comes from the video compression or from the texture quality is not measured. The traces show the game's own picture without it.
- **The traces' texture quality** (2026-10-27): in `GTAIV.1` all textures lack their two largest levels, not only the sky's. The pier's 512 × 256 diffuse and normal map and 512 × 512 specular map (draw 74259883) are 128 × 64 and 128 × 128, byte for byte their third levels (within DXT rounding). Tried for all of a cutscene's textures: bc_3 sum −0.9, but bc_1 +0.15 to +0.74 in every frame and et01_aa up to +1.16 (against the same branch without it). The traces were recorded with lowered graphics settings on purpose (with HQ textures they would have been far too large; confirmed by the user on 2026-10-28), the reference videos with HQ textures, so SparkIV keeps the full textures, the clouds' noise too (its third level, tried, matched the videos better only by chance; see the sky). Other findings from the traces that may depend on the settings (render target sizes such as the water's simulation, the reflection, the shadows) are to be checked against the videos the same way.

### The water's waves and their mirror (GTA IV trace `GTAIV.1`, bc_3 s1 f79, resolved in part on 2026-10-27, #6, #13)

- **The band of specks before the shore** (#6) is not litter. It is the water's mirror of the rocks and the shore, broken up by the waves.
  - Dumps of the frame (Present 74267898) before and after the water's 316 draws (PS 0x24641060, calls 74265526–74267101) show it: before, only the sand of the sea bed; after, the speckled band right below the rock pile.
  - The water shader reads the wave texture (s0) three times (world xy × 0.002, 0.01, 0.0455) and looks the mirror (s1, the mirrored scene) up at the screen point, shifted by the slope × min(2, 0.5 / (0.03 · distance)).
  - The same holds for s1 f984 (the "line before the shore") and s3 f357.5 (the chimney's mirror shifted into a slanting line).
- **The wave simulation** (`waterTex.fxc`, step at call 74263369, PS 0x24640fc0) is as SparkIV had it: pull towards the neighbours, back to rest, kick, Verlet. Its constants waterSimParam = (1, 0.1, 0.01, 0.5, 0.5, 0.5, 1, the step = frame ms × 0.02).
  - The quad's vertex colour (D3DCOLOR at offset 24 of the vertex declaration) gives a random noise shift (R, G), the threshold 51/255 = 0.2 (B) and the kick 6/255 ≈ 0.025 (A). That is (wind · 0.75 + 0.25) · 10 · 0.01 with the wind byte 0x12DDEB4 = 0, as `FUN_00ADC280` computes it. Seen in three frames (calls 74952390, 74263367, 84163935).
  - **Size:** `FUN_00AD61C0` makes it 128 << the water quality (0x1160EB4). The trace's is 1024 × 1024, with its neighbours one texel of 1024 apart (waterTexResValue 0.25 × 1/256). SparkIV had 256.
  - **Its state:** in the trace it has long settled (spread of the heights 0.17, of the slopes 0.12). SparkIV started it flat each time a scene was made, so in the references its waves were next to nothing (spread 0.0007) and the mirror came out sharp. Run on from flat at 16 ms a step, it settles after about 15 000 steps (0.13 / 0.09).
- **In SparkIV:** `waterSimStep` in `gtaiv-pc.js` (1024 × 1024, 20 000 steps run on when it is made). Visual run: bc_3 s3 f137.5 −0.43, s1 f984 −0.34, s1 f79 −0.26; sum −1.01; worse s1 f586.75 +0.09, s2 f1055.25 +0.07.
- **The mirror's brightness and the see-through** (#13, 2026-10-27):
  - The game's water reads an HDR ReflectTexture (A16B16G16R16F). SparkIV mirrored the scene into an 8-bit picture and turned it back into light, which clipped the bright sky. Now the mirror pass also writes the scene's light (RGBA16F), and the water reads that.
  - Measured against `GTAIV.1` after the water's draws (call 74267150, bc_3 s1 f79, scene at the trace's 10:38): the water was 13–24 % too dark, rising with distance, which points at the mirror; now it is 3–14 % too dark.
  - The water's alpha as the game's: sat((d · waterColour.w · 0.25)^0.25 + 0.125), d the view depth of the scene behind less the water's; drawn after everything solid, blended over it. In the references it only shows at the very edges (the water there is deeper than 1–2 m).
  - Visual run: bc_3 s3 f137.5 −1.58, s1 f79 −1.35, s1 f984 −0.98, s3 f357.5 −0.86, s2 f437 −0.53, s2 f1055.25 −0.50; worse s2 f786.75 +0.03.
- **Open (#13):**
  - The reflection is kept on the water's side of the horizon as in the game (the point 2000 m ahead on the water's plane, projected; v at least 0.5 (1 + its y) + 0.05 × near + 0.005); in the references this changes next to nothing.
  - Which normal takes what, as in the game's shader: Fresnel and the mirror's shift take the gentle slopes, turned towards the view where the water is shallow (the shore's flattening). The sun's light and its glint take the steep ones (×4). SparkIV had it the other way round.
  - The glint is ×10 × gDirectionalLight.w × the sun's shadow. Fresnel takes 1 − cos as 1 where the view meets a wave's back (the game's cmp, not abs).
  - Visual run (bc_3): s3 f357.5 −0.22, f137.5 −0.17, s1 f79 −0.11, s2 f437 −0.11; worse s1 f984 +0.28, s2 f786.75 +0.09.
  - The mirror's content matches the game's: its ReflectTexture, read mirrored in x as the shader reads it, is 7.5–7.8 / 8.1–9.1 / 8.4–9.8 at the water's regions; SparkIV's 7.1–7.7 / 8.3–9.1 / 9.1–10.0.
  - **The view from a metre above:** the water's vertex shader passes the place + (0, 0, 1) to the pixel shader, which takes the view and the distance from that point. The view comes out flatter, so the Fresnel is larger. Measured at f79 the far water is now −7/−4/−1 %, the middle +3/+8/+12 %, the near +16/+27/+35 %. Visual run (bc_3): s1 f984 −2.84, s3 f137.5 −0.70, f357.5 −0.53; worse s1 f79 +0.59, s2 f437 +0.53, f786.75 +0.37, f108 +0.23, f1055.25 +0.21; sum −2.16.
  - Still open (before the view from above): the water was 3–14 % darker. In the comparison pictures the game's water has a bright, glittering band towards the horizon that SparkIV lacks. Steeper waves (×1.33) and 21 ms steps changed nothing; the far term (lerp by r0.z) only acts at the very shore line.
  - The game's water is still rougher and brighter. Its settled spread is about 1.3 × SparkIV's; the trace ran at 16–23 ms a frame, so its steps were larger.
  - The wind: SparkIV keeps its 0.25 (`WaterWind`); the trace has 0 (the step's kick comes from the quad's vertex colour: alpha 6/255 = (0 × 0.75 + 0.25) × 0.1 in 8 bits, its blue the threshold 0.2, red and green the noise's shift).
  - **The step to the bit** (2026-10-28): the game's step (`GTAIV.1` call 74263369, waterTex.fxc) recomputed from its own state and noise gives all of its heights and speeds (round to nearest into the 8-bit target); SparkIV's GL step with those inputs gives the same, to the bit. So the rougher water of the game (heights' spread 0.164, slopes' 0.115 at f79) is its history: from its state the same step with its kick falls to 0.13 / 0.09 within 2000 steps (≈ 30 s), as SparkIV's settles whatever its kick (0.044, 0.06, 0.08: 0.135–0.139; even the game's own kick, too small for a step of 8 bits, keeps that level once it is there). The game's water was driven harder before the cutscene, by more than its wind can give (at most 0.1); by what is not found. SparkIV settles it with a kick of 0.25 now: 0.163 / 0.109 2.6 s on (the trace's 0.164 / 0.115). Visual run (water references): sum +0.24 (the specks spread wider, as the game's, which the grey difference counts against where they don't fall on the same pixels).
  - The rest of the water shader (its colour, Fresnel, the depth fade) is not yet checked against the trace.
- **The near water's swell** (#13, 2026-10-28):
  - Recomputed per pixel with the trace's own inputs (depth s2, waves s0, mirror s1 at the first water draw 74265526), the near water came out 10–16 % brighter than the game's in a band 9–13 m off. The water's grid round the camera (the CPU swell, 2 m apart) passes its vertex normals into the slopes (r3 += v1.xy × near / v1.z). With them, read from the draws' vertex buffers, the band is gone and the game lies an even ~10 % above the mirror's term (its sun and glint).
  - The vertex normal is ≈ 0.95–0.98 × (height 2 m before − height 2 m after) (fitted on 1782 points, r 0.97–0.98), as SparkIV takes it. The game's grid has settled: heights' spread 0.10 m, normals 0.024 across and 0.065 along y.
  - SparkIV's grid started flat for every picture (8 steps), so the near water had next to no swell. Now it runs 1000 steps of 20 ms (the trace's frames: 16 to 23 ms; the kicks come per frame, so their share of the swell grows with the root of the step) when it starts, without the breeze WaterWind gives the waves' texture (with wind 0: 0.13 m, 0.015, 0.090; with 0.25: 0.23 m, 0.027, 0.159).
  - The swell's parameters in GTAIV.exe (filled at 0xADB770 for CWater::UpdateDynamicWaterTask, run at 0xADBA40) are SparkIV's: 0.2 × 100² kicks, (0.7 wind + 0.23) × 10, the push sin(y × 0.15 + 2π t / 4000) × (0.55 wind + 0.18), pull 40, rest 0.5, damping 0.7, dt at most 0.13. Settled, SparkIV's is stronger than the trace's (0.13 m; 0.012; 0.08), but 2.6 s on from flat (the trace's frame is 2.6 s into the cutscene) it is 0.093 m, 0.009 across, 0.068 along y, the trace's 0.10, 0.024, 0.065: the game's grid started flat when the cutscene's camera came (moved beyond its 200 m window, 0xAD4E80). So SparkIV runs it on for as long as the cutscene has played (at most 30 s; 20 s without one). Visual run (water references): sum −0.94 (s3 f137.5 −0.52, s2 f108 −0.31, s1 f79 −0.23); worse s1 f984 +0.15, s2 f1055.25 +0.12. The kicks across (0.009 against the trace's 0.024) stay weaker; why is open.
  - **The swell's window round the mission's marker** (2026-10-28): the point 0x93F090 gives is the player, who stands at the mission's marker when the cutscene starts (the user). For bc_3: main.sco `sub_5d16(32, 487.5043, −499.1311, 3.6922, …)` (mission 32 = Bernie3, `sub_879a(18, 2, 0, 32, …, "Bernie3", …)`), the same place bernie3 clears right before `START_CUTSCENE_NOW("BC_3")`. ±100 m round it on even metres: x 386–588, y −600 to −398. It explains the trace's grid: its east edge at 588, the water quad to the north (x 476–588, y −396 to −92) drawn flat as it starts 2 m beyond −398, the one to the south (−800 to −598) flat. The same script sets `FORCE_WEATHER_NOW(1)` (SUNNY) and `FORCE_WIND(0.0f)` right before the cutscene. SparkIV now keeps the window round the marker as well (the page's `water` message, `swellAt`; without a marker round the camera as before).
  - **What the scripts set, in the cutscene script index** (`CutsceneScriptIndex`, 2026-10-28): per cutscene the last `FORCE_WEATHER_NOW`, `FORCE_WIND` and `SET_TIME_OF_DAY`/`FORCE_TIME_OF_DAY` in the function before its `START_CUTSCENE_NOW`, and the mission's marker:
    - The registration (`sub_879a`/`sub_6f40` in GTA IV, `sub_7180` in TLAD: strand, step, trigger type, contact point, text, three numbers, script name) keeps the contact point's number in the trigger's +4 (`sub_7020`); trigger types 1 to 7 get 50, none. `sub_bb0f` reads the blip's place from the table of the function asserting "Store_Contact_Point" (`sub_5d16`) with that number. A second function with the same numbers, "Add_Map_Blip" (`sub_9a75(32, −477.8248, 1409.6919, …)`), places other blips.
    - Found: GTA IV 91 markers, 12 weathers, 2 winds, 1 hour (of 132 cutscenes); TLAD 36, 12, 0, 2 (of 49); TBoGT 30, 22, 0, 0 (of 45).
    - The weather's number is its block in `timecyc.dat` (EXTRASUNNY, SUNNY, SUNNY_WINDY, CLOUDY, RAIN, DRIZZLE, FOGGY, LIGHTNING; the same order in all three games). The cutscene view lights the location with it and the hour (else SUNNY, 12:00). elizabeta1 forces EXTRASUNNY only for ET01_AA and restores the weather after it, so ET01_BA gets none (the references' EXTRASUNNY for both stays as set).
    - A forced wind goes to the waves' texture (instead of the breeze 0.25) and to the swell. bc_3: wind 0, as the trace's quad (alpha 6 / 255).
    - The marker counts for all cutscenes of the mission's script, though the player may have moved for later ones.
    - Visual run (all references): bc_3 +0.44 in all (−0.04 to +0.14 per picture, within the swell's chance), all others 0.
  - **f984 is the swell's phase**: with the swell, the near water's Fresnel at f984 varies with the swell's time (its push sin(y × 0.15 + 2π t / 4000) is 42 m long, so a near patch tilts as a whole) from 0.53 to 0.72 (six starting times 0.7 s apart), on average 0.618, the game's shader with its own inputs and grid 0.622 (flat 0.645; SparkIV flat 0.638). The reference picture's phase is chance and not known; the regression at f984 (and the gain at f79) is that chance, not a fault.
  - (Before the marker was found, 2026-10-28) the game's grid is not round the camera. GTAIV.exe 0xAD9E20 sets a square of ±100 m (0x103F4BC, on even metres) round the point 0x93F090 gives (an entity's place, by its many callers the player's), and 0xAD97C0 cuts every water quad against it: inside into the grid (0xAD8FA0, only its visible parts), outside flat (0xAD8930). The swell's window (0xADBA40) is round the same point. In `GTAIV.1` (bc_3, f79 and the trace's frame of f984, `Present` 108626434 at 10:53:33, 1 frame = 1 game second) the grid ends at y = −498 and reaches to x 588: the point lies about 96 m south of the camera, not at the cutscene's Niko (501.4 / −504.5). So the near water north of −498, left in the picture at f984, is flat in the game; SparkIV puts its window round the camera, and its swell darkens that water: f984 near 3.18/3.67/4.01 (flat 3.93/4.51/4.89; the game 4.02/4.28/4.41; at f984 the game's grid has settled, 0.139 m / 0.011 / 0.087, as SparkIV's). Where that point lies in a cutscene (the player where the cutscene began?) is not known from the cutscene's files.
  - At f79 (the trace's frame, HDR after the water) the near water is now 3.10/3.48/3.73, the middle 4.20/4.89/5.32, the far 4.49/5.31/5.86 (game 3.05/3.18/3.22, 3.95/4.39/4.62, 4.81/5.47/5.85; before +16/+27/+35 % near). With the swell's wind at 0.25 the near water came out 2.87/3.15/3.33.
  - Visual run (the water's references, against no warm-up): s1 f79 −2.00, s3 f357.5 −1.38, s2 f437 −0.63, s2 f786.75 −0.32; worse s1 f984 +1.85 (its open water darker than the game's), s2 f108 +0.31; sum −2.28.
- **What the water mirrors** (#13, 2026-10-27):
  - The game draws its ReflectTexture from the far versions only, even beside the camera: of the 59 vertex buffers of its draws (`GTAIV.1`, 74262969–74263286) 50 aren't in the main pass, the 9 others are far versions it shows there too (the Brooklyn Bridge's, 100 m and more off). By their places in the WPL they are `LOD_P17_Port_MH1`, `LOD_P17_SW_MH1`, `LODKs_coastwall_01`, `LODBBrijSup2_Ks` and the like. What has no far version (`EC_hbr_bench_2`, `P17_PortHood_MH1`) is not mirrored, nor are people or vehicles.
  - SparkIV mirrored the near models, the shore's ground among them (the dark land in the water). Now it mirrors the far versions, also those only meant for the map of the surroundings (`MirrorOnly`).
  - A far version is any placement another names as its LOD (`SupLOD_…`, `…_lod` as well), not only one named `lod…`. With names alone the quay's and the ship's far versions were missing from bc_3 s2 f786.75's mirror. This also applies to the map of the surroundings.
  - Of the far versions not the ground (2026-10-28, the user's observation from the videos): of Coney Island's beach far versions the game draws only the parts without the terrain's layered shader. `LOD_ci6_7_beach05` with one draw of 424 vertices, the rocks `LOD_ci6CI7_rks07` and `rks08` with 2351 and 3110, `beach08` with 19; their sand (669 to 828 vertices, layered in SparkIV) never, nor `beach04`, `beach06`, `beach07`, `beach10` at all (all of them with the same IDE flags 12582912). In its mirror the pier, the huts and the rocks stand over no land. SparkIV now leaves layered terrain out of the mirror. Visual run (water references): sum −1.12 (s3 f357.5 −0.81, s1 f79 −0.20; with the swell's 20 ms steps, see below); worse s3 f137.5 +0.12.
  - Cut half a metre under the water, as the game's clip plane (set at 74262927: in the world z ≥ −0.515 with the water at 0). What stands just under the water shows in the mirror too: the pier's dark lower edge in the corner at f79 (game 1.15, SparkIV now 0.32, before sky at 7.3).
  - **The mirror's specks** (the user's observation, 2026-10-28): the game reads its wave texture with this one level only (the water draw's SurfaceTextureSampler, 1024 × 1024, no smaller levels, MINFILTER LINEAR). Far off, each pixel takes the slope at its own point, so the mirror breaks up into specks. SparkIV made mipmaps of it and read them averaged, so its mirror stayed smooth in the app. Visual run (water references): sum −0.14; at f79 the rocks' and the pier's mirror now breaks up into specks as the game's, a little fainter and less spread.
  - Lit as the game's pixel shader for the ReflectTexture (0x1A3F8A40): the texture × (the light from around × AO + the sun × saturate((n.l − 0.25) × 4/3)), the far fog over it; no shadow, no shine, no lamps. The same as for the map of the surroundings; the constants (c18, c37, c38, c45) are SparkIV's.
  - Only front faces (CULLMODE CW for all its draws, 74262957–74263295): from below SparkIV showed the sunlit tops of the far ground and of the pier.
  - Visual run (bc_3, against the mirror of the near models): s3 f357.5 −0.36, s2 f786.75 −0.12, s1 f984 −0.09; worse s2 f437 +0.11, s1 f586.75 +0.06; sum −0.48. TLAD et01_ba (the map of the surroundings) +0.01 to +0.05.
  - Open: at f79 the game's mirror has a dark underside of the pier in its corner (1.2), SparkIV's sky there; the shore's far version behind the bridge stays in SparkIV's mirror, in the game's it is gone in the fog.

### `[DRAW_DISTANCE]` in the `.cut` (GTAIV.exe, resolved on 2026-10-13)

- **Parsing:** `Cut_ParseFile` (0x9C3840) reads each line in block state 0x13 as `%d %*d %f %f`.
  - The line yields a 12-byte entry: +0 time in ms, +8 the distance, +4 the fourth number.
  - If the fourth number is −1 or missing, +4 becomes 0.05 (`0x1048AB4`).
  - It is stored in the list `0x1294760` with the counter `0x1295724`, at most 100 entries.
- **Use:** `FUN_009CA620` uses the entries. Ghidra's analysis had not recognised the function as code; it was found via a byte search for the addresses.
  - It selects the last entry whose time has been reached (time ≤ `0x1295748` + `0x1295740`).
  - It then calls `0xC61D80(index, distance, fourth number)`. Without a matching entry, for example when the block is missing, it calls `0xC61D80(index, 0, 0)`.
  - `0xC61D80` (thiscall) writes into the object `this[0x140 + index·4]`: +0x154 = distance, +0x158 = fourth number.
  - Assumption: the object is the camera. Then +0x154 would be the far clip and +0x158 the near clip distance; the default value 0.05 matches the measured `dofProj` near of 0.05.
  - This is not yet confirmed. In GTAIV.1.trace, for bc_3, frame 870, the far value of `dofProj` was 1500, although the block specifies 185 m there.
  - **Video finding 2026-10-23:**
    - In bc_3, section 3, frame 828 (step 89.2 m) the game shows nothing of the part of the large bridge beyond about 89 m.
    - SparkIV drew the mesh completely because it has near parts. Since 10-23, SparkIV clips per pixel beyond the step via the cutscene camera (`drawReach` in ModelView.html, not for water).
    - This matches the video and improves 4 of 5 checked references.
    - Whether the game uses the projection, a clip in the shaders or something else for this is not confirmed (see the deviating `dofProj` value above).
- **Chain (statically confirmed):**
  - Every frame, `0x9CB0F0` gets the cutscene camera controller via `0xA86AC0(0x103E498)`. It then calls `0xC61B30` and `0x9CA7E0` (the lights, see above) as well as `0x9CA620`.
  - The object `this[0x140 + section·4]` is the camera of the section. It is created in `0xC61AD0` via `0xA86C40(0x1F, …)` as camera type 0x1F and is named `CutCam%d`.
  - Distance and fourth number are therefore fields of this camera (+0x154, +0x158).
- **Reader (resolved):** `0xD53280`, a method of the `CutCam` (built by `0xD527E0`).
  - If the distance is 0, the method sets the flag +0x1B4 on the top camera (`0xA7C9F0`) and 800 as the far value of the frame (+0x68).
  - Otherwise it takes the distance as the far value.
  - The near value (+0x64) is the fourth number, or 0.05.
- **Comparison with the time cycle:** `0xC46050` compares the far value with FarClp from the time cycle (`0x15E8954`, column 23; the fog in `0xAF3A50` reads the same value).
  - Without the flag and as long as far ≤ FarClp, the far value stays; otherwise FarClp applies.
  - With the flag, i.e. without a block, FarClp applies.
  - A negative value is a factor on FarClp.
- **Result:** View distance = min(block value, FarClp), without a block FarClp. This is how SparkIV now implements it (`CutsceneView.xaml.cs`).
- **Missing block:** `et01_ba` (TLAD) has none. SparkIV then reports "draw distance 0" and loads no distant models; the game then passes 0, 0.

### Vehicle colours in cutscenes

- **In the game** (GTAIV.exe): `[VEHICLE_DETAILS]` is `Model Colour1 Colour2 Colour3 Colour4 Dirt [7th number]` (`Cut_ParseFile`, stored from section +0xF08C). These values are only the fallback:
  - `CCutsceneObject_Init` -> `0xC67450` first searches for a vehicle of the same model in the game world (pool `0x12E22A4`).
  - If it finds one, it takes its colours and dirt and sets +0x2B1; the `.cut` colours then no longer apply (`Cut_CreateSectionObjects` -> `0xC677E0` -> `0xD25D80`).
- **In SparkIV:** The index `CutsceneScriptIndex` assigns to each cutscene the mission script that starts it (`START_CUTSCENE_NOW`). The colours come from its `CREATE_CAR` and the following `CHANGE_CAR_COLOUR`/`SET_EXTRA_CAR_COLOURS`; without them the `.cut` colours apply.
  - Example: `elizabeta1` creates the Presidente for `et01_ba` with the colours 0/0/0/0 (black).
  - The index is built the first time the game is loaded in SparkIV, in the background with a message in the InfoBar. It is stored in `%LocalAppData%\SparkIV\CutsceneScripts\`.
  - It is rebuilt when the `script.img`, the SparkIV build or the index format changes.
- **Unresolved or simplified:**
  - If a script creates several vehicles of the same model, the first one with colours applies.
  - Colours set via variables instead of as numbers are not recognised.

### Episodes (TLAD, TBoGT)

SparkIV recognises an episode by a `content.dat` in its folder and GTA IV's folder directly above it. An episode only extends GTA IV, so SparkIV loads GTA IV's files as well, like the game. Its lists `content.dat` and `images.txt` name only its own files.

- **Map (`.ide`, `.wpl`, `.img`):**
  - The episode's files come first; its models take precedence.
  - A GTA IV archive with the same path (`east\bronx_w.img`) is read for its models, but not for its placements.
  - Result for `et01_ba`: 0 instead of 29 missing models.
- **`carcols.dat` and `vehicles.ide`:** Both files are read, the episode's first. The colour table (`col`) comes from the first file that has one; TLAD's file contains none.
- **Time cycle modifiers:** If the episode lacks its own file, SparkIV takes GTA IV's.
- **`.ide` sections `anim` and `tanm`:** Animated objects have their animation dictionary in third position; after it follow the same fields as in `objs` and `tobj`. An example is `LOD_BAWSAQ_01_DC9, LODs_04_DC9, manhat09, 700, …, slodmh96mh98_dc9_grp`. SparkIV now reads both sections, but shows the objects in their rest pose, not animated.
- **Sound:** It is determined by `CutsceneSound.Find` like the game (`0x988990`).
  - The game forms `"CUTSCENES_" + [AUDIO] name` in upper case.
  - With this it searches for the sound in the sound metadata, i.e. `sounds.dat15` or the `<sounds>` file from `e1_audio.xml`/`e2_audio.xml`.
  - The bank path `<Pack>\<Name>` names the `.rpf` in `pc\audio\sfx`.
  - The object `CUTSCENES_<Name>` in `game.dat` contains only hashes of mix categories (`cutscene_positioned_radio`, `ambience` …), no bank.
  - Checked for all cutscenes: GTA IV 153/153; TLAD 68/68 (66 in `EP1_SFX.rpf`, 2 in GTA IV's `cutscenes.rpf`, 13 without sound); TBoGT 50/104.
  - The 54 remaining TBoGT cutscenes are partial or old versions (`gt01_aa`, `f1aap1`–`5`, `e2int` …). The game's metadata has no sound for them either; the played versions are named `gt01_za`, `f1_aa`, `e2_int` etc.
- **Unresolved:**
  - `DISABLE_FILE` in `content.dat` is not evaluated.
  - Whether the game merges `.ide` files of the same name from both games has not been checked.

## `common/data/visualSettings.dat`

Not yet fully recorded. Known so far:

| Key | Status | Content / source |
|---|---|---|
| `misc.DOFBlurMultiplier.HD` / `.SD` (1 / 0.5) | used: the HD value 1 | Strength of the depth of field. Read at 0xAF2210, stored in 0x103F9B0. Measured with x32dbg on 2026-10-23 at the settings of the reference videos: DOF_BLUR = (1, 0, 1, 0) during a cutscene; it also fits the reference videos better than 0.5 (7 frames). An earlier x32dbg read (2026-10-11, other settings) gave the SD value 0.5; the TLAD trace (et01_aa, 1920×1080, call 102289045, lowered settings) has 1 and TexelSize = (1/1920, 1/1080), so the 4 taps are only ±1.5 pixels apart. Which setting switches between HD and SD is open (0x424000 not readable in Ghidra). |
| `misc.BloomIntensityClamp.HD` (10) | used | 0x103F9B4, clamps the bloom. |
| `misc.HiDof.nearBlur/midBlur/farBlur`, `misc.cutscene.nearBlurMin` | **unclear** | Not used. Whether the PC version uses them is unresolved. |
| `misc.CrossPMultiplier.*` | **unclear** | Not used (RimLight 0.7, Gamma 1.15, Desaturation 1.29 …). |

## Other formats and tools

Not yet recorded. Each format gets its own section following the same pattern: models (`.wdr`,
`.wdd`, `.wft`), textures (`.wtd`), collision (`.wbn`, `.wbd`), time cycle (`timecyc.dat`),
`visualsettings.dat`, shader parameters, water, lights.
