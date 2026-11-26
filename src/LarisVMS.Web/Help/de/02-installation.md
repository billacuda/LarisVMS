# Installation und Aktualisierung

Installation des Webservers, der Aufzeichnungsknoten und der Medien-Proxys und wie Sie sie aktuell halten.

Jeder Teil hat sein eigenes Installationsprogramm: `LarisVMS-Web`, `LarisVMS-Node` und `LarisVMS-Proxy` (`.msi`). Doppelklicken Sie auf eines, um nach seinen Einstellungen gefragt zu werden, oder übergeben Sie sie auf der Befehlszeile mit `msiexec /i <file>.msi NAME=value … /qn`. Was nicht angegeben ist, wird abgefragt, und ein Upgrade übernimmt die Einstellungen der vorherigen Installation.

Jeder Teil läuft auch unter Linux, aus einem `.deb`- oder `.rpm`-Paket (siehe [Linux](#linux)), und beides lässt sich mischen: Ein Windows-Server kann Linux-Knoten und -Proxys betreiben und umgekehrt.

## Webserver

1. Stellen Sie einen vom Server erreichbaren SQL Server bereit (Integrierte Sicherheit oder SQL-Authentifizierung) oder einen MariaDB-Server (siehe [MariaDB](#mariadb)).
2. Führen Sie `LarisVMS-Web.msi` aus. Einstellungen:
   - `HTTPSPORT`: der HTTPS-Port (Standard 8444). Das Installationsprogramm öffnet ihn in der Firewall, für TCP und für UDP ([HTTP/3](/Help/security#http3)).
   - `CERTPATH`, `CERTPASSWORD`: das Serverzertifikat (`.pfx`). Leer lassen, um ein selbstsigniertes Zertifikat zu verwenden, bis Sie eines haben. Das Installationsprogramm prüft vor dem Fortfahren, ob sich die Datei mit dem Passwort öffnen lässt; eine Dateifreigabe muss zudem für das Dienstkonto lesbar sein.
   - `SERVICEACCOUNT`, `SERVICEPASSWORD`: den Dienst unter einem bestimmten Konto ausführen, etwa einem mit Rechten für die Integrierte Sicherheit von SQL. Leer führt ihn als LocalSystem aus. Für MariaDB nicht nötig.
3. Rufen Sie `https://<host>:8444/` auf. Der Einrichtungsassistent führt durch Datenbank, erstes Administratorkonto, Speicher und Knotenregistrierung. Ein Browser auf dem Server selbst oder im lokalen Netzwerk gelangt direkt hinein. Von überall sonst fragt er zuerst nach dem Einrichtungstoken in `%ProgramData%\LarisVMS\setup-token.txt` auf dem Server (unter Linux `/var/lib/larisvms/setup-token.txt`; nur für Administratoren lesbar), sodass niemand aus dem Internet die Einrichtung zuerst vornehmen kann. Um das Token von jedem anderen Computer, auch im lokalen Netzwerk, abzufragen, setzen Sie `"Setup": { "AlwaysRequireToken": true }` in `appsettings.Production.json`, bevor Sie den Assistenten öffnen. Die Datei wird gelöscht, sobald das Administratorkonto existiert.

Das Passwort einer SQL-Anmeldung oder ein MariaDB-Passwort wird verschlüsselt auf dem Server gespeichert (unter Windows DPAPI, unter Linux ein an die ID des Rechners gebundener Schlüssel; nur dieser Rechner), sodass `setup-generated.json` nicht mit funktionierendem Passwort auf einen anderen Rechner kopiert werden kann. Führen Sie den Assistenten dort stattdessen erneut aus.

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

Für die KI-Erkennung benötigen NVIDIA-Rechner zusätzlich CUDA 12 und cuDNN 9; unter Windows kopiert das Installationsprogramm deren DLLs an die richtige Stelle, wenn es sie findet. Siehe [KI-Erkennung](/Help/ai-detection#hardware).

Nach der Installation erscheint der Knoten unter **Knoten** als **wartet auf Freigabe**. Er zeichnet nichts auf und erhält keine Kameras, bis Sie auf **Freigeben** klicken, geben Sie also nur Knoten frei, die Sie selbst installiert haben. Weisen Sie ihm dann auf der Bearbeitungsseite jeder Kamera Kameras zu.

## Medien-Proxys

Führen Sie `LarisVMS-Proxy.msi` mit `SERVERURL`, `REGISTRATIONKEY` und `CLIENTPORT` (Standard 4443) aus, dazu dieselben optionalen Zertifikatseinstellungen wie bei einem Knoten. Ein neuer Proxy startet deaktiviert: Aktivieren Sie **Aktiviert** für ihn unter **Einstellungen → Medien-Proxys** und wählen Sie ihn dann auf der Seite jedes Knotens aus (siehe [Knoten](/Help/nodes#proxies)).

## Linux

Webserver, Knoten und Proxys werden aus `.deb`-Paketen (Ubuntu 22.04, 24.04, 26.04, Debian 12, 13) oder `.rpm`-Paketen (Rocky Linux 9, 10 und andere RHEL-9- oder -10-Nachbauten) für x86_64 installiert. Jeder läuft als systemd-Dienst (`larisvms-web`, `larisvms-node`, `larisvms-proxy`) unter dem eigenen Konto `larisvms`. Das Programm liegt in `/opt/larisvms`, die Einstellungen in `/etc/larisvms`, die Daten (Schlüssel, Modelle, das Einrichtungstoken) in `/var/lib/larisvms` und die Protokolldateien in `/var/log/larisvms`. Eine .NET-Laufzeit ist nicht nötig. Die Installation öffnet die Ports des Dienstes in firewalld oder ufw, wenn eines davon aktiv ist.

- **Webserver:** `sudo apt install ./larisvms-web_<version>-1_amd64.deb` (oder `sudo dnf install ./larisvms-web-<version>-1.x86_64.rpm`). Er startet sofort; öffnen Sie `https://<host>:8444/` für den Einrichtungsassistenten. Port und Zertifikat werden in `/etc/larisvms/web.env` festgelegt (`Kestrel__HttpsPort`, `Kestrel__Certificates__Default__Path`, `Kestrel__Certificates__Default__Password`; das Konto larisvms muss die Datei lesen können), danach `sudo systemctl restart larisvms-web`. `appsettings.Production.json` und `setup-generated.json` liegen ebenfalls in `/etc/larisvms`. SQL Server benötigt unter Linux eine SQL-Anmeldung; MariaDB ist dort die einfachere Wahl.
- **Knoten:** Installieren Sie `larisvms-node` und koppeln Sie ihn dann mit `sudo larisvms-node setup --server-url https://<server>:8444 --registration-key <key>`. Der Schritt „Knoten“ des Einrichtungsassistenten zeigt diesen Befehl mit eingetragenem Schlüssel und Fingerabdruck des Serverzertifikats. Er zeichnet nach `/var/lib/larisvms/recordings` auf, sofern Sie nicht `--storage-root <folder>` angeben; eine Netzwerkfreigabe muss vorher eingehängt sein (zum Beispiel in `/etc/fstab`) und vom Konto larisvms beschrieben werden können. Weitere Optionen: `--archive-root`, `--live-port`, `--live-allow-from`, `--client-port`, `--client-pfx-path`, `--client-endpoint-host` und `--ffmpeg-path`. `sudo larisvms-node setup --help` listet alle auf, und ein erneuter Aufruf von `setup` ändert sie.
- **ffmpeg** kommt aus der Distribution und wird mit dem Knoten installiert. Unter Rocky Linux kommt es von RPM Fusion; aktivieren Sie daher EPEL, CRB und RPM Fusion, bevor Sie einen Knoten installieren.
- **Proxys:** Installieren Sie `larisvms-proxy`, danach `sudo larisvms-proxy setup --server-url https://<server>:8444 --registration-key <key> --client-port 4443`.

Den Zustand eines Dienstes sehen Sie mit `systemctl status larisvms-node` und `journalctl -u larisvms-node`, seine Version mit `larisvms-node --version`.

**Docker:** Jede Version wird auch als Images in der Container-Registry von GitHub veröffentlicht: `ghcr.io/billacuda/larisvms-web`, `larisvms-node` (mit den Tags `-cuda` und `-tensorrt` für NVIDIA-GPUs) und `larisvms-proxy`. Der Webserver benötigt eine Datenbank, die Sie selbst betreiben (MariaDB 10.11 oder neuer oder SQL Server mit einer SQL-Anmeldung). Installieren Sie ihn auf eine dieser Arten:

- **Docker Compose:** Laden Sie [compose.yml](https://github.com/billacuda/LarisVMS/blob/main/docker/compose.yml) herunter und führen Sie in dem Ordner `docker compose up -d` aus. Damit startet der Webserver; öffnen Sie `https://<host>:8444/` für den Einrichtungsassistenten. Entfernen Sie dann in der Datei die Kommentarzeichen vor dem Knoten (und dem Proxy, falls gewünscht), tragen Sie die Adresse des Servers, den Registrierungsschlüssel und den Aufnahmeordner ein und führen Sie `docker compose up -d` erneut aus.
- **Portainer-Stack:** Fügen Sie unter **Stacks → Add stack → Web editor** dieselbe Datei ein und wählen Sie **Deploy the stack**. Entfernen Sie nach dem Assistenten auf dem Tab **Editor** des Stacks die Kommentarzeichen vor dem Knoten und wählen Sie **Update the stack**. Verwenden Sie eine eigenständige Docker-Umgebung, keinen Swarm.
- **Reines Docker:** Starten Sie den Webserver, führen Sie den Assistenten aus und starten Sie dann den Knoten:

```bash
docker run -d --name larisvms-web --network host --restart unless-stopped \
  -e Kestrel__HttpsPort=8444 \
  -v /srv/larisvms/web/data:/var/lib/larisvms \
  -v /srv/larisvms/web/config:/etc/larisvms \
  -v /srv/larisvms/web/logs:/var/log/larisvms \
  ghcr.io/billacuda/larisvms-web:latest

docker run -d --name larisvms-node --hostname larisvms-node-1 --network host --restart unless-stopped \
  -e LARISVMS_SERVER_URL=https://<server address>:8444 \
  -e LARISVMS_REGISTRATION_KEY=<key> \
  -e LARISVMS_STORAGE_ROOT=/recordings \
  -v /srv/larisvms/node/data:/var/lib/larisvms \
  -v /srv/larisvms/node/config:/etc/larisvms \
  -v /srv/larisvms/node/logs:/var/log/larisvms \
  -v /srv/larisvms/recordings:/recordings \
  ghcr.io/billacuda/larisvms-node:latest
```

Geben Sie den Knoten anschließend unter **Knoten** frei. Verwenden Sie als Adresse des Servers seine LAN-Adresse oder seinen Namen, nicht `localhost`, auch auf demselben Rechner. Hat der Rechner eine Firewall, lassen Sie TCP 8444 (Webserver), 8554 (Knoten), 8555 (ein Knoten, der direkt an Browser streamt) und 4443 (Proxy) zu sowie UDP 8444, wenn Sie HTTP/3 verwenden. Die Container laufen als Benutzer und Gruppe 8444, denen ihre Ordner auf dem Host gehören.

Der Schritt „Knoten“ des Einrichtungsassistenten und die Seite **Medien-Proxys** zeigen einen Compose-Stack für Portainer oder `docker compose`, in dem Adresse, Registrierungsschlüssel und Zertifikat dieses Servers eingetragen sind. Die Einstellungen sind dieselben Umgebungsvariablen, die auch die Linux-Dienste verwenden (`LARISVMS_SERVER_URL`, `LARISVMS_REGISTRATION_KEY`, `LARISVMS_STORAGE_ROOT` und so weiter). Legen Sie `/var/lib/larisvms` auf ein Volume: Dort liegen die Schlüssel, die Registrierung und die Rechner-ID, mit der gespeicherte Passwörter lesbar bleiben, wenn der Container neu erstellt wird. Verwenden Sie Host-Networking, damit die Kamerasuche funktioniert. Zum Aktualisieren führen Sie `docker compose pull` und `docker compose up -d` aus, wählen in Portainer **Update the stack** mit eingeschaltetem **Re-pull image and redeploy**, oder Sie laden mit reinem Docker das Image, entfernen den Container und führen seinen Befehl erneut aus.

**Eine Linux-Installation zu Docker umziehen:** Kopieren Sie den Datenordner (vor allem `keys`) und die Rechner-ID vor dem ersten Start des Containers und sichern Sie zuerst die Datenbank. Das Datenbankpasswort und die Knotenregistrierungen sind mit der Rechner-ID verschlüsselt, die Passwörter und Knotenschlüssel in der Datenbank mit `keys`; ohne sie zeigen Seiten wie **Knoten** einen Fehler. Eine Windows-Installation kann nicht auf diese Weise umziehen. Für den Webserver:

```bash
sudo systemctl disable --now larisvms-web
sudo mkdir -p /srv/larisvms/web/data /srv/larisvms/web/config
sudo tar -C /var/lib/larisvms --exclude=./recordings --exclude=./node.config --exclude=./proxy.config -cf - . \
  | sudo tar -C /srv/larisvms/web/data -xf -
sudo cp -a /etc/larisvms/. /srv/larisvms/web/config/
sudo install -m 600 /etc/machine-id /srv/larisvms/web/data/machine-id
```

Übernehmen Sie Einstellungen aus `/etc/larisvms/web.env` in `environment` des Stacks und starten Sie dann den Container. Er verbindet sich ohne den Assistenten mit derselben Datenbank. Betreiben Sie nie den alten Dienst und den Container gleichzeitig. Bei einem Knoten bleiben die Aufnahmen, wo sie sind:

```bash
sudo systemctl disable --now larisvms-node
sudo mkdir -p /srv/larisvms/node/data
sudo tar -C /var/lib/larisvms --exclude=./recordings --exclude=./keys --exclude=./update --exclude=./proxy.config -cf - . \
  | sudo tar -C /srv/larisvms/node/data -xf -
sudo install -m 600 /etc/machine-id /srv/larisvms/node/data/machine-id

# The container's account needs to own the recordings (repeat for an archive folder):
sudo chown -R 8444:8444 /var/lib/larisvms/recordings
```

Übernehmen Sie die Einstellungen aus `/etc/larisvms/node.env` in `environment` des Stacks und hängen Sie jeden Aufnahme- und Archivordner im Container unter demselben Pfad ein (zum Beispiel `/var/lib/larisvms/recordings:/var/lib/larisvms/recordings`, mit `LARISVMS_STORAGE_ROOT` auf diesen Pfad gesetzt). Der Knoten meldet sich als er selbst zurück, mit seinen Kameras und Aufnahmen, und braucht weder Registrierungsschlüssel noch Freigabe. Ein Proxy zieht genauso um, nur ohne die Aufnahmen. Wenn Sie die alten Pakete danach entfernen, werden die Ports in der Firewall geschlossen; öffnen Sie sie also für die Container wieder.

Haben Sie den Assistenten im Container bereits mit der alten Datenbank ausgeführt, fehlen nur die Schlüssel. Kopieren Sie sie neben die eigenen des Containers und behalten Sie beide:

```bash
docker stop larisvms-web
sudo docker cp /var/lib/larisvms/keys/. larisvms-web:/var/lib/larisvms/keys/
docker start larisvms-web
```

Wo die Hilfe einen Windows-Ordner nennt, ist es unter Linux dieser: `%ProgramData%\LarisVMS` → `/var/lib/larisvms` (sein Ordner `logs` → `/var/log/larisvms`), und `appsettings.Production.json` sowie `setup-generated.json` aus dem Installationsordner → `/etc/larisvms`. `Restart-Service LarisVMSWeb` ist `sudo systemctl restart larisvms-web`.

## Aktualisierungen

**Upgrade:** Führen Sie das neuere `.msi` aus. Es findet die vorhandene Installation und geht direkt zu **Bereit für das Upgrade**; die Einstellungen werden übernommen, sodass nichts erneut eingegeben werden muss außer dem Passwort eines Domänen-Dienstkontos. Der Webserver wendet etwaige Datenbankänderungen beim Start selbst an. Upgrades berühren nie Aufnahmen, Konfigurationsdateien, die Knotenregistrierung oder die Datenschutzschlüssel, und eine fehlgeschlagene Installation wird zurückgesetzt.

**Knoten und Proxys aktualisieren sich selbst.** Eine neue Webinstallation registriert die mitgelieferten Knoten- und Proxy-Builds als **Ausstehend** im Abschnitt **Builds** unter **Knoten**. Nur der neueste Knoten-Build und der neueste Proxy-Build warten auf Freigabe; ältere werden **Ersetzt**. **Build-Verlauf** listet jeden Build auf. Geben Sie einen frei, und jeder ältere Knoten lädt ihn beim nächsten Check-in herunter, prüft und installiert ihn. Der Server signiert jedes Update mit seinem Signaturschlüssel (siehe **Einstellungen → Verschlüsselung und Signierung**); Knoten und Proxys behalten diesen Schlüssel seit ihrer Registrierung und installieren nichts, was nicht damit signiert ist, selbst wenn ihre Verbindung zum Server manipuliert würde. Deaktivieren Sie dies mit **Automatische Aktualisierung** unter **Knoten → Aktualisierungen und Registrierung**.

**Neue Versionen:** Einmal täglich (und beim Start des Dienstes) prüft der Server die GitHub-Releases von LarisVMS. Wenn eine neuere Version verfügbar ist, sehen Personen, die Einstellungen ändern dürfen, in der oberen Leiste **✨ Version X ist verfügbar** mit einem Link zu den Versionshinweisen. Über Ihre Installation wird nichts gesendet. Deaktivieren Sie dies mit **Neue Versionen** unter **Knoten → Aktualisierungen und Registrierung**.

**Unter Linux** aktualisieren Sie den Webserver, indem Sie das neuere Paket auf dieselbe Weise installieren; `/etc/larisvms` und `/var/lib/larisvms` bleiben erhalten. Linux-Knoten und -Proxys aktualisieren sich wie die unter Windows selbst vom Server, sobald Sie den Build freigeben: Ein kleines Hilfsprogramm, das als root läuft, installiert die Aktualisierung, die der Dienst heruntergeladen und geprüft hat. Ein Linux-Server bietet Windows-Knoten ihren Windows-Build an, ein Windows-Server Linux-Knoten den ihren.

**Deinstallation** entfernt das Programm, seinen Dienst und die Firewall-Regeln. Aufnahmen, Konfiguration und `%ProgramData%\LarisVMS` bleiben erhalten. Unter Linux bewirkt `apt remove` oder `dnf remove` dasselbe und behält `/var/lib/larisvms`, `/var/log/larisvms` und `/etc/larisvms`.

## Installation aus dem Quellcode

Entwickler können stattdessen mit PowerShell aus dem Repository bauen und installieren: `install-web.ps1` (Server) sowie die Skripte `install-node.ps1` / `install-proxy.ps1` in den von `build-node.ps1` / `build-proxy.ps1` erstellten Paketen. `build-installers.ps1` erstellt die drei MSIs und die `.deb`- und `.rpm`-Pakete für Linux (mit nFPM). Ein MSI aktualisiert einen per Skript installierten Rechner direkt und behält dabei seine Einstellungen, seine Registrierung und einen etwaigen Endpunkt für direktes Streaming samt Firewall-Regel.

## Zertifikate

`Kestrel:Certificates:Default:Path` und `:Password` in `appsettings.Production.json` verweisen auf eine `.pfx`-Datei (unter Linux auch in `/etc/larisvms/web.env` als `Kestrel__Certificates__Default__Path` und `__Password` festlegbar). Der Server prüft sie alle 60 Sekunden und übernimmt ein erneuertes Zertifikat ohne Neustart.

In Docker hängen Sie den Ordner des Zertifikats ein (`/srv/larisvms/certs:/certs:ro`) und verwenden den Pfad innerhalb des Containers, etwa `/certs/vms.pfx`. Benutzer 8444 muss ihn lesen können, und ein `$` im Passwort wird in einer Compose-Datei als `$$` geschrieben. Ein Zertifikat, das nicht geladen werden kann, wird durch ein selbstsigniertes ersetzt, und `docker logs` nennt den Grund.
