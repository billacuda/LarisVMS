# Détection de mouvement

Sources de mouvement, mouvement côté serveur, zones et grille de mouvement.

## Source

Une caméra peut signaler le mouvement de plusieurs façons :

- **Mouvement côté serveur** : le nœud compare les images vidéo à l’intérieur de zones de mouvement ou de la grille de mouvement.
- **Événements de la caméra** : les événements de mouvement ONVIF propres à la caméra.
- **Intégration du fabricant** : par exemple les événements intelligents Dahua / Amcrest.
- **Détection IA** : un objet en mouvement détecté.

La **Source du mouvement** de la caméra choisit laquelle de ces sources décide de ce que le mode Mouvement conserve, afin que la chronologie n’ait pas d’entrées en double pour le même événement. **Auto** choisit la plus riche qui est configurée. Les règles d’étiquettes d’événements et les libellés d’objets IA étiquettent toujours la chronologie, quel que soit ce choix.

## Mouvement côté serveur

**Mouvement côté serveur** sur la page de la caméra active ou désactive la détection de mouvement par pixels propre au nœud. Gardez-le activé pour étiqueter le simple mouvement sur la chronologie en solution de repli, même quand une autre source pilote l’enregistrement. Désactivez-le pour économiser le CPU qu’il utilise.

## Zones

La page **Zones** d’une caméra propose deux méthodes. Une seule est active à la fois, et changer conserve les paramètres de l’autre.

**Grille** : cliquez sur des cellules pour les masquer (rouge). Les cellules deviennent ambrées quand il y a du mouvement dedans, ce qui aide à repérer une branche qui se balance ou une lumière qui scintille à masquer. Changer la taille de la grille efface le masque.

**Zones polygonales** :

- **Mouvement** : une zone à surveiller.
- **Ignorer** : une zone à exclure (un arbre, un trottoir public). Les pixels d’une zone Ignorer ne comptent jamais, même là où elle chevauche une zone Mouvement.
- **Confidentialité** : une zone à masquer en noir. Tracez-les sous **🔒 Masques de confidentialité**, qui fonctionne quelle que soit la méthode de mouvement (Grille ou Zones polygonales) utilisée par la caméra. Elle est masquée dans les enregistrements, la vue en direct (les deux flux), les instantanés et les exports. Les événements se produisent toujours derrière elle comme d’habitude (leurs instantanés sont masqués comme la vidéo), sauf si vous activez **Ignorer aussi les détections à l’intérieur des masques de confidentialité** pour cette caméra, dans la vue Masques de confidentialité. Le masque suit la forme que vous tracez, avec une petite marge pour qu’aucun bord ne transparaisse. Une caméra avec une zone de confidentialité voit sa vidéo réencodée pendant l’enregistrement, ce qui utilise un peu de CPU (ou le GPU, quand le nœud en a un pris en charge).
- Les zones de mouvement **côté caméra** peuvent être enregistrées mais ne sont pas encore actives.

Pour remodeler une zone, sélectionnez-la dans la liste et faites glisser ses coins, puis **Enregistrez**. Pendant le tracé, le clic droit retire le dernier point. Survoler une zone dans la liste la met en évidence sur l’image. Vous pouvez aussi cliquer sur un coin, puis cliquer à l’endroit où il doit aller.

La **Sensibilité** est le pourcentage des pixels de la zone qui doivent changer. Plus bas est plus sensible. Les zones larges nécessitent des valeurs plus basses, car un vrai mouvement n’en couvre qu’une petite partie. Commencez bas (environ 3 %) et n’augmentez que si le vent ou des changements d’éclairage la déclenchent.
