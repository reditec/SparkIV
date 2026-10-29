# Cutscene camera (GTA IV, PC)

Status: **used** (position, rotation, FOV, depth of field). Read in `src/SparkIV/Viewers/GtaIV/Pc/Cutscene.cs`, applied
in `ModelView.html` (`filmCameraNow`).

## Where

Each section of a cutscene has a camera clip in the section's `.wad` (`camera_<section>`, the name in the `.cut`
section). Its tracks:

| Kind | Name | Values | Use |
|---|---|---|---|
| 7 | CameraPosition | x, y, z (scene space) | used |
| 8 | CameraRotation | quaternion | used |
| 136 | CameraFov | vertical field of view in degrees | used |
| 137 | CameraDof | near, far (metres), 0 | used (blur beyond far) |
| 138 | CameraMatrixRotateFactor | 0 in the cutscenes checked | not used |
| 144 | CameraControl | -1 in the cutscenes checked; marks cuts | not used |

## Projection

At a 16:9 or wider screen the FOV is vertical and used unchanged; the horizontal angle follows from the screen's
aspect. Only at an aspect of 1.5 or less does the game multiply it by 1.295 and draw bars over 12.5 % of the height
at the top and the bottom.

**Measured** (trace `GTAIV.trace`, bc_1, window 1920 x 1061): the scene camera's projection P = WV⁻¹ · WVP from the
vertex shader constants c4-c11 (gWorldView, gWorldViewProj) gives fovy = 40.000°, aspect 1.8096 = 1920/1061.

## When the game takes a new FOV

GTAIV.exe does not read the FOV every frame. The cut detection `FUN_00d52ca0` is a state machine:

- It samples the control track (144) at k-1, k and k+1, where k = round(phase × duration × 30), or the frame stored
  at camera +0x1B4.
- It returns 1 (the next frame is a cut), 2 (this frame is the cut), 3 (the end of the hold) or 0.
- `FUN_00d532f0` writes the FOV (camera +0x60) and the DOF only on status 0 or 3, or on 2 when `DAT_011d6fd4` > 0.

**It can get stuck.** On status 1 while the flag at +0x1B0 is 1, it stores the cut frame at +0x1B4. Every later call
then returns 2 and changes nothing. The FOV stays at the previous shot's value for the rest of the section, while
position and rotation keep running. The flag is cleared only on status 0 or at phase 0.

**When it happens:**
- **Normal play:** never. x32dbg (2026-10-09) showed every FOV change of bc_3's three sections arriving on time
  (status 1 → 2 → 3 → 0 at each cut).
- **Video:** seen in a third-party recording of bc_3 (sections 2 and 3).
- **Slow trace:** seen in an apitrace trace, where the game runs at a few frames a second. In bc_1 section 3 the
  track changes from 40° to 34.274° at the cut at frame 936. The game kept 40.000° in every frame from 751 to the
  section's end.

The likely trigger is a large step of the cutscene clock per frame. This isn't proven.

**SparkIV** uses the track's FOV as the game does in normal play. A reference picture from a frame with a frozen FOV
shows a different zoom. Check the game's FOV in the frame before using it as a reference.
