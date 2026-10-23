# GTA IV (PC): Innenräume (`mlo`-Abschnitt der `.ide`)

Gilt nur für **GTA IV, PC-Version** (mit TLAD und TBoGT). Übersicht aller Formate: [../daten-unterstuetzung.md](../daten-unterstuetzung.md).

Ein Innenraum (Interior, MLO) ist in einer `.ide` beschrieben. Eine Platzierung in einer `.wpl` mit dem Namen des Innenraums setzt alle seine Objekte in die Welt. Beispiel: Bernies Wohnung `BernieCrane` in `pc/data/maps/interiors/generic/public_3.ide`.

## Aufbau

```
<Name>, <Flags>, <Anzahl Objekte>, <Anzahl Räume>, <Anzahl Portale>, ...   (nicht eingerückt)
	<Objekt>, x, y, z, qx, qy, qz, qw, <LOD>, <Instanz-Flags>,              (eingerückt, je Objekt)
mloroomstart
	<Raum>, <Anzahl Objekte>, <Anzahl Portale>, maxX, maxY, maxZ, minX, minY, minZ, <?>, <Modifikator-Hash>, <?>
	<Objektnummern, -1 als Füllung>
	roomend
mloportalstart
	<von>, <nach>, <Ecke 0 xyz>, <Ecke 1 xyz>, <Ecke 2 xyz>, <Ecke 3 xyz>, -1, -1, -1, -1, <Flags>, 16777215, 1
mloend
```

## Objekte

| Feld | Inhalt | Status | Herkunft |
|---|---|---|---|
| 0 | Modellname | verwendet | |
| 1–3 | Ort im Innenraum | verwendet | |
| 4–7 | Drehung als Quaternion. Anders als in der `.wpl` **nicht** invertiert: zuerst die Drehung des Objekts, dann die des Innenraums. | verwendet | Vergleich mit Spielbildern (Bernies Regale, et01_aa) |
| 8 | 0 = Modell selbst, sonst ferne Version (wird nicht gezeigt) | verwendet | |
| 9 | **Instanz-Flags**, meist 384 = 0x180. Dieselbe Bedeutung wie in der `.wpl`, siehe [wpl.md](wpl.md); 0x100 erlaubt Lampenschatten. | verwendet (0x100) | GTAIV.exe 0x9D76C0 |

## Räume

| Feld | Inhalt | Status | Herkunft |
|---|---|---|---|
| 0 | Name; `limbo` ist der Bereich außerhalb aller Räume (Wände) | verwendet | |
| 1, 2 | Anzahl Objekte, Anzahl Portale | – | |
| 3–5, 6–8 | Ecken der Raumbox (in der Datei Max vor Min; SparkIV sortiert) | verwendet | |
| 9 | meist 1 | **unklar** | |
| 10 | Hash des Zeitzyklus-Modifikators (`GxtFile.Hash` des Namens; 0 = keiner) | verwendet | Aufnahme `GTAIV.2.trace` (bc_1) |
| 11 | meist 111 | **unklar** | |

Die Räume werden ab 0 gezählt, `limbo` ist meist Raum 0. Die Zahlenzeile nach einem Raum listet die Nummern seiner Objekte.

### Welcher Raum gilt

- **Kamera:** Der Modifikator des Raums, in dem die Kamera steht, bestimmt Licht, Belichtung und Nebel.
- **Überlappende Boxen verschiedener Innenräume:** SparkIV nimmt den Raum, über dessen Boxboden der Punkt am niedrigsten steht. Belegt mit et01_aa: Die Box der Wohnung darunter (RM3a) reicht 1,3 m in Elizabetas Wohnzimmer. Das Spiel ordnet Kamera und alle Figuren dem Wohnzimmer zu (Aufnahme `GTAIV.3.trace`: `globalScalars.z` = 0,2 für alle Figuren). Wie das Spiel selbst den Raum bestimmt (Portal-Tracker), ist nicht entschlüsselt.
- **Boxen desselben Innenraums:** Es gilt der erste Raum in der Liste (bc_1: Das Wohnzimmer umfasst den Flur vor Bernies Tür).

## Portale

| Feld | Inhalt | Status | Herkunft |
|---|---|---|---|
| 0, 1 | Räume auf beiden Seiten (Raumnummern, 0 = `limbo` bzw. draußen) | verwendet | |
| 2–13 | vier Ecken | verwendet (Ecken 0 und 2) | |
| 14–17 | immer −1 | **unklar** | |
| 18 | **Flags** (meist 72 oder 73). Portale mit Bit 0x10 oder 0x4 nimmt das Spiel aus der Überblendung unten aus. | verwendet | GTAIV.exe 0xA8F6A0, 0xA8F7D0 (Portal +0x4C) |
| 19, 20 | 16777215, 1 | **unklar** | |

### Überblendung des Umgebungslichts über Portale

`Room_AmbientScale` (GTAIV.exe 0xA0D720) berechnet für Figuren und bewegliche Objekte den Anteil des Umgebungslichts (siehe `AmbientDownMult` in [timecyclemodifiers.md](timecyclemodifiers.md)):

1. Der Wert des Raums ist AmbientDownMult × Blend + (1 − Blend). Blend steht im Raum bei +0x4C; woher er kommt, ist offen (angenommen 1).
2. Für jedes Portal des Raums bildet das Spiel eine Kugel: Mittelpunkt in der Mitte zwischen Ecke 0 und Ecke 2, Radius gleich der halben Diagonale (0xB20EC0).
3. Liegt das Objekt im Abstand d innerhalb dieser Kugel, gilt t = 1 − d²/r².
4. **Portal nach draußen** (Raum 0): Wert += (1 − Wert) · t.
5. **Portal in einen anderen Raum** mit höherem Wert: Wert += (anderer − Wert) · t.

In SparkIV: `LocationRoom.Portals`, `actorAmbientAt` in `ModelView.html`.

## Offen

- **bc_1, Abschnitt 0, Frames 569–717:** Im Spielvideo hat Bernie an der Tür etwa den Wert 1 statt 0,5. Er wechselt mitten in einer durchgehenden Einstellung, bei Frame 715–720. Weder Raumboxen noch Portale (der Flur hat denselben Modifikator) erklären das. Die Aufnahme `GTAIV.2.trace` von bc_1 könnte es klären.
