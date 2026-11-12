# Risoluzione dei problemi

Problemi comuni e dove guardare.

## Dove si trovano i registri

- **Server**: **Registri → Registri di sistema**. Imposti livello e conservazione in **Impostazioni → Registri**. Debug e Trace aggiungono dettagli ma producono file grandi.
- **Registro eventi delle telecamere**: gli eventi grezzi che le telecamere inviano, usati solo per elencare gli argomenti degli eventi di una telecamera per i tag evento. Sono conservati 30 giorni per impostazione predefinita (**Impostazioni → Registri**, 0 li conserva per sempre); cancellarli non tocca eventi, istantanee o filmati.
- **Manutenzione del database**: ogni notte verso le 3 il server riordina gli indici e le statistiche del database e registra una riga su ciò che ha fatto. Se il suo accesso al database non è autorizzato a farlo, registra invece un avviso. Su MariaDB viene saltata; MariaDB gestisce le proprie statistiche.
- **Nodi**: `%ProgramData%\LarisVMS\logs\node-*.log` su ogni nodo.
- **Rilevamento IA**: `%ProgramData%\LarisVMS\logs\vision-*.log` su ogni nodo.
- **Aggiornamento automatico**: `%ProgramData%\LarisVMS\logs\updater-*.log` su ogni nodo o proxy.

## Una telecamera non registra

1. È **assegnata a un nodo**, e quel nodo è **online**?
2. Il nodo ha un **percorso di archiviazione**? L’elenco dei nodi segnala quelli che non ne hanno uno.
3. La telecamera è **attivata** e i suoi flussi sembrano a posto? **Sonda di nuovo** dopo qualsiasi modifica sulla telecamera stessa.
4. In modalità Movimento, Pianificazione o Evento, verifichi se i filmati vengono scartati (vedere [Registrazione](/Help/recording#modes)).

## Dal vivo e riproduzione restano su «connessione»

Quando un nodo trasmette direttamente ai browser (vedere [Streaming diretto](/Help/nodes#direct-streaming)), il browser si connette alla porta client del nodo, 4200 per impostazione predefinita. Se la console del browser mostra una connessione `wss://<nodo>:<porta>/live/…` non riuscita, verifichi che il firewall del nodo consenta quella porta: il programma di installazione crea una regola in ingresso chiamata **LarisVMS Node Client Endpoint**. I programmi di installazione precedenti alla 0.215.1 potevano perdere quella regola durante un aggiornamento o la presa in carico di un nodo; esegua di nuovo il programma di installazione del nodo attuale, oppure aggiunga la regola a mano.

## L’accesso o la sincronizzazione Active Directory non riesce

Usi **Prova connessione** in **Impostazioni → Active Directory**; l’errore dell’ultima sincronizzazione è mostrato nella stessa pagina.

- **Impossibile raggiungere un controller di dominio:** verifichi che il nome di dominio si risolva dal server e che la porta 636 (LDAPS) o 389 (LDAP) sia aperta verso un controller di dominio. Con LDAPS, il controller di dominio ha bisogno di un certificato considerato attendibile da questo server.
- **L’identità propria del server è stata rifiutata:** la sicurezza integrata funziona solo quando il servizio LarisVMS Web gira su un server aggiunto al dominio, come LocalSystem o un account di dominio. Altrimenti usi un account di servizio.
- **«Non appartiene ad alcun gruppo con accesso»:** l’utente ha effettuato l’accesso correttamente ma non è in un gruppo collegato. I gruppi sono abbinati come gruppi di sicurezza, compresi quelli annidati.
- **Escluso dalle pagine di amministrazione dopo aver disattivato l’accesso locale:** vedere [Active Directory](/Help/users#active-directory) per il comando del database che lo riattiva.

## La visualizzazione dal vivo va in pausa in una finestra in secondo piano

Chrome limita le finestre non visibili, quindi un muro dal vivo su un secondo monitor può bloccarsi e poi recuperare. La visualizzazione dal vivo si riprende da sola. Per evitarlo, aggiunga questi al target del collegamento di Chrome:

```
--disable-backgrounding-occluded-windows --disable-background-timer-throttling
```

## Tutti i riquadri si bloccano, poi si riconnettono insieme

Si è bloccato il nodo, non il browser o la rete. Cerchi nel registro del nodo `Node process health: tick … ms late`. Un nodo in buono stato lo registra circa una volta al minuto con un ritardo di pochi millisecondi.

- Se `gcPauseMs` è vicino al ritardo, la causa è la garbage collection.
- Se `gcPauseMs` è piccolo ma `pendingWorkItems` è nell’ordine delle decine, i thread sono bloccati. Acquisisca un dump mentre è bloccato: `dotnet-dump collect -p <pid di LarisVMS.Node>`.

## Il rilevamento IA si ferma ma la registrazione continua

Di solito il driver della GPU è stato reimpostato. Il registro eventi di sistema di Windows mostra in quel momento l’evento 153 di `nvlddmkm` o l’evento 4101 di `Display`. Il servizio di visione si riavvia da solo dopo 60 secondi senza risultati. Reimpostazioni ripetute indicano la GPU, il suo driver, il raffreddamento o l’alimentazione.

## Un nodo o proxy non è tornato dopo un aggiornamento

Avvii il suo servizio (`sc start LarisVMSNode`, oppure `LarisVMSProxy`), poi controlli `updater-*.log` per capire perché il riavvio è fallito. Un aggiornamento scaricato che non è mai stato applicato viene anche annotato nel registro del nodo o del proxy al suo avvio successivo, e viene offerto di nuovo a un check-in successivo.

## Un nodo mostra «orologio»

L’orologio del nodo è sfasato di più di un minuto rispetto a quello del server. Corregga la sua sincronizzazione dell’ora. Lo sfasamento dell’orologio influisce su pianificazioni e timeline.

## Un nodo continua a non fare check-in

Usi **Reimposta autenticazione** dal menu **⋯** del nodo. Cancella il token di check-in del nodo, e il nodo si risincronizza al successivo heartbeat.
