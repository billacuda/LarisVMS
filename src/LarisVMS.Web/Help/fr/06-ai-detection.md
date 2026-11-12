# Détection IA

Détection d’objets sur le nœud : modèles, matériel, seuils, instantanés et moteur externe.

Chaque nœud peut exécuter une détection d’objets en temps réel sur ses caméras. Les objets détectés reçoivent des cadres englobants en direct, une marque colorée sur la chronologie, un badge sur la tuile en direct et un instantané recadré. Une détection compte aussi comme du mouvement pour l’enregistrement en mode Mouvement. La détection s’exécute dans un processus séparé, donc un problème de GPU ou de pilote n’arrête jamais l’enregistrement.

Activez-la par caméra avec **Surveiller les objets sur cette caméra**. Seuls **5 à 7 ips par caméra** sont nécessaires pour une détection précise ; plafonnez donc la [fréquence d’images](#thresholds) à ce niveau pour limiter la charge du GPU. Les valeurs par défaut globales se trouvent sous **Paramètres → Détection IA**. Les nœuds et les caméras peuvent en remplacer la plupart.

## Matériel

Un seul paquet de nœud couvre toutes les machines. Au démarrage, le service de vision choisit un moteur adapté au matériel qu’il trouve :

| Matériel | Moteur | Requis sur le nœud |
|---|---|---|
| GPU NVIDIA | CUDA (repasse à DirectML) | CUDA Toolkit 12.x et cuDNN 9.x |
| GPU AMD / Intel | DirectML | Un pilote de GPU à jour |
| Pas de GPU | CPU | Rien |

La page de modification du nœud affiche les accélérateurs détectés, et son paramètre **Accélérateur** peut en imposer un. Les modèles ne sont jamais fournis avec le logiciel. Un nœud récupère les modèles YOLOX intégrés depuis le serveur, et les autres modèles vont dans `C:\ProgramData\LarisVMS\models` sur le nœud.

## Moteur lent

Si un nœud NVIDIA ne peut pas utiliser CUDA, la détection IA repasse à DirectML, qui s’exécute plusieurs fois plus lentement. Moins d’images sont analysées, le GPU chauffe, et des objets rapides comme des voitures qui passent peuvent ne pas être détectés. La page **Nœuds** affiche alors **⚠️ IA sur DirectML** à côté du nœud, avec la raison, et les super administrateurs reçoivent une notification.

La cause habituelle est un cuDNN manquant. Installez le CUDA Toolkit 12.x et cuDNN 9.x pour CUDA 12 sur le nœud, depuis le téléchargement cuDNN de NVIDIA ou avec `pip install nvidia-cudnn-cu12`. Le nœud les trouve dans les 10 minutes, les copie à côté de son moteur d’IA et redémarre la détection sur CUDA.

## Familles de modèles

Le modèle s’exécute une fois par nœud et dessert toutes les caméras de ce nœud ; les paramètres du modèle sont donc par nœud, pas par caméra.

- **Auto / YOLOX** : la valeur par défaut. Rapide, ne nécessite pas de bandes noires, et fonctionne bien sur les GPU peu puissants et non NVIDIA. **Taille YOLOX** va de Nano (le plus rapide) à X (le plus précis). Utilisez Nano ou Tiny sur les machines peu puissantes et L ou X sur les GPU puissants.
- **D-FINE** : expérimental. Les résultats peuvent être imprévisibles, et les objets à cheval sur deux tuiles en mode Tranche peuvent être manqués ou dupliqués. **Poids** : Obj2Coco connaît 80 types d’objets (petit ou moyen), et Obj365 en connaît 365.
- **Personnalisé** : n’importe quel modèle `.onnx` déposé dans le dossier des modèles du nœud. Voir [Modèles personnalisés](#custom-models).

Changer de modèle redémarre la détection de chaque caméra.

## TensorRT

**D-FINE TensorRT** n’a d’effet que si le nœud a TensorRT activé (`Vision:EnableTensorRt`) et le SDK TensorRT 10.x installé. **FP32** apporte une accélération sûre. **FP16** est le plus rapide ; si le moteur d’une caméra déborde en FP16, il se reconstruit automatiquement en FP32. Le premier démarrage à chaque résolution de caméra prend plusieurs minutes pour construire le moteur.

## Modèles personnalisés

Déposez un fichier `.onnx` dans `C:\ProgramData\LarisVMS\models` sur le nœud. Si les métadonnées du modèle décrivent son décodeur, sa taille d’entrée et ses libellés, il fonctionne tel quel. Sinon, ajoutez un fichier `.json` du même nom qui fournit ce qui manque. **Actualiser** liste chaque modèle trouvé par les nœuds et signale ceux dont les métadonnées sont incomplètes. Un modèle avec un avertissement peut être sélectionné mais ne s’exécutera pas tant que l’avertissement n’est pas corrigé.

## Ajustement du format

Comment l’image d’une caméra s’adapte à l’entrée carrée du modèle :

- **Bandes noires** (par défaut) : conserve les proportions et complète avec des bandes noires.
- **Étirer** : écrase l’image pour remplir le carré, ce qui déforme les formes.
- **Tranche** : découpe l’image en carrés se chevauchant à pleine résolution. C’est idéal pour les objets petits ou lointains sur des caméras grand angle, mais coûte environ 2× le travail sur une caméra 16:9. Elle nécessite le prétraitement par GPU, et l’active automatiquement.

Le **prétraitement des images par GPU** déplace la conversion des couleurs du CPU vers le GPU, ce qui aide sur les nœuds avec de nombreuses caméras.

## Seuils

- **Confiance** : le score minimal pour qu’une détection compte. Plus bas détecte plus d’objets mais ajoute des faux positifs. Augmentez-le sur une caméra qui détecte sans cesse mal quelque chose.
- **IoU** : de combien deux cadres doivent se chevaucher pour être fusionnés en un seul objet.
- **Flux de détection** : Secondaire est plus léger ; Principal voit plus de détails.
- **Orientation** : certaines caméras montées dans des couloirs signalent un flux paysage mais envoient une vidéo portrait. Réglez **Portrait** sur ces caméras pour que la détection et les instantanés ne soient pas écrasés.
- **Fréquence d’images de détection max.** : images par seconde et par caméra qui atteignent le modèle. **5 à 7 ips suffisent pour une détection et un suivi précis.** Des fréquences plus élevées ajoutent de la charge au GPU sans améliorer les résultats. 0 : aucune limite.

## Instantanés

Chaque objet suivi reçoit un instantané, recadré à partir de sa meilleure image.

- **Délai d’inactivité** : combien de temps un objet peut rester invisible avant la clôture de son instantané.
- **Signaler les objets immobiles** : signale aussi les objets présents mais immobiles, comme une voiture garée.
- **Précision du mouvement des instantanés** : clôt un instantané peu après le départ de son objet, afin qu’un objet ultérieur du même type ne soit pas fusionné avec lui. **Délai de départ** est la durée d’attente. Si elle est trop courte, un objet qui marque une pause est scindé en deux instantanés.
- **Marge de recadrage de l’instantané** : marge supplémentaire autour de l’objet, en pourcentage de son cadre.

Plusieurs objets du même type dans un instantané s’affichent sous forme de nombre (×2). Chacun est aussi conservé séparément avec ses propres couleurs et sa propre vitesse.

## Couleurs des objets

La couleur de chaque objet suivi est lue sur son image la plus nette : une couleur pour les véhicules, les animaux et les autres objets, et des couleurs de chemise et de pantalon pour les personnes. Les instantanés affichent un badge pour chacune, peint de cette couleur, et les sections **Couleur**, **Chemise** et **Pantalon** du filtre Instantanés restreignent les résultats aux couleurs que vous cochez. Les alertes de détection peuvent aussi filtrer par couleur.

Les couleurs proviennent de l’image, pas du modèle, donc elles fonctionnent avec n’importe quel détecteur. Les couleurs juste à l’extérieur du cadre sont aussi échantillonnées puis écartées, afin que la pelouse autour d’un chien ou la route autour d’une voiture ne l’emporte pas. Ce sont malgré tout des estimations au mieux : les scènes chargées, les objets qui se chevauchent et l’éclairage peuvent donner une mauvaise couleur. L’éclairage compte le plus : l’ombre, les phares et la balance des blancs décalent les couleurs, et une voiture gris clair peut sembler argentée ou blanche. Tant qu’une caméra est en mode nuit noir et blanc (IR), aucune couleur n’est enregistrée. Désactivez-les avec **Couleurs des objets** sous **Paramètres → Détection IA**.

## Contrôles de vitesse

Un contrôle de vitesse chronomètre les objets entre deux lignes tracées en travers d’une route. Sur la page **Contrôles de vitesse** de la caméra (depuis **Caméras → Modifier**), cliquez sur **Nouveau contrôle de vitesse**, cliquez sur les deux extrémités de la ligne **A**, puis sur les deux extrémités de la ligne **B**, et saisissez la distance réelle entre les lignes, mesurée le long de la route. Chaque objet suivi qui franchit les deux lignes reçoit une vitesse, affichée sur son cadre en direct et sur son instantané. **Sens** chronomètre dans les deux sens par défaut ; choisissez **De A vers B uniquement** ou **De B vers A uniquement** pour chronométrer une voie d’une route à double sens. Les **Unités de vitesse** (km/h ou mph) se trouvent sous **Paramètres → Détection IA**.

Un objet qu’un contrôle chronomètre est enregistré comme en mouvement même s’il semble par ailleurs immobile, comme un véhicule qui roule droit vers la caméra. Le chronométrage utilise le milieu du bas de chaque cadre, là où l’objet touche le sol ; tracez donc les lignes sur la surface de la route. Pour une bonne mesure :

- Tracez les lignes en travers du sens de circulation, sur un sol plat, et aussi éloignées que la vue le permet tant que les objets restent bien visibles.
- Mesurez soigneusement la distance : comptez vos pas, ou utilisez l’outil de mesure d’une carte.
- Vérifiez une fois avec un véhicule qui passe à une vitesse connue, et ajustez la distance si la mesure est erronée.

Les mesures sont des estimations. La fréquence d’images de détection de la caméra limite la précision avec laquelle un franchissement est chronométré ; les contrôles courts et les véhicules rapides sont donc les moins précis. L’enregistrement d’un contrôle redémarre la détection IA de la caméra.

Un contrôle peut avoir une **limite de vitesse** (facultative). Les vitesses supérieures sont affichées en rouge et comptées séparément dans la carte thermique des vitesses.

Les vitesses mesurées apparaissent à plusieurs endroits :

- **Vue en direct** : sur le cadre de chaque objet tant qu’il est en vue.
- **Instantanés** : un badge ⏱ sur la fiche, et un filtre **Vitesse** (« à partir de ») dans la liste des filtres.
- **Lecture** : un repère sur la chronologie à chaque mesure (rouge au-dessus de la limite ; survolez pour les détails), et un badge sur la vidéo tant que l’objet est à l’écran.
- **Tableau de bord** : le widget **Journal des vitesses** affiche les dernières mesures. Cliquez sur son titre pour le journal complet, avec tri, filtrage et téléchargement CSV.
- **Activité** : passez à **Vitesses** pour une carte thermique de la vitesse moyenne, de la vitesse maximale, du nombre au-dessus de la limite ou du nombre mesuré, par jour et par heure. L’historique est conservé aussi longtemps que l’historique d’activité, bien après la disparition des enregistrements.
- **Alertes** : une règle « Vitesse supérieure à une limite ».

## Mouvement

Les instantanés, les alertes et les cadres **En mouvement** en direct ne comptent que les objets qui vont quelque part. Le cadre d’un détecteur tremble un peu d’une image à l’autre, même autour d’une voiture garée ; quelques pixels de changement ne suffisent donc pas. **Détection de mouvement** sous **Paramètres → Détection IA**, ou par caméra dans **Caméras → Modifier**, détermine comment cela est jugé.

**Standard** (la valeur par défaut) examine la forme de la trajectoire récente de chaque objet :

- Tout ce qui se déplace régulièrement dans une direction compte en quelques images (une demi-seconde environ à 7 ips), qu’il s’agisse d’une voiture qui traverse la vue en moins d’une seconde ou d’un camion qui sort d’une allée. Les pas de chaque objet sont comparés au tremblement de son propre cadre, donc un grand objet n’a pas à parcourir plus de distance qu’un petit.
- Quelque chose qui avance plus lentement que le tremblement de son cadre, une fraction de la vitesse de la marche, compte une fois que sa trajectoire a suivi une ligne régulière pendant quelques secondes. Le tremblement va et vient, donc il ne construit jamais de ligne.
- La distance qu’un objet doit parcourir croît avec sa taille, mais plus lentement que la taille elle-même. Un grand camion n’a pas à franchir d’abord la moitié de sa propre longueur.
- Un objet qui entre par le bord de l’image, ou qui sort de derrière un poteau ou un arbre, est jugé d’après le côté de son cadre qui est visible.
- Un objet qui roule droit vers la caméra ou s’en éloigne compte à mesure qu’il grandit ou rétrécit.
- Le cadre d’une voiture garée qui change quand quelqu’un passe devant ne compte pas.

La **Sensibilité au mouvement** (Standard uniquement) est **Normale** par défaut. **Élevée** détecte le mouvement plus tôt et **Faible** ignore davantage le tremblement. Baissez-la sur une caméra où une voiture garée ou un buisson qui se balance est signalé comme en mouvement, et augmentez-la là où des objets lents sont manqués.

Un cadre qui change simplement de forme, comme celui d’une plante en pot quand le détecteur inclut les feuilles un instant et pas le suivant, ou celui d’une voiture garée quand quelque chose passe devant, ne compte pas. Le mouvement doit se construire sur plusieurs images, pas se produire d’un seul coup.

**Hérité** est la règle précédente : un objet compte dès que son centre s’est déplacé de la moitié de la taille de son cadre en 2 secondes. Utilisez-la si Standard se comporte mal sur une caméra.

Un objet qui était en mouvement et disparaît derrière un arbre, un poteau ou une voiture garée reste le même objet lorsqu’il ressort plus loin sur la même ligne en environ 30 images analysées (environ 5 secondes à 6 ips) ; il est donc compté une fois et garde une seule trajectoire. Il doit aussi être du même type d’objet et d’une taille similaire, et sa couleur doit correspondre lorsque les deux peuvent être déterminées. Quelque chose qui ressort en allant dans une autre direction, ou après plus longtemps, est un nouvel objet.

## Objets en mouvement

La trajectoire de chaque objet en mouvement dans l’image est conservée avec son événement, pour la carte thermique **Scène** de la page Activité.

**Objets en mouvement** sous **Paramètres → Détection IA** choisit quels types d’objets peuvent compter comme en mouvement. Par défaut, ce sont les **Personnes**, les **Véhicules** et les **Animaux**. Tout le reste est quand même détecté, et s’affiche comme un cadre immobile avec **Immobile** activé, mais ne compte jamais comme en mouvement, donc il ne peut pas déclencher d’instantané ni d’alerte. Choisissez d’autres objets un par un dans la liste **Autres objets** : un colis livré apparaît généralement comme une valise, un sac à dos ou un sac à main. Pour un modèle personnalisé, saisissez ses propres noms d’objets, séparés par des virgules.

## Objets ignorés

**Objets ignorés** sous **Paramètres → Détection IA** désactive entièrement certains types d’objets. Ils sont écartés dès que le modèle les signale, donc ils n’ont jamais de cadre, d’instantané, d’événement ni d’alerte. Utilisez-le pour ce que le modèle confond sans cesse sur vos caméras, comme une terrasse qu’il prend pour un avion. Cochez des catégories entières (**Personnes**, **Véhicules**, **Animaux** et **Autres objets**) ou des objets individuels dans la liste. L’orthographe de l’un ou l’autre modèle correspond, donc ignorer **airplane** ignore aussi **aeroplane**. Pour un modèle personnalisé, saisissez ses propres noms d’objets, séparés par des virgules.

La page **Modifier** d’une caméra peut choisir sa propre liste à la place de la liste globale : choisissez **Choisir pour cette caméra** sous **Objets ignorés**. Ne rien choisir détecte tout sur cette caméra, même ce que la liste globale ignore. L’enregistrement redémarre la détection de cette caméra, ce qui ne réinitialise que le suivi.

## Gigue

Le **seuil de gigue** est le plus petit mouvement en pixels qui compte, quelle que soit la taille de l’objet. En mode Hérité, le **rejet de la gigue des objets immobiles** lisse le tremblement d’un véhicule garé lointain ; il peut aussi maintenir comme immobile un objet réellement lent, c’est pourquoi il est désactivé par défaut. Il ne s’applique pas à Standard, qui gère lui-même le tremblement.

## Moteur externe

**Service HTTP externe** n’exécute aucun modèle sur le nœud. Chaque image est envoyée à un serveur d’inférence que vous exploitez, qui renvoie les cadres en JSON. Utilisez-le pour les modèles que LarisVMS ne fournit pas, ou pour les nœuds sans GPU. Le suivi, les instantanés, la chronologie et la superposition en direct fonctionnent de la même façon.

Le service doit exposer `POST /v1/detect`, `GET /v1/models` et `GET /healthz`. **Tester** vérifie la connexion et charge la liste des modèles. La **clé API** est envoyée comme jeton Bearer. **Transport** : JPEG fonctionne avec n’importe quel service. Les modes bruts YUV et BGRA évitent l’encodage JPEG mais utilisent 4 à 10× la bande passante, et ne fonctionnent que si le modèle les annonce.
