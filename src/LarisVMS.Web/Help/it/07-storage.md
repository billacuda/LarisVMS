# Archiviazione e conservazione

Dove si trovano le registrazioni, per quanto tempo vengono conservate, la soglia e il volume di archivio.

## Percorsi di archiviazione

Ogni nodo registra nel proprio **percorso di archiviazione**, impostato nella pagina di modifica del nodo o durante l’installazione. Non esiste un percorso globale. Un nodo senza percorso di archiviazione non registra nulla, e l’elenco dei nodi lo segnala.

## Conservazione

La **conservazione** è per quanti giorni i filmati vengono mantenuti. Si imposta globalmente in **Impostazioni → Archiviazione e conservazione** e può essere sostituita per nodo e per telecamera; vince il valore più specifico. 0 conserva i filmati per sempre.

Una telecamera può anche avere una **quota di archiviazione** in GB. Quando viene superata, i suoi filmati più vecchi vengono rimossi per primi.

## Soglia

La **soglia** è un limite rigido di quanto può riempirsi il disco di un nodo (90% per impostazione predefinita). Oltre di essa, il nodo archivia in anticipo i suoi filmati più vecchi, o li elimina se l’archiviazione non è disponibile, finché il disco non torna sotto il limite. L’elenco dei nodi mostra **archiviazione piena** mentre ciò accade.

## Archivio

Un nodo può avere un secondo **percorso di archivio** facoltativo, come una condivisione SMB o un’unità USB. Deve essere una posizione distinta dal percorso di archiviazione. Con l’archiviazione attiva, i filmati che scadono dalla conservazione vengono **spostati** nell’archivio invece di essere eliminati, e mantenuti fino al raggiungimento della **conservazione dell’archivio**. La conservazione dell’archivio è contata dalla data di registrazione originale, e 0 conserva per sempre.

Riproduzione, miniature ed esportazioni funzionano allo stesso modo da entrambi i volumi. L’elenco dei nodi mostra lo spazio libero e una stima dei giorni rimasti per entrambi i volumi. La stima si basa sulle ultime 24 ore di scrittura.

## Filmati residui

Quando una telecamera passa a un altro nodo o viene rimossa, i suoi vecchi filmati restano sul nodo precedente fino alla scadenza. Il conteggio ⚠️ accanto allo stato di un nodo mostra quante telecamere hanno ancora filmati lì. Si cancella da solo.

## Cifratura

I filmati possono essere cifrati sui dischi dei registratori, così un disco rubato o dismesso non mostra nulla. È disattivata per impostazione predefinita.

**La configuri** in **Impostazioni → Cifratura e firma**:

1. **Crea chiave principale.** È conservata su questo server e sblocca ogni registrazione cifrata.
2. **Scarichi un backup** della chiave principale, protetto da una passphrase a Sua scelta. La cifratura non può essere attivata finché non ne ha uno. Conservi il file e la passphrase in un luogo diverso da questo server: se il server viene ricostruito, **Ripristina** con il backup è l’unico modo per leggere di nuovo i filmati cifrati. Se perde entrambi, i filmati sono persi per sempre.
3. **Scelga cosa cifrare:** **Video** (registrazioni e audio di conversazione bidirezionale), oppure **Video, istantanee e miniature**. È il valore predefinito per ogni telecamera; ogni telecamera può sostituirlo nella propria pagina (**Telecamere → Modifica**, Archiviazione).

Ogni segmento viene cifrato sul registratore subito dopo aver finito di registrare, quindi solo il segmento in registrazione è mai non cifrato sul disco. I filmati registrati prima dell’attivazione della cifratura restano come sono; **cifra i filmati esistenti** nella pagina della telecamera li converte in background. Disattivare la cifratura in seguito lascia leggibili i filmati cifrati.

Riproduzione, miniature, istantanee ed esportazioni funzionano come prima. I file esportati non sono cifrati, dato che sono pensati per essere consegnati. Il registratore preleva da questo server la chiave giornaliera di ogni telecamera quando ne ha bisogno e la tiene solo in memoria, quindi una registrazione cifrata non può essere riprodotta mentre il registratore non può raggiungere il server.
