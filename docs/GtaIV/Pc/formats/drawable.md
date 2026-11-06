# Drawables: vertex buffers in `.wdr`, `.wdd`, `.wft` (GTA IV, PC)

Author: reditec. Status: **used**. Reader: `src/RageLib/Models/Resource/` (`DrawableModel.cs`, `Models/Geometry.cs`,
`Models/VertexBuffer.cs`, `Models/IndexBuffer.cs`).

This file so far records one finding about the vertex buffer (`rage::grcVertexBufferD3D`) that every drawable
uses: map models (`.wdr`), dictionaries (`.wdd`) and fragments (`.wft`) alike.

## The vertex buffer's layout

Offsets inside the structure in the resource's system segment; pointers `0x5xxxxxxx` point into the system
segment, `0x6xxxxxxx` into the graphics segment.

| Offset | Type | Content | SparkIV |
|---|---|---|---|
| +0x00 | u32 | vtable (see below) | ignored |
| +0x04 | u16 | vertex count | used |
| +0x06 | u16 | locked flag / alignment (0) | ignored |
| +0x08 | ptr (0x6...) | `pLockedData`: the vertices in the graphics segment, **or 0** | used when not 0 |
| +0x0C | u32 | stride | used |
| +0x10 | ptr (0x5...) | vertex declaration | used |
| +0x14 | u32 | 0 | ignored |
| +0x18 | ptr (0x6...) | `piVertexBuffer`: the vertices in the graphics segment | used when +0x08 is 0 |
| +0x1C | ptr | 0 | ignored |

## Older files leave `pLockedData` null (fixed 2026-11-06)

- **Evidence.** `vigero.wft` from GTA IV PC **1.1.2.0** (an older patch; RSC header `52 53 43 05`, type 0x70, flags
  0xDC1A2018: system segment 0x18000, graphics segment 0x1A0000) against the current game's `vigero.wft` in
  `pc\models\cdimages\vehicles.img` (flags 0xD8232018: 0x18000 / 0x118000). Same structures, same vertex
  declarations (e.g. usage flags 0x5F, stride 0x2C, types `0x6755555555996996`), same counts per mesh; only the
  vertex buffers differ:
  - current, first LOD 0 buffer at system 0x15300: `006BBAD8 00000924 600C0000 0000002C 50017250 00000000 600C0000 00000000`
    (+0x08 and +0x18 both 0x600C0000);
  - 1.1.2.0, first LOD 0 buffer at system 0x15370: `5CB234A6 00000077 00000000 00000034 50017140 00000000 600FE580 00000000`
    (+0x08 is **0**, the data pointer only at +0x18). All 30 buffers of the old body's four LODs are like that (the
    wheel child's were not dumped one by one; it reads right with the fix).
- **What went wrong.** SparkIV read the vertices only from +0x08. With 0 there every buffer was read from the start
  of the graphics segment, so all meshes got the same first vertices (the first 0x34 bytes of LOD 0's mesh 2), and the
  indices pointed into foreign data: a burst of long spikes, in the native renderer and the WebGL page alike.
- **Fix.** `VertexBuffer.ReadData` takes +0x08 when it isn't 0, else +0x18. The game's current files carry the same
  pointer in both fields, so their output is unchanged: the positions and indices of vigero, infernus, police, bus,
  banshee (`.wft`), `f_m_business_01.wdd` and `nge_det_mh1.wdr` / `nge_det3_mh1.wdr` hash the same before and after,
  and the reference bc_1 s1 f144 renders byte-identically.
- **Result for the old vigero.** LOD 0 of the body 11,445 vertices / 11,418 triangles, plus the wheel (fragment
  child 15, bone 40) 1,284 / 884: **12,729 vertices, 12,302 triangles**, exactly OpenIV's count for the high LOD.
  The LOD 0 meshes' boxes together span (-0.927, -2.690, -0.498) to (0.932, 2.212, 1.185); the drawable's bounds
  (+0x20 / +0x30) are (-0.927, -2.690, -0.498) to (0.932, 2.212, 1.185).
- The index buffers were already right: their only data pointer (+0x08) is set in both files.

### Second file: a loose `dukes.wft` (origin unknown, likely modded)

- **The file.** RSC header `52 53 43 05`, type 0x70, flags 0xE40DA020 (system 0x20000, graphics 0x360000). The same
  hash-like vtables as the old vigero (drawable 0x49C27D95, vertex buffer 0x5CB234A6). All 34 vertex buffers of the
  body's three LODs have +0x08 = **0** and the data pointer only at +0x18 (e.g. LOD 0 mesh 0:
  `5CB234A6 00004C7D 00000000 00000044 5001EDB0 00000000 60000000 00000000`). Its "_hd" texture names and 46,348
  triangles at LOD 0 (stock cars have about 10-15k) point to a modded car exported in the older layout; not confirmed.
- **Before the fix** every buffer was read from the graphics segment's start, i.e. from LOD 0 mesh 0's data with each
  mesh's own stride (44 to 68 bytes). Two meshes (LOD 0 meshes 5 and 13, strides 52 and 44) came out with **NaN
  positions**. `Vector3.Min`/`Max` carry a NaN through (checked: `Min(NaN, 1)` and `Min(1, NaN)` are both NaN), so the
  box the view fits to was NaN, so was the camera, and every vertex was clipped: the viewport stayed empty although
  draws were issued and nothing was logged. (The old vigero had no NaN among its misread vertices; it showed spikes.)
- **With the fix** the reader gives OpenIV's numbers: LOD 0 of the body 39,816 vertices / 46,348 triangles; with the two
  wheel drawables (fragment children 15 and 16: 1,105 / 932 and 1,287 / 1,282) **42,208 vertices**, OpenIV's count. No
  NaN or Inf, every index below its mesh's vertex count, the LOD 0 meshes' boxes inside the drawable's bounds
  (+0x20 / +0x30: (-0.982, -2.875, -0.410) to (0.977, 2.512, 0.760); the lowest LOD reaches slightly beyond, to x 1.038).
- **Not guarded.** The model view has no NaN check in its fit; a file whose vertices really are NaN would still show
  nothing.

## Other differences between the two vigero files (no effect on SparkIV)

- **vtables.** The old file's structures carry other values in their first dword (drawable 0x49C27D95, geometry
  0xEFE7D461, vertex buffer 0x5CB234A6, index buffer 0x8CA91DA9) where the current one has exe addresses
  (0x006A32DC, ..., 0x006BBAD8). The game replaces them on loading; SparkIV doesn't read them. **unclear** what the
  old values are (they look like hashes).
- **Levels of detail.** The old drawable fills all four LOD slots (+0x40..+0x4C: high, medium, low, very low), the
  current one three; the dwords at +0x60..+0x6C are 3, 3, 3, 3 in the old and 3, 3, 3, 0xFFFFFFFF in the current
  file. SparkIV shows the 4 LODs the old file really has; OpenIV's count above is its high LOD.
- **Fragment children.** Same 19 children, same groups and matrices; the old file's bone indices are 2 higher from
  child 4 on (its skeleton has two more bones).
