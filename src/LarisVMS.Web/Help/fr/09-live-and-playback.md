# Vue en direct et lecture

Vues, tuiles en direct, chronologie de lecture, exports, signets, verrouillages de preuves, instantanés, activité et carte.

## Vues

Une **vue** est une disposition de caméras enregistrée. Créez des vues sous **Vues** en faisant glisser et en redimensionnant des tuiles. **Direct** et **Lecture** affichent toutes deux une vue. Sur téléphone, la disposition se réduit à une ou deux colonnes. Les vues peuvent s’exécuter en plein écran en mode kiosque, ou alterner entre plusieurs vues selon un minuteur. Une vue partagée peut être utilisée par toutes les personnes qui voient les vues, mais seuls son propriétaire ou un administrateur peuvent la modifier ou la supprimer.

## Tuiles en direct

- Double-cliquez sur une tuile pour le plein écran. En plein écran, faites défiler ou pincez pour zoomer et faites glisser pour déplacer.
- Les commandes d’une tuile apparaissent quand vous la survolez, ou quand vous y accédez avec Tab. Voir [Accessibilité](/Help/accessibility).
- Chaque tuile avec audio a ses propres commandes de coupure du son et de volume. Les tuiles démarrent toujours sans le son.
- **Diffusion adaptative** (sous **Paramètres → Vue en direct**) montre aux petites tuiles le flux secondaire et passe au flux principal quand la tuile est grande ou en plein écran. Chaque tuile a aussi un choix manuel Auto/HD/SD.
- Des badges indiquent le mouvement et les objets détectés. **Paramètres → Événements** en définit les couleurs et le coin.
- Les interrupteurs IA de la barre d’outils dessinent un cadre autour de chaque objet détecté : **En mouvement**, **Immobile**, ou les deux. **Confiance** ajoute le score de chaque cadre, et **Vitesse** et **Couleur** (activées par défaut) ajoutent sa vitesse, une fois qu’un contrôle de vitesse l’a chronométré, et sa couleur. **Tracé** (avec En mouvement activé) dessine la ligne suivie par chaque objet en mouvement depuis le centre de son cadre, dans la couleur du cadre. La ligne reste tant que l’objet continue de bouger. Une fois qu’il s’arrête ou part, la ligne reste pendant la durée **Persistance du tracé** (**Paramètres → Vue en direct**, 5 secondes par défaut), puis se dissout depuis son début pendant la durée **Dissolution du tracé** (même page, 3 secondes par défaut).
- Les caméras PTZ ont un pavé directionnel et un zoom.
- 🔴 en bas à gauche signifie que les enregistrements de la caméra sont conservés en ce moment, pour une raison quelconque. Les caméras en mode Continu l’affichent en permanence. Il peut falloir jusqu’à 20 secondes pour qu’il change après un mouvement ou un événement.
- Ⓜ️ à côté signifie qu’un [enregistrement manuel](/Help/recording#manual-recording) est actif. Ⓜ️ seul signifie que l’enregistrement est actif, mais que la caméra est hors ligne et que rien n’est sauvegardé.
- Le bouton ⏺ démarre un enregistrement manuel, et ⏹ l’arrête. Dans une vue avec d’autres caméras, il demande si vous voulez dire cette caméra ou toutes. Toute personne autorisée peut arrêter un enregistrement, quel que soit celui qui l’a démarré.

## Parler via une caméra

Les caméras avec haut-parleur affichent un bouton 🎙️ sur leur tuile en direct. Maintenez-le (ou donnez-lui le focus et maintenez Espace) pour parler, et relâchez pour arrêter. **▾** à côté choisit le microphone : un casque, une webcam ou toute autre entrée que le navigateur peut utiliser.

- Votre navigateur demande l’accès au microphone la première fois. Il ne l’autorise que si le site est ouvert en **HTTPS** ; voir [Certificats](/Help/security#certificates).
- Une seule personne à la fois peut parler via une caméra.
- Cela nécessite l’autorisation **Parler** sur la caméra (**Paramètres → Accès aux caméras**). La caméra doit prendre en charge l’audio bidirectionnel ONVIF (une sortie audio dans son profil ONVIF). Les caméras Dahua, Amcrest, Lorex et EmpireTech dont le micrologiciel ne le propose pas se rabattent sur l’API audio propre à la caméra.
- Tout ce qui est dit est enregistré. Le microphone propre à la caméra fait partie de l’enregistrement vidéo comme d’habitude, et chaque appui sur le bouton est sauvegardé avec les enregistrements. Les appuis apparaissent sur la chronologie de lecture en bleu-vert et sont lus en synchronisation avec la vidéo, avec le volume de la tuile. Les exports les incluent, mélangés à l’audio propre à la caméra. Ils sont conservés aussi longtemps que les enregistrements de la caméra.
- Chaque session est écrite dans le journal d’audit.

## Chronologie

La lecture a deux chronologies : une pour la caméra sélectionnée et une combinée pour toutes les caméras. Les deux sont colorées selon le mouvement, les détections, les étiquettes d’événements et l’audio bidirectionnel.

- Faites défiler pour zoomer (des semaines aux secondes), faites glisser pour déplacer, et cliquez pour amener chaque tuile à cet instant.
- Cliquez sur l’heure au-dessus de la chronologie pour saisir une heure exacte.
- Les flèches avancent d’une seconde en pause ; Maj+flèche avance de dix.
- Les vitesses jusqu’à 8× se lisent de façon fluide. Les vitesses supérieures parcourent les enregistrements comme un diaporama.
- Survolez la chronologie pour un aperçu en miniature.
- Les passages avec mouvement ou événement sont rayés, afin de ne pas dépendre de la couleur seule.
- Les boutons − et + à côté de l’heure zooment aussi, et la chronologie fonctionne au clavier. Voir [Accessibilité](/Help/accessibility).

Votre position et votre zoom sont mémorisés d’une visite à l’autre.

## Exports

**Exporter…** dans Lecture crée un fichier vidéo pour chaque caméra sélectionnée sur une période. Les exports s’exécutent en arrière-plan et apparaissent sur la page **Exports**, où ils peuvent être téléchargés, relancés ou supprimés. Un export couvre au plus 64 caméras, 31 jours et 1 000 heures-caméras au total (4 caméras pendant une semaine font 672) ; pour davantage, faites-en plusieurs. Les exportations terminées sont conservées 7 jours, puis supprimées avec leurs fichiers.

- **✏ Masquer** à côté d’une caméra masque en noir des zones dans l’export de cette caméra uniquement, comme la fenêtre d’un voisin ou un passant. Tracez des rectangles (faites glisser) ou des polygones (cliquez sur chaque coin ; cliquez sur le premier point, double-cliquez ou appuyez sur Entrée pour terminer ; le clic droit retire le dernier point) sur une image prise à l’heure de début de l’export. Les enregistrements eux-mêmes sont inchangés.
- Un export masqué est réencodé, il prend donc plus de temps qu’un export simple. Il est marqué **Masqué** sur la page Exports, et le journal d’audit enregistre que des zones ont été masquées.
- Les [masques de confidentialité](/Help/motion) d’une caméra font déjà partie de ses enregistrements, donc chaque export les inclut.
- Tout audio bidirectionnel prononcé via la caméra pendant la période est mélangé à l’audio de l’export.

### Vérifier un export

Chaque segment enregistré reçoit une empreinte (SHA-256) au moment de son écriture, et les segments de chaque caméra sont reliés en chaîne : chaque maillon couvre l’empreinte de son segment et le maillon précédent, donc modifier, supprimer ou réordonner des enregistrements rompt la chaîne. Une fois par heure, le serveur signe l’état d’avancement de la chaîne de chaque caméra.

Chaque export terminé reçoit aussi un **🔏 Manifeste** signé sur la page Exports. Il consigne l’empreinte du fichier exporté, les enregistrements sources et leur place dans la chaîne, et si chaque fichier source correspondait encore à son empreinte lors de l’export.

Pour vérifier un export, ouvrez **Exports → Vérifier un export** et choisissez le manifeste et la vidéo. La vérification s’exécute dans votre navigateur et rien n’est envoyé. Elle confirme que :

- le manifeste a été signé par ce serveur et n’a pas été modifié ;
- la vidéo est exactement le fichier exporté ;
- les enregistrements sources n’avaient pas changé depuis leur capture, et s’enchaînent jusqu’à un point de contrôle signé.

Conservez le manifeste avec la vidéo lorsque vous transmettez un export. N’importe qui peut vérifier la signature sans LarisVMS : enregistrez le `publicKeyPem` du manifeste dans `key.pem`, son texte `signedPayload` dans `payload.txt`, et son `signatureDer` décodé en base64 dans `sig.der`, puis exécutez `openssl dgst -sha256 -verify key.pem -signature sig.der payload.txt`. Les enregistrements faits avant cette fonctionnalité n’ont pas d’empreintes, donc seul l’export lui-même peut être vérifié.

La signature est toujours active. La clé de signature du serveur, avec un téléchargement de sa clé publique, se trouve sous **Paramètres → Chiffrement et signature**.

## Signets

Marquez un instant pendant la Lecture avec une note. **Signets** les liste tous, avec un lien vers l’instant exact. Les signets sont partagés avec toutes les personnes qui peuvent consulter la lecture, et expirent lorsque leurs enregistrements arrivent à échéance.

## Verrouillages de preuves

Un verrouillage de preuves empêche la conservation de supprimer des enregistrements. Dans Lecture, cliquez sur **🔒 Verrouiller…**, choisissez les caméras ainsi que le début et la fin, choisissez la durée de conservation (1, 3, 7, 14 ou 30 jours, jusqu’à une date, ou indéfiniment) et indiquez un motif. Les passages verrouillés apparaissent comme une bande violette le long du haut de la chronologie. Un verrouillage couvre jusqu’à 7 jours d’enregistrements.

- **Les enregistrements verrouillés ne sont jamais supprimés**, ni par la conservation, ni par les quotas, ni par l’expiration de l’archive, et pas même quand le disque est plein. Si un enregistreur se remplit d’enregistrements verrouillés, son entrée dans Nœuds affiche **plein d’enregistrements verrouillés**, et l’enregistrement s’arrête une fois le disque plein. Terminez ou retirez des verrouillages, exportez les enregistrements ou ajoutez du stockage.
- **Verrouillages longs** : tout ce qui dépasse 30 jours affiche un avertissement. Quand le stockage est limité, il vaut mieux exporter la vidéo (**Exporter plutôt cette période**) et la conserver ailleurs.
- **Retirer un verrouillage** : **Verrouillages de preuves** liste chaque verrouillage avec sa taille et son expiration. La personne qui a posé un verrouillage, ou un super administrateur, peut le retirer en indiquant un motif. Les enregistrements restent protégés pendant **24 heures** de plus au cas où le retrait serait une erreur, et **Annuler le retrait** rétablit le verrouillage d’ici là.
- **Journalisé** : la pose, le retrait, le rétablissement et l’expiration d’un verrouillage sont tous écrits dans le journal d’audit.

Une caméra dont des enregistrements sont encore sous verrouillage ne peut pas être supprimée. Poser des verrouillages nécessite l’autorisation **Verrouiller les enregistrements**, que les rôles pouvant exporter ont par défaut. Les [notifications](/Help/alerts#notifications) peuvent vous prévenir lorsqu’un verrouillage est sur le point de se terminer ou a été retiré.

## Instantanés

**Instantanés** liste chaque détection et étiquette d’événement sous forme de miniature, avec des filtres par caméra, groupe, vue, type d’objet et heure. Le simple mouvement n’apparaît que sur la chronologie. ▶ ouvre l’instant dans Lecture.

## Activité

**Activité** montre quand les choses se produisent sur une caméra, ou sur toutes les caméras ensemble, sous forme de carte thermique par heure de la journée. Choisissez la caméra (ou **Toutes les caméras**) et une période : 24 heures, 7, 30 ou 90 jours, ou un an.

- **Semaine type** (par défaut) : les lignes sont les jours de la semaine et les colonnes les heures, totalisés sur la période ; une cellule sombre est donc un moment régulièrement chargé, comme le samedi de 18 h à 19 h. Sur plus d’une semaine, survoler une cellule donne aussi la moyenne par jour (par exemple, « 8,8 par samedi en moyenne »).
- **Par jour** (30 jours et plus) : chaque date va de gauche à droite avec les heures de haut en bas, et une bande de totaux quotidiens en dessous, pour repérer des jours particuliers.
- Le jour le plus chargé de la période et l’heure la plus chargée sont nommés au-dessus du graphique.
- Pour les dernières 24 heures, il y a une ligne pour chaque type d’événement.
- **Types d’événements** restreint à ce qui vous intéresse : un type entier (Animal) ou seulement certains de ses libellés (Animal → chat). Le simple mouvement est désactivé par défaut pour ne pas noyer les objets. Votre choix est mémorisé et reste quand vous changez de caméra.
- Survolez une cellule pour son nombre exact, ou activez **Afficher sous forme de tableau** pour les chiffres.
- Les heures sont dans le fuseau horaire de la caméra (défini sur la caméra), ou celui du serveur si elle n’en a pas. Toutes les caméras utilise le fuseau horaire du serveur.
- L’historique d’activité est conservé 400 jours (paramètre `Activity.HistoryDays`), même après la suppression des enregistrements eux-mêmes, donc les périodes plus longues montrent toute la durée. L’historique se constitue à partir de l’installation de cette fonction : les événements déjà supprimés avec leurs enregistrements ne peuvent pas être récupérés.
- La vue figure dans l’adresse de la page, donc vous pouvez la mettre en favori ou la partager. Le widget **Activité de la caméra** du tableau de bord affiche les 7 derniers jours d’une caméra ou de toutes.

### Scène

**Scène** montre *où* dans l’image d’une caméra les choses bougent : une couche de chaleur sur une image récente pleine taille issue des enregistrements de la caméra. Le rouge est là où il se passe le plus de choses, puis orange, jaune, vert, et bleu pour très peu. Les endroits sans rien restent clairs.

- **Objets en mouvement** compte combien d’objets sont passés par chaque endroit. Restreignez à **Personnes**, **Véhicules**, **Animaux** ou **Autres**.
- **Mouvement** montre où la détection de mouvement côté serveur de la caméra a vu du mouvement, pour les caméras qui l’utilisent en mode **Grille**.
- Choisissez une période, et éventuellement une **Heure de la journée** (par exemple de 22 h à 6 h pour les nuits).
- La chaleur est additionnée une fois par heure (le mouvement toutes les 10 minutes) et conservée aussi longtemps que le reste de l’historique d’activité. Les trajectoires qui la sous-tendent sont conservées avec leurs événements, donc elles disparaissent avec les enregistrements.
- Choisissez une caméra, ou une **vue** (listée sous **Vues** dans la liste des caméras) pour voir côte à côte une carte thermique de chaque caméra qu’elle contient. Chaque caméra a sa propre échelle, donc une caméra calme montre quand même où est son activité. Elle ne peut pas être affichée pour toutes les caméras à la fois.
- **Arrière-plan** : si vous pouvez configurer la caméra, vous pouvez remplacer son image par la vôtre (une prise de vue claire de jour, par exemple) avec **Utiliser ma propre image…**. Elle doit provenir de la même caméra avec le même cadrage, en pleine largeur ou de même forme, pour que la chaleur s’aligne. **Utiliser l’image de la caméra** revient à l’image automatique.
- **Réinitialiser la carte thermique** : après qu’une caméra a été déplacée ou réorientée, son ancienne chaleur ne s’aligne plus avec l’image. Si vous pouvez configurer la caméra, **Réinitialiser la carte thermique** l’efface et ne compte que ce qui se passe à partir de là ; la fiche indique quand le comptage a commencé.

## Carte

**Carte** (sous Direct) montre où se trouvent vos caméras et ce qu’elles regardent. Chaque caméra est une icône avec un cône pour son champ de vision : la direction vers laquelle elle pointe, sa largeur de vue et sa portée. La couleur indique son état : vert pour en enregistrement, rouge pour hors ligne, gris pour désactivée. Cliquez sur une caméra pour sa dernière image, avec des liens vers Direct et Lecture. Les états s’actualisent toutes les 30 secondes. La carte se rouvre là où vous l’avez laissée, séparément pour chaque personne et chaque carte.

Il y a deux types de cartes, et vous pouvez en avoir autant de chaque que vous le souhaitez :

- **Carte routière** : une carte normale de la zone (OpenStreetMap par défaut). La portée est en mètres.
- **Plan** : une image que vous téléversez (PNG, JPEG, WebP ou GIF, jusqu’à 10 Mo), comme un plan de bâtiment ou un dessin de site. La portée est en pixels de cette image.

Les personnes qui peuvent modifier les caméras peuvent configurer les cartes :

1. **Nouvelle carte → Carte routière** ou **Plan**. Une nouvelle carte routière s’ouvre là où l’actuelle s’affiche, ou sur le monde entier ; déplacez et zoomez sur votre site. Quand vous appuyez sur **Terminer la modification**, la carte est enregistrée pour s’ouvrir là où elle s’affiche pour tout le monde ; **Gérer → Ouvrir cette carte ici pour tout le monde** fait de même sans modification. Cela fonctionne aussi pour les plans.
2. **Modifier la carte**, puis faites glisser des caméras depuis **Caméras absentes de cette carte** sur la carte. Ou cliquez sur l’une d’elles pour la placer au milieu de la carte.
3. Cliquez sur une caméra pour la sélectionner. Faites glisser l’icône pour la déplacer, et faites glisser la poignée blanche à la pointe de son cône pour l’orienter (direction et portée). Le panneau latéral règle le champ de vision, ou la direction et la portée exactes, et **Retirer de cette carte** la retire. Les modifications s’enregistrent au fur et à mesure. Les boutons fléchés du panneau la déplacent sans la faire glisser.

Une caméra peut figurer sur plusieurs cartes, comme une carte routière et le plan de son bâtiment. Les personnes ne voient que les caméras qu’elles sont autorisées à consulter.

Les images de la carte routière sont chargées par votre navigateur depuis le serveur de tuiles ; une carte routière nécessite donc un accès à Internet depuis le navigateur. Pour un site hors ligne, ou un autre style de carte, définissez votre propre serveur de tuiles https sous **Gérer → Tuiles de carte routière**, ainsi que son attribution (les conditions de la plupart des serveurs de tuiles exigent qu’elle soit affichée sur la carte). La modifier nécessite aussi l’autorisation de modification des paramètres, car elle s’applique à tout le serveur.
