# Sicherungen

Datenbanksicherungen und was sonst gesichert werden muss.

**Einstellungen → Sicherungen** erstellt vollständige SQL-Server-Sicherungen der LarisVMS-Datenbank, nach einem täglichen Zeitplan oder auf Abruf. Es ist für SQL Express gedacht, das keinen SQL-Agent hat, um eigene Sicherungen zu planen.

- **Sicherungsverzeichnis**: ein Ordner, in den der *SQL-Server-Dienst* schreiben kann, außerhalb des LarisVMS-Installationsordners. Befindet sich SQL Server auf einem anderen Rechner, muss dieser Pfad auf jenem Rechner existieren, und alte Sicherungen werden nicht automatisch aufgeräumt.
- **Letzte behalten**: wie viele Sicherungen aufbewahrt werden (nur bei lokalem SQL Server).

Die Wiederherstellung erfolgt außerhalb von LarisVMS, mit SSMS oder `sqlcmd`. Es gibt nur vollständige Sicherungen. Verwenden Sie bei SQL Express das Wiederherstellungsmodell SIMPLE.

## MariaDB

Bei MariaDB werden Sicherungen mit `mariadb-dump` erstellt und als komprimierte `.sql.gz`-Dateien gespeichert. `mariadb-dump` läuft auf dem LarisVMS-Server, daher müssen die MariaDB-Client-Tools dort installiert sein, auch wenn MariaDB selbst auf einem anderen Rechner läuft. LarisVMS findet sie unter `C:\Program Files\MariaDB …`, oder ein Super-Admin kann den Pfad zu `mariadb-dump.exe` auf der Seite „Sicherungen“ festlegen. Dieser Pfad muss auf diesem Server in einem Ordner liegen, den nur Administratoren ändern können (etwa unter „Programme“), da LarisVMS es mit den eigenen Rechten des Dienstes ausführt.

- **Sicherungsverzeichnis**: ein Ordner auf dem LarisVMS-Server (oder eine Freigabe), in den der *LarisVMS-Dienst* schreiben kann, außerhalb des Installationsordners. Alte Sicherungen werden immer aufgeräumt, wo auch immer MariaDB läuft.
- Die Sicherung enthält die gesamte Datenbank in einem konsistenten Schnappschuss, erstellt ohne die Aufzeichnung zu stoppen.

Entpacken Sie die `.sql.gz` (zum Beispiel mit 7-Zip) und stellen Sie sie dann mit dem `mariadb`-Client wieder her. Die Datei erstellt die Datenbank, falls sie fehlt, und ersetzt ihre Tabellen, beenden Sie daher zuerst den Dienst LarisVMS Web:

```powershell
cmd /c "mariadb -u root -p < larisvms_20261026_030000.sql"
```

## Ebenfalls sichern

- **Datenschutzschlüssel** unter `%ProgramData%\LarisVMS\keys`. Kamerapasswörter, E-Mail-Secrets und Knotenschlüssel sind damit verschlüsselt. Ohne diese Schlüssel kann eine wiederhergestellte Datenbank keines davon entschlüsseln.
- **`setup-generated.json`** im Installationsordner, die die Verbindungszeichenfolge der Datenbank enthält. Ihr Passwort funktioniert nur auf dem Rechner, der es gespeichert hat; führen Sie auf einem neuen Rechner den Einrichtungsassistenten erneut aus und richten Sie ihn auf die wiederhergestellte Datenbank.

Aufnahmen sind nicht Teil der Datenbanksicherung. Schützen Sie sie mit dem Archiv-Volume oder Ihren eigenen Speichersicherungen.
