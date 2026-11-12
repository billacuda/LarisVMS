# Speicher und Aufbewahrung

Wo Aufnahmen liegen, wie lange sie aufbewahrt werden, die Schwelle und das Archiv-Volume.

## Speicherpfade

Jeder Knoten zeichnet auf seinen eigenen **Speicherpfad** auf, der auf der Bearbeitungsseite des Knotens oder bei der Installation festgelegt wird. Es gibt keinen globalen Pfad. Ein Knoten ohne Speicherpfad zeichnet nichts auf, und die Knotenliste weist darauf hin.

## Aufbewahrung

Die **Aufbewahrung** gibt an, wie viele Tage Aufnahmen aufbewahrt werden. Sie wird global unter **Einstellungen → Speicher und Aufbewahrung** festgelegt und kann pro Knoten und pro Kamera überschrieben werden; der spezifischste Wert gewinnt. 0 bewahrt Aufnahmen für immer auf.

Eine Kamera kann außerdem ein **Speicherkontingent** in GB haben. Wird es überschritten, werden zuerst ihre ältesten Aufnahmen entfernt.

## Schwelle

Die **Schwelle** ist eine harte Grenze dafür, wie voll die Festplatte eines Knotens werden darf (standardmäßig 90 %). Darüber archiviert der Knoten seine ältesten Aufnahmen vorzeitig oder löscht sie, falls keine Archivierung verfügbar ist, bis die Festplatte wieder unter der Grenze liegt. Die Knotenliste zeigt dabei **Speicher voll** an.

## Archiv

Ein Knoten kann einen optionalen zweiten **Archivpfad** haben, etwa eine SMB-Freigabe oder ein USB-Laufwerk. Er muss sich vom Speicherpfad unterscheiden. Ist die Archivierung aktiviert, werden Aufnahmen, die aus der Aufbewahrungsfrist laufen, ins Archiv **verschoben**, statt gelöscht zu werden, und dort aufbewahrt, bis die **Archiv-Aufbewahrung** erreicht ist. Die Archiv-Aufbewahrung wird ab dem ursprünglichen Aufnahmedatum gezählt, und 0 bewahrt für immer auf.

Wiedergabe, Vorschaubilder und Exporte funktionieren von beiden Volumes gleich. Die Knotenliste zeigt freien Speicher und eine Schätzung der verbleibenden Tage für beide Volumes. Die Schätzung beruht auf den letzten 24 Stunden Schreibvorgang.

## Verbliebene Aufnahmen

Wenn eine Kamera auf einen anderen Knoten wechselt oder entfernt wird, bleiben ihre alten Aufnahmen auf dem früheren Knoten, bis sie ablaufen. Die Zahl mit ⚠️ neben dem Status eines Knotens zeigt, wie viele Kameras dort noch Aufnahmen haben. Sie verschwindet von selbst.

## Verschlüsselung

Aufnahmen können auf den Festplatten der Aufzeichnungsknoten verschlüsselt werden, sodass eine gestohlene oder entsorgte Festplatte nichts preisgibt. Standardmäßig ist sie aus.

**Einrichten** unter **Einstellungen → Verschlüsselung und Signierung**:

1. **Hauptschlüssel erstellen.** Er liegt auf diesem Server und entsperrt jede verschlüsselte Aufnahme.
2. **Eine Sicherung** des Hauptschlüssels **herunterladen**, geschützt durch eine Passphrase Ihrer Wahl. Ohne eine solche kann die Verschlüsselung nicht aktiviert werden. Bewahren Sie die Datei und die Passphrase an einem anderen Ort als diesem Server auf: Wird der Server neu aufgesetzt, ist **Wiederherstellen** mit der Sicherung der einzige Weg, verschlüsselte Aufnahmen wieder zu lesen. Gehen beide verloren, sind die Aufnahmen endgültig verloren.
3. **Wählen, was verschlüsselt wird:** **Video** (Aufnahmen und Gegensprech-Audio) oder **Video, Schnappschüsse und Vorschaubilder**. Das ist der Standard für jede Kamera; jede Kamera kann ihn auf ihrer eigenen Seite überschreiben (**Kameras → Bearbeiten**, Speicher).

Jedes Segment wird auf dem Aufzeichnungsknoten kurz nach Ende der Aufnahme verschlüsselt, sodass nur das gerade aufgezeichnete Segment unverschlüsselt auf der Festplatte liegt. Aufnahmen, die vor dem Aktivieren der Verschlüsselung entstanden, bleiben unverändert; **bestehende Aufnahmen verschlüsseln** auf der Seite der Kamera wandelt sie im Hintergrund um. Wird die Verschlüsselung später deaktiviert, bleiben verschlüsselte Aufnahmen lesbar.

Wiedergabe, Vorschaubilder, Schnappschüsse und Exporte funktionieren wie bisher. Exportierte Dateien sind nicht verschlüsselt, da sie zur Weitergabe gedacht sind. Der Aufzeichnungsknoten holt sich den Tagesschlüssel jeder Kamera bei Bedarf von diesem Server und hält ihn nur im Arbeitsspeicher, sodass eine verschlüsselte Aufnahme nicht abgespielt werden kann, solange der Aufzeichnungsknoten den Server nicht erreicht.
