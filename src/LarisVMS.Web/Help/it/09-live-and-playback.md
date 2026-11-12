# Visualizzazione dal vivo e riproduzione

Viste, riquadri dal vivo, timeline di riproduzione, esportazioni, segnalibri, blocchi delle prove, istantanee, attività e mappa.

## Viste

Una **vista** è un layout di telecamere salvato. Crei le viste in **Viste** trascinando e ridimensionando i riquadri. **Dal vivo** e **Riproduzione** mostrano entrambe una vista. Sui telefoni il layout si riduce a una o due colonne. Le viste possono girare a schermo intero in modalità kiosk, o ruotare tra più viste con un timer. Una vista condivisa può essere usata da tutti coloro che vedono le viste, ma solo il suo proprietario o un amministratore può modificarla o eliminarla.

## Riquadri dal vivo

- Faccia doppio clic su un riquadro per lo schermo intero. A schermo intero, scorra o pizzichi per ingrandire e trascini per spostare.
- I controlli di un riquadro compaiono quando ci passa sopra o quando li raggiunge con Tab. Vedere [Accessibilità](/Help/accessibility).
- Ogni riquadro con audio ha i propri controlli di disattivazione audio e volume. I riquadri partono sempre senza audio.
- **Streaming adattivo** (in **Impostazioni → Visualizzazione dal vivo**) mostra ai riquadri piccoli il flusso secondario e passa al principale quando il riquadro è grande o a schermo intero. Ogni riquadro ha anche una scelta manuale Auto/HD/SD.
- I badge mostrano movimento e oggetti rilevati. **Impostazioni → Eventi** ne imposta colori e angolo.
- Gli interruttori IA della barra degli strumenti disegnano un riquadro attorno a ogni oggetto rilevato: **In movimento**, **Fermo** o entrambi. **Confidenza** aggiunge il punteggio di ogni riquadro, e **Velocità** e **Colore** (attivi per impostazione predefinita) aggiungono la sua velocità, una volta cronometrata da un controllo di velocità, e il suo colore. **Percorso** (con In movimento attivo) disegna la linea seguita da ogni oggetto in movimento dal centro del suo riquadro, nel colore del riquadro. La linea resta finché l’oggetto continua a muoversi. Una volta che si ferma o se ne va, la linea resta per il tempo **Persistenza del percorso** (**Impostazioni → Visualizzazione dal vivo**, 5 secondi per impostazione predefinita), poi si dissolve dall’inizio nel tempo **Dissolvenza del percorso** (stessa pagina, 3 secondi per impostazione predefinita).
- Le telecamere PTZ hanno un pad direzionale e lo zoom.
- 🔴 in basso a sinistra significa che i filmati della telecamera vengono conservati in questo momento, per qualsiasi motivo. Le telecamere in modalità Continua lo mostrano sempre. Può servire fino a 20 secondi perché cambi dopo un movimento o un evento.
- Ⓜ️ accanto significa che è attiva una [registrazione manuale](/Help/recording#manual-recording). Ⓜ️ da solo significa che la registrazione è attiva, ma la telecamera è offline e non viene salvato nulla.
- Il pulsante ⏺ avvia una registrazione manuale e ⏹ la interrompe. In una vista con altre telecamere, chiede se intende questa telecamera o tutte. Chiunque abbia l’autorizzazione può interrompere una registrazione, chiunque l’abbia avviata.

## Parlare tramite una telecamera

Le telecamere con altoparlante mostrano un pulsante 🎙️ sul loro riquadro dal vivo. Lo tenga premuto (o lo selezioni e tenga premuto Spazio) per parlare e rilasci per smettere. **▾** accanto sceglie il microfono: un auricolare, una webcam o qualsiasi altro ingresso che il browser possa usare.

- Il browser chiede l’accesso al microfono la prima volta. Lo consente solo quando il sito è aperto tramite **HTTPS**; vedere [Certificati](/Help/security#certificates).
- Una sola persona alla volta può parlare tramite una telecamera.
- Serve l’autorizzazione **Parla** sulla telecamera (**Impostazioni → Accesso alle telecamere**). La telecamera deve supportare l’audio bidirezionale ONVIF (un’uscita audio nel suo profilo ONVIF). Le telecamere Dahua, Amcrest, Lorex ed EmpireTech il cui firmware non lo offre ripiegano sull’API audio della telecamera stessa.
- Tutto ciò che viene detto è registrato. Il microfono della telecamera stessa fa parte della registrazione video come al solito, e ogni pressione del pulsante viene salvata insieme ai filmati. Le pressioni compaiono sulla timeline di riproduzione in verde acqua e vengono riprodotte in sincronia con il video, con il volume del riquadro. Le esportazioni le includono, mescolate all’audio della telecamera. Sono conservate quanto i filmati della telecamera.
- Ogni sessione viene scritta nel registro di audit.

## Timeline

La riproduzione ha due timeline: una per la telecamera selezionata e una combinata per tutte le telecamere. Entrambe sono colorate per movimento, rilevamenti, tag evento e audio bidirezionale.

- Scorra per ingrandire (da settimane a secondi), trascini per spostare e faccia clic per portare ogni riquadro a quel momento.
- Faccia clic sull’ora sopra la timeline per digitare un’ora esatta.
- I tasti freccia avanzano di un secondo in pausa; Maiusc+freccia avanza di dieci.
- Velocità fino a 8× si riproducono in modo fluido. Velocità superiori scorrono i filmati come una presentazione.
- Passi sopra la timeline per un’anteprima in miniatura.
- I tratti con movimento o un evento sono a strisce, così non dipendono solo dal colore.
- Anche i pulsanti − e + accanto all’ora ingrandiscono, e la timeline funziona da tastiera. Vedere [Accessibilità](/Help/accessibility).

La Sua posizione e lo zoom vengono ricordati tra una visita e l’altra.

## Esportazioni

**Esporta…** in Riproduzione crea un file video per ogni telecamera selezionata su un intervallo di tempo. Le esportazioni vengono eseguite in background e compaiono nella pagina **Esportazioni**, dove possono essere scaricate, ritentate o eliminate. Un’esportazione copre fino a 64 telecamere, 31 giorni e 1.000 ore-telecamera in tutto (4 telecamere per una settimana sono 672); per di più, ne faccia diverse. Le esportazioni completate vengono conservate per 7 giorni, poi rimosse insieme ai loro file.

- **✏ Oscura** accanto a una telecamera oscura aree nell’esportazione di quella telecamera soltanto, come la finestra di un vicino o un passante. Disegni rettangoli (trascinando) o poligoni (faccia clic su ogni vertice; faccia clic sul primo punto, doppio clic o prema Invio per terminare; il clic destro rimuove l’ultimo punto) su un fotogramma dell’ora di inizio dell’esportazione. I filmati stessi non cambiano.
- Un’esportazione oscurata viene ricodificata, quindi richiede più tempo di una normale. È segnata **Oscurata** nella pagina Esportazioni e il registro di audit annota che delle aree sono state oscurate.
- Le [maschere di privacy](/Help/motion) di una telecamera sono già parte delle sue registrazioni, quindi ogni esportazione le include.
- Qualsiasi audio bidirezionale detto tramite la telecamera durante l’intervallo viene mescolato nell’audio dell’esportazione.

### Verificare un’esportazione

Ogni segmento registrato riceve un’impronta (SHA-256) mentre viene scritto, e i segmenti di ciascuna telecamera sono collegati in una catena: ogni anello copre l’impronta del suo segmento e l’anello precedente, quindi modificare, rimuovere o riordinare i filmati registrati rompe la catena. Una volta all’ora il server firma a che punto è arrivata la catena di ogni telecamera.

Ogni esportazione completata riceve anche un **🔏 Manifest** firmato nella pagina Esportazioni. Registra l’impronta del file esportato, i filmati sorgente e il loro posto nella catena, e se ogni file sorgente corrispondeva ancora alla sua impronta al momento dell’esportazione.

Per verificare un’esportazione, apra **Esportazioni → Verifica un’esportazione** e scelga il manifest e il video. La verifica viene eseguita nel browser e non viene caricato nulla. Conferma che:

- il manifest è stato firmato da questo server e non è stato modificato;
- il video è esattamente il file esportato;
- i filmati sorgente non erano cambiati dopo la registrazione e si collegano a un checkpoint firmato.

Conservi il manifest con il video quando consegna un’esportazione. Chiunque può verificare la firma senza LarisVMS: salvi il `publicKeyPem` del manifest in `key.pem`, il suo testo `signedPayload` in `payload.txt` e il suo `signatureDer` decodificato da base64 in `sig.der`, poi esegua `openssl dgst -sha256 -verify key.pem -signature sig.der payload.txt`. I filmati registrati prima di questa funzione non hanno impronte, quindi si può verificare solo l’esportazione stessa.

La firma è sempre attiva. La chiave di firma del server, con un download della sua chiave pubblica, è in **Impostazioni → Cifratura e firma**.

## Segnalibri

Segni un momento durante la Riproduzione con una nota. **Segnalibri** li elenca tutti, con un link al momento esatto. I segnalibri sono condivisi con tutti coloro che possono vedere la riproduzione e scadono quando i loro filmati scadono.

## Blocchi delle prove

Un blocco delle prove impedisce che i filmati vengano eliminati dalla conservazione. In Riproduzione, faccia clic su **🔒 Blocca…**, scelga le telecamere e inizio e fine, scelga per quanto tempo conservare (1, 3, 7, 14 o 30 giorni, fino a una data, o indefinitamente) e indichi un motivo. I tratti bloccati compaiono come una banda viola lungo la parte alta della timeline. Un blocco copre fino a 7 giorni di filmati.

- **I filmati bloccati non vengono mai eliminati**, né dalla conservazione, né dalle quote, né dalla scadenza dell’archivio, e nemmeno quando il disco è pieno. Se un registratore si riempie di filmati bloccati, la sua voce in Nodi mostra **pieno di filmati bloccati** e la registrazione si ferma quando il disco è pieno. Termini o rimuova i blocchi, esporti i filmati o aggiunga spazio.
- **Blocchi lunghi**: qualsiasi cosa oltre 30 giorni mostra un avviso. Quando l’archiviazione è scarsa, è meglio esportare il video (**Esporta invece questo intervallo**) e conservarlo altrove.
- **Rimuovere un blocco**: **Blocchi delle prove** elenca ogni blocco con dimensione e scadenza. La persona che ha posto un blocco, o un Super Admin, può rimuoverlo con un motivo. I filmati restano protetti per altre **24 ore** nel caso la rimozione fosse un errore, e **Annulla rimozione** ripristina il blocco fino ad allora.
- **Registrato**: posizionamento, rimozione, ripristino e scadenza di un blocco sono tutti scritti nel registro di audit.

Una telecamera con filmati ancora sotto blocco non può essere eliminata. Posizionare blocchi richiede l’autorizzazione **Blocca i filmati**, che i ruoli che possono esportare hanno per impostazione predefinita. Le [notifiche](/Help/alerts#notifications) possono avvisarla quando un blocco sta per terminare o è stato rimosso.

## Istantanee

**Istantanee** elenca ogni rilevamento e tag evento come miniatura, con filtri per telecamera, gruppo, vista, tipo di oggetto e ora. Il semplice movimento appare solo sulla timeline. ▶ apre il momento in Riproduzione.

## Attività

**Attività** mostra quando le cose accadono su una telecamera, o su tutte le telecamere insieme, come mappa di calore per ora del giorno. Scelga la telecamera (o **Tutte le telecamere**) e un intervallo: 24 ore, 7, 30 o 90 giorni, o un anno.

- **Settimana tipo** (predefinita): le righe sono i giorni della settimana e le colonne le ore, sommati sull’intervallo, quindi una cella scura è un momento regolarmente affollato, come il sabato dalle 18 alle 19. Su più di una settimana, passare sopra una cella dà anche la media per giorno (ad esempio «8,8 per sabato in media»).
- **Per giorno** (da 30 giorni in su): ogni data va da sinistra a destra con le ore dall’alto in basso, e una striscia di totali giornalieri sotto, così può individuare giorni particolari.
- Il giorno più affollato dell’intervallo e l’ora più affollata sono nominati sopra il grafico.
- Per le ultime 24 ore c’è una riga per ogni tipo di evento.
- **Tipi di evento** restringe a ciò che Le interessa: un intero tipo (Animale) o solo alcune sue etichette (Animale → gatto). Il semplice movimento è disattivato per impostazione predefinita perché non sommerga gli oggetti. La Sua scelta viene ricordata e rimane quando cambia telecamera.
- Passi sopra una cella per il suo conteggio esatto, oppure attivi **Mostra come tabella** per i numeri.
- Gli orari sono nel fuso orario della telecamera (impostato sulla telecamera), o in quello del server se non ne ha uno. Tutte le telecamere usa il fuso orario del server.
- La cronologia delle attività è conservata per 400 giorni (impostazione `Activity.HistoryDays`), anche dopo che i filmati stessi sono stati eliminati, quindi gli intervalli più lunghi mostrano l’intero periodo. La cronologia si accumula da quando questa funzione è stata installata: gli eventi già eliminati insieme ai loro filmati non possono essere recuperati.
- La vista è nell’indirizzo della pagina, quindi può aggiungerla ai preferiti o condividerla. Il widget **Attività della telecamera** della dashboard mostra gli ultimi 7 giorni per una telecamera o per tutte.

### Scena

**Scena** mostra *dove* nell’immagine di una telecamera le cose si muovono: uno strato di calore su un fotogramma recente a dimensione piena dalle registrazioni della telecamera. Il rosso è dove accade di più, poi arancione, giallo, verde e blu per pochissimo. I punti senza nulla restano chiari.

- **Oggetti in movimento** conta quanti oggetti sono passati per ogni punto. Lo restringa a **Persone**, **Veicoli**, **Animali** o **Altri**.
- **Movimento** mostra dove il rilevamento del movimento lato server della telecamera ha visto movimento, per le telecamere che lo usano in modalità **Griglia**.
- Scelga un intervallo e, facoltativamente, un’**Ora del giorno** (ad esempio dalle 22 alle 6 per le notti).
- Il calore viene sommato una volta all’ora (il movimento ogni 10 minuti) e conservato quanto il resto della cronologia delle attività. I percorsi che ne sono alla base sono conservati con i loro eventi, quindi se ne vanno quando se ne vanno i filmati.
- Scelga una telecamera, o una **vista** (elencata in **Viste** nell’elenco delle telecamere) per vedere affiancata una mappa di calore per ogni telecamera al suo interno. Ogni telecamera ha la propria scala, quindi una telecamera tranquilla mostra comunque dove c’è la sua attività. Non può essere mostrata per tutte le telecamere insieme.
- **Sfondo**: se può configurare la telecamera, può sostituire il suo fotogramma con una Sua immagine (ad esempio uno scatto diurno nitido) con **Usa una mia immagine…**. Deve provenire dalla stessa telecamera con la stessa inquadratura, a piena larghezza o della stessa forma, perché il calore si allinei. **Usa l’immagine della telecamera** torna al fotogramma automatico.
- **Reimposta la mappa di calore**: dopo che una telecamera è stata spostata o riorientata, il suo vecchio calore non si allinea più con l’immagine. Se può configurare la telecamera, **Reimposta la mappa di calore** lo cancella e conta solo ciò che accade da allora; la scheda mostra quando è iniziato il conteggio.

## Mappa

**Mappa** (sotto Dal vivo) mostra dove sono le Sue telecamere e cosa guardano. Ogni telecamera è un’icona con un cono per il suo campo visivo: la direzione verso cui punta, quanto ampia vede e quanto lontano. Il colore ne mostra lo stato: verde è in registrazione, rosso è offline, grigio è disattivata. Faccia clic su una telecamera per la sua ultima immagine, con link a Dal vivo e Riproduzione. Gli stati si aggiornano ogni 30 secondi. La mappa si riapre dove l’ha lasciata, separatamente per ogni persona e ogni mappa.

Ci sono due tipi di mappa, e può averne quante vuole di ciascuno:

- **Mappa stradale**: una normale mappa della zona (OpenStreetMap per impostazione predefinita). La portata è in metri.
- **Planimetria**: un’immagine che carica (PNG, JPEG, WebP o GIF, fino a 10 MB), come una pianta di edificio o un disegno del sito. La portata è in pixel di quell’immagine.

Le persone che possono modificare le telecamere possono configurare le mappe:

1. **Nuova mappa → Mappa stradale** o **Planimetria**. Una nuova mappa stradale si apre dove è mostrata quella attuale, oppure sul mondo intero; sposti e ingrandisca fino al Suo sito. Quando preme **Termina modifica**, la mappa viene salvata per aprirsi dove è mostrata, per tutti; **Gestisci → Apri questa mappa qui per tutti** fa lo stesso senza modificare. Funziona anche per le planimetrie.
2. **Modifica mappa**, poi trascini le telecamere da **Telecamere non su questa mappa** sulla mappa. Oppure faccia clic su una per collocarla a metà mappa.
3. Faccia clic su una telecamera per selezionarla. Trascini l’icona per spostarla e trascini la maniglia bianca alla punta del suo cono per orientarla (direzione e portata). Il pannello laterale imposta il campo visivo, o direzione e portata esatte, e **Togli da questa mappa** la rimuove. Le modifiche si salvano man mano. I pulsanti freccia nel pannello la spostano senza trascinare.

Una telecamera può essere su più mappe, come una mappa stradale e la planimetria del suo edificio. Le persone vedono solo le telecamere che sono autorizzate a vedere.

Le immagini della mappa stradale sono caricate dal browser dal server di tile, quindi una mappa stradale richiede l’accesso a Internet dal browser. Per un sito offline, o un altro stile di mappa, imposti un proprio server di tile https in **Gestisci → Tile della mappa stradale**, e la sua attribuzione (i termini della maggior parte dei server di tile richiedono che sia mostrata sulla mappa). Modificarlo richiede anche l’autorizzazione di modifica delle impostazioni, dato che si applica all’intero server.
