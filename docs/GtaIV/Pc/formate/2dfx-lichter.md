# GTA IV (PC): Lichter in Modellen (2dfx-Lichteffekte)

Gilt nur für **GTA IV, PC-Version** (mit TLAD und TBoGT). Übersicht aller Formate: [../daten-unterstuetzung.md](../daten-unterstuetzung.md).

Modelle (`.wdr`, `.wdd`, `.wft`) können Lichteffekte enthalten, also Lampen und Wandleuchten. SparkIV liest sie in `ModelScene` (`Lights`) und schickt sie über `CutsceneLocation.LightsMessage` an die Ansicht.

## Felder

| Feld | Bedeutung | Herkunft |
|---|---|---|
| Ort, Richtung | Lage im Modell | |
| Farbe (RGB, Bytes) | Farbe | |
| `Distance` (+0x38) | **Radius** des Lichts | GTAIV.exe 0xC1E200 → 0xABCCD0 |
| `Range` (+0x3C) | **Stärke** | ebd. |
| Falloff, HotSpot | äußerer und innerer Kegelwinkel eines Spots | |
| Intensity | weiterer Faktor | |
| Flags (+0x4C) | siehe unten | |

## Flags

| Flag | Bedeutung | Status | Herkunft |
|---|---|---|---|
| 0x20 | an bei Tag | verwendet | 0xC1F9A0 |
| 0x40 (ohne 0x20) | nur nachts | verwendet | ebd. |
| 0x80 | Flackern | ignoriert | 0xC1E200 |
| 0x200, 0x1000 | Blinken | ignoriert | ebd. |
| 0x4000 | Licht-Flag 2, nur wenn das Objekt (Instanz-Flag 0x80) es erlaubt; Wirkung offen | **unklar** | ebd. |
| **0x8000** | **Schattenlampe** (Licht-Flag 4), nur wenn die Instanz des Objekts Flag 0x100 hat (siehe [wpl.md](wpl.md)) | verwendet (seit 22.10.2026) | ebd. |
| 0x10000 | eigener Pfad (Lichtfarbe aus 0xC1DAA0) | ignoriert | ebd. |
| 0x80000 | Licht-Flag 8 | **unklar** | ebd. |
| 0x100000 | Licht-Flag 0x10 | **unklar** | ebd. |

### Flags in der Lichtliste des Spiels

`Light_Add` (0xABCCD0) legt die Lichter des Bildes an (Struktur mit 0x80 Byte: +0x20 Ort, +0x44 Typ, +0x48 Flags, +0x54 Radius):

- **Innen/außen:** Bit 0x20 heißt im Innenraum, 0x40 außen, 0x60 beides.
- **Lichter der Zwischensequenz:** 0x80. Beginnt ihr Name mit `env`, kommt 0x100 dazu, mit `shd` außerdem 4 (0x9CA7E0).
- **Korona** (`CoronaSize`, `CoronaHash`): wird gelesen, aber **nicht unterstützt**, SparkIV zeichnet keine Koronas. Vermutlich ist deshalb die Innenseite der Deckenspots in bc_1 dunkel; im Spiel leuchtet sie hell weiß.
- **Lichter der Zwischensequenz und Räume** (Beobachtung, 23.10.2026; im Exe nicht belegt):
  - Ein Cut-Licht wirkt auf Figuren unabhängig vom Raum, in dem es steht. Beleg: In et01_aa (Section 4, Frame 484) steht `FillLight1_3` 1,2 m hinter der Kamera und damit hinter der Wand im Nachbarraum. Im Video des Spiels beleuchtet es die Figuren vor der Kamera.
  - Eine Raumprüfung je Licht ist im Spiel nicht gefunden. Belegt ist die Stencil-Regel: Cut-Lichter treffen **nur Figuren**, drinnen wie draußen.
    - Drinnen: Trace GTAIV.3, et01_aa.
    - Draußen: Trace GTAIV.4, et01_ba Frame 9350, ausgelesen mit d3dretrace am 23.10.2026. Nach `BoothLight_0` (Aufruf 145552474) ist das Gesicht des Wärters von 1,72 auf 2,69 gestiegen, die Kabine unverändert (Paneel 0,52/0,88/0,94).
  - SparkIV wendet Cut-Lichter seit dem 23.10.2026 auf keine Karten- oder Requisiten-Meshes mehr an (`ModelView.html`, `marked < 0 && baked`).

## Welches Programm eine Lampe zeichnet

| Lampe | Programm in `deferred_lighting.fxc` | Formel |
|---|---|---|
| normal | p31 (Stencil ≥ 8: Innenraum-Objekte und Figuren), p32 (Stencil < 8), p30 (ohne Stencil) | Abfall 1,5 · ((1 − d²/r²)² − 1/3); × sat((n·l − 0,2) · 1,25); kein AO, kein Glanz |
| mit Schatten (Flag 4) | p35 | Abfall 1,5 · ((1 − d²/r²)² − 1/3); × sat(1,5 · (n·l − 1/3)); kein AO, kein Glanz; × Schatten aus 4 Abtastungen einer Paraboloid-Schattenkarte (siehe [Lampenschatten](#lampenschatten-p35)) |

Belege aus `GTAIV.3.trace`, et01_aa, Frame 10210:
- **Normale Lampen:** Die Tisch- und Wandlampen laufen über PS 0x25d41c80 (Draws 102287887/905/923). Dessen Code ist Instruktion für Instruktion p31. Früher stand hier p26 – das war **falsch**: p26–p29 (Abfall ((a²)² − 0,1)/0,9, mit Glanz) sind die Programme der Cut-Lichter.
- **Schattenlampe:** Die Deckenlampe in Elizabetas Wohnzimmer (Lampe 0x108060, Instanz 384) läuft über p35 (Draw 102287953). Malcs Hemd bekommt sein ganzes Lampenlicht von ihr (+2,6 bis +4,8, mit Schattenstreifen), die übrigen Lampen tragen dort nichts bei.
- **Messmethode:** `d3dretrace -D N` speichert den Zustand **nach** Aufruf N.

### Lampenschatten (p35)

Abgeleitet aus dem Shader-Code von p35 und den Konstanten dieses Draws (Aufrufe 102287940–102287951):

| Register | Name | Wert (Deckenlampe) | Bedeutung |
|---|---|---|---|
| c72–c75 | `dShadowMatrix` | Zeilen (0,0,1), (1,0,0), (0,−1,0), Versatz (−y, z, −x − 256) der Lampe | Lichtraum: Achse = Welt-X, quer = Welt-Y und −Welt-Z |
| c76 | `dShadowParam891113` | (0; 5; 256; 5,2) | z: die 256, die die Matrix abzieht; w: Radius |
| c66 | `dShadowParam0123` | (−0,025; 0; 0; −0,1923) | w = −1/Radius |
| c77 | `dShadowOffsetScale` | (0,2500244; 0,5000244; 0,0029297; 0,25) | xy: Platz der Kachel im Atlas, z: Abstand der Abtastungen, w: Kachelgröße |
| s14 | `gShadowZSamplerCache` | 4096 × 4096, G16R16F | Atlas der Lampen-Schattenkarten |

Ablauf im Shader:
1. **Punkt:** Weltpunkt aus der Tiefe, um 0,1 m entlang der Normale verschoben, dann mit `dShadowMatrix` in den Lichtraum (L).
2. **Halbkugel:** Seite der Achse über das Vorzeichen von L.z (+256 wieder addiert). L.z ≤ 0 liest Kanal R, sonst G.
3. **Projektion:** uv = L.xy / (|L.z| + |L|) · (0,5; −0,5) + 0,5 (Dual-Paraboloid).
4. **Abtastungen:** 4 Stück, um einen Winkel pro Pixel gedreht (frac(vPos · (3; 4,272)) einer Umdrehung). Abstände 0,75 / 0,25 / 0,5 / 1 × c77.z. Jede wird auf die Kachel begrenzt und dann mit c77.w und c77.xy in den Atlas gelegt.
5. **Vergleich:** beleuchtet, wenn gespeicherter Wert + |L| · c66.w ≥ 0, also gespeicherter Abstand ≥ eigener Abstand / Radius. Der Schatten ist der Mittelwert der 4 Abtastungen.

**Inhalt der Schattenkarte** (aus dem Zustands-Dump von Draw 102287887, `PS_RESOURCE_14`):
- Die Kachel der Deckenlampe (1024 × 1024, Platz 0,25/0,5) enthält **nur die Figuren**: 6 Silhouetten mit Werten 0,23–1,0.
- Wände und Möbel sind nicht darin. Leer ist 1.
- Belegte Pixel: R-Hälfte 6013, G-Hälfte 45225.
- Die Datei von apitrace (PFM) speichert die Zeilen **von oben nach unten**.

**Wie das Spiel die Figuren hineinzeichnet** (jedes Frame neu; Aufrufe 102284209–102284772):
1. Ziel: eine 1024×1024-Textur G16R16F (0x42f1ae60), per Quad (PS 0x25d439e0, c66 = (1, 1, 0, 1)) auf (1, 1) gesetzt.
2. Durchgang R: nur Rot schreiben, `gShadowParam14151617` c56 = (0, 0, 0, 0), CULL_CCW. Durchgang G: nur Grün, c56 = (0, 1, 0, 0), CULL_CW. Je 43 Draws (alle Figuren-Meshes, geskinnt, VS 0x25afd220 / 0x25afd9c0, PS 0x25d42d60).
3. Vertex-Shader (Zweig c56.x = 0): L = `gShadowMatrix` (c60–c63) · P, a = L.z + c59.z, s = +1 (R) oder −1 (G). Position = (L.xy / (|L| − s·a), |L|/R, 1) – **für alle Vertices**, auch die der anderen Seite (sie werden weit hinaus gestreckt). Weitergegeben: (s·a/R, |L|/R).
4. Pixel-Shader: verwirft, wo s·a/R > 0,01; schreibt den **zwischen den Vertices interpolierten** Wert |L|/R.
5. Ein Quad (PS 0x25d42900) kopiert das Ergebnis an die Kachelstelle im 4096er-Atlas (102284772).

In SparkIV nachgebaut (`lampShadowVertex`, `lampShadowFragment`). Das Culling musste in der WebGL-Umsetzung andersherum gesetzt werden (R: Vorderseiten weg, G: Rückseiten weg). Mit der wörtlichen Übertragung stand Malcs **Rücken** in der Karte (0,61 statt 0,55). Mit dem jetzigen Culling stimmen die gespeicherten Werte an Malcs Hemd mit dem Spiel-Atlas auf 4 Nachkommastellen überein.

**SparkIV** (seit 22.10.2026) zeichnet für die bis zu 4 nächsten Schattenlampen jedes Frame die Figuren und Requisiten der Szene in eine solche Karte (`lampShadowTarget`, `lampShadowVertex`, `lampShadow` in `gtaiv-pc.js`). Die Abfrage läuft wie in p35. Unterschiede zum Spiel:
- R32F mit 1024 je Halbkugel statt einem G16R16F-Atlas.
- Die Auswahl der Lampen nach Nähe zur Kamera ist **geschätzt**.
- Unsere Karte ist der des Spiels ähnlich: R 6484, G 36098 Pixel, kleinster Wert 0,223 gegen 0,235.

Wirkung in et01_aa, gemessen an Spiel-Snapshots desselben Moments:
- Sofa unten links: 46,9 → 37,6, Spiel 37,1.
- Sessel unten links: 36,2 → 31,8, Spiel 31,4.

**Offen:** Im Spiel zeigt Malcs Hemd im Licht der Deckenlampe Konturbänder mit dunklen Rändern (Self-Shadowing-Artefakte). SparkIV zeigt sie nicht, obwohl die Karte übereinstimmt. Unser Abfragepunkt liegt 1,9 cm vor der gespeicherten Fläche. Vermutete, aber **unbelegte** Ursache: die auf 1/64 abgerundete und durch die Normal-Map verbogene G-Buffer-Normale des Spiels beim Versatz um 0,1 m. Das Hemd ist dadurch bei uns noch etwa 7 % zu hell.

## Lichter für vorwärts gezeichnete Objekte

Durchsichtige Map-Teile, etwa ein Schleiervorhang, werden nach den Deferred-Lichtern einzeln gezeichnet. Sie bekommen höchstens 8 Lichter (Shader-Register c19–c31, `LightPosition*`, `LightCol*`, `LightFallOff`, `LightCone*`):

- **Auswahl:** `Lights_ChooseForEntity` (0xAC5670) bewertet jedes Licht mit `Light_ScoreForEntity` (0xABD8A0).
  - Ein Licht zählt nur, wenn sein **Radius größer als 4,0 m** ist (Konstante 0xFE8AB8).
  - Seine Bewertung ist (1 − d²/(r + Ausdehnung)²)⁴ × Luma(Farbe) × Intensität.
  - Die 8 besten werden genommen.
- **Ausgeschlossen:**
  - Lichter der Zwischensequenz (0x80, ohne 0x100) fallen weg.
  - Ein Objekt im Innenraum bekommt keine reinen Außenlichter.
- **Hochladen:** `Lights_SetForwardConstants` (0xAC30F0).
- **Beleg:** et01_aa: Der Vorhang bekam nur Deckenlampe (5,2 m) und Spot (5 m), nicht die Tischlampen (2–3,5 m).

In SparkIV: Uniform `forwardLit` in `ModelView.html`.
