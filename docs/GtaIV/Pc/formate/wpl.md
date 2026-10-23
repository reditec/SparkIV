# GTA IV (PC): Platzierungen (`.wpl`, binär)

Gilt nur für **GTA IV, PC-Version** (mit TLAD und TBoGT). Übersicht aller Formate: [../daten-unterstuetzung.md](../daten-unterstuetzung.md).

Eine `.wpl` legt fest, welches Modell wo in der Welt steht. Die losen Dateien liegen unter `pc/data/maps/**`, weitere stecken in den `.img`-Archiven der Karte.

## Kopf

| Offset | Typ | Inhalt | Herkunft |
|---|---|---|---|
| 0 | uint32 | Version, immer 3 | `CutsceneLocation.ReadPlacements` |
| 4 | uint32 | Anzahl der Instanzen | ebd. |
| 68 | – | Beginn der Instanzen, je 48 Byte | ebd. |

## Instanz (48 Byte)

| Offset | Typ | Inhalt | Status | Herkunft |
|---|---|---|---|---|
| 0 | 3 × float | Ort x, y, z | verwendet | |
| 12 | 4 × float | Drehung als Quaternion x, y, z, w. Das Spiel liest sie **invertiert**: Die Drehung des Modells ist die Konjugierte. | verwendet | Vergleich mit Spielbildern |
| 28 | uint32 | Hash des Modellnamens (`GxtFile.Hash`, Jenkins one-at-a-time, Kleinbuchstaben) | verwendet | |
| 32 | uint32 | **Instanz-Flags**, siehe unten | teilweise verwendet | GTAIV.exe 0x9D76C0 (Feld 8 des Eintrags) |
| 36 | int32 | Index der fernen Version (LOD) in der losen `.wpl` gleichen Namens; −1 = keine | verwendet | |
| 40, 44 | – | unbekannt | **unklar** | |

### Instanz-Flags (Offset 32)

Das Spiel überträgt drei Bits beim Anlegen des Objekts in dessen Flag-Feld (+0x28). Gefunden in `Entity_CreateFromInstance` (GTAIV.exe 0x9D76C0, Name aus `ApplyCutsceneFindings.java`):

| Instanz-Bit | Objekt +0x28 | Bedeutung | Status |
|---|---|---|---|
| 0x80 | \|= 4 | Das Lampen-Flag 0x4000 der Modell-Lichter wird wirksam. Die genaue Wirkung ist offen. | ignoriert |
| 0x100 | \|= 8 | **Lampen dieses Objekts dürfen Schatten werfen.** Nur zusammen mit dem Lampen-Flag 0x8000 (siehe [2dfx-lichter.md](2dfx-lichter.md)) wird eine Lampe als Schattenlampe gezeichnet. | verwendet (seit 22.10.2026) |
| 0x200 | \|= 2 | unbekannt | **unklar** |
| 1, 4, 0x20, 0x40 | Bits im Objekt-Feld +0x24 (0x1000000, 0x100000, löscht 0x20000000, löscht 0x80000000) | unbekannt | **unklar** |

Die Übergabe an die Lichter: `Entity_AddModelLights` (0xC1DB60) reicht die Bits 1, 2 und 3 von +0x28 an `Light_FromEffect` (0xC1E200) weiter.

**Beleg im Bild:** In bc_3 hat die Wandlampe am Pier das Lampen-Flag 0x8000, ihre Instanz aber nicht 0x100. Im Spielvideo hat sie den orangen Schein der normalen Lampe. Mit Schattenprogramm verschwand er in SparkIV, mit der Instanz-Bedingung ist er wieder da.

In SparkIV: `MapIndex.LampShadows`, `LocationObject.LampShadows`, im Lichter-Datensatz `shadows`.
