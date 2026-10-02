# Backups

Database backups, and what else needs backing up.

**Settings → Backups** takes full SQL Server backups of the LarisVMS database, on a daily schedule or on demand. It's meant for SQL Express, which has no SQL Agent to schedule its own backups.

- **Backup directory**: a folder the *SQL Server service* can write to, outside the LarisVMS install folder. If SQL Server is on another machine, this path must exist on that machine, and old backups aren't cleaned up automatically.
- **Keep last**: how many backups to keep (local SQL Server only).

Restoring is done outside LarisVMS, with SSMS or `sqlcmd`. Backups are full backups only. On SQL Express, use the SIMPLE recovery model.

## Also back up

- **Data-protection keys** at `%ProgramData%\LarisVMS\keys`. Camera passwords, email secrets and node keys are encrypted with them. Without these keys, a restored database can't decrypt any of them.
- **`setup-generated.json`** in the install folder, which holds the database connection string.

Recordings are not part of the database backup. Protect them with the archive volume or your own storage backups.
