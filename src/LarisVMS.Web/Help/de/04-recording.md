# Aufzeichnung

Aufzeichnungsmodi, Zeitpläne, Ereignis-Markierungen, Vor- und Nachlauf sowie Segmentlänge.

## Modi

Jede Kamera zeichnet durchgehend auf ihrem Knoten auf. Der **Aufzeichnungsmodus** entscheidet, welche Aufnahmen *aufbewahrt* werden:

| Modus | Bewahrt auf |
|---|---|
| **Durchgehend** | Alles. |
| **Bewegung** | Aufnahmen rund um Bewegung: aus einer Bewegungszone, den eigenen Bewegungsereignissen der Kamera, einer Integration oder der KI-Erkennung (siehe [Bewegung](/Help/motion#source)). |
| **Zeitplan** | Aufnahmen, die innerhalb eines aktiven Zeitfensters beginnen. |
| **Ereignis** | Aufnahmen rund um eine Ereignis-Markierungsregel, bei der *Steuert die Aufzeichnung* aktiviert ist. |
| **Nur manuell** | Nichts, es sei denn, eine [manuelle Aufzeichnung](#manual-recording) läuft. Die übrige Zeit dient die Kamera nur der Live-Ansicht. |

Aufnahmen, die nicht aufbewahrt werden, werden kurz nach dem Schreiben gelöscht, statt auf die Aufbewahrungsfrist zu warten.

Die Modi „Bewegung“, „Zeitplan“ und „Ereignis“ sind **offen bei Fehlern**. Solange eine Kamera keine Bewegungszone, kein Zeitfenster und keine steuernde Regel hat, bewahrt sie alles auf, statt es zu verwerfen. Die Seite der Kamera warnt, wenn das der Fall ist. „Nur manuell“ ist nicht offen bei Fehlern, denn nichts aufzubewahren ist genau sein Zweck.

Legen Sie den globalen Modus unter **Einstellungen → Aufzeichnungsstandards** fest und überschreiben Sie ihn auf der Seite einer Kamera.

## Manuelle Aufzeichnung

Eine manuelle Aufzeichnung bewahrt die Aufnahmen einer Kamera auf, bis jemand sie stoppt, unabhängig vom Aufzeichnungsmodus der Kamera. Starten Sie eine mit der Aufnahmetaste auf einer [Live-Kachel](/Help/live-and-playback#live-tiles) oder über die [API](/Help/api#recording). Sie bleibt über Neustarts und Upgrades hinweg aktiv.

Sie wirkt zusätzlich zum Modus der Kamera. Eine Kamera im Modus „Bewegung“ bewahrt Aufnahmen auf, wenn sie Bewegung sieht *oder* wenn eine manuelle Aufzeichnung läuft, und Ereignisse markieren die Zeitleiste wie gewohnt. Bei einer Kamera mit durchgehender Aufzeichnung markiert sie nur die Zeitleiste.

- Aufnahmen werden in ganzen Segmenten aufbewahrt, daher wird bis zu ein Segment vor und nach der Aufzeichnung ebenfalls gespeichert. Vor- und Nachlauf gelten nicht.
- Gespeicherte Aufnahmen folgen der normalen Aufbewahrung der Kamera. Fügen Sie eine [Beweissicherung](/Help/live-and-playback#evidence-locks) hinzu, um sie länger aufzubewahren.
- Sie erscheint auf der Zeitleiste als rotes Band. Ändern Sie die Farbe unter **Einstellungen → Ereignisse**.
- Eine Kamera, die offline ist, speichert nichts, bis sie zurück ist. Die Aufzeichnung bleibt aktiv.
- Aktualisieren Sie die Aufzeichnungsknoten zusammen mit dem Server. Ein älterer Knoten kennt keine manuellen Aufzeichnungen.

## Vorlauf und Nachlauf

In den Modi „Bewegung“ und „Ereignis“ bestimmt der **Vorlauf**, wie viel Aufnahme vor der Aktivität aufbewahrt wird (damit Sie sehen, wie jemand hereinkommt), und der **Nachlauf**, wie viel nach ihrem Ende.

## Segmentlänge

Aufnahmen werden als Dateien fester Länge geschrieben (standardmäßig 60 Sekunden). Die Wiedergabe muss einen Teil eines Segments herunterladen, bevor sie genau an dem gewählten Moment starten kann. Kürzere Segmente beschleunigen das Springen bei hochauflösenden Kameras, erzeugen aber mehr Dateien. Eine Änderung der Länge startet die Aufzeichnung der Kamera kurz neu.

## Zeitplan

Die Seite **Zeitplan** einer Kamera enthält ihre Zeitfenster: Tage, eine Startzeit und eine Endzeit. Eine Endzeit vor der Startzeit reicht über Mitternacht. Die Zeiten richten sich nach der **lokalen Uhr des Aufzeichnungsknotens**. Zeitfenster sind nur im Modus „Zeitplan“ von Bedeutung.

## Ereignis-Markierungen

Die Seite **Ereignis-Markierungen** einer Kamera macht aus ONVIF-Ereignissen der Kamera (Bewegung, Alarme, Analyse, Sabotage) farbige Markierungen auf der Zeitleiste.

- **Start-Thema**: das Ereignis, das die Markierung beginnt. Wählen Sie eines aus *Beobachtete Themen*, die alles auflisten, was die Kamera tatsächlich gesendet hat.
- **Stopp-Thema**: optional. Lassen Sie es leer, wenn das Start-Thema seinen eigenen Wahr/Falsch-Zustand mitbringt (wie die meisten Bewegungs- und Alarmthemen). Legen Sie es fest, wenn die Kamera getrennte Start- und Stoppereignisse sendet.
- **Steuert die Aufzeichnung**: In den Modi „Ereignis“ oder „Bewegung“ werden Aufnahmen rund um diese Markierung aufbewahrt.

Eine Markierung endet erst, wenn ihr Stoppereignis eintrifft, nie durch eine Zeitüberschreitung.
