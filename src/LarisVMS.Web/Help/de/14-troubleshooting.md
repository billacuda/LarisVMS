# Fehlerbehebung

Häufige Probleme und wo Sie nachsehen.

## Wo die Protokolle liegen

- **Server**: **Protokolle → Systemprotokolle**. Legen Sie Stufe und Aufbewahrung unter **Einstellungen → Protokolle** fest. Debug und Trace liefern mehr Details, erzeugen aber große Dateien.
- **Kameraereignis-Protokoll**: die Rohereignisse, die Kameras senden, die nur verwendet werden, um die Ereignisthemen einer Kamera für Ereignis-Markierungen aufzulisten. Sie werden standardmäßig 30 Tage aufbewahrt (**Einstellungen → Protokolle**, 0 bewahrt sie für immer auf); ihr Löschen berührt weder Ereignisse noch Schnappschüsse oder Aufnahmen.
- **Datenbankpflege**: Jede Nacht gegen 3 Uhr räumt der Server die Indizes und Statistiken der Datenbank auf und protokolliert eine Zeile darüber, was er getan hat. Darf sich seine Datenbankanmeldung dafür nicht verwenden lassen, protokolliert er stattdessen eine Warnung. Bei MariaDB entfällt dies; MariaDB pflegt seine eigenen Statistiken.
- **Knoten**: `%ProgramData%\LarisVMS\logs\node-*.log` auf jedem Knoten.
- **KI-Erkennung**: `%ProgramData%\LarisVMS\logs\vision-*.log` auf jedem Knoten.
- **Automatische Aktualisierung**: `%ProgramData%\LarisVMS\logs\updater-*.log` auf jedem Knoten oder Proxy.

## Eine Kamera zeichnet nicht auf

1. Ist sie **einem Knoten zugewiesen**, und ist dieser Knoten **online**?
2. Hat der Knoten einen **Speicherpfad**? Die Knotenliste weist auf Knoten ohne einen hin.
3. Ist die Kamera **aktiviert**, und sehen ihre Streams richtig aus? **Neu abfragen** nach jeder Änderung an der Kamera selbst.
4. Prüfen Sie in den Modi „Bewegung“, „Zeitplan“ oder „Ereignis“, ob Aufnahmen verworfen werden (siehe [Aufzeichnung](/Help/recording#modes)).

## Live-Ansicht und Wiedergabe bleiben bei „Verbindung wird hergestellt“

Wenn ein Knoten direkt an Browser streamt (siehe [Direktes Streaming](/Help/nodes#direct-streaming)), verbindet sich der Browser mit dem Client-Port des Knotens, standardmäßig 4200. Zeigt die Konsole des Browsers eine fehlgeschlagene Verbindung `wss://<node>:<port>/live/…`, prüfen Sie, ob die Firewall des Knotens diesen Port zulässt: Das Installationsprogramm erstellt eine eingehende Regel namens **LarisVMS Node Client Endpoint**. Installationsprogramme vor 0.215.1 konnten diese Regel beim Upgrade oder bei der Übernahme eines Knotens verlieren; führen Sie das aktuelle Installationsprogramm für Knoten erneut aus oder fügen Sie die Regel von Hand hinzu.

## Active-Directory-Anmeldung oder -Synchronisierung schlägt fehl

Verwenden Sie **Verbindung testen** unter **Einstellungen → Active Directory**; der Fehler der letzten Synchronisierung wird auf derselben Seite angezeigt.

- **Kein Domänencontroller erreichbar:** Prüfen Sie, ob der Domänenname vom Server aus aufgelöst wird und ob Port 636 (LDAPS) oder 389 (LDAP) zu einem Domänencontroller offen ist. Bei LDAPS braucht der Domänencontroller ein Zertifikat, dem dieser Server vertraut.
- **Die eigene Identität des Servers wurde abgelehnt:** Integrierte Sicherheit funktioniert nur, wenn der Dienst LarisVMS Web auf einem domänenverbundenen Server als LocalSystem oder Domänenkonto läuft. Verwenden Sie andernfalls ein Dienstkonto.
- **„Gehört keiner Gruppe mit Zugriff an“:** Der Benutzer hat sich korrekt angemeldet, gehört aber keiner verknüpften Gruppe an. Gruppen werden als Sicherheitsgruppen abgeglichen, auch verschachtelte.
- **Nach dem Deaktivieren der lokalen Anmeldung von den Administrationsseiten ausgesperrt:** Siehe [Active Directory](/Help/users#active-directory) für den Datenbankbefehl, der sie wieder aktiviert.

## Die Live-Ansicht pausiert in einem Hintergrundfenster

Chrome drosselt Fenster, die nicht sichtbar sind, sodass eine Live-Wand auf einem zweiten Monitor einfrieren und dann aufholen kann. Die Live-Ansicht erholt sich von selbst. Um das zu verhindern, fügen Sie dem Ziel der Chrome-Verknüpfung Folgendes hinzu:

```
--disable-backgrounding-occluded-windows --disable-background-timer-throttling
```

## Alle Kacheln frieren ein und verbinden sich dann gleichzeitig neu

Der Knoten hat gestockt, nicht der Browser oder das Netzwerk. Suchen Sie im Knotenprotokoll nach `Node process health: tick … ms late`. Ein funktionsfähiger Knoten protokolliert das etwa einmal pro Minute mit einer Verspätung im einstelligen Millisekundenbereich.

- Liegt `gcPauseMs` nahe an der Verspätung, ist die Ursache die Speicherbereinigung.
- Ist `gcPauseMs` klein, aber `pendingWorkItems` liegt im zweistelligen Bereich, sind Threads blockiert. Erstellen Sie einen Dump, während es stockt: `dotnet-dump collect -p <LarisVMS.Node pid>`.

## Die KI-Erkennung stoppt, aber die Aufzeichnung läuft weiter

Meist wurde der GPU-Treiber zurückgesetzt. Das Windows-Systemereignisprotokoll zeigt in diesem Moment `nvlddmkm` Ereignis 153 oder `Display` Ereignis 4101. Der Vision-Dienst startet sich nach 60 Sekunden ohne Ergebnis selbst neu. Wiederholte Zurücksetzungen deuten auf die GPU, ihren Treiber, die Kühlung oder die Stromversorgung hin.

## Ein Knoten oder Proxy kam nach einem Update nicht zurück

Starten Sie seinen Dienst (`sc start LarisVMSNode` oder `LarisVMSProxy`) und prüfen Sie dann `updater-*.log`, warum der Neustart fehlgeschlagen ist. Ein heruntergeladenes Update, das nie angewendet wurde, wird außerdem im eigenen Protokoll des Knotens oder Proxys vermerkt, wenn er das nächste Mal startet, und bei einem späteren Check-in erneut angeboten.

## Ein Knoten zeigt „Uhr“

Die Uhr des Knotens weicht um mehr als eine Minute von der des Servers ab. Beheben Sie seine Zeitsynchronisierung. Eine Uhrabweichung beeinträchtigt Zeitpläne und die Zeitleiste.

## Ein Knoten meldet sich dauerhaft nicht

Verwenden Sie **Authentifizierung zurücksetzen** im Menü **⋯** des Knotens. Es löscht das Check-in-Token des Knotens, und der Knoten synchronisiert sich beim nächsten Heartbeat neu.
