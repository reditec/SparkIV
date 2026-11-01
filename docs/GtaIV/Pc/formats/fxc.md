# GTA IV (PC): Compiled shaders (`.fxc`, `rgxa`)

Applies only to **GTA IV, PC version** (with TLAD and TBoGT). Overview of all formats: [../data-support.md](../data-support.md).

The game's shaders are in `common/shaders/win32_30/` (102 files; the folders `win32_30_atidx10`, `_low_ati`, `_nv6` to
`_nv8` hold the same shaders for other cards). Each file holds a shader's programs, its parameters and its techniques,
with each pass's render states. `tools/fxc-dump.py` reads them all (format below) and writes per shader its techniques,
parameters and disassembled programs; `--states` lists every technique's pass states with the shaders that use them.
Since 2026-10-31 (#74).

## What it answers

What a trace showed only draw by draw is in the files for every shader at once:

- **Which programs a technique uses** (`draw`, `unlit_draw`, `deferred_draw`, `deferredalphaclip_draw`,
  `reflection_draw`, `paraboloid_draw`, `wd_draw`, `imposterdeferred_draw`, `lightweight0_draw`, ... and their
  `skinned` and `_inst` versions), and what each program computes.
- **The render states a pass sets itself.** Most of the materials' techniques (`draw`, `deferred_draw`,
  `reflection_draw`) set none: those the engine sets (the G-buffer pass, its draw buckets). Where a pass sets them,
  the game's draws show them: the glow of see-through emissives below, checked against `GTAIV.trace` and
  `GTAIV.1.trace`.
- **The parameters** with their registers, defaults and UI names (`emissiveMultiplier`: "Emissive HDR Multiplier",
  0 to 255).

The presets in `common/shaders/db/*.sps` name a shader and its draw bucket, e.g. `gta_emissive_alpha.sps` =
`gta_emissive` in bucket 5, `gta_glass_emissive.sps` = bucket 4.

## The glow of see-through emissives (`unlit_draw`)

A see-through emissive's glow is its `unlit_draw` technique: texture × vertex colour × `emissiveMultiplier` ×
`colorize`, alpha texture.a × vertex.a × `globalScalars.x`, never lit. Its blend is the pass's own:

| Shader | `unlit_draw`, `paraboloid_draw` |
|---|---|
| `gta_emissive` | SRCALPHA / **INVSRCALPHA**: blended over what is behind it |
| `gta_emissivenight`, `gta_emissivestrong`, `gta_glass_emissive`, `gta_glass_emissivenight`, `gta_normal_spec_reflect_emissive`, `gta_normal_spec_reflect_emissivenight` | SRCALPHA / **ONE**: added |

In the traces: Bernie's flat (`GTAIV.trace`, bc_1 s2 f1174, the frame from 129300040) draws the ceiling spot
(`gta_emissive`, multiplier 16, draw 129309611) with PS 0x2d0b1c40 = `gta_emissive.fxc` ps 2 and
SRCALPHA / INVSRCALPHA, the table lamp's `gta_emissivestrong` (12) with SRCALPHA / ONE; bc_3's pier wall lamps
(`GTAIV.1.trace`, `gta_emissivestrong`, 88298834) with SRCALPHA / ONE. `gta_emissive`'s ps 2 also scales its alpha by
`globalScalars.x` and `.y` for fading (1 and 1 in those frames: alpha unchanged).

## Shine read from the G-buffer programs

The G-buffer pass (`deferred_draw`) writes the shine into GB2: GB2.x = half the strength, GB2.y² × 512 the power
(the sun pass takes them back as 2 × GB2.x and GB2.y² × 512).

- `gta_normal` and `rage_default` shine **without a specular map**: GB2.x = 0.5 × `specularColorFactor`, GB2.y =
  sqrt(`specularFactor` / 512), as `gta_normal_spec` with a white map. bc_3's picnic basket (`csHamper`, `gta_normal`,
  `GTAIV.1.trace` G-buffer draw 188259798: `specularColorFactor` 0.12, `specularFactor` 15): the game's light after
  its sun pass (188264545) 1.57 / 1.46 / 1.20, SparkIV 1.40 / 1.37 / 1.18 with it, 1.23 / 1.20 / 1.00 without.
- The decals `gta_decal_glue`, `gta_normal_decal` and `gta_decal_dirt` the same, without a map (blended by their alpha;
  glue writes GB2 whole, the other two only GB2.xy, keeping the AO beneath: colour-write masks 0x7 and 0x3).
- The vehicle shaders (strength factor, power factor, as `GtaIVPcMaterials.VehicleShineOf` keeps them): paint1–3 and
  shuts (0.15, 190), mesh (1, 50), tire (1.4, 50), interior and interior2 (0.005, 10), badges and lightsemissive
  (0.225, 190, the strength at most 1), vehglass (0.225, 190, forward), chrome the material's own
  (`specularColorFactor`, `specularFactor`). Tire's and shuts' bumps are × 3 and × 5, not × `bumpiness`.

## AO read from the G-buffer programs

GB2.z is the AO the sun and ambient pass dims the light from around by.

- Most map shaders: the vertex colour's (red by day, green by night, by `gDayNightEffects`; terrain the same, worked
  out in its vertex program).
- `gta_trees`: always 0.5 (GB2 = 0, 0.5, 0.5, 0; its vertex program works out the vertex colour's AO, the pixel
  program doesn't use it). Albedo the texture alone, stencil 3.
- `gta_emissive`, `gta_emissivestrong`, `gta_emissivenight` (their solid G-buffer pass): `globalScalars.z` (0.996 in
  the traces), not the vertex colour, which only colours their glow. Albedo texture × `colorize`, no shine.
- Every `gta_vehicle_*`: the vertex colour's luminance, dot(colour, `LuminanceConstants` 0.2125 / 0.7154 / 0.0721) ×
  `globalScalars.z` (`lightsemissive`: `globalScalars.z` alone). The albedo is × the vertex colour as well.
- `gta_ped_skin(_blendshape)`, `gta_ped`, `gta_ped_reflect`: the vertex red plus the rim² (see `outsideLight` in
  `gtaiv-pc.js`).
- The decals keep the AO beneath (colour-write masks, above); `gta_decal_dirt`'s own GB2.z (0) never reaches it.

## Cut out in the G-buffer

The map shaders' `deferred_draw` kill what has texture alpha × vertex alpha × `globalScalars.x` up to **45 / 255**
(`def c0.x 0.176470593`, `texkill`) **whatever their bucket** (0 opaque, 3 cut out, 4 emissive): `gta_default`,
`gta_cutout_fence`, `gta_spec`, `gta_normal`, `gta_normal_spec`, `gta_reflect`, `gta_normal_reflect`,
`gta_spec_reflect`, `gta_normal_spec_reflect`, both cube-map ones, `gta_parallax_specmap`, `rage_default`,
`gta_emissive`, `gta_emissivenight`, `gta_emissivestrong` (one shared program). Not: the decals, glass, `gta_trees`
(stipple, below), the peds, hair, terrain, vehicles. Since 2026-11-01 SparkIV cuts them out in the opaque bucket too
(`GtaIVPcMaterials.CutsInGBuffer`), with the vertex alpha.

The peds' skin G-buffer pass runs with the engine's alpha test (ref 100, `GTAIV.trace` bc_1 f940) on its output alpha
`globalScalars.x` × vertex alpha (not the texture's: its alpha holds the subsurface mask); with the engine's
`ALPHAFUNC` `ALWAYS` (see Hair) it cuts nothing anyway.

## Hair

`gta_hair_sorted_alpha_exp`'s `deferred_draw` has two passes: ps 5 writes only zeros (colour-write alpha only, depth
writes off), ps 6 the hair: albedo texture × `matMaterialColorScale`, alpha texture.a × vertex alpha ×
`globalScalars.x`, the ped's bent normal, GB2.x = 0.5 × rim². Neither has a threshold of its own (only the stipple of
`globalScalars.x`). The engine draws Niko's hair (`GTAIV.trace` bc_1 f940, from 103011448) with ps 6 opaque into the
G-buffer under its alpha test at ref 0, then alternately ps 5 and ps 6 blended (SRCALPHA / INVSRCALPHA) on the back
faces; no forward pass. `gta_hair_sorted_alpha`'s `deferred_draw` (ps 5) the same without the second pass.

**The engine's alpha test cuts nothing:** its `ALPHAFUNC` is set to `D3DCMP_ALWAYS` at start-up (`GTAIV.trace` call
14906, after `NOTEQUAL` at 3345 and 6369) and never again up to bc_1 f940 (no `D3DRS_ALPHAFUNC` from call 60000 to
102990940). So the alpha tests at ref 0 (hair) and ref 100 (`gta_ped_skin`'s G-buffer pass, the peds' and hair's
forward passes) pass everything; what is cut out is cut by the programs' own `texkill` (the 45 / 255 above, the
stipple). SparkIV keeps cutting hair out at 45 / 255 all the same: cut only at alpha 0, bc_3's hairlines came out
with a hard dark edge where the game's fade softly into the skin (the blended back-face passes), 7 of 49 references up
to +0.06 worse (2026-11-01); hair not cut at all the same (+0.06).

## Trees

`gta_trees`' `deferred_draw` (ps 1) has no alpha threshold: the texture's alpha picks a level of
`platform:/textures/stipple.wtd` (alpha × 3.996: the row its whole part, the column a quarter of the rest, as
`gta_ped`) and keeps that level's share of each 8 × 8 pixels (0, 6, 8, 12, 11, 12, 16, 24, 32, 40, 48, 52, 52, 56, 58,
64 of 64). The engine draws it twice, CCW and CW (`GTAIV.trace` f940), and the normal is written as the vertex's on
both sides (not turned to the camera). GB2 = 0, 0.5, 0.5. SparkIV: ordered 8 × 8 pattern with those shares.

## Mirrored surroundings (`*_reflect`)

Added to the albedo before it is lit (`mad oC0, env, weight, albedo`):

- env = `EnvironmentSampler` at −0.5 (R.xz / |R| + 1), R the view mirrored by the normal: the vertex normal for
  `gta_spec_reflect`, the **bent normal before the G-buffer's rounding** for `gta_normal_reflect`,
  `gta_normal_spec_reflect` and the glass.
- weight = dot(spec map, `specMapIntMask`) × `specularColorFactor` × `reflectivePower` where the shader has
  `specularColorFactor`; `reflectivePower` alone for `gta_reflect`, `gta_normal_reflect` (and its decal),
  `gta_normal_cubemap_reflect`.
- The cube-map ones (`dcl_cube`): texCUBE(`EnvironmentSampler`, R.xzy). But their `EnvironmentSampler` is an ordinary
  2D texture (et01_aa's `eliza_loungestuf` / `ab_metalsheet`: 256 × 256 DXT1), and the game creates no cube texture at
  all (`GTAIV.3.trace` up to et01_aa's frame: no `CreateCubeTexture`): a 2D texture on a cube sampler, undefined in
  D3D9. SparkIV keeps looking it up as the other `*_reflect` (no visible change either way in the references).
- Forward glass (`gta_glass`, `gta_glass_reflect` `draw`): env × `specularColorFactor` × `reflectivePower` ÷ alpha
  (so it shows whole on clear glass), then lit.

## Forward programs (`draw`, `lightweight0_draw`, `lightweight4_draw`)

What is drawn see-through (bucket 1: map materials, glass, the peds' see-through parts, vehicle badges and lights) is
lit by its own `draw` program, all on the same set of constants: the light from around `gLightAmbient0` +
`gLightAmbient1` × saturate(0.5 − 0.5 n.z) × the vertex AO, the sun `gDirectionalColour` × `.w` × saturate((n.l −
0.25) × 4/3) × its shadow (12 taps of `gShadowZSamplerDir`), 4 spots (`gLightPos*`, `gLightDir*`, `gLightColR/G/B`,
falloff ((1 − d² × falloff)⁴ − 0.1) / 0.9, cone `gLightConeScale/Offset`) and 4 more (`gLightPointPos*`,
`gLightDir2*`), then `gDepthFxParams`' desaturation and the fog. `lightweight0_draw` is the same without the lamps,
`lightweight4_draw` with the first 4 only (the engine picks them by how many lights reach the object).

The shine forward is not the G-buffer's: s = dot(spec map, `specMapIntMask`) × `specularColorFactor` (flat:
`specularColorFactor`), the sun's s × sun × shadow × (saturate(R.l) + 1e-4)^(spec.a × `specularFactor` + 1e-4) ×
`gDirectionalLight.w` (half the sun pass's, which doubles it), each lamp's s × its light × saturate(n.l) ×
|R.l|^(spec.a × `specularFactor` / 4); no mirrored sky (`gta_spec`, `gta_ped`'s `draw`; the peds' without the rim and
the 0.75 on the power their G-buffer pass has). The `*_reflect` ones add the mirrored surroundings to the albedo divided
by the alpha (below 0.01: × 100). SparkIV since 2026-11-01: the map's see-through materials (`forwardLit`), its glass
and the peds' stippled parts so.

`gta_vehicle_badges` and `gta_vehicle_lightsemissive` (bucket 1, forward): f = (1 − |v.n|)⁴, s = min(dot(spec map,
`specMapIntMask`) × `reflectivePowerED` × 0.225, 1) × saturate(0.5 f + 0.5); the sun's s × sun × `gDirectionalLight.w`
× shadow × (saturate(R.l) + 1e-4)^(spec.a × `reflectivePowerED` × 190 + 1e-4), the lamps' with the power ÷ 4, the
mirrored map (level 0) × saturate(5 R.z) × (1.6 f + 0.4) × 18 × `reflectivePowerED` × AO × s². `gta_vehicle_vehglass`
the same with its own factors (see `vehicleShine` in `gtaiv-pc.js`) and divided by the alpha.

## Other materials

- **`gta_parallax_specmap`**: texture and normal map at uv + (height × s − 0.5 s) × V.xy, height the normal map's
  alpha, s = `parallaxScaleBias`, V the normalised view to the camera in the tangent frame (its vertex program's o8:
  tangent, cross(tangent, normal) × w, normal); the specular map at the unmoved uv. bc_3: `BBrijSup2_Ks_01`.
- **`gta_vehicle_paint3`**: albedo = lerp(texture × `matDiffuseColor`, `TextureSampler2` × `matDiffuseColor2` on the
  second texture coordinates, by its alpha × `matDiffuseColor2.w`) × vertex colour; `matDiffuseColor2` (2, slot, slot)
  a vehicle colour as `matDiffuseColor`'s. TBoGT e2_int only.
- **`gta_vehicle_lightsemissive`**: `draw` adds r5 × `emissiveMultiplier`, r5 from its vertex program: `dimmerSet`
  (c208) × `switchOn` (b8) — the vehicle's lights on or off. SparkIV draws them off (when the engine switches them on in
  a cutscene is not known).
- **`gta_emissivenight`** glow: texture × vertex colour × `emissiveMultiplier` × `gDayNightEffects.w` × `colorize`
  (none at noon).
- **Decals** (`gta_decal_glue`, `gta_decal_dirt`, `gta_normal_decal`, `*_decal`): blended into the G-buffer
  (SRCALPHA / INVSRCALPHA, depth writes off; `GTAIV.trace` f940). `gta_decal_dirt`: albedo the vertex colour, alpha
  dot(texture, `dirtDecalMask`) × vertex alpha × `globalScalars.x`, the normal not written (GB1 alpha 0).

## Shaders in the cutscenes

51 shaders are used by the reference cutscenes (bc_1, bc_3, the intro, et01_aa, et01_ba) and five TBoGT ones (e2_int,
gt01_aa, bu01_aa, f1_aa, br01_aa); TLAD and TBoGT have no shader files of their own. Their G-buffer passes are 44
different programs, their forward `draw` passes 36, their `unlit_draw` passes 11. All checked against SparkIV on
2026-11-01 (the sections above); not drawn as the game: a vehicle's lights switched on (`gta_vehicle_lightsemissive`),
the moon and the stars (none by day), the hair's passes (approximated, see Hair); taken from runtime state SparkIV
doesn't have: the water's dynamic height map (taken as calm) and the clouds' noise `Phase`.

**Normal maps** (`gta_normal*`, `gta_spec*` with a bump map, the decals, props): the G-buffer pass tilts the normal as
the peds' does: the map's x and y (its alpha for x where 1 − alpha − x ≥ 0), k = (xy − 0.5) × `bumpiness`, z = sqrt(|1 −
x² − y²|), normal × z + tangent × k.x + binormal × k.y with the binormal cross(tangent, normal) × w (deferred_draw vs 4 of
`gta_normal_spec`). Since 2026-11-01 SparkIV does this for the map and props too, along the vertex tangents (before,
their bumps were off).

**How the engine draws them** (`GTAIV.trace`, bc_1 frame 940, every draw's shader mapped to its `.fxc` program by
its disassembly): bucket 0 and 3 by `deferred_draw` into the G-buffer, opaque (the trees both faces); decals blended
into it; see-through (bucket 1) by `draw` forward, SRCALPHA / INVSRCALPHA, after the light passes (`gta_spec_reflect`,
`gta_glass_reflect` by `lightweight4_draw`, others `lightweight0_draw`: the same programs with fewer lights);
emissives' glow by `unlit_draw`; the mirrored surroundings by `paraboloid_draw`; the trees' far versions by
`imposterdeferred_draw`.

## The other shaders of a frame

Every draw of a whole frame mapped to its program (`GTAIV.trace` bc_1 s2 f1174, inside; `GTAIV.1.trace` bc_3 f975,
outside), in the order the game draws them:

| Shader: technique (program) | What | SparkIV |
|---|---|---|
| `shadowZDir`, `cascadeGen`, `cascadeConvert` | the sun's shadow cascades | `shadowFragment`, `gameSunShadow` |
| `shadowZ` (ps 1) | lamps' shadow maps | `lampShadowFragment` |
| `shadowSmartBlit`: `shadblit` (ps 3), `clearf`, `stencilmask` | the sun's shadow onto the screen (12 taps) | `gameSunShadow` |
| `rage_perlinnoise` | the clouds' noise | `perlinNoise` |
| `gta_atmoscatt_clouds`: `draw_withstarfield` (ps 8), `draw_withstarfield_withmoon` (ps 9) | sky, clouds (ps 9: and the moon) | the sky (ps 8); the moon not drawn |
| `gta_atmoscatt_clouds`: `dpskyfront/back_draw`, `paraboloid_draw` (ps 1, 5) | the sky in the mirrored surroundings | `drawSky(…, toMap)` |
| `deferred_lighting`: `refMipBlur` (ps 40, 41) | blurring the mirrored surroundings' levels | `mirroredAround` (level by power) |
| `deferred_lighting`: `lightShadowDirectional` (ps 1, 2), `lightNoDirectional` (ps 3, 4) | sun and light from around (outside / inside) | `outsideLight` |
| `deferred_lighting`: `lightVolumePoint` (ps 6–9), `fillerVolumePoint` (ps 10–12), `lightVolumeShadowPoint` (ps 14), `fillerVolumeShadowPoint` (ps 15) | lamps and a cutscene's lights | the lamps' loop in `ModelView.html` |
| `deferred_lighting`: `corona`, `paraboloid_corona` (ps 24, 25) | coronas | coronas |
| `deferred_lighting`: `stencilVolumePoint` (ps 5), `gbufferDepthCopy` (ps 34) | stencil and depth only | — |
| `water` (ps 2), `waterTex` | water | water |
| `rage_postfx`: `GTACompositePostFx` (ps 1–8, 11, 14, 20) | eye adaptation, bloom, tone mapping | `gameToneMap` |
| `gta_im`, `rage_im` | fades, blits | the fade |
| `rage_billboard_nobump` | down-sampling | — |

TLAD's frames (`GTAIV.3.trace` et01_aa, `GTAIV.2.trace` et01_ba) add `deferred_lighting`'s `lightShafts` (ps 18, 19:
the lamps' light volumes, SparkIV's `gameLightVolumes`) and `rage_postfx` ps 26 (the composite with TLAD's film
grain), and the map drawn into the water's reflection by its shaders' `reflection_draw`: gta_default's ps 8 = texture
× (the light from around × AO + the sun × saturate((n.l − 0.25) × 4/3)), no shadow, shine or lamps, the far fog
over it, alpha texture × vertex × `globalScalars.x`, blended and stippled by it (SparkIV blends without the stipple).

Checked instruction by instruction against SparkIV (2026-11-01): `lightShadowDirectional` ps 1 (= `outsideLight`,
`pedShine`), `lightVolumePoint` ps 6–9, `fillerVolumePoint` ps 11, `corona` ps 24, `GTACompositePostFx` ps 1–8, 11, 14,
20, 26 (the eye's adaptation, the bloom's blur and threshold, the depth of field, the fog; the motion blur's
`gDirectionalMotionBlurLength` 0 in the traces), `reflection_draw`, the shadows, the water, the sky and the clouds'
noise (sections above). `refMipBlur` (ps 40, 41: the mirrored surroundings' levels) is SparkIV's mipmaps.
`lightNoDirectional` ps 3 (inside) is the sun pass without the sun's shadow and shine; inside the game's sun is 0
anyway (`GTAIV.trace`: `gDirectionalColour` (0, 0, 0, 0) from call 83201451 on, bc_1's BernieCrane modifier; SparkIV's
room modifiers give the same).

(SparkIV's comments number the programs by another count in places: `deferred_lighting`'s "sun pass, programs 21
to 24" is `lightShadowDirectional` / `lightNoDirectional`; `rage_postfx`'s "program 12" is ps 11, "21" ps 20,
"25 and 27" ps 24 and 26. The shader addresses its comments cite are from replays (d3dretrace), not the traces.)

## Sun shadows

`shadowZDir` (`wd_draw`, `wd_masked_draw`, `cascadeGen`) writes the height along the sun, `gShadowMatrix`'s fourth
column (camera-relative: in `GTAIV.1.trace` −160.66 = the camera's own), into R32F at 4096 × 4096 per cascade, its
depth 1 − min(z, 1); no texture, nothing cut out. `cascadeConvert` ps 1 clears to −100, ps 2 averages 2 × 2 into the
2048 × 2048 R16F map the sun pass reads (12 taps, lit where the point is as high or higher). The see-through, decal
and cut-out materials draw into it by their own `wd_draw` (`GTAIV.2.trace` et01_ba: 334 draws into the 4096 target
after 2147 `shadowZDir` ones): height out, alpha texture.a × `globalScalars.x`, blended SRCALPHA / INVSRCALPHA, depth
written; the engine's alpha test (ref 0) cuts nothing. `gta_trees`' `wd_draw` has its own states: no blend, no alpha
test, both faces — the trees' whole cards cast. The peds' (`gta_ped`, hair) write their height without the texture.
SparkIV since 2026-11-01 the same (`shadowTarget`, `shadowConvert`): the map's objects kept per cascade at 4096 (drawn
again when the cascades change), the cast each frame at 4096, the mean of max(map, cast) into R16F.

## Water

`water` ps 2 (pass 1; all frames): slopes from the simulation's texture (`SurfaceTextureSampler`, blue and alpha) at
three scales — ×0.002 by 0.0512 × (1 − near) for **both** normals, ×0.01 by 0.256 / 1.024, ×0.0454545 by 0.11636 /
0.46545 × near (near = max(1 − d² × 0.0004, 0)) — plus the vertex normal's slope × near; the shore by the view depth
(clip w); Fresnel (1 − cos)⁵ × 0.6 + 0.2; the reflection shifted by the gentle normal × min(0.5 / (0.03 × view depth),
2), kept below the horizon's line; the glint (saturate(R.l) + 1e-4)^512 × sun × shadow × `gDirectionalLight.w` × (10 −
9 × the near field's weight^0.25); the shadow read a metre up, that metre × near; `waterColour` × 0.5 ×
`bottomSkyColour.w` + 0.05 × the sun's light; alpha saturate((depth behind × `waterColour.w` × 0.25)^0.25 + 0.125).
Its vertex program passes o1.w = saturate((z − 1 − `waterRenderSimParam.z`) × 0.5) (0 in the traces): water higher up
mirrors `globalFogColor` instead. Within 30 m the mirror's normal is drawn towards the dynamic height map's
(`HeightMapSampler`, 56 m across) and the colour towards a foam colour 3 × (sun × f.x + light from around × f.y), by a
weight from the water's vertex colour (water.dat's four numbers after x y z, the sea's 0 0 1 0.99) and the heights.
SparkIV since 2026-11-01 the same (its big mirror slope was 0.2048 before, from none of the programs), the near field
with the height map taken as calm (its normal up, lying on the surface: the vertex weight (min(red⁴ × 16, 1) × (1 −
|n.z|⁶⁴) × 0.75 + 0.25) × blue; the height map is a vertex texture, not in d3dretrace's state dumps). bc_3's harbour
quads have 0 0 0 0 there: no near field, as in the game.

## Sky

`gta_atmoscatt_clouds` ps 8 (and 9, 1, 5): as SparkIV's sky, and the stars (`StarFieldSampler`, `GalaxySampler` ×
`StarFieldBrightness`: (0, 0, 0, 4) by day in all traces, none) and below the horizon a fade towards `gtaWaterColor`
(= `globalFogColor` in the traces) by saturate((`gtaSkyDomeFade`.x − y) × .y), (0, 0.00025), y the dome's height: the
dome is 10000 m across and 2000 m high (`gWorld`), its vertex program stretching it twice below the horizon. Then
`rage_postfx` ps 20 lays NearFogAmount × (`globalFogColorN` − sky) on it (bc_3 0.157; bc_1, et01_ba 0) — both in
SparkIV since 2026-11-01. `rage_perlinnoise` ps 1 = SparkIV's noise; its `Phase` is the game's running time (bc_3
4712.98; SparkIV starts at TLAD's 593.25), which places the clouds.

## Format

Little-endian. A string is a length byte (with its terminating zero) and its characters.

1. `rgxa`.
2. **Vertex programs:** a count byte, then per program: a count byte of its constants, each a type byte, a byte, its
   register (16 bits) and its name; its size (16 bits) twice; its D3D9 bytecode (`vs_3_0`).
3. **Pixel programs:** the same (`ps_3_0`; program 0 is often empty).
4. **Global variables**, then **the shader's own variables:** a count byte, then per variable: its type (2 float,
   3 float2, 4 float3, 5 float4, 6 sampler, 8 float3x4, 9 float4x4), an array count byte, its name, its semantic, its
   annotations (a count byte, then each a name, a type byte (0 int, 1 float, 2 string) and its value), and its default
   as a count byte and that many 32-bit words. A sampler's default is its sampler states as pairs.
5. **Techniques:** a count byte, then per technique its name and a count byte of its passes; per pass the vertex and
   the pixel program's number, a count byte of its states and those states as pairs of 32-bit words (state, value).

A pass's state number is an index into GTAIV.exe's table at file offset 0xD10980, which gives the D3D render state:

| # | State | # | State | # | State |
|---|---|---|---|---|---|
| 0 | ZENABLE | 10 | ALPHABLENDENABLE | 20 | COLORWRITEENABLE1 |
| 1 | FILLMODE | 11 | STENCILENABLE | 21 | COLORWRITEENABLE2 |
| 2 | ZWRITEENABLE | 12 | STENCILFAIL | 22 | COLORWRITEENABLE3 |
| 3 | ALPHATESTENABLE | 13 | STENCILZFAIL | 23 | BLENDOP |
| 4 | SRCBLEND | 14 | STENCILPASS | 24 | BLENDOPALPHA |
| 5 | DESTBLEND | 15 | STENCILFUNC | 25 | SEPARATEALPHABLENDENABLE |
| 6 | CULLMODE | 16 | STENCILREF | 26 | SRCBLENDALPHA |
| 7 | ZFUNC | 17 | STENCILMASK | 27 | DESTBLENDALPHA |
| 8 | ALPHAREF | 18 | STENCILWRITEMASK | | |
| 9 | ALPHAFUNC | 19 | COLORWRITEENABLE | | |

The values are D3D's (SRCBLEND 5 = SRCALPHA, DESTBLEND 6 = INVSRCALPHA, CULLMODE 2 = CW, ...).

## Open

- Which technique the engine picks for which bucket and pass is in GTAIV.exe, not in the files; known from the traces
  (see "How the engine draws them").
- The states the engine sets around the passes without their own are known from the traces too (bc_1 f940); its
  `ALPHAFUNC` stays `ALWAYS` from start-up (see Hair).
- Why the game's hair, uncut by its alpha test, fades softly into the skin (the blended back-face passes over the
  opaque front faces are not drawn as such here).
- The moon (`gta_atmoscatt_clouds` ps 9: `MoonPosition`, `MoonVisiblity`, `MoonColorConstant`; 0 in bc_3's daylight)
  and the stars (`StarFieldBrightness` by the time of day).
- The water's dynamic height map (`HeightMapSampler`, 56 m round the camera) and the clouds' `Phase` at a cutscene's
  start (running game time).
