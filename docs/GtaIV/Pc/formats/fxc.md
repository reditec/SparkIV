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

- Which technique the engine picks for which bucket and pass (the G-buffer's `deferred_draw` /
  `deferredalphaclip_draw`, a see-through emissive's `unlit_draw`) is in GTAIV.exe, not in the files; so far known
  from the traces only.
- The states the engine sets around the passes without their own (depth writes, culling, the G-buffer's blending).
