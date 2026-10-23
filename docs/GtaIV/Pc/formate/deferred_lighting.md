# GTA IV (PC): Beleuchtungs-Shader (`deferred_lighting.fxc` und Verwandte)

Gilt nur für **GTA IV, PC-Version** (mit TLAD und TBoGT). Übersicht aller Formate: [../daten-unterstuetzung.md](../daten-unterstuetzung.md).

Die Shader liegen in `common/shaders/win32_30/`. Disassembliert mit `d3dcompiler_47` (Skript `fxlist.py`). Die Programme werden in Dateireihenfolge ab 0 gezählt, Vertex- und Pixel-Programme gemeinsam. Gemessen in den D3D-Aufnahmen `GTAIV.3.trace` (TLAD, et01_aa) und `GTAIV.1.trace` (bc_3).

## Ablauf eines Bildes (Innenraum, et01_aa)

1. **G-Buffer:** Farbe (GB0), Normale (GB1), Glanz/AO (GB2), Tiefe; außerdem ein **Stencil-Wert** je Objekt (siehe unten).
2. **Sonne und Umgebung** (p21): Albedo × ((c37 + c38 · sat(0,5 − 0,5 n.z)) × AO + Sonne) + Spiegelung · `globalScalars.w`.
3. **Lichter der Zwischensequenz** (p29), dann **Lampen** (p31 im Innenraum, p32 außen; PS 0x25d41c80 in `GTAIV.3.trace`) und **Lampen mit Schatten** (p35); Formeln in [2dfx-lichter.md](2dfx-lichter.md).
4. **Leuchten durchsichtiger Emissive** (Glühbirnen, Schirme, PS 0x24B6EE60):
   - Farbe: Textur × Vertexfarbe × `emissiveMultiplier` × `colorize`; Alpha: Textur.a × Vertex.a × `globalScalars.x`.
   - Zustand: SRCALPHA/ONE, nur Vorderseiten (CULL CW), schreibt Tiefe.
   - Kommt **vor** dem Nebel und allen vorwärts gezeichneten Teilen.
5. **Nebel** (PS 0x2581A220): Anteil = w · sat(z/Start) + (1 − w) · sat((z − Start)/(Ende − Start)) (c41), mit der Sichttiefe z. Außerdem Tiefeneffekt `gDepthFxParams` (c16).
6. **Vorwärts gezeichnete durchsichtige Teile:**
   - Schleiervorhang u. a. mit `gta_normal` vorwärts (PS 0x2493AC20): bis zu 4 Lichter, Lichter ohne AO, dann Nebel.
   - Zeichenzustand: nur Vorderseiten (CULL CW).
7. **Tone-Mapping.**

## Stencil-Wert je Objekt

`Entity_StencilValue` (GTAIV.exe 0x5D4E40):

| Bit | Bedeutung |
|---|---|
| 8 | Objekt im Innenraum (+0x24 Bit 0x8000000) |
| 1 | Figur |
| 2 | Fahrzeug |
| 128 | weiteres Bit, von den Licht-Shadern abgezogen (gemessen bei einigen Figuren: 137) |

Gemessen in et01_aa: Karte und Requisiten 8, Figuren 9 oder 137.

## Lichter der Zwischensequenz und Stencil

| Programm | beleuchtet | Bedingung (s = Stencil, ohne 128) |
|---|---|---|
| p27 | Objekte im Innenraum, auch die Karte | s ≥ 8 |
| p28 | Objekte außen | s < 8 |
| p29 | nur Figuren | (s mod 8) = 1 |

Keines der drei Programme liest AO aus dem G-Buffer. sat(AO² + 0,5) steht nur in p26, und das zeichnet in den untersuchten Aufnahmen kein Licht. Die Lampen laufen über p30–p32 (ohne AO). Früher stand hier, die Lampen nutzten p26 – das war falsch.

### G-Buffer der Figuren (gta_ped, PS 0x25ee22a0)

Aus dem Shader-Code (`GTAIV.3.trace`, Malcs Körper bei Aufruf 102287002):
- **rim** = max((v·n ≤ 0 ? 1 + 2·v·n : 1) · v4.y, 0). Dabei ist v die Blickrichtung zum Punkt, n die Normale nach der Normal-Map, v4.y = `globalScalars2`.w · Vertexfarbe Grün (Vertex-Shader; gs2.w zur Laufzeit 0,725).
- **GB2.z (AO)** = v4.x + rim², mit v4.x = 1 + gs2.y · gs2.z · (Rot · gs.z − 1).
- **GB2.x (Glanz)** = 0,25 · Glanzwert + 0,5 · rim².
- **Normale:** gespeichert als floor((n + 1) · 64)/128, mit Zusatzbits in GB1.w. Die Licht-Shader lesen nur die groben 7 Bit pro Komponente.

SparkIV hat rim am 23.10.2026 eingebaut (`pedRim` in `gtaiv-pc.js`) und noch am selben Tag wieder **ausgeschaltet** (`PedRimScale = 0`).
- **Grund:** Abgeglichen mit den Spielvideos (bc_3 s2 f437, s1 f854.75, s2 f108; bc_1 s3 f750.75) erzeugt rim einen hellen Saum an Haar und Silhouette, den das Spiel nicht zeigt. Ohne rim sind diese Bilder 0,2 bis 1,45 näher am Spiel, et01_aa bleibt unverändert.
- **Offen:** Wo die Ableitung falsch ist, z. B. das Vorzeichen von v (Punkt → Kamera statt Kamera → Punkt), oder wie GB2.z im Lichtpass gelesen wird.

**Gemessen:**
- **et01_aa** (Kamera im Innenraum, keine `env`-Lichter): Alle 5 Lichter laufen nur über p29. Wände, Vorhang und Ohrringe bekommen nichts, Malcs Arm +2,4.
- **bc_3 (draußen):** pro Licht mehrere Pässe, darunter „s < 8“.
- Wann das Spiel p27 nimmt, vermutlich bei Lichtern mit `env`, ist nicht geprüft.

In SparkIV: Lichter der Zwischensequenz treffen in einem Raum nur Figuren; ohne AO-Faktor.

## Gemessene Konstanten (et01_aa)

| Register | Wert | Bedeutung |
|---|---|---|
| c39 (PS) | (1, 1, 0,996, 0) | `globalScalars`: w = 0 drinnen (GlobEnvRefl, siehe [timecyclemodifiers.md](timecyclemodifiers.md)) |
| c39 (VS) | z = 0,2 bei Figuren und Requisiten, 0,996 bei der Karte | AO-Faktor (AmbientDownMult) |
| c41 | (3, 1500, 0, 0,784) | Nebel: Start, Ende, –, naher Anteil |
| c45 (VS) | (0,94, 0,06, 0,94, 0,06) | Tagesfaktor |
| c51 | (1, 1, 1, 1) | `colorize` |
