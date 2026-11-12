# Avvisi, notifiche ed e-mail

Regole di avviso, notifiche agli utenti, canali del team e configurazione del provider e-mail.

## Regole di avviso

**Impostazioni → Avvisi** contiene le regole condivise. Ogni regola osserva una condizione:

- **Telecamera non risponde**: la telecamera ha smesso di inviare report di stato.
- **Nodo offline**: il nodo ha smesso di fare check-in.
- **Spazio di archiviazione scarso**: lo spazio libero è sceso sotto una percentuale.
- **Failover del nodo**: le telecamere di un nodo sono passate al suo nodo di riserva.
- **Movimento rilevato**: movimento su una telecamera, o in una delle sue zone di movimento.
- **Oggetto rilevato**: il rilevamento IA ha visto un oggetto corrispondente.
- **Velocità oltre un limite**: un oggetto ha attraversato un [controllo di velocità](/Help/ai-detection#speed-traps) a una velocità pari o superiore a una data.
- **Blocco delle prove in scadenza** e **Blocco delle prove rimosso**: vedere [blocchi delle prove](/Help/live-and-playback#evidence-locks).

Le regole di stato vengono controllate una volta al minuto. L’**intervallo di attesa** è il tempo minimo tra avvisi ripetuti, così una condizione che resta vera non avvisa ogni minuto.

### Avvisi di rilevamento

**Oggetto rilevato** e **Velocità oltre un limite** scattano entro pochi secondi dal rilevamento. Ogni filtro è facoltativo e tutti quelli che imposta devono corrispondere:

- **Telecamera**: una telecamera, le telecamere su un nodo o qualsiasi telecamera.
- **Categoria** ed **etichetta**: ad esempio Veicolo, o solo camion.
- **Colore**: il colore dell’oggetto, o il colore della maglia o dei pantaloni di una persona. Vedere [colori degli oggetti](/Help/ai-detection#object-colors).
- **Zona**: solo oggetti che si trovano all’interno di una delle zone della telecamera (giudicato dalla parte inferiore del riquadro).
- **Attivo**: giorni della settimana e fascia oraria, nel fuso orario di chi ha salvato la regola. Una fascia come 22:00–06:00 attraversa la notte.

Un rilevamento avvisa una sola volta. Su una regola che osserva più telecamere, l’intervallo di attesa è conteggiato separatamente per ciascuna telecamera, così una telecamera molto attiva non trattiene le altre.

### Avvisi di movimento

**Movimento rilevato** scatta entro pochi secondi dal movimento, dal rilevamento del movimento del server o dagli eventi di movimento della telecamera. Scelga una telecamera, le telecamere su un nodo o qualsiasi telecamera. Con una telecamera scelta, **Zona** lo limita a una delle sue zone **Movimento del server**; lasci **(tutta la telecamera)** per movimento ovunque. I giorni e orari **Attivo** funzionano come per gli avvisi di rilevamento. Un tratto di movimento avvisa una sola volta e l’intervallo di attesa trattiene il successivo.

### Canali del team

Ogni regola può anche inviare a elenchi e-mail, webhook, ntfy, Pushover, Slack e Teams. Attivi un canale e ne compili i campi.

## Notifiche

Le notifiche vanno a singole persone, nel loro browser o via e-mail, invece che a un canale del team. Ogni persona sceglie ciò che vuole in **Notifiche** nella barra laterale (anche il 🔔 accanto al Suo nome). Una notifica raggiunge qualcuno solo per le telecamere che può vedere.

- **Notifiche condivise**: su una regola di avviso, **Consenti agli utenti di iscriversi** la elenca in **Le mie notifiche → Disponibile**, dove chiunque può attivarla. Gli amministratori possono anche aggiungere un utente, o tutti i membri di un ruolo, in **Iscritti** sulla regola.
- **Le Sue notifiche**: **Le mie notifiche → Mie → Nuova notifica** ne crea una solo per Lei, con le stesse scelte di una regola di avviso. Ad esempio, animali sulla telecamera del giardino sul retro tra le 22 e le 6. Solo Lei la vede.
- **Silenzi** una notifica per un’ora, 8 ore, un giorno o finché non la riattiva, senza annullare l’iscrizione.

**Impostazioni → Notifiche** attiva o disattiva ogni tipo di notifica per gli utenti. Disattivare un tipo non ferma i canali del team delle regole di avviso.

Iscriversi richiede l’autorizzazione **Iscriversi alle notifiche condivise**, e crearne di proprie richiede **Creare notifiche personali**. Ogni ruolo le ha entrambe per impostazione predefinita.

### Notifiche del browser

Le attivi in ogni browser in **Le mie notifiche → Questo browser e dispositivi**, poi usi **Invia un test**. **Impostazioni → Notifiche** sceglie come vengono consegnate:

| Consegna | Funziona quando | Richiede |
|---|---|---|
| **Mentre LarisVMS è aperto** | È aperta una scheda LarisVMS o l’app installata | Nulla. Resta sulla Sua rete. |
| **Web Push** | Nessuna pagina LarisVMS aperta (Chrome, Edge, Firefox e Safari). Su un computer, il browser stesso deve essere in esecuzione; vedere sotto | **Genera chiavi Web Push** una volta, e accesso a Internet in uscita dal server verso i servizi push dei produttori di browser. Nessun account. |
| **Firebase Cloud Messaging** | Nessuna pagina LarisVMS aperta, con lo stesso limite sui computer | Il Suo progetto Firebase: la configurazione dell’app web, il certificato web push e una chiave dell’account di servizio. Usato solo quando Web Push è disattivato. |

Senza l’autorizzazione a mostrare le notifiche, compaiono invece all’interno delle pagine LarisVMS aperte. Su un telefono, installi prima LarisVMS come app (vedere [la panoramica](/Help/overview)).

I telefoni ricevono le notifiche push anche con il browser chiuso. Su un computer, un browser le riceve solo mentre è in esecuzione, e chiudere la sua ultima finestra di solito lo chiude; ciò che viene inviato nel frattempo compare al successivo avvio. Per mantenerlo in esecuzione in background, attivi **Continua a eseguire le app in background quando Google Chrome è chiuso** (Chrome: Impostazioni → Sistema) o **Continua a eseguire estensioni e app in background quando Microsoft Edge è chiuso** (Edge: Impostazioni → Sistema e prestazioni). Usi l’e-mail per chi ha bisogno di avvisi senza un browser in esecuzione.

### Notifiche e-mail

Le persone possono anche scegliere l’e-mail per una notifica. Va all’indirizzo del loro account, tramite l’e-mail configurata qui sotto. **Impostazioni → Notifiche** può disattivare le notifiche e-mail.

## E-mail

Avvisi e notifiche e-mail vengono inviati tramite il provider configurato in **Impostazioni → E-mail**. **Salva e invia test** verifica le impostazioni salvate.

- **SMTP**: host, porta, SSL/TLS e credenziali.
- **Microsoft Graph**: registri un’app in Entra ID, le conceda l’autorizzazione di **applicazione** `Mail.Send` (non delegata) con il consenso dell’amministratore, e inserisca il suo ID tenant, l’ID client e il segreto. Non c’è accesso per utente.
- **Gmail**: crei un client OAuth di tipo *Applicazione web* in Google Cloud. Aggiunga `https://<il-suo-host>/Admin/OAuthCallback` come URI di reindirizzamento autorizzato e abiliti lo scope `https://mail.google.com/`. Poi usi **Salva e connetti a Google** una volta per concedere il consenso.

I segreti salvati sono cifrati. Se le chiavi di protezione dei dati del server vanno perse, li inserisca di nuovo e riconnetta Gmail.
