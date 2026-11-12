# Alertes, notifications et e-mail

Règles d’alerte, notifications aux utilisateurs, canaux d’équipe et configuration du fournisseur d’e-mail.

## Règles d’alerte

**Paramètres → Alertes** contient les règles partagées. Chaque règle surveille une condition :

- **Caméra sans signal** : la caméra a cessé d’envoyer des rapports d’état.
- **Nœud hors ligne** : le nœud a cessé de se connecter.
- **Stockage faible** : l’espace libre est passé sous un pourcentage.
- **Basculement de nœud** : les caméras d’un nœud sont passées à son nœud de secours.
- **Mouvement détecté** : mouvement sur une caméra, ou dans l’une de ses zones de mouvement.
- **Objet détecté** : la détection IA a vu un objet correspondant.
- **Vitesse supérieure à une limite** : un objet a franchi un [contrôle de vitesse](/Help/ai-detection#speed-traps) à une vitesse donnée ou au-dessus.
- **Verrouillage de preuves bientôt terminé** et **Verrouillage de preuves retiré** : voir [verrouillages de preuves](/Help/live-and-playback#evidence-locks).

Les règles d’état sont vérifiées une fois par minute. Le **délai de répétition** est la durée minimale entre deux alertes répétées, afin qu’une condition qui reste vraie ne déclenche pas une alerte chaque minute.

### Alertes de détection

**Objet détecté** et **Vitesse supérieure à une limite** se déclenchent quelques secondes après la détection. Chaque filtre est facultatif, et tous ceux que vous définissez doivent correspondre :

- **Caméra** : une caméra, les caméras d’un nœud, ou n’importe quelle caméra.
- **Catégorie** et **libellé** : par exemple Véhicule, ou seulement les camions.
- **Couleur** : la couleur de l’objet, ou la couleur de la chemise ou du pantalon d’une personne. Voir [couleurs des objets](/Help/ai-detection#object-colors).
- **Zone** : uniquement les objets situés à l’intérieur de l’une des zones de la caméra (jugé d’après le bas du cadre).
- **Actif** : jours de la semaine et plage horaire, dans le fuseau horaire de la personne qui a enregistré la règle. Une plage comme 22:00 à 06:00 passe la nuit.

Une détection n’alerte qu’une fois. Sur une règle qui surveille plusieurs caméras, le délai de répétition est compté séparément pour chaque caméra, afin qu’une caméra très active ne retienne pas les autres.

### Alertes de mouvement

**Mouvement détecté** se déclenche quelques secondes après un mouvement, issu de la détection de mouvement du serveur ou des événements de mouvement propres à la caméra. Choisissez une caméra, les caméras d’un nœud, ou n’importe quelle caméra. Avec une seule caméra choisie, **Zone** la limite à l’une de ses zones **Mouvement du serveur** ; laissez **(toute la caméra)** pour un mouvement n’importe où. Les jours et heures **Actif** fonctionnent comme pour les alertes de détection. Une période de mouvement n’alerte qu’une fois, et le délai de répétition retient la suivante.

### Canaux d’équipe

Chaque règle peut aussi envoyer vers des listes d’e-mails, des webhooks, ntfy, Pushover, Slack et Teams. Activez un canal et renseignez ses champs.

## Notifications

Les notifications vont à des personnes, dans leur navigateur ou par e-mail, plutôt qu’à un canal d’équipe. Chaque personne choisit ce qu’elle veut sous **Notifications** dans la barre latérale (aussi le 🔔 à côté de votre nom). Une notification n’atteint quelqu’un que pour les caméras qu’il peut voir.

- **Notifications partagées** : sur une règle d’alerte, **Autoriser les utilisateurs à s’abonner** la liste sous **Mes notifications → Disponible**, où chacun peut l’activer. Les administrateurs peuvent aussi ajouter un utilisateur, ou tous les membres d’un rôle, sous **Abonnés** sur la règle.
- **Vos propres notifications** : **Mes notifications → Les miennes → Nouvelle notification** en crée une rien que pour vous, avec les mêmes choix qu’une règle d’alerte. Par exemple, animaux sur la caméra du jardin entre 22 h et 6 h. Vous seul la voyez.
- **Mettre en sourdine** une notification pendant une heure, 8 heures, un jour, ou jusqu’à ce que vous la réactiviez, sans vous désabonner.

**Paramètres → Notifications** active ou désactive chaque type de notification pour les utilisateurs. Désactiver un type n’arrête pas les canaux d’équipe des règles d’alerte.

S’abonner nécessite l’autorisation **S’abonner aux notifications partagées**, et créer les vôtres nécessite **Créer des notifications personnelles**. Chaque rôle a les deux par défaut.

### Notifications du navigateur

Activez-les dans chaque navigateur sous **Mes notifications → Ce navigateur et ces appareils**, puis utilisez **Envoyer un test**. **Paramètres → Notifications** choisit leur mode de remise :

| Remise | Fonctionne quand | Nécessite |
|---|---|---|
| **Tant que LarisVMS est ouvert** | Un onglet LarisVMS ou l’application installée est ouvert | Rien. Reste sur votre réseau. |
| **Web Push** | Aucune page LarisVMS ouverte (Chrome, Edge, Firefox et Safari). Sur un ordinateur, le navigateur lui-même doit être en cours d’exécution ; voir ci-dessous | **Générer des clés Web Push** une fois, et un accès Internet sortant du serveur vers les services push des éditeurs de navigateurs. Aucun compte. |
| **Firebase Cloud Messaging** | Aucune page LarisVMS ouverte, avec la même limite sur les ordinateurs | Votre propre projet Firebase : sa configuration d’application web, son certificat web push et une clé de compte de service. Utilisé uniquement lorsque Web Push est désactivé. |

Sans autorisation d’afficher des notifications, elles apparaissent à la place dans les pages LarisVMS ouvertes. Sur un téléphone, installez d’abord LarisVMS comme application (voir [la vue d’ensemble](/Help/overview)).

Les téléphones reçoivent les notifications push même avec le navigateur fermé. Sur un ordinateur, un navigateur ne les reçoit que lorsqu’il est en cours d’exécution, et fermer sa dernière fenêtre le quitte généralement ; ce qui est envoyé entre-temps s’affiche à son redémarrage. Pour le garder actif en arrière-plan, activez **Continuer à exécuter les applications en arrière-plan lorsque Google Chrome est fermé** (Chrome : Paramètres → Système) ou **Continuer à exécuter les extensions et applications en arrière-plan lorsque Microsoft Edge est fermé** (Edge : Paramètres → Système et performances). Utilisez l’e-mail pour toute personne qui a besoin d’alertes sans navigateur en cours d’exécution.

### Notifications par e-mail

Les personnes peuvent aussi choisir l’e-mail pour une notification. Elle va à l’adresse de leur compte, via l’e-mail configuré ci-dessous. **Paramètres → Notifications** peut désactiver les notifications par e-mail.

## E-mail

Les alertes et notifications par e-mail sont envoyées via le fournisseur configuré sous **Paramètres → E-mail**. **Enregistrer et envoyer un test** vérifie les paramètres enregistrés.

- **SMTP** : hôte, port, SSL/TLS et identifiants.
- **Microsoft Graph** : enregistrez une application dans Entra ID, accordez-lui l’autorisation d’**application** `Mail.Send` (pas déléguée) avec consentement de l’administrateur, et saisissez son ID de locataire, son ID client et son secret. Il n’y a pas de connexion par utilisateur.
- **Gmail** : créez un client OAuth de type *Application web* dans Google Cloud. Ajoutez `https://<votre-hôte>/Admin/OAuthCallback` comme URI de redirection autorisé, et activez la portée `https://mail.google.com/`. Utilisez ensuite **Enregistrer et se connecter à Google** une fois pour accorder le consentement.

Les secrets enregistrés sont chiffrés. Si les clés de protection des données du serveur sont perdues, saisissez-les de nouveau et reconnectez Gmail.
