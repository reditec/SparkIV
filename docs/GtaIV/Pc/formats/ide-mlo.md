# GTA IV (PC): Interiors (`mlo` section of the `.ide`)

Applies only to **GTA IV, PC version** (with TLAD and TBoGT). Overview of all formats: [../data-support.md](../data-support.md).

An interior (MLO) is described in an `.ide`. A placement in a `.wpl` with the name of the interior puts all of its objects into the world. Example: Bernie's apartment `BernieCrane` in `pc/data/maps/interiors/generic/public_3.ide`.

## Structure

```
<Name>, <Flags>, <Number of objects>, <Number of rooms>, <Number of portals>, ...   (not indented)
	<Object>, x, y, z, qx, qy, qz, qw, <LOD>, <Instance flags>,              (indented, one per object)
mloroomstart
	<Room>, <Number of objects>, <Number of portals>, maxX, maxY, maxZ, minX, minY, minZ, <?>, <Modifier hash>, <?>
	<Object indices, -1 as padding>
	roomend
mloportalstart
	<from>, <to>, <Corner 0 xyz>, <Corner 1 xyz>, <Corner 2 xyz>, <Corner 3 xyz>, -1, -1, -1, -1, <Flags>, 16777215, 1
mloend
```

## Objects

| Field | Content | Status | Source |
|---|---|---|---|
| 0 | Model name | used | |
| 1–3 | Position in the interior | used | |
| 4–7 | Rotation as quaternion. Unlike in the `.wpl`, **not** inverted: first the object's rotation, then the interior's. | used | Comparison with game frames (Bernie's shelves, et01_aa) |
| 8 | 0 = the model itself, otherwise distant version (not shown) | used | |
| 9 | **Instance flags**, usually 384 = 0x180. Same meaning as in the `.wpl`, see [wpl.md](wpl.md); 0x100 allows lamp shadows. | used (0x100) | GTAIV.exe 0x9D76C0 |

## Rooms

| Field | Content | Status | Source |
|---|---|---|---|
| 0 | Name; `limbo` is the area outside all rooms (walls) | used | |
| 1, 2 | Number of objects, number of portals | – | |
| 3–5, 6–8 | Corners of the room box (in the file max before min; SparkIV sorts) | used | |
| 9 | usually 1 | **unclear** | |
| 10 | Hash of the timecycle modifier (`GxtFile.Hash` of the name; 0 = none) | used | Capture `GTAIV.2.trace` (bc_1) |
| 11 | usually 111 | **unclear** | |

Rooms are numbered from 0; `limbo` is usually room 0. The number line after a room lists the indices of its objects.

### Which room applies

- **Camera:** The modifier of the room the camera is in determines light, exposure and fog.
- **Overlapping boxes of different interiors:** SparkIV takes the room whose box floor the point is lowest above. Confirmed with et01_aa: The box of the apartment below (RM3a) reaches 1.3 m into Elizabeta's living room. The game assigns the camera and all characters to the living room (capture `GTAIV.3.trace`: `globalScalars.z` = 0.2 for all characters). How the game itself determines the room (portal tracker) has not been decoded.
- **Boxes of the same interior:** The first room in the list applies (bc_1: the living room includes the hallway in front of Bernie's door).

## Portals

| Field | Content | Status | Source |
|---|---|---|---|
| 0, 1 | Rooms on both sides (room indices, 0 = `limbo` or outside) | used | |
| 2–13 | four corners | used (corners 0 and 2) | |
| 14–17 | always −1 | **unclear** | |
| 18 | **Flags** (usually 72 or 73). The game excludes portals with bit 0x10 or 0x4 from the blending below. | used | GTAIV.exe 0xA8F6A0, 0xA8F7D0 (portal +0x4C) |
| 19, 20 | 16777215, 1 | **unclear** | |

### Blending of ambient light across portals

`Room_AmbientScale` (GTAIV.exe 0xA0D720) computes the share of ambient light for characters and movable objects (see `AmbientDownMult` in [timecyclemodifiers.md](timecyclemodifiers.md)):

1. The room's value is AmbientDownMult × Blend + (1 − Blend). Blend is stored in the room at +0x4C; where it comes from is open (assumed 1).
2. For each portal of the room, the game forms a sphere: centre midway between corner 0 and corner 2, radius equal to half the diagonal (0xB20EC0).
3. If the object lies inside this sphere at distance d, t = 1 − d²/r².
4. **Portal to the outside** (room 0): value += (1 − value) · t.
5. **Portal into another room** with a higher value: value += (other − value) · t.

In SparkIV: `LocationRoom.Portals`, `actorAmbientAt` in `ModelView.html`.

## Open

- **bc_1, section 0, frames 569–717:** In the game video, Bernie at the door has a value of about 1 instead of 0.5. It changes in the middle of a continuous shot, at frames 715–720. Neither room boxes nor portals (the hallway has the same modifier) explain this. The capture `GTAIV.2.trace` of bc_1 could clarify it.
