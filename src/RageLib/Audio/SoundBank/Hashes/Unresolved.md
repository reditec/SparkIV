# Unresolved wave name hashes

Wave names in sound banks are stored as 32-bit hashes (Jenkins one-at-a-time, lower case).
`Names.txt` maps hashes back to names by hashing every known name. This file documents
hashes that are still unresolved, together with what the audio actually contains, so that
names found later (community lists, other game versions) can be checked against it.

**The descriptions below are content, not names.** Do not add them to `Names.txt` unless
the exact name has been found verbatim in a game file or another reliable source.

## Why guessing doesn't work

Candidate names can only be verified by hashing them. With a 32-bit hash, testing more
than a few million candidates per hash produces random matches that look just as valid as
real ones. Appending suffixes (`_01`, `_LEFT` ...) does not help: the hash keeps a 32-bit
running state, so any string that matches `NAME` also matches every suffix after it.
Only names that appear verbatim somewhere, or very small candidate sets whose expected
random matches are far below 1, can be trusted.

## general.rpf / DOWNLOADABLE_RINGTONES.ivaud

The ringtones sold on the in-game website vipluxuryringtones.com. The three resolved waves
are named after the shop titles (`DRAGONBRAIN`, `SCIENCE_OF_CRIME`, `STTHOMAS`); the other
nine are not. Shop titles, their descriptions and common prefixes/suffixes (`SFX_`,
`RINGTONE_`, `_TONE` ...) were tested without a match (about 7 million candidates), as were
word combinations describing each sound (about 80 million candidates). Expected random
matches over all of these: about 0.1.

Content identified by listening, matched to the shop list
(https://gta.fandom.com/wiki/Vipluxuryringtones.com):

| Hash         | Content                                         | Shop title                        |
|--------------|-------------------------------------------------|-----------------------------------|
| `0x190cb7c2` | Jet engine flying by                            | Jet                               |
| `0x51eb893f` | Beep, beep, long beep, fade out                 | Flat Line                         |
| `0x6c3a168e` | Liquid poured into a glass                      | Champagne                         |
| `0x80ee71a8` | Chimes / sparkle                                | Diamonds                          |
| `0xeccd0fb4` | Sword, beheading, no scream                     | Beheading                         |
| `0xf08a8a4b` | Moaning                                         | Lesbians                          |
| `0x7012d1cc` | Rap song                                        | Hooker (song "Dat's Pimpin'")     |
| `0x1da6dc12` | Motor-like noise with an odd sound at the end   | probably Money Counter            |
| `0x89ef9266` | Woman screaming                                 | probably Old Bitch                |
