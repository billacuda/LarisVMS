# Stockage et conservation

Où se trouvent les enregistrements, combien de temps ils sont conservés, le seuil et le volume d’archive.

## Chemins de stockage

Chaque nœud enregistre dans son propre **chemin de stockage**, défini sur la page de modification du nœud ou à l’installation. Il n’y a pas de chemin global. Un nœud sans chemin de stockage n’enregistre rien, et la liste des nœuds le signale.

## Conservation

La **conservation** est le nombre de jours pendant lesquels les enregistrements sont gardés. Elle se définit globalement sous **Paramètres → Stockage et conservation**, et peut être remplacée par nœud et par caméra ; la valeur la plus spécifique l’emporte. 0 conserve les enregistrements indéfiniment.

Une caméra peut aussi avoir un **quota de stockage** en Go. Lorsqu’il est dépassé, ses enregistrements les plus anciens sont supprimés en premier.

## Seuil

Le **seuil** est une limite stricte de remplissage du disque d’un nœud (90 % par défaut). Au-delà, le nœud archive ses enregistrements les plus anciens plus tôt, ou les supprime si l’archivage n’est pas disponible, jusqu’à ce que le disque repasse sous la limite. La liste des nœuds affiche **stockage plein** pendant que cela se produit.

## Archive

Un nœud peut avoir un second **chemin d’archive** facultatif, comme un partage SMB ou un disque USB. Il doit être un emplacement distinct du chemin de stockage. Avec l’archivage activé, les enregistrements qui arrivent à échéance de conservation sont **déplacés** vers l’archive au lieu d’être supprimés, et conservés jusqu’à atteindre la **conservation des archives**. La conservation des archives est comptée à partir de la date d’enregistrement d’origine, et 0 conserve indéfiniment.

La lecture, les miniatures et les exports fonctionnent de la même façon depuis l’un ou l’autre volume. La liste des nœuds affiche l’espace libre et une estimation des jours restants pour les deux volumes. L’estimation se fonde sur les dernières 24 heures d’écriture.

## Enregistrements résiduels

Lorsqu’une caméra passe à un autre nœud ou est supprimée, ses anciens enregistrements restent sur le nœud précédent jusqu’à leur échéance. Le nombre ⚠️ à côté de l’état d’un nœud indique combien de caméras ont encore des enregistrements là. Il disparaît de lui-même.

## Chiffrement

Les enregistrements peuvent être chiffrés sur les disques des enregistreurs, afin qu’un disque volé ou mis au rebut ne montre rien. C’est désactivé par défaut.

**Configurez-le** sous **Paramètres → Chiffrement et signature** :

1. **Créer la clé principale.** Elle est conservée sur ce serveur et déverrouille chaque enregistrement chiffré.
2. **Téléchargez une sauvegarde** de la clé principale, protégée par une phrase secrète de votre choix. Le chiffrement ne peut pas être activé tant que vous n’en avez pas une. Conservez le fichier et la phrase secrète ailleurs que sur ce serveur : si le serveur est reconstruit, **Restaurer** avec la sauvegarde est le seul moyen de lire de nouveau les enregistrements chiffrés. Si vous perdez les deux, les enregistrements sont perdus définitivement.
3. **Choisissez ce qu’il faut chiffrer :** **Vidéo** (enregistrements et audio d’interphone bidirectionnel), ou **Vidéo, instantanés et miniatures**. C’est la valeur par défaut de chaque caméra ; chaque caméra peut la remplacer sur sa propre page (**Caméras → Modifier**, Stockage).

Chaque segment est chiffré sur l’enregistreur un instant après la fin de son enregistrement, donc seul le segment en cours d’enregistrement est jamais non chiffré sur le disque. Les enregistrements faits avant l’activation du chiffrement restent tels quels ; **chiffrer les enregistrements existants** sur la page de la caméra les convertit en arrière-plan. Désactiver plus tard le chiffrement laisse les enregistrements chiffrés lisibles.

La lecture, les miniatures, les instantanés et les exports fonctionnent comme avant. Les fichiers exportés ne sont pas chiffrés, puisqu’ils sont destinés à être transmis. L’enregistreur récupère la clé quotidienne de chaque caméra auprès de ce serveur quand il en a besoin et ne la garde qu’en mémoire ; un enregistrement chiffré ne peut donc pas être lu tant que l’enregistreur ne peut pas joindre le serveur.
