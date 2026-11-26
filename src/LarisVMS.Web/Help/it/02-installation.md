# Installazione e aggiornamenti

Installare il server web, i nodi di registrazione e i proxy multimediali, e mantenerli aggiornati.

Ogni parte ha il proprio programma di installazione: `LarisVMS-Web`, `LarisVMS-Node` e `LarisVMS-Proxy` (`.msi`). Faccia doppio clic su uno per ricevere la richiesta delle sue impostazioni, oppure le passi dalla riga di comando con `msiexec /i <file>.msi NOME=valore … /qn`. Tutto ciò che non viene fornito viene richiesto, e un aggiornamento riutilizza le impostazioni dell’installazione precedente.

Ogni parte funziona anche su Linux, da un pacchetto `.deb` o `.rpm` (vedere [Linux](#linux)), e i due sistemi si possono combinare: un server Windows può avere nodi e proxy Linux, e viceversa.

## Server web

1. Disponga di un SQL Server raggiungibile dal server (sicurezza integrata o autenticazione SQL), o di un server MariaDB (vedere [MariaDB](#mariadb)).
2. Esegua `LarisVMS-Web.msi`. Impostazioni:
   - `HTTPSPORT`: la porta HTTPS (predefinita 8444). Il programma di installazione la apre nel firewall, per TCP e per UDP ([HTTP/3](/Help/security#http3)).
   - `CERTPATH`, `CERTPASSWORD`: il certificato del server (`.pfx`). Lasci vuoto per usare un certificato autofirmato finché non ne ha uno. Il programma di installazione verifica che il file si apra con la password prima di continuare; una condivisione di file deve inoltre essere leggibile dall’account del servizio.
   - `SERVICEACCOUNT`, `SERVICEPASSWORD`: esegue il servizio come un account specifico, ad esempio uno con diritti di sicurezza integrata SQL. Vuoto lo esegue come LocalSystem. Non necessario per MariaDB.
3. Vada su `https://<host>:8444/`. La procedura guidata di installazione accompagna nella configurazione di database, primo account amministratore, archiviazione e registrazione dei nodi. Un browser sul server stesso o sulla rete locale entra direttamente. Da qualsiasi altro luogo chiede prima il token di installazione in `%ProgramData%\LarisVMS\setup-token.txt` sul server (`/var/lib/larisvms/setup-token.txt` su Linux; leggibile solo dagli amministratori), così nessuno su Internet può configurarlo per primo. Per richiedere il token da ogni altro computer, rete locale compresa, imposti `"Setup": { "AlwaysRequireToken": true }` in `appsettings.Production.json` prima di aprire la procedura guidata. Il file viene eliminato una volta creato l’account amministratore.

La password di un accesso SQL, o di MariaDB, è memorizzata cifrata sul server (DPAPI su Windows, o una chiave legata all’ID della macchina su Linux; solo questa macchina), quindi `setup-generated.json` non può essere copiato su un’altra macchina con la password ancora funzionante. Esegua invece di nuovo la procedura guidata lì.

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

Per il rilevamento IA, le macchine NVIDIA richiedono anche CUDA 12 e cuDNN 9; su Windows il programma di installazione copia le loro DLL al posto giusto quando le trova. Vedere [Rilevamento IA](/Help/ai-detection#hardware).

Dopo l’installazione, il nodo compare in **Nodi** come **in attesa di approvazione**. Non registra nulla e non riceve telecamere finché non fa clic su **Approva**, quindi approvi solo un nodo che ha installato Lei stesso. Poi assegni le telecamere dalla pagina di modifica di ciascuna telecamera.

## Proxy multimediali

Esegua `LarisVMS-Proxy.msi` con `SERVERURL`, `REGISTRATIONKEY` e `CLIENTPORT` (predefinita 4443), più le stesse impostazioni facoltative del certificato di un nodo. Un nuovo proxy parte disattivato: attivi **Attivato** per esso in **Impostazioni → Proxy multimediali**, poi lo scelga nella pagina di ogni nodo (vedere [Nodi](/Help/nodes#proxies)).

## Linux

Il server web, i nodi e i proxy si installano da pacchetti `.deb` (Ubuntu 22.04, 24.04, 26.04, Debian 12, 13) o `.rpm` (Rocky Linux 9, 10 e altre derivate di RHEL 9 o 10), per x86_64. Ognuno viene eseguito come servizio systemd (`larisvms-web`, `larisvms-node`, `larisvms-proxy`) con il proprio account `larisvms`. Il programma va in `/opt/larisvms`, le impostazioni in `/etc/larisvms`, i dati (chiavi, modelli, token di installazione) in `/var/lib/larisvms` e i file di registro in `/var/log/larisvms`. Non serve alcun runtime .NET. L’installazione apre le porte del servizio in firewalld o ufw quando uno dei due è attivo.

- **Server web:** `sudo apt install ./larisvms-web_<version>-1_amd64.deb` (o `sudo dnf install ./larisvms-web-<version>-1.x86_64.rpm`). Si avvia subito; apra `https://<host>:8444/` per la procedura guidata di installazione. Porta e certificato si impostano in `/etc/larisvms/web.env` (`Kestrel__HttpsPort`, `Kestrel__Certificates__Default__Path`, `Kestrel__Certificates__Default__Password`; l’account larisvms deve poter leggere il file), poi `sudo systemctl restart larisvms-web`. Anche `appsettings.Production.json` e `setup-generated.json` sono in `/etc/larisvms`. Su Linux SQL Server richiede un accesso SQL; lì MariaDB è la scelta più semplice.
- **Nodi:** installi `larisvms-node`, poi lo associ con `sudo larisvms-node setup --server-url https://<server>:8444 --registration-key <key>`. Il passaggio Nodo della procedura guidata di installazione mostra questo comando con la chiave e l’impronta del certificato del server già compilate. Registra in `/var/lib/larisvms/recordings` a meno che non aggiunga `--storage-root <folder>`; una condivisione di rete deve prima essere montata (ad esempio in `/etc/fstab`) ed essere scrivibile dall’account larisvms. Altre opzioni: `--archive-root`, `--live-port`, `--live-allow-from`, `--client-port`, `--client-pfx-path`, `--client-endpoint-host` e `--ffmpeg-path`. `sudo larisvms-node setup --help` le elenca tutte, ed eseguendo di nuovo `setup` si modificano.
- **ffmpeg** proviene dalla distribuzione e viene installato con il nodo. Su Rocky Linux proviene da RPM Fusion, quindi attivi EPEL, CRB e RPM Fusion prima di installare un nodo.
- **Proxy:** installi `larisvms-proxy`, poi `sudo larisvms-proxy setup --server-url https://<server>:8444 --registration-key <key> --client-port 4443`.

Controlli un servizio con `systemctl status larisvms-node` e `journalctl -u larisvms-node`, e ne mostri la versione con `larisvms-node --version`.

**Docker:** ogni versione è pubblicata anche come immagini nel registro dei container di GitHub: `ghcr.io/billacuda/larisvms-web`, `larisvms-node` (con i tag `-cuda` e `-tensorrt` per le GPU NVIDIA) e `larisvms-proxy`. Il server web richiede un database gestito da Lei (MariaDB 10.11 o successivo, oppure SQL Server con un accesso SQL). Lo installi in uno di questi modi:

- **Docker Compose:** scarichi [compose.yml](https://github.com/billacuda/LarisVMS/blob/main/docker/compose.yml) ed esegua `docker compose up -d` nella sua cartella. Avvia il server web; apra `https://<host>:8444/` per la procedura guidata di installazione. Poi rimuova i commenti dal nodo (e dal proxy, se lo desidera) nel file, inserisca l’indirizzo del server, la chiave di registrazione e la cartella delle registrazioni, ed esegua di nuovo `docker compose up -d`.
- **Stack Portainer:** in **Stacks → Add stack → Web editor**, incolli lo stesso file e scelga **Deploy the stack**. Dopo la procedura guidata, rimuova i commenti dal nodo nella scheda **Editor** dello stack e scelga **Update the stack**. Usi un ambiente Docker autonomo, non uno Swarm.
- **Solo Docker:** avvii il server web, esegua la procedura guidata, poi avvii il nodo:

```bash
docker run -d --name larisvms-web --network host --restart unless-stopped \
  -e Kestrel__HttpsPort=8444 \
  -v /srv/larisvms/web/data:/var/lib/larisvms \
  -v /srv/larisvms/web/config:/etc/larisvms \
  -v /srv/larisvms/web/logs:/var/log/larisvms \
  ghcr.io/billacuda/larisvms-web:latest

docker run -d --name larisvms-node --hostname larisvms-node-1 --network host --restart unless-stopped \
  -e LARISVMS_SERVER_URL=https://<server address>:8444 \
  -e LARISVMS_REGISTRATION_KEY=<key> \
  -e LARISVMS_STORAGE_ROOT=/recordings \
  -v /srv/larisvms/node/data:/var/lib/larisvms \
  -v /srv/larisvms/node/config:/etc/larisvms \
  -v /srv/larisvms/node/logs:/var/log/larisvms \
  -v /srv/larisvms/recordings:/recordings \
  ghcr.io/billacuda/larisvms-node:latest
```

Approvi poi il nodo in **Nodi**. Come indirizzo del server usi il suo indirizzo o nome sulla LAN, non `localhost`, anche sulla stessa macchina. Se la macchina ha un firewall, consenta TCP 8444 (server web), 8554 (nodo), 8555 (un nodo che trasmette direttamente ai browser) e 4443 (proxy), e UDP 8444 se usa HTTP/3. I container vengono eseguiti come utente e gruppo 8444, proprietario delle loro cartelle sull’host.

Il passaggio Nodo della procedura guidata di installazione e la pagina **Proxy multimediali** mostrano uno stack Compose per Portainer o `docker compose` con indirizzo, chiave di registrazione e certificato di questo server già compilati. Le impostazioni sono le stesse variabili d’ambiente usate dai servizi Linux (`LARISVMS_SERVER_URL`, `LARISVMS_REGISTRATION_KEY`, `LARISVMS_STORAGE_ROOT` e così via). Tenga `/var/lib/larisvms` su un volume: contiene le chiavi, la registrazione e l’ID della macchina che mantiene leggibili le password salvate quando il container viene ricreato. Usi la rete dell’host, così il rilevamento delle telecamere funziona. Per aggiornare, esegua `docker compose pull` e `docker compose up -d`, scelga **Update the stack** con **Re-pull image and redeploy** attivo in Portainer, oppure, con il solo Docker, scarichi l’immagine, rimuova il container ed esegua di nuovo il suo comando.

**Spostare un’installazione Linux in Docker:** copi la cartella dei dati (soprattutto `keys`) e l’ID della macchina prima del primo avvio del container, e faccia prima il backup del database. La password del database e le registrazioni dei nodi sono cifrate con l’ID della macchina, e le password e le chiavi dei nodi nel database con `keys`; senza, pagine come **Nodi** mostrano un errore. Un’installazione Windows non può essere spostata così. Per il server web:

```bash
sudo systemctl disable --now larisvms-web
sudo mkdir -p /srv/larisvms/web/data /srv/larisvms/web/config
sudo tar -C /var/lib/larisvms --exclude=./recordings --exclude=./node.config --exclude=./proxy.config -cf - . \
  | sudo tar -C /srv/larisvms/web/data -xf -
sudo cp -a /etc/larisvms/. /srv/larisvms/web/config/
sudo install -m 600 /etc/machine-id /srv/larisvms/web/data/machine-id
```

Riporti le impostazioni di `/etc/larisvms/web.env` in `environment` dello stack, poi avvii il container. Si connette allo stesso database, senza la procedura guidata. Non esegua mai il vecchio servizio e il container contemporaneamente. Per un nodo, lasci le registrazioni dove sono:

```bash
sudo systemctl disable --now larisvms-node
sudo mkdir -p /srv/larisvms/node/data
sudo tar -C /var/lib/larisvms --exclude=./recordings --exclude=./keys --exclude=./update --exclude=./proxy.config -cf - . \
  | sudo tar -C /srv/larisvms/node/data -xf -
sudo install -m 600 /etc/machine-id /srv/larisvms/node/data/machine-id

# The container's account needs to own the recordings (repeat for an archive folder):
sudo chown -R 8444:8444 /var/lib/larisvms/recordings
```

Riporti le impostazioni di `/etc/larisvms/node.env` in `environment` dello stack, e monti ogni cartella di registrazioni e di archivio nello stesso percorso dentro il container (ad esempio `/var/lib/larisvms/recordings:/var/lib/larisvms/recordings`, con `LARISVMS_STORAGE_ROOT` impostato su di esso). Il nodo torna com’era, con le sue telecamere e registrazioni, senza bisogno di chiave di registrazione né di approvazione. Un proxy si sposta allo stesso modo, senza le registrazioni. Rimuovendo poi i vecchi pacchetti si chiudono le porte nel firewall, quindi le riapra per i container.

Se ha già eseguito la procedura guidata nel container con il vecchio database, mancano solo le chiavi. Le copi accanto a quelle del container e conservi entrambe:

```bash
docker stop larisvms-web
sudo docker cp /var/lib/larisvms/keys/. larisvms-web:/var/lib/larisvms/keys/
docker start larisvms-web
```

Dove la Guida cita una cartella Windows, quella di Linux è: `%ProgramData%\LarisVMS` → `/var/lib/larisvms` (la sua cartella `logs` → `/var/log/larisvms`), e `appsettings.Production.json` e `setup-generated.json` della cartella di installazione → `/etc/larisvms`. `Restart-Service LarisVMSWeb` è `sudo systemctl restart larisvms-web`.

## Aggiornamenti

**Aggiornamento:** esegua il `.msi` più recente. Trova l’installazione esistente e passa direttamente a **Pronto per l’aggiornamento**; le impostazioni sono ricordate, quindi non serve inserire di nuovo nulla tranne la password di un account di servizio di dominio. Il server web applica da sé le eventuali modifiche al database all’avvio. Gli aggiornamenti non toccano mai registrazioni, file di configurazione, registrazione dei nodi né chiavi di protezione dei dati, e un’installazione non riuscita viene annullata.

**Nodi e proxy si aggiornano da soli.** Una nuova installazione web registra come **In attesa** le build di nodo e proxy che contiene nella sezione **Build** di **Nodi**. Solo la build di nodo più recente e la build di proxy più recente attendono l’approvazione; le più vecchie sono **Sostituite**. **Cronologia delle build** elenca ogni build. Ne approvi una e ogni nodo più vecchio la scarica, la verifica e la installa al suo prossimo check-in. Il server firma ogni aggiornamento con la sua chiave di firma (vedere **Impostazioni → Cifratura e firma**); nodi e proxy conservano quella chiave da quando si sono registrati e non installano nulla che non sia firmato con essa, anche se la loro connessione al server fosse manomessa. Lo disattivi con **Aggiornamento automatico** in **Nodi → Aggiornamenti e registrazione**.

**Nuove versioni:** una volta al giorno (e all’avvio del servizio) il server controlla le release GitHub di LarisVMS. Quando è disponibile una versione più recente, le persone che possono modificare le impostazioni vedono **✨ È disponibile la versione X** nella barra superiore, con un link alle note di rilascio. Non viene inviato nulla sulla Sua installazione. Lo disattivi con **Nuove versioni** in **Nodi → Aggiornamenti e registrazione**.

**Su Linux,** aggiorni il server web installando il pacchetto più recente allo stesso modo; `/etc/larisvms` e `/var/lib/larisvms` vengono conservati. I nodi e i proxy Linux si aggiornano da soli dal server come quelli Windows, una volta approvata la build: un piccolo programma di supporto eseguito come root installa l’aggiornamento che il servizio ha scaricato e verificato. Un server Linux offre ai nodi Windows la loro build Windows, e un server Windows offre ai nodi Linux la loro.

**La disinstallazione** rimuove il programma, il suo servizio e le regole del firewall. Registrazioni, configurazione e `%ProgramData%\LarisVMS` vengono conservati. Su Linux, `apt remove` o `dnf remove` fa lo stesso, conservando `/var/lib/larisvms`, `/var/log/larisvms` e `/etc/larisvms`.

## Installazione dai sorgenti

Gli sviluppatori possono compilare e installare dal repository con PowerShell: `install-web.ps1` (server) e gli script `install-node.ps1` / `install-proxy.ps1` nei pacchetti prodotti da `build-node.ps1` / `build-proxy.ps1`. `build-installers.ps1` produce i tre MSI e i pacchetti Linux `.deb` e `.rpm` (con nFPM). Un MSI aggiorna sul posto una macchina installata con script, conservandone impostazioni, registrazione ed eventuale endpoint di streaming diretto con la sua regola del firewall.

## Certificati

`Kestrel:Certificates:Default:Path` e `:Password` in `appsettings.Production.json` puntano a un file `.pfx` (su Linux, impostabili anche in `/etc/larisvms/web.env` come `Kestrel__Certificates__Default__Path` e `__Password`). Il server lo controlla ogni 60 secondi e applica un certificato rinnovato senza riavvio.

In Docker, monti la cartella del certificato (`/srv/larisvms/certs:/certs:ro`) e usi il percorso all’interno del container, come `/certs/vms.pfx`. L’utente 8444 deve poterlo leggere, e un `$` nella password si scrive `$$` in un file Compose. Un certificato che non può essere caricato viene sostituito da uno autofirmato, e `docker logs` ne indica il motivo.
