# API

Jeder Befehl der REST-API, mit dem, was er tut, was er benötigt, und einem Anwendungsbeispiel.

Die Beispiele verwenden erfundene Werte. Ersetzen Sie `https://vms.example.com` durch die Adresse Ihres Servers, `lvk_EXAMPLEKEY` durch Ihren API-Schlüssel und IDs wie `00000000-0000-0000-0000-000000000001` durch echte.

## Erste Schritte

1. Wählen Sie unter **Administration → API-Schlüssel** **Schlüssel hinzufügen**. Wählen Sie eine Rolle und kopieren Sie den Schlüssel, wenn er angezeigt wird. Sie sehen ihn nur einmal.
2. Senden Sie den Schlüssel bei jeder Anfrage in einem Header `X-Api-Key`.
3. Rufen Sie `/api/v1/status` auf, um zu prüfen, ob der Schlüssel funktioniert, und um Ihre Kamera-IDs zu erhalten.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" https://vms.example.com/api/v1/status
```

Jede Kamera in der Antwort hat eine `cameraId`, die die anderen Befehle verwenden.

Nur Pfade, die mit `/api/v1` beginnen, akzeptieren API-Schlüssel. Andere Pfade sind intern für die Weboberfläche und können sich ohne Ankündigung ändern.

## Schlüssel und Berechtigungen

Sie können beliebig viele Schlüssel haben, jeden mit eigenen Berechtigungen. Der Zugriff eines Schlüssels ergibt sich aus drei Dingen:

- **Rolle:** Der Schlüssel hat die Berechtigungen der Rolle, an die er gebunden ist.
- **Einschränkungen:** Kreuzen Sie **Diesen Schlüssel einschränken** an, um nur einige dieser Berechtigungen zuzulassen. Ein Schlüssel kann nie mehr als seine Rolle.
- **Kamerazugriff:** Der Kamerazugriff der Rolle unter **Einstellungen → Kamerazugriff** bestimmt, welche Kameras der Schlüssel nutzen kann. Zum Beispiel muss die Aktion **Aufzeichnung** für eine Kamera aktiviert sein, bevor ein Schlüssel sie aufzeichnen kann. Um einen Schlüssel auf bestimmte Kameras zu beschränken, binden Sie ihn an eine Rolle mit Zugriff nur auf diese Kameras.

Jeder Befehl unten nennt die Berechtigung, die er benötigt. Ein eingeschränkter Schlüssel muss diese Berechtigung angekreuzt haben.

Zum Beispiel könnten sich diese beiden Schlüssel eine Rolle mit breitem Zugriff teilen:

| Schlüssel | Einschränkungen | Kann |
|---|---|---|
| Überwachung | Das Kamera-Zustands-Dashboard ansehen | `/api/v1/status` lesen und sonst nichts |
| Werkstatt-Taster | Kameras ansehen, Manuelle Aufzeichnung starten und stoppen | Aufzeichnungen starten, stoppen und lesen |

Bearbeiten Sie Namen und Einschränkungen eines Schlüssels unter **Administration → API-Schlüssel**. Der Schlüssel selbst ändert sich nicht, sodass alles, was ihn verwendet, weiter funktioniert. Rolle und Ablauf eines Schlüssels lassen sich nicht ändern; erstellen Sie stattdessen einen neuen. **Widerrufen** stoppt einen Schlüssel sofort.

Ein eingeschränkter Schlüssel wird nie als Administrator behandelt, auch wenn seine Rolle **Super-Admin** ist.

Aktionen eines Schlüssels erscheinen im Audit-Protokoll als `API key: {name}`.

## Konventionen

- Anfragen und Antworten sind JSON, außer wo ein Befehl ein Bild oder eine Datei zurückgibt.
- Zeiten sind UTC im ISO-8601-Format, etwa `2026-11-06T14:30:00Z`.
- Feldnamen in JSON sind camelCase.
- **Einstellungen → Sicherheit** gilt auch hier. Die Zulassungsliste für die Verwaltung deckt die API ab. Bilder und Downloads zählen als Live- und Wiedergabeverkehr, folgen also dieser Zulassungsliste und, falls eingerichtet, dem eigenen Port.

| Status | Bedeutung |
|---|---|
| `200` | Es hat funktioniert. |
| `400` | Die Anfrage ist ungültig, etwa eine Endzeit vor der Startzeit. |
| `401` | Der Schlüssel fehlt, ist falsch, widerrufen oder abgelaufen. |
| `403` | Dem Schlüssel fehlt die Berechtigung, oder er hat keinen Zugriff auf eine Kamera. |
| `404` | Dieses Element existiert nicht. |
| `409` | Die Kamera kann das gerade nicht, etwa Aufzeichnen auf einer deaktivierten Kamera oder das Bewegen einer PTZ-Kamera, die jemand mit höherer Priorität nutzt. |
| `429` | Zu viele Anfragen. Warten Sie und versuchen Sie es erneut. Prüfen Sie den Header `Retry-After`. |
| `502`, `503`, `504` | Der Aufzeichnungsknoten war nicht erreichbar oder hat nicht rechtzeitig geantwortet. |

## Überwachung

### GET /health

Ob der Server läuft. Benötigt keinen Schlüssel, zur Verwendung durch Load Balancer und Verfügbarkeitsmonitore.

```bash
curl https://vms.example.com/health
```

```text
Healthy
```

### GET /api/v1/status

Der Zustand jeder Kamera und der Status jedes Knotens.

**Berechtigung:** Das Kamera-Zustands-Dashboard ansehen. Listet nur Kameras auf, die der Schlüssel ansehen kann.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" https://vms.example.com/api/v1/status
```

```json
{
  "cameras": [
    {
      "cameraId": "00000000-0000-0000-0000-000000000001",
      "cameraName": "Workshop",
      "cameraEnabled": true,
      "nodeName": "Recorder-1",
      "nodeOnline": true,
      "fps": 15,
      "bitrateKbps": 4096,
      "healthFresh": true
    }
  ],
  "nodes": []
}
```

### GET /api/v1/cameras/motion-state

Die IDs der Kameras, die gerade Bewegung sehen.

**Berechtigung:** Kameras ansehen.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" https://vms.example.com/api/v1/cameras/motion-state
```

```json
["00000000-0000-0000-0000-000000000001"]
```

### GET /api/v1/cameras/detection-state

Welche Kameras gerade ein erkanntes Objekt sehen und was es ist.

**Berechtigung:** Kameras ansehen.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" https://vms.example.com/api/v1/cameras/detection-state
```

```json
[
  {
    "cameraId": "00000000-0000-0000-0000-000000000001",
    "detections": [
      { "kind": "Human", "label": "Person", "emoji": "🧍", "colorHex": "#ff9f1c" }
    ]
  }
]
```

## Aufzeichnung

Manuelle Aufzeichnung starten und stoppen, die Aufnahmen aufbewahrt, was auch immer der Aufzeichnungsmodus der Kamera täte. Eine Aufzeichnung läuft, bis etwas sie stoppt, auch über Neustarts und Upgrades hinweg. Siehe [Aufzeichnung](/Help/recording#manual-recording).

Eine Kamera zu starten, die bereits aufzeichnet, oder eine zu stoppen, die es nicht tut, ist sicher und ändert nichts.

Die Befehle zum Starten und Stoppen geben zurück, welche Kameras sich geändert haben (`changed`), welche unverändert blieben (`unchanged`) und welche übersprungen wurden (`skipped`), weil sie deaktiviert sind oder nicht existieren.

### POST /api/v1/cameras/{cameraId}/recording/start

Startet die Aufzeichnung einer Kamera. Gibt `409` zurück, wenn die Kamera deaktiviert ist oder nicht existiert.

**Berechtigung:** Manuelle Aufzeichnung starten und stoppen, plus Kamerazugriff **Aufzeichnung**.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/recording/start
```

```json
{
  "changed": ["00000000-0000-0000-0000-000000000001"],
  "unchanged": [],
  "skipped": []
}
```

### POST /api/v1/cameras/{cameraId}/recording/stop

Stoppt die Aufzeichnung einer Kamera.

**Berechtigung:** Manuelle Aufzeichnung starten und stoppen, plus Kamerazugriff **Aufzeichnung**.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/recording/stop
```

### POST /api/v1/recording/start

Startet die Aufzeichnung mehrerer Kameras. Gibt `403` zurück und ändert nichts, wenn dem Schlüssel der Zugriff auf eine davon fehlt.

**Berechtigung:** Manuelle Aufzeichnung starten und stoppen, plus Kamerazugriff **Aufzeichnung**.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraIds":["00000000-0000-0000-0000-000000000001","00000000-0000-0000-0000-000000000002"]}' \
  https://vms.example.com/api/v1/recording/start
```

### POST /api/v1/recording/stop

Stoppt die Aufzeichnung mehrerer Kameras.

**Berechtigung:** Manuelle Aufzeichnung starten und stoppen, plus Kamerazugriff **Aufzeichnung**.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraIds":["00000000-0000-0000-0000-000000000001","00000000-0000-0000-0000-000000000002"]}' \
  https://vms.example.com/api/v1/recording/stop
```

### GET /api/v1/recording

Welche Kameras gerade Aufnahmen aufbewahren. Kameras, bei denen nichts passiert, werden nicht aufgeführt.

`keeping` bedeutet, dass gerade aus irgendeinem Grund Aufnahmen gespeichert werden. `manual` bedeutet, dass eine manuelle Aufzeichnung läuft. Eine Kamera, die offline ist und bei der eine manuelle Aufzeichnung läuft, zeigt `manual` ohne `keeping`.

**Berechtigung:** Kameras ansehen.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" https://vms.example.com/api/v1/recording
```

```json
[
  {
    "cameraId": "00000000-0000-0000-0000-000000000001",
    "keeping": true,
    "manual": true,
    "manualStartedUtc": "2026-11-06T14:30:00Z",
    "manualStartedBy": "API key: Workshop button"
  }
]
```

## Bilder

### GET /api/v1/cameras/{cameraId}/snapshot

Ein JPEG, das jetzt von der Kamera aufgenommen wird. Dies öffnet eine kurze Verbindung zur Kamera, verwenden Sie daher das neueste Vorschaubild unten, wenn Sie häufig prüfen.

**Berechtigung:** Kameras hinzufügen, bearbeiten und löschen, plus Kamerazugriff **Konfigurieren**.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" -o snapshot.jpg \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/snapshot
```

### GET /api/v1/cameras/{cameraId}/thumbnail/latest

Ein Bild aus den zuletzt gespeicherten Aufnahmen der Kamera. Eine Kamera ohne gespeicherte Aufnahmen hat kein Vorschaubild.

**Berechtigung:** Kameras ansehen.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" -o latest.jpg \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/thumbnail/latest
```

## PTZ

### POST /api/v1/cameras/{cameraId}/ptz/move

Setzt die Kamera in Bewegung. Die Werte reichen von `-1` bis `1`, wobei `0` keine Bewegung bedeutet. Die Kamera bewegt sich weiter, bis Sie stop aufrufen.

| Feld | Bedeutung |
|---|---|
| `panX` | Links (negativ) oder rechts (positiv). |
| `tiltY` | Nach unten (negativ) oder oben (positiv). |
| `zoomX` | Heraus (negativ) oder hinein (positiv). |

**Berechtigung:** Kameras ansehen, plus Kamerazugriff **PTZ**.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"panX":0.5,"tiltY":0,"zoomX":0}' \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/ptz/move
```

### POST /api/v1/cameras/{cameraId}/ptz/stop

Stoppt die Bewegung der Kamera.

**Berechtigung:** Kameras ansehen, plus Kamerazugriff **PTZ**.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/ptz/stop
```

## Wiedergabedaten

### GET /api/v1/cameras/{cameraId}/timeline

Eine Zusammenfassung eines Zeitraums, aufgeteilt in gleich große Abschnitte: ob jeder Aufnahmen oder Bewegung enthält und die Farben etwaiger Ereignisse.

| Parameter | Bedeutung |
|---|---|
| `from`, `to` | Der Zeitraum. Erforderlich. |
| `buckets` | Wie viele Abschnitte. Standard 200. |

**Berechtigung:** Aufgezeichnete Aufnahmen wiedergeben, plus Kamerazugriff **Wiedergabe**.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" \
  "https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/timeline?from=2026-11-06T00:00:00Z&to=2026-11-07T00:00:00Z&buckets=24"
```

```json
[
  {
    "startUtc": "2026-11-06T00:00:00Z",
    "endUtc": "2026-11-06T01:00:00Z",
    "hasRecording": true,
    "hasMotion": false,
    "tagColorHex": "#e5383b",
    "tagColorHexes": ["#e5383b"]
  }
]
```

### GET /api/v1/cameras/{cameraId}/segments

Die gespeicherten Aufnahmedateien in einem Zeitraum.

| Parameter | Bedeutung |
|---|---|
| `from`, `to` | Der Zeitraum. Erforderlich. |

**Berechtigung:** Aufgezeichnete Aufnahmen wiedergeben, plus Kamerazugriff **Wiedergabe**.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" \
  "https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/segments?from=2026-11-06T14:00:00Z&to=2026-11-06T15:00:00Z"
```

```json
[
  {
    "id": 1234,
    "startUtc": "2026-11-06T14:30:00Z",
    "endUtc": "2026-11-06T14:31:00Z",
    "isArchived": false
  }
]
```

### GET /api/v1/cameras/{cameraId}/activity

Wann Ereignisse auf dieser Kamera tendenziell auftreten, nach Tageszeit.

| Parameter | Bedeutung |
|---|---|
| `range` | Wie weit zurück, etwa `7d` (der Standard) oder `30d`. |
| `view` | `calendar` zeigt es nach Tag, ab 30 Tagen. |
| `sel` | Ein einzubeziehender Ereignistyp oder eine Bezeichnung, etwa `Animal` oder `Animal:cat`. Für mehrere wiederholen. |

**Berechtigung:** Aufgezeichnete Aufnahmen wiedergeben, plus Kamerazugriff **Wiedergabe**.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" \
  "https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/activity?range=30d"
```

## Lesezeichen

### GET /api/v1/cameras/{cameraId}/bookmarks

Die Lesezeichen einer Kamera in einem Zeitraum.

| Parameter | Bedeutung |
|---|---|
| `from`, `to` | Der Zeitraum. Erforderlich. |

**Berechtigung:** Aufgezeichnete Aufnahmen wiedergeben, plus Kamerazugriff **Wiedergabe**.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" \
  "https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/bookmarks?from=2026-11-06T00:00:00Z&to=2026-11-07T00:00:00Z"
```

```json
[
  {
    "id": "00000000-0000-0000-0000-00000000000b",
    "cameraId": "00000000-0000-0000-0000-000000000001",
    "cameraName": "Workshop",
    "timestampUtc": "2026-11-06T14:30:00Z",
    "note": "Print failed here",
    "createdByUserName": "API key: Workshop button",
    "createdAt": "2026-11-06T14:35:00Z"
  }
]
```

### POST /api/v1/bookmarks

Fügt ein Lesezeichen an einem Moment in den Aufnahmen einer Kamera hinzu.

**Berechtigung:** Aufgezeichnete Aufnahmen wiedergeben und Lesezeichen für aufgezeichnete Aufnahmen erstellen, plus Kamerazugriff **Wiedergabe**.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraId":"00000000-0000-0000-0000-000000000001","timestampUtc":"2026-11-06T14:30:00Z","note":"Print failed here"}' \
  https://vms.example.com/api/v1/bookmarks
```

```json
{ "id": "00000000-0000-0000-0000-00000000000b" }
```

## Exporte

Ein Export speichert Aufnahmen von einer oder mehreren Kameras als Datei für jede Kamera. Exporte laufen im Hintergrund: Erstellen Sie einen, prüfen Sie ihn und laden Sie jede Datei herunter, wenn sie fertig ist.

### POST /api/v1/exports

Startet einen Export. Auf 20 Anfragen pro Minute und Schlüssel begrenzt.

**Berechtigung:** Exporte erstellen und herunterladen, plus Kamerazugriff **Export**.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraIds":["00000000-0000-0000-0000-000000000001"],"fromUtc":"2026-11-06T14:00:00Z","toUtc":"2026-11-06T14:30:00Z"}' \
  https://vms.example.com/api/v1/exports
```

```json
{ "jobId": "00000000-0000-0000-0000-00000000000e" }
```

### GET /api/v1/exports

Listet Exporte und ihren Fortschritt auf. Jeder Export hat `items`, eines für jede Kamera. Die `id` eines Elements verwenden Sie zum Herunterladen oder Wiederholen.

**Berechtigung:** Exporte erstellen und herunterladen.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" https://vms.example.com/api/v1/exports
```

```json
[
  {
    "id": "00000000-0000-0000-0000-00000000000e",
    "createdUtc": "2026-11-06T14:40:00Z",
    "requestedByUserName": "API key: Workshop button",
    "fromUtc": "2026-11-06T14:00:00Z",
    "toUtc": "2026-11-06T14:30:00Z",
    "status": "Completed",
    "items": [
      {
        "id": "00000000-0000-0000-0000-00000000000f",
        "cameraId": "00000000-0000-0000-0000-000000000001",
        "cameraName": "Workshop",
        "nodeName": "Recorder-1",
        "status": "Completed",
        "errorMessage": null,
        "outputSizeBytes": 104857600,
        "redacted": false
      }
    ]
  }
]
```

### GET /api/v1/exports/items/{exportItemId}/download

Lädt eine fertige Exportdatei herunter.

**Berechtigung:** Exporte erstellen und herunterladen, plus Kamerazugriff **Export**.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" -o workshop.mp4 \
  https://vms.example.com/api/v1/exports/items/00000000-0000-0000-0000-00000000000f/download
```

### POST /api/v1/exports/{itemId}/retry

Wiederholt ein fehlgeschlagenes Exportelement.

**Berechtigung:** Exporte erstellen und herunterladen.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/exports/00000000-0000-0000-0000-00000000000f/retry
```

### DELETE /api/v1/exports/{jobId}

Löscht einen Export und seine Dateien. Ein Schlüssel kann Exporte löschen, die er erstellt hat. Nur ein Administrator kann die anderer löschen.

**Berechtigung:** Exporte erstellen und herunterladen.

```bash
curl -X DELETE -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/exports/00000000-0000-0000-0000-00000000000e
```

## Beweissicherungen

Eine Beweissicherung verhindert, dass Aufnahmen gelöscht werden. Siehe [Beweissicherungen](/Help/live-and-playback#evidence-locks).

### GET /api/v1/evidence-locks

Listet Sicherungen auf.

| Parameter | Bedeutung |
|---|---|
| `cameraId` | Nur diese Kamera. Für mehrere wiederholen. |
| `fromUtc`, `toUtc` | Nur Sicherungen von Aufnahmen in diesem Zeitraum. |
| `includeInactive` | `true` schließt beendete Sicherungen ein. |

**Berechtigung:** Aufgezeichnete Aufnahmen wiedergeben, plus Kamerazugriff **Wiedergabe**.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" \
  "https://vms.example.com/api/v1/evidence-locks?cameraId=00000000-0000-0000-0000-000000000001"
```

### POST /api/v1/evidence-locks

Sichert Aufnahmen auf einer oder mehreren Kameras.

| Feld | Bedeutung |
|---|---|
| `cameraIds` | Die Kameras. |
| `fromUtc`, `toUtc` | Die zu sichernden Aufnahmen. |
| `duration` | `1d`, `3d`, `7d`, `14d`, `30d`, `custom` oder `indefinite`. |
| `customUntilUtc` | Wann eine `custom`-Sicherung endet. |
| `reason` | Warum gesichert wird. |

**Berechtigung:** Aufgezeichnete Aufnahmen wiedergeben und Aufnahmen sichern, damit die Aufbewahrung sie nicht löschen kann, plus Kamerazugriff **Wiedergabe**.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraIds":["00000000-0000-0000-0000-000000000001"],"fromUtc":"2026-11-06T14:00:00Z","toUtc":"2026-11-06T14:30:00Z","duration":"30d","reason":"Incident 1042"}' \
  https://vms.example.com/api/v1/evidence-locks
```

### POST /api/v1/evidence-locks/{id}/remove

Entfernt eine Sicherung. Ein Grund ist erforderlich.

**Berechtigung:** Aufgezeichnete Aufnahmen wiedergeben, und dieselben Regeln wie beim Entfernen einer Sicherung in der Weboberfläche.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"reason":"Case closed"}' \
  https://vms.example.com/api/v1/evidence-locks/00000000-0000-0000-0000-00000000000c/remove
```

### POST /api/v1/evidence-locks/{id}/restore

Stellt eine entfernte Sicherung während ihrer Schonfrist wieder her.

**Berechtigung:** Aufgezeichnete Aufnahmen wiedergeben, und dieselben Regeln wie beim Wiederherstellen einer Sicherung in der Weboberfläche.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/evidence-locks/00000000-0000-0000-0000-00000000000c/restore
```

## Nicht in der API

Diese stehen API-Schlüsseln nicht zur Verfügung:

- Live-Video. Verwenden Sie die Weboberfläche oder die obigen Bilder.
- Aufnahmen direkt wiedergeben. Verwenden Sie einen Export.
- Einstellungen, Benutzer, Rollen, API-Schlüssel, Verschlüsselung und Sicherungen.
- Kamera-Einrichtung, etwa Zonen, Zeitpläne und Geräteeinstellungen.
