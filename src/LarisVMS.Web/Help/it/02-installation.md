# Installazione e aggiornamenti

Installare il server web, i nodi di registrazione e i proxy multimediali, e mantenerli aggiornati.

Ogni parte ha il proprio programma di installazione: `LarisVMS-Web`, `LarisVMS-Node` e `LarisVMS-Proxy` (`.msi`). Faccia doppio clic su uno per ricevere la richiesta delle sue impostazioni, oppure le passi dalla riga di comando con `msiexec /i <file>.msi NOME=valore … /qn`. Tutto ciò che non viene fornito viene richiesto, e un aggiornamento riutilizza le impostazioni dell’installazione precedente.

## Server web

1. Disponga di un SQL Server raggiungibile dal server (sicurezza integrata o autenticazione SQL), o di un server MariaDB (vedere [MariaDB](#mariadb)).
2. Esegua `LarisVMS-Web.msi`. Impostazioni:
   - `HTTPSPORT`: la porta HTTPS (predefinita 8444). Il programma di installazione la apre nel firewall, per TCP e per UDP ([HTTP/3](/Help/security#http3)).
   - `CERTPATH`, `CERTPASSWORD`: il certificato del server (`.pfx`). Lasci vuoto per usare un certificato autofirmato finché non ne ha uno. Il programma di installazione verifica che il file si apra con la password prima di continuare; una condivisione di file deve inoltre essere leggibile dall’account del servizio.
   - `SERVICEACCOUNT`, `SERVICEPASSWORD`: esegue il servizio come un account specifico, ad esempio uno con diritti di sicurezza integrata SQL. Vuoto lo esegue come LocalSystem. Non necessario per MariaDB.
3. Vada su `https://<host>:8444/`. La procedura guidata di installazione accompagna nella configurazione di database, primo account amministratore, archiviazione e registrazione dei nodi. Un browser sul server stesso o sulla rete locale entra direttamente. Da qualsiasi altro luogo chiede prima il token di installazione in `%ProgramData%\LarisVMS\setup-token.txt` sul server (leggibile solo dagli amministratori), così nessuno su Internet può configurarlo per primo. Per richiedere il token da ogni altro computer, rete locale compresa, imposti `"Setup": { "AlwaysRequireToken": true }` in `appsettings.Production.json` prima di aprire la procedura guidata. Il file viene eliminato una volta creato l’account amministratore.

La password di un accesso SQL, o di MariaDB, è memorizzata cifrata sul server (DPAPI, solo questa macchina), quindi `setup-generated.json` non può essere copiato su un’altra macchina con la password ancora funzionante. Esegua invece di nuovo la procedura guidata lì.

Se punta la procedura guidata a un database LarisVMS esistente (ad esempio dopo aver reinstallato il server), lo usa così com’è: aggiornato se necessario, nulla viene inserito o sovrascritto e i passaggi rimanenti vengono saltati. Un database che non è di LarisVMS viene rifiutato. Una volta completata la configurazione, la procedura guidata non può essere riaperta.

Il server non richiede alcun runtime .NET; è incluso. Il certificato può anche essere cambiato in seguito in `appsettings.Production.json` nella cartella di installazione. Un `.pfx` rinnovato nello stesso percorso viene recepito entro un minuto, senza riavvio.

## MariaDB

LarisVMS può usare MariaDB 10.11 o successivo al posto di SQL Server. Nella procedura guidata di installazione scelga **MariaDB** e inserisca host, porta (predefinita 3306), nome del database e un utente e una password MariaDB. MariaDB non ha un accesso Windows che LarisVMS possa usare, quindi servono sempre un utente e una password.

- **L’utente** ha bisogno dell’autorizzazione a creare il database. Oppure lo crei Lei prima, con il set di caratteri `utf8mb4`, e conceda all’utente `ALL` su di esso:

  ```sql
  CREATE DATABASE larisvms CHARACTER SET utf8mb4 COLLATE utf8mb4_uca1400_as_ci;
  CREATE USER 'larisvms'@'%' IDENTIFIED BY 'a-strong-password';
  GRANT ALL ON larisvms.* TO 'larisvms'@'%';
  ```

- **Cifratura:** la connessione usa TLS. Il certificato del server viene verificato a meno che MariaDB sia su questa macchina, oppure selezioni **Considera attendibile il certificato di questo server** (per un certificato autofirmato su un’altra macchina).
- **MariaDB precedente alla 11.4 ha TLS disattivato** finché non riceve un certificato, e LarisVMS non invierà la password a un’altra macchina senza di esso. Aggiunga il certificato e la sua chiave alla sezione `[mysqld]` di `my.ini`, riavvii MariaDB e selezioni **Considera attendibile il certificato di questo server** se è autofirmato:

  ```ini
  [mysqld]
  ssl_cert = C:/MariaDB/certs/server-cert.pem
  ssl_key = C:/MariaDB/certs/server-key.pem
  ```

  MariaDB 11.4 e successivo configurano un certificato da sé.
- **I backup** richiedono gli strumenti client MariaDB sul server LarisVMS (vedere [Backup](/Help/backups)).
- Un’installazione SQL Server esistente non può essere spostata su MariaDB; MariaDB è per le nuove installazioni.

## Nodi

Esegua `LarisVMS-Node.msi` su ogni macchina di registrazione. Servono:

- `SERVERURL` e `REGISTRATIONKEY`: l’indirizzo del server e la **chiave di registrazione** in **Nodi → Aggiornamenti e registrazione**. Vengono usate solo la prima volta che il nodo si registra. Cambiare la chiave non influisce sui nodi esistenti.
- **FFmpeg**, che non è incluso. Lo installi prima con `winget install ffmpeg --scope machine`. Il programma di installazione lo trova, oppure imposti `FFMPEGPATH`.
- `STORAGEROOT`: dove il nodo registra. Può anche essere impostato in seguito nella pagina di modifica del nodo.

Facoltativi: `ARCHIVEROOT`, `LIVEPORT` (predefinita 8554), `LIVEALLOWFROM` (indirizzi consentiti dal firewall verso la porta dal vivo, come IP o blocchi CIDR separati da virgole: il server LarisVMS ed eventuali proxy; vuoto consente tutti), `INSECURETLS=1` (non più necessario: i nodi considerano attendibile il certificato autofirmato del server al primo utilizzo, vedere [Certificato del server](/Help/nodes#server-certificate)), l’endpoint di streaming diretto (`CLIENTPORT`, `CLIENTENDPOINTHOST`, `CLIENTPFXPATH`, `CLIENTPFXPASSWORD`) e `SERVICEACCOUNT` / `SERVICEPASSWORD` per un account che possa raggiungere l’archiviazione SMB.

Per il rilevamento IA, le macchine NVIDIA richiedono anche CUDA Toolkit 12.x e cuDNN 9.x; il programma di installazione copia le loro DLL al posto giusto quando le trova. Vedere [Rilevamento IA](/Help/ai-detection#hardware).

Dopo l’installazione, il nodo compare in **Nodi** come **in attesa di approvazione**. Non registra nulla e non riceve telecamere finché non fa clic su **Approva**, quindi approvi solo un nodo che ha installato Lei stesso. Poi assegni le telecamere dalla pagina di modifica di ciascuna telecamera.

## Proxy multimediali

Esegua `LarisVMS-Proxy.msi` con `SERVERURL`, `REGISTRATIONKEY` e `CLIENTPORT` (predefinita 4443), più le stesse impostazioni facoltative del certificato di un nodo. Un nuovo proxy parte disattivato: attivi **Attivato** per esso in **Impostazioni → Proxy multimediali**, poi lo scelga nella pagina di ogni nodo (vedere [Nodi](/Help/nodes#proxies)).

## Aggiornamenti

**Aggiornamento:** esegua il `.msi` più recente. Trova l’installazione esistente e passa direttamente a **Pronto per l’aggiornamento**; le impostazioni sono ricordate, quindi non serve inserire di nuovo nulla tranne la password di un account di servizio di dominio. Il server web applica da sé le eventuali modifiche al database all’avvio. Gli aggiornamenti non toccano mai registrazioni, file di configurazione, registrazione dei nodi né chiavi di protezione dei dati, e un’installazione non riuscita viene annullata.

**Nodi e proxy si aggiornano da soli.** Una nuova installazione web registra come **In attesa** le build di nodo e proxy che contiene nella sezione **Build** di **Nodi**. Solo la build di nodo più recente e la build di proxy più recente attendono l’approvazione; le più vecchie sono **Sostituite**. **Cronologia delle build** elenca ogni build. Ne approvi una e ogni nodo più vecchio la scarica, la verifica e la installa al suo prossimo check-in. Il server firma ogni aggiornamento con la sua chiave di firma (vedere **Impostazioni → Cifratura e firma**); nodi e proxy conservano quella chiave da quando si sono registrati e non installano nulla che non sia firmato con essa, anche se la loro connessione al server fosse manomessa. Lo disattivi con **Aggiornamento automatico** in **Nodi → Aggiornamenti e registrazione**.

**Nuove versioni:** una volta al giorno (e all’avvio del servizio) il server controlla le release GitHub di LarisVMS. Quando è disponibile una versione più recente, le persone che possono modificare le impostazioni vedono **✨ È disponibile la versione X** nella barra superiore, con un link alle note di rilascio. Non viene inviato nulla sulla Sua installazione. Lo disattivi con **Nuove versioni** in **Nodi → Aggiornamenti e registrazione**.

**La disinstallazione** rimuove il programma, il suo servizio e le regole del firewall. Registrazioni, configurazione e `%ProgramData%\LarisVMS` vengono conservati.

## Installazione dai sorgenti

Gli sviluppatori possono compilare e installare dal repository con PowerShell: `install-web.ps1` (server) e gli script `install-node.ps1` / `install-proxy.ps1` nei pacchetti prodotti da `build-node.ps1` / `build-proxy.ps1`. `build-installers.ps1` produce i tre MSI. Un MSI aggiorna sul posto una macchina installata con script, conservandone impostazioni, registrazione ed eventuale endpoint di streaming diretto con la sua regola del firewall.

## Certificati

`Kestrel:Certificates:Default:Path` e `:Password` in `appsettings.Production.json` puntano a un file `.pfx`. Il server lo controlla ogni 60 secondi e applica un certificato rinnovato senza riavvio.
