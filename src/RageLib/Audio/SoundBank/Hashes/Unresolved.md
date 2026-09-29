# Unresolved wave name hashes

Wave names in sound banks are stored as 32-bit hashes (Jenkins one-at-a-time, lower case).
`Names.txt` maps hashes back to names by hashing every known name. This file lists every
wave hash that is still unresolved in GTA IV, The Lost and Damned and The Ballad of Gay Tony
(PC, Complete Edition), so that names found later (community lists, other game versions) can
be checked against it.

State: 142 unresolved waves (141 distinct hashes) out of 143,338 in 3,286 sound banks.
All 3,568 archive entry names are resolved. Hashes are written the way SparkIV shows them
(`0x` + lower-case hex without leading zeros).

**Content descriptions below are not names.** Do not add them to `Names.txt` unless the exact
name has been found in a game file, or a candidate fits the bank so well that a random match
can be ruled out (see below).

## Why guessing is hard

Candidate names can only be verified by hashing them. With a 32-bit hash, testing more
than a few million candidates per hash produces random matches that look just as valid as
real ones.

Series don't help as much as they seem to. The hash keeps a 32-bit running state and its
final mixing step is reversible, so a wrong prefix whose state collides with the real one
matches *every* suffix after it: `STARE_BEFORE_INVITES_12` to `_16` matches the same five
waves as `PLAYER_HAS_PULLED_01` to `_05`. The per-character step also adds the character
before mixing, so a collision can shift the digits: a series that matches `_16/_17/_18`
may really be `_01/_02/_03`. What a series does prove is that the waves are consecutive
takes of one context, and in which order.

A match is trusted when the candidate set was small (expected random matches far below 1),
or when the name is coherent and fits an existing neighbour in the same bank
(`CLUB_GAME_ESCORT_NOT_DRUNK` next to `CLUB_GAME_ESCORT_DRUNK`), while the random matches
of the same search are word salad.

## How the names added in 2026 were found

- **Sound objects.** `sounds.dat15` (and the EP1/EP2 variants) contain about 30,000 named
  sound objects that reference waves by hash. The object name is usually a variation of the
  wave name: object `COLLISIONS_COLLISIONS_ROADCONE_COLLISION_2` plays `ROAD_CONE_COLLISION_2`,
  `SLO_MO_AK47_TAIL_L` plays `SLO_MO_AK47_TAIL_1_LEFT`. Permutations and synonyms of the
  object name resolved 50 waves; for most of the rest the object name at least tells the content.
- **Speech context tables.** `speech.dat` / `EP2_SPEECH.DAT` list every speech context of a
  game as the hash of its stem (without `_01`) plus the number of takes. In TBoGT only four of
  9,886 contexts are still unknown; one of them is listed below.
- **Listening.** Knowing what is said (for example "we gonna need something bigger than that")
  made small, targeted searches possible (`NEED_BIG_CAR_01` to `_05`).
- **Dialogue codes.** Scripted speech is named `MISSION_LINECODE_TAKE` (`E2T5_IA1_01`); the
  missing codes are gaps in the sequence of the bank.

## Sources already searched

Every string from these was hashed and tested against all unresolved hashes, with and
without `_LEFT`/`_RIGHT` and `_01` to `_20`:

- `GTAIV.exe` and all audio config files (`sounds.dat15`, `game.dat16`, `speech.dat`, EP1/EP2 variants, XML)
- the English game text (`american.gxt`) of all three games, including the audio labels of dialogue lines
- all 940 game scripts (`script*.img` of all three games), decrypted, also as concatenations of
  two and three fragments of the club, dancing and booty call scripts
- the text of all 726 in-game web pages (`.whm`)
- the stems of all known names with `_00` to `_99`
- the names of all sound objects, with permutations, synonyms and number/channel endings

## TBoGT/pc/audio/sfx/EP2_SPEECH.rpf / LUIS_NORMAL_4.ivaud

Five takes of one context (stem hash `0x9ac74025` in `EP2_SPEECH.DAT`). After the dancing
mini-game Luis and the girl have sex in the women's toilet of the club, she gives him her
number (booty call), and he answers:

| Take | Hash         | Line                                          |
|------|--------------|-----------------------------------------------|
| 1    | `0xe4fe5c1e` | "I'll hit you up"                             |
| 2    | `0xf7708102` | "I will definitely be calling you"            |
| 3    | `0x88d233f`  | "I'll call you, mommy"                        |
| 4    | `0xf94584b0` | "If I got free time, I might call you"        |
| 5    | `0x2c5feae4` | "We gotta have a good time, baby"             |

The take order comes from colliding series; the lines were matched by listening to each hash. `LUIS_NORMAL_4` holds the
contexts from `JACKING_CAR_FEM` to `RADIO_REQ_LRR`, so the name probably starts with J to R.
Not found with: booty call, toilet, phone number, goodbye and girl name vocabularies
(Domino, Cindy, Jojo, Tania, Vikky, Lily, Taylor, Ana, Tami, Dana, Heidi, Simone) up to four
words, prefixes like `POST_`, `LEAVE_`, `JONI_`, `TOILET_` with all 376,000 English words,
and fragments of the club scripts. The scripts only play `AFTER_BOOTY_CALL` (in
`bootycall.sco`); this context is triggered by the engine.

## general.rpf / DOWNLOADABLE_RINGTONES.ivaud

The ringtones sold on the in-game website vipluxuryringtones.com, played by the sound objects
`MOBILE_PHONE_RING_27` to `_35`. The three resolved waves are named after the shop titles
(`DRAGONBRAIN`, `SCIENCE_OF_CRIME`, `STTHOMAS`), like the ringtones in `MUSIC_RINGTONES.ivaud`
(`BASSMATIC`, `TAKETHEPAIN` ...); the other nine are not.

Content identified by listening, matched to the shop list
(https://gta.fandom.com/wiki/Vipluxuryringtones.com):

| Hash         | Content                                         | Shop title                        |
|--------------|-------------------------------------------------|-----------------------------------|
| `0x190cb7c2` | Jet engine flying by                            | Jet                               |
| `0x51eb893f` | Beep, beep, long beep, fade out                 | Flat Line                         |
| `0x6c3a168e` | Liquid poured into a glass                      | Champagne                         |
| `0x80ee71a8` | Chimes / sparkle                                | Diamonds                          |
| `0xeccd0fb4` | Sword, beheading, no scream                     | Beheading                         |
| `0xf08a8ab4` | Moaning                                         | Lesbians                          |
| `0x7012d1cc` | Rap song                                        | Hooker (song "Dat's Pimpin'")     |
| `0x1da6dc12` | Motor-like noise with an odd sound at the end   | probably Money Counter            |
| `0x89ef9266` | Woman screaming                                 | probably Old Bitch                |

"Dat's Pimpin'" is the production music track `mtx173_15` (Megatrax, "Hip-Hop Chopshop
Vol. 3"). The catalogue data did not lead to the name either.

Searched for these nine hashes without a plausible match (charset `A-Z 0-9 _`):

| Search                                                                 | Names tested       |
|------------------------------------------------------------------------|--------------------|
| every name up to 9 characters                                          | 134 trillion       |
| 72 content words (`JET`, `CHAMPAGNE`, `HOOKER` ...) with up to 7 arbitrary characters before/after | 55 trillion |
| music words (`TUNE`, `THEME_TUNE`, `SONG`, `INTRO` ...) with up to 7 characters, Hooker only | 19 trillion |
| content word + one English word (376,000 words), `_` or not, 61 number endings | 6.6 billion |
| two words from the game text dictionary (23,000 words), number endings | 64 billion |
| content word + two of the 5,000 most common words, number endings      | 1.3 trillion       |
| shop titles, descriptions, catalogue data, prefixes/suffixes           | about 250 million  |

## All other unresolved hashes

Content comes from the sound objects that play the wave (names in code format) or from
listening.

### pc/audio/sfx/gps.rpf (72)

Mission complete jingles. Every bank `SMCnn` holds one stereo pair (both channels have exactly the same
length), played by the sound objects `MISSION_COMPLETE_nn` -> `MC_nn_L` / `MC_nn_R`. `SMC18` (an accent
like after a passed mission) and `SMC71` have no sound object and are probably unused.

| Bank | Hashes |
|------|--------|
| `SMC6` | `0x6f90a6df` `0xc50951cf` |
| `SMC7` | `0x56370e0a` `0x59b19503` |
| `SMC10` | `0xac3b6d5d` `0xc0d19691` |
| `SMC11` | `0xc235e9f8` `0xd8a696dd` |
| `SMC15` | `0x947e73fb` `0xce60e7bf` |
| `SMC18` | `0x4528844c` `0xa459c2c5` |
| `SMC24` | `0x234d62f5` `0x79280ea9` |
| `SMC25` | `0x60284791` `0x9a003b44` |
| `SMC27` | `0x96052f4f` `0x9c873c57` |
| `SMC28` | `0x57d64945` `0xdf0d577d` |
| `SMC33` | `0x82d2bf42` `0xa7b308ee` |
| `SMC34` | `0x1beaf9b8` `0xa935944f` |
| `SMC35` | `0x614f3f8a` `0xaf28db3c` |
| `SMC42` | `0x72289467` `0x88b11d` |
| `SMC43` | `0x1398e624` `0x8f845df9` |
| `SMC50` | `0xb4262f36` `0xf67133cf` |
| `SMC51` | `0x8fc77bb8` `0xc24260b1` |
| `SMC52` | `0x5b427a69` `0xc6094ff9` |
| `SMC53` | `0x77c703ad` `0xb35cfad4` |
| `SMC54` | `0x2f77766e` `0xd8f9496f` |
| `SMC55` | `0xd3376193` `0xea580fd8` |
| `SMC56` | `0x8fb55dca` `0xe2e0841f` |
| `SMC57` | `0xd0cde6e2` `0xe6f2933f` |
| `SMC58` | `0x5e3ced63` `0xf3f4f69` |
| `SMC59` | `0x43ee1a2d` `0x44c71be3` |
| `SMC60` | `0x19307a08` `0xf811b7cb` |
| `SMC61` | `0x6d23d560` `0xad5555c6` |
| `SMC62` | `0x3362818b` `0xcc79b387` |
| `SMC63` | `0x13f7d4a1` `0x8dce4854` |
| `SMC64` | `0x15ee5469` `0xa6d6763b` |
| `SMC65` | `0x1843673e` `0x46b6c420` |
| `SMC66` | `0x49942292` `0x7814ff93` |
| `SMC67` | `0x1b565954` `0xedc4fe2e` |
| `SMC68` | `0x40f1a4a7` `0xb1cf866d` |
| `SMC69` | `0x4e14e653` `0xc519d45f` |
| `SMC71` | `0x51212971` `0xea7e5c29` |

### pc/audio/sfx/resident.rpf (48)

| Bank | Hash | Content |
|------|------|---------|
| `AMB_RESIDENT` | `0x598e3d1e` | sound object `BELLS_GRAIN`, `BELLS_GRAIN_NANR` |
| `AMB_RESIDENT` | `0xa6968222` | sound object `DESK_FAN_LOW` |
| `AMB_RESIDENT` | `0xe81a8179` | sound object `DRINKS_COOLER_HUM` |
| `COLLISIONS` | `0x4fac7cbc` | sound object `DOOR_KNOCKS_KNOCK_RATTLE`, `FM2_FINAL_INTERVIEW_DOOR_CHECK_RATTLE` |
| `COLLISIONS` | `0x8dbfea9b` | sound object `GYM_BAG_HIT_1` |
| `COLLISIONS` | `0x30582fa9` | sound object `GYM_BAG_HIT_2` |
| `COLLISIONS` | `0xc16c95e5` | sound object `GYM_BAG_HIT_3` |
| `COLLISIONS` | `0xcebe3088` | sound object `GYM_BAG_HIT_4` |
| `COLLISIONS` | `0xa6b354a` | sound object `GYM_BAG_SWIPE_1` |
| `COLLISIONS` | `0xd059412f` | sound object `GYM_BAG_SWIPE_2` |
| `COLLISIONS` | `0xe6236cc3` | sound object `GYM_BAG_SWIPE_3` |
| `COLLISIONS` | `0xc6242cc5` | sound object `GYM_BAG_SWIPE_4` |
| `COLLISIONS` | `0xbd7d6ed6` | sound object `GYM_BAG_TAIL_1` |
| `COLLISIONS` | `0xab1cca15` | sound object `GYM_BAG_TAIL_2` |
| `COLLISIONS` | `0x57a0a31a` | sound object `GYM_BAG_TAIL_3` |
| `COLLISIONS` | `0x46b70147` | sound object `GYM_BAG_TAIL_4` |
| `COLLISIONS` | `0xdc2dbe6c` | sound object `SLO_MO_MAIN_METAL_DEFORMATION_01` |
| `COLLISIONS` | `0xe796d53e` | sound object `SLO_MO_MAIN_METAL_DEFORMATION_02` |
| `COLLISIONS` | `0x68a05757` | sound object `SLO_MO_MAIN_METAL_DEFORMATION_03` |
| `COLLISIONS` | `0x76c8f3a8` | sound object `SLO_MO_MAIN_METAL_DEFORMATION_04` |
| `COLLISIONS` | `0x4c0a9e2c` | sound object `SLO_MO_MAIN_METAL_DEFORMATION_05` |
| `COLLISIONS` | `0xfd3d7166` | sound object `SLO_MO_VEHICLE_SUB_01` |
| `COLLISIONS` | `0x8f6515b3` | sound object `SLO_MO_VEHICLE_SUB_02` |
| `FRONTEND_GAME` | `0x5c086af0` | sound object `CALLING_CELL_RING` |
| `FRONTEND_GAME` | `0x6ec9acaf` | sound object `RADIO_INTERFERENCE_TWINLOOP_A` |
| `FRONTEND_GAME` | `0x256fa0c4` | sound object `RADIO_INTERFERENCE_TWINLOOP_B` |
| `FRONTEND_GAME` | `0xd92fec91` | sound object `RESIDENT_FRONTEND_GAME_RINGING_CLOCK_LOOP_32K` |
| `FRONTEND_MENU` | `0xcc10b6ff` | menu sound |
| `FRONTEND_MENU` | `0xe0ffd125` | sound object `FRONTEND_MENU_MONTAGE_NAVIGATE_L` |
| `FRONTEND_MENU` | `0x562d3b82` | sound object `FRONTEND_MENU_MONTAGE_NAVIGATE_R` |
| `FRONTEND_MENU` | `0x2730af2e` | sound object `FRONTEND_MENU_MONTAGE_PRESS_L` |
| `FRONTEND_MENU` | `0x6cbd3a4a` | sound object `FRONTEND_MENU_MONTAGE_PRESS_R` |
| `HORNS` | `0x1e8a882e` | sound object `BARONY_HORN`, `CAVALCADE_HORN`, `LANDSTALKER_HORN`, `SOLAIR_HORN` |
| `HORNS` | `0x65ae9679` | sound object `BLISTA_HORN`, `REBLA_HORN` |
| `HORNS` | `0x97d8facd` | sound object `BULLOCK_HORN`, `HAKUMAI_HORN`, `CABBY_HORN`, `TAXI_CAR_HORN` |
| `HORNS` | `0x50e16cdb` | sound object `BUS_HORN`, `TURISMO_HORN`, `INFERNUS_HORN`, `FORTUNE_HORN` |
| `HORNS` | `0xe45e13d6` | sound object `DESPERADO_HORN`, `BOBBER_HORN`, `CADDY_HORN`, `FORKLIFT_HORN` |
| `HORNS` | `0xcfc6eaa8` | sound object `FUTO_HORN`, `MINIVAN_HORN` |
| `HORNS` | `0x4987e488` | sound object `ICEVAN_A3`, `ICEVAN_A4`, `ICEVAN_B3`, `ICEVAN_B4`, `ICEVAN_C3`, `ICEVAN_C4`, `ICEVAN_D3`, `ICEVAN_D4`, `ICEVAN_E3`, `ICEVAN_E4`, `ICEVAN_F3`, `ICEVAN_F4`, `ICEVAN_G3`, `ICEVAN_G4`, `ICEVAN_A2` |
| `HORNS` | `0x89135d42` | sound object `INGOT_HORN`, `DILETTANTE_HORN`, `NRG_HORN` |
| `HORNS` | `0x9e339943` | sound object `PIANO_HIT_NOTE_SINES` |
| `HORNS` | `0xcea58c7c` | sound object `SIRENS_AIRHORN`, `GT11_DEPARTURE_TIME_FUNFAIR_JINGLE_6_NOTE_SOUND_SS` |
| `HORNS` | `0x2b65a1e4` | sound object `STRATUM_HORN`, `PRIMO_HORN`, `HABANERO_HORN` |
| `HORNS` | `0x12ced393` | tiny single-cycle waveform (sibling of SIREN_SAW_A?) |
| `HORNS` | `0xb9c2782c` | tiny single-cycle waveform (sibling of SIREN_SAW_A?) |
| `TRAIN` | `0x74a724ee` | sound object `SUBWAY_TRAIN_BRAKE_RELEASE` |
| `WEAPONS` | `0x142cc411` | sound object `SLO_MO_AK47_LAZERLAYER2_L` |
| `WEAPONS` | `0x483f9160` | sound object `SLO_MO_AK47_LAZERLAYER2_R` |

### pc/audio/sfx/script_mission.rpf (7)

| Bank | Hash | Content |
|------|------|---------|
| `BR2_AMBUSH` | `0xbf327ad3` | garage door loop (sound objects `BR2_GARAGE_DOOR_LOOP`, `RP10_EXOTIC_IMPORTS_GARAGE_DOOR_LOOP`) |
| `EM1_SPOOKED` | `0xf13506cf` | locked door rattle (sound object `EM1_SPOOKED_LOCKED_DOOR_MT_B`) |
| `EM1_SPOOKED` | `0x1c8d5d7f` | locked door rattle (sound object `EM1_SPOOKED_LOCKED_DOOR_MT_A`; `manny1.sco` plays `EM1_SPOOKED_LOCKED_DOOR`) |
| `HOSPITAL_KILLING` | `0x99401587` | odd gagging/choking sound (sound object `HOSPITAL_KILLING_RUBBER_GLOVES`) |
| `HOTEL_ATTACK` | `0xdbd3ae07` | short high beep (sound object `HOTEL_ATTACK_FIRE_ALARM_ELECTRONIC`) |
| `LURE` | `0xd6c5b041` | TV clip, high-pass filtered: dialogue about a daughter who wanted to be a teacher, organ chord (sound object `LURE_TV`) |
| `RP10_EXOTIC_IMPORTS` | `0xbf327ad3` | garage door loop (sound objects `BR2_GARAGE_DOOR_LOOP`, `RP10_EXOTIC_IMPORTS_GARAGE_DOOR_LOOP`) |

### TLAD/pc/audio/sfx/EP1_SFX.rpf (1)

| Bank | Hash | Content |
|------|------|---------|
| `TS4_KILL_BILLY_IN_JAIL` | `0x7d0e1144` | short alarm for rhythmic repeat (sound object `TS4_KILL_BILLY_IN_JAIL_PRISON_ALARM_LOOP`) |
