# Sicherheit

IP-Zulassungslisten, ein eigener Port für Live/Wiedergabe, die strenge Skriptrichtlinie, Zertifikate, Hostnamen, Aktualisierungen, verschlüsselte Geheimnisse und zusätzlicher Schutz für Unternehmen.

## Zulassungslisten

**Einstellungen → Sicherheit** hat zwei IP-Zulassungslisten. Geben Sie eine IP oder einen CIDR-Block pro Zeile ein (zum Beispiel `10.0.0.5` oder `10.0.0.0/24`); Zeilen, die mit `#` beginnen, sind Kommentare. Eine leere Liste lässt jeden zu.

- **Verwaltung / API**: die Administrationsseiten und die REST-API. Eine Liste, die die Adresse blockieren würde, von der aus Sie speichern, wird abgelehnt, sodass Sie sich nicht aussperren können.
- **Live-Ansicht / Wiedergabe**: Live-Video, Wiedergabe, Vorschaubilder, Export-Downloads und Kamera-Schnappschüsse.

## Eigener Port

**Einstellungen → Live-Ansicht → Eigener Port** verlegt den Live- und Wiedergabeverkehr auf einen eigenen HTTPS-Port mit demselben Zertifikat. So können Sie ihn getrennt per Firewall absichern oder nur ihn außerhalb des LANs freigeben. Er wird nach `Restart-Service LarisVMSWeb` wirksam. Danach antwortet Video nur noch auf dem neuen Port und alles andere nur auf dem Hauptport. Leeren Sie das Feld, um zu einem Port zurückzukehren.

## HTTP/3

Der Server verwendet HTTP/2 und ergänzt HTTP/3 unter Windows 11, Windows Server 2022 oder neuer. Browser starten mit HTTP/2 und wechseln von selbst zu HTTP/3, wenn sie es erreichen können. HTTP/3 verwendet UDP auf dem HTTPS-Port (und dem eigenen Port, falls festgelegt). Das Installationsprogramm öffnet TCP und UDP; fügen Sie bei einer älteren Installation eine UDP-Regel für den Port hinzu oder führen Sie das aktuelle Installationsprogramm erneut aus. Bis dahin funktioniert alles weiter über HTTP/2. Um HTTP/3 zu deaktivieren, entfernen Sie das Häkchen bei **Einstellungen → Sicherheit → HTTP/3** und führen Sie dann `Restart-Service LarisVMSWeb` aus.

## Strenge Skriptrichtlinie

Ist **Einstellungen → Sicherheit → Strenge Skriptrichtlinie** aktiviert, führen Browser nur die Skripte aus, die dieser Server sendet, sodass in eine Seite eingeschleuster Code nicht laufen kann. Sie ist standardmäßig aktiv, und ein Upgrade aktiviert sie. Funktioniert etwas nicht mehr, deaktivieren Sie sie: Browser melden dann alles, was die strenge Richtlinie blockiert hätte, und die neuesten Berichte erscheinen unter dem Schalter. Sobald nach der Nutzung der Seiten, auf die Sie angewiesen sind, keine Berichte mehr erscheinen, aktivieren Sie sie wieder. Eine Änderung wird beim nächsten Laden der Seite wirksam.

## Zertifikate

Das Zertifikat des Servers ist eine `.pfx`-Datei, die in `appsettings.Production.json` festgelegt wird. Eine erneuerte Datei wird innerhalb einer Minute ohne Neustart übernommen. Knoten mit direktem Streaming haben auf ihrer Seite eine eigene Zertifikatseinstellung.

## Kamerazertifikate

Über HTTPS erreichte Kameras haben selbstsignierte Zertifikate, die sich gegen nichts prüfen lassen, daher wird standardmäßig jedes Zertifikat akzeptiert. Aktivieren Sie **Einstellungen → Sicherheit → Kamerazertifikate festhalten**, damit der Server das Zertifikat jeder Kameraadresse bei der ersten Verbindung speichert und ab dann ein anderes ablehnt, auf dem Server und auf den Knoten. Etwas im Netzwerk, das sich als Kamera ausgibt, kann dann weder ihr Passwort einsammeln noch falsche Ereignisse senden.

Gespeicherte Zertifikate sind unter der Einstellung aufgelistet. Ändert sich das Zertifikat einer Kamera tatsächlich (ein Firmware-Update, ein Zurücksetzen oder ein neues Zertifikat), wählen Sie neben ihrer Adresse **Vergessen**; das nächste Zertifikat, das sie vorlegt, wird gespeichert. Bis dahin wird jede abgelehnte Verbindung, auf dem Server oder einem Knoten, im Audit-Protokoll festgehalten und Super-Admins werden informiert (höchstens einmal pro Stunde und Kameraadresse), da sich etwas im Netzwerk als Kamera ausgeben könnte. Eine Kameraadresse wird gespeichert, wenn der Server sie zum ersten Mal erreicht, meist beim Hinzufügen der Kamera; ein Knoten akzeptiert eine Adresse, die der Server noch nicht erreicht hat.

## Hostnamen

Standardmäßig antwortet der Server auf jeden Hostnamen, den ein Browser verwendet, sodass er per IP-Adresse, Kurzname und vollem Namen gleichermaßen funktioniert. Um nur auf die von Ihnen verwendeten Namen zu antworten, setzen Sie `AllowedHosts` in `appsettings.Production.json` auf diese, durch Semikola getrennt (zum Beispiel `"AllowedHosts": "vms.example.com;vms;localhost"`), und führen Sie dann `Restart-Service LarisVMSWeb` aus. Führen Sie jeden Namen auf, den Knoten, Proxys und Personen verwenden, um ihn zu erreichen: Alles andere erhält einen Fehler.

Tragen Sie hinter einem Reverse-Proxy (IIS ARR, nginx und so weiter) dessen Adresse in `appsettings.Production.json` als `"ReverseProxy": { "KnownProxies": [ "10.0.0.2" ] }` ein und starten Sie dann neu. Der Server übernimmt dann die echte Adresse und das Schema des Clients aus den Headern `X-Forwarded-For` und `X-Forwarded-Proto` dieses Proxys, sodass Zulassungslisten und Audit-Protokoll die Person sehen, nicht den Proxy. Diese Header werden von allem ignoriert, was nicht auf der Liste steht, und vollständig, solange die Liste leer ist.

## Aktualisierungen

Diese Prüfungen stehen zwischen einem Download und einer laufenden Installation:

- **Release-Downloads** kommen mit `SHA256SUMS.txt`. Prüfen Sie ein Installationsprogramm vor dem Ausführen mit `Get-FileHash <file> -Algorithm SHA256` und vergleichen Sie es mit der Zeile für diese Datei, oder führen Sie `sha256sum -c SHA256SUMS.txt` aus. Sind die Installationsprogramme codesigniert, zeigt Windows außerdem den Herausgeber an.
- **Knoten- und Proxy-Updates** sind mit dem Signaturschlüssel dieses Servers signiert. Ein Knoten oder Proxy behält diesen Schlüssel seit seiner Registrierung und installiert nichts, was nicht damit signiert ist, selbst wenn seine Verbindung zum Server manipuliert würde. Er lehnt außerdem einen Build ab, der nicht neuer ist als der, den er ausführt. Siehe [Installation](/Help/installation#updates).
- **Die Signatur des Herausgebers** (optional): Ein Knoten oder Proxy, der mit dem Schlüssel des Herausgebers gebaut wurde (`-PublisherKeyPath`), benötigt für jedes Update zusätzlich die Signatur des Herausgebers; andere Builds überspringen diese Prüfung. Dieser Schlüssel wird offline aufbewahrt und nie auf dem Server, sodass selbst ein aufgebrochener Server keinen eigenen Code an Knoten senden kann.
- **Die Verbindung** zum Server wird so geprüft, wie unter **Knoten → Aktualisierungen und Registrierung → Prüfung des Serverzertifikats** festgelegt (siehe [Knoten](/Help/nodes#server-certificate)).

## Verschlüsselte Geheimnisse

Kamera-Zugangsdaten, SMB-Zugangsdaten, Knotenschlüssel, E-Mail-Secrets, das Entra-Client-Secret und das Passwort des Active-Directory-Dienstkontos sind in der Datenbank verschlüsselt, mit den Datenschutzschlüsseln in `%ProgramData%\LarisVMS\keys`. Nur SYSTEM, Administratoren und die Konten der LarisVMS-Dienste können `%ProgramData%\LarisVMS` öffnen: Jeder andere dort vorgefundene Zugriff, etwa ein Ordner, den ein lokaler Benutzer vor der Installation von LarisVMS angelegt hat, wird beim Start eines Dienstes entfernt. Sichern Sie diesen Ordner und löschen Sie ihn nie bei einem Deployment.

## Zusätzlicher Schutz für Unternehmen

Von Haus aus ist LarisVMS so eingerichtet, dass eine Heiminstallation einfach funktioniert. Alles Folgende ist optional und standardmäßig aus; aktivieren Sie, was zu einem Unternehmen oder einem aus dem Internet erreichbaren Server passt:

- **Zwei-Faktor-Anmeldung verlangen**: Einstellungen → Sicherheit (siehe [Zwei-Faktor-Anmeldung](/Help/users#two-factor-sign-in)).
- **Passwort für Administrationsseiten bestätigen**: Einstellungen → Sicherheit (siehe [Sitzungen](/Help/users#sessions)).
- **Vorläufige, vom Administrator gesetzte Passwörter**: Einstellungen → Sicherheit (siehe [Passwörter](/Help/users#passwords)).
- **Kürzere Sitzungen und ein Inaktivitäts-Timeout**: jede Rolle unter Einstellungen → Rollen (siehe [Rollen](/Help/users#roles)).
- **Kamerazertifikate festhalten**: Einstellungen → Sicherheit (siehe [Kamerazertifikate](#camera-certificates)).
- **Zulassungslisten**: Einstellungen → Sicherheit (siehe [Zulassungslisten](#allow-lists)).
- **Das Einrichtungstoken von jedem anderen Computer**: Setzen Sie `"Setup": { "AlwaysRequireToken": true }` in `appsettings.Production.json`, bevor Sie den Einrichtungsassistenten ausführen (siehe [Installation](/Help/installation#web-server)).
- **Das Serverzertifikat bei der Installation eines Knotens oder Proxys festhalten**: `-ServerCertThumbprint` (siehe [Serverzertifikat](/Help/nodes#server-certificate)).
- **Einmal-Registrierungscodes, bei deaktiviertem gemeinsamem Schlüssel**: Knoten → Aktualisierungen und Registrierung (siehe [Einmal-Registrierungscodes](/Help/nodes#one-time-registration-codes)).
- **Verschlüsselte Verbindungen vom Server zu Knoten**: auf der Seite jedes Knotens (siehe [Verschlüsselte Verbindungen vom Server](/Help/nodes#encrypted-connections-from-the-server)).
- **Das Audit-Protokoll an Syslog weiterleiten**: Einstellungen → Protokolle (siehe [Audit-Protokoll](/Help/users#audit-log)).
- **Vom Herausgeber signierte Updates**, für Builds, die Sie selbst erstellen (siehe [Aktualisierungen](#updates)).
- **Ein Dienstkonto mit weniger Rechten.** Die LarisVMS-Dienste laufen als LocalSystem. Sie können sie unter einem anderen Konto ausführen (`services.msc` oder `sc.exe config`), müssen dann aber selbst dafür sorgen, dass dieses Konto hat, was der Dienst braucht: den Installationsordner, jeden Speicher- und Archivordner oder jede Freigabe sowie die Zertifikatsdateien. `%ProgramData%\LarisVMS` wird für Sie erledigt: Jeder LarisVMS-Dienst beschränkt ihn beim Start auf SYSTEM, Administratoren und die Konten, unter denen die LarisVMS-Dienste laufen, und entfernt jeden anderen Zugriff dort. Prüfen Sie nach der Änderung das Protokoll des Dienstes.
