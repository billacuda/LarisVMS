# Bewegungserkennung

Bewegungsquellen, serverseitige Bewegung, Zonen und das Bewegungsraster.

## Quelle

Eine Kamera kann Bewegung auf mehrere Arten melden:

- **Serverseitige Bewegung**: Der Knoten vergleicht Videobilder innerhalb von Bewegungszonen oder des Bewegungsrasters.
- **Kameraereignisse**: die eigenen ONVIF-Bewegungsereignisse der Kamera.
- **Hersteller-Integration**: zum Beispiel Dahua / Amcrest Smart Events.
- **KI-Erkennung**: ein erkanntes bewegtes Objekt.

Die **Bewegungsquelle** der Kamera wählt, welche *eine* davon entscheidet, was der Modus „Bewegung“ aufbewahrt, damit die Zeitleiste keine doppelten Einträge für dasselbe Ereignis erhält. **Automatisch** wählt die aussagekräftigste, die eingerichtet ist. Ereignis-Markierungsregeln und KI-Objektbezeichnungen markieren die Zeitleiste unabhängig von dieser Wahl immer.

## Serverseitige Bewegung

**Serverseitige Bewegung** auf der Seite der Kamera schaltet die eigene Pixel-Bewegungserkennung des Knotens ein oder aus. Lassen Sie sie aktiviert, um einfache Bewegung als Rückfallebene auf der Zeitleiste zu markieren, auch wenn eine andere Quelle die Aufzeichnung steuert. Deaktivieren Sie sie, um die dafür benötigte CPU zu sparen.

## Zonen

Die Seite **Zonen** einer Kamera bietet zwei Methoden. Es ist immer nur eine aktiv, und beim Wechsel bleiben die Einstellungen der anderen erhalten.

**Raster**: Klicken Sie auf Zellen, um sie auszunehmen (rot). Zellen werden gelb, solange darin Bewegung ist, was Ihnen hilft, einen schwankenden Ast oder ein flackerndes Licht zum Ausnehmen zu finden. Eine Änderung der Rastergröße löscht die Maske.

**Polygonzonen**:

- **Bewegung**: ein zu überwachender Bereich.
- **Ignorieren**: ein auszunehmender Bereich (ein Baum, ein öffentlicher Gehweg). Pixel in einer Ignorierzone zählen nie, auch wo sie eine Bewegungszone überlappt.
- **Privat**: ein zu schwärzender Bereich. Zeichnen Sie diese unter **🔒 Privatmasken**, was unabhängig davon funktioniert, welche Bewegungsmethode (Raster oder Polygonzonen) die Kamera verwendet. Der Bereich ist in Aufnahmen, Live-Ansicht (beide Streams), Schnappschüssen und Exporten verborgen. Ereignisse finden dahinter wie gewohnt statt (ihre Schnappschüsse werden wie das Video maskiert), es sei denn, Sie aktivieren für diese Kamera in der Ansicht „Privatmasken“ **Erkennungen innerhalb von Privatzonen ebenfalls ignorieren**. Die Maske folgt der gezeichneten Form mit einem kleinen Rand, damit keine Kante durchscheint. Bei einer Kamera mit Privatzone wird das Video während der Aufzeichnung neu kodiert, was etwas CPU belegt (oder die GPU, wenn der Knoten eine unterstützte hat).
- Zonen für **kameraseitige Bewegung** können gespeichert werden, sind aber noch nicht aktiv.

Um eine Zone umzuformen, wählen Sie sie in der Liste aus, ziehen Sie ihre Ecken und klicken Sie dann auf **Speichern**. Beim Zeichnen entfernt ein Rechtsklick den letzten Punkt. Wenn Sie mit der Maus über eine Zone in der Liste fahren, wird sie im Bild hervorgehoben. Sie können auch auf eine Ecke klicken und dann dorthin klicken, wo sie hin soll.

**Empfindlichkeit** ist der Prozentsatz der Pixel einer Zone, der sich ändern muss. Niedriger ist empfindlicher. Große Zonen benötigen niedrigere Werte, da echte Bewegung nur einen kleinen Teil davon abdeckt. Beginnen Sie niedrig (etwa 3 %) und erhöhen Sie den Wert nur, wenn Wind oder Lichtwechsel auslösen.
