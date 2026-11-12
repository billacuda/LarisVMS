# Rilevamento del movimento

Sorgenti di movimento, movimento lato server, zone e griglia di movimento.

## Sorgente

Una telecamera può segnalare il movimento in più modi:

- **Movimento lato server**: il nodo confronta i fotogrammi video all’interno di zone di movimento o della griglia di movimento.
- **Eventi della telecamera**: gli eventi di movimento ONVIF della telecamera stessa.
- **Integrazione del produttore**: ad esempio gli eventi intelligenti Dahua / Amcrest.
- **Rilevamento IA**: un oggetto in movimento rilevato.

La **Sorgente del movimento** della telecamera sceglie quale *una* di queste decide cosa conserva la modalità Movimento, così la timeline non ha voci duplicate per lo stesso evento. **Auto** sceglie la più ricca tra quelle configurate. Le regole dei tag evento e le etichette degli oggetti IA etichettano sempre la timeline indipendentemente da questa scelta.

## Movimento lato server

**Movimento lato server** nella pagina della telecamera attiva o disattiva il rilevamento del movimento sui pixel del nodo. Lo lasci attivo per etichettare il semplice movimento sulla timeline come ripiego, anche quando un’altra sorgente controlla la registrazione. Lo disattivi per risparmiare la CPU che usa.

## Zone

La pagina **Zone** di una telecamera ha due metodi. Solo uno è attivo alla volta, e passare all’altro conserva le impostazioni dell’altro.

**Griglia**: faccia clic sulle celle per mascherarle (rosso). Le celle diventano color ambra quando c’è movimento al loro interno, il che aiuta a trovare un ramo che oscilla o una luce che sfarfalla da mascherare. Cambiare la dimensione della griglia cancella la maschera.

**Zone poligonali**:

- **Movimento**: un’area da osservare.
- **Ignora**: un’area da escludere (un albero, un marciapiede pubblico). I pixel in una zona Ignora non contano mai, nemmeno dove si sovrappone a una zona Movimento.
- **Privacy**: un’area da oscurare. Le disegni in **🔒 Maschere di privacy**, che funziona qualunque metodo di movimento (Griglia o Zone poligonali) usi la telecamera. Viene nascosta nelle registrazioni, nella visualizzazione dal vivo (entrambi i flussi), nelle istantanee e nelle esportazioni. Gli eventi continuano ad accadere dietro di essa come al solito (le loro istantanee sono mascherate come il video) a meno che attivi **Ignora anche i rilevamenti all’interno delle maschere di privacy** per quella telecamera, nella vista Maschere di privacy. La maschera segue la forma che disegna, con un piccolo margine perché non traspaia alcun bordo. Una telecamera con una zona di privacy ha il video ricodificato durante la registrazione, il che usa un po’ di CPU (o la GPU, quando il nodo ne ha una supportata).
- Le zone di movimento **lato telecamera** possono essere salvate ma non sono ancora attive.

Per rimodellare una zona, la selezioni nell’elenco e trascini i suoi vertici, poi **Salvi**. Durante il disegno, il clic destro rimuove l’ultimo punto. Passare sopra una zona nell’elenco la evidenzia sull’immagine. Può anche fare clic su un vertice, poi fare clic dove deve andare.

La **Sensibilità** è la percentuale dei pixel della zona che devono cambiare. Più bassa è più sensibile. Le zone ampie richiedono valori più bassi, perché un movimento reale ne copre solo una piccola parte. Parta da un valore basso (circa 3%) e lo aumenti solo se vento o variazioni di luce lo fanno scattare.
