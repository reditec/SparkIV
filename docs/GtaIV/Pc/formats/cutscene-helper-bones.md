# Cutscene helper bones: twist and roll (GTA IV, PC)

Status: **used** (forearm twist, arm roll). Applied in `src/SparkIV/Viewers/ModelView.html` (`findFollowers`,
`sampledPose`).

## What they are

Ped skeletons have helper bones that carry the skin between two joints, so that a turning hand or arm doesn't wring
the sleeve:

| Bone | Id (player skeleton) | Parent | Place |
|---|---|---|---|
| `Char_L_ForeTwist` / `Char_R_ForeTwist` | 14497 / 14753 | upper arm | at the forearm (same position) |
| `L_ArmRoll` / `R_ArmRoll` | 15857 / 15873 | clavicle | at the upper arm |
| `L_UpperArmRoll` / `R_UpperArmRoll` | 14496 / 14752 | clavicle | at the clavicle |
| `L_Calf_Roll` / `R_Calf_Roll`, `Neck_Roll` | 14512 / 14768, 14240 | | |

## Rule SparkIV uses

- A twist or roll bone without a track, at the same place (within 3 cm) under the same parent as a bone with a
  track, follows that bone's turn away from its rest.
- A forearm twist bone also takes **half of the hand's turn about the forearm's axis** (x). That is the swing-twist
  split of the hand's turn away from its rest, taken in the parent's frame.
- `Char_*_ForeTwist` tracks in a clip are **ignored**.

## Evidence (trace `GTAIV.trace`, bc_1 section 2, 2026-10-30)

The bone palettes the game uploads (vertex shader register 64, 3 rows per bone) were compared with SparkIV's skin
matrices. The comparison used relative matrices, so the placement of the whole figure drops out.

- **Niko's clip `player_1` has ForeTwist tracks; the game doesn't use them.** With the rule, his twist bones match the
  game to 0.010 and 0.001 (call 107577950, frame 84.5). With his own tracks they don't match. The toll booth man in
  et01_ba (TLAD) showed the same: his clip turns `Char_L_ForeTwist` 40° away from the forearm, but the game's sleeve
  stays straight.
- **Bernie's clip `cs_bernie_cranet_1` has no twist tracks.** 40 hand palettes from 13 places were matched to
  SparkIV frames by the 29 finger bones (30 matches). In most frames the rule matches the game to within about 1°, with
  no time offset.
- **Still open:** a few frames are off by 5 to 13°:
  - f19, f79 and f84.5 on the right arm;
  - f990 on both arms.
  
  At f990 the game's left twist (−9.2°) is larger than the hand's whole turn about the forearm (−6.7°), so there it
  isn't a share of the current hand pose.
  
  Neither of these explains those frames: the hand's turn taken in its own frame, Euler angles in any of the six
  orders, or the projected y or z axis. See issue #17.

## In GTAIV.exe

- `FUN_00aba040` resolves 11 bone names (table 0x0103EE60) into bone indices: `Char_Head` four times, then
  `Char_L_Hand`, `Char_R_Hand`, `Char_L_ForeTwist`, `Char_R_ForeTwist`, `Char_Pelvis`, `Char_L_Foot` and
  `Char_R_Foot`.
- The set is 0xA8 bytes, made by `FUN_00ab9290`. A cutscene ped (`CPed_InitVariations`) keeps it at +0x29C; the
  player (`FUN_005d5180`) keeps it at +0xE68.
- It looks like a set for IK (hands, feet, pelvis, head). No code was found that reads its twist entries directly as
  `[[ped+0x29C]+0x18]`, so the code that turns the twist bones is not known yet.
- The bone tag names (`R_FORETWIST`, `R_FORETWIST1`, `R_UPPPERARMROLL`, `R_ARMROLL` …) are a pointer table at
  0x0103D690; `R_FORETWIST` is entry 28.
