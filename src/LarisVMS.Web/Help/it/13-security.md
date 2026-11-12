# Sicurezza

Elenchi di IP consentiti, una porta separata per dal vivo/riproduzione, il criterio script rigoroso, certificati, nomi host, aggiornamenti, segreti cifrati e protezione aggiuntiva per le aziende.

## Elenchi consentiti

**Impostazioni → Sicurezza** ha due elenchi di IP consentiti. Inserisca un IP o blocco CIDR per riga (ad esempio `10.0.0.5` o `10.0.0.0/24`); le righe che iniziano con `#` sono commenti. Un elenco vuoto consente tutti.

- **Gestione / API**: le pagine di amministrazione e l’API REST. Un elenco che bloccherebbe l’indirizzo da cui sta salvando viene rifiutato, quindi non può chiudersi fuori.
- **Visualizzazione dal vivo / riproduzione**: video dal vivo, riproduzione, miniature, download di esportazioni e istantanee delle telecamere.

## Porta separata

**Impostazioni → Visualizzazione dal vivo → Porta personalizzata** sposta il traffico dal vivo e di riproduzione su una propria porta HTTPS, con lo stesso certificato. Questo permette di proteggerla separatamente con un firewall, o di esporre solo quella fuori dalla LAN. Ha effetto dopo `Restart-Service LarisVMSWeb`. Dopo di ciò, il video risponde solo sulla nuova porta e tutto il resto solo sulla porta principale. Svuoti il campo per tornare a una sola porta.

## HTTP/3

Il server usa HTTP/2 e aggiunge HTTP/3 su Windows 11, Windows Server 2022 o successivo. I browser partono su HTTP/2 e passano a HTTP/3 da soli quando possono raggiungerlo. HTTP/3 usa UDP sulla porta HTTPS (e sulla porta personalizzata, se impostata). Il programma di installazione apre sia TCP sia UDP; su un’installazione più vecchia, aggiunga una regola UDP per la porta o esegua di nuovo il programma di installazione attuale. Fino ad allora, tutto continua a funzionare su HTTP/2. Per disattivare HTTP/3, deselezioni **Impostazioni → Sicurezza → HTTP/3**, poi `Restart-Service LarisVMSWeb`.

## Criterio script rigoroso

Con **Impostazioni → Sicurezza → Criterio script rigoroso** attivo, i browser eseguono solo gli script che questo server invia, quindi uno script iniettato in una pagina non può essere eseguito. È attivo per impostazione predefinita e l’aggiornamento lo attiva. Se qualcosa smette di funzionare, lo disattivi: i browser segnalano allora tutto ciò che il criterio rigoroso avrebbe bloccato, e le segnalazioni più recenti compaiono sotto l’interruttore. Quando non compare più alcuna segnalazione dopo aver usato le pagine da cui dipende, lo riattivi. Una modifica ha effetto al successivo caricamento di pagina.

## Certificati

Il certificato del server è un file `.pfx` impostato in `appsettings.Production.json`. Un file rinnovato viene recepito entro un minuto senza riavvio. I nodi che usano lo streaming diretto hanno la propria impostazione del certificato nella pagina del nodo.

## Certificati delle telecamere

Le telecamere raggiunte via HTTPS hanno certificati autofirmati che non si possono verificare rispetto a nulla, quindi per impostazione predefinita qualsiasi certificato è accettato. Attivi **Impostazioni → Sicurezza → Fissa i certificati delle telecamere** perché il server memorizzi il certificato di ogni indirizzo di telecamera la prima volta che si connette, e ne rifiuti uno diverso da quel momento, sul server e sui nodi. Qualcosa nella rete che si finge una telecamera non può allora raccogliere la sua password né inviare eventi falsi.

I certificati memorizzati sono elencati sotto l’impostazione. Se il certificato di una telecamera cambia davvero (un aggiornamento del firmware, un ripristino o un nuovo certificato), selezioni **Dimentica** accanto al suo indirizzo; il prossimo certificato che presenta viene memorizzato. Fino ad allora, ogni connessione rifiutata, sul server o su un nodo, viene registrata nel registro di audit e i Super Admin vengono avvisati (al massimo una volta all’ora per indirizzo di telecamera), poiché qualcosa nella rete potrebbe fingersi la telecamera. Un indirizzo di telecamera viene memorizzato quando il server lo raggiunge per la prima volta, di solito quando la telecamera viene aggiunta; un nodo accetta un indirizzo che il server non ha ancora raggiunto.

## Nomi host

Per impostazione predefinita il server risponde a qualunque nome host usi un browser, quindi funziona per indirizzo IP, nome breve e nome completo. Per rispondere solo ai nomi che usa, imposti `AllowedHosts` in `appsettings.Production.json` con essi, separati da punto e virgola (ad esempio `"AllowedHosts": "vms.example.com;vms;localhost"`), poi `Restart-Service LarisVMSWeb`. Includa ogni nome che nodi, proxy e persone usano per raggiungerlo: qualsiasi altro nome riceve un errore.

Dietro un reverse proxy (IIS ARR, nginx e così via), ne elenchi l’indirizzo in `appsettings.Production.json` come `"ReverseProxy": { "KnownProxies": [ "10.0.0.2" ] }`, poi riavvii. Il server prende allora l’indirizzo e lo schema reali del client dalle intestazioni `X-Forwarded-For` e `X-Forwarded-Proto` di quel proxy, così elenchi consentiti e registro di audit vedono la persona, non il proxy. Quelle intestazioni vengono ignorate da qualsiasi cosa non presente nell’elenco, e interamente finché l’elenco è vuoto.

## Aggiornamenti

Questi controlli si frappongono tra un download e un’installazione in esecuzione:

- **I download delle release** includono `SHA256SUMS.txt`. Verifichi un programma di installazione prima di eseguirlo con `Get-FileHash <file> -Algorithm SHA256` e lo confronti con la riga di quel file, oppure esegua `sha256sum -c SHA256SUMS.txt`. Quando i programmi di installazione sono firmati, Windows mostra anche l’autore.
- **Gli aggiornamenti di nodi e proxy** sono firmati dalla chiave di firma di questo server. Un nodo o proxy conserva quella chiave da quando si è registrato e non installa nulla che non sia firmato con essa, anche se la sua connessione al server fosse manomessa. Rifiuta inoltre una build che non sia più recente di quella che esegue. Vedere [Installazione](/Help/installation#updates).
- **La firma dell’autore** (facoltativa): un nodo o proxy compilato con la chiave dell’autore (`-PublisherKeyPath`) richiede anche la firma dell’autore su ogni aggiornamento; le altre build saltano questo controllo. Quella chiave è tenuta offline e mai sul server, quindi nemmeno un server violato può inviare ai nodi il proprio codice.
- **La connessione** al server viene verificata come impostato in **Nodi → Aggiornamenti e registrazione → Verifica del certificato del server** (vedere [Nodi](/Help/nodes#server-certificate)).

## Segreti cifrati

Credenziali delle telecamere, credenziali SMB, chiavi dei nodi, segreti e-mail, il segreto client Entra e la password dell’account di servizio Active Directory sono cifrati nel database, usando le chiavi di protezione dei dati in `%ProgramData%\LarisVMS\keys`. Solo SYSTEM, Administrators e gli account dei servizi LarisVMS possono aprire `%ProgramData%\LarisVMS`: qualsiasi altro accesso trovato lì, come una cartella creata da un utente locale prima dell’installazione di LarisVMS, viene rimosso all’avvio di un servizio. Faccia il backup di quella cartella e non la elimini mai durante un deploy.

## Protezione aggiuntiva per le aziende

Di serie, LarisVMS è configurato perché un’installazione domestica funzioni e basta. Tutto ciò che segue è facoltativo e disattivato per impostazione predefinita; attivi ciò che si adatta a un’azienda, o a un server raggiungibile da Internet:

- **Richiedi l’accesso a due fattori**: Impostazioni → Sicurezza (vedere [Accesso a due fattori](/Help/users#two-factor-sign-in)).
- **Conferma la password per le pagine di amministrazione**: Impostazioni → Sicurezza (vedere [Sessioni](/Help/users#sessions)).
- **Password temporanee impostate dall’amministratore**: Impostazioni → Sicurezza (vedere [Password](/Help/users#passwords)).
- **Sessioni più brevi e un timeout di inattività**: ogni ruolo in Impostazioni → Ruoli (vedere [Ruoli](/Help/users#roles)).
- **Fissa i certificati delle telecamere**: Impostazioni → Sicurezza (vedere [Certificati delle telecamere](#camera-certificates)).
- **Elenchi consentiti**: Impostazioni → Sicurezza (vedere [Elenchi consentiti](#allow-lists)).
- **Il token di installazione da ogni altro computer**: imposti `"Setup": { "AlwaysRequireToken": true }` in `appsettings.Production.json` prima di eseguire la procedura guidata di installazione (vedere [Installazione](/Help/installation#web-server)).
- **Fissare il certificato del server quando installa un nodo o un proxy**: `-ServerCertThumbprint` (vedere [Certificato del server](/Help/nodes#server-certificate)).
- **Codici di registrazione monouso, con la chiave condivisa disattivata**: Nodi → Aggiornamenti e registrazione (vedere [Codici di registrazione monouso](/Help/nodes#one-time-registration-codes)).
- **Connessioni cifrate dal server ai nodi**: nella pagina di ogni nodo (vedere [Connessioni cifrate dal server](/Help/nodes#encrypted-connections-from-the-server)).
- **Inoltrare il registro di audit a syslog**: Impostazioni → Registri (vedere [Registro di audit](/Help/users#audit-log)).
- **Aggiornamenti firmati dall’autore**, per le build che crea Lei stesso (vedere [Aggiornamenti](#updates)).
- **Un account di servizio meno privilegiato.** I servizi LarisVMS vengono eseguiti come LocalSystem. Può eseguirli con un altro account (`services.msc`, o `sc.exe config`), ma spetta allora a Lei dare a quell’account ciò di cui il servizio ha bisogno: la cartella di installazione, ogni cartella o condivisione di archiviazione e archivio, e i file dei certificati. `%ProgramData%\LarisVMS` è gestita per Lei: ogni servizio LarisVMS, all’avvio, la limita a SYSTEM, Administrators e agli account con cui vengono eseguiti i servizi LarisVMS, e rimuove qualsiasi altro accesso. Controlli il registro del servizio dopo la modifica.
