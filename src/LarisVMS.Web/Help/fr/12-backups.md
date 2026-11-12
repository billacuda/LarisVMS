# Sauvegardes

Sauvegardes de la base de données, et ce qu’il faut aussi sauvegarder.

**Paramètres → Sauvegardes** effectue des sauvegardes complètes SQL Server de la base de données LarisVMS, selon un planning quotidien ou à la demande. C’est conçu pour SQL Express, qui n’a pas d’agent SQL pour planifier ses propres sauvegardes.

- **Répertoire de sauvegarde** : un dossier dans lequel le *service SQL Server* peut écrire, en dehors du dossier d’installation de LarisVMS. Si SQL Server est sur une autre machine, ce chemin doit exister sur cette machine, et les anciennes sauvegardes ne sont pas nettoyées automatiquement.
- **Conserver les** : combien de sauvegardes garder (SQL Server local uniquement).

La restauration se fait en dehors de LarisVMS, avec SSMS ou `sqlcmd`. Les sauvegardes sont uniquement complètes. Sur SQL Express, utilisez le modèle de récupération SIMPLE.

## MariaDB

Sur MariaDB, les sauvegardes sont réalisées avec `mariadb-dump` et enregistrées sous forme de fichiers compressés `.sql.gz`. `mariadb-dump` s’exécute sur le serveur LarisVMS ; les outils clients MariaDB doivent donc y être installés, même quand MariaDB lui-même s’exécute sur une autre machine. LarisVMS les trouve sous `C:\Program Files\MariaDB …`, ou un super administrateur peut définir le chemin de `mariadb-dump.exe` sur la page Sauvegardes. Ce chemin doit se trouver sur ce serveur, dans un dossier que seuls les administrateurs peuvent modifier (par exemple sous Program Files), car LarisVMS l’exécute avec les droits propres du service.

- **Répertoire de sauvegarde** : un dossier sur le serveur LarisVMS (ou un partage) dans lequel le *service LarisVMS* peut écrire, en dehors du dossier d’installation. Les anciennes sauvegardes sont toujours nettoyées, où que MariaDB s’exécute.
- La sauvegarde contient toute la base de données en un instantané cohérent, pris sans arrêter l’enregistrement.

Décompressez le `.sql.gz` (avec 7-Zip, par exemple), puis restaurez-le avec le client `mariadb`. Le fichier crée la base de données si elle est absente et remplace ses tables ; arrêtez donc d’abord le service LarisVMS Web :

```powershell
cmd /c "mariadb -u root -p < larisvms_20261026_030000.sql"
```

## À sauvegarder aussi

- **Les clés de protection des données** dans `%ProgramData%\LarisVMS\keys`. Les mots de passe des caméras, les secrets d’e-mail et les clés des nœuds sont chiffrés avec elles. Sans ces clés, une base de données restaurée ne peut déchiffrer aucun d’entre eux.
- **`setup-generated.json`** dans le dossier d’installation, qui contient la chaîne de connexion à la base de données. Son mot de passe ne fonctionne que sur la machine qui l’a enregistré ; sur une nouvelle machine, exécutez de nouveau l’assistant d’installation et pointez-le vers la base de données restaurée.

Les enregistrements ne font pas partie de la sauvegarde de la base de données. Protégez-les avec le volume d’archive ou vos propres sauvegardes de stockage.
