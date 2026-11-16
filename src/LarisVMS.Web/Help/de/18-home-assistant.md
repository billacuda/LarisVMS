# Home Assistant

Zeigen Sie Ihre Kameras, Erkennungen und Aufnahmeknoten in Home Assistant an, und starten Sie von dort Aufzeichnungen oder setzen Sie Lesezeichen.

> **Experimentell.** Die Home-Assistant-Anbindung ist neu, und Home Assistant wird für sehr Unterschiedliches genutzt. Bitte [teilen Sie uns mit, wie sie bei Ihnen funktioniert](https://github.com/billacuda/LarisVMS/discussions/new?category=general&title=Home%20Assistant%20feedback): wofür Sie sie nutzen, was gut läuft und was schiefgeht.

## So funktioniert es

LarisVMS sendet an den MQTT-Broker, den Home Assistant bereits nutzt. Die MQTT-Erkennung von Home Assistant legt dann für jede Kamera und jeden Aufnahmeknoten ein Gerät an, ganz ohne YAML.

Home Assistant handelt als eine Rolle, die Sie wählen, genau wie ein API-Schlüssel:

- Es erhält nur die Kameras, die diese Rolle unter **Einstellungen → Kamerazugriff** ansehen darf.
- Es kann nur, was diese Rolle darf. Der Aufzeichnungsschalter erscheint zum Beispiel nur, wenn die Rolle **Manuelle Aufzeichnung starten und stoppen** und den Kamerazugriff **Aufzeichnung** hat.
- Was es tut, steht im Audit-Protokoll als **Home Assistant**.

## Einrichten

1. Installieren Sie in Home Assistant das Add-on **Mosquitto broker** und die Integration **MQTT**, falls Sie sie noch nicht nutzen. Legen Sie eine Broker-Anmeldung für LarisVMS an, etwa einen Home-Assistant-Benutzer namens `larisvms`.
2. Legen Sie in LarisVMS unter **Einstellungen → Berechtigungen → Rollen** eine Rolle für Home Assistant an. Geben Sie ihr **Kameras ansehen** und unter **Einstellungen → Kamerazugriff** Zugriff auf die gewünschten Kameras. Fügen Sie **Manuelle Aufzeichnung starten und stoppen** oder **Aufgezeichnete Aufnahmen wiedergeben** und **Lesezeichen für aufgezeichnete Aufnahmen erstellen** hinzu, wenn Home Assistant aufzeichnen oder Lesezeichen setzen soll.
3. Öffnen Sie **Einstellungen → Home Assistant** und schalten Sie es ein. Geben Sie die Adresse des Brokers ein (mit dem Add-on die Adresse Ihres Home Assistant), den Port (meist `1883`), die Anmeldung und die Rolle. Wählen Sie **Verbindung testen** und dann **Speichern**.
4. Öffnen Sie in Home Assistant **Einstellungen → Geräte & Dienste → MQTT**. Ihre Kameras erscheinen dort als Geräte.

Der Status unter **Einstellungen → Home Assistant** zeigt, ob LarisVMS verbunden ist und wie viele Kameras es sendet. Startet der Broker neu oder fällt das Netzwerk aus, verbindet sich LarisVMS von selbst wieder. Startet Home Assistant neu, sendet LarisVMS alles erneut.

## Was in Home Assistant erscheint

Jede Kamera erhält:

| Entität | Zeigt |
|---|---|
| **Bewegung** | An, solange Bewegung oder eine Erkennung vorliegt |
| **Person**, **Fahrzeug**, **Tier** | An, solange diese Art Objekt erkannt wird |
| **Online** | Ob die Kamera Video sendet |
| **Neueste Erkennung** | Ein Bild des zuletzt erkannten Objekts |
| **Letzte Erkennung**, **Letztes Ereignis** | Was zuletzt erkannt wurde, und wann |
| **Letzte Geschwindigkeit** | Die zuletzt gemessene Geschwindigkeit aus [Geschwindigkeitsfallen](/Help/ai-detection), in den Einheiten unter **Einstellungen → KI-Erkennung** |
| **Manuelle Aufnahme** | Ein Schalter, der eine manuelle Aufzeichnung startet und stoppt, wenn die Rolle es erlaubt |
| **Lesezeichen setzen** | Eine Schaltfläche, die zur aktuellen Zeit ein Lesezeichen setzt, wenn die Rolle es erlaubt |

Jeder Aufnahmeknoten erhält **Online** und **Freier Speicher**.

Ein Sensor schaltet sich ein bis zwei Sekunden nach einer Erkennung ein und etwa 25 Sekunden nach ihrem Ende aus. Das entspricht dem Bewegungssymbol unter **Live**.

## Live-Video

Die **Generic Camera** von Home Assistant kann Live-Video von LarisVMS zeigen. Sie nutzt denselben Stream wie die Webseiten, die Kamera muss also keinen zweiten liefern.

1. Geben Sie der Home-Assistant-Rolle die Berechtigung **Live-Video an andere Programme streamen**. Keine Rolle hat sie, bis Sie sie vergeben.
2. Fügen Sie unter **Administration → API-Schlüssel** einen Schlüssel für diese Rolle hinzu.
3. Fügen Sie in Home Assistant für jede Kamera eine **Generic Camera** hinzu. **Einstellungen → Home Assistant** listet für jede Kamera die **Standbild-URL** und die **Stream-Quelle** auf. Wählen Sie die Authentifizierung **Basic**, geben Sie `apikey` als Benutzernamen und den Schlüssel als Kennwort ein.

Die [API](/Help/api) beschreibt den Stream.

## Alarme per Webhook

Der Webhook einer Alarmregel kann auch an Home Assistant senden. Erstellen Sie in Home Assistant eine Automatisierung mit dem Auslöser **Webhook**, und tragen Sie deren Adresse beim Webhook der Regel ein, etwa `http://homeassistant.local:8123/api/webhook/larisvms-front-door`.

Der Webhook sendet JSON mit Regel, Meldung und Zeit, und, wenn der Alarm sie hat, Ereignis, Kamera, Knoten, erkanntem Objekt, Geschwindigkeit und `snapshotPath`. Hängen Sie `snapshotPath` an Ihre LarisVMS-Adresse an, um mit einem API-Schlüssel das Bild der Erkennung abzurufen.

```yaml
triggers:
  - trigger: webhook
    webhook_id: larisvms-front-door
    local_only: true
actions:
  - action: notify.notify
    data:
      message: "{{ trigger.json.message }}"
```

## Wenn etwas fehlt

- **Nicht verbunden:** Prüfen Sie Adresse, Port und Anmeldung mit **Verbindung testen**. Ein Broker, der TLS verlangt, nutzt meist Port `8883`.
- **Keine Geräte in Home Assistant:** Prüfen Sie, ob die MQTT-Integration eingerichtet ist und das Discovery-Präfix zu dem von Home Assistant passt (`homeassistant`, sofern Sie es nicht geändert haben).
- **Eine Kamera fehlt:** Die Rolle darf sie nicht ansehen, oder die Kamera ist ausgeschaltet.
- **Kein Aufzeichnungsschalter oder keine Lesezeichen-Schaltfläche:** Der Rolle fehlt die Berechtigung oder der Kamerazugriff dafür.
- **Der Webhook kommt nicht an:** LarisVMS sendet keine Webhooks an `localhost`. Verwenden Sie die Netzwerkadresse von Home Assistant, auch wenn es auf demselben Rechner läuft.
