# Vue d’ensemble

Comment LarisVMS s’articule : le serveur web, les nœuds d’enregistrement, les proxys multimédias et les caméras.

## Les composants

| Composant | Rôle |
|---|---|
| **Serveur web** | L’application web LarisVMS. Stocke la configuration dans SQL Server ou MariaDB, sert l’interface et l’API, et relaie la vidéo en direct et la lecture sauf si la diffusion directe est activée. S’exécute comme service Windows *LarisVMS Web*. |
| **Nœud d’enregistrement** | Un service Windows sur chaque machine d’enregistrement. Récupère le RTSP de ses caméras, écrit des segments dans son chemin de stockage, applique la conservation, exécute la détection de mouvement et par IA, et signale son état. Un serveur peut avoir de nombreux nœuds. |
| **Service de vision** | Processus enfant facultatif d’un nœud qui exécute la détection d’objets par IA sur le GPU (ou le CPU) du nœud. Un plantage ici n’arrête jamais l’enregistrement. |
| **Proxy multimédia** | Relais facultatif entre les navigateurs et les nœuds, pour les sites distants ou les liaisons à faible bande passante. |
| **Caméras** | Caméras ONVIF. Chacune est affectée à un nœud d’enregistrement. |

## Le parcours de la vidéo

1. Un nœud ouvre une session RTSP vers chacune de ses caméras et enregistre avec `ffmpeg -c copy`, donc aucun réencodage n’a lieu.
2. Les enregistrements sont écrits sous forme de courts fichiers de segments (voir [Enregistrement](/Help/recording)) dans le chemin de stockage du nœud.
3. La vue en direct et la lecture sont servies par le nœud et relayées par ce serveur, par un proxy multimédia, ou directement vers le navigateur (voir [Nœuds](/Help/nodes#direct-streaming)).

## Confidentialité

LarisVMS est auto-hébergé : votre vidéo et vos paramètres restent sur vos propres machines, et rien sur votre installation n’est collecté ni partagé. Il n’y a ni télémétrie ni appel vers l’extérieur. La seule requête sortante de routine est une vérification quotidienne des versions GitHub de LarisVMS pour détecter une nouvelle version (voir [Mises à jour](/Help/installation#updates)) ; elle peut être désactivée. L’autre téléchargement intégré est un modèle d’IA YOLOX issu des versions GitHub de ce projet, la première fois que la détection en utilise un, sauf si vous placez vous-même les fichiers sur le serveur. Les cartes routières de la page [Carte](/Help/live-and-playback#map) chargent leurs images dans votre navigateur depuis OpenStreetMap, ou depuis un serveur de tuiles de votre choix ; les plans, non. Tout le reste va uniquement vers vos caméras, vos propres serveurs et les services que vous configurez, comme l’e-mail et les canaux d’alerte.

## Héritage des paramètres

La plupart des paramètres existent à trois niveaux. Le plus spécifique l’emporte :

1. **Caméra** : sur la page de modification de la caméra.
2. **Nœud** : sur la page de modification du nœud, sous **Nœuds**.
3. **Global** : sur la page **Paramètres** correspondante.

Un champ vide (ou une option « hériter — … ») utilise le niveau supérieur, et affiche la valeur qu’il hérite actuellement.

## Tableau de bord

Le tableau de bord est composé de widgets. Chaque utilisateur choisit les siens, et la disposition est enregistrée dans son compte, donc elle le suit sur d’autres navigateurs.

- **Personnaliser** (en haut à droite ; masqué lorsque la fenêtre est trop étroite pour la grille complète) déverrouille la disposition. Faites glisser un widget par sa barre de titre, redimensionnez-le par le coin, ou retirez-le avec ×. **Ajouter un widget** liste tout ce que vous pouvez ajouter, et **Réinitialiser la disposition** revient à la disposition par défaut. Cliquez sur **Terminé** lorsque vous avez fini.
- Pendant la personnalisation, chaque widget a des boutons pour le déplacer et le redimensionner sans le faire glisser.
- **Suspendre les mises à jour** empêche les widgets de changer pendant que vous les lisez.
- La plupart des widgets sont des classements top 10 de caméras ou de nœuds, comme le stockage utilisé, la fréquence d’images, la bande passante, les enregistrements les plus anciens, les caméras hors ligne, le temps écoulé depuis le dernier enregistrement et les détections. Certains ont une fenêtre de temps (1 h, 24 h, 7 j, 30 j), et certains peuvent alterner entre les plus élevés et les plus bas en premier.
- Cliquez sur le titre d’un widget pour ouvrir son rapport complet : chaque caméra ou nœud, 100 par page, avec une zone de filtre, un filtre par nœud, des colonnes triables et un téléchargement CSV.
- **Activité de la caméra** affiche les 7 derniers jours d’une caméra sous forme de carte thermique par heure de la journée ; choisissez la caméra dans sa barre de titre, et cliquez sur le titre pour la page complète [Activité](/Help/live-and-playback#activity).
- **Dernières détections** affiche les instantanés les plus récents. Son bouton ⚙ choisit les types de détection à inclure ; le simple mouvement (rien d’identifié) est désactivé sauf si vous le cochez.
- **Temps écoulé depuis le dernier enregistrement** signale une caméra réglée sur l’enregistrement continu qui n’a rien enregistré depuis plus de 5 minutes.
- Les widgets de nœuds fondés sur les caméras (nombre de caméras, caméras hors ligne, fréquence d’images, temps de stockage restant) omettent les nœuds sans caméras.
- Vous ne voyez que les caméras auxquelles vous avez accès. **Alertes récentes** n’est proposé qu’aux utilisateurs pouvant gérer les règles d’alerte.

Les widgets s’actualisent chaque minute.

## Sur téléphone

Tout le site fonctionne dans un navigateur de téléphone. La barre latérale s’ouvre avec le bouton ☰ en haut à gauche.

Pour l’installer comme application, ouvrez le site dans Chrome sur Android et choisissez **Installer l’application** dans le menu ⋮. Sur iOS, ouvrez-le dans Safari et choisissez **Partager → Sur l’écran d’accueil**. L’application s’ouvre en plein écran, sans la barre d’adresse du navigateur.

L’installation nécessite un certificat auquel le téléphone fait confiance. Les navigateurs n’installent pas un site qui utilise le certificat auto-signé avec lequel démarre une installation neuve ; voir [Certificats](/Help/security#certificates).

## Par où commencer

- Nouvelle installation : [Installation](/Help/installation)
- Ajouter des caméras : [Caméras](/Help/cameras)
- Choisir ce qu’il faut conserver : [Enregistrement](/Help/recording) et [Stockage et conservation](/Help/storage)
- Connecter vos propres outils : [API](/Help/api)
- Utiliser une autre langue : [Langues](/Help/languages)
- Clavier et lecteurs d’écran : [Accessibilité](/Help/accessibility)
- Un problème : [Dépannage](/Help/troubleshooting)
