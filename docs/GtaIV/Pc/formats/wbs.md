# `.wbs`: a cutscene ped's face shapes (GTA IV, PC)

Status: **used** since 2026-10-29 (cutscenes). Reader: `src/RageLib/GtaIV/Pc/Models/BlendShapeFile.cs`.

## Where

One file per cutscene ped that talks, beside its model in `pc\anim\cutsprops.img`: 45 in GTA IV
(`cs_bernie_cranet.wbs`, `cs_niko.wbs` …) and 21 in TLAD's (`cs_malc.wbs`, `cs_johnnythebiker.wbs`, `cs_jim_fitz.wbs` …).
The ped's head (`head_000_R`) is drawn with `gta_ped_skin_blendshape`.

## What the game does with it

- Its face clip (`<ped>_head_<section>` in the cutscene's `.wad`) has, besides the face bones' tracks, tracks of
  **type 21** (data type 2, one number a frame, 0 to 1). Their bone id is the **key** of a shape in the `.wbs`.
- Each frame the CPU brings one of three work buffers up to the shapes' weights now and uploads it as a second
  vertex stream: a `memcpy` of the head's whole buffer, 48 bytes a vertex (position, normal, tangent as three
  floats each, each padded to 16 bytes). Trace `GTAIV.3` (TLAD, et01_aa), call 102274443: 155 648 bytes for
  Malc's 3183 vertices.
- How (GTAIV.exe, `rage::crCreatureComponentBlendShapes`, vtable 0x00FE38DC, update = entry 3 at 0x00694450):
  the component keeps per shape {key, weight now, the weight last applied to each of the 3 buffers}. A shape is
  applied to this frame's buffer only if its weight changed by more than **0.01** since it was last applied there
  (|now − last| > 0.01 × max(|now|, |last|, 1.0); 0.01 = `0x3c23d70a`, set by the constructors at 0x00693E90 /
  0x00693F80); then the buffer is read, `(now − last) × delta` added and written back (0x006ABE60 positions,
  0x006ABFB0 with normals; disassembly at 0x00694626: `xmm1 = now − last`), and `last = now`. Every **100**
  updates all three buffers are cleared and built again from 0. So the buffer drawn follows the weights to within
  0.01, at any frame rate.
- The shader's technique `deferredbs_drawskinned` (vertex program 9 of `gta_ped_skin_blendshape.fxc`) adds
  `POSITION1`, `NORMAL1` and `TANGENT1` of that stream to the vertex before skinning. Its other techniques
  (`deferred_drawskinned`, program 5) have no such inputs.

**Measured:** fitting that buffer to the file's shapes is exact (residual 1e-19). The position is
Σ weight × int16 / **4096**, the normal and tangent Σ weight × int8 / **128**. The scale came from the three
strongest shapes in that frame (`upper_lip_mid_up`, `left_mouth_corner_up`, `right_mouth_corner_up`): weight
over fitted coefficient 2.448e-4, 2.441e-4, 2.441e-4 = 1/4096. SparkIV's blend of the same frame (et01_aa s2
f119.5) differs from the game's buffer by 0.005 mm RMS (the game's own change: 0.033 mm RMS; at most 0.06 mm).

## Layout (resource, system memory)

Pointers are `0x5` + offset. Values in hex offsets.

**Header**

| Offset | Content |
|---|---|
| +0x0 | vtable |
| +0x4, +0x8 | the meshes: pointer to a list of pointers, count (u16) |
| +0xC, +0x10 | the keys: pointer to a hash table, its number of buckets (u16) |

**Hash table:** per bucket a pointer to a chain of entries {u16 key (rest of the word 0xCDCD), pointer to the
shape's name object, pointer to the next entry}. The **name object**: vtable, the name inline at +0x4
(`jaw_wide_open`, `left_blink` …), at +0x44 the shape's place in its mesh's list.

**Mesh** (from the header's list): the name inline (`head_000_R.mesh`); at +0x88 a pointer to an object whose
first word points at the mesh's data:

| Offset (data) | Content |
|---|---|
| +0x44, +0x48 | the shapes: pointer to a list of pointers, count (u16) |
| +0x4C | pointer to the mesh's name |
| +0x50, +0x54, +0x58 | three vertex buffers of the mesh's size, stride 48 (the work buffers the game fills; all 0 in the file) |
| +0x5C | the vertex declaration |
| +0x60 | number of vertices (Malc 3183, Bernie 2447) |

**Shape** (from the mesh's list): (pointer, count) pairs, the count in the low 16 bits:

| Offset | Content |
|---|---|
| +0x0 | the mesh's number of vertices |
| +0x2C | positions: 4 × int16 a moved vertex: 0, z, y, x |
| +0x34 | normals: 4 × int8: 0, z, y, x |
| +0x3C | tangents: 4 × int8: 0, z, y, x |
| +0x4C | the moved vertices' indices (u32, ascending) |

The vertices are the head mesh's own in its order, so SparkIV applies the shapes to the mesh of the part
`head_000_R` that has that many vertices (its high level of detail).

## Shapes

Malc (TLAD) has 22, the mouth only: `jaw_wide_open`, `jaw_tighten`, `upper_lip_up` … `right_mouth_corner_down`.
Bernie (`cs_bernie_cranet`) has 85, adding lip shapes, phonemes (`oo`, `mbp`, `normalFV` …), cheeks, nose, brows
and blinks (`left_blink`, `right_blink`).

## Checked against the game

- Elizabeta (TLAD, `cs_elizabetat`, 2854 vertices, 87 shapes): trace `GTAIV.3` call 102279336 (et01_aa s2
  f119.5). The weights read back from the game's buffer by least squares over SparkIV's shapes fit with no
  residual and equal the clip's (e.g. `upper_lip_up`, `sneer_left/right` ≈ 0.40); SparkIV's own buffer differs by
  4e-6 m RMS (correlation 0.9999; normals 1.0000). Her 27 head bone matrices (call 102279334) equal SparkIV's
  within 0.4 mm and 0.2°.
- Bernie (GTA IV, `cs_bernie_cranet`): trace `GTAIV.2` call 84486417 at bc_1 s2 **f447** (the trace frame,
  Present 84490042, is f447 by its picture, not f465 as noted before): 0.025 mm, correlation 0.999.
- Pictures of the trace frames against SparkIV (pixel aligned, uncompressed): with the shapes closer to the game
  (Elizabeta face 3.98 → 3.82, nose 5.41 → 4.44; Bernie 5.08 → 4.90 mean /255).
- Videos of the game are not exact enough for this: at the same frame a video picture differs from the trace's
  picture by 3 to 8 /255 everywhere, also where no shape moves anything (hair, earring), while the shapes change the
  picture by about 1.

## Open

- SparkIV blends the exact weights each frame; the game's 0.01 step (and its rebuild every 100 updates) isn't
  reproduced. The difference is at most 0.01 of a shape.
- Peds without a `.wbs` (most street peds) have no face shapes; their face bones move as before.
