# Dépannage

Problèmes courants, et où chercher.

## Où se trouvent les journaux

- **Serveur** : **Journaux → Journaux système**. Définissez le niveau et la conservation sous **Paramètres → Journaux**. Debug et Trace ajoutent du détail mais produisent de gros fichiers.
- **Journal des événements de caméra** : les événements bruts envoyés par les caméras, utilisés uniquement pour lister les sujets d’événements d’une caméra pour les étiquettes d’événements. Ils sont conservés 30 jours par défaut (**Paramètres → Journaux**, 0 les conserve indéfiniment) ; les effacer ne touche ni aux événements, ni aux instantanés, ni aux enregistrements.
- **Entretien de la base de données** : chaque nuit vers 3 h, le serveur range les index et les statistiques de la base de données, et consigne une ligne indiquant ce qu’il a fait. Si sa connexion à la base de données n’y est pas autorisée, il consigne un avertissement à la place. Sur MariaDB, cela est ignoré ; MariaDB gère ses propres statistiques.
- **Nœuds** : `%ProgramData%\LarisVMS\logs\node-*.log` sur chaque nœud.
- **Détection IA** : `%ProgramData%\LarisVMS\logs\vision-*.log` sur chaque nœud.
- **Mise à jour automatique** : `%ProgramData%\LarisVMS\logs\updater-*.log` sur chaque nœud ou proxy.

## Une caméra n’enregistre pas

1. Est-elle **affectée à un nœud**, et ce nœud est-il **en ligne** ?
2. Le nœud a-t-il un **chemin de stockage** ? La liste des nœuds signale ceux qui n’en ont pas.
3. La caméra est-elle **activée**, et ses flux semblent-ils corrects ? **Sonder à nouveau** après toute modification sur la caméra elle-même.
4. En mode Mouvement, Planning ou Événement, vérifiez si des enregistrements sont écartés (voir [Enregistrement](/Help/recording#modes)).

## La vue en direct et la lecture restent sur « connexion »

Lorsqu’un nœud diffuse directement vers les navigateurs (voir [Diffusion directe](/Help/nodes#direct-streaming)), le navigateur se connecte au port client du nœud, 4200 par défaut. Si la console du navigateur affiche une connexion `wss://<nœud>:<port>/live/…` en échec, vérifiez que le pare-feu du nœud autorise ce port : le programme d’installation crée une règle entrante nommée **LarisVMS Node Client Endpoint**. Les programmes d’installation antérieurs à 0.215.1 pouvaient perdre cette règle lors d’une mise à niveau ou de la reprise d’un nœud ; exécutez de nouveau le programme d’installation de nœud actuel, ou ajoutez la règle à la main.

## La connexion ou la synchronisation Active Directory échoue

Utilisez **Tester la connexion** sous **Paramètres → Active Directory** ; l’erreur de la dernière synchronisation est affichée sur la même page.

- **Impossible de joindre un contrôleur de domaine :** vérifiez que le nom de domaine se résout depuis le serveur et que le port 636 (LDAPS) ou 389 (LDAP) est ouvert vers un contrôleur de domaine. Avec LDAPS, le contrôleur de domaine a besoin d’un certificat auquel ce serveur fait confiance.
- **L’identité propre du serveur a été rejetée :** la sécurité intégrée ne fonctionne que lorsque le service LarisVMS Web s’exécute sur un serveur joint au domaine, sous LocalSystem ou un compte de domaine. Sinon, utilisez un compte de service.
- **« N’appartient à aucun groupe ayant accès » :** l’utilisateur s’est connecté correctement mais n’appartient à aucun groupe lié. Les groupes sont appariés en tant que groupes de sécurité, y compris imbriqués.
- **Exclu des pages d’administration après avoir désactivé la connexion locale :** voir [Active Directory](/Help/users#active-directory) pour la commande de base de données qui la réactive.

## La vue en direct se met en pause dans une fenêtre en arrière-plan

Chrome limite les fenêtres qui ne sont pas visibles, de sorte qu’un mur en direct sur un second écran peut se figer puis rattraper son retard. La vue en direct se rétablit d’elle-même. Pour l’éviter, ajoutez ceci à la cible du raccourci de Chrome :

```
--disable-backgrounding-occluded-windows --disable-background-timer-throttling
```

## Toutes les tuiles se figent, puis se reconnectent d’un coup

Le nœud a calé, pas le navigateur ni le réseau. Cherchez dans le journal du nœud `Node process health: tick … ms late`. Un nœud en bonne santé consigne cela environ une fois par minute avec un retard de l’ordre de quelques millisecondes.

- Si `gcPauseMs` est proche du retard, la cause est le ramasse-miettes.
- Si `gcPauseMs` est petit mais que `pendingWorkItems` est de l’ordre de la dizaine, des threads sont bloqués. Capturez un dump pendant le blocage : `dotnet-dump collect -p <pid de LarisVMS.Node>`.

## La détection IA s’arrête mais l’enregistrement continue

Généralement, le pilote du GPU a été réinitialisé. Le journal d’événements système de Windows montre l’événement 153 de `nvlddmkm` ou l’événement 4101 de `Display` à ce moment. Le service de vision redémarre de lui-même après 60 secondes sans résultat. Des réinitialisations répétées pointent vers le GPU, son pilote, le refroidissement ou l’alimentation.

## Un nœud ou un proxy n’est pas revenu après une mise à jour

Démarrez son service (`sc start LarisVMSNode`, ou `LarisVMSProxy`), puis consultez `updater-*.log` pour savoir pourquoi le redémarrage a échoué. Une mise à jour téléchargée qui n’a jamais été appliquée est aussi notée dans le journal propre du nœud ou du proxy à son prochain démarrage, et est proposée de nouveau lors d’une connexion ultérieure.

## Un nœud affiche « horloge »

L’horloge du nœud diffère de plus d’une minute de celle du serveur. Corrigez sa synchronisation de l’heure. Un décalage d’horloge affecte les plannings et la chronologie.

## Un nœud échoue sans cesse à se connecter

Utilisez **Réinitialiser l’authentification** dans le menu **⋯** du nœud. Cela efface le jeton de connexion du nœud, et le nœud se resynchronise à sa prochaine pulsation.
