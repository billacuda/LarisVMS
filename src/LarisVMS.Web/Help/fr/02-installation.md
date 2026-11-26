# Installation et mises à niveau

Installer le serveur web, les nœuds d’enregistrement et les proxys multimédias, et les maintenir à jour.

Chaque composant a son propre programme d’installation : `LarisVMS-Web`, `LarisVMS-Node` et `LarisVMS-Proxy` (`.msi`). Double-cliquez sur l’un d’eux pour qu’il vous demande ses paramètres, ou passez-les en ligne de commande avec `msiexec /i <fichier>.msi NOM=valeur … /qn`. Tout ce qui n’est pas fourni est demandé, et une mise à niveau réutilise les paramètres de l’installation précédente.

Chaque partie fonctionne aussi sous Linux, à partir d’un paquet `.deb` ou `.rpm` (voir [Linux](#linux)), et les deux peuvent être mélangés : un serveur Windows peut avoir des nœuds et des proxys Linux, et inversement.

## Serveur web

1. Disposez d’un SQL Server joignable depuis le serveur (sécurité intégrée ou authentification SQL), ou d’un serveur MariaDB (voir [MariaDB](#mariadb)).
2. Exécutez `LarisVMS-Web.msi`. Paramètres :
   - `HTTPSPORT` : le port HTTPS (8444 par défaut). Le programme d’installation l’ouvre dans le pare-feu, pour TCP et pour UDP ([HTTP/3](/Help/security#http3)).
   - `CERTPATH`, `CERTPASSWORD` : le certificat du serveur (`.pfx`). Laissez vide pour utiliser un certificat auto-signé en attendant d’en avoir un. Le programme d’installation vérifie que le fichier s’ouvre avec le mot de passe avant de continuer ; un partage de fichiers doit aussi être lisible par le compte du service.
   - `SERVICEACCOUNT`, `SERVICEPASSWORD` : exécute le service sous un compte précis, par exemple un compte disposant des droits de sécurité intégrée SQL. Vide : s’exécute sous LocalSystem. Inutile pour MariaDB.
3. Allez sur `https://<hôte>:8444/`. L’assistant d’installation guide la configuration de la base de données, du premier compte administrateur, du stockage et de l’enregistrement des nœuds. Un navigateur sur le serveur lui-même ou sur le réseau local entre directement. De n’importe où ailleurs, il demande d’abord le jeton d’installation situé dans `%ProgramData%\LarisVMS\setup-token.txt` sur le serveur (`/var/lib/larisvms/setup-token.txt` sous Linux ; lisible uniquement par les administrateurs), afin que personne sur Internet ne puisse l’installer en premier. Pour demander le jeton depuis tous les autres ordinateurs, réseau local compris, définissez `"Setup": { "AlwaysRequireToken": true }` dans `appsettings.Production.json` avant d’ouvrir l’assistant. Le fichier est supprimé une fois le compte administrateur créé.

Le mot de passe d’une connexion SQL, ou un mot de passe MariaDB, est stocké chiffré sur le serveur (DPAPI sous Windows, ou une clé liée à l’identifiant de la machine sous Linux ; cette machine uniquement), donc `setup-generated.json` ne peut pas être copié sur une autre machine avec le mot de passe toujours fonctionnel. Exécutez plutôt l’assistant de nouveau là-bas.

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

Pour la détection par IA, les machines NVIDIA ont aussi besoin de CUDA 12 et de cuDNN 9 ; sous Windows, le programme d’installation copie leurs DLL en place lorsqu’il les trouve. Voir [Détection IA](/Help/ai-detection#hardware).

Après l’installation, le nœud apparaît sous **Nœuds** avec la mention **en attente d’approbation**. Il n’enregistre rien et ne reçoit aucune caméra tant que vous ne cliquez pas sur **Approuver** ; n’approuvez donc qu’un nœud que vous avez installé vous-même. Affectez ensuite des caméras depuis la page de modification de chaque caméra.

## Proxys multimédias

Exécutez `LarisVMS-Proxy.msi` avec `SERVERURL`, `REGISTRATIONKEY` et `CLIENTPORT` (4443 par défaut), ainsi que les mêmes paramètres de certificat facultatifs qu’un nœud. Un nouveau proxy démarre désactivé : activez **Activé** pour lui sous **Paramètres → Proxys multimédias**, puis choisissez-le sur la page de chaque nœud (voir [Nœuds](/Help/nodes#proxies)).

## Linux

Le serveur web, les nœuds et les proxys s’installent à partir de paquets `.deb` (Ubuntu 22.04, 24.04, 26.04, Debian 12, 13) ou `.rpm` (Rocky Linux 9, 10 et autres dérivés de RHEL 9 ou 10), pour x86_64. Chacun s’exécute comme service systemd (`larisvms-web`, `larisvms-node`, `larisvms-proxy`) sous son propre compte `larisvms`. Le programme va dans `/opt/larisvms`, les paramètres dans `/etc/larisvms`, les données (clés, modèles, jeton d’installation) dans `/var/lib/larisvms` et les journaux dans `/var/log/larisvms`. Aucun runtime .NET n’est nécessaire. L’installation ouvre les ports du service dans firewalld ou ufw lorsque l’un d’eux est actif.

- **Serveur web :** `sudo apt install ./larisvms-web_<version>-1_amd64.deb` (ou `sudo dnf install ./larisvms-web-<version>-1.x86_64.rpm`). Il démarre aussitôt ; ouvrez `https://<host>:8444/` pour l’assistant d’installation. Son port et son certificat se définissent dans `/etc/larisvms/web.env` (`Kestrel__HttpsPort`, `Kestrel__Certificates__Default__Path`, `Kestrel__Certificates__Default__Password` ; le compte larisvms doit pouvoir lire le fichier), puis `sudo systemctl restart larisvms-web`. `appsettings.Production.json` et `setup-generated.json` se trouvent aussi dans `/etc/larisvms`. Sous Linux, SQL Server nécessite une connexion SQL ; MariaDB y est le choix le plus simple.
- **Nœuds :** installez `larisvms-node`, puis associez-le avec `sudo larisvms-node setup --server-url https://<server>:8444 --registration-key <key>`. L’étape Nœud de l’assistant d’installation affiche cette commande avec la clé et l’empreinte du certificat du serveur déjà remplies. Il enregistre dans `/var/lib/larisvms/recordings` sauf si vous ajoutez `--storage-root <folder>` ; un partage réseau doit être monté au préalable (par exemple dans `/etc/fstab`) et accessible en écriture au compte larisvms. Autres options : `--archive-root`, `--live-port`, `--live-allow-from`, `--client-port`, `--client-pfx-path`, `--client-endpoint-host` et `--ffmpeg-path`. `sudo larisvms-node setup --help` les liste toutes, et exécuter de nouveau `setup` les modifie.
- **ffmpeg** vient de la distribution et s’installe avec le nœud. Sous Rocky Linux, il vient de RPM Fusion : activez EPEL, CRB et RPM Fusion avant d’installer un nœud.
- **Proxys :** installez `larisvms-proxy`, puis `sudo larisvms-proxy setup --server-url https://<server>:8444 --registration-key <key> --client-port 4443`.

Vérifiez un service avec `systemctl status larisvms-node` et `journalctl -u larisvms-node`, et affichez sa version avec `larisvms-node --version`.

**Docker :** chaque version est aussi publiée sous forme d’images dans le registre de conteneurs de GitHub : `ghcr.io/billacuda/larisvms-web`, `larisvms-node` (avec les étiquettes `-cuda` et `-tensorrt` pour les GPU NVIDIA) et `larisvms-proxy`. Le serveur web a besoin d’une base de données que vous hébergez vous-même (MariaDB 10.11 ou ultérieur, ou SQL Server avec une connexion SQL). Installez-le de l’une de ces façons :

- **Docker Compose :** téléchargez [compose.yml](https://github.com/billacuda/LarisVMS/blob/main/docker/compose.yml) et exécutez `docker compose up -d` dans son dossier. Le serveur web démarre ; ouvrez `https://<host>:8444/` pour l’assistant d’installation. Décommentez ensuite le nœud (et le proxy, si vous en voulez un) dans le fichier, renseignez l’adresse du serveur, la clé d’enregistrement et le dossier des enregistrements, puis exécutez de nouveau `docker compose up -d`.
- **Pile Portainer :** sous **Stacks → Add stack → Web editor**, collez le même fichier et choisissez **Deploy the stack**. Après l’assistant, décommentez le nœud dans l’onglet **Editor** de la pile et choisissez **Update the stack**. Utilisez un environnement Docker autonome, pas un Swarm.
- **Docker seul :** démarrez le serveur web, suivez l’assistant, puis démarrez le nœud :

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

Approuvez ensuite le nœud sous **Nœuds**. Pour l’adresse du serveur, utilisez son adresse ou son nom sur le réseau local, pas `localhost`, même sur la même machine. Si la machine a un pare-feu, autorisez TCP 8444 (serveur web), 8554 (nœud), 8555 (un nœud qui diffuse directement vers les navigateurs) et 4443 (proxy), et UDP 8444 si vous utilisez HTTP/3. Les conteneurs s’exécutent avec l’utilisateur et le groupe 8444, propriétaires de leurs dossiers sur l’hôte.

L’étape Nœud de l’assistant d’installation et la page **Proxys multimédias** affichent une pile Compose pour Portainer ou `docker compose`, avec l’adresse, la clé d’enregistrement et le certificat de ce serveur déjà remplis. Les paramètres sont les mêmes variables d’environnement que celles des services Linux (`LARISVMS_SERVER_URL`, `LARISVMS_REGISTRATION_KEY`, `LARISVMS_STORAGE_ROOT`, etc.). Gardez `/var/lib/larisvms` sur un volume : il contient les clés, l’enregistrement et l’identifiant de machine qui garde lisibles les mots de passe enregistrés quand le conteneur est recréé. Utilisez le réseau de l’hôte, pour que la découverte des caméras fonctionne. Pour mettre à jour, exécutez `docker compose pull` puis `docker compose up -d`, choisissez **Update the stack** avec **Re-pull image and redeploy** activé dans Portainer, ou, avec Docker seul, récupérez l’image, supprimez le conteneur et exécutez de nouveau sa commande.

**Passer une installation Linux à Docker :** copiez le dossier de données (surtout `keys`) et l’identifiant de machine avant le premier démarrage du conteneur, et sauvegardez d’abord la base de données. Le mot de passe de la base de données et les enregistrements des nœuds sont chiffrés avec l’identifiant de machine, et les mots de passe et clés de nœud de la base de données avec `keys` ; sans eux, des pages comme **Nœuds** affichent une erreur. Une installation Windows ne peut pas être déplacée ainsi. Pour le serveur web :

```bash
sudo systemctl disable --now larisvms-web
sudo mkdir -p /srv/larisvms/web/data /srv/larisvms/web/config
sudo tar -C /var/lib/larisvms --exclude=./recordings --exclude=./node.config --exclude=./proxy.config -cf - . \
  | sudo tar -C /srv/larisvms/web/data -xf -
sudo cp -a /etc/larisvms/. /srv/larisvms/web/config/
sudo install -m 600 /etc/machine-id /srv/larisvms/web/data/machine-id
```

Reportez les paramètres de `/etc/larisvms/web.env` dans `environment` de la pile, puis démarrez le conteneur. Il se connecte à la même base de données, sans l’assistant. N’exécutez jamais l’ancien service et le conteneur en même temps. Pour un nœud, laissez les enregistrements où ils sont :

```bash
sudo systemctl disable --now larisvms-node
sudo mkdir -p /srv/larisvms/node/data
sudo tar -C /var/lib/larisvms --exclude=./recordings --exclude=./keys --exclude=./update --exclude=./proxy.config -cf - . \
  | sudo tar -C /srv/larisvms/node/data -xf -
sudo install -m 600 /etc/machine-id /srv/larisvms/node/data/machine-id

# The container's account needs to own the recordings (repeat for an archive folder):
sudo chown -R 8444:8444 /var/lib/larisvms/recordings
```

Reportez les paramètres de `/etc/larisvms/node.env` dans `environment` de la pile, et montez chaque dossier d’enregistrements et d’archive au même chemin dans le conteneur (par exemple `/var/lib/larisvms/recordings:/var/lib/larisvms/recordings`, avec `LARISVMS_STORAGE_ROOT` défini sur ce chemin). Le nœud revient tel qu’il était, avec ses caméras et ses enregistrements, sans clé d’enregistrement ni approbation. Un proxy se déplace de la même façon, sans les enregistrements. Supprimer ensuite les anciens paquets ferme les ports dans le pare-feu ; rouvrez-les donc pour les conteneurs.

Si vous avez déjà suivi l’assistant dans le conteneur avec l’ancienne base de données, seules les clés manquent. Copiez-les à côté de celles du conteneur, et gardez les deux :

```bash
docker stop larisvms-web
sudo docker cp /var/lib/larisvms/keys/. larisvms-web:/var/lib/larisvms/keys/
docker start larisvms-web
```

Là où l’Aide cite un dossier Windows, voici celui de Linux : `%ProgramData%\LarisVMS` → `/var/lib/larisvms` (son dossier `logs` → `/var/log/larisvms`), et `appsettings.Production.json` et `setup-generated.json` du dossier d’installation → `/etc/larisvms`. `Restart-Service LarisVMSWeb` correspond à `sudo systemctl restart larisvms-web`.

## Mises à jour

**Mise à niveau :** exécutez le `.msi` plus récent. Il trouve l’installation existante et passe directement à **Prêt pour la mise à niveau** ; les paramètres sont mémorisés, donc rien n’a besoin d’être saisi de nouveau, sauf le mot de passe d’un compte de service de domaine. Le serveur web applique lui-même les modifications de base de données à son démarrage. Les mises à niveau ne touchent jamais aux enregistrements, aux fichiers de configuration, à l’enregistrement des nœuds ni aux clés de protection des données, et une installation en échec est annulée.

**Les nœuds et les proxys se mettent à jour eux-mêmes.** Une nouvelle installation web enregistre les versions de nœud et de proxy qu’elle contient comme **En attente** dans la section **Versions** de **Nœuds**. Seules la version de nœud la plus récente et la version de proxy la plus récente attendent l’approbation ; les plus anciennes sont **Remplacées**. **Historique des versions** liste chaque version. Approuvez-en une, et chaque nœud plus ancien la télécharge, la vérifie et l’installe à sa prochaine connexion. Le serveur signe chaque mise à jour avec sa clé de signature (voir **Paramètres → Chiffrement et signature**) ; les nœuds et les proxys conservent cette clé depuis leur enregistrement et n’installent rien qui ne soit signé avec elle, même si leur connexion au serveur était altérée. Désactivez cela avec **Mise à jour automatique** sous **Nœuds → Mises à jour et enregistrement**.

**Nouvelles versions :** une fois par jour (et au démarrage du service), le serveur consulte les versions GitHub de LarisVMS. Lorsqu’une version plus récente est disponible, les personnes pouvant modifier les paramètres voient **✨ La version X est disponible** dans la barre supérieure, avec un lien vers ses notes de version. Rien sur votre installation n’est envoyé. Désactivez cela avec **Nouvelles versions** sous **Nœuds → Mises à jour et enregistrement**.

**Sous Linux,** mettez à niveau le serveur web en installant le paquet plus récent de la même façon ; `/etc/larisvms` et `/var/lib/larisvms` sont conservés. Les nœuds et proxys Linux se mettent à jour d’eux-mêmes depuis le serveur comme ceux de Windows, une fois la version approuvée : un petit utilitaire exécuté en tant que root installe la mise à jour que le service a téléchargée et vérifiée. Un serveur Linux propose aux nœuds Windows leur version Windows, et un serveur Windows propose aux nœuds Linux la leur.

**La désinstallation** supprime le programme, son service et les règles de pare-feu. Les enregistrements, la configuration et `%ProgramData%\LarisVMS` sont conservés. Sous Linux, `apt remove` ou `dnf remove` fait de même et conserve `/var/lib/larisvms`, `/var/log/larisvms` et `/etc/larisvms`.

## Installation depuis les sources

Les développeurs peuvent compiler et installer depuis le dépôt avec PowerShell à la place : `install-web.ps1` (serveur), et les scripts `install-node.ps1` / `install-proxy.ps1` des paquets produits par `build-node.ps1` / `build-proxy.ps1`. `build-installers.ps1` produit les trois MSI, ainsi que les paquets Linux `.deb` et `.rpm` (avec nFPM). Un MSI met à niveau sur place une machine installée par script, en conservant ses paramètres, son enregistrement et tout point d’accès de diffusion directe avec sa règle de pare-feu.

## Certificats

`Kestrel:Certificates:Default:Path` et `:Password` dans `appsettings.Production.json` pointent vers un fichier `.pfx` (sous Linux, aussi définissable dans `/etc/larisvms/web.env` avec `Kestrel__Certificates__Default__Path` et `__Password`). Le serveur le vérifie toutes les 60 secondes et met en place un certificat renouvelé sans redémarrage.

Dans Docker, montez le dossier du certificat (`/srv/larisvms/certs:/certs:ro`) et utilisez le chemin à l’intérieur du conteneur, comme `/certs/vms.pfx`. L’utilisateur 8444 doit pouvoir le lire, et un `$` dans le mot de passe s’écrit `$$` dans un fichier Compose. Un certificat qui ne peut pas être chargé est remplacé par un certificat auto-signé, et `docker logs` indique pourquoi.
