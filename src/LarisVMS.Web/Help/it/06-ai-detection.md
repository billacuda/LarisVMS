# Rilevamento IA

Rilevamento di oggetti sul nodo: modelli, hardware, soglie, istantanee e backend esterno.

Ogni nodo può eseguire il rilevamento di oggetti in tempo reale sulle sue telecamere. Gli oggetti rilevati ottengono riquadri di delimitazione dal vivo, un segno colorato sulla timeline, un badge sul riquadro dal vivo e un’istantanea ritagliata. Un rilevamento conta anche come movimento per la registrazione in modalità Movimento. Il rilevamento viene eseguito in un processo separato, quindi un problema di GPU o driver non ferma mai la registrazione.

Lo attivi per telecamera con **Osserva gli oggetti su questa telecamera**. Bastano solo **5–7 fps per telecamera** per un rilevamento accurato, quindi limiti lì la [frequenza fotogrammi](#thresholds) per contenere il carico sulla GPU. I valori predefiniti globali sono in **Impostazioni → Rilevamento IA**. Nodi e telecamere possono sostituirne la maggior parte.

## Hardware

Un unico pacchetto del nodo copre ogni macchina. All’avvio, il servizio di visione sceglie un backend per l’hardware che trova:

| Hardware | Backend | Richiesto sul nodo |
|---|---|---|
| GPU NVIDIA | CUDA (ripiega su DirectML) | CUDA Toolkit 12.x e cuDNN 9.x |
| GPU AMD / Intel | DirectML | Un driver GPU aggiornato |
| Nessuna GPU | CPU | Nulla |

La pagina di modifica del nodo mostra gli acceleratori rilevati e la sua impostazione **Acceleratore** può forzarne uno. I modelli non sono mai inclusi. Un nodo preleva dal server i modelli YOLOX integrati, e gli altri modelli vanno in `C:\ProgramData\LarisVMS\models` sul nodo.

## Backend lento

Se un nodo NVIDIA non può usare CUDA, il rilevamento IA ripiega su DirectML, che è diverse volte più lento. Vengono analizzati meno fotogrammi, la GPU si scalda e oggetti veloci come le auto di passaggio possono non essere rilevati. La pagina **Nodi** mostra allora **⚠️ IA su DirectML** accanto al nodo, con il motivo, e i Super Admin ricevono una notifica.

La causa abituale è un cuDNN mancante. Installi CUDA Toolkit 12.x e cuDNN 9.x per CUDA 12 sul nodo, dal download di cuDNN di NVIDIA o con `pip install nvidia-cudnn-cu12`. Il nodo li trova entro 10 minuti, li copia accanto al suo backend IA e riavvia il rilevamento su CUDA.

## Famiglie di modelli

Il modello viene eseguito una volta per nodo e serve tutte le telecamere di quel nodo, quindi le impostazioni del modello sono per nodo, non per telecamera.

- **Auto / YOLOX**: il valore predefinito. Veloce, non richiede letterbox e funziona bene su GPU a basso consumo e non NVIDIA. **Dimensione YOLOX** va da Nano (il più veloce) a X (il più accurato). Usi Nano o Tiny su macchine a basso consumo e L o X su GPU potenti.
- **D-FINE**: sperimentale. I risultati possono essere imprevedibili e gli oggetti a cavallo di due tile in modalità Slice possono essere persi o duplicati. **Pesi**: Obj2Coco conosce 80 tipi di oggetto (piccolo o medio) e Obj365 ne conosce 365.
- **Personalizzato**: qualsiasi modello `.onnx` inserito nella cartella dei modelli del nodo. Vedere [Modelli personalizzati](#custom-models).

Cambiare il modello riavvia il rilevamento di ogni telecamera.

## TensorRT

**D-FINE TensorRT** fa qualcosa solo quando il nodo ha TensorRT attivato (`Vision:EnableTensorRt`) e l’SDK TensorRT 10.x installato. **FP32** offre un’accelerazione sicura. **FP16** è il più veloce; se il motore di una telecamera va in overflow in FP16, si ricostruisce automaticamente in FP32. Il primo avvio a ciascuna risoluzione di telecamera richiede diversi minuti per costruire il motore.

## Modelli personalizzati

Inserisca un file `.onnx` in `C:\ProgramData\LarisVMS\models` sul nodo. Se i metadati del modello descrivono il suo decodificatore, la dimensione di input e le etichette, funziona subito. Altrimenti aggiunga un file `.json` con lo stesso nome che fornisca ciò che manca. **Aggiorna** elenca ogni modello trovato dai nodi e segnala quelli con metadati incompleti. Un modello con un avviso può essere selezionato ma non verrà eseguito finché l’avviso non è risolto.

## Adattamento delle proporzioni

Come il fotogramma di una telecamera si adatta all’input quadrato del modello:

- **Letterbox** (predefinito): mantiene le proporzioni e riempie con barre nere.
- **Stira**: schiaccia il fotogramma per riempire il quadrato, il che deforma le forme.
- **Slice**: taglia il fotogramma in quadrati sovrapposti a risoluzione piena. È ideale per oggetti piccoli o distanti su telecamere grandangolari, ma costa circa 2× il lavoro su una telecamera 16:9. Richiede la pre-elaborazione su GPU e la attiva automaticamente.

La **pre-elaborazione dei fotogrammi su GPU** sposta la conversione dei colori dalla CPU alla GPU, il che aiuta sui nodi con molte telecamere.

## Soglie

- **Confidenza**: il punteggio minimo perché un rilevamento conti. Più basso rileva più oggetti ma aggiunge falsi positivi. La aumenti su una telecamera che rileva male qualcosa di continuo.
- **IoU**: quanto due riquadri devono sovrapporsi per essere uniti come un unico oggetto.
- **Flusso di rilevamento**: Secondario è più leggero; Principale vede più dettagli.
- **Orientamento**: alcune telecamere montate nei corridoi segnalano un flusso orizzontale ma inviano video verticale. Imposti **Verticale** su quelle telecamere perché rilevamento e istantanee non vengano schiacciati.
- **Frequenza fotogrammi di rilevamento massima**: fotogrammi al secondo per telecamera che raggiungono il modello. **5–7 fps bastano per un rilevamento e un tracciamento accurati.** Frequenze più alte aggiungono carico alla GPU senza migliorare i risultati. 0 è nessun limite.

## Istantanee

Ogni oggetto tracciato ottiene un’istantanea, ritagliata dal suo fotogramma migliore.

- **Timeout di inattività**: per quanto tempo un oggetto può restare non visto prima che la sua istantanea si chiuda.
- **Segnala gli oggetti fermi**: segnala anche gli oggetti presenti ma fermi, come un’auto parcheggiata.
- **Precisione del movimento delle istantanee**: chiude un’istantanea poco dopo che il suo oggetto se ne va, così un oggetto successivo dello stesso tipo non viene unito a essa. **Tolleranza di uscita** è quanto attendere. Se è troppo breve, un oggetto che si ferma viene diviso in due istantanee.
- **Margine di ritaglio dell’istantanea**: spazio extra attorno all’oggetto, come percentuale del suo riquadro.

Più oggetti dello stesso tipo in un’istantanea compaiono come un conteggio (×2). Ciascuno è inoltre conservato separatamente con i propri colori e la propria velocità.

## Colori degli oggetti

Il colore di ogni oggetto tracciato è letto dal suo fotogramma più nitido: un colore per veicoli, animali e altri oggetti, e colori di maglia e pantaloni per le persone. Le istantanee mostrano un badge per ciascuno, dipinto in quel colore, e le sezioni **Colore**, **Maglia** e **Pantaloni** del filtro Istantanee restringono i risultati ai colori che seleziona. Anche gli avvisi di rilevamento possono filtrare per colore.

I colori provengono dall’immagine, non dal modello, quindi funzionano con qualsiasi rilevatore. Anche i colori appena fuori dal riquadro vengono campionati e messi da parte, così il prato attorno a un cane o la strada attorno a un’auto non prevalgono. Restano comunque stime al meglio: scene affollate, oggetti sovrapposti e illuminazione possono dare il colore sbagliato. L’illuminazione conta di più: ombra, fari e bilanciamento del bianco alterano i colori, e un’auto grigio chiaro può risultare argento o bianca. Finché una telecamera è in modalità notte in bianco e nero (IR), non viene registrato alcun colore. Li disattivi con **Colori degli oggetti** in **Impostazioni → Rilevamento IA**.

## Controlli di velocità

Un controllo di velocità cronometra gli oggetti tra due linee attraverso una strada. Nella pagina **Controlli di velocità** della telecamera (da **Telecamere → Modifica**), faccia clic su **Nuovo controllo di velocità**, faccia clic sulle due estremità della linea **A**, poi sulle due estremità della linea **B**, e inserisca la distanza reale tra le linee, misurata lungo la strada. Ogni oggetto tracciato che attraversa entrambe le linee ottiene una velocità, mostrata sul suo riquadro dal vivo e sulla sua istantanea. **Direzione** cronometra in entrambi i sensi per impostazione predefinita; scelga **Solo da A a B** o **Solo da B ad A** per cronometrare una corsia di una strada a doppio senso. Le **Unità di velocità** (km/h o mph) sono in **Impostazioni → Rilevamento IA**.

Un oggetto cronometrato da un controllo viene registrato come in movimento anche se altrimenti sembra fermo, come un veicolo che va dritto verso la telecamera. Il cronometraggio usa il centro del lato inferiore di ciascun riquadro, dove l’oggetto tocca il suolo, quindi tracci le linee sulla superficie stradale. Per una buona misura:

- Tracci le linee perpendicolari alla direzione di marcia, su terreno piano, e il più distanti possibile compatibilmente con l’inquadratura, finché gli oggetti restano ben visibili.
- Misuri la distanza con cura: la percorra a passi o usi lo strumento di misura di una mappa.
- Verifichi una volta con un veicolo che passa a una velocità nota e regoli la distanza se la lettura è sbagliata.

Le letture sono stime. La frequenza fotogrammi di rilevamento della telecamera limita la precisione del cronometraggio di un attraversamento, quindi controlli corti e veicoli veloci sono i meno accurati. Salvare un controllo riavvia il rilevamento IA della telecamera.

Un controllo può avere un **limite di velocità** (facoltativo). Le velocità superiori sono mostrate in rosso e contate separatamente nella mappa di calore delle velocità.

Le velocità misurate compaiono in diversi punti:

- **Visualizzazione dal vivo**: sul riquadro di ogni oggetto mentre è in vista.
- **Istantanee**: un badge ⏱ sulla scheda e un filtro **Velocità** («pari o oltre») nell’elenco dei filtri.
- **Riproduzione**: un segno sulla timeline a ogni misura (rosso se oltre il limite; passi sopra per i dettagli) e un badge sul video mentre l’oggetto è sullo schermo.
- **Dashboard**: il widget **Registro delle velocità** mostra le misure più recenti. Faccia clic sul suo titolo per il registro completo, con ordinamento, filtro e download CSV.
- **Attività**: passi a **Velocità** per una mappa di calore di velocità media, velocità massima, numero oltre il limite o numero misurato, per giorno e ora. La cronologia viene conservata quanto la cronologia delle attività, molto dopo che i filmati sono spariti.
- **Avvisi**: una regola «Velocità oltre un limite».

## Movimento

Istantanee, avvisi e riquadri dal vivo **In movimento** contano solo gli oggetti che stanno andando da qualche parte. Il riquadro di un rilevatore tremola un po’ da un fotogramma all’altro anche attorno a un’auto parcheggiata, quindi pochi pixel di variazione non bastano. **Rilevamento del movimento** in **Impostazioni → Rilevamento IA**, o per telecamera in **Telecamere → Modifica**, decide come viene giudicato.

**Standard** (il valore predefinito) esamina la forma del percorso recente di ciascun oggetto:

- Qualsiasi cosa si muova con costanza in una direzione conta nel giro di pochi fotogrammi (circa mezzo secondo a 7 fps), che sia un’auto che attraversa la vista in meno di un secondo o un camion che esce da un passo carrabile. I passi di ciascun oggetto sono confrontati con quanto tremola il suo stesso riquadro, quindi un oggetto grande non deve percorrere più strada di uno piccolo.
- Qualcosa che avanza più lentamente di quanto tremola il suo riquadro, una frazione del passo di una camminata, conta una volta che il suo percorso ha seguito una linea costante per un paio di secondi. Il tremolio va avanti e indietro, quindi non costruisce mai una linea.
- Quanta strada deve fare un oggetto cresce con la sua dimensione, ma più lentamente della dimensione stessa. Un grande camion non deve prima attraversare metà della propria lunghezza.
- Un oggetto che entra dal bordo dell’immagine, o esce da dietro un palo o un albero, è giudicato dal lato del suo riquadro che è in vista.
- Un oggetto che va dritto verso la telecamera o si allontana conta man mano che cresce o si rimpicciolisce.
- Il riquadro di un’auto parcheggiata che cambia mentre qualcuno ci passa davanti non conta.

La **Sensibilità al movimento** (solo Standard) è **Normale** per impostazione predefinita. **Alta** rileva il movimento prima e **Bassa** ignora più tremolio. La abbassi su una telecamera in cui un’auto parcheggiata o un cespuglio che ondeggia sono segnalati come in movimento, e la alzi dove gli oggetti lenti vengono persi.

Un riquadro che cambia semplicemente forma, come quello di una pianta in vaso quando il rilevatore include le foglie un momento e non il successivo, o quello di un’auto parcheggiata quando qualcosa le passa davanti, non conta. Il movimento deve accumularsi sui fotogrammi, non avvenire in un unico salto.

**Legacy** è la regola precedente: un oggetto conta quando il suo centro si è spostato della metà della dimensione del suo riquadro entro 2 secondi. La usi se Standard si comporta male su una telecamera.

Un oggetto che era in movimento e sparisce dietro un albero, un palo o un’auto parcheggiata resta lo stesso oggetto quando riemerge più avanti sulla stessa linea entro circa 30 fotogrammi analizzati (circa 5 secondi a 6 fps), quindi viene contato una volta e mantiene un solo percorso. Deve anche essere lo stesso tipo di oggetto e di dimensione simile, e il suo colore deve corrispondere quando entrambi si possono determinare. Qualcosa che riemerge andando in un’altra direzione, o dopo più tempo, è un nuovo oggetto.

## Oggetti in movimento

Il percorso di ogni oggetto in movimento attraverso l’immagine viene conservato con il suo evento, per la mappa di calore **Scena** della pagina Attività.

**Oggetti in movimento** in **Impostazioni → Rilevamento IA** sceglie quali tipi di oggetto possono contare come in movimento. Per impostazione predefinita sono **Persone**, **Veicoli** e **Animali**. Tutto il resto viene comunque rilevato e appare come riquadro fermo con **Fermo** attivo, ma non conta mai come in movimento, quindi non può avviare un’istantanea o un avviso. Scelga altri oggetti uno per uno nell’elenco **Altri oggetti**: un pacco consegnato di solito appare come valigia, zaino o borsetta. Per un modello personalizzato, digiti i suoi nomi di oggetto, separati da virgole.

## Oggetti ignorati

**Oggetti ignorati** in **Impostazioni → Rilevamento IA** disattiva del tutto alcuni tipi di oggetto. Vengono scartati non appena il modello li segnala, quindi non ottengono mai un riquadro, un’istantanea, un evento o un avviso. Lo usi per le cose che il modello sbaglia di continuo sulle Sue telecamere, come una terrazza che scambia per un aereo. Selezioni intere categorie (**Persone**, **Veicoli**, **Animali** e **Altri oggetti**) o singoli oggetti dall’elenco. Corrisponde l’ortografia di entrambi i modelli, quindi ignorare **airplane** ignora anche **aeroplane**. Per un modello personalizzato, digiti i suoi nomi di oggetto, separati da virgole.

La pagina **Modifica** di una telecamera può scegliere un proprio elenco invece di quello globale: scelga **Scegli per questa telecamera** in **Oggetti ignorati**. Non scegliere nulla lì rileva tutto su quella telecamera, anche ciò che l’elenco globale ignora. Salvare riavvia il rilevamento di quella telecamera, il che reimposta solo il tracciamento.

## Jitter

La **soglia di jitter** è il più piccolo movimento in pixel che conti, qualunque sia la dimensione dell’oggetto. In modalità Legacy, il **rifiuto del jitter degli oggetti fermi** attenua il tremolio di un veicolo parcheggiato lontano; può anche mantenere come fermo un oggetto davvero lento, per questo è disattivato per impostazione predefinita. Non si applica a Standard, che gestisce da sé il tremolio.

## Backend esterno

**Servizio HTTP esterno** non esegue alcun modello sul nodo. Ogni fotogramma viene inviato a un server di inferenza che Lei gestisce, il quale restituisce i riquadri come JSON. Lo usi per modelli che LarisVMS non include, o per nodi senza GPU. Tracciamento, istantanee, timeline e sovrapposizione dal vivo funzionano allo stesso modo.

Il servizio deve esporre `POST /v1/detect`, `GET /v1/models` e `GET /healthz`. **Prova** verifica la connessione e carica l’elenco dei modelli. La **chiave API** viene inviata come token Bearer. **Trasporto**: JPEG funziona con qualsiasi servizio. Le modalità grezze YUV e BGRA evitano la codifica JPEG ma usano 4–10× la banda, e funzionano solo se il modello le annuncia.
