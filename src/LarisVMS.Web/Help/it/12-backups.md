# Backup

Backup del database e cos’altro va salvato.

**Impostazioni → Backup** esegue backup completi SQL Server del database LarisVMS, con una pianificazione giornaliera o su richiesta. È pensato per SQL Express, che non ha SQL Agent per pianificare i propri backup.

- **Directory di backup**: una cartella in cui il *servizio SQL Server* può scrivere, fuori dalla cartella di installazione di LarisVMS. Se SQL Server è su un’altra macchina, questo percorso deve esistere su quella macchina, e i vecchi backup non vengono ripuliti automaticamente.
- **Conserva gli ultimi**: quanti backup mantenere (solo SQL Server locale).

Il ripristino si fa fuori da LarisVMS, con SSMS o `sqlcmd`. I backup sono solo completi. Su SQL Express, usi il modello di recupero SIMPLE.

## MariaDB

Su MariaDB, i backup sono eseguiti con `mariadb-dump` e salvati come file compressi `.sql.gz`. `mariadb-dump` viene eseguito sul server LarisVMS, quindi gli strumenti client MariaDB devono esservi installati, anche quando MariaDB stesso gira su un’altra macchina. LarisVMS li trova in `C:\Program Files\MariaDB …`, oppure un Super Admin può impostare il percorso di `mariadb-dump.exe` nella pagina Backup. Quel percorso deve trovarsi su questo server, in una cartella modificabile solo dagli amministratori (ad esempio sotto Program Files), perché LarisVMS lo esegue con i diritti propri del servizio.

- **Directory di backup**: una cartella sul server LarisVMS (o una condivisione) in cui il *servizio LarisVMS* può scrivere, fuori dalla cartella di installazione. I vecchi backup vengono sempre ripuliti, ovunque giri MariaDB.
- Il backup contiene l’intero database in un’unica istantanea coerente, presa senza fermare la registrazione.

Decomprima il `.sql.gz` (ad esempio con 7-Zip), poi lo ripristini con il client `mariadb`. Il file crea il database se manca e ne sostituisce le tabelle, quindi fermi prima il servizio LarisVMS Web:

```powershell
cmd /c "mariadb -u root -p < larisvms_20261026_030000.sql"
```

## Salvi anche

- **Le chiavi di protezione dei dati** in `%ProgramData%\LarisVMS\keys`. Password delle telecamere, segreti e-mail e chiavi dei nodi sono cifrati con esse. Senza queste chiavi, un database ripristinato non può decifrare nessuno di essi.
- **`setup-generated.json`** nella cartella di installazione, che contiene la stringa di connessione al database. La sua password funziona solo sulla macchina che l’ha salvata; su una nuova macchina, esegua di nuovo la procedura guidata e la punti al database ripristinato.

Le registrazioni non fanno parte del backup del database. Le protegga con il volume di archivio o con i Suoi backup dell’archiviazione.
