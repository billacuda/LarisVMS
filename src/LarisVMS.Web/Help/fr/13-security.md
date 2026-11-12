# Sécurité

Listes d’adresses IP autorisées, port distinct pour le direct/la lecture, politique de scripts stricte, certificats, noms d’hôte, mises à jour, secrets chiffrés et protection supplémentaire pour les entreprises.

## Listes autorisées

**Paramètres → Sécurité** propose deux listes d’adresses IP autorisées. Saisissez une IP ou un bloc CIDR par ligne (par exemple `10.0.0.5` ou `10.0.0.0/24`) ; les lignes commençant par `#` sont des commentaires. Une liste vide autorise tout le monde.

- **Gestion / API** : les pages d’administration et l’API REST. Une liste qui bloquerait l’adresse depuis laquelle vous enregistrez est refusée, donc vous ne pouvez pas vous enfermer dehors.
- **Vue en direct / lecture** : la vidéo en direct, la lecture, les miniatures, les téléchargements d’exports et les instantanés des caméras.

## Port distinct

**Paramètres → Vue en direct → Port personnalisé** déplace le trafic du direct et de la lecture vers son propre port HTTPS, avec le même certificat. Cela permet de le filtrer séparément par pare-feu, ou de n’exposer que lui en dehors du réseau local. Cela prend effet après `Restart-Service LarisVMSWeb`. Ensuite, la vidéo ne répond que sur le nouveau port et tout le reste uniquement sur le port principal. Videz le champ pour revenir à un seul port.

## HTTP/3

Le serveur utilise HTTP/2, et ajoute HTTP/3 sous Windows 11, Windows Server 2022 ou ultérieur. Les navigateurs démarrent en HTTP/2 et passent à HTTP/3 d’eux-mêmes lorsqu’ils peuvent l’atteindre. HTTP/3 utilise UDP sur le port HTTPS (et le port personnalisé, s’il est défini). Le programme d’installation ouvre TCP et UDP ; sur une installation plus ancienne, ajoutez une règle UDP pour le port ou exécutez de nouveau le programme d’installation actuel. D’ici là, tout continue de fonctionner en HTTP/2. Pour désactiver HTTP/3, décochez **Paramètres → Sécurité → HTTP/3**, puis `Restart-Service LarisVMSWeb`.

## Politique de scripts stricte

Avec **Paramètres → Sécurité → Politique de scripts stricte** activé, les navigateurs n’exécutent que les scripts que ce serveur envoie, de sorte qu’un script injecté dans une page ne puisse pas s’exécuter. Elle est activée par défaut, et la mise à niveau l’active. Si quelque chose cesse de fonctionner, désactivez-la : les navigateurs signalent alors tout ce que la politique stricte aurait bloqué, et les derniers rapports apparaissent sous l’interrupteur. Une fois qu’aucun rapport n’apparaît après avoir utilisé les pages dont vous dépendez, réactivez-la. Une modification prend effet au prochain chargement de page.

## Certificats

Le certificat du serveur est un fichier `.pfx` défini dans `appsettings.Production.json`. Un fichier renouvelé est pris en compte en une minute sans redémarrage. Les nœuds qui utilisent la diffusion directe ont leur propre paramètre de certificat sur la page du nœud.

## Certificats des caméras

Les caméras jointes en HTTPS ont des certificats auto-signés qui ne peuvent être vérifiés par rapport à rien ; par défaut, tout certificat est donc accepté. Activez **Paramètres → Sécurité → Épingler les certificats des caméras** pour que le serveur mémorise le certificat de chaque adresse de caméra la première fois qu’il s’y connecte, et en refuse un autre à partir de là, sur le serveur comme sur les nœuds. Quelque chose sur le réseau qui se fait passer pour une caméra ne peut alors plus récupérer son mot de passe ni envoyer de faux événements.

Les certificats mémorisés sont listés sous le paramètre. Si le certificat d’une caméra change vraiment (mise à jour du micrologiciel, réinitialisation ou nouveau certificat), sélectionnez **Oublier** à côté de son adresse ; le prochain certificat qu’elle présente est mémorisé. D’ici là, chaque connexion refusée, sur le serveur ou un nœud, est consignée dans le journal d’audit et les super administrateurs sont prévenus (au plus une fois par heure et par adresse de caméra), car quelque chose sur le réseau peut se faire passer pour la caméra. Une adresse de caméra est mémorisée quand le serveur la joint pour la première fois, généralement lors de l’ajout de la caméra ; un nœud accepte une adresse que le serveur n’a pas encore jointe.

## Noms d’hôte

Par défaut, le serveur répond à n’importe quel nom d’hôte utilisé par un navigateur, donc il fonctionne par adresse IP, nom court et nom complet. Pour ne répondre qu’aux noms que vous utilisez, définissez `AllowedHosts` dans `appsettings.Production.json` avec ces noms, séparés par des points-virgules (par exemple `"AllowedHosts": "vms.example.com;vms;localhost"`), puis `Restart-Service LarisVMSWeb`. Incluez chaque nom que les nœuds, les proxys et les personnes utilisent pour le joindre : tout autre nom provoque une erreur.

Derrière un proxy inverse (IIS ARR, nginx, etc.), indiquez son adresse dans `appsettings.Production.json` sous la forme `"ReverseProxy": { "KnownProxies": [ "10.0.0.2" ] }`, puis redémarrez. Le serveur prend alors l’adresse et le schéma réels du client dans les en-têtes `X-Forwarded-For` et `X-Forwarded-Proto` de ce proxy, de sorte que les listes autorisées et le journal d’audit voient la personne, pas le proxy. Ces en-têtes sont ignorés s’ils proviennent de quoi que ce soit qui ne figure pas dans la liste, et entièrement tant que la liste est vide.

## Mises à jour

Ces vérifications se dressent entre un téléchargement et une installation en cours d’exécution :

- **Les téléchargements de versions** sont accompagnés de `SHA256SUMS.txt`. Vérifiez un programme d’installation avant de l’exécuter avec `Get-FileHash <fichier> -Algorithm SHA256` et comparez-le avec la ligne de ce fichier, ou exécutez `sha256sum -c SHA256SUMS.txt`. Lorsque les programmes d’installation sont signés, Windows affiche aussi l’éditeur.
- **Les mises à jour des nœuds et des proxys** sont signées par la clé de signature de ce serveur. Un nœud ou un proxy conserve cette clé depuis son enregistrement et n’installe rien qui ne soit signé avec elle, même si sa connexion au serveur était altérée. Il refuse aussi une version qui n’est pas plus récente que celle qu’il exécute. Voir [Installation](/Help/installation#updates).
- **La signature de l’éditeur** (facultative) : un nœud ou un proxy compilé avec la clé de l’éditeur (`-PublisherKeyPath`) exige aussi la signature de l’éditeur sur chaque mise à jour ; les autres versions ignorent cette vérification. Cette clé est gardée hors ligne et jamais sur le serveur, donc même un serveur piraté ne peut pas pousser son propre code vers les nœuds.
- **La connexion** au serveur est vérifiée selon le réglage sous **Nœuds → Mises à jour et enregistrement → Vérification du certificat du serveur** (voir [Nœuds](/Help/nodes#server-certificate)).

## Secrets chiffrés

Les identifiants des caméras, les identifiants SMB, les clés des nœuds, les secrets d’e-mail, le secret client Entra et le mot de passe du compte de service Active Directory sont chiffrés dans la base de données, à l’aide des clés de protection des données de `%ProgramData%\LarisVMS\keys`. Seuls SYSTEM, les administrateurs et les comptes des services LarisVMS peuvent ouvrir `%ProgramData%\LarisVMS` : tout autre accès trouvé là, comme un dossier créé par un utilisateur local avant l’installation de LarisVMS, est supprimé au démarrage d’un service. Sauvegardez ce dossier, et ne le supprimez jamais lors d’un déploiement.

## Protection supplémentaire pour les entreprises

Prêt à l’emploi, LarisVMS est configuré pour qu’une installation domestique fonctionne simplement. Tout ce qui suit est facultatif et désactivé par défaut ; activez ce qui convient à une entreprise, ou à un serveur accessible depuis Internet :

- **Exiger la connexion en deux étapes** : Paramètres → Sécurité (voir [Connexion en deux étapes](/Help/users#two-factor-sign-in)).
- **Confirmer le mot de passe pour les pages d’administration** : Paramètres → Sécurité (voir [Sessions](/Help/users#sessions)).
- **Mots de passe temporaires définis par un administrateur** : Paramètres → Sécurité (voir [Mots de passe](/Help/users#passwords)).
- **Sessions plus courtes et délai d’inactivité** : chaque rôle sous Paramètres → Rôles (voir [Rôles](/Help/users#roles)).
- **Épingler les certificats des caméras** : Paramètres → Sécurité (voir [Certificats des caméras](#camera-certificates)).
- **Listes autorisées** : Paramètres → Sécurité (voir [Listes autorisées](#allow-lists)).
- **Le jeton d’installation depuis tous les autres ordinateurs** : définissez `"Setup": { "AlwaysRequireToken": true }` dans `appsettings.Production.json` avant d’exécuter l’assistant d’installation (voir [Installation](/Help/installation#web-server)).
- **Épingler le certificat du serveur lors de l’installation d’un nœud ou d’un proxy** : `-ServerCertThumbprint` (voir [Certificat du serveur](/Help/nodes#server-certificate)).
- **Codes d’enregistrement à usage unique, avec la clé partagée désactivée** : Nœuds → Mises à jour et enregistrement (voir [Codes d’enregistrement à usage unique](/Help/nodes#one-time-registration-codes)).
- **Connexions chiffrées du serveur vers les nœuds** : sur la page de chaque nœud (voir [Connexions chiffrées depuis le serveur](/Help/nodes#encrypted-connections-from-the-server)).
- **Transférer le journal d’audit vers syslog** : Paramètres → Journaux (voir [Journal d’audit](/Help/users#audit-log)).
- **Mises à jour signées par l’éditeur**, pour les versions que vous compilez vous-même (voir [Mises à jour](#updates)).
- **Un compte de service moins privilégié.** Les services LarisVMS s’exécutent sous LocalSystem. Vous pouvez les exécuter sous un autre compte (`services.msc`, ou `sc.exe config`), mais il vous revient alors de donner à ce compte ce dont le service a besoin : le dossier d’installation, chaque dossier ou partage de stockage et d’archive, et les fichiers de certificat. `%ProgramData%\LarisVMS` est géré pour vous : chaque service LarisVMS, au démarrage, le limite à SYSTEM, aux administrateurs et aux comptes sous lesquels s’exécutent les services LarisVMS, et supprime tout autre accès. Vérifiez le journal du service après le changement.
