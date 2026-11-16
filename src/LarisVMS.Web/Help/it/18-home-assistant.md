# Home Assistant

Mostri le Sue telecamere, i rilevamenti e i nodi di registrazione in Home Assistant, e da lì avvii registrazioni o aggiunga segnalibri.

> **Sperimentale.** Il supporto per Home Assistant è nuovo, e Home Assistant viene usato per molti scopi diversi. [Ci racconti come funziona per Lei](https://github.com/billacuda/LarisVMS/discussions/new?category=general&title=Home%20Assistant%20feedback): per cosa lo usa, cosa funziona bene e cosa non va.

## Come funziona

LarisVMS invia al broker MQTT che Home Assistant usa già. Il rilevamento MQTT di Home Assistant aggiunge poi un dispositivo per ogni telecamera e ogni nodo di registrazione, senza scrivere YAML.

Home Assistant agisce con un ruolo che sceglie Lei, proprio come una chiave API:

- Riceve solo le telecamere che quel ruolo può vedere in **Impostazioni → Accesso alle telecamere**.
- Può fare solo ciò che quel ruolo consente. Per esempio, l’interruttore di registrazione compare solo se il ruolo ha **Avviare e interrompere la registrazione manuale** e l’accesso **Registra** alle telecamere.
- Ciò che fa compare nel registro di audit come **Home Assistant**.

## Configurazione

1. In Home Assistant, installi il componente aggiuntivo **Mosquitto broker** e l’integrazione **MQTT**, se non li usa già. Crei un accesso al broker per LarisVMS, per esempio un utente di Home Assistant chiamato `larisvms`.
2. In LarisVMS, crei un ruolo per Home Assistant in **Impostazioni → Autorizzazioni → Ruoli**. Gli assegni **Vedere le telecamere** e l’accesso alle telecamere desiderate in **Impostazioni → Accesso alle telecamere**. Aggiunga **Avviare e interrompere la registrazione manuale**, oppure **Riprodurre i filmati registrati** e **Creare segnalibri sui filmati registrati**, se Home Assistant deve poter registrare o aggiungere segnalibri.
3. Apra **Impostazioni → Home Assistant** e lo attivi. Inserisca l’indirizzo del broker (quello del Suo Home Assistant se usa il componente aggiuntivo), la porta (di solito `1883`), l’accesso e il ruolo. Selezioni **Prova connessione**, poi **Salva**.
4. In Home Assistant, apra **Impostazioni → Dispositivi e servizi → MQTT**. Le Sue telecamere compaiono lì come dispositivi.

Lo stato in **Impostazioni → Home Assistant** indica se LarisVMS è connesso e quante telecamere invia. Se il broker si riavvia o la rete cade, LarisVMS si riconnette da solo. Quando Home Assistant si riavvia, LarisVMS invia di nuovo tutto.

## Cosa compare in Home Assistant

Ogni telecamera riceve:

| Entità | Mostra |
|---|---|
| **Movimento** | Attivo finché c’è movimento o un rilevamento |
| **Persona**, **Veicolo**, **Animale** | Attivo finché viene rilevato quel tipo di oggetto |
| **Online** | Se la telecamera sta inviando video |
| **Rilevamento più recente** | Un’immagine dell’ultimo oggetto rilevato |
| **Ultimo rilevamento**, **Ultimo evento** | Cosa è stato rilevato per ultimo, e quando |
| **Ultima velocità** | L’ultima velocità misurata dalle [trappole di velocità](/Help/ai-detection), nelle unità di **Impostazioni → Rilevamento IA** |
| **Registrazione manuale** | Un interruttore che avvia e interrompe una registrazione manuale, se il ruolo lo consente |
| **Aggiungi segnalibro** | Un pulsante che aggiunge un segnalibro all’ora attuale, se il ruolo lo consente |

Ogni nodo di registrazione riceve **Online** e **Spazio libero**.

Un sensore si attiva uno o due secondi dopo un rilevamento e si disattiva circa 25 secondi dopo la sua fine. È lo stesso comportamento dell’indicatore di movimento in **Dal vivo**.

## Video live

La **Generic Camera** di Home Assistant può mostrare il video live di LarisVMS. Usa lo stesso flusso delle pagine web, quindi la telecamera non deve fornirne un secondo.

1. Assegni al ruolo di Home Assistant l’autorizzazione **Trasmettere il video live ad altri programmi**. Nessun ruolo ce l’ha finché non la assegna.
2. In **Admin → Chiavi API**, aggiunga una chiave legata a quel ruolo.
3. In Home Assistant, aggiunga una **Generic Camera** per ogni telecamera. **Impostazioni → Home Assistant** elenca l’**URL immagine fissa** e la **Sorgente del flusso** di ogni telecamera. Scelga l’autenticazione **Basic**, inserisca `apikey` come nome utente e la chiave come password.

L’[API](/Help/api) descrive il flusso.

## Avvisi tramite webhook

Il webhook di una regola di avviso può inviare anche a Home Assistant. In Home Assistant, crei un’automazione con un attivatore **Webhook**, poi inserisca il suo indirizzo nel webhook della regola, per esempio `http://homeassistant.local:8123/api/webhook/larisvms-front-door`.

Il webhook invia JSON con la regola, il messaggio e l’ora, e, quando l’avviso li ha, l’evento, la telecamera, il nodo, l’oggetto rilevato, la velocità e `snapshotPath`. Aggiunga `snapshotPath` al Suo indirizzo di LarisVMS per ottenere l’immagine del rilevamento con una chiave API.

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

## Se manca qualcosa

- **Non connesso:** controlli indirizzo, porta e accesso con **Prova connessione**. Un broker che richiede TLS di solito usa la porta `8883`.
- **Nessun dispositivo in Home Assistant:** controlli che l’integrazione MQTT sia configurata e che il prefisso di rilevamento corrisponda a quello di Home Assistant (`homeassistant`, a meno che non l’abbia cambiato).
- **Manca una telecamera:** il ruolo non può vederla, oppure la telecamera è disattivata.
- **Nessun interruttore di registrazione né pulsante del segnalibro:** al ruolo manca l’autorizzazione o l’accesso alla telecamera necessario.
- **Il webhook non arriva:** LarisVMS non invia webhook a `localhost`. Usi l’indirizzo di rete di Home Assistant, anche se gira sulla stessa macchina.
