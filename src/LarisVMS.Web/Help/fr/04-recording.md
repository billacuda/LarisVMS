# Enregistrement

Modes d’enregistrement, plannings, étiquettes d’événements, pré/post-enregistrement et durée des segments.

## Modes

Chaque caméra enregistre en continu vers son nœud. Le **mode d’enregistrement** détermine quels enregistrements sont *conservés* :

| Mode | Conserve |
|---|---|
| **Continu** | Tout. |
| **Mouvement** | Les enregistrements autour d’un mouvement : issu d’une zone de mouvement, des événements de mouvement propres à la caméra, d’une intégration ou de la détection IA (voir [Mouvement](/Help/motion#source)). |
| **Planning** | Les enregistrements qui commencent à l’intérieur d’une plage de planning activée. |
| **Événement** | Les enregistrements autour d’une règle d’étiquette d’événement pour laquelle *Pilote l’enregistrement* est activé. |
| **Manuel uniquement** | Rien, sauf si un [enregistrement manuel](#manual-recording) est actif. La caméra n’est qu’en vue en direct le reste du temps. |

Les enregistrements qui ne sont pas conservés sont supprimés peu après leur écriture, au lieu d’attendre la conservation.

Les modes Mouvement, Planning et Événement **sont permissifs par défaut**. Tant qu’une caméra n’a pas de zone de mouvement, de plage de planning ou de règle pilote, elle conserve tout au lieu de le rejeter. La page de la caméra avertit lorsque cela se produit. Manuel uniquement n’est pas permissif, car ne rien conserver est son but.

Définissez le mode global sous **Paramètres → Valeurs d’enregistrement par défaut**, et remplacez-le sur la page d’une caméra.

## Enregistrement manuel

Un enregistrement manuel conserve les enregistrements d’une caméra jusqu’à ce que quelqu’un l’arrête, quel que soit le mode d’enregistrement de la caméra. Lancez-en un avec le bouton d’enregistrement d’une [tuile en direct](/Help/live-and-playback#live-tiles) ou via l’[API](/Help/api#recording). Il reste actif malgré les redémarrages et les mises à niveau.

Il fonctionne avec le mode de la caméra. Une caméra en mode Mouvement conserve les enregistrements quand elle voit du mouvement *ou* quand un enregistrement manuel est actif, et les événements étiquettent toujours la chronologie comme d’habitude. Sur une caméra en mode Continu, il ne fait que marquer la chronologie.

- Les enregistrements sont conservés par segments entiers, donc jusqu’à un segment avant et après l’enregistrement est sauvegardé aussi. Le pré-enregistrement et le post-enregistrement ne s’appliquent pas.
- Les enregistrements sauvegardés suivent la conservation normale de la caméra. Ajoutez un [verrouillage de preuves](/Help/live-and-playback#evidence-locks) pour les conserver plus longtemps.
- Il apparaît sur la chronologie sous forme de bande rouge. Changez la couleur sous **Paramètres → Événements**.
- Une caméra hors ligne ne sauvegarde rien jusqu’à son retour. L’enregistrement reste actif.
- Mettez à niveau les nœuds d’enregistrement en même temps que le serveur. Un nœud plus ancien ne connaît pas les enregistrements manuels.

## Pré-enregistrement et post-enregistrement

Dans les modes Mouvement et Événement, le **pré-enregistrement** est la durée d’enregistrement conservée avant l’activité (pour voir quelqu’un entrer), et le **post-enregistrement** celle conservée après sa fin.

## Durée des segments

Les enregistrements sont écrits sous forme de fichiers d’une durée fixe (60 secondes par défaut). La lecture doit télécharger une partie d’un segment avant de pouvoir démarrer à l’instant exact que vous choisissez. Des segments plus courts accélèrent la recherche sur les caméras haute résolution, mais créent plus de fichiers. Modifier la durée redémarre brièvement l’enregistrement de la caméra.

## Planning

La page **Planning** d’une caméra contient ses plages : des jours, une heure de début et une heure de fin. Une heure de fin antérieure à l’heure de début passe minuit. Les heures utilisent l’**horloge locale du nœud d’enregistrement**. Les plages ne comptent qu’en mode Planning.

## Étiquettes d’événements

La page **Étiquettes d’événements** d’une caméra transforme les événements ONVIF de la caméra (mouvement, alarmes, analyse, sabotage) en marques colorées sur la chronologie.

- **Sujet de démarrage** : l’événement qui démarre l’étiquette. Choisissez-en un dans *Sujets observés*, qui liste tout ce que la caméra a réellement envoyé.
- **Sujet d’arrêt** : facultatif. Laissez-le vide lorsque le sujet de démarrage porte son propre état vrai/faux (c’est le cas de la plupart des sujets de mouvement et d’alarme). Renseignez-le lorsque la caméra envoie des événements de démarrage et d’arrêt distincts.
- **Pilote l’enregistrement** : en mode Événement ou Mouvement, les enregistrements autour de cette étiquette sont conservés.

Une étiquette ne se termine que lorsque son événement d’arrêt arrive, jamais sur un délai d’expiration.
