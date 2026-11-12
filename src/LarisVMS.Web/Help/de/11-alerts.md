# Alarme, Benachrichtigungen und E-Mail

Alarmregeln, Benachrichtigungen an Benutzer, Team-Kanäle und die Einrichtung des E-Mail-Anbieters.

## Alarmregeln

**Einstellungen → Alarme** enthält die gemeinsamen Regeln. Jede Regel überwacht eine Bedingung:

- **Kamera meldet sich nicht**: Die Kamera sendet keine Zustandsberichte mehr.
- **Knoten offline**: Der Knoten meldet sich nicht mehr.
- **Speicher knapp**: Der freie Speicher ist unter einen Prozentwert gefallen.
- **Knoten-Failover**: Die Kameras eines Knotens wurden auf seinen Ersatzknoten verschoben.
- **Bewegung erkannt**: Bewegung auf einer Kamera oder in einer ihrer Bewegungszonen.
- **Objekt erkannt**: Die KI-Erkennung hat ein passendes Objekt gesehen.
- **Geschwindigkeit über einem Limit**: Ein Objekt hat eine [Geschwindigkeitsmessung](/Help/ai-detection#speed-traps) mit einer festgelegten Geschwindigkeit oder darüber überquert.
- **Beweissicherung läuft bald ab** und **Beweissicherung entfernt**: siehe [Beweissicherungen](/Help/live-and-playback#evidence-locks).

Zustandsregeln werden einmal pro Minute geprüft. Die **Wartezeit** ist die Mindestzeit zwischen wiederholten Alarmen, damit eine Bedingung, die bestehen bleibt, nicht jede Minute alarmiert.

### Erkennungsalarme

**Objekt erkannt** und **Geschwindigkeit über einem Limit** lösen innerhalb von Sekunden nach der Erkennung aus. Jeder Filter ist optional, und alle, die Sie festlegen, müssen zutreffen:

- **Kamera**: eine Kamera, die Kameras eines Knotens oder beliebige Kamera.
- **Kategorie** und **Bezeichnung**: zum Beispiel Fahrzeug oder nur Lkw.
- **Farbe**: die Farbe des Objekts oder Oberteil- oder Hosenfarbe einer Person. Siehe [Objektfarben](/Help/ai-detection#object-colors).
- **Zone**: nur Objekte, die innerhalb einer Zone der Kamera stehen (beurteilt anhand der Unterkante des Rahmens).
- **Aktiv**: Wochentage und ein Zeitfenster, in der Zeitzone derjenigen Person, die die Regel gespeichert hat. Ein Fenster wie 22:00 bis 06:00 läuft über Nacht.

Eine Erkennung alarmiert einmal. Bei einer Regel, die mehrere Kameras überwacht, zählt die Wartezeit für jede Kamera getrennt, sodass eine aktive Kamera die anderen nicht zurückhält.

### Bewegungsalarme

**Bewegung erkannt** löst innerhalb von Sekunden nach Bewegung aus, durch die Bewegungserkennung des Servers oder die eigenen Bewegungsereignisse der Kamera. Wählen Sie eine Kamera, die Kameras eines Knotens oder beliebige Kamera. Bei einer gewählten Kamera beschränkt **Zone** auf eine ihrer **Serverbewegung**-Zonen; lassen Sie es auf **(gesamte Kamera)** für Bewegung irgendwo. Die **Aktiv**-Tage und -Zeiten funktionieren wie bei Erkennungsalarmen. Ein Bewegungsabschnitt alarmiert einmal, und die Wartezeit hält den nächsten zurück.

### Team-Kanäle

Jede Regel kann außerdem an E-Mail-Listen, Webhooks, ntfy, Pushover, Slack und Teams senden. Aktivieren Sie einen Kanal und füllen Sie seine Felder aus.

## Benachrichtigungen

Benachrichtigungen gehen an einzelne Personen, in deren Browser oder per E-Mail, statt an einen Team-Kanal. Jede Person wählt unter **Benachrichtigungen** in der Seitenleiste (auch das 🔔 neben Ihrem Namen), was sie möchte. Eine Benachrichtigung erreicht jemanden nur für Kameras, die er ansehen darf.

- **Gemeinsame Benachrichtigungen**: Bei einer Alarmregel führt **Benutzer abonnieren lassen** sie unter **Meine Benachrichtigungen → Verfügbar** auf, wo jeder sie aktivieren kann. Administratoren können außerdem unter **Abonnenten** der Regel einen Benutzer oder alle Mitglieder einer Rolle hinzufügen.
- **Ihre eigenen Benachrichtigungen**: **Meine Benachrichtigungen → Meine → Neue Benachrichtigung** erstellt eine nur für Sie, mit denselben Optionen wie eine Alarmregel. Zum Beispiel Tiere auf der Gartenkamera zwischen 22 und 6 Uhr. Nur Sie sehen sie.
- **Stummschalten** Sie eine Benachrichtigung für eine Stunde, 8 Stunden, einen Tag oder bis Sie sie wieder aktivieren, ohne sie abzubestellen.

**Einstellungen → Benachrichtigungen** schaltet jede Art von Benachrichtigung für Benutzer ein oder aus. Das Ausschalten einer Art stoppt die Team-Kanäle der Alarmregeln nicht.

Das Abonnieren erfordert die Berechtigung **Gemeinsame Benachrichtigungen abonnieren**, und das Erstellen eigener die Berechtigung **Persönliche Benachrichtigungen erstellen**. Jede Rolle hat beide standardmäßig.

### Browser-Benachrichtigungen

Aktivieren Sie sie in jedem Browser unter **Meine Benachrichtigungen → Dieser Browser und Geräte** und verwenden Sie dann **Test senden**. **Einstellungen → Benachrichtigungen** wählt, wie sie zugestellt werden:

| Zustellung | Funktioniert, wenn | Benötigt |
|---|---|---|
| **Solange LarisVMS geöffnet ist** | Ein LarisVMS-Tab oder die installierte App geöffnet ist | Nichts. Bleibt in Ihrem Netzwerk. |
| **Web Push** | Keine LarisVMS-Seite geöffnet ist (Chrome, Edge, Firefox und Safari). Auf einem Computer muss der Browser selbst laufen; siehe unten | **Web-Push-Schlüssel generieren** einmalig und ausgehenden Internetzugang vom Server zu den Push-Diensten der Browserhersteller. Kein Konto. |
| **Firebase Cloud Messaging** | Keine LarisVMS-Seite geöffnet ist, mit derselben Einschränkung auf Computern | Ihr eigenes Firebase-Projekt: seine Web-App-Konfiguration, das Web-Push-Zertifikat und ein Dienstkonto-Schlüssel. Wird nur verwendet, wenn Web Push aus ist. |

Ohne Berechtigung zum Anzeigen von Benachrichtigungen erscheinen sie stattdessen innerhalb geöffneter LarisVMS-Seiten. Installieren Sie LarisVMS auf einem Telefon zuerst als App (siehe [den Überblick](/Help/overview)).

Telefone erhalten Push-Benachrichtigungen auch bei geschlossenem Browser. Auf einem Computer empfängt ein Browser sie nur, solange er läuft, und das Schließen seines letzten Fensters beendet ihn meist; zwischenzeitlich Gesendetes erscheint, wenn er wieder startet. Damit er im Hintergrund weiterläuft, aktivieren Sie **Hintergrund-Apps weiter ausführen, wenn Google Chrome geschlossen ist** (Chrome: Einstellungen → System) oder **Hintergrund-Erweiterungen und -Apps weiter ausführen, wenn Microsoft Edge geschlossen ist** (Edge: Einstellungen → System und Leistung). Verwenden Sie E-Mail für alle, die Alarme ohne laufenden Browser benötigen.

### E-Mail-Benachrichtigungen

Personen können für eine Benachrichtigung auch E-Mail wählen. Sie geht an die Adresse ihres Kontos, über die unten eingerichtete E-Mail. **Einstellungen → Benachrichtigungen** kann E-Mail-Benachrichtigungen ausschalten.

## E-Mail

Alarm-E-Mails und Benachrichtigungen werden über den unter **Einstellungen → E-Mail** eingerichteten Anbieter gesendet. **Speichern und Test senden** prüft die gespeicherten Einstellungen.

- **SMTP**: Host, Port, SSL/TLS und Zugangsdaten.
- **Microsoft Graph**: Registrieren Sie eine App in Entra ID, gewähren Sie ihr die **Anwendungs**berechtigung `Mail.Send` (nicht delegiert) mit Administratoreinwilligung und geben Sie ihre Mandanten-ID, Client-ID und das Secret ein. Es gibt keine Anmeldung pro Benutzer.
- **Gmail**: Erstellen Sie in Google Cloud einen OAuth-Client vom Typ *Webanwendung*. Fügen Sie `https://<your-host>/Admin/OAuthCallback` als autorisierten Weiterleitungs-URI hinzu und aktivieren Sie den Bereich `https://mail.google.com/`. Verwenden Sie dann einmal **Speichern und mit Google verbinden**, um die Zustimmung zu erteilen.

Gespeicherte Secrets sind verschlüsselt. Gehen die Datenschutzschlüssel des Servers verloren, geben Sie sie erneut ein und verbinden Sie Gmail neu.
