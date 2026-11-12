# Caméras

Ajouter des caméras, la liste des caméras, les flux, les paramètres de l’appareil, les groupes, les appareils multi-objectifs et les intégrations de fabricants.

## Ajouter une caméra

Utilisez **Caméras → Détecter** pour analyser le réseau local à la recherche d’appareils ONVIF, ou **Caméras → Ajouter** pour saisir l’URL du service de l’appareil (par exemple `http://192.168.1.50/onvif/device_service`) et les identifiants. LarisVMS **sonde** ensuite la caméra : il lit ses fonctionnalités ONVIF et ses flux, et choisit les flux principal et secondaire.

Affectez un **nœud d’enregistrement**, sinon la caméra n’enregistrera pas.

## La liste des caméras

**Caméras** liste chaque caméra que vous pouvez voir. La ligne sous les en-têtes de colonnes filtre sur n’importe quelle colonne, comme Marque, Modèle, Nœud ou Groupe, et la zone de recherche au-dessus du tableau filtre par texte n’importe où dans une ligne. Les filtres se combinent, et **Effacer les filtres** les réinitialise. **Colonnes** choisit les colonnes affichées. Un filtre sur une colonne masquée est ignoré.

Vos filtres, colonnes, ordre de tri et taille de page sont enregistrés dans votre compte, donc ils reviennent à votre prochaine connexion, sur n’importe quel appareil.

Cochez des caméras pour agir sur plusieurs à la fois : **Réaffecter la sélection au nœud**, ou **Paramètres de l’appareil…** pour des caméras de même marque et modèle (voir [Modifications groupées](#bulk-changes)). **Tout sélectionner** ne sélectionne que les caméras qui correspondent aux filtres actuels.

## Nouveau sondage

**Sonder à nouveau** sur la page d’une caméra relit ses fonctionnalités et ses flux. Faites-le après avoir modifié la résolution, le codec ou le micrologiciel de la caméra sur sa propre page web. Les modifications faites dans [Paramètres de l’appareil](#device-settings) déclenchent automatiquement un nouveau sondage. Changer l’URL de l’appareil déclenche automatiquement un nouveau sondage à l’enregistrement. **Paramètres → Paramètres des caméras** peut sonder à nouveau chaque caméra une fois par jour. Les caméras sont sondées une par une, afin que les caméras bon marché ne perdent pas leurs sessions d’enregistrement. Les noms de flux et les indicateurs d’activation sont conservés lors d’un nouveau sondage.

## Flux

Chaque caméra a généralement un flux **principal** (pleine résolution, utilisé pour l’enregistrement) et un flux **secondaire** (résolution plus basse, utilisé pour les petites tuiles en direct et la détection IA). Développez **Appareil** sur la page de la caméra pour renommer ou désactiver un flux. Survolez un flux pour voir son URL RTSP. LarisVMS utilise l’adresse avec laquelle la caméra a été ajoutée, en conservant le port et le chemin que la caméra signale, de sorte qu’une caméra qui signale une adresse interne (ou quelqu’un qui se fait passer pour elle) ne puisse pas envoyer l’enregistreur ailleurs avec son mot de passe.

## Paramètres de l’appareil

**Paramètres de l’appareil** sur la page d’une caméra modifie les propres paramètres de la caméra via ONVIF, donc vous n’avez pas besoin de vous connecter à la page web de la caméra. Les paramètres sont lus en direct depuis la caméra, et seuls ceux qu’elle prend en charge sont affichés.

| Section | Paramètres |
|---|---|
| **Flux vidéo** | Pour chaque flux : codec (H.264, H.265 ou MJPEG), profil d’encodage, résolution, fréquence d’images, mode de débit (CBR ou VBR), débit, qualité et intervalle d’images clés |
| **Image** | Luminosité, contraste, saturation et netteté ; mode jour/nuit ; WDR ; compensation de contre-jour ; mode d’exposition ; et balance des blancs |
| **Audio** | Codec, débit et fréquence d’échantillonnage |

Chaque section a son propre bouton **Appliquer**, et seuls les paramètres que vous avez modifiés sont envoyés. Appliquer une modification de flux ou d’audio fait se reconnecter le flux, ce qui laisse une interruption de quelques secondes dans l’enregistrement ; LarisVMS sonde ensuite de nouveau la caméra et le nœud d’enregistrement prend aussitôt en compte le nouveau format. Les modifications d’image prennent effet sans reconnexion.

Certaines caméras partagent un seul jeu de paramètres d’encodeur entre plusieurs flux. La page l’indique, car modifier l’un modifie l’autre.

H.265 et le choix CBR/VBR nécessitent une caméra avec ONVIF Profile T (Media2). Sur les caméras plus anciennes, ces options n’apparaissent pas.

## Modifications groupées

Pour modifier d’un coup plusieurs caméras de même marque et modèle :

1. Sous **Caméras**, filtrez la colonne **Modèle**, puis cochez les caméras (ou **Tout sélectionner**).
2. Cliquez sur **Paramètres de l’appareil…**. C’est uniquement disponible lorsque toutes les caméras cochées ont la même marque et le même modèle.
3. Modifiez ce dont vous avez besoin, et cliquez sur **Appliquer** dans cette section.

Vous pouvez aussi partir de la page **Paramètres de l’appareil** d’une caméra et choisir d’autres caméras du même modèle sous **Appliquer à**. Un badge *micrologiciel différent* signale les caméras avec un autre micrologiciel, qui peuvent proposer des options légèrement différentes.

La page affiche les paramètres actuels de la première caméra. Seuls les paramètres que vous modifiez sont envoyés à chaque caméra, donc le reste des paramètres propres de chaque caméra reste inchangé. Les flux sont appariés par rôle (principal, secondaire ou troisième). Chaque caméra vérifie la modification par rapport à ses propres options, et la liste des résultats indique, pour chaque caméra, si elle a été appliquée, partiellement appliquée, ignorée (par exemple, une caméra sans troisième flux) ou en échec (par exemple, hors ligne). **Réessayer les échecs** renvoie la modification aux caméras en échec.

## Groupes

Les groupes organisent les caméras en sites, bâtiments et étages. Un **groupe de premier niveau est un site**, et les groupes en dessous sont des bâtiments ou des étages. Une caméra peut appartenir à un nombre quelconque de groupes, tant qu’ils appartiennent tous au même site. Les groupes servent au filtrage, aux remplacements de paramètres et à l’[accès aux caméras](/Help/users#camera-access). Le groupe intégré *Toutes les caméras* contient toujours chaque caméra.

## Multicanal

Certains appareils ont plusieurs objectifs dans un seul boîtier. Lorsqu’un sondage trouve plus d’une source vidéo, la page de la caméra propose **Diviser en N caméras**. Chaque objectif devient alors sa propre caméra, avec son propre enregistrement et sa propre conservation. La caméra existante devient le canal 1 et conserve ses enregistrements.

## Intégrations

Certaines fonctions ne sont pas disponibles via ONVIF. Les intégrations (listées sous **Paramètres → Plug-ins**) sont associées automatiquement d’après la marque et le modèle de la caméra lors d’un sondage. Il n’y a rien à activer. Par exemple, l’intégration **Événements intelligents Dahua / Amcrest** lit les détections de personnes et de véhicules issues de l’analyse propre à la caméra. Le panneau **Appareil** de la caméra indique quand une intégration est active.
