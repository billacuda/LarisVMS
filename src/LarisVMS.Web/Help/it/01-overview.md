# Panoramica

Come si integrano le parti di LarisVMS: il server web, i nodi di registrazione, i proxy multimediali e le telecamere.

## Le componenti

| Componente | Cosa fa |
|---|---|
| **Server web** | L’applicazione web LarisVMS. Memorizza la configurazione in SQL Server o MariaDB, serve l’interfaccia e l’API e inoltra il video dal vivo e in riproduzione a meno che lo streaming diretto sia attivo. Viene eseguito come servizio Windows *LarisVMS Web*. |
| **Nodo di registrazione** | Un servizio Windows su ogni macchina di registrazione. Preleva l’RTSP dalle sue telecamere, scrive segmenti nel suo percorso di archiviazione, applica la conservazione, esegue il rilevamento del movimento e IA e segnala il proprio stato. Un server può avere molti nodi. |
| **Servizio di visione** | Processo figlio facoltativo di un nodo che esegue il rilevamento di oggetti IA sulla GPU (o CPU) del nodo. Un arresto anomalo qui non ferma mai la registrazione. |
| **Proxy multimediale** | Relay facoltativo tra browser e nodi, per siti remoti o collegamenti a banda ridotta. |
| **Telecamere** | Telecamere ONVIF. Ciascuna è assegnata a un nodo di registrazione. |

## Come scorre il video

1. Un nodo apre una sessione RTSP verso ciascuna delle sue telecamere e registra con `ffmpeg -c copy`, quindi non avviene alcuna ricodifica.
2. I filmati vengono scritti come brevi file di segmenti (vedere [Registrazione](/Help/recording)) nel percorso di archiviazione del nodo.
3. La visualizzazione dal vivo e la riproduzione sono servite dal nodo e inoltrate attraverso questo server, un proxy multimediale oppure direttamente al browser (vedere [Nodi](/Help/nodes#direct-streaming)).

## Privacy

LarisVMS è self-hosted: il Suo video e le Sue impostazioni restano sulle Sue macchine e nulla sulla Sua installazione viene raccolto o condiviso. Non c’è telemetria né comunicazione verso l’esterno. L’unica richiesta in uscita di routine è un controllo giornaliero delle release GitHub di LarisVMS per una nuova versione (vedere [Aggiornamenti](/Help/installation#updates)); può essere disattivato. L’altro download integrato è un modello IA YOLOX dalle release GitHub di quel progetto, la prima volta che il rilevamento ne usa uno, a meno che Lei metta i file sul server direttamente. Le mappe stradali della pagina [Mappa](/Help/live-and-playback#map) caricano le loro immagini nel browser da OpenStreetMap, o da un server di tile a Sua scelta; le planimetrie no. Tutto il resto va solo alle Sue telecamere, ai Suoi server e ai servizi che configura, come e-mail e canali di avviso.

## Ereditarietà delle impostazioni

La maggior parte delle impostazioni esiste su tre livelli. Vince il più specifico:

1. **Telecamera**: nella pagina di modifica della telecamera.
2. **Nodo**: nella pagina di modifica del nodo, in **Nodi**.
3. **Globale**: nella pagina **Impostazioni** corrispondente.

Un campo vuoto (o un’opzione «eredita — …») usa il livello superiore e mostra il valore che sta ereditando.

## Dashboard

La dashboard è composta da widget. Ogni utente sceglie i propri e il layout viene salvato nel suo account, quindi lo segue su altri browser.

- **Personalizza** (in alto a destra; nascosto quando la finestra è troppo stretta per la griglia completa) sblocca il layout. Trascini un widget dalla barra del titolo, lo ridimensioni dall’angolo oppure lo rimuova con ×. **Aggiungi widget** elenca tutto ciò che può aggiungere e **Reimposta layout** torna a quello predefinito. Faccia clic su **Fatto** quando ha finito.
- Durante la personalizzazione, ogni widget ha pulsanti per spostarlo e ridimensionarlo senza trascinare.
- **Metti in pausa gli aggiornamenti** impedisce ai widget di cambiare mentre li legge.
- La maggior parte dei widget sono classifiche top 10 di telecamere o nodi, come archiviazione usata, frequenza fotogrammi, larghezza di banda, filmati più vecchi, telecamere offline, tempo dall’ultima registrazione e rilevamenti. Alcuni hanno una finestra temporale (1 h, 24 h, 7 g, 30 g) e alcuni possono alternare tra più alti e più bassi per primi.
- Faccia clic sul titolo di un widget per aprire il suo report completo: ogni telecamera o nodo, 100 per pagina, con una casella di filtro, un filtro per nodo, colonne ordinabili e un download CSV.
- **Attività della telecamera** mostra gli ultimi 7 giorni di una telecamera come mappa di calore per ora del giorno; scelga la telecamera nella sua barra del titolo e faccia clic sul titolo per la pagina completa [Attività](/Help/live-and-playback#activity).
- **Ultimi rilevamenti** mostra le istantanee più recenti. Il suo pulsante ⚙ sceglie quali tipi di rilevamento includere; il semplice movimento (nulla di identificato) è disattivato a meno che lo selezioni.
- **Tempo dall’ultima registrazione** segnala una telecamera impostata sulla registrazione continua che non registra da più di 5 minuti.
- I widget dei nodi basati sulle telecamere (conteggio telecamere, telecamere offline, frequenza fotogrammi, tempo di archiviazione rimasto) omettono i nodi senza telecamere.
- Vede solo le telecamere a cui ha accesso. **Avvisi recenti** è offerto solo agli utenti che possono gestire le regole di avviso.

I widget si aggiornano ogni minuto.

## Su telefono

L’intero sito funziona nel browser di un telefono. La barra laterale si apre dal pulsante ☰ in alto a sinistra.

Per installarlo come app, apra il sito in Chrome su Android e scelga **Installa app** dal menu ⋮. Su iOS lo apra in Safari e scelga **Condividi → Aggiungi alla schermata Home**. L’app si apre a schermo intero, senza la barra degli indirizzi del browser.

L’installazione richiede un certificato considerato attendibile dal telefono. I browser non installano un sito che usa il certificato autofirmato con cui parte una nuova installazione; vedere [Certificati](/Help/security#certificates).

## Da dove iniziare

- Nuova installazione: [Installazione](/Help/installation)
- Aggiungere telecamere: [Telecamere](/Help/cameras)
- Scegliere cosa conservare: [Registrazione](/Help/recording) e [Archiviazione e conservazione](/Help/storage)
- Collegare i propri strumenti: [API](/Help/api)
- Usare un’altra lingua: [Lingue](/Help/languages)
- Tastiera e screen reader: [Accessibilità](/Help/accessibility)
- Qualcosa non va: [Risoluzione dei problemi](/Help/troubleshooting)
