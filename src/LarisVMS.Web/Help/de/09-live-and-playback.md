# Live-Ansicht und Wiedergabe

Ansichten, Live-Kacheln, die Wiedergabe-Zeitleiste, Exporte, Lesezeichen, Beweissicherungen, Schnappschüsse, Aktivität und die Karte.

## Ansichten

Eine **Ansicht** ist ein gespeichertes Kamera-Layout. Erstellen Sie Ansichten unter **Ansichten**, indem Sie Kacheln ziehen und in der Größe ändern. **Live** und **Wiedergabe** zeigen beide eine Ansicht. Auf Telefonen klappt das Layout auf eine oder zwei Spalten zusammen. Ansichten können im Kioskmodus im Vollbild laufen oder per Zeitgeber zwischen mehreren Ansichten wechseln. Eine geteilte Ansicht kann von allen genutzt werden, die Ansichten sehen, aber nur ihr Besitzer oder ein Administrator kann sie ändern oder löschen.

## Live-Kacheln

- Doppelklicken Sie auf eine Kachel für den Vollbildmodus. Im Vollbild zoomen Sie durch Scrollen oder Zusammenziehen der Finger und verschieben mit Ziehen.
- Die Steuerelemente einer Kachel erscheinen, wenn Sie mit der Maus darüberfahren oder mit der Tabulatortaste dorthin wechseln. Siehe [Barrierefreiheit](/Help/accessibility).
- Jede Kachel mit Audio hat ihre eigene Stummschaltung und Lautstärkeregelung. Kacheln starten immer stummgeschaltet.
- **Adaptives Streaming** (unter **Einstellungen → Live-Ansicht**) zeigt kleinen Kacheln den Nebenstream und wechselt auf den Hauptstream, wenn die Kachel groß ist oder im Vollbild. Jede Kachel hat außerdem eine manuelle Wahl zwischen Auto/HD/SD.
- Abzeichen zeigen Bewegung und erkannte Objekte. Unter **Einstellungen → Ereignisse** legen Sie Farben und Ecke fest.
- Die KI-Schalter der Symbolleiste zeichnen einen Rahmen um jedes erkannte Objekt: **Bewegt**, **Ruhend** oder beides. **Konfidenz** fügt die Bewertung jedes Rahmens hinzu, und **Geschwindigkeit** und **Farbe** (standardmäßig aktiv) fügen seine Geschwindigkeit, sobald eine Geschwindigkeitsmessung ihn erfasst hat, und seine Farbe hinzu. **Weg** (mit aktiviertem „Bewegt“) zeichnet in der Farbe des Rahmens die Linie, die jedes bewegte Objekt von der Mitte seines Rahmens aus genommen hat. Die Linie bleibt, solange sich das Objekt weiter bewegt. Sobald es anhält oder verschwindet, bleibt die Linie für die Zeit **Verweildauer des Weges** (**Einstellungen → Live-Ansicht**, standardmäßig 5 Sekunden) und löst sich dann von ihrem Anfang her innerhalb der Zeit **Verblassen des Weges** (dieselbe Seite, standardmäßig 3 Sekunden) auf.
- PTZ-Kameras erhalten ein Steuerkreuz und Zoom.
- 🔴 unten links bedeutet, dass die Aufnahmen der Kamera gerade aus irgendeinem Grund aufbewahrt werden. Kameras mit durchgehender Aufzeichnung zeigen es ständig. Es kann bis zu 20 Sekunden dauern, bis es sich nach Bewegung oder einem Ereignis ändert.
- Ⓜ️ daneben bedeutet, dass eine [manuelle Aufzeichnung](/Help/recording#manual-recording) läuft. Ⓜ️ allein bedeutet, dass die Aufzeichnung läuft, die Kamera aber offline ist und nichts gespeichert wird.
- Die Schaltfläche ⏺ startet eine manuelle Aufzeichnung, und ⏹ stoppt sie. In einer Ansicht mit weiteren Kameras fragt sie, ob Sie diese Kamera oder alle meinen. Jeder mit Berechtigung kann eine Aufzeichnung stoppen, wer auch immer sie gestartet hat.

## Über eine Kamera sprechen

Kameras mit Lautsprecher zeigen auf ihrer Live-Kachel eine Schaltfläche 🎙️. Halten Sie sie gedrückt (oder fokussieren Sie sie und halten Sie die Leertaste), um zu sprechen, und lassen Sie los, um aufzuhören. **▾** daneben wählt das Mikrofon: ein Headset, eine Webcam oder jeden anderen Eingang, den der Browser nutzen kann.

- Ihr Browser fragt beim ersten Mal nach dem Mikrofonzugriff. Er erlaubt dies nur, wenn die Website über **HTTPS** geöffnet wird; siehe [Zertifikate](/Help/security#certificates).
- Es kann immer nur eine Person über eine Kamera sprechen.
- Es benötigt die Berechtigung **Sprechen** auf der Kamera (**Einstellungen → Kamerazugriff**). Die Kamera muss ONVIF-Gegensprechen unterstützen (einen Audioausgang in ihrem ONVIF-Profil). Kameras von Dahua, Amcrest, Lorex und EmpireTech, deren Firmware das nicht bietet, greifen auf die eigene Audio-API der Kamera zurück.
- Alles Gesagte wird aufgezeichnet. Das eigene Mikrofon der Kamera ist wie gewohnt Teil der Videoaufzeichnung, und jeder Tastendruck wird neben den Aufnahmen gespeichert. Tastendrücke erscheinen auf der Wiedergabe-Zeitleiste in Blaugrün und werden synchron zum Video mit der Lautstärke der Kachel abgespielt. Exporte enthalten sie, gemischt mit dem eigenen Audio der Kamera. Sie werden so lange aufbewahrt wie die Aufnahmen der Kamera.
- Jede Sitzung wird in das Audit-Protokoll geschrieben.

## Zeitleiste

Die Wiedergabe hat zwei Zeitleisten: eine für die ausgewählte Kamera und eine kombinierte über alle Kameras. Beide sind nach Bewegung, Erkennungen, Ereignis-Markierungen und Gegensprechen eingefärbt.

- Scrollen zum Zoomen (von Wochen bis auf Sekunden), Ziehen zum Verschieben und Klicken, damit jede Kachel zu diesem Moment springt.
- Klicken Sie auf die Zeit über der Zeitleiste, um eine genaue Zeit einzugeben.
- Die Pfeiltasten springen im pausierten Zustand eine Sekunde; Umschalt+Pfeil zehn.
- Geschwindigkeiten bis 8× laufen flüssig. Schnellere springen wie eine Diashow durch die Aufnahmen.
- Fahren Sie mit der Maus über die Zeitleiste für ein Vorschaubild.
- Abschnitte mit Bewegung oder einem Ereignis sind gestreift, sodass sie sich nicht allein auf Farbe stützen.
- Die Schaltflächen − und + neben der Zeit zoomen ebenfalls, und die Zeitleiste lässt sich mit der Tastatur bedienen. Siehe [Barrierefreiheit](/Help/accessibility).

Ihre Position und Ihr Zoom bleiben zwischen den Besuchen erhalten.

## Exporte

**Exportieren…** in der Wiedergabe erstellt für jede ausgewählte Kamera über einen Zeitraum eine Videodatei. Exporte laufen im Hintergrund und erscheinen auf der Seite **Exporte**, wo sie heruntergeladen, wiederholt oder gelöscht werden können. Ein Export umfasst bis zu 64 Kameras, 31 Tage und insgesamt 1.000 Kamerastunden (4 Kameras für eine Woche sind 672); für mehr erstellen Sie mehrere. Fertige Exporte werden 7 Tage lang aufbewahrt und dann samt ihren Dateien entfernt.

- **✏ Schwärzen** neben einer Kamera schwärzt Bereiche nur im Export dieser Kamera, etwa das Fenster eines Nachbarn oder einen Passanten. Zeichnen Sie Rechtecke (Ziehen) oder Polygone (jede Ecke anklicken; auf den ersten Punkt klicken, doppelklicken oder die Eingabetaste drücken, um abzuschließen; Rechtsklick entfernt den letzten Punkt) über ein Bild von der Startzeit des Exports. Die Aufnahmen selbst bleiben unverändert.
- Ein geschwärzter Export wird neu kodiert und dauert daher länger als ein einfacher. Er ist auf der Seite „Exporte“ als **Geschwärzt** markiert, und das Audit-Protokoll hält fest, dass Bereiche geschwärzt wurden.
- Die [Privatmasken](/Help/motion) einer Kamera sind bereits Teil ihrer Aufnahmen, daher enthält jeder Export sie.
- Jedes Gegensprech-Audio, das im Zeitraum über die Kamera gesprochen wurde, wird in das Audio des Exports gemischt.

### Einen Export prüfen

Jedes aufgezeichnete Segment erhält beim Schreiben einen Fingerabdruck (SHA-256), und die Segmente jeder Kamera werden zu einer Kette verknüpft: Jedes Glied umfasst den Fingerabdruck seines Segments und das vorherige Glied, sodass das Ändern, Entfernen oder Umordnen aufgezeichneter Aufnahmen die Kette bricht. Einmal pro Stunde signiert der Server, wie weit die Kette jeder Kamera gekommen ist.

Jeder fertige Export erhält außerdem ein signiertes **🔏 Manifest** auf der Seite „Exporte“. Es hält den Fingerabdruck der exportierten Datei fest, die Quellaufnahmen und ihren Platz in der Kette sowie ob jede Quelldatei beim Export noch mit ihrem Fingerabdruck übereinstimmte.

Um einen Export zu prüfen, öffnen Sie **Exporte → Export prüfen** und wählen Sie das Manifest und das Video. Die Prüfung läuft in Ihrem Browser, und nichts wird hochgeladen. Sie bestätigt:

- dass das Manifest von diesem Server signiert und nicht bearbeitet wurde;
- dass das Video genau die exportierte Datei ist;
- dass die Quellaufnahmen seit der Aufzeichnung unverändert waren und sich zu einem signierten Prüfpunkt verketten.

Bewahren Sie das Manifest beim Weitergeben eines Exports zusammen mit dem Video auf. Jeder kann die Signatur ohne LarisVMS prüfen: Speichern Sie den `publicKeyPem` des Manifests als `key.pem`, seinen Text `signedPayload` als `payload.txt` und sein Base64-dekodiertes `signatureDer` als `sig.der` und führen Sie dann `openssl dgst -sha256 -verify key.pem -signature sig.der payload.txt` aus. Aufnahmen, die vor dieser Funktion aufgezeichnet wurden, haben keine Fingerabdrücke, daher kann nur der Export selbst geprüft werden.

Die Signierung ist immer aktiv. Der Signaturschlüssel des Servers, mit Download seines öffentlichen Schlüssels, befindet sich unter **Einstellungen → Verschlüsselung und Signierung**.

## Lesezeichen

Markieren Sie während der Wiedergabe einen Moment mit einer Notiz. **Lesezeichen** listet alle auf, mit einem Link zurück zum genauen Moment. Lesezeichen werden mit allen geteilt, die die Wiedergabe sehen dürfen, und laufen ab, wenn ihre Aufnahmen ablaufen.

## Beweissicherungen

Eine Beweissicherung bewahrt Aufnahmen davor, durch die Aufbewahrungsregeln gelöscht zu werden. Klicken Sie in der Wiedergabe auf **🔒 Sichern…**, wählen Sie die Kameras sowie Anfang und Ende, bestimmen Sie, wie lange sie aufbewahrt wird (1, 3, 7, 14 oder 30 Tage, bis zu einem Datum oder unbefristet), und geben Sie einen Grund an. Gesicherte Abschnitte erscheinen als violettes Band am oberen Rand der Zeitleiste. Eine Sicherung umfasst bis zu 7 Tage Aufnahmen.

- **Gesicherte Aufnahmen werden nie gelöscht**, weder durch Aufbewahrung, Kontingente oder Archivablauf, noch bei voller Festplatte. Füllt sich ein Aufzeichnungsknoten mit gesicherten Aufnahmen, zeigt sein Eintrag unter „Knoten“ **voll mit gesicherten Aufnahmen**, und die Aufzeichnung stoppt, sobald die Festplatte voll ist. Beenden oder entfernen Sie Sicherungen, exportieren Sie die Aufnahmen oder fügen Sie Speicher hinzu.
- **Lange Sicherungen**: Alles über 30 Tage zeigt eine Warnung. Bei knappem Speicher ist es besser, das Video zu exportieren (**Stattdessen diesen Zeitraum exportieren**) und woanders aufzubewahren.
- **Eine Sicherung entfernen**: **Beweissicherungen** listet jede Sicherung mit Größe und Ablauf auf. Die Person, die eine Sicherung angelegt hat, oder ein Super-Admin kann sie mit einem Grund entfernen. Die Aufnahmen bleiben weitere **24 Stunden** geschützt, falls das Entfernen ein Versehen war, und **Entfernen rückgängig machen** stellt die Sicherung bis dahin wieder her.
- **Protokolliert**: Anlegen, Entfernen, Wiederherstellen und Ablaufen einer Sicherung werden alle in das Audit-Protokoll geschrieben.

Eine Kamera mit Aufnahmen, die noch unter einer Sicherung stehen, kann nicht gelöscht werden. Das Anlegen von Sicherungen erfordert die Berechtigung **Aufnahmen sichern**, die Rollen, die exportieren dürfen, standardmäßig haben. [Benachrichtigungen](/Help/alerts#notifications) können Sie informieren, wenn eine Sicherung bald endet oder entfernt wurde.

## Schnappschüsse

**Schnappschüsse** listet jede Erkennung und Ereignis-Markierung als Vorschaubild auf, mit Filtern für Kamera, Gruppe, Ansicht, Objektart und Zeit. Einfache Bewegung erscheint nur auf der Zeitleiste. ▶ öffnet den Moment in der Wiedergabe.

## Aktivität

**Aktivität** zeigt, wann auf einer Kamera oder auf allen Kameras zusammen etwas passiert, als Heatmap nach Tageszeit. Wählen Sie die Kamera (oder **Alle Kameras**) und einen Zeitraum: 24 Stunden, 7, 30 oder 90 Tage oder ein Jahr.

- **Typische Woche** (der Standard): Zeilen sind Wochentage und Spalten Stunden, über den Zeitraum summiert, sodass eine dunkle Zelle eine regelmäßig belebte Zeit ist, etwa samstags 18–19 Uhr. Über mehr als eine Woche gibt das Darüberfahren mit der Maus auch den Durchschnitt pro Tag an (zum Beispiel „durchschnittlich 8,8 pro Samstag“).
- **Nach Tag** (ab 30 Tagen): Jedes Datum läuft von links nach rechts, die Stunden von oben nach unten, mit einem Streifen der Tagessummen darunter, sodass Sie bestimmte Tage herausgreifen können.
- Der aktivste Tag des Zeitraums und die aktivste Stunde werden über dem Diagramm genannt.
- Für die letzten 24 Stunden gibt es eine Zeile für jeden Ereignistyp.
- **Ereignistypen** beschränkt auf das, was Sie interessiert: einen ganzen Typ (Tier) oder nur einige seiner Bezeichnungen (Tier → Katze). Einfache Bewegung ist standardmäßig aus, damit sie die Objekte nicht übertönt. Ihre Auswahl bleibt gespeichert und gilt auch beim Wechsel der Kamera.
- Fahren Sie mit der Maus über eine Zelle für ihre genaue Anzahl, oder aktivieren Sie **Als Tabelle anzeigen** für die Zahlen.
- Zeiten gelten in der Zeitzone der Kamera (auf der Kamera festgelegt) oder in der des Servers, falls sie keine hat. „Alle Kameras“ verwendet die Zeitzone des Servers.
- Der Aktivitätsverlauf wird 400 Tage lang aufbewahrt (Einstellung `Activity.HistoryDays`), auch nachdem die Aufnahmen selbst gelöscht wurden, sodass die längeren Zeiträume die gesamte Spanne zeigen. Der Verlauf baut sich ab der Installation auf: Ereignisse, die zusammen mit ihren Aufnahmen bereits gelöscht wurden, lassen sich nicht wiederherstellen.
- Die Ansicht steht in der Seitenadresse, sodass Sie sie als Lesezeichen speichern oder teilen können. Das Dashboard-Widget **Kameraaktivität** zeigt die letzten 7 Tage für eine Kamera oder alle.

### Szene

**Szene** zeigt, *wo* im Bild einer Kamera sich etwas bewegt: eine Wärmeschicht über einem aktuellen Bild in voller Größe aus den Aufnahmen der Kamera. Rot ist, wo am meisten passiert, dann Orange, Gelb, Grün und Blau für sehr wenig. Stellen ohne Aktivität bleiben klar.

- **Bewegte Objekte** zählt, wie viele Objekte durch jede Stelle gekommen sind. Beschränken Sie auf **Personen**, **Fahrzeuge**, **Tiere** oder **Andere**.
- **Bewegung** zeigt, wo die serverseitige Bewegungserkennung der Kamera Bewegung sah, für Kameras, die sie im Modus **Raster** verwenden.
- Wählen Sie einen Zeitraum und optional eine **Tageszeit** (zum Beispiel 22 bis 6 Uhr für Nächte).
- Die Wärme wird einmal pro Stunde aufsummiert (Bewegung alle 10 Minuten) und so lange aufbewahrt wie der übrige Aktivitätsverlauf. Die zugrunde liegenden Wege werden mit ihren Ereignissen aufbewahrt und verschwinden daher mit den Aufnahmen.
- Wählen Sie eine Kamera oder eine **Ansicht** (in der Kameraliste unter **Ansichten** aufgeführt), um eine Heatmap für jede ihrer Kameras nebeneinander zu sehen. Jede Kamera hat ihre eigene Skala, sodass eine ruhige Kamera trotzdem zeigt, wo ihre Aktivität ist. Für alle Kameras gleichzeitig ist sie nicht darstellbar.
- **Hintergrund**: Wenn Sie die Kamera konfigurieren dürfen, können Sie ihr Bild mit **Eigenes Bild verwenden…** durch ein eigenes ersetzen (etwa eine klare Tagesaufnahme). Es muss von derselben Kamera mit demselben Ausschnitt stammen, in voller Breite oder in derselben Form, damit die Wärme passt. **Bild der Kamera verwenden** kehrt zum automatischen Bild zurück.
- **Heatmap zurücksetzen**: Nachdem eine Kamera versetzt oder neu ausgerichtet wurde, passt ihre alte Wärme nicht mehr zum Bild. Wenn Sie die Kamera konfigurieren dürfen, löscht **Heatmap zurücksetzen** sie und zählt nur noch, was ab dann passiert; die Karte zeigt, wann das Zählen begann.

## Karte

**Karte** (unter Live) zeigt, wo Ihre Kameras stehen und was sie ansehen. Jede Kamera ist ein Symbol mit einem Kegel für ihr Sichtfeld: die Richtung, in die sie zeigt, wie weit sie sieht und wie weit. Die Farbe zeigt ihren Zustand: Grün zeichnet auf, Rot ist offline, Grau ist deaktiviert. Klicken Sie auf eine Kamera für ihr neuestes Bild, mit Links zu Live und Wiedergabe. Status werden alle 30 Sekunden aktualisiert. Die Karte öffnet sich dort, wo Sie sie verlassen haben, getrennt für jede Person und jede Karte.

Es gibt zwei Arten von Karten, und Sie können beliebig viele von jeder haben:

- **Straßenkarte**: eine normale Karte der Umgebung (standardmäßig OpenStreetMap). Die Reichweite wird in Metern angegeben.
- **Grundriss**: ein von Ihnen hochgeladenes Bild (PNG, JPEG, WebP oder GIF, bis zu 10 MB), etwa ein Gebäudeplan oder eine Geländezeichnung. Die Reichweite wird in Pixeln dieses Bildes angegeben.

Personen, die Kameras bearbeiten dürfen, können Karten einrichten:

1. **Neue Karte → Straßenkarte** oder **Grundriss**. Eine neue Straßenkarte öffnet sich dort, wo die aktuelle gerade steht, oder mit der ganzen Welt; verschieben und zoomen Sie zu Ihrem Standort. Wenn Sie auf **Bearbeitung beenden** klicken, wird die Karte so gespeichert, dass sie sich für alle dort öffnet, wo sie gerade steht; **Verwalten → Diese Karte hier für alle öffnen** bewirkt dasselbe ohne Bearbeitung. Das funktioniert auch bei Grundrissen.
2. **Karte bearbeiten**, dann Kameras aus **Kameras, die nicht auf dieser Karte sind** auf die Karte ziehen. Oder auf eine klicken, um sie in der Mitte der Karte zu platzieren.
3. Klicken Sie auf eine Kamera, um sie auszuwählen. Ziehen Sie das Symbol, um sie zu verschieben, und ziehen Sie den weißen Griff an der Spitze ihres Kegels, um sie auszurichten (Richtung und Reichweite). Das Seitenfeld legt das Sichtfeld oder genaue Richtung und Reichweite fest, und **Von dieser Karte nehmen** entfernt sie. Änderungen werden beim Vornehmen gespeichert. Die Pfeiltasten im Feld verschieben sie ohne Ziehen.

Eine Kamera kann auf mehreren Karten sein, etwa einer Straßenkarte und dem Grundriss ihres Gebäudes. Personen sehen nur die Kameras, die sie ansehen dürfen.

Straßenkartenbilder werden von Ihrem Browser vom Kachelserver geladen, daher benötigt eine Straßenkarte Internetzugang vom Browser aus. Legen Sie für einen Standort ohne Internet oder einen anderen Kartenstil unter **Verwalten → Straßenkarten-Kacheln** Ihren eigenen https-Kachelserver und seine Quellenangabe fest (die Bedingungen der meisten Kachelserver verlangen, dass sie auf der Karte angezeigt wird). Die Änderung erfordert zusätzlich die Berechtigung zum Bearbeiten der Einstellungen, da sie für den gesamten Server gilt.
