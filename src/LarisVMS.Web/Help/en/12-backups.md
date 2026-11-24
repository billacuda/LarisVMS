# Backups

Database backups, and what else needs backing up.

**Settings → Backups** takes full SQL Server backups of the LarisVMS database, on a daily schedule or on demand. It's meant for SQL Express, which has no SQL Agent to schedule its own backups.

- **Backup directory**: a folder the *SQL Server service* can write to, outside the LarisVMS install folder. If SQL Server is on another machine, this path must exist on that machine, and old backups aren't cleaned up automatically. A Linux LarisVMS server can use a Windows path such as `D:\Backups` here when SQL Server runs on Windows.
- **Keep last**: how many backups to keep (local SQL Server only).

Restoring is done outside LarisVMS, with SSMS or `sqlcmd`. Backups are full backups only. On SQL Express, use the SIMPLE recovery model.

## MariaDB

On MariaDB, backups are made with `mariadb-dump` and saved as compressed `.sql.gz` files. `mariadb-dump` runs on the LarisVMS server, so the MariaDB client tools must be installed there, even when MariaDB itself runs on another machine. LarisVMS finds them under `C:\Program Files\MariaDB …`, or a Super Admin can set the path to `mariadb-dump.exe` on the Backups page. On a Linux server, install the client package (`sudo apt install mariadb-client`, or `sudo dnf install mariadb`), and LarisVMS finds `mariadb-dump` on the path. That path has to be on this server, in a folder only administrators can change (such as under Program Files), since LarisVMS runs it with the service's own rights.

- **Backup directory**: a folder on the LarisVMS server (or a share) that the *LarisVMS service* can write to, outside the install folder. Old backups are always cleaned up, wherever MariaDB runs.
- The backup holds the whole database in one consistent snapshot, taken without stopping recording.

Unzip the `.sql.gz` (7-Zip, for example), then restore it with the `mariadb` client. The file creates the database if it's missing and replaces its tables, so stop the LarisVMS Web service first:

```powershell
cmd /c "mariadb -u root -p < larisvms_20261026_030000.sql"
```

On Linux, `sudo systemctl stop larisvms-web`, then `gunzip -c larisvms_20261026_030000.sql.gz | mariadb -u root -p`.

## Also back up

- **Data-protection keys** at `%ProgramData%\LarisVMS\keys` (`/var/lib/larisvms/keys` on Linux). Camera passwords, email secrets, and node keys are encrypted with them. Without these keys, a restored database can't decrypt any of them.
- **`setup-generated.json`** in the install folder (`/etc/larisvms` on Linux), which holds the database connection string. Its password only works on the machine that saved it; on a new machine, run the setup wizard again and point it at the restored database.

Recordings are not part of the database backup. Protect them with the archive volume or your own storage backups.
