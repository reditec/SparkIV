# GTA IV (PC): Zeitzyklus-Modifikatoren (`timecyclemodifiers*.dat`)

Gilt nur für **GTA IV, PC-Version** (mit TLAD und TBoGT). Übersicht aller Formate: [../daten-unterstuetzung.md](../daten-unterstuetzung.md).

Die Dateien `pc/data/timecyclemodifiers.dat` bis `…4.dat` enthalten benannte Modifikatoren. Ein Raum eines Innenraums nennt seinen Modifikator über den Namens-Hash (siehe [ide-mlo.md](ide-mlo.md)); auch Boxen in der Welt verwenden sie. Eine Zeile besteht aus dem Namen und Zahlen, −1 heißt „unverändert“.

Die Spaltennamen im Dateikopf passen ab etwa der Mitte nicht mehr zu den Zahlen. Die folgende Tabelle zählt die **Zahlen nach dem Namen ab 0**, so wie `TimeCycle.With` sie liest.

## Spalten

| Spalte | Name im Kopf | Wirkung | Status | Herkunft |
|---|---|---|---|---|
| 0, 1 | MinFarClip, MaxFarClip | Sichtweite (1) | verwendet (1) | |
| 2, 3 | MinFogStart, MaxFogStart | Nebelbeginn: 3, sonst 2 | verwendet | `GTAIV.3.trace`: c41.x = 3 bei em_4 |
| 4–8 | AmbientLight0 RGBA, Multiplikator | Umgebungslicht oben: RGB/255 × Mult., Alpha als Mischanteil (angenommen) | verwendet | `GTAIV.2.trace` (bc_1): c37 exakt |
| 9–13 | AmbientLight1 RGBA, Multiplikator | Umgebungslicht unten | verwendet | ebd. |
| 14–18 | DirectionalLight RGBA, Multiplikator | Sonne (drinnen meist 0) | verwendet | ebd. |
| **19** | **AmbientDownMult** | **Anteil des Umgebungslichts für Figuren und bewegliche Objekte** (Cutscene-Requisiten, Fahrzeuge), nicht für die Karte. Siehe unten. | verwendet (seit 22.10.2026) | GTAIV.exe 0xAEF820 (Feld +0x48 des Modifikators), `GTAIV.3.trace` |
| 20–23 | Fog RGBA | Farbe des fernen Nebels | ignoriert | |
| 24–27 | NearFog RGBA | Farbe des nahen Nebels: RGB/255 × Spalte 66; Alpha/255 = Anteil | verwendet | `GTAIV.3.trace`: Nebelfarbe (1,625, 1,425, 1,185) = (59, 52, 43)/255 × 7,0 |
| 32, 33 | Exposure, ExposureMultiplier | Belichtung = 32 × 33 | verwendet | |
| 34 | BrightPassThreshold | Schwelle des Blooms | verwendet | |
| 35 | MidGreyValue | Tone-Mapping | verwendet | |
| 36 | IntensityBloom | Bloom | verwendet | |
| 37–39, 40–42 | ColourCorrect, ColourAdd | Farbkorrektur | verwendet | |
| 43, 45 | Desaturation, Gamma | | verwendet | |
| 51–53 | LumMin, LumMax, LumAdaptDelay | Augenanpassung | verwendet | |
| **54** | **GlobEnvRefl** | **`globalScalars.w` = 0,6 × Wert:** Stärke der gespiegelten Umgebung im Glanz. em_4 und BernieCrane haben 0, also keine Spiegelung drinnen. | verwendet (seit 22.10.2026) | `GTAIV.3.trace`: c39.w = 0 in Elizabetas Wohnung; bc_1-Video: ohne den Wert zeigte Bernies Flurtapete helle Streifen und war doppelt so hell (69 gegen 32) |
| 66 | (Kopf: SkyMultiplier, verschoben) | Faktor der nahen Nebelfarbe | verwendet | `GTAIV.3.trace` |
| übrige | Temperature, PostFxStrength, Contrast, Far-, DOF-Spalten, AmbientOcclusionMult, PedAmbOccHack, letzte Spalte (em_4 255, BernieCrane 0) | | **unklar** bzw. ignoriert | |

## AmbientDownMult im Detail

Gefunden in GTAIV.exe und gemessen in der Aufnahme `GTAIV.3.trace` (TLAD, et01_aa):

1. **Wert je Objekt:** `Entity_UpdateAmbientScale` (0xB12370) speichert für ein Objekt im Innenraum `Entity_AmbientScaleAt` (0xAEE600) × 255 im Byte +0x5B; 0xFE steht für 1. Im Innenraum ist das `Room_AmbientScale` (0xA0D720), mit der Portal-Überblendung aus [ide-mlo.md](ide-mlo.md).
2. **Weitergabe an den Shader:** Der Zeichenbefehl einer Cutscene-Figur (0xC645A0) setzt das Byte /255 als `globalScalars.z` (0xAC6FB0).
3. **Wirkung im Vertex-Shader:**
   - `gta_ped`: AO = 1 + gs2.y · gs2.z · (Vertexfarbe Rot · gs.z − 1).
   - `gta_normal_spec` (Requisiten): AO = 1 + gs2.z · (gs.z · (Tagesfaktor.x · Rot + Tagesfaktor.y · Grün) − 1).
   - Mit `globalScalars2` = 1 ist das Vertex-AO × AmbientDownMult.
4. **Wirkung im Licht:** Der AO multipliziert das Umgebungslicht im Sonnen- und Umgebungs-Pass und über sat(AO² + 0,5) die Map-Lampen (siehe [deferred_lighting.md](deferred_lighting.md)).
5. **Messung:** em_4 hat 0,20. In der Aufnahme war `globalScalars.z` = 0,2 bei allen Figuren-Draws und bei Elizabetas Ohrringen; bei der Karte 0,996.

**Folgen im Bild:**
- **et01_aa:** Zusammen mit der richtigen Raumzuordnung und der Formel der Schattenlampe sank der Ohrring im HDR-Puffer von 3,5 auf 1,3 (Spiel 1,0), die Haut liegt bei 3,35 (Spiel 3,46).
- **bc_1 (BernieCrane, 0,50):** Im Wohnzimmer ist Bernies Gesicht 63 statt vorher 88 (Spiel 62).

In SparkIV: `TimeCycle.ActorAmbient`, Sky-Feld `actorAmbient`, Uniform `actorAmbient` in `ModelView.html`.
