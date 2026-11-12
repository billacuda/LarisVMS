# Installation et mises à niveau

Installer le serveur web, les nœuds d’enregistrement et les proxys multimédias, et les maintenir à jour.

Chaque composant a son propre programme d’installation : `LarisVMS-Web`, `LarisVMS-Node` et `LarisVMS-Proxy` (`.msi`). Double-cliquez sur l’un d’eux pour qu’il vous demande ses paramètres, ou passez-les en ligne de commande avec `msiexec /i <fichier>.msi NOM=valeur … /qn`. Tout ce qui n’est pas fourni est demandé, et une mise à niveau réutilise les paramètres de l’installation précédente.

## Serveur web

1. Disposez d’un SQL Server joignable depuis le serveur (sécurité intégrée ou authentification SQL), ou d’un serveur MariaDB (voir [MariaDB](#mariadb)).
2. Exécutez `LarisVMS-Web.msi`. Paramètres :
   - `HTTPSPORT` : le port HTTPS (8444 par défaut). Le programme d’installation l’ouvre dans le pare-feu, pour TCP et pour UDP ([HTTP/3](/Help/security#http3)).
   - `CERTPATH`, `CERTPASSWORD` : le certificat du serveur (`.pfx`). Laissez vide pour utiliser un certificat auto-signé en attendant d’en avoir un. Le programme d’installation vérifie que le fichier s’ouvre avec le mot de passe avant de continuer ; un partage de fichiers doit aussi être lisible par le compte du service.
   - `SERVICEACCOUNT`, `SERVICEPASSWORD` : exécute le service sous un compte précis, par exemple un compte disposant des droits de sécurité intégrée SQL. Vide : s’exécute sous LocalSystem. Inutile pour MariaDB.
3. Allez sur `https://<hôte>:8444/`. L’assistant d’installation guide la configuration de la base de données, du premier compte administrateur, du stockage et de l’enregistrement des nœuds. Un navigateur sur le serveur lui-même ou sur le réseau local entre directement. De n’importe où ailleurs, il demande d’abord le jeton d’installation situé dans `%ProgramData%\LarisVMS\setup-token.txt` sur le serveur (lisible uniquement par les administrateurs), afin que personne sur Internet ne puisse l’installer en premier. Pour demander le jeton depuis tous les autres ordinateurs, réseau local compris, définissez `"Setup": { "AlwaysRequireToken": true }` dans `appsettings.Production.json` avant d’ouvrir l’assistant. Le fichier est supprimé une fois le compte administrateur créé.

Le mot de passe d’une connexion SQL, ou un mot de passe MariaDB, est stocké chiffré sur le serveur (DPAPI, cette machine uniquement), donc `setup-generated.json` ne peut pas être copié sur une autre machine avec le mot de passe toujours fonctionnel. Exécutez plutôt l’assistant de nouveau là-bas.

Si vous pointez l’assistant vers une base de données LarisVMS existante (par exemple après une réinstallation du serveur), il l’utilise telle quelle : mise à niveau si nécessaire, rien d’ajouté ni écrasé, et les étapes restantes ignorées. Une base de données qui n’est pas une base LarisVMS est refusée. Une fois l’installation terminée, l’assistant ne peut plus être rouvert.

Le serveur n’a besoin d’aucun runtime .NET ; il est inclus. Le certificat peut aussi être modifié plus tard dans `appsettings.Production.json` dans le dossier d’installation. Un `.pfx` renouvelé au même emplacement est pris en compte en une minute, sans redémarrage.

## MariaDB

LarisVMS peut utiliser MariaDB 10.11 ou ultérieur à la place de SQL Server. Dans l’assistant d’installation, choisissez **MariaDB** et saisissez l’hôte, le port (3306 par défaut), le nom de la base de données, ainsi qu’un utilisateur et un mot de passe MariaDB. MariaDB n’a pas de connexion Windows que LarisVMS pourrait utiliser, donc un utilisateur et un mot de passe sont toujours nécessaires.

- **L’utilisateur** doit avoir l’autorisation de créer la base de données. Ou créez-la vous-même d’abord, avec le jeu de caractères `utf8mb4`, et accordez à l’utilisateur `ALL` dessus :

  ```sql
  CREATE DATABASE larisvms CHARACTER SET utf8mb4 COLLATE utf8mb4_uca1400_as_ci;
  CREATE USER 'larisvms'@'%' IDENTIFIED BY 'a-strong-password';
  GRANT ALL ON larisvms.* TO 'larisvms'@'%';
  ```

- **Chiffrement :** la connexion utilise TLS. Le certificat du serveur est vérifié, sauf si MariaDB est sur cette machine, ou si vous cochez **Faire confiance au certificat de ce serveur** (pour un certificat auto-signé sur une autre machine).
- **MariaDB avant 11.4 a TLS désactivé** tant qu’on ne lui donne pas de certificat, et LarisVMS n’enverra pas le mot de passe à une autre machine sans lui. Ajoutez le certificat et sa clé à la section `[mysqld]` de `my.ini`, redémarrez MariaDB, et cochez **Faire confiance au certificat de ce serveur** s’il est auto-signé :

  ```ini
  [mysqld]
  ssl_cert = C:/MariaDB/certs/server-cert.pem
  ssl_key = C:/MariaDB/certs/server-key.pem
  ```

  MariaDB 11.4 et ultérieur configurent eux-mêmes un certificat.
- **Les sauvegardes** nécessitent les outils clients MariaDB sur le serveur LarisVMS (voir [Sauvegardes](/Help/backups)).
- Une installation SQL Server existante ne peut pas être migrée vers MariaDB ; MariaDB est destiné aux nouvelles installations.

## Nœuds

Exécutez `LarisVMS-Node.msi` sur chaque machine d’enregistrement. Il faut :

- `SERVERURL` et `REGISTRATIONKEY` : l’adresse du serveur et la **clé d’enregistrement** sous **Nœuds → Mises à jour et enregistrement**. Elles ne servent qu’à la première inscription du nœud. Changer la clé n’affecte pas les nœuds existants.
- **FFmpeg**, qui n’est pas fourni. Installez-le d’abord avec `winget install ffmpeg --scope machine`. Le programme d’installation le trouve, ou définissez `FFMPEGPATH`.
- `STORAGEROOT` : l’endroit où le nœud enregistre. Il peut aussi être défini plus tard sur la page de modification du nœud.

Facultatifs : `ARCHIVEROOT`, `LIVEPORT` (8554 par défaut), `LIVEALLOWFROM` (adresses autorisées par le pare-feu vers le port du direct, sous forme d’IP ou de blocs CIDR séparés par des virgules : le serveur LarisVMS et les éventuels proxys ; vide autorise tout), `INSECURETLS=1` (n’est plus nécessaire : les nœuds font confiance au certificat auto-signé du serveur à la première utilisation, voir [Certificat du serveur](/Help/nodes#server-certificate)), le point d’accès de diffusion directe (`CLIENTPORT`, `CLIENTENDPOINTHOST`, `CLIENTPFXPATH`, `CLIENTPFXPASSWORD`), et `SERVICEACCOUNT` / `SERVICEPASSWORD` pour un compte pouvant accéder au stockage SMB.

Pour la détection par IA, les machines NVIDIA ont aussi besoin du CUDA Toolkit 12.x et de cuDNN 9.x ; le programme d’installation copie leurs DLL en place lorsqu’il les trouve. Voir [Détection IA](/Help/ai-detection#hardware).

Après l’installation, le nœud apparaît sous **Nœuds** avec la mention **en attente d’approbation**. Il n’enregistre rien et ne reçoit aucune caméra tant que vous ne cliquez pas sur **Approuver** ; n’approuvez donc qu’un nœud que vous avez installé vous-même. Affectez ensuite des caméras depuis la page de modification de chaque caméra.

## Proxys multimédias

Exécutez `LarisVMS-Proxy.msi` avec `SERVERURL`, `REGISTRATIONKEY` et `CLIENTPORT` (4443 par défaut), ainsi que les mêmes paramètres de certificat facultatifs qu’un nœud. Un nouveau proxy démarre désactivé : activez **Activé** pour lui sous **Paramètres → Proxys multimédias**, puis choisissez-le sur la page de chaque nœud (voir [Nœuds](/Help/nodes#proxies)).

## Mises à jour

**Mise à niveau :** exécutez le `.msi` plus récent. Il trouve l’installation existante et passe directement à **Prêt pour la mise à niveau** ; les paramètres sont mémorisés, donc rien n’a besoin d’être saisi de nouveau, sauf le mot de passe d’un compte de service de domaine. Le serveur web applique lui-même les modifications de base de données à son démarrage. Les mises à niveau ne touchent jamais aux enregistrements, aux fichiers de configuration, à l’enregistrement des nœuds ni aux clés de protection des données, et une installation en échec est annulée.

**Les nœuds et les proxys se mettent à jour eux-mêmes.** Une nouvelle installation web enregistre les versions de nœud et de proxy qu’elle contient comme **En attente** dans la section **Versions** de **Nœuds**. Seules la version de nœud la plus récente et la version de proxy la plus récente attendent l’approbation ; les plus anciennes sont **Remplacées**. **Historique des versions** liste chaque version. Approuvez-en une, et chaque nœud plus ancien la télécharge, la vérifie et l’installe à sa prochaine connexion. Le serveur signe chaque mise à jour avec sa clé de signature (voir **Paramètres → Chiffrement et signature**) ; les nœuds et les proxys conservent cette clé depuis leur enregistrement et n’installent rien qui ne soit signé avec elle, même si leur connexion au serveur était altérée. Désactivez cela avec **Mise à jour automatique** sous **Nœuds → Mises à jour et enregistrement**.

**Nouvelles versions :** une fois par jour (et au démarrage du service), le serveur consulte les versions GitHub de LarisVMS. Lorsqu’une version plus récente est disponible, les personnes pouvant modifier les paramètres voient **✨ La version X est disponible** dans la barre supérieure, avec un lien vers ses notes de version. Rien sur votre installation n’est envoyé. Désactivez cela avec **Nouvelles versions** sous **Nœuds → Mises à jour et enregistrement**.

**La désinstallation** supprime le programme, son service et les règles de pare-feu. Les enregistrements, la configuration et `%ProgramData%\LarisVMS` sont conservés.

## Installation depuis les sources

Les développeurs peuvent compiler et installer depuis le dépôt avec PowerShell à la place : `install-web.ps1` (serveur), et les scripts `install-node.ps1` / `install-proxy.ps1` des paquets produits par `build-node.ps1` / `build-proxy.ps1`. `build-installers.ps1` produit les trois MSI. Un MSI met à niveau sur place une machine installée par script, en conservant ses paramètres, son enregistrement et tout point d’accès de diffusion directe avec sa règle de pare-feu.

## Certificats

`Kestrel:Certificates:Default:Path` et `:Password` dans `appsettings.Production.json` pointent vers un fichier `.pfx`. Le serveur le vérifie toutes les 60 secondes et met en place un certificat renouvelé sans redémarrage.
