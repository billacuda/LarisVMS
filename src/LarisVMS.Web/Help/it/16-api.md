# API

Ogni comando dell’API REST, con cosa fa, cosa richiede e un esempio d’uso.

Gli esempi usano valori inventati. Sostituisca `https://vms.example.com` con l’indirizzo del Suo server, `lvk_EXAMPLEKEY` con la Sua chiave API e ID come `00000000-0000-0000-0000-000000000001` con quelli reali.

## Per iniziare

1. In **Admin → Chiavi API**, selezioni **Aggiungi chiave**. Scelga un ruolo e copi la chiave quando viene mostrata. La vede una sola volta.
2. Invii la chiave in un’intestazione `X-Api-Key` a ogni richiesta.
3. Chiami `/api/v1/status` per verificare che la chiave funzioni e ottenere gli ID delle telecamere.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" https://vms.example.com/api/v1/status
```

Ogni telecamera nella risposta ha un `cameraId`, che gli altri comandi usano.

Solo i percorsi che iniziano con `/api/v1` accettano chiavi API. Gli altri percorsi sono interni all’interfaccia web e possono cambiare senza preavviso.

## Chiavi e autorizzazioni

Può avere quante chiavi vuole, ciascuna con le proprie autorizzazioni. L’accesso di una chiave deriva da tre cose:

- **Ruolo:** la chiave ha le autorizzazioni del ruolo a cui è associata.
- **Limiti:** selezioni **Limita questa chiave** per consentire solo alcune di quelle autorizzazioni. Una chiave non può mai fare più del suo ruolo.
- **Accesso alle telecamere:** l’accesso alle telecamere del ruolo in **Impostazioni → Accesso alle telecamere** decide quali telecamere la chiave può usare. Ad esempio, l’azione **Registra** deve essere attiva per una telecamera prima che una chiave possa registrarla. Per limitare una chiave a certe telecamere, la associ a un ruolo con accesso solo a quelle telecamere.

Ogni comando qui sotto elenca l’autorizzazione di cui ha bisogno. Una chiave limitata deve avere quell’autorizzazione selezionata.

Ad esempio, queste due chiavi potrebbero condividere un ruolo con ampio accesso:

| Chiave | Limiti | Può fare |
|---|---|---|
| Monitoraggio | Vedere la dashboard dello stato delle telecamere | Leggere `/api/v1/status`, e nient’altro |
| Pulsante dell’officina | Vedere le telecamere, Avviare e interrompere la registrazione manuale | Avviare, interrompere e leggere le registrazioni |

Modifichi nome e limiti di una chiave in **Admin → Chiavi API**. La chiave stessa non cambia, quindi tutto ciò che la usa continua a funzionare. Il ruolo e la scadenza di una chiave non possono cambiare; ne crei invece una nuova. **Revoca** ferma subito una chiave.

Una chiave limitata non viene mai trattata come amministratore, anche se il suo ruolo è **Super Admin**.

Le azioni di una chiave compaiono nel registro di audit come `API key: {name}`.

## Convenzioni

- Richieste e risposte sono in JSON, tranne dove un comando restituisce un’immagine o un file.
- Gli orari sono in UTC, in formato ISO 8601, come `2026-11-06T14:30:00Z`.
- I nomi dei campi JSON sono in camelCase.
- **Impostazioni → Sicurezza** si applica anche qui. L’elenco consentiti di gestione copre l’API. Immagini e download contano come traffico di visualizzazione dal vivo e riproduzione, quindi seguono quell’elenco consentiti e, se ne ha impostata una, la porta separata.

| Stato | Significato |
|---|---|
| `200` | Ha funzionato. |
| `400` | La richiesta non è valida, ad esempio un’ora di fine prima dell’inizio. |
| `401` | La chiave è mancante, errata, revocata o scaduta. |
| `403` | La chiave non ha l’autorizzazione, o non ha accesso a una telecamera. |
| `404` | Quell’elemento non esiste. |
| `409` | La telecamera non può farlo in questo momento, ad esempio registrare su una telecamera disattivata, o muovere una telecamera PTZ usata da qualcuno con priorità superiore. |
| `429` | Troppe richieste. Attenda e riprovi. Controlli l’intestazione `Retry-After`. |
| `502`, `503`, `504` | Il nodo di registrazione non ha potuto essere raggiunto o non ha risposto in tempo. |

## Monitoraggio

### GET /health

Se il server è attivo. Non richiede chiave, per l’uso da parte di bilanciatori di carico e monitor di uptime.

```bash
curl https://vms.example.com/health
```

```text
Healthy
```

### GET /api/v1/status

Lo stato di ogni telecamera e di ogni nodo.

**Autorizzazione:** Vedere la dashboard dello stato delle telecamere. Elenca solo le telecamere che la chiave può vedere.

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

Gli ID delle telecamere che vedono movimento in questo momento.

**Autorizzazione:** Vedere le telecamere.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" https://vms.example.com/api/v1/cameras/motion-state
```

```json
["00000000-0000-0000-0000-000000000001"]
```

### GET /api/v1/cameras/detection-state

Quali telecamere vedono un oggetto rilevato in questo momento e di cosa si tratta.

**Autorizzazione:** Vedere le telecamere.

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

## Registrazione

Avviare e interrompere la registrazione manuale, che conserva i filmati qualunque sia la modalità di registrazione della telecamera. Una registrazione dura finché qualcosa non la interrompe, anche attraverso riavvii e aggiornamenti. Vedere [Registrazione](/Help/recording#manual-recording).

Avviare una telecamera che sta già registrando, o interromperne una che non lo fa, è sicuro e non cambia nulla.

I comandi di avvio e interruzione restituiscono quali telecamere sono `changed`, quali `unchanged` e quali `skipped` perché disattivate o inesistenti.

### POST /api/v1/cameras/{cameraId}/recording/start

Avvia la registrazione di una telecamera. Restituisce `409` se la telecamera è disattivata o non esiste.

**Autorizzazione:** Avviare e interrompere la registrazione manuale, più l’accesso **Registra** alle telecamere.

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

Interrompe la registrazione di una telecamera.

**Autorizzazione:** Avviare e interrompere la registrazione manuale, più l’accesso **Registra** alle telecamere.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/recording/stop
```

### POST /api/v1/recording/start

Avvia la registrazione di più telecamere. Restituisce `403`, e non cambia nulla, se la chiave non ha accesso a una qualsiasi di esse.

**Autorizzazione:** Avviare e interrompere la registrazione manuale, più l’accesso **Registra** alle telecamere.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraIds":["00000000-0000-0000-0000-000000000001","00000000-0000-0000-0000-000000000002"]}' \
  https://vms.example.com/api/v1/recording/start
```

### POST /api/v1/recording/stop

Interrompe la registrazione di più telecamere.

**Autorizzazione:** Avviare e interrompere la registrazione manuale, più l’accesso **Registra** alle telecamere.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraIds":["00000000-0000-0000-0000-000000000001","00000000-0000-0000-0000-000000000002"]}' \
  https://vms.example.com/api/v1/recording/stop
```

### GET /api/v1/recording

Quali telecamere stanno conservando filmati in questo momento. Le telecamere in cui non accade nulla non sono elencate.

`keeping` significa che i filmati vengono salvati al momento, per qualsiasi motivo. `manual` significa che è attiva una registrazione manuale. Una telecamera offline con una registrazione manuale attiva mostra `manual` senza `keeping`.

**Autorizzazione:** Vedere le telecamere.

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

## Immagini

### GET /api/v1/cameras/{cameraId}/snapshot

Un JPEG scattato ora dalla telecamera. Questo apre una breve connessione alla telecamera, quindi usi l’ultima miniatura qui sotto se controlla spesso.

**Autorizzazione:** Aggiungere, modificare ed eliminare telecamere, più l’accesso **Configura** alle telecamere.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" -o snapshot.jpg \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/snapshot
```

### GET /api/v1/cameras/{cameraId}/thumbnail/latest

Un’immagine dai filmati salvati più recenti della telecamera. Una telecamera senza filmati salvati non ha miniatura.

**Autorizzazione:** Vedere le telecamere.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" -o latest.jpg \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/thumbnail/latest
```

## PTZ

### POST /api/v1/cameras/{cameraId}/ptz/move

Avvia il movimento della telecamera. I valori vanno da `-1` a `1`, dove `0` è nessun movimento. La telecamera continua a muoversi finché non chiama stop.

| Campo | Significato |
|---|---|
| `panX` | Sinistra (negativo) o destra (positivo). |
| `tiltY` | Giù (negativo) o su (positivo). |
| `zoomX` | Riduzione (negativo) o ingrandimento (positivo). |

**Autorizzazione:** Vedere le telecamere, più l’accesso **PTZ** alle telecamere.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"panX":0.5,"tiltY":0,"zoomX":0}' \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/ptz/move
```

### POST /api/v1/cameras/{cameraId}/ptz/stop

Ferma il movimento della telecamera.

**Autorizzazione:** Vedere le telecamere, più l’accesso **PTZ** alle telecamere.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/ptz/stop
```

## Dati di riproduzione

### GET /api/v1/cameras/{cameraId}/timeline

Un riepilogo di un intervallo di tempo, diviso in intervalli di dimensione uguale: se ciascuno ha filmati o movimento e i colori di eventuali eventi.

| Parametro | Significato |
|---|---|
| `from`, `to` | L’intervallo di tempo. Obbligatorio. |
| `buckets` | Quanti intervalli. Predefinito 200. |

**Autorizzazione:** Riprodurre i filmati registrati, più l’accesso **Riproduzione** alle telecamere.

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

I file di filmati salvati in un intervallo di tempo.

| Parametro | Significato |
|---|---|
| `from`, `to` | L’intervallo di tempo. Obbligatorio. |

**Autorizzazione:** Riprodurre i filmati registrati, più l’accesso **Riproduzione** alle telecamere.

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

Quando gli eventi tendono ad accadere su questa telecamera, per ora del giorno.

| Parametro | Significato |
|---|---|
| `range` | Quanto indietro, come `7d` (predefinito) o `30d`. |
| `view` | `calendar` lo mostra per giorno, per 30 giorni o più. |
| `sel` | Un tipo di evento o un’etichetta da includere, come `Animal` o `Animal:cat`. Si ripete per più di uno. |

**Autorizzazione:** Riprodurre i filmati registrati, più l’accesso **Riproduzione** alle telecamere.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" \
  "https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/activity?range=30d"
```

## Segnalibri

### GET /api/v1/cameras/{cameraId}/bookmarks

I segnalibri su una telecamera in un intervallo di tempo.

| Parametro | Significato |
|---|---|
| `from`, `to` | L’intervallo di tempo. Obbligatorio. |

**Autorizzazione:** Riprodurre i filmati registrati, più l’accesso **Riproduzione** alle telecamere.

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

Aggiunge un segnalibro in un momento dei filmati di una telecamera.

**Autorizzazione:** Riprodurre i filmati registrati, e Creare segnalibri sui filmati registrati, più l’accesso **Riproduzione** alle telecamere.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraId":"00000000-0000-0000-0000-000000000001","timestampUtc":"2026-11-06T14:30:00Z","note":"Print failed here"}' \
  https://vms.example.com/api/v1/bookmarks
```

```json
{ "id": "00000000-0000-0000-0000-00000000000b" }
```

## Esportazioni

Un’esportazione salva i filmati di una o più telecamere come file per ciascuna telecamera. Le esportazioni vengono eseguite in background: ne crei una, controlli lo stato e scarichi ogni file quando è completato.

### POST /api/v1/exports

Avvia un’esportazione. Limitata a 20 richieste al minuto per chiave.

**Autorizzazione:** Creare e scaricare esportazioni, più l’accesso **Esportazione** alle telecamere.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraIds":["00000000-0000-0000-0000-000000000001"],"fromUtc":"2026-11-06T14:00:00Z","toUtc":"2026-11-06T14:30:00Z"}' \
  https://vms.example.com/api/v1/exports
```

```json
{ "jobId": "00000000-0000-0000-0000-00000000000e" }
```

### GET /api/v1/exports

Elenca le esportazioni e il loro avanzamento. Ogni esportazione ha `items`, uno per ogni telecamera. L’`id` di un elemento è ciò che usa per scaricarlo o ritentarlo.

**Autorizzazione:** Creare e scaricare esportazioni.

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

Scarica un file di esportazione completato.

**Autorizzazione:** Creare e scaricare esportazioni, più l’accesso **Esportazione** alle telecamere.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" -o workshop.mp4 \
  https://vms.example.com/api/v1/exports/items/00000000-0000-0000-0000-00000000000f/download
```

### POST /api/v1/exports/{itemId}/retry

Ritenta un elemento di esportazione non riuscito.

**Autorizzazione:** Creare e scaricare esportazioni.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/exports/00000000-0000-0000-0000-00000000000f/retry
```

### DELETE /api/v1/exports/{jobId}

Elimina un’esportazione e i suoi file. Una chiave può eliminare le esportazioni che ha creato. Solo un amministratore può eliminare quelle di altri.

**Autorizzazione:** Creare e scaricare esportazioni.

```bash
curl -X DELETE -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/exports/00000000-0000-0000-0000-00000000000e
```

## Blocchi delle prove

Un blocco delle prove impedisce che i filmati vengano eliminati. Vedere [Blocchi delle prove](/Help/live-and-playback#evidence-locks).

### GET /api/v1/evidence-locks

Elenca i blocchi.

| Parametro | Significato |
|---|---|
| `cameraId` | Solo questa telecamera. Si ripete per più di una. |
| `fromUtc`, `toUtc` | Solo i blocchi su filmati in questo intervallo di tempo. |
| `includeInactive` | `true` include i blocchi terminati. |

**Autorizzazione:** Riprodurre i filmati registrati, più l’accesso **Riproduzione** alle telecamere.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" \
  "https://vms.example.com/api/v1/evidence-locks?cameraId=00000000-0000-0000-0000-000000000001"
```

### POST /api/v1/evidence-locks

Blocca i filmati su una o più telecamere.

| Campo | Significato |
|---|---|
| `cameraIds` | Le telecamere. |
| `fromUtc`, `toUtc` | I filmati da bloccare. |
| `duration` | `1d`, `3d`, `7d`, `14d`, `30d`, `custom` o `indefinite`. |
| `customUntilUtc` | Quando termina un blocco `custom`. |
| `reason` | Perché è bloccato. |

**Autorizzazione:** Riprodurre i filmati registrati, e Bloccare i filmati perché la conservazione non possa eliminarli, più l’accesso **Riproduzione** alle telecamere.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraIds":["00000000-0000-0000-0000-000000000001"],"fromUtc":"2026-11-06T14:00:00Z","toUtc":"2026-11-06T14:30:00Z","duration":"30d","reason":"Incident 1042"}' \
  https://vms.example.com/api/v1/evidence-locks
```

### POST /api/v1/evidence-locks/{id}/remove

Rimuove un blocco. È richiesto un motivo.

**Autorizzazione:** Riprodurre i filmati registrati, e le stesse regole della rimozione di un blocco nell’interfaccia web.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"reason":"Case closed"}' \
  https://vms.example.com/api/v1/evidence-locks/00000000-0000-0000-0000-00000000000c/remove
```

### POST /api/v1/evidence-locks/{id}/restore

Ripristina un blocco rimosso durante il suo periodo di tolleranza.

**Autorizzazione:** Riprodurre i filmati registrati, e le stesse regole del ripristino di un blocco nell’interfaccia web.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/evidence-locks/00000000-0000-0000-0000-00000000000c/restore
```

## Non presente nell’API

Questi non sono disponibili per le chiavi API:

- Il video dal vivo. Usi l’interfaccia web, o le immagini qui sopra.
- La riproduzione diretta dei filmati. Usi un’esportazione.
- Impostazioni, utenti, ruoli, chiavi API, cifratura e backup.
- La configurazione delle telecamere, come zone, pianificazioni e impostazioni del dispositivo.
