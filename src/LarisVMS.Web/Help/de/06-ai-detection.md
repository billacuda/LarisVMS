# KI-Erkennung

Objekterkennung auf dem Knoten: Modelle, Hardware, Schwellenwerte, Schnappschüsse und das externe Backend.

Jeder Knoten kann auf seinen Kameras eine Objekterkennung in Echtzeit ausführen. Erkannte Objekte erhalten Live-Rahmen, eine farbige Markierung auf der Zeitleiste, ein Abzeichen auf der Live-Kachel und einen zugeschnittenen Schnappschuss. Eine Erkennung zählt für die Aufzeichnung im Modus „Bewegung“ auch als Bewegung. Die Erkennung läuft in einem eigenen Prozess, sodass ein Problem mit GPU oder Treiber nie die Aufzeichnung stoppt.

Aktivieren Sie sie pro Kamera mit **Diese Kamera auf Objekte überwachen**. Für eine genaue Erkennung sind nur **5–7 fps pro Kamera** nötig, begrenzen Sie daher die [Bildrate](#thresholds) auf diesen Wert, um die GPU-Last gering zu halten. Globale Standardwerte finden Sie unter **Einstellungen → KI-Erkennung**. Knoten und Kameras können die meisten davon überschreiben.

## Hardware

Ein Knotenpaket deckt jeden Rechner ab. Beim Start wählt der Vision-Dienst ein Backend für die Hardware, die er findet:

| Hardware | Backend | Benötigt auf dem Knoten |
|---|---|---|
| NVIDIA-GPU | CUDA (fällt auf DirectML zurück) | CUDA Toolkit 12.x und cuDNN 9.x |
| AMD- / Intel-GPU | DirectML | Ein aktueller GPU-Treiber |
| Keine GPU | CPU | Nichts |

Die Bearbeitungsseite des Knotens zeigt die erkannten Beschleuniger, und seine Einstellung **Beschleuniger** kann einen erzwingen. Modelle werden nie mitgeliefert. Ein Knoten bezieht die integrierten YOLOX-Modelle vom Server, und andere Modelle gehören in `C:\ProgramData\LarisVMS\models` auf dem Knoten.

## Langsames Backend

Kann ein NVIDIA-Knoten CUDA nicht verwenden, fällt die KI-Erkennung auf DirectML zurück, das um ein Vielfaches langsamer läuft. Es werden weniger Bilder analysiert, die GPU läuft heiß, und schnelle Objekte wie vorbeifahrende Autos können unerkannt bleiben. Die Seite **Knoten** zeigt dann neben dem Knoten **⚠️ KI auf DirectML** mit der Ursache an, und Super-Admins erhalten eine Benachrichtigung.

Die übliche Ursache ist ein fehlendes cuDNN. Installieren Sie auf dem Knoten das CUDA Toolkit 12.x und cuDNN 9.x für CUDA 12, über den cuDNN-Download von NVIDIA oder mit `pip install nvidia-cudnn-cu12`. Der Knoten findet sie innerhalb von 10 Minuten, kopiert sie neben sein KI-Backend und startet die Erkennung auf CUDA neu.

## Modellfamilien

Das Modell läuft einmal pro Knoten und bedient alle Kameras dieses Knotens, daher gelten die Modelleinstellungen pro Knoten, nicht pro Kamera.

- **Automatisch / YOLOX**: der Standard. Schnell, braucht kein Letterboxing und funktioniert gut auf leistungsschwachen und Nicht-NVIDIA-GPUs. **YOLOX-Größe** reicht von Nano (am schnellsten) bis X (am genauesten). Verwenden Sie Nano oder Tiny auf leistungsschwachen Rechnern und L oder X auf starken GPUs.
- **D-FINE**: experimentell. Die Ergebnisse können unvorhersehbar sein, und Objekte, die im Modus „Slice“ zwei Kacheln überspannen, können übersehen oder doppelt erfasst werden. **Gewichte**: Obj2Coco kennt 80 Objektarten (klein oder mittel), Obj365 kennt 365.
- **Eigenes Modell**: jedes `.onnx`-Modell, das im Modellordner des Knotens abgelegt wird. Siehe [Eigene Modelle](#custom-models).

Eine Änderung des Modells startet die Erkennung jeder Kamera neu.

## TensorRT

**D-FINE TensorRT** bewirkt nur etwas, wenn auf dem Knoten TensorRT aktiviert ist (`Vision:EnableTensorRt`) und das TensorRT-10.x-SDK installiert ist. **FP32** bringt eine sichere Beschleunigung. **FP16** ist am schnellsten; läuft die Engine einer Kamera bei FP16 über, wird sie automatisch bei FP32 neu erstellt. Der erste Start bei jeder Kameraauflösung braucht mehrere Minuten, um die Engine zu erstellen.

## Eigene Modelle

Legen Sie eine `.onnx`-Datei in `C:\ProgramData\LarisVMS\models` auf dem Knoten ab. Beschreiben die eigenen Metadaten des Modells seinen Decoder, seine Eingabegröße und seine Bezeichnungen, funktioniert es einfach. Andernfalls fügen Sie eine `.json`-Datei mit demselben Namen hinzu, die das Fehlende liefert. **Aktualisieren** listet jedes von den Knoten gefundene Modell auf und markiert Modelle mit unvollständigen Metadaten. Ein Modell mit Warnung kann ausgewählt werden, läuft aber erst, wenn die Warnung behoben ist.

## Seitenverhältnis-Anpassung

Wie das Bild einer Kamera in die quadratische Eingabe des Modells passt:

- **Letterbox** (Standard): behält die Proportionen und füllt mit schwarzen Balken auf.
- **Strecken**: staucht das Bild, um das Quadrat zu füllen, was Formen verzerrt.
- **Slice**: zerschneidet das Bild in überlappende Quadrate in voller Auflösung. Am besten für kleine oder weit entfernte Objekte auf Weitwinkelkameras, kostet bei einer 16:9-Kamera aber etwa den doppelten Aufwand. Es erfordert GPU-Vorverarbeitung und aktiviert diese automatisch.

**GPU-Bildvorverarbeitung** verlagert die Farbumwandlung von der CPU auf die GPU, was bei Knoten mit vielen Kameras hilft.

## Schwellenwerte

- **Konfidenz**: die Mindestbewertung, ab der eine Erkennung zählt. Niedriger erfasst mehr Objekte, bringt aber Fehlerkennungen. Erhöhen Sie sie bei einer Kamera, die immer wieder etwas falsch erkennt.
- **IoU**: wie stark sich zwei Rahmen überlappen müssen, um als ein Objekt zusammengeführt zu werden.
- **Erkennungs-Stream**: „Neben“ ist leichter; „Haupt“ sieht mehr Details.
- **Ausrichtung**: Manche im Korridor montierte Kameras melden einen Querformat-Stream, senden aber Hochformat-Video. Stellen Sie bei diesen Kameras **Hochformat** ein, damit Erkennung und Schnappschüsse nicht gestaucht werden.
- **Maximale Erkennungs-Bildrate**: Bilder pro Sekunde und Kamera, die das Modell erreichen. **5–7 fps genügen für eine genaue Erkennung und Verfolgung.** Höhere Raten erhöhen die GPU-Last, ohne die Ergebnisse zu verbessern. 0 bedeutet keine Begrenzung.

## Schnappschüsse

Jedes verfolgte Objekt erhält einen Schnappschuss, zugeschnitten aus seinem besten Bild.

- **Ruhezeit-Limit**: wie lange ein Objekt unsichtbar sein darf, bevor sein Schnappschuss geschlossen wird.
- **Ruhende Objekte melden**: meldet auch Objekte, die vorhanden sind, sich aber nicht bewegen, etwa ein geparktes Auto.
- **Genauigkeit der Schnappschuss-Bewegung**: schließt einen Schnappschuss kurz nachdem sein Objekt das Bild verlässt, damit ein späteres Objekt derselben Art nicht darin aufgeht. **Toleranz beim Verlassen** ist die Wartezeit. Ist sie zu kurz, wird ein Objekt, das kurz innehält, in zwei Schnappschüsse geteilt.
- **Zuschnittrand für Schnappschüsse**: zusätzlicher Platz um das Objekt, als Prozentsatz seines Rahmens.

Mehrere Objekte derselben Art in einem Schnappschuss erscheinen als Anzahl (×2). Jedes wird außerdem einzeln mit eigenen Farben und eigener Geschwindigkeit aufbewahrt.

## Objektfarben

Die Farbe jedes verfolgten Objekts wird aus seinem deutlichsten Bild gelesen: eine Farbe für Fahrzeuge, Tiere und andere Objekte sowie Farben für Oberteil und Hose bei Personen. Schnappschüsse zeigen für jede ein in dieser Farbe gemaltes Abzeichen, und die Abschnitte **Farbe**, **Oberteil** und **Hose** im Filter der Schnappschüsse beschränken die Ergebnisse auf die angekreuzten Farben. Auch Erkennungsalarme können nach Farbe filtern.

Farben stammen aus dem Bild, nicht aus dem Modell, funktionieren also mit jedem Detektor. Die Farben knapp außerhalb des Rahmens werden ebenfalls erfasst und beiseitegelegt, damit nicht der Rasen um einen Hund oder die Straße um ein Auto gewinnt. Es bleiben bestmögliche Schätzungen: Unruhige Szenen, überlappende Objekte und die Beleuchtung können alle zu einer falschen Farbe führen. Die Beleuchtung wirkt am stärksten: Schatten, Scheinwerfer und Weißabgleich verschieben Farben, und ein hellgraues Auto kann als silbern oder weiß erscheinen. Solange eine Kamera im Schwarzweiß-Nachtmodus (IR) ist, werden keine Farben erfasst. Deaktivieren Sie dies mit **Objektfarben** unter **Einstellungen → KI-Erkennung**.

## Geschwindigkeitsmessungen

Eine Geschwindigkeitsmessung misst die Zeit von Objekten zwischen zwei Linien quer über einer Straße. Klicken Sie auf der Seite **Geschwindigkeitsmessungen** der Kamera (über **Kameras → Bearbeiten**) auf **Neue Geschwindigkeitsmessung**, klicken Sie auf die beiden Enden von Linie **A**, dann auf die beiden Enden von Linie **B**, und geben Sie den tatsächlichen Abstand zwischen den Linien ein, entlang der Straße gemessen. Jedes verfolgte Objekt, das beide Linien überquert, erhält eine Geschwindigkeit, die auf seinem Live-Rahmen und seinem Schnappschuss angezeigt wird. **Richtung** misst standardmäßig beide Richtungen; wählen Sie **Nur A nach B** oder **Nur B nach A**, um eine Spur einer Straße mit Gegenverkehr zu messen. **Geschwindigkeitseinheiten** (km/h oder mph) finden Sie unter **Einstellungen → KI-Erkennung**.

Ein Objekt, das eine Messung erfasst, wird als bewegt aufgezeichnet, auch wenn es sonst stillzustehen scheint, etwa ein Fahrzeug, das direkt auf die Kamera zufährt. Die Messung verwendet die untere Mitte jedes Rahmens, wo das Objekt den Boden berührt, zeichnen Sie die Linien also auf die Straßenoberfläche. Für eine gute Messung:

- Zeichnen Sie die Linien quer zur Fahrtrichtung, auf ebenem Boden und so weit auseinander, wie die Ansicht erlaubt, solange die Objekte deutlich im Bild bleiben.
- Messen Sie den Abstand sorgfältig: schreiten Sie ihn ab oder verwenden Sie das Messwerkzeug einer Karte.
- Prüfen Sie es einmal mit einem Fahrzeug, das mit bekannter Geschwindigkeit vorbeifährt, und passen Sie den Abstand an, wenn der Messwert abweicht.

Messwerte sind Schätzungen. Die Erkennungs-Bildrate der Kamera begrenzt, wie genau ein Überqueren gemessen wird, daher sind kurze Messstrecken und schnelle Fahrzeuge am ungenauesten. Das Speichern einer Messung startet die KI-Erkennung der Kamera neu.

Eine Messung kann ein **Geschwindigkeitslimit** haben (optional). Geschwindigkeiten darüber werden rot angezeigt und in der Geschwindigkeits-Heatmap gesondert gezählt.

Gemessene Geschwindigkeiten erscheinen an mehreren Stellen:

- **Live-Ansicht**: auf dem Rahmen jedes Objekts, solange es im Bild ist.
- **Schnappschüsse**: ein ⏱-Abzeichen auf der Karte und ein Filter **Geschwindigkeit** („mindestens“) in der Filterliste.
- **Wiedergabe**: eine Markierung auf der Zeitleiste bei jeder Messung (rot bei Überschreitung des Limits; Details beim Darüberfahren) und ein Abzeichen über dem Video, solange das Objekt im Bild ist.
- **Dashboard**: Das Widget **Geschwindigkeitsprotokoll** zeigt die neuesten Messungen. Klicken Sie auf seinen Titel für das vollständige Protokoll mit Sortierung, Filterung und CSV-Download.
- **Aktivität**: Wechseln Sie zu **Geschwindigkeiten** für eine Heatmap der Durchschnittsgeschwindigkeit, Höchstgeschwindigkeit, Anzahl über dem Limit oder Anzahl gemessen, nach Tag und Stunde. Der Verlauf wird so lange aufbewahrt wie der Aktivitätsverlauf, weit über das Ende der Aufnahmen hinaus.
- **Alarme**: eine Regel „Geschwindigkeit über einem Limit“.

## Bewegung

Schnappschüsse, Alarme und die Live-Rahmen „Bewegt“ zählen nur Objekte, die irgendwohin unterwegs sind. Der Rahmen eines Detektors wackelt von Bild zu Bild ein wenig, selbst bei einem geparkten Auto, daher genügen ein paar Pixel Änderung nicht. **Bewegungserkennung** unter **Einstellungen → KI-Erkennung**, oder pro Kamera unter **Kameras → Bearbeiten**, bestimmt, wie das beurteilt wird.

**Standard** (der Vorgabewert) betrachtet die Form des jüngsten Weges jedes Objekts:

- Alles, was sich gleichmäßig in eine Richtung bewegt, zählt innerhalb weniger Bilder (etwa eine halbe Sekunde bei 7 fps), sei es ein Auto, das in unter einer Sekunde durchs Bild fährt, oder ein Lkw, der aus einer Einfahrt fährt. Die Schritte jedes Objekts werden mit dem Wackeln seines eigenen Rahmens verglichen, sodass ein großes Objekt nicht weiter wandern muss als ein kleines.
- Etwas, das langsamer kriecht, als sein Rahmen wackelt, einen Bruchteil eines Schritttempos, zählt, sobald sein Weg ein paar Sekunden lang einer gleichmäßigen Linie gefolgt ist. Wackeln geht hin und her und bildet daher nie eine Linie.
- Wie weit ein Objekt kommen muss, wächst mit seiner Größe, aber langsamer als die Größe selbst. Ein großer Lkw muss nicht erst die halbe eigene Länge zurücklegen.
- Ein Objekt, das am Bildrand hereinkommt oder hinter einem Pfosten oder Baum hervortritt, wird anhand der sichtbaren Seite seines Rahmens beurteilt.
- Ein Objekt, das direkt auf die Kamera zu- oder von ihr wegfährt, zählt, sobald es größer oder kleiner wird.
- Der Rahmen eines geparkten Autos, der sich ändert, wenn jemand davor vorbeigeht, zählt nicht.

**Bewegungsempfindlichkeit** (nur Standard) ist standardmäßig **Normal**. **Hoch** erkennt Bewegung früher, und **Niedrig** ignoriert mehr Wackeln. Senken Sie sie bei einer Kamera, auf der ein geparktes Auto oder ein schwankender Busch als bewegt gemeldet wird, und erhöhen Sie sie, wo langsame Objekte übersehen werden.

Ein Rahmen, der nur seine Form ändert, etwa der einer Topfpflanze, wenn der Detektor einmal die Blätter einbezieht und einmal nicht, oder der eines geparkten Autos, wenn etwas davor vorbeigeht, zählt nicht. Bewegung muss sich über die Bilder aufbauen, nicht in einem Sprung geschehen.

**Klassisch** ist die frühere Regel: Ein Objekt zählt, sobald sich sein Mittelpunkt innerhalb von 2 Sekunden um die halbe Größe seines Rahmens bewegt hat. Verwenden Sie sie, wenn „Standard“ sich auf einer Kamera falsch verhält.

Ein Objekt, das sich bewegte und hinter einem Baum, einem Pfosten oder einem geparkten Auto aus dem Blick gerät, bleibt dasselbe Objekt, wenn es innerhalb von etwa 30 analysierten Bildern (rund 5 Sekunden bei 6 fps) weiter auf derselben Linie wieder hervorkommt, wird also einmal gezählt und behält einen Weg. Es muss außerdem dieselbe Art von Objekt und von ähnlicher Größe sein, und seine Farbe muss übereinstimmen, wenn beide bestimmt werden können. Etwas, das in eine andere Richtung oder erst später wieder hervorkommt, ist ein neues Objekt.

## Bewegte Objekte

Der Weg jedes bewegten Objekts durch das Bild wird mit seinem Ereignis aufbewahrt, für die Heatmap **Szene** auf der Seite „Aktivität“.

**Bewegte Objekte** unter **Einstellungen → KI-Erkennung** wählt, welche Arten von Objekten als bewegt gelten können. Standardmäßig sind das **Personen**, **Fahrzeuge** und **Tiere**. Alles andere wird weiterhin erkannt und erscheint mit aktiviertem **Ruhend** als ruhender Rahmen, zählt aber nie als bewegt und kann daher keinen Schnappschuss und keinen Alarm auslösen. Nehmen Sie andere Objekte einzeln in die Liste **Andere Objekte** auf: Ein geliefertes Paket erscheint meist als Koffer, Rucksack oder Handtasche. Geben Sie bei einem eigenen Modell dessen eigene Objektnamen durch Kommas getrennt ein.

## Ignorierte Objekte

**Ignorierte Objekte** unter **Einstellungen → KI-Erkennung** schaltet Objektarten vollständig ab. Sie werden verworfen, sobald das Modell sie meldet, erhalten also nie einen Rahmen, einen Schnappschuss, ein Ereignis oder einen Alarm. Verwenden Sie dies für Dinge, die das Modell auf Ihren Kameras immer wieder falsch erkennt, etwa eine Terrasse, die es für ein Flugzeug hält. Kreuzen Sie ganze Kategorien (**Personen**, **Fahrzeuge**, **Tiere** und **Andere Objekte**) oder einzelne Objekte aus der Liste an. Die Schreibweise beider Modelle passt, daher ignoriert **airplane** auch **aeroplane**. Geben Sie bei einem eigenen Modell dessen eigene Objektnamen durch Kommas getrennt ein.

Die Seite **Bearbeiten** einer Kamera kann statt der globalen Liste eine eigene wählen: Wählen Sie unter **Ignorierte Objekte** **Für diese Kamera auswählen**. Wenn Sie dort nichts auswählen, erkennt diese Kamera alles, auch was die globale Liste ignoriert. Das Speichern startet die Erkennung dieser Kamera neu, was nur die Verfolgung zurücksetzt.

## Zittern

Die **Zitterschwelle** ist die kleinste Bewegung in Pixeln, die überhaupt zählt, unabhängig von der Größe des Objekts. Im Modus „Klassisch“ glättet die **Zitterunterdrückung für stehende Objekte** das Wackeln eines weit entfernten geparkten Fahrzeugs; sie kann aber auch ein tatsächlich langsam bewegtes Objekt als ruhend festhalten und ist daher standardmäßig aus. Für „Standard“ gilt sie nicht, da es das Wackeln selbst behandelt.

## Externes Backend

**Externer HTTP-Dienst** führt kein Modell auf dem Knoten aus. Jedes Bild wird an einen von Ihnen betriebenen Inferenzserver gesendet, der Rahmen als JSON zurückgibt. Verwenden Sie ihn für Modelle, die LarisVMS nicht mitliefert, oder für Knoten ohne GPU. Verfolgung, Schnappschüsse, Zeitleiste und Live-Überlagerung funktionieren genauso.

Der Dienst muss `POST /v1/detect`, `GET /v1/models` und `GET /healthz` bereitstellen. **Testen** prüft die Verbindung und lädt die Modellliste. Der **API-Schlüssel** wird als Bearer-Token gesendet. **Übertragung**: JPEG funktioniert mit jedem Dienst. Die Rohmodi YUV und BGRA überspringen die JPEG-Kodierung, benötigen aber die 4- bis 10-fache Bandbreite und funktionieren nur, wenn das Modell sie ankündigt.
