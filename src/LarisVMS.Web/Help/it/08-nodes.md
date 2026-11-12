# Nodi

Nodi di registrazione: stato, percorsi di streaming, proxy multimediali, failover, manutenzione e aggiornamenti.

**Nodi** nella barra laterale elenca ogni nodo con il suo stato, indirizzo, versione, numero di telecamere e utilizzo del disco. Faccia clic su un nodo per modificarne le impostazioni. Il menu **⋯** ha queste azioni:

- **Riavvia servizio**: riavvia il servizio Windows del nodo, non la macchina. La registrazione si interrompe per alcuni secondi.
- **Reimposta autenticazione**: cancella il token di check-in del nodo, per un nodo bloccato in errori di autenticazione dopo un heartbeat perso. Non ha effetto su un nodo in buono stato.
- **Manutenzione**: sposta le telecamere del nodo sul suo nodo di riserva finché non disattiva la manutenzione.
- **Elimina**: rimuove il nodo. Le sue telecamere diventano non assegnate. Le registrazioni non vengono eliminate.

Un nodo è **online** se ha fatto check-in negli ultimi 2 minuti. Un avviso ⚠️ **orologio** significa che l’orologio del nodo è sfasato di più di un minuto rispetto a quello del server, quindi verifichi la sua sincronizzazione dell’ora.

## Streaming diretto

Per impostazione predefinita tutto il video dal vivo e in riproduzione è inoltrato attraverso il server web (**Proxy**). Con **Diretto**, i browser si connettono direttamente all’endpoint HTTPS di ciascun nodo, il che toglie carico al server. Imposti la modalità in **Impostazioni → Visualizzazione dal vivo** e la sostituisca per nodo.

La modalità Diretto richiede un **host dell’endpoint client** raggiungibile dai browser e un certificato (un percorso `.pfx` nella pagina del nodo). **Consenti autofirmati** è solo per test, perché gli spettatori ricevono un avviso del browser. Una telecamera il cui nodo non ha un endpoint in buono stato ripiega automaticamente sul proxy.

## Connessioni cifrate dal server

Il server e i proxy multimediali raggiungono le route video e di controllo di ogni nodo tramite HTTP semplice sulla sua porta dal vivo (8554), il che va bene su una rete attendibile. Per cifrare quel traffico, attivi **Cifra le connessioni dal server** nella pagina del nodo, poi riavvii il nodo una volta (**Riavvia**). Il nodo avvia il suo endpoint HTTPS (con il proprio certificato autofirmato se non ha un `.pfx`) e segnala il certificato, che il server poi fissa: il server e i proxy accettano dal nodo solo quel certificato, e il token multimediale non fa più parte dell’indirizzo. Una volta cifrato, la porta non cifrata del nodo risponde solo a `/health`.

La colonna **Cifrato** della pagina Nodi mostra 🔒 per ogni nodo cifrato e ⏳ per uno attivato ma che ha ancora bisogno di un riavvio. Lo stato sotto l’interruttore mostra il certificato in uso, o che il server lo sta ancora aspettando (fino ad allora il server continua a usare HTTP semplice, quindi nulla smette di funzionare). È disattivato per impostazione predefinita. I certificati autofirmati usati per questo non vengono mai offerti ai browser; lo streaming diretto richiede comunque un certificato reale.

## Proxy

Un **proxy multimediale** è un relay separato (`LarisVMS.Proxy`) tra browser e nodi, per siti remoti o collegamenti a banda ridotta. Registri i proxy in **Impostazioni → Proxy multimediali**, poi scelga un proxy principale e uno di riserva nella pagina di ogni nodo. I browser usano il primo in buono stato.

## Failover

Assegni a un nodo un **nodo di registrazione di riserva**, e quando va fuori servizio le sue telecamere vengono registrate dal nodo di riserva finché non torna. «Fuori servizio» è deciso da un voto tra il partner del nodo, il server e il suo proxy. **Manutenzione** sposta le sue telecamere di proposito e ve le mantiene.

La pagina del nodo avvisa quando il nodo di riserva ha hardware IA diverso o è solo di registrazione. In quei casi il rilevamento può essere più lento, o sospeso, durante un failover.

## Aggiornamenti

Nodi e proxy si aggiornano da soli dalle build approvate (vedere [Installazione](/Help/installation#updates)). **Nodi → Aggiornamenti e registrazione → Aggiornamento automatico** lo attiva o disattiva per ogni nodo.

La sezione **Build** della pagina Nodi mostra la build verso cui sta andando ogni tipo di nodo e qualsiasi build in attesa di approvazione, con **Approva** e **Rifiuta**. **Cronologia delle build** elenca ogni build, comprese quelle approvate, sostituite e rifiutate.

## Certificato del server

**Nodi → Aggiornamenti e registrazione → Verifica del certificato del server** decide come nodi e proxy verificano il certificato HTTPS di questo server:

- **Fiducia al primo utilizzo** (predefinito): un certificato di una CA attendibile funziona sempre, anche se rinnovato o sostituito. Un certificato autofirmato viene memorizzato la prima volta che un nodo si connette e continua a funzionare finché resta lo stesso, anche oltre la sua data di scadenza. Se cambia, nodi e proxy lo rifiutano finché non vengono registrati di nuovo.
- **Solo certificato attendibile**: solo un certificato di una CA attendibile che corrisponda al nome del server. Ne installi uno prima di scegliere questa opzione.
- **Non verificare**: qualsiasi certificato. Non sicuro: chiunque sul percorso di rete potrebbe fingersi il server.

Con la fiducia al primo utilizzo, un nodo considera attendibile il certificato che vede la prima volta che si connette. Perché verifichi fin dalla primissima connessione, passi al programma di installazione l’impronta mostrata in **Verifica del certificato del server**: `-ServerCertThumbprint` per `install-node.ps1` e `install-proxy.ps1`, oppure `SERVERCERTTHUMBPRINT=` per l’MSI. I comandi di installazione nelle pagine Installazione e Proxy multimediali la includono già. Dietro un reverse proxy, usi invece l’impronta del certificato che il reverse proxy presenta.

Il certificato autofirmato che il server crea per sé dura 10 anni e non viene sostituito da solo. **Rigenera certificato autofirmato** lo sostituisce; ogni nodo e proxy deve allora essere registrato di nuovo, e la pagina Nodi elenca quelli che non hanno fatto check-in da allora. Per registrare di nuovo un nodo, esegua di nuovo il programma di installazione del nodo, oppure esegua `LarisVMS.Node.exe --reset-server-pin` su di esso e riavvii il suo servizio (i proxy hanno la stessa opzione).

## Codici di registrazione monouso

I nuovi nodi e proxy si registrano con la **chiave di registrazione** condivisa, o con un codice monouso. Un codice monouso registra un solo nodo o proxy, poi smette di funzionare. Se non usato, scade dopo 1 ora, 24 ore o 7 giorni. Per crearne uno, vada in **Nodi → Aggiornamenti e registrazione**, scelga quanto dura e selezioni **Crea codice**. Il codice viene mostrato una sola volta. Lo usi dove il programma di installazione chiede la chiave di registrazione. **Revoca** annulla un codice non ancora usato.

La chiave condivisa funziona per ogni installazione finché non viene cambiata. Una volta che usa i codici monouso, deselezioni **Accetta la chiave di registrazione condivisa** così una copia della chiave in un vecchio script o e-mail non può registrare nulla. I nodi e i proxy già registrati non sono interessati. In entrambi i casi, un nuovo nodo attende comunque l’approvazione prima di ricevere telecamere.
