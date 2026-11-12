# Utenti e accesso

Account, ruoli, autorizzazioni, accesso alle telecamere, Active Directory e accesso con Microsoft.

## Utenti

L’auto-registrazione è disattivata. Gli account vengono creati in **Impostazioni → Utenti**, dove può anche assegnare ruoli, disattivare un account e reimpostare una password. Gli account locali accedono con il proprio indirizzo e-mail. Disattivare un account lo disconnette subito ovunque e chiude il suo video dal vivo.

## Password

Le password hanno almeno 12 caratteri. Quelle comuni, quelle come `123456789012` e quelle che contengono il nome o l’indirizzo e-mail della persona vengono rifiutate. Per rifiutarne altre, le inserisca in `%ProgramData%\LarisVMS\password-blocklist.txt` sul server web, una per riga (va bene un elenco pubblicato di password compromesse). Viene recepito senza riavvio.

Chiunque abbia un account locale cambia la propria password selezionando il proprio nome in alto nella pagina. La password attuale è richiesta e tutte le altre sue sessioni vengono disconnesse. Gli amministratori cambiano la propria password nello stesso modo, non in **Impostazioni → Utenti**. Gli account Active Directory cambiano la propria in Windows.

Quando crea un account o reimposta una password in **Impostazioni → Utenti**, può selezionare **Deve cambiarla al prossimo accesso**. La persona deve allora scegliere la propria password prima di poter fare qualsiasi altra cosa. Con **Impostazioni → Sicurezza → Password temporanee impostate dall’amministratore** attivo, parte selezionato.

Dopo 5 password errate per un account da un indirizzo entro 15 minuti, quell’indirizzo deve attendere lo scadere dei 15 minuti; la persona può comunque accedere da qualsiasi altro luogo. Un account è bloccato per 15 minuti solo dopo 20 tentativi errati da qualsiasi luogo, e i Super Admin non vengono mai bloccati da password errate all’accesso. I Super Admin vengono informati di ripetuti errori e blocchi, e di tentativi distribuiti su molte reti: un account che fallisce 10 volte da 3 o più reti, o 50 errori da 10 o più reti sull’intero server, entro 15 minuti.

## Accesso a due fattori

Chiunque acceda con una password, locale o Active Directory, può anche richiedere un codice di sei cifre da un’app di autenticazione sul telefono (Microsoft Authenticator, Google Authenticator, 1Password e altre). Ogni codice funziona una sola volta, quindi uno che qualcuno vede digitare non può essere riutilizzato. Per configurarlo, selezioni il Suo nome in alto nella pagina, poi **Accesso a due fattori**, scansioni il codice QR e inserisca il codice mostrato dall’app. Riceve dieci codici di ripristino; li conservi in un luogo sicuro, poiché ognuno consente un accesso se perde il telefono. Modificare qualsiasi cosa in seguito, come nuovi codici di ripristino o la disattivazione, richiede un codice attuale.

All’accesso, **Non chiedere più su questo browser per 14 giorni** salta il codice su quel browser finché la password o la configurazione a due fattori cambia.

Se qualcuno perde il telefono e i codici di ripristino, **Disattiva 2FA** accanto al suo nome in **Impostazioni → Utenti** gli permette di accedere con la sola password e di configurarlo di nuovo.

**Impostazioni → Sicurezza → Accesso a due fattori** obbliga chiunque acceda con una password a configurarlo prima di poter fare qualsiasi altra cosa, e impedisce di disattivarlo. Gli accessi Microsoft sono lasciati ai controlli propri di Entra.

## Sessioni

Selezioni il Suo nome in alto nella pagina, poi **Sessioni**, per vedere ogni browser su cui ha effettuato l’accesso, con il suo indirizzo e l’ultimo utilizzo. Disconnetta quelli che non riconosce, o tutti tranne quello che sta usando, poi cambi la password. Cambiare la password o attivare l’accesso a due fattori disconnette subito le Sue altre sessioni.

Con **Impostazioni → Sicurezza → Conferma la password per le pagine di amministrazione** attivo, le pagine di amministrazione richiedono di nuovo la password (e un codice, con l’accesso a due fattori attivo) a meno che abbia effettuato l’accesso o l’abbia confermata negli ultimi 15 minuti; usarle mantiene valido il periodo. Chi trova il Suo browser con l’accesso effettuato può allora usare il sistema ma non modificarne impostazioni, utenti o chiavi. Gli accessi Microsoft effettuano invece di nuovo l’accesso. È disattivato per impostazione predefinita.

In **Impostazioni → Utenti**, **Disconnetti** accanto a una persona la disconnette da ogni browser. Può accedere di nuovo; disattivi l’account per tenerla fuori.

## Ruoli

Un ruolo è un insieme di autorizzazioni con un nome, gestito in **Impostazioni → Ruoli**. Ogni ruolo ha anche:

- un **livello di ambito**;
- una **scadenza** per l’accesso a tempo (ad esempio 480 minuti per un turno di 8 ore);
- una **durata della sessione**: ore dopo l’accesso prima che l’utente debba accedere di nuovo, per quanto sia attivo (al massimo 30 giorni, o 0 per non scadere mai);
- un **timeout di inattività**: minuti senza aprire una pagina prima che l’utente venga disconnesso (0 è disattivato). Uno schermo che mostra video dal vivo conta come inattivo, quindi lo lasci disattivato per i ruoli che guardano un muro video;
- una **priorità PTZ**, che decide chi vince quando due utenti muovono la stessa telecamera.

## Autorizzazioni

**Impostazioni → Matrice delle autorizzazioni** imposta quali azioni è consentito a ciascun ruolo (vedere telecamere, riproduzione, esportazione, modificare impostazioni e così via). Il ruolo integrato **Super Admin** ha sempre ogni autorizzazione.

**Avvia e interrompi la registrazione manuale** controlla il pulsante di registrazione sui riquadri dal vivo. Richiede anche l’accesso **Registrazione** alle telecamere qui sotto.

## Accesso alle telecamere

**Impostazioni → Accesso alle telecamere** decide quali telecamere ogni ruolo può usare: ogni telecamera, un gruppo o singole telecamere, separatamente per Vedere, Riproduzione, Esportazione, PTZ, Parla (audio bidirezionale), Configura e Registra (registrazione manuale). Un ruolo **senza concessioni non ha telecamere**; il Super Admin ha sempre ogni telecamera. L’esportazione non va mai oltre la riproduzione. L’aggiornamento da una versione in cui i ruoli senza concessioni potevano usare ogni telecamera aggiunge una concessione «tutte le telecamere» per mantenere invariato l’accesso di ogni ruolo e utente.

## Chiavi API

**Admin → Chiavi API** emette chiavi per l’[API](/Help/api). Ogni chiave agisce con il ruolo a cui è associata e può essere limitata ad alcune delle autorizzazioni di quel ruolo, quindi più chiavi possono fare cose diverse. L’accesso alle telecamere del ruolo decide quali telecamere una chiave può usare.

## Entra

**Impostazioni → Sicurezza → Accesso Entra ID** aggiunge un pulsante «Accedi con Microsoft». Non crea mai account e non abbina gli account per e-mail: in **Impostazioni → Utenti**, usi **Accesso con Microsoft** sulla riga di una persona per inserire il suo **ID oggetto** Entra (dalla sua pagina utente nell’interfaccia di amministrazione di Microsoft Entra). Il suo primo accesso Microsoft completa il collegamento; vuoto lo rimuove. Registri un’app in Entra con l’URI di reindirizzamento mostrato nella pagina Sicurezza, poi inserisca il suo ID tenant (il GUID o il dominio della Sua directory, non `common`), l’ID client e il segreto. Le modifiche hanno effetto subito. Gli account già collegati continuano a funzionare.

## Active Directory

**Impostazioni → Active Directory** permette agli utenti di dominio di accedere con nome utente e password AD e assegna loro ruoli in base ai gruppi AD. Solo un Super Admin può modificare il dominio, il modo in cui LarisVMS vi si connette o l’accesso locale, poiché questi decidono chi può accedere come chiunque; gli altri amministratori possono modificare l’intervallo di sincronizzazione e i collegamenti dei gruppi.

- **Dominio:** solo il nome di dominio, come `corp.example.com`.
- **Cifratura:** LDAPS sulla porta 636 è il valore predefinito e richiede un certificato sui controller di dominio considerato attendibile da questo server. LDAP semplice sulla porta 389 funziona senza, ma non è sicuro e non è consigliato.
- **Account di ricerca:** la sicurezza integrata (predefinita) usa l’account stesso del servizio LarisVMS Web. Quando il servizio viene eseguito come LocalSystem su un server aggiunto al dominio, è l’account computer. Oppure inserisca un account di servizio come `DOMINIO\nomeutente` con la sua password.
- **Accesso:** un nome con una `@` è un account locale. Tutto il resto è un nome utente AD, digitato senza il dominio.

**Collegamenti gruppo → ruolo:** inizi a digitare il nome di un gruppo e scelga dall’elenco (Tab prende la prima corrispondenza). I membri del gruppo ricevono il ruolo, compresi i membri dei gruppi annidati. Gli account vengono creati la prima volta che servono, non appena un gruppo viene collegato o quando l’utente accede per la prima volta. Gruppi e utenti sono tracciati per SID, quindi rinominare l’uno o l’altro in AD viene recepito alla sincronizzazione successiva. I ruoli degli account AD derivano solo dai collegamenti dei gruppi e non possono essere modificati nella pagina Utenti.

**La sincronizzazione** viene eseguita ogni 30 minuti per impostazione predefinita (da 5 minuti a 24 ore), o subito con **Sincronizza ora**. Un account AD disattivato o scaduto in AD, o che non è più in alcun gruppo collegato, viene disattivato qui e disconnesso. Se AD non è raggiungibile, la sincronizzazione non modifica nulla e riprova dopo 5 minuti. Un account disattivato dalla sincronizzazione viene riattivato quando torna a qualificarsi. Uno disattivato a mano nella pagina Utenti resta disattivato.

**L’accesso locale** può essere disattivato mentre l’accesso AD è attivo, una volta che almeno un account AD ha il ruolo Super Admin. Conservi in un luogo sicuro l’account Super Admin creato durante la configurazione, e la sua password. Se AD diventa non disponibile, esegua questo sul database LarisVMS e acceda con quell’account. Su SQL Server:

```sql
UPDATE ActiveDirectorySettings SET LocalLoginsEnabled = 1
```

Su MariaDB:

```sql
UPDATE `ActiveDirectorySettings` SET `LocalLoginsEnabled` = 1;
```

## Registro di audit

Tutto ciò che fanno gli utenti è registrato in **Registri → Registri di audit**: modifiche (con valori vecchi e nuovi; i segreti sono registrati solo come «modificato»), visualizzazione delle telecamere, avvio della riproduzione ed esportazioni.

**A prova di manomissione.** Ogni voce è sigillata insieme a quella precedente, usando una chiave che il solo database non possiede. Ogni 6 ore (e quando seleziona **Controlla ora** nella pagina del registro di audit) viene controllato l’intero registro. Se una voce è stata modificata, rimossa o aggiunta direttamente nel database, la pagina dice quale e i Super Admin vengono avvisati. Le voci rimosse dalla conservazione sono attese e non contano, e ogni rimozione è a sua volta registrata (**AuditLog.Pruned**). Le voci precedenti a questo aggiornamento non sono coperte.

Il server conserva anche un piccolo record fuori dal database, in `%ProgramData%\LarisVMS\audit-chain.dat`, della chiave del sigillo e di dove è iniziato il registro, così il registro non può essere risigillato con un’altra chiave né cancellato e fatto ripartire senza che si noti. Se ricomincia deliberatamente con un database nuovo e vuoto sullo stesso server, elimini quel file una volta; ne viene creato uno nuovo con la voce successiva.

**Inoltro a syslog.** Per conservare una copia del registro di audit fuori dal server, inserisca un server syslog in **Impostazioni → Registri**. Ogni nuova voce viene inviata mentre viene scritta (RFC 5424, facility *log audit*, con la voce come JSON). Scelga UDP, TCP o TLS. TLS richiede che il certificato del server syslog sia considerato attendibile dal server web. Se il server syslog non è raggiungibile, le voci vengono conservate e inviate quando torna disponibile. L’inoltro inizia con la voce successiva, non con la cronologia esistente. Solo un Super Admin può cambiare dove viene inoltrato il registro, o disattivare l’inoltro; quella modifica viene a sua volta inviata prima alla vecchia destinazione.
