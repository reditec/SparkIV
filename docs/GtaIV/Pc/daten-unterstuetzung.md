# GTA IV (PC): Welche Spieldaten SparkIV unterstützt

Diese Datei gilt nur für **GTA IV, PC-Version**. Andere Spiele und Plattformen bekommen eine eigene Datei unter
`docs/<Spiel>/<Plattform>/`.

Für jedes Dateiformat und jedes Werkzeug hält sie fest, was SparkIV aus den Spieldaten **verwendet**, was es
**liest, aber ignoriert**, und was in seiner **Bedeutung unklar** ist. Ziel ist, alle Punkte unter
„ignoriert“ und „unklar“ zu klären. Wo die Dateien allein nicht reichen, helfen Ghidra (GTAIV.exe), x32dbg
(das laufende Spiel) oder eine apitrace-Aufnahme (Direct3D 9).

## Status

Jede Zeile trägt einen Status:

| Status | Bedeutung |
|---|---|
| **verwendet** | SparkIV liest die Daten und setzt sie im Bild oder Ablauf um. |
| **ignoriert** | Die Bedeutung ist bekannt oder naheliegend, SparkIV setzt sie aber nicht um. |
| **unklar** | Die Daten sind vorhanden, ihre Bedeutung ist nicht geklärt. |
| **nur Spiellogik** | Die Daten wirken nicht aufs Bild, nur auf das Spiel; SparkIV braucht sie nicht. |

Jeder Eintrag sagt außerdem, **woher** die Erkenntnis stammt: Datei, Exe-Adresse, Shader oder Aufnahme.
Vermutungen sind als solche gekennzeichnet.

---

## Zwischensequenzen

Stand: 11.10.2026. Grundlage sind alle 153 `.cut`-Dateien aus `pc/anim/cuts.img` und 6030 Clips aus einem
openFormats-Export (`.onim`) aller 357 Zwischensequenz-Wörterbücher.

### `.cut` (Text)

| Block | Status | Inhalt / Herkunft |
|---|---|---|
| `[ANIM]` | verwendet | Animationswörterbuch des Abschnitts (`BC_3_0` → `bc_3_0.wad`). |
| `[AUDIO]` | verwendet | Name des Tons. |
| `[CAMERA]` | verwendet | Kamera-Clip im Wörterbuch (`camera_0`). |
| `[DRAW_DISTANCE]` | verwendet, **Zeitpunkt unklar** | Pro Einstellung: Zeit (ms), Nummer, eine Zahl (bc_3 immer 0, Bedeutung unklar), Sichtweite (m). Eingelesen bei `Cut_ParseFile` 0x9C3840, Zustand 0x13 (`"%d %*d %f %f"`), Tabelle 0x1294760. Die Zeiten liegen 0,1 bis 2 Bilder **vor** den Kameraschnitten. Das Spielvideo zeigt beim ersten Schnitt von bc_3 dasselbe wie SparkIV: Im letzten Bild vor dem Schnitt fehlt die ferne Küste schon. Wo das Spiel die Tabelle pro Bild liest, ist nicht gefunden (außer Einlesen und Aufräumen greift kein Code auf 0x1294760 zu). |
| `[DURATION]` | verwendet | Länge des Abschnitts (ms). |
| `[FLAGS]` | verwendet (teilweise) | Davon nur Ein- und Ausblendung (`LONG_FADE_OUT` usw.); die Längen der Blenden sind angenommen. Die übrigen Flags sind nicht einzeln geprüft. |
| `[LIGHTS]` | verwendet | Licht-Clips des Abschnitts. |
| `[MISSION_TEXT_NAME]`, `[TEXT]` | verwendet | Untertitel: Tabelle, Startzeit, Dauer, Schlüssel. |
| `[MODELS]` | verwendet (teilweise) | Nummer, Modell, Clip, Gesichts-Clip. Ein **fünftes Feld** (`0`, nur bei Zeilen mit Gesichts-Clip) ist **unklar**. |
| `[OFFSET]`, `[ORIENT]` | verwendet | Lage und Drehung der Szene in der Welt. |
| `[PROPS]`, `[VARIATION]` | verwendet | Requisiten und Kleidungsvarianten. |
| `[REMOVE]` | verwendet | Weltobjekte, die während der Szene ausgeblendet werden. |
| `[VEHICLE_DETAILS]`, `[VEHICLE_REMOVAL]` | verwendet | Fahrzeugfarben und Schmutz; abgenommene Fahrzeugteile. |
| `[CUTSCENE_HEADER]` | **unklar** | bc_3: `44 1143 2578 3615`, die Abschnittsgrenzen in einer Gesamtzählung von Bildern, beginnend bei 44. Möglicherweise ein Versatz für Ton oder Untertitel (44 Bilder ≈ 1,5 s). Die Synchronität in SparkIV ist noch zu prüfen. |
| `[ANIMRANGE]` | **unklar** | `range on 44.0 3615.0`; passt zu `[CUTSCENE_HEADER]`. |
| `[COMPRESSION]` | **unklar** | Überall `1 A`. |
| `[EFFECTS]` | **ignoriert** | Partikeleffekte, z. B. `FX_blood_gun_entry:effect2_1 5 1205 71900 71900 <6 Zahlen> 0`: Effektname, Clip, Zahlen für Objekt, Knochen, Zeit, Lage und Drehung (Vermutung). Die zugehörigen `effect`-Clips haben nur eine `LightDirection`-Spur. |
| `[CAMCORDER]` | **ignoriert** | Zeitspannen (ms) in 134 Dateien, vermutlich der Handkamera-Look. |
| `[BLOCKING_BOUNDS]` | nur Spiellogik (Vermutung) | Vier Eckpunkte und ein Radius, wohl der gesperrte Bereich. |
| `[PLAYER_START]` | nur Spiellogik | Position des Spielers nach der Szene. |
| `[MAX_CARS]`, `[MAX_PEDS]` | nur Spiellogik (Vermutung) | In 4 Dateien. |
| `[TIME]` | – | Überall leer. |
| Bytes nach dem letzten `[SECTION_END]` | – | Füllbytes; die Dateien sind auf 2048 Byte aufgefüllt. |

### Animations-Clips (`.wad`, Spurarten)

| Spurart | Status | Inhalt / Herkunft |
|---|---|---|
| `BonePosition` (0), `BoneRotation` (1) | verwendet | Knochen von Figuren, Objekten und Gesichtern. |
| `ModelPosition` (5), `ModelRotation` (6) | verwendet | Wurzel des Modells. |
| `CameraPosition` (7), `CameraRotation` (8), `CameraFov` (136) | verwendet | Kamera. |
| `CameraDof` (137) | verwendet (seit 11.10.2026) | (nah, fern, 0) in Metern. **Gemessen mit x32dbg** (`dof_dump.txt`, Haltepunkt 0x5E16F0): Das Spiel übergibt genau diese Werte an seine Tiefenunschärfe. Im Zwischensequenz-Modus (`Cut_StartSection` 0x9C9180 setzt Postfx +0x264) ist alles bis „fern“ scharf; ab „fern + 0,5 m“ wird mit der Stärke b = M² gemischt, mit einem 4×4-Weichzeichner (rage_postfx Programm 12/13). **M ist gemessen 0,5** (`dof_blur.txt`, Haltepunkt 0x930580: DOF_BLUR = (0,5, 0, 0,5, 0)), also `misc.DOFBlurMultiplier.SD` aus `visualSettings.dat`, b höchstens 0,25. Warum der SD-Wert gilt, ist offen: Die Prüffunktion 0x424000 ist in Ghidra nicht lesbar. |
| `CameraMatrixRotateFactor` (138) | **unklar** | Kamera, eine Zahl pro Bild. |
| `CameraControl` (144) | **unklar** | Kamera, −1 oder 1. |
| `LightColor` (139), `LightDirection` (143) | verwendet | Farbe und Richtung der Szenenlichter. |
| `LightRange` (140), `LightIntensity1` (141), `LightIntensity2` (142) | verwendet (seit 11.10.2026 richtig zugeordnet; Helligkeit seit 12.10.2026 geklärt: draußen Farbe × Stärke × Belichtung) | **Die openFormats-Namen sind irreführend.** Laut Exe (0x9CA7E0 → 0xABCC50 → 0xABCCD0) ist 140 die **Stärke**, 141 der **Radius** in Metern (der 11. Parameter, mit ihm wird die Kugel gegen das Sichtfeld getestet) und 142 der äußere Kegelwinkel ab der Achse in Grad (höchstens 89°); der innere ist die Hälfte. SparkIV hatte 140 und 141 vertauscht. Der Spot-Shader (deferred_lighting Programm 26) rechnet Albedo × Farbe × Stärke × (a⁴ − 0,1)/0,9 × Kegel × n·l × saturate(GB2.z² + 0,5), ohne weiteren Faktor. **Gemessen mit x32dbg** (`cutlight_dump.txt`, 0xABCCD0 und 0xAD30E0) bei bc_3 `filler_0`: Stärke 3,5, Radius 7, Kegel 42,5°/85°, Farbe (1, 0,927, 0,763), Position wie in SparkIV; genau so kommen die Werte am Shader an. Offen: Mit diesen Werten ist Bernies Gesicht in SparkIV zu hell, im Spiel nicht. Die Ursache liegt also in SparkIVs Verrechnung (Normalen, Tonwerte), nicht in den Daten. |
| `Type21` (21) | **ignoriert** | In jedem Gesichts-Clip (`*_head`), z. B. 85 Spuren bei Bernie. Werte 0 bis 1 an Kennungen, die keine Skelettknochen sind. Der Kopf nutzt den Shader `gta_ped_skin_blendshape`, sehr wahrscheinlich sind das also **Gewichte von Formzielen** (Mimik). Wo die Formziele im Modell stehen, ist noch offen. |
| `Type145` (145) | **unklar** | Nur in 3 Clips von `cs_vlad`. |
| `ActionFlags` (128), `UV0`/`UV1` (130/131), `FacialAnimation` (134) | – | In Zwischensequenzen nicht vorhanden; im Animationsbetrachter verwendet. |

---

### Modell-Materialien (Ausschnitt, für Zwischensequenzen relevant)

| Shader / Parameter | Status | Inhalt / Herkunft |
|---|---|---|
| `gta_emissive`, `gta_emissivestrong`, `gta_emissivenight` | verwendet (seit 12.10.2026 wie im Spiel) | Programm 14: eine normal beleuchtete Fläche, die Textur als Albedo. Programm 12: dazu Textur × Vertexfarbe × `emissiveMultiplier`, bei `emissivenight` × `gDayNightEffects.w` = 1 − Tagesfaktor. Draußen zusätzlich × Belichtung, wie alle Lichter. |
| `gta_ped_skin(_blendshape)`: `SubColor`, `SubScatWrap`, `SubScatWidth` | verwendet (seit 12.10.2026) | Programm 10: Albedo + 0,5 × SubColor × Texturalpha in einem Band an der Schattengrenze (Formel im Code). Schablonenwert 1 im GBuffer, Durchgang 1 der Spotlichter beleuchtet nur diesen Wert. |

### Licht draußen (gemessen in der D3D-Aufnahme `GTAIV.1.trace`, bc_3)

- **Sonne (c18):** (1, 0,851, 0,714) × 10,25, wie in SparkIV.
- **Umgebung:** c37 = Amb0 und c38 = Amb1 − Amb0. Daraus folgt ein Überblenden von oben (Amb0) nach unten (Amb1), wie in SparkIV.
- **Tagesfaktor:** `gDayNightEffects` = (f, 1 − f, f, 1 − f); `globalScalars2` = (1, 1, 1, 0,725).
- **Szenenlichter der Zwischensequenz** (Fülllichter): Farbe × Stärke, ohne weiteren Faktor; draußen × Belichtung. Damit stimmt Bernies Gesicht im HDR-Puffer mit dem Spiel überein (Nase 2,68 gegen 2,68).

### Licht drinnen (gemessen in der D3D-Aufnahme `GTAIV.2.trace`, bc_1, Frames 4613–13049)

- **Herkunft der Werte:** Innen ersetzt der Zeitzyklus-Modifikator des Raums die Werte aus `timecyc.dat`. Danach rechnet das Spiel genauso wie draußen.
  - Zuordnung: Die Raumzeile im `mlo`-Abschnitt der `.ide` enthält im 11. Feld den Hash des Modifikator-Namens (`GxtFile.Hash`).
  - Bernies Wohnung: Der Hash 1720238244 steht für `BernieCrane` aus `timecyclemodifiers3.dat`.
- **Gemessen:**
  - c18 Sonne = 0, weil der Richtungslicht-Multiplikator 0 ist.
  - c37 = Amb0 RGB/255 × 4,5 = (4,447, 4,182, 3,741), exakt.
  - c37 + c38 = Amb1 × 4,5, exakt.
  - c40.w = 0,6 (draußen 0,725).
  - c45 ≈ 0,78.
- **Unterstützt:** `TimeCycle.LoadModifiers` und `TimeCycle.With` übernehmen Licht, Belichtung, Tone-Mapping-Werte, Nebelbeginn und Sichtweite. Der Wert −1 heißt „unverändert“.
- **Offen bzw. ungeklärt:**
  - Der Alpha-Wert der Farben; angenommen als Mischanteil, gemessen nur bei 255.
  - Diese Spalten werden nicht verwendet: `AmbientDownMult`, `Temperature`, `PostFxStrength`, `BrightPassThreshold`, `Contrast`, die `Far`-Spalten, die `DOF`-Spalten, `AmbientOcclusionMult`, `SkyMultiplier` (vermutlich c40.w) und `PedAmbOccHack`.
  - Wie im Spiel gilt der Modifikator des Raums, in dem die Kamera steht. Steht sie in keinem Raum, gilt `timecyc.dat` allein. SparkIV schickt dazu pro Raum den veränderten Zeitzyklus mit, und die Ansicht wählt ihn in jedem Frame. Ob das Spiel beim Raumwechsel überblendet, ist nicht geprüft.
  - Die Tone-Mapping-Konstanten innen sind noch nicht gegen die Aufnahme geprüft.

### Spiegelung, Sonnenschatten, Filmkorn (D3D-Aufnahme `GTAIV.4.trace`, TLAD `et01_ba`, Frame 9350, 12:50 Uhr)

- **Spiegelkarte der Umgebung** (`ParabSampler` im Sonnen-Pass, `EnvironmentSampler` im Fahrzeugglas):
  - Das Spiel zeichnet sie jedes Bild neu: 1024 × 1024 HDR, an der Kamera zentriert. Zuerst kommt der Himmel, dann die Kartenobjekte, ohne Personen und Fahrzeuge.
  - Danach verkleinert es sie auf 512, 256 und 128.
  - Shader aus der Aufnahme:
    - Vertex-Shader 0x22EC8A80: Ort = Richtung.xy / (Abstand + Richtung.z), Tiefe 1 − 1/(Abstand + 1).
    - Pixel-Shader 0x1A37E8E0: verwirft alles unterhalb der Kamera. Licht = Umgebungslicht × AO + Sonne, ohne Schatten und Lampen, darüber der ferne Nebel.
  - Gelesen wird bei 1 − (R.xy/(2(1 + sat R.z)) + 0,5), in der Stufe 4 − 4·sat(Stärke × 512 × `dReflectionParams.x`); der Wert ist 1/230 in beiden Aufnahmen.
  - **In SparkIV:** `parabolicTarget`, `mirroredAround` in `gtaiv-pc.js`. Die kleineren Stufen sind Mipmaps; den Verkleinerungs-Shader des Spiels habe ich nicht nachgebaut.
- **Sonnenschatten:**
  - 4 Kaskaden bis 9,5 / 20 / 50 / 175 m Sichttiefe (`gFacetCentre`).
  - Jede wird mit 4096 × 4096 Pixeln (R32F) gezeichnet und 2 × 2 gemittelt in einen Atlas mit 8192 × 2048 Pixeln (R16F) kopiert.
  - Gespeichert wird die Höhe entlang der Sonnenachse. Achse u ist die Kamerarichtung, flach auf die Ebene senkrecht zur Sonne gelegt; Achse v = u × Sonne.
  - Bias je Kaskade: 0,14 / 0,24 / 0,5 / 0,75 m.
  - Der Sonnen-Pass liest 12 Abtastpunkte. Ergebnis: Licht = sat(Anteil/12 + 1,5·(Abstand/175)²).
  - Der Schatten wirkt auf direktes Sonnenlicht und Sonnenglanz, nicht auf die Spiegelung.
  - **In SparkIV:** `sunShadowCascades`, `shadowTarget`, `gameSunShadow`. Abweichung: Die Kaskaden werden direkt mit 2048 Pixeln in R32F gezeichnet, ohne Mittelung, und ungefiltert gelesen.
- **Filmkorn (nur TLAD):**
  - Quelle ist die Spalte 10 der `timecyc.dat`, als Ganzzahl. Die Spaltenüberschrift sagt fälschlich „Sky top“. Gelesen wird sie im Parser 0xAEFEB0.
  - `NoiseParams.z` = Wert/64 (0x930268); unter einem Raum-Modifikator wird der Wert halbiert.
  - Das Rauschen kommt aus einer Textur mit 1024 × 1024 Zufallsbytes, die die exe beim Start selbst erzeugt. Gelesen wird bei frac(uv·(3,2, 1,8) + Zufall), dazu kommt (Blau − 0,5)·z·1,1 vor der Belichtung.
  - GTA IV und TBoGT haben dort überall 0.
  - **In SparkIV:** `TimeCycle.Noise`, `gameGrainTexture`.
- **Gelblicher Look von TLAD:** Er kommt aus der eigenen `timecyc.dat`, mittags ColorCorrect (161, 117, 74) gegenüber (115, 122, 113) im Basisspiel. SparkIV liest die TLAD-Datei bereits.

### Himmel und Wolken (`gta_atmoscatt_clouds`, Aufnahmen `GTAIV.4.trace` und `GTAIV.1.trace`, geklärt am 16.10.2026)

- **Kuppel:** Es gibt kein Modell in den Spieldateien. Die exe baut sie selbst. In GTAIV.4 ist es der Vertex-Buffer 0x248B51C0 (Aufruf 147965, 992 Dreiecke).
  - 17 Ringe mit Radius 2·cos(k·π/32) und Höhe 1,636·sin(k·π/32). Die Texturkoordinaten liegen radial um (0,5, 0,5).
  - Gezeichnet wird mit Skalierung (10000, 2000, 10000) um die Kamera. Kuppel (x, y, z) entspricht Welt (x, z, −y).
- **Farbe des Himmels:** SkyColor + Azimut-Farbe (Spalten 64–72, 77, 78) und Sonnenschein (103–105, 119).
  - HDRExposure = SkyLightMult (Spalte 60) × 2,85 (0xDBC049), begrenzt auf 30. Danach wird alles tone-gemappt wie der Rest.
- **Wolkenrauschen:** Der Shader `rage_perlinnoise` (0x23C2D220) rechnet es jedes Bild neu in eine 256²-Textur. Grundlage ist `baseperlinnoise3channel` aus `pc\textures\skydome.wtd`, 6 Oktaven.
  - Die Feinstruktur kommt aus `noise16_p`, nicht aus `detailbump2`. In der Aufnahme sind die Texturen zwei Stufen kleiner; das liegt an der Texturqualität.
- **Spalten der Wolken** (in beiden Aufnahmen abgeglichen):
  - CloudColor 81–83, SunsetColor 73–75 × Spalte 89, obere Wolkenfarbe 99–101 + 73–75, TopCloud 95–98.
  - Schwelle 85, Bias 86, Schattenstärke 84, Schattenversatz 87, Inscattering 88, Detail-Maßstab 90, Detail-Stärke 91, Dicke 92, Ausblenden zum Horizont 94.
- **Bewegung:** Phase wächst pro Spielstunde um Spalte 107 (Parser 0xAEFEB0 → +0x1B4, 0x62E110).
- **In SparkIV:** `SKY_FRAGMENT`, `PERLIN_FRAGMENT`, `perlinNoise` in `ModelView.html`; die Werte stehen in `TimeCycle.cs` als `skyHdr`.
- **Offen:**
  - Eine Spielstunde ist mit 120 s Abspielzeit angenommen; das ist nicht aus der exe gelesen.
  - `DetailOffset` bewegt sich im Spiel mit; in SparkIV ist es fest.
  - Sterne, Mond und Galaxie (nachts) fehlen noch, ebenso das Überblenden zur Wasserfarbe unter dem Horizont.

### `[DRAW_DISTANCE]` in der `.cut` (GTAIV.exe, geklärt am 13.10.2026)

- **Einlesen:** `Cut_ParseFile` (0x9C3840) liest im Blockzustand 0x13 jede Zeile als `%d %*d %f %f`.
  - Die Zeile ergibt einen 12-Byte-Eintrag: +0 Zeit in ms, +8 die Entfernung, +4 die vierte Zahl.
  - Ist die vierte Zahl −1 oder fehlt sie, wird +4 zu 0,05 (`0x1048AB4`).
  - Gespeichert wird in der Liste `0x1294760` mit dem Zähler `0x1295724`, höchstens 100 Einträge.
- **Verwendung:** `FUN_009CA620` nutzt die Einträge. Ghidras Analyse hatte die Funktion nicht als Code erkannt; gefunden wurde sie über eine Byte-Suche nach den Adressen.
  - Sie wählt den letzten Eintrag, dessen Zeit erreicht ist (Zeit ≤ `0x1295748` + `0x1295740`).
  - Dann ruft sie `0xC61D80(Index, Entfernung, vierte Zahl)` auf. Ohne passenden Eintrag, etwa wenn der Block fehlt, ruft sie `0xC61D80(Index, 0, 0)` auf.
  - `0xC61D80` (thiscall) schreibt in das Objekt `this[0x140 + Index·4]`: +0x154 = Entfernung, +0x158 = vierte Zahl.
  - Vermutung: Das Objekt ist die Kamera. Dann wäre +0x154 die Far-Clip- und +0x158 die Near-Clip-Entfernung; der Standardwert 0,05 passt zum gemessenen `dofProj`-Near von 0,05.
  - Das ist noch nicht belegt. In GTAIV.1.trace war bei bc_3, Frame 870, der Far-Wert von `dofProj` 1500, obwohl der Block dort 185 m vorgibt.
- **Kette (statisch belegt):**
  - Jeden Frame holt `0x9CB0F0` über `0xA86AC0(0x103E498)` die Kamera-Steuerung der Zwischensequenz. Dann ruft es `0xC61B30` und `0x9CA7E0` auf (die Lichter, siehe oben) sowie `0x9CA620`.
  - Das Objekt `this[0x140 + Abschnitt·4]` ist die Kamera des Abschnitts. Sie wird in `0xC61AD0` über `0xA86C40(0x1F, …)` als Kamera-Typ 0x1F angelegt und heißt `CutCam%d`.
  - Entfernung und vierte Zahl sind damit Felder dieser Kamera (+0x154, +0x158).
- **Leser (geklärt):** `0xD53280`, eine Methode der `CutCam` (gebaut von `0xD527E0`).
  - Ist die Entfernung 0, setzt die Methode an der obersten Kamera (`0xA7C9F0`) das Flag +0x1B4 und als Far-Wert des Bildes (+0x68) 800.
  - Sonst übernimmt sie die Entfernung als Far-Wert.
  - Der Near-Wert (+0x64) ist die vierte Zahl, oder 0,05.
- **Abgleich mit dem Zeitzyklus:** `0xC46050` vergleicht den Far-Wert mit FarClp aus dem Zeitzyklus (`0x15E8954`, Spalte 23; der Nebel in `0xAF3A50` liest denselben Wert).
  - Ohne Flag und solange Far ≤ FarClp bleibt der Far-Wert; sonst gilt FarClp.
  - Mit Flag, also ohne Block, gilt FarClp.
  - Ein negativer Wert ist ein Faktor auf FarClp.
- **Ergebnis:** Sichtweite = min(Blockwert, FarClp), ohne Block FarClp. So setzt SparkIV es jetzt um (`CutsceneView.xaml.cs`).
- **Fehlender Block:** `et01_ba` (TLAD) hat keinen. SparkIV meldet dann „draw distance 0“ und lädt keine fernen Modelle; das Spiel übergibt dann 0, 0.

### Fahrzeugfarben in Zwischensequenzen

- **Im Spiel** (GTAIV.exe): `[VEHICLE_DETAILS]` ist `Modell Farbe1 Farbe2 Farbe3 Farbe4 Dirt [7. Zahl]` (`Cut_ParseFile`, gespeichert ab Abschnitt +0xF08C). Diese Werte sind nur der Ersatz:
  - `CCutsceneObject_Init` → `0xC67450` sucht zuerst ein Fahrzeug desselben Modells in der Spielwelt (Pool `0x12E22A4`).
  - Findet es eins, übernimmt es dessen Farben und Dirt und setzt +0x2B1; die `.cut`-Farben greifen dann nicht mehr (`Cut_CreateSectionObjects` → `0xC677E0` → `0xD25D80`).
- **In SparkIV:** Der Index `CutsceneScriptIndex` ordnet jeder Cutscene das Missionsskript zu, das sie startet (`START_CUTSCENE_NOW`). Die Farben stammen aus dessen `CREATE_CAR` und dem folgenden `CHANGE_CAR_COLOUR`/`SET_EXTRA_CAR_COLOURS`; ohne sie gelten die `.cut`-Farben.
  - Beispiel: `elizabeta1` erzeugt für `et01_ba` den Presidente mit den Farben 0/0/0/0 (schwarz).
  - Der Index entsteht beim ersten Laden des Spiels in SparkIV, im Hintergrund mit Meldung in der InfoBar. Gespeichert wird er in `%LocalAppData%\SparkIV\CutsceneScripts\`.
  - Neu gebaut wird er, wenn sich die `script.img`, die SparkIV-Build oder das Indexformat ändert.
- **Ungeklärt bzw. vereinfacht:**
  - Erzeugt ein Skript mehrere Fahrzeuge desselben Modells, gilt das erste mit Farben.
  - Farben, die über Variablen gesetzt werden statt als Zahlen, werden nicht erkannt.

### Episoden (TLAD, TBoGT)

Eine Episode erkennt SparkIV daran, dass in ihrem Ordner eine `content.dat` liegt und GTA IVs Ordner direkt darüber. Eine Episode ergänzt GTA IV nur, deshalb lädt SparkIV GTA IVs Dateien mit, so wie das Spiel. Ihre Listen `content.dat` und `images.txt` nennen nur die eigenen Dateien.

- **Karte (`.ide`, `.wpl`, `.img`):**
  - Die Dateien der Episode kommen zuerst, ihre Modelle haben Vorrang.
  - Ein GTA-IV-Archiv mit demselben Pfad (`east\bronx_w.img`) wird für seine Modelle gelesen, seine Platzierungen aber nicht.
  - Ergebnis bei `et01_ba`: 0 statt 29 fehlende Modelle.
- **`carcols.dat` und `vehicles.ide`:** Beide Dateien werden gelesen, die der Episode zuerst. Die Farbtabelle (`col`) kommt aus der ersten Datei, die eine hat; TLADs Datei enthält keine.
- **Zeitzyklus-Modifikatoren:** Fehlt der Episode eine eigene Datei, nimmt SparkIV die von GTA IV.
- **`.ide`-Abschnitte `anim` und `tanm`:** Animierte Objekte haben an dritter Stelle ihr Animations-Wörterbuch; dahinter folgen dieselben Felder wie bei `objs` und `tobj`. Ein Beispiel ist `LOD_BAWSAQ_01_DC9, LODs_04_DC9, manhat09, 700, …, slodmh96mh98_dc9_grp`. SparkIV liest jetzt beide Abschnitte, zeigt die Objekte aber in Ruhestellung, nicht animiert.
- **Ton:** Ihn bestimmt `CutsceneSound.Find` wie das Spiel (`0x988990`).
  - Das Spiel bildet `"CUTSCENES_" + [AUDIO]-Name` in Großbuchstaben.
  - Damit sucht es den Sound in den Sound-Metadaten, also `sounds.dat15` bzw. der `<sounds>`-Datei aus `e1_audio.xml`/`e2_audio.xml`.
  - Der Bank-Pfad `<Pack>\<Name>` nennt die `.rpf` in `pc\audio\sfx`.
  - Das Objekt `CUTSCENES_<Name>` in `game.dat` enthält nur Hashes von Mischkategorien (`cutscene_positioned_radio`, `ambience` …), keine Bank.
  - Geprüft für alle Cutscenes: GTA IV 153/153; TLAD 68/68 (66 in `EP1_SFX.rpf`, 2 in GTA IVs `cutscenes.rpf`, 13 ohne Ton); TBoGT 50/104.
  - Die 54 übrigen TBoGT-Cutscenes sind Teil- oder Altfassungen (`gt01_aa`, `f1aap1`–`5`, `e2int` …). Für sie haben auch die Metadaten des Spiels keinen Sound; die gespielten Fassungen heißen `gt01_za`, `f1_aa`, `e2_int` usw.
- **Ungeklärt:**
  - `DISABLE_FILE` in `content.dat` wird nicht ausgewertet.
  - Ob das Spiel gleichnamige `.ide` beider Spiele mischt, ist nicht geprüft.

## `common/data/visualSettings.dat`

Noch nicht vollständig aufgenommen. Bisher bekannt:

| Schlüssel | Status | Inhalt / Herkunft |
|---|---|---|
| `misc.DOFBlurMultiplier.HD` / `.SD` (1 / 0,5) | verwendet (SD-Wert fest eingetragen), **Wahl unklar** | Stärke der Tiefenunschärfe. Gelesen bei 0xAF2210, gespeichert in 0x103F9B0. Mit x32dbg gemessen galt bei deinen Einstellungen der SD-Wert 0,5. In der zweiten Aufnahme (mit gesenkten Grafikeinstellungen) war es 1 (DOF_BLUR = (1, 0, 1, 0)). Welche Einstellung das umschaltet, ist offen (0x424000 in Ghidra nicht lesbar). |
| `misc.BloomIntensityClamp.HD` (10) | verwendet | 0x103F9B4, begrenzt den Bloom. |
| `misc.HiDof.nearBlur/midBlur/farBlur`, `misc.cutscene.nearBlurMin` | **unklar** | Nicht verwendet. Ob die PC-Fassung sie nutzt, ist ungeklärt. |
| `misc.CrossPMultiplier.*` | **unklar** | Nicht verwendet (RimLight 0,7, Gamma 1,15, Desaturation 1,29 …). |

## Weitere Formate und Werkzeuge

Noch nicht aufgenommen. Jedes Format bekommt einen eigenen Abschnitt nach demselben Muster: Modelle (`.wdr`,
`.wdd`, `.wft`), Texturen (`.wtd`), Kollision (`.wbn`, `.wbd`), Zeitzyklus (`timecyc.dat`),
`visualsettings.dat`, Shader-Parameter, Wasser, Lichter.
