# Telecamere

Aggiungere telecamere, l’elenco delle telecamere, i flussi, le impostazioni del dispositivo, i gruppi, i dispositivi multi-obiettivo e le integrazioni dei produttori.

## Aggiungere una telecamera

Usi **Telecamere → Rileva** per scansionare la LAN alla ricerca di dispositivi ONVIF, oppure **Telecamere → Aggiungi** per inserire l’URL del servizio del dispositivo (ad esempio `http://192.168.1.50/onvif/device_service`) e le credenziali. LarisVMS poi **sonda** la telecamera: legge le sue funzionalità ONVIF e i suoi flussi e sceglie i flussi principale e secondario.

Assegni un **nodo di registrazione**, altrimenti la telecamera non registrerà.

## L’elenco delle telecamere

**Telecamere** elenca ogni telecamera che può vedere. La riga sotto le intestazioni delle colonne filtra per qualsiasi colonna, come Marca, Modello, Nodo o Gruppo, e la casella di ricerca sopra la tabella filtra per testo ovunque in una riga. I filtri si combinano e **Cancella filtri** li reimposta. **Colonne** sceglie quali colonne mostrare. Un filtro su una colonna nascosta viene ignorato.

I Suoi filtri, colonne, ordinamento e dimensione di pagina sono salvati nel Suo account, quindi tornano quando accede di nuovo, su qualsiasi dispositivo.

Selezioni delle telecamere per agire su più di una alla volta: **Riassegna selezionate al nodo**, oppure **Impostazioni del dispositivo…** per telecamere della stessa marca e modello (vedere [Modifiche in blocco](#bulk-changes)). **Seleziona tutto** seleziona solo le telecamere che corrispondono ai filtri attuali.

## Nuovo sondaggio

**Sonda di nuovo** nella pagina di una telecamera rilegge le sue funzionalità e i suoi flussi. Lo faccia dopo aver cambiato risoluzione, codec o firmware della telecamera nella sua pagina web. Le modifiche fatte in [Impostazioni del dispositivo](#device-settings) provocano automaticamente un nuovo sondaggio. Cambiare l’URL del dispositivo provoca automaticamente un nuovo sondaggio al salvataggio. **Impostazioni → Impostazioni delle telecamere** può sondare di nuovo ogni telecamera una volta al giorno. Le telecamere vengono sondate una alla volta, così le telecamere economiche non perdono le sessioni di registrazione. I nomi dei flussi e gli indicatori di attivazione vengono conservati dopo un nuovo sondaggio.

## Flussi

Ogni telecamera di solito ha un flusso **principale** (risoluzione piena, usato per la registrazione) e un flusso **secondario** (risoluzione inferiore, usato per i piccoli riquadri dal vivo e il rilevamento IA). Espanda **Dispositivo** nella pagina della telecamera per rinominare o disattivare un flusso. Passi sopra un flusso per vederne l’URL RTSP. LarisVMS usa l’indirizzo con cui la telecamera è stata aggiunta, mantenendo la porta e il percorso che la telecamera segnala, così una telecamera che segnala un indirizzo interno (o qualcuno che la imita) non può indirizzare il registratore altrove con la sua password.

## Impostazioni del dispositivo

**Impostazioni del dispositivo** nella pagina di una telecamera modifica le impostazioni della telecamera stessa tramite ONVIF, quindi non deve accedere alla sua pagina web. Le impostazioni vengono lette dal vivo dalla telecamera e vengono mostrate solo quelle che supporta.

| Sezione | Impostazioni |
|---|---|
| **Flussi video** | Per ogni flusso: codec (H.264, H.265 o MJPEG), profilo di codifica, risoluzione, frequenza fotogrammi, modalità bitrate (CBR o VBR), bitrate, qualità e intervallo dei keyframe |
| **Immagine** | Luminosità, contrasto, saturazione e nitidezza; modalità giorno/notte; WDR; compensazione del controluce; modalità di esposizione; e bilanciamento del bianco |
| **Audio** | Codec, bitrate e frequenza di campionamento |

Ogni sezione ha il proprio pulsante **Applica**, e vengono inviate solo le impostazioni che ha modificato. Applicare una modifica di flusso o audio fa riconnettere il flusso, il che lascia un’interruzione di qualche secondo nella registrazione; LarisVMS poi sonda di nuovo la telecamera e il nodo di registrazione recepisce subito il nuovo formato. Le modifiche all’immagine hanno effetto senza riconnessione.

Alcune telecamere condividono un unico insieme di impostazioni dell’encoder tra più flussi. La pagina lo indica, perché cambiarne uno cambia anche l’altro.

H.265 e la scelta CBR/VBR richiedono una telecamera con ONVIF Profile T (Media2). Sulle telecamere più vecchie quelle opzioni non compaiono.

## Modifiche in blocco

Per modificare in una volta più telecamere della stessa marca e modello:

1. In **Telecamere**, filtri la colonna **Modello**, poi selezioni le telecamere (o **Seleziona tutto**).
2. Faccia clic su **Impostazioni del dispositivo…**. È disponibile solo quando tutte le telecamere selezionate hanno la stessa marca e lo stesso modello.
3. Modifichi ciò che serve e faccia clic su **Applica** in quella sezione.

Può anche partire dalla pagina **Impostazioni del dispositivo** di una telecamera e scegliere altre telecamere dello stesso modello in **Applica a**. Un badge *firmware diverso* segna le telecamere con firmware diverso, che possono offrire opzioni leggermente diverse.

La pagina mostra le impostazioni attuali della prima telecamera. A ogni telecamera vengono inviate solo le impostazioni che modifica, quindi le altre impostazioni di ciascuna telecamera restano invariate. I flussi sono abbinati per ruolo (principale, secondario o terzo). Ogni telecamera verifica la modifica rispetto alle proprie opzioni e l’elenco dei risultati mostra, per ciascuna telecamera, se è stata applicata, applicata in parte, saltata (ad esempio una telecamera senza terzo flusso) o non riuscita (ad esempio offline). **Riprova quelle non riuscite** invia di nuovo la modifica alle telecamere che non sono riuscite.

## Gruppi

I gruppi organizzano le telecamere in siti, edifici e piani. Un **gruppo di primo livello è un sito** e i gruppi al di sotto sono edifici o piani. Una telecamera può far parte di un numero qualsiasi di gruppi, purché appartengano tutti allo stesso sito. I gruppi servono per filtrare, per le sostituzioni delle impostazioni e per l’[accesso alle telecamere](/Help/users#camera-access). Il gruppo integrato *Tutte le telecamere* contiene sempre ogni telecamera.

## Multicanale

Alcuni dispositivi hanno più obiettivi in un unico alloggiamento. Quando un sondaggio trova più di una sorgente video, la pagina della telecamera offre **Dividi in N telecamere**. Ogni obiettivo diventa poi una telecamera a sé, con la propria registrazione e conservazione. La telecamera esistente diventa il canale 1 e conserva le sue registrazioni.

## Integrazioni

Alcune funzioni non sono disponibili tramite ONVIF. Le integrazioni (elencate in **Impostazioni → Plugin**) vengono abbinate automaticamente da marca e modello della telecamera durante un sondaggio. Non c’è nulla da attivare. Ad esempio, l’integrazione **Eventi intelligenti Dahua / Amcrest** legge i rilevamenti di persone e veicoli dall’analisi della telecamera stessa. Il pannello **Dispositivo** della telecamera mostra quando un’integrazione è attiva.
