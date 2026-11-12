# Nœuds

Nœuds d’enregistrement : état, chemins de diffusion, proxys multimédias, basculement, maintenance et mises à jour.

**Nœuds** dans la barre latérale liste chaque nœud avec son état, son adresse, sa version, son nombre de caméras et son utilisation du disque. Cliquez sur un nœud pour modifier ses paramètres. Le menu **⋯** propose ces actions :

- **Redémarrer le service** : redémarre le service Windows du nœud, pas la machine. L’enregistrement s’interrompt quelques secondes.
- **Réinitialiser l’authentification** : efface le jeton de connexion du nœud, pour un nœud qui échoue sans cesse à s’authentifier après une pulsation manquée. Sans effet sur un nœud en bonne santé.
- **Maintenance** : déplace les caméras du nœud vers son nœud de secours jusqu’à la fin de la maintenance.
- **Supprimer** : supprime le nœud. Ses caméras deviennent non affectées. Les enregistrements ne sont pas supprimés.

Un nœud est **en ligne** s’il s’est connecté au cours des 2 dernières minutes. Un avertissement ⚠️ **horloge** signifie que l’horloge du nœud diffère de plus d’une minute de celle du serveur ; vérifiez sa synchronisation de l’heure.

## Diffusion directe

Par défaut, toute la vidéo en direct et de lecture est relayée par le serveur web (**Proxy**). Avec **Direct**, les navigateurs se connectent directement au point d’accès HTTPS de chaque nœud, ce qui décharge le serveur. Définissez le mode sous **Paramètres → Vue en direct** et remplacez-le par nœud.

Le mode Direct nécessite un **hôte de point d’accès client** que les navigateurs peuvent joindre, et un certificat (un chemin `.pfx` sur la page du nœud). **Autoriser les certificats auto-signés** est réservé aux tests, car les spectateurs reçoivent un avertissement du navigateur. Une caméra dont le nœud n’a aucun point d’accès en bonne santé repasse automatiquement par le proxy.

## Connexions chiffrées depuis le serveur

Le serveur et les proxys multimédias atteignent les routes vidéo et de contrôle de chaque nœud en HTTP simple sur son port du direct (8554), ce qui convient sur un réseau de confiance. Pour chiffrer ce trafic, activez **Chiffrer les connexions depuis le serveur** sur la page du nœud, puis redémarrez le nœud une fois (**Redémarrer**). Le nœud démarre son point d’accès HTTPS (avec son propre certificat auto-signé s’il n’a pas de `.pfx`) et signale le certificat, que le serveur épingle alors : le serveur et les proxys n’acceptent que ce certificat de la part du nœud, et le jeton multimédia ne fait plus partie de l’adresse. Une fois chiffré, le port non chiffré du nœud ne répond plus qu’à `/health`.

La colonne **Chiffré** de la page Nœuds affiche 🔒 pour chaque nœud chiffré, et ⏳ pour un nœud activé mais qui a encore besoin d’un redémarrage. L’état sous l’interrupteur affiche le certificat utilisé, ou indique que le serveur l’attend encore (d’ici là, le serveur continue d’utiliser HTTP simple, donc rien ne cesse de fonctionner). C’est désactivé par défaut. Les certificats auto-signés utilisés pour cela ne sont jamais proposés aux navigateurs ; la diffusion directe nécessite toujours un vrai certificat.

## Proxys

Un **proxy multimédia** est un relais distinct (`LarisVMS.Proxy`) entre les navigateurs et les nœuds, pour les sites distants ou les liaisons à faible bande passante. Enregistrez les proxys sous **Paramètres → Proxys multimédias**, puis choisissez un proxy principal et un proxy de secours sur la page de chaque nœud. Les navigateurs utilisent le premier en bonne santé.

## Basculement

Donnez à un nœud un **nœud d’enregistrement de secours**, et lorsqu’il tombe en panne, ses caméras sont enregistrées par le nœud de secours jusqu’à son retour. « En panne » est décidé par un vote entre le partenaire du nœud, le serveur et son proxy. **Maintenance** déplace volontairement ses caméras et les y maintient.

La page du nœud avertit lorsque le nœud de secours a un matériel d’IA différent, ou n’enregistre que. Dans ces cas, la détection peut être plus lente, ou suspendue, pendant un basculement.

## Mises à jour

Les nœuds et les proxys se mettent à jour eux-mêmes à partir des versions approuvées (voir [Installation](/Help/installation#updates)). **Nœuds → Mises à jour et enregistrement → Mise à jour automatique** l’active ou la désactive pour chaque nœud.

La section **Versions** de la page Nœuds affiche la version vers laquelle chaque type de nœud évolue et toute version en attente d’approbation, avec **Approuver** et **Rejeter**. **Historique des versions** liste chaque version, y compris les approuvées, les remplacées et les rejetées.

## Certificat du serveur

**Nœuds → Mises à jour et enregistrement → Vérification du certificat du serveur** décide comment les nœuds et les proxys vérifient le certificat HTTPS de ce serveur :

- **Faire confiance à la première utilisation** (par défaut) : un certificat d’une autorité de confiance fonctionne toujours, y compris renouvelé ou remplacé. Un certificat auto-signé est mémorisé la première fois qu’un nœud se connecte et continue de fonctionner tant qu’il reste le même, même après sa date d’expiration. S’il change, les nœuds et les proxys le refusent jusqu’à être réenregistrés.
- **Certificat de confiance uniquement** : uniquement un certificat d’une autorité de confiance qui correspond au nom du serveur. Installez-en un avant de choisir cette option.
- **Ne pas vérifier** : n’importe quel certificat. Non sécurisé : quiconque sur le chemin réseau pourrait se faire passer pour le serveur.

Avec la confiance à la première utilisation, un nœud fait confiance au certificat qu’il voit lors de sa première connexion. Pour qu’il vérifie dès la toute première connexion, transmettez au programme d’installation l’empreinte affichée sous **Vérification du certificat du serveur** : `-ServerCertThumbprint` pour `install-node.ps1` et `install-proxy.ps1`, ou `SERVERCERTTHUMBPRINT=` pour le MSI. Les commandes d’installation des pages Installation et Proxys multimédias l’incluent déjà. Derrière un proxy inverse, utilisez plutôt l’empreinte du certificat que présente le proxy inverse.

Le certificat auto-signé que le serveur crée pour lui-même dure 10 ans et n’est pas remplacé de lui-même. **Régénérer le certificat auto-signé** le remplace ; chaque nœud et chaque proxy doit alors être réenregistré, et la page Nœuds liste ceux qui ne se sont pas connectés depuis. Pour réenregistrer un nœud, exécutez de nouveau le programme d’installation du nœud, ou exécutez `LarisVMS.Node.exe --reset-server-pin` dessus et redémarrez son service (les proxys ont la même option).

## Codes d’enregistrement à usage unique

Les nouveaux nœuds et proxys s’enregistrent avec la **clé d’enregistrement** partagée, ou avec un code à usage unique. Un code à usage unique enregistre un seul nœud ou proxy, puis cesse de fonctionner. Inutilisé, il expire après 1 heure, 24 heures ou 7 jours. Pour en créer un, allez dans **Nœuds → Mises à jour et enregistrement**, choisissez sa durée de validité et sélectionnez **Créer un code**. Le code n’est affiché qu’une fois. Utilisez-le partout où le programme d’installation demande la clé d’enregistrement. **Révoquer** annule un code qui n’a pas encore été utilisé.

La clé partagée fonctionne pour toutes les installations tant qu’elle n’est pas modifiée. Une fois que vous utilisez des codes à usage unique, décochez **Accepter la clé d’enregistrement partagée** afin qu’une copie de la clé dans un ancien script ou e-mail ne puisse rien enregistrer. Les nœuds et proxys déjà enregistrés ne sont pas affectés. Dans les deux cas, un nouveau nœud attend toujours l’approbation avant de recevoir des caméras.
