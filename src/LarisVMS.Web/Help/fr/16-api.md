# API

Chaque commande de l’API REST, avec ce qu’elle fait, ce qu’elle nécessite et un exemple d’utilisation.

Les exemples utilisent des valeurs fictives. Remplacez `https://vms.example.com` par l’adresse de votre serveur, `lvk_EXAMPLEKEY` par votre clé API, et des identifiants comme `00000000-0000-0000-0000-000000000001` par des identifiants réels.

## Premiers pas

1. Sous **Admin → Clés API**, sélectionnez **Ajouter une clé**. Choisissez un rôle et copiez la clé quand elle s’affiche. Vous ne la voyez qu’une fois.
2. Envoyez la clé dans un en-tête `X-Api-Key` à chaque requête.
3. Appelez `/api/v1/status` pour vérifier que la clé fonctionne et obtenir les identifiants de vos caméras.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" https://vms.example.com/api/v1/status
```

Chaque caméra de la réponse a un `cameraId`, que les autres commandes utilisent.

Seuls les chemins commençant par `/api/v1` acceptent les clés API. Les autres chemins sont internes à l’interface web et peuvent changer sans préavis.

## Clés et autorisations

Vous pouvez avoir autant de clés que vous le souhaitez, chacune avec ses propres autorisations. L’accès d’une clé provient de trois éléments :

- **Rôle :** la clé a les autorisations du rôle auquel elle est liée.
- **Limites :** cochez **Limiter cette clé** pour n’autoriser que certaines de ces autorisations. Une clé ne peut jamais faire plus que son rôle.
- **Accès aux caméras :** l’accès aux caméras du rôle sous **Paramètres → Accès aux caméras** décide quelles caméras la clé peut utiliser. Par exemple, l’action **Enregistrer** doit être activée pour une caméra avant qu’une clé puisse l’enregistrer. Pour limiter une clé à certaines caméras, liez-la à un rôle qui n’a accès qu’à ces caméras.

Chaque commande ci-dessous indique l’autorisation dont elle a besoin. Une clé limitée doit avoir cette autorisation cochée.

Par exemple, ces deux clés pourraient partager un rôle à large accès :

| Clé | Limites | Peut faire |
|---|---|---|
| Supervision | Voir le tableau de bord de l’état des caméras | Lire `/api/v1/status`, et rien d’autre |
| Bouton d’atelier | Voir les caméras, Démarrer et arrêter l’enregistrement manuel | Démarrer, arrêter et lire les enregistrements |

Modifiez le nom et les limites d’une clé sous **Admin → Clés API**. La clé elle-même ne change pas, donc tout ce qui l’utilise continue de fonctionner. Le rôle et l’expiration d’une clé ne peuvent pas changer ; créez plutôt une nouvelle clé. **Révoquer** arrête une clé immédiatement.

Une clé limitée n’est jamais traitée comme un administrateur, même si son rôle est **Super administrateur**.

Les actions d’une clé apparaissent dans le journal d’audit sous la forme `API key: {name}`.

## Conventions

- Les requêtes et les réponses sont en JSON, sauf lorsqu’une commande renvoie une image ou un fichier.
- Les heures sont en UTC, au format ISO 8601, comme `2026-11-06T14:30:00Z`.
- Les noms de champs JSON sont en camelCase.
- **Paramètres → Sécurité** s’applique aussi ici. La liste autorisée de gestion couvre l’API. Les images et les téléchargements comptent comme du trafic de vue en direct et de lecture, donc ils suivent cette liste autorisée et, si vous en avez configuré un, le port distinct.

| Statut | Signification |
|---|---|
| `200` | Cela a fonctionné. |
| `400` | La requête n’est pas valide, par exemple une heure de fin antérieure à l’heure de début. |
| `401` | La clé est absente, erronée, révoquée ou expirée. |
| `403` | La clé n’a pas l’autorisation, ou n’a pas accès à une caméra. |
| `404` | Cet élément n’existe pas. |
| `409` | La caméra ne peut pas faire cela en ce moment, comme enregistrer sur une caméra désactivée, ou déplacer une caméra PTZ utilisée par quelqu’un de priorité supérieure. |
| `429` | Trop de requêtes. Patientez et réessayez. Consultez l’en-tête `Retry-After`. |
| `502`, `503`, `504` | Le nœud d’enregistrement n’a pas pu être joint ou n’a pas répondu à temps. |

## Supervision

### GET /health

Indique si le serveur est en marche. Ne nécessite aucune clé, pour les répartiteurs de charge et les outils de surveillance de disponibilité.

```bash
curl https://vms.example.com/health
```

```text
Healthy
```

### GET /api/v1/status

L’état de chaque caméra et de chaque nœud.

**Autorisation :** Voir le tableau de bord de l’état des caméras. Ne liste que les caméras que la clé peut voir.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" https://vms.example.com/api/v1/status
```

```json
{
  "cameras": [
    {
      "cameraId": "00000000-0000-0000-0000-000000000001",
      "cameraName": "Workshop",
      "cameraEnabled": true,
      "nodeName": "Recorder-1",
      "nodeOnline": true,
      "fps": 15,
      "bitrateKbps": 4096,
      "healthFresh": true
    }
  ],
  "nodes": []
}
```

### GET /api/v1/cameras/motion-state

Les identifiants des caméras qui voient du mouvement en ce moment.

**Autorisation :** Voir les caméras.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" https://vms.example.com/api/v1/cameras/motion-state
```

```json
["00000000-0000-0000-0000-000000000001"]
```

### GET /api/v1/cameras/detection-state

Quelles caméras voient un objet détecté en ce moment, et de quoi il s’agit.

**Autorisation :** Voir les caméras.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" https://vms.example.com/api/v1/cameras/detection-state
```

```json
[
  {
    "cameraId": "00000000-0000-0000-0000-000000000001",
    "detections": [
      { "kind": "Human", "label": "Person", "emoji": "🧍", "colorHex": "#ff9f1c" }
    ]
  }
]
```

## Enregistrement

Démarrer et arrêter l’enregistrement manuel, qui conserve les enregistrements quel que soit le mode d’enregistrement de la caméra. Un enregistrement dure jusqu’à ce que quelque chose l’arrête, y compris malgré les redémarrages et les mises à niveau. Voir [Enregistrement](/Help/recording#manual-recording).

Démarrer une caméra qui enregistre déjà, ou en arrêter une qui n’enregistre pas, est sans danger et ne change rien.

Les commandes de démarrage et d’arrêt renvoient les caméras qui ont `changed`, celles qui sont `unchanged` et celles qui ont été `skipped` parce qu’elles sont désactivées ou n’existent pas.

### POST /api/v1/cameras/{cameraId}/recording/start

Démarre l’enregistrement d’une caméra. Renvoie `409` si la caméra est désactivée ou n’existe pas.

**Autorisation :** Démarrer et arrêter l’enregistrement manuel, plus l’accès **Enregistrement** aux caméras.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/recording/start
```

```json
{
  "changed": ["00000000-0000-0000-0000-000000000001"],
  "unchanged": [],
  "skipped": []
}
```

### POST /api/v1/cameras/{cameraId}/recording/stop

Arrête l’enregistrement d’une caméra.

**Autorisation :** Démarrer et arrêter l’enregistrement manuel, plus l’accès **Enregistrement** aux caméras.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/recording/stop
```

### POST /api/v1/recording/start

Démarre l’enregistrement de plusieurs caméras. Renvoie `403`, et ne change rien, si la clé n’a accès à aucune d’elles.

**Autorisation :** Démarrer et arrêter l’enregistrement manuel, plus l’accès **Enregistrement** aux caméras.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraIds":["00000000-0000-0000-0000-000000000001","00000000-0000-0000-0000-000000000002"]}' \
  https://vms.example.com/api/v1/recording/start
```

### POST /api/v1/recording/stop

Arrête l’enregistrement de plusieurs caméras.

**Autorisation :** Démarrer et arrêter l’enregistrement manuel, plus l’accès **Enregistrement** aux caméras.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraIds":["00000000-0000-0000-0000-000000000001","00000000-0000-0000-0000-000000000002"]}' \
  https://vms.example.com/api/v1/recording/stop
```

### GET /api/v1/recording

Quelles caméras conservent des enregistrements en ce moment. Les caméras où rien ne se passe ne sont pas listées.

`keeping` signifie que des enregistrements sont sauvegardés à l’instant, pour une raison quelconque. `manual` signifie qu’un enregistrement manuel est actif. Une caméra hors ligne avec un enregistrement manuel actif affiche `manual` sans `keeping`.

**Autorisation :** Voir les caméras.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" https://vms.example.com/api/v1/recording
```

```json
[
  {
    "cameraId": "00000000-0000-0000-0000-000000000001",
    "keeping": true,
    "manual": true,
    "manualStartedUtc": "2026-11-06T14:30:00Z",
    "manualStartedBy": "API key: Workshop button"
  }
]
```

## Images

### GET /api/v1/cameras/{cameraId}/snapshot

Un JPEG pris maintenant depuis la caméra. Cela ouvre une courte connexion vers la caméra ; utilisez donc la dernière miniature ci-dessous si vous vérifiez souvent.

**Autorisation :** Ajouter, modifier et supprimer des caméras, plus l’accès **Configurer** aux caméras.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" -o snapshot.jpg \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/snapshot
```

### GET /api/v1/cameras/{cameraId}/thumbnail/latest

Une image tirée des enregistrements sauvegardés les plus récents de la caméra. Une caméra sans enregistrement sauvegardé n’a pas de miniature.

**Autorisation :** Voir les caméras.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" -o latest.jpg \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/thumbnail/latest
```

## PTZ

### POST /api/v1/cameras/{cameraId}/ptz/move

Fait bouger la caméra. Les valeurs vont de `-1` à `1`, où `0` signifie aucun mouvement. La caméra continue de bouger jusqu’à ce que vous appeliez stop.

| Champ | Signification |
|---|---|
| `panX` | Vers la gauche (négatif) ou la droite (positif). |
| `tiltY` | Vers le bas (négatif) ou le haut (positif). |
| `zoomX` | Zoom arrière (négatif) ou avant (positif). |

**Autorisation :** Voir les caméras, plus l’accès **PTZ** aux caméras.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"panX":0.5,"tiltY":0,"zoomX":0}' \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/ptz/move
```

### POST /api/v1/cameras/{cameraId}/ptz/stop

Arrête le mouvement de la caméra.

**Autorisation :** Voir les caméras, plus l’accès **PTZ** aux caméras.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/ptz/stop
```

## Données de lecture

### GET /api/v1/cameras/{cameraId}/timeline

Un résumé d’une période, découpée en tranches de taille égale : si chacune contient des enregistrements ou du mouvement, et les couleurs des éventuels événements.

| Paramètre | Signification |
|---|---|
| `from`, `to` | La période. Obligatoire. |
| `buckets` | Combien de tranches. 200 par défaut. |

**Autorisation :** Lire les enregistrements, plus l’accès **Lecture** aux caméras.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" \
  "https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/timeline?from=2026-11-06T00:00:00Z&to=2026-11-07T00:00:00Z&buckets=24"
```

```json
[
  {
    "startUtc": "2026-11-06T00:00:00Z",
    "endUtc": "2026-11-06T01:00:00Z",
    "hasRecording": true,
    "hasMotion": false,
    "tagColorHex": "#e5383b",
    "tagColorHexes": ["#e5383b"]
  }
]
```

### GET /api/v1/cameras/{cameraId}/segments

Les fichiers d’enregistrements sauvegardés sur une période.

| Paramètre | Signification |
|---|---|
| `from`, `to` | La période. Obligatoire. |

**Autorisation :** Lire les enregistrements, plus l’accès **Lecture** aux caméras.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" \
  "https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/segments?from=2026-11-06T14:00:00Z&to=2026-11-06T15:00:00Z"
```

```json
[
  {
    "id": 1234,
    "startUtc": "2026-11-06T14:30:00Z",
    "endUtc": "2026-11-06T14:31:00Z",
    "isArchived": false
  }
]
```

### GET /api/v1/cameras/{cameraId}/activity

Quand les événements ont tendance à se produire sur cette caméra, selon l’heure de la journée.

| Paramètre | Signification |
|---|---|
| `range` | Jusqu’où remonter, comme `7d` (par défaut) ou `30d`. |
| `view` | `calendar` l’affiche par jour, pour 30 jours ou plus. |
| `sel` | Un type d’événement ou un libellé à inclure, comme `Animal` ou `Animal:cat`. Répétez pour plusieurs. |

**Autorisation :** Lire les enregistrements, plus l’accès **Lecture** aux caméras.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" \
  "https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/activity?range=30d"
```

## Signets

### GET /api/v1/cameras/{cameraId}/bookmarks

Les signets d’une caméra sur une période.

| Paramètre | Signification |
|---|---|
| `from`, `to` | La période. Obligatoire. |

**Autorisation :** Lire les enregistrements, plus l’accès **Lecture** aux caméras.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" \
  "https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/bookmarks?from=2026-11-06T00:00:00Z&to=2026-11-07T00:00:00Z"
```

```json
[
  {
    "id": "00000000-0000-0000-0000-00000000000b",
    "cameraId": "00000000-0000-0000-0000-000000000001",
    "cameraName": "Workshop",
    "timestampUtc": "2026-11-06T14:30:00Z",
    "note": "Print failed here",
    "createdByUserName": "API key: Workshop button",
    "createdAt": "2026-11-06T14:35:00Z"
  }
]
```

### POST /api/v1/bookmarks

Ajoute un signet à un instant des enregistrements d’une caméra.

**Autorisation :** Lire les enregistrements, et Créer des signets sur les enregistrements, plus l’accès **Lecture** aux caméras.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraId":"00000000-0000-0000-0000-000000000001","timestampUtc":"2026-11-06T14:30:00Z","note":"Print failed here"}' \
  https://vms.example.com/api/v1/bookmarks
```

```json
{ "id": "00000000-0000-0000-0000-00000000000b" }
```

## Exports

Un export sauvegarde les enregistrements d’une ou plusieurs caméras dans un fichier par caméra. Les exports s’exécutent en arrière-plan : créez-en un, suivez son avancement et téléchargez chaque fichier une fois terminé.

### POST /api/v1/exports

Démarre un export. Limité à 20 requêtes par minute et par clé.

**Autorisation :** Créer et télécharger des exports, plus l’accès **Export** aux caméras.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraIds":["00000000-0000-0000-0000-000000000001"],"fromUtc":"2026-11-06T14:00:00Z","toUtc":"2026-11-06T14:30:00Z"}' \
  https://vms.example.com/api/v1/exports
```

```json
{ "jobId": "00000000-0000-0000-0000-00000000000e" }
```

### GET /api/v1/exports

Liste les exports et leur progression. Chaque export a des `items`, un pour chaque caméra. L’`id` d’un élément sert à le télécharger ou à le relancer.

**Autorisation :** Créer et télécharger des exports.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" https://vms.example.com/api/v1/exports
```

```json
[
  {
    "id": "00000000-0000-0000-0000-00000000000e",
    "createdUtc": "2026-11-06T14:40:00Z",
    "requestedByUserName": "API key: Workshop button",
    "fromUtc": "2026-11-06T14:00:00Z",
    "toUtc": "2026-11-06T14:30:00Z",
    "status": "Completed",
    "items": [
      {
        "id": "00000000-0000-0000-0000-00000000000f",
        "cameraId": "00000000-0000-0000-0000-000000000001",
        "cameraName": "Workshop",
        "nodeName": "Recorder-1",
        "status": "Completed",
        "errorMessage": null,
        "outputSizeBytes": 104857600,
        "redacted": false
      }
    ]
  }
]
```

### GET /api/v1/exports/items/{exportItemId}/download

Télécharge un fichier d’export terminé.

**Autorisation :** Créer et télécharger des exports, plus l’accès **Export** aux caméras.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" -o workshop.mp4 \
  https://vms.example.com/api/v1/exports/items/00000000-0000-0000-0000-00000000000f/download
```

### POST /api/v1/exports/{itemId}/retry

Relance un élément d’export en échec.

**Autorisation :** Créer et télécharger des exports.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/exports/00000000-0000-0000-0000-00000000000f/retry
```

### DELETE /api/v1/exports/{jobId}

Supprime un export et ses fichiers. Une clé peut supprimer les exports qu’elle a créés. Seul un administrateur peut supprimer ceux des autres.

**Autorisation :** Créer et télécharger des exports.

```bash
curl -X DELETE -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/exports/00000000-0000-0000-0000-00000000000e
```

## Verrouillages de preuves

Un verrouillage de preuves empêche la suppression d’enregistrements. Voir [Verrouillages de preuves](/Help/live-and-playback#evidence-locks).

### GET /api/v1/evidence-locks

Liste les verrouillages.

| Paramètre | Signification |
|---|---|
| `cameraId` | Uniquement cette caméra. Répétez pour plusieurs. |
| `fromUtc`, `toUtc` | Uniquement les verrouillages sur des enregistrements de cette période. |
| `includeInactive` | `true` inclut les verrouillages terminés. |

**Autorisation :** Lire les enregistrements, plus l’accès **Lecture** aux caméras.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" \
  "https://vms.example.com/api/v1/evidence-locks?cameraId=00000000-0000-0000-0000-000000000001"
```

### POST /api/v1/evidence-locks

Verrouille des enregistrements sur une ou plusieurs caméras.

| Champ | Signification |
|---|---|
| `cameraIds` | Les caméras. |
| `fromUtc`, `toUtc` | Les enregistrements à verrouiller. |
| `duration` | `1d`, `3d`, `7d`, `14d`, `30d`, `custom` ou `indefinite`. |
| `customUntilUtc` | Quand un verrouillage `custom` se termine. |
| `reason` | Pourquoi il est verrouillé. |

**Autorisation :** Lire les enregistrements, et Verrouiller les enregistrements pour que la conservation ne puisse pas les supprimer, plus l’accès **Lecture** aux caméras.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraIds":["00000000-0000-0000-0000-000000000001"],"fromUtc":"2026-11-06T14:00:00Z","toUtc":"2026-11-06T14:30:00Z","duration":"30d","reason":"Incident 1042"}' \
  https://vms.example.com/api/v1/evidence-locks
```

### POST /api/v1/evidence-locks/{id}/remove

Retire un verrouillage. Un motif est obligatoire.

**Autorisation :** Lire les enregistrements, et les mêmes règles que pour retirer un verrouillage dans l’interface web.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"reason":"Case closed"}' \
  https://vms.example.com/api/v1/evidence-locks/00000000-0000-0000-0000-00000000000c/remove
```

### POST /api/v1/evidence-locks/{id}/restore

Rétablit un verrouillage retiré pendant son délai de grâce.

**Autorisation :** Lire les enregistrements, et les mêmes règles que pour rétablir un verrouillage dans l’interface web.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/evidence-locks/00000000-0000-0000-0000-00000000000c/restore
```

## Absent de l’API

Ces éléments ne sont pas disponibles pour les clés API :

- La vidéo en direct. Utilisez l’interface web, ou les images ci-dessus.
- La lecture directe des enregistrements. Utilisez un export.
- Les paramètres, les utilisateurs, les rôles, les clés API, le chiffrement et les sauvegardes.
- La configuration des caméras, comme les zones, les plannings et les paramètres de l’appareil.
