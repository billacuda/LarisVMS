# Knoten

Aufzeichnungsknoten: Status, Streaming-Pfade, Medien-Proxys, Failover, Wartung und Aktualisierungen.

**Knoten** in der Seitenleiste listet jeden Knoten mit Status, Adresse, Version, Kameraanzahl und Speicherbelegung auf. Klicken Sie auf einen Knoten, um seine Einstellungen zu bearbeiten. Das Menü **⋯** enthält diese Aktionen:

- **Dienst neu starten**: startet den Windows-Dienst des Knotens neu, nicht den Rechner. Die Aufzeichnung pausiert für einige Sekunden.
- **Authentifizierung zurücksetzen**: löscht das Check-in-Token des Knotens, für einen Knoten, der nach einem verpassten Heartbeat dauerhaft an der Authentifizierung scheitert. Auf einen funktionierenden Knoten hat es keine Wirkung.
- **Wartung**: verschiebt die Kameras des Knotens auf seinen Ersatzknoten, bis Sie die Wartung beenden.
- **Löschen**: entfernt den Knoten. Seine Kameras werden nicht mehr zugewiesen. Aufnahmen werden nicht gelöscht.

Ein Knoten ist **online**, wenn er sich in den letzten 2 Minuten gemeldet hat. Eine Warnung ⚠️ **Uhr** bedeutet, dass die Uhr des Knotens um mehr als eine Minute von der des Servers abweicht, prüfen Sie also seine Zeitsynchronisierung.

## Direktes Streaming

Standardmäßig wird alles Live- und Wiedergabevideo über den Webserver geleitet (**Proxy**). Bei **Direkt** verbinden sich Browser direkt mit dem eigenen HTTPS-Endpunkt jedes Knotens, was den Server entlastet. Legen Sie den Modus unter **Einstellungen → Live-Ansicht** fest und überschreiben Sie ihn pro Knoten.

Der Modus „Direkt“ benötigt einen **Client-Endpunkt-Host**, den Browser erreichen können, und ein Zertifikat (ein `.pfx`-Pfad auf der Seite des Knotens). **Selbstsigniert zulassen** dient nur zum Testen, da Betrachter eine Browserwarnung erhalten. Eine Kamera, deren Knoten keinen funktionierenden Endpunkt hat, fällt automatisch auf den Proxy zurück.

## Verschlüsselte Verbindungen vom Server

Der Server und die Medien-Proxys erreichen die Video- und Steuerungsrouten jedes Knotens über unverschlüsseltes HTTP auf seinem Live-Port (8554), was in einem vertrauenswürdigen Netzwerk in Ordnung ist. Um diesen Verkehr zu verschlüsseln, aktivieren Sie auf der Seite des Knotens **Verbindungen vom Server verschlüsseln** und starten Sie den Knoten dann einmal neu (**Neu starten**). Der Knoten startet seinen HTTPS-Endpunkt (mit einem eigenen selbstsignierten Zertifikat, wenn er keine `.pfx` hat) und meldet das Zertifikat, das der Server dann festhält: Server und Proxys akzeptieren vom Knoten nur dieses Zertifikat, und das Medien-Token ist nicht mehr Teil der Adresse. Sobald verschlüsselt wird, antwortet der unverschlüsselte Port des Knotens nur noch auf `/health`.

Die Spalte **Verschlüsselt** auf der Seite „Knoten“ zeigt 🔒 für jeden verschlüsselten Knoten und ⏳ für einen, bei dem es aktiviert ist, der aber noch einen Neustart braucht. Der Status unter dem Schalter zeigt das verwendete Zertifikat oder dass der Server noch darauf wartet (bis dahin verwendet der Server weiter unverschlüsseltes HTTP, sodass nichts ausfällt). Standardmäßig ist es aus. Dafür verwendete selbstsignierte Zertifikate werden Browsern nie angeboten; direktes Streaming benötigt weiterhin ein echtes.

## Proxys

Ein **Medien-Proxy** ist ein eigenes Relay (`LarisVMS.Proxy`) zwischen Browsern und Knoten, für entfernte Standorte oder Verbindungen mit geringer Bandbreite. Registrieren Sie Proxys unter **Einstellungen → Medien-Proxys** und wählen Sie dann auf der Seite jedes Knotens einen primären und einen Ersatz-Proxy. Browser verwenden den ersten funktionsfähigen.

## Failover

Weisen Sie einem Knoten einen **Ersatz-Aufzeichnungsknoten** zu, und wenn er ausfällt, zeichnet der Ersatzknoten seine Kameras auf, bis er zurück ist. „Ausgefallen“ wird durch eine Abstimmung zwischen dem Partner des Knotens, dem Server und seinem Proxy entschieden. **Wartung** verschiebt seine Kameras absichtlich und hält sie dort.

Die Knotenseite warnt, wenn der Ersatzknoten andere KI-Hardware hat oder nur aufzeichnet. In diesen Fällen kann die Erkennung während eines Failovers langsamer sein oder pausieren.

## Aktualisierungen

Knoten und Proxys aktualisieren sich selbst aus freigegebenen Builds (siehe [Installation](/Help/installation#updates)). **Knoten → Aktualisierungen und Registrierung → Automatische Aktualisierung** schaltet dies für alle Knoten ein oder aus.

Der Abschnitt **Builds** auf der Seite „Knoten“ zeigt den Build, auf den jede Art von Knoten zusteuert, und jeden Build, der auf Freigabe wartet, mit **Freigeben** und **Ablehnen**. **Build-Verlauf** listet jeden Build auf, auch freigegebene, ersetzte und abgelehnte.

## Serverzertifikat

**Knoten → Aktualisierungen und Registrierung → Prüfung des Serverzertifikats** bestimmt, wie Knoten und Proxys das HTTPS-Zertifikat dieses Servers prüfen:

- **Beim ersten Kontakt vertrauen** (der Standard): Ein Zertifikat einer vertrauenswürdigen Zertifizierungsstelle funktioniert immer, auch erneuerte und ersetzte. Ein selbstsigniertes Zertifikat wird beim ersten Verbinden eines Knotens gespeichert und funktioniert weiter, solange es gleich bleibt, selbst über sein Ablaufdatum hinaus. Ändert es sich, lehnen Knoten und Proxys es ab, bis sie neu registriert werden.
- **Nur vertrauenswürdiges Zertifikat**: nur ein Zertifikat einer vertrauenswürdigen Zertifizierungsstelle, das zum Namen des Servers passt. Installieren Sie eines, bevor Sie dies wählen.
- **Nicht prüfen**: jedes Zertifikat. Nicht sicher: Jeder im Netzwerkpfad könnte sich als Server ausgeben.

Bei „Beim ersten Kontakt vertrauen“ vertraut ein Knoten dem Zertifikat, das er bei der ersten Verbindung sieht. Damit er schon ab der allerersten Verbindung prüft, übergeben Sie dem Installationsprogramm den unter **Prüfung des Serverzertifikats** angezeigten Fingerabdruck: `-ServerCertThumbprint` für `install-node.ps1` und `install-proxy.ps1` oder `SERVERCERTTHUMBPRINT=` für das MSI. Die Installationsbefehle auf den Seiten „Einrichtung“ und „Medien-Proxys“ enthalten ihn bereits. Hinter einem Reverse-Proxy verwenden Sie stattdessen den Fingerabdruck des Zertifikats, das der Reverse-Proxy vorlegt.

Das selbstsignierte Zertifikat, das der Server für sich erstellt, ist 10 Jahre gültig und wird nicht von selbst ersetzt. **Selbstsigniertes Zertifikat neu erstellen** ersetzt es; jeder Knoten und Proxy muss dann neu registriert werden, und die Seite „Knoten“ listet diejenigen auf, die sich seitdem nicht gemeldet haben. Um einen Knoten neu zu registrieren, führen Sie das Installationsprogramm für Knoten erneut aus oder führen Sie `LarisVMS.Node.exe --reset-server-pin` darauf aus und starten Sie seinen Dienst neu (Proxys haben dieselbe Option).

## Einmal-Registrierungscodes

Neue Knoten und Proxys registrieren sich mit dem gemeinsamen **Registrierungsschlüssel** oder mit einem Einmal-Code. Ein Einmal-Code registriert einen einzelnen Knoten oder Proxy und funktioniert danach nicht mehr. Ungenutzt läuft er nach 1 Stunde, 24 Stunden oder 7 Tagen ab. Um einen zu erstellen, gehen Sie zu **Knoten → Aktualisierungen und Registrierung**, wählen Sie die Gültigkeitsdauer und klicken Sie auf **Code erstellen**. Der Code wird einmal angezeigt. Verwenden Sie ihn überall dort, wo das Installationsprogramm nach dem Registrierungsschlüssel fragt. **Widerrufen** annulliert einen Code, der noch nicht verwendet wurde.

Der gemeinsame Schlüssel funktioniert für jede Installation, bis er geändert wird. Sobald Sie Einmal-Codes verwenden, entfernen Sie das Häkchen bei **Den gemeinsamen Registrierungsschlüssel akzeptieren**, damit eine Kopie des Schlüssels in einem alten Skript oder einer alten E-Mail nichts registrieren kann. Bereits registrierte Knoten und Proxys sind nicht betroffen. In beiden Fällen wartet ein neuer Knoten weiterhin auf Freigabe, bevor er Kameras erhält.
