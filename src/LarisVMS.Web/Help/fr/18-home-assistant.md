# Home Assistant

Affichez vos caméras, détections et nœuds d’enregistrement dans Home Assistant, et lancez des enregistrements ou ajoutez des signets depuis celui-ci.

> **Expérimental.** La prise en charge de Home Assistant est nouvelle, et Home Assistant sert à beaucoup d’usages différents. [Dites-nous comment elle fonctionne chez vous](https://github.com/billacuda/LarisVMS/discussions/new?category=general&title=Home%20Assistant%20feedback) : à quoi vous l’utilisez, ce qui marche bien et ce qui ne va pas.

## Fonctionnement

LarisVMS envoie au broker MQTT qu’utilise déjà Home Assistant. La découverte MQTT de Home Assistant ajoute ensuite un appareil pour chaque caméra et chaque nœud d’enregistrement, sans YAML à écrire.

Home Assistant agit avec un rôle que vous choisissez, comme une clé API :

- Il ne reçoit que les caméras que ce rôle peut voir dans **Paramètres → Accès aux caméras**.
- Il ne peut faire que ce que ce rôle autorise. Par exemple, l’interrupteur d’enregistrement n’apparaît que si le rôle a **Démarrer et arrêter l’enregistrement manuel** et l’accès **Enregistrement** aux caméras.
- Ce qu’il fait apparaît dans le journal d’audit sous **Home Assistant**.

## Mise en place

1. Dans Home Assistant, installez le module complémentaire **Mosquitto broker** et l’intégration **MQTT**, si vous ne les utilisez pas déjà. Créez un identifiant de broker pour LarisVMS, par exemple un utilisateur Home Assistant nommé `larisvms`.
2. Dans LarisVMS, créez un rôle pour Home Assistant dans **Paramètres → Autorisations → Rôles**. Donnez-lui **Voir les caméras**, et l’accès aux caméras voulues dans **Paramètres → Accès aux caméras**. Ajoutez **Démarrer et arrêter l’enregistrement manuel**, ou **Lire les enregistrements** et **Créer des signets sur les enregistrements**, si Home Assistant doit pouvoir enregistrer ou ajouter des signets.
3. Ouvrez **Paramètres → Home Assistant** et activez-le. Saisissez l’adresse du broker (celle de votre Home Assistant avec le module complémentaire), le port (souvent `1883`), l’identifiant et le rôle. Sélectionnez **Tester la connexion**, puis **Enregistrer**.
4. Dans Home Assistant, ouvrez **Paramètres → Appareils et services → MQTT**. Vos caméras y apparaissent comme appareils.

L’état dans **Paramètres → Home Assistant** indique si LarisVMS est connecté, et combien de caméras il envoie. Si le broker redémarre ou si le réseau tombe, LarisVMS se reconnecte de lui-même. Quand Home Assistant redémarre, LarisVMS renvoie tout.

## Ce qui apparaît dans Home Assistant

Chaque caméra reçoit :

| Entité | Affiche |
|---|---|
| **Mouvement** | Actif tant qu’il y a du mouvement ou une détection |
| **Personne**, **Véhicule**, **Animal** | Actif tant que ce type d’objet est détecté |
| **En ligne** | Si la caméra envoie de la vidéo |
| **Dernière détection (image)** | Une image du dernier objet détecté |
| **Dernière détection**, **Dernier événement** | Ce qui a été détecté en dernier, et quand |
| **Dernière vitesse** | La dernière vitesse mesurée par les [pièges de vitesse](/Help/ai-detection), dans les unités de **Paramètres → Détection IA** |
| **Enregistrement manuel** | Un interrupteur qui démarre et arrête un enregistrement manuel, si le rôle l’autorise |
| **Ajouter un signet** | Un bouton qui ajoute un signet à l’heure actuelle, si le rôle l’autorise |

Chaque nœud d’enregistrement reçoit **En ligne** et **Stockage libre**.

Un capteur s’active une ou deux secondes après une détection, et se désactive environ 25 secondes après sa fin. C’est le même fonctionnement que l’indicateur de mouvement dans **Direct**.

## Vidéo en direct

La **Generic Camera** de Home Assistant peut afficher la vidéo en direct de LarisVMS. Elle utilise le même flux que les pages web : la caméra n’a pas à en fournir un second.

1. Donnez au rôle de Home Assistant l’autorisation **Diffuser la vidéo en direct vers d’autres programmes**. Aucun rôle ne l’a tant que vous ne l’accordez pas.
2. Dans **Admin → Clés API**, ajoutez une clé liée à ce rôle.
3. Dans Home Assistant, ajoutez une **Generic Camera** pour chaque caméra. **Paramètres → Home Assistant** liste l’**URL de l’image fixe** et la **Source du flux** de chaque caméra. Choisissez l’authentification **Basic**, saisissez `apikey` comme nom d’utilisateur et la clé comme mot de passe.

L’[API](/Help/api) décrit le flux.

## Alertes par webhook

Le webhook d’une règle d’alerte peut aussi envoyer à Home Assistant. Dans Home Assistant, créez une automatisation avec un déclencheur **Webhook**, puis indiquez son adresse dans le webhook de la règle, par exemple `http://homeassistant.local:8123/api/webhook/larisvms-front-door`.

Le webhook envoie du JSON avec la règle, le message et l’heure, et, quand l’alerte les a, l’événement, la caméra, le nœud, l’objet détecté, la vitesse et `snapshotPath`. Ajoutez `snapshotPath` à votre adresse LarisVMS pour obtenir l’image de la détection avec une clé API.

```yaml
triggers:
  - trigger: webhook
    webhook_id: larisvms-front-door
    local_only: true
actions:
  - action: notify.notify
    data:
      message: "{{ trigger.json.message }}"
```

## S’il manque quelque chose

- **Non connecté :** vérifiez l’adresse, le port et l’identifiant avec **Tester la connexion**. Un broker qui exige TLS utilise souvent le port `8883`.
- **Aucun appareil dans Home Assistant :** vérifiez que l’intégration MQTT est configurée, et que le préfixe de découverte correspond à celui de Home Assistant (`homeassistant` sauf si vous l’avez changé).
- **Une caméra manque :** le rôle ne peut pas la voir, ou la caméra est désactivée.
- **Pas d’interrupteur d’enregistrement ni de bouton de signet :** il manque au rôle l’autorisation ou l’accès aux caméras nécessaire.
- **Le webhook n’arrive pas :** LarisVMS n’envoie pas de webhooks vers `localhost`. Utilisez l’adresse réseau de Home Assistant, même s’il tourne sur la même machine.
