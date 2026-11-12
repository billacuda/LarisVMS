# Installation und Aktualisierung

Installation des Webservers, der Aufzeichnungsknoten und der Medien-Proxys und wie Sie sie aktuell halten.

Jeder Teil hat sein eigenes Installationsprogramm: `LarisVMS-Web`, `LarisVMS-Node` und `LarisVMS-Proxy` (`.msi`). Doppelklicken Sie auf eines, um nach seinen Einstellungen gefragt zu werden, oder übergeben Sie sie auf der Befehlszeile mit `msiexec /i <file>.msi NAME=value … /qn`. Was nicht angegeben ist, wird abgefragt, und ein Upgrade übernimmt die Einstellungen der vorherigen Installation.

## Webserver

1. Stellen Sie einen vom Server erreichbaren SQL Server bereit (Integrierte Sicherheit oder SQL-Authentifizierung) oder einen MariaDB-Server (siehe [MariaDB](#mariadb)).
2. Führen Sie `LarisVMS-Web.msi` aus. Einstellungen:
   - `HTTPSPORT`: der HTTPS-Port (Standard 8444). Das Installationsprogramm öffnet ihn in der Firewall, für TCP und für UDP ([HTTP/3](/Help/security#http3)).
   - `CERTPATH`, `CERTPASSWORD`: das Serverzertifikat (`.pfx`). Leer lassen, um ein selbstsigniertes Zertifikat zu verwenden, bis Sie eines haben. Das Installationsprogramm prüft vor dem Fortfahren, ob sich die Datei mit dem Passwort öffnen lässt; eine Dateifreigabe muss zudem für das Dienstkonto lesbar sein.
   - `SERVICEACCOUNT`, `SERVICEPASSWORD`: den Dienst unter einem bestimmten Konto ausführen, etwa einem mit Rechten für die Integrierte Sicherheit von SQL. Leer führt ihn als LocalSystem aus. Für MariaDB nicht nötig.
3. Rufen Sie `https://<host>:8444/` auf. Der Einrichtungsassistent führt durch Datenbank, erstes Administratorkonto, Speicher und Knotenregistrierung. Ein Browser auf dem Server selbst oder im lokalen Netzwerk gelangt direkt hinein. Von überall sonst fragt er zuerst nach dem Einrichtungstoken in `%ProgramData%\LarisVMS\setup-token.txt` auf dem Server (nur für Administratoren lesbar), sodass niemand aus dem Internet die Einrichtung zuerst vornehmen kann. Um das Token von jedem anderen Computer, auch im lokalen Netzwerk, abzufragen, setzen Sie `"Setup": { "AlwaysRequireToken": true }` in `appsettings.Production.json`, bevor Sie den Assistenten öffnen. Die Datei wird gelöscht, sobald das Administratorkonto existiert.

Das Passwort einer SQL-Anmeldung oder ein MariaDB-Passwort wird verschlüsselt auf dem Server gespeichert (DPAPI, nur dieser Rechner), sodass `setup-generated.json` nicht mit funktionierendem Passwort auf einen anderen Rechner kopiert werden kann. Führen Sie den Assistenten dort stattdessen erneut aus.

Wenn Sie den Assistenten auf eine vorhandene LarisVMS-Datenbank richten (etwa nach einer Neuinstallation des Servers), verwendet er diese Datenbank unverändert: bei Bedarf aktualisiert, nichts vorbelegt oder überschrieben, und die übrigen Schritte werden übersprungen. Eine Datenbank, die nicht zu LarisVMS gehört, wird abgelehnt. Sobald die Einrichtung abgeschlossen ist, kann der Assistent nicht mehr geöffnet werden.

Der Server benötigt keine .NET-Laufzeit; sie ist enthalten. Das Zertifikat kann auch später in `appsettings.Production.json` im Installationsordner geändert werden. Eine erneuerte `.pfx` am selben Pfad wird innerhalb einer Minute übernommen, ohne Neustart.

## MariaDB

LarisVMS kann statt SQL Server auch MariaDB 10.11 oder neuer verwenden. Wählen Sie im Einrichtungsassistenten **MariaDB** und geben Sie Host, Port (Standard 3306), Datenbanknamen sowie einen MariaDB-Benutzer und ein Passwort ein. MariaDB hat keine Windows-Anmeldung, die LarisVMS verwenden könnte, daher sind Benutzer und Passwort immer nötig.

- **Der Benutzer** benötigt die Berechtigung, die Datenbank zu erstellen. Oder erstellen Sie sie zuerst selbst mit dem Zeichensatz `utf8mb4` und gewähren Sie dem Benutzer `ALL` darauf:

  ```sql
  CREATE DATABASE larisvms CHARACTER SET utf8mb4 COLLATE utf8mb4_uca1400_as_ci;
  CREATE USER 'larisvms'@'%' IDENTIFIED BY 'a-strong-password';
  GRANT ALL ON larisvms.* TO 'larisvms'@'%';
  ```

- **Verschlüsselung:** Die Verbindung verwendet TLS. Das Zertifikat des Servers wird geprüft, es sei denn, MariaDB läuft auf diesem Rechner oder Sie kreuzen **Dem Zertifikat dieses Servers vertrauen** an (für ein selbstsigniertes Zertifikat auf einem anderen Rechner).
- **Bei MariaDB vor 11.4 ist TLS deaktiviert**, bis es ein Zertifikat erhält, und ohne TLS sendet LarisVMS das Passwort nicht an einen anderen Rechner. Fügen Sie das Zertifikat und seinen Schlüssel im Abschnitt `[mysqld]` von `my.ini` hinzu, starten Sie MariaDB neu und kreuzen Sie **Dem Zertifikat dieses Servers vertrauen** an, wenn es selbstsigniert ist:

  ```ini
  [mysqld]
  ssl_cert = C:/MariaDB/certs/server-cert.pem
  ssl_key = C:/MariaDB/certs/server-key.pem
  ```

  MariaDB 11.4 und neuer richten selbst ein Zertifikat ein.
- **Sicherungen** benötigen die MariaDB-Client-Tools auf dem LarisVMS-Server (siehe [Sicherungen](/Help/backups)).
- Eine vorhandene SQL-Server-Installation kann nicht nach MariaDB umgezogen werden; MariaDB ist für neue Installationen gedacht.

## Knoten

Führen Sie `LarisVMS-Node.msi` auf jedem Aufzeichnungsrechner aus. Es benötigt:

- `SERVERURL` und `REGISTRATIONKEY`: die Adresse des Servers und den **Registrierungsschlüssel** unter **Knoten → Aktualisierungen und Registrierung**. Sie werden nur bei der ersten Registrierung des Knotens verwendet. Eine Änderung des Schlüssels wirkt sich nicht auf bestehende Knoten aus.
- **FFmpeg**, das nicht mitgeliefert wird. Installieren Sie es zuerst mit `winget install ffmpeg --scope machine`. Das Installationsprogramm findet es, oder setzen Sie `FFMPEGPATH`.
- `STORAGEROOT`: wohin der Knoten aufzeichnet. Kann auch später auf der Bearbeitungsseite des Knotens festgelegt werden.

Optional: `ARCHIVEROOT`, `LIVEPORT` (Standard 8554), `LIVEALLOWFROM` (Adressen, die durch die Firewall zum Live-Port dürfen, als IPs oder CIDR-Blöcke durch Kommas getrennt: der LarisVMS-Server und etwaige Proxys; leer lässt alle zu), `INSECURETLS=1` (nicht mehr nötig: Knoten vertrauen dem selbstsignierten Zertifikat des Servers beim ersten Kontakt, siehe [Serverzertifikat](/Help/nodes#server-certificate)), der Endpunkt für direktes Streaming (`CLIENTPORT`, `CLIENTENDPOINTHOST`, `CLIENTPFXPATH`, `CLIENTPFXPASSWORD`) sowie `SERVICEACCOUNT` / `SERVICEPASSWORD` für ein Konto, das SMB-Speicher erreichen kann.

Für die KI-Erkennung benötigen NVIDIA-Rechner zusätzlich das CUDA Toolkit 12.x und cuDNN 9.x; das Installationsprogramm kopiert deren DLLs an die richtige Stelle, wenn es sie findet. Siehe [KI-Erkennung](/Help/ai-detection#hardware).

Nach der Installation erscheint der Knoten unter **Knoten** als **wartet auf Freigabe**. Er zeichnet nichts auf und erhält keine Kameras, bis Sie auf **Freigeben** klicken, geben Sie also nur Knoten frei, die Sie selbst installiert haben. Weisen Sie ihm dann auf der Bearbeitungsseite jeder Kamera Kameras zu.

## Medien-Proxys

Führen Sie `LarisVMS-Proxy.msi` mit `SERVERURL`, `REGISTRATIONKEY` und `CLIENTPORT` (Standard 4443) aus, dazu dieselben optionalen Zertifikatseinstellungen wie bei einem Knoten. Ein neuer Proxy startet deaktiviert: Aktivieren Sie **Aktiviert** für ihn unter **Einstellungen → Medien-Proxys** und wählen Sie ihn dann auf der Seite jedes Knotens aus (siehe [Knoten](/Help/nodes#proxies)).

## Aktualisierungen

**Upgrade:** Führen Sie das neuere `.msi` aus. Es findet die vorhandene Installation und geht direkt zu **Bereit für das Upgrade**; die Einstellungen werden übernommen, sodass nichts erneut eingegeben werden muss außer dem Passwort eines Domänen-Dienstkontos. Der Webserver wendet etwaige Datenbankänderungen beim Start selbst an. Upgrades berühren nie Aufnahmen, Konfigurationsdateien, die Knotenregistrierung oder die Datenschutzschlüssel, und eine fehlgeschlagene Installation wird zurückgesetzt.

**Knoten und Proxys aktualisieren sich selbst.** Eine neue Webinstallation registriert die mitgelieferten Knoten- und Proxy-Builds als **Ausstehend** im Abschnitt **Builds** unter **Knoten**. Nur der neueste Knoten-Build und der neueste Proxy-Build warten auf Freigabe; ältere werden **Ersetzt**. **Build-Verlauf** listet jeden Build auf. Geben Sie einen frei, und jeder ältere Knoten lädt ihn beim nächsten Check-in herunter, prüft und installiert ihn. Der Server signiert jedes Update mit seinem Signaturschlüssel (siehe **Einstellungen → Verschlüsselung und Signierung**); Knoten und Proxys behalten diesen Schlüssel seit ihrer Registrierung und installieren nichts, was nicht damit signiert ist, selbst wenn ihre Verbindung zum Server manipuliert würde. Deaktivieren Sie dies mit **Automatische Aktualisierung** unter **Knoten → Aktualisierungen und Registrierung**.

**Neue Versionen:** Einmal täglich (und beim Start des Dienstes) prüft der Server die GitHub-Releases von LarisVMS. Wenn eine neuere Version verfügbar ist, sehen Personen, die Einstellungen ändern dürfen, in der oberen Leiste **✨ Version X ist verfügbar** mit einem Link zu den Versionshinweisen. Über Ihre Installation wird nichts gesendet. Deaktivieren Sie dies mit **Neue Versionen** unter **Knoten → Aktualisierungen und Registrierung**.

**Deinstallation** entfernt das Programm, seinen Dienst und die Firewall-Regeln. Aufnahmen, Konfiguration und `%ProgramData%\LarisVMS` bleiben erhalten.

## Installation aus dem Quellcode

Entwickler können stattdessen mit PowerShell aus dem Repository bauen und installieren: `install-web.ps1` (Server) sowie die Skripte `install-node.ps1` / `install-proxy.ps1` in den von `build-node.ps1` / `build-proxy.ps1` erstellten Paketen. `build-installers.ps1` erstellt die drei MSIs. Ein MSI aktualisiert einen per Skript installierten Rechner direkt und behält dabei seine Einstellungen, seine Registrierung und einen etwaigen Endpunkt für direktes Streaming samt Firewall-Regel.

## Zertifikate

`Kestrel:Certificates:Default:Path` und `:Password` in `appsettings.Production.json` verweisen auf eine `.pfx`-Datei. Der Server prüft sie alle 60 Sekunden und übernimmt ein erneuertes Zertifikat ohne Neustart.
