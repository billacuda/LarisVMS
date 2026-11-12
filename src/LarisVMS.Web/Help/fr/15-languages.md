# Langues

Afficher LarisVMS dans une autre langue, et ajouter votre propre traduction.

## Choisir une langue

Choisissez une langue dans le menu 🌐 en haut de n’importe quelle page, y compris la page de connexion. Votre choix vous suit sur chaque appareil sur lequel vous vous connectez. Le menu n’apparaît que lorsque plus d’une langue est installée.

Un administrateur définit sous **Paramètres → Langue** la langue de toutes les personnes qui n’en ont pas choisi. Laissez **La langue du navigateur** pour suivre à la place le navigateur de chaque personne.

Les dates, les heures et les nombres suivent aussi la langue. Si votre navigateur utilise la même langue avec un format régional (l’anglais britannique, par exemple), le format régional est conservé.

Les alertes et les notifications parviennent à chaque personne dans sa propre langue. Les canaux d’équipe (e-mail vers une adresse fixe, webhook, ntfy, Pushover, Slack et Teams) utilisent la langue par défaut. Le journal d’audit reste en anglais.

Le texte qu’une langue n’a pas encore traduit s’affiche en anglais.

## Ajouter une langue

Aucune programmation n’est nécessaire : une langue est un fichier texte.

1. Sous **Paramètres → Langue**, téléchargez **en.json**, le texte anglais.
2. Traduisez le texte à droite de chaque ligne. Laissez la clé à gauche exactement telle quelle.
3. Téléversez le fichier sous **Paramètres → Langue**, ou copiez-le dans `%ProgramData%\LarisVMS\locales` sur le serveur. Nommez-le d’après son code de langue, par exemple `fr.json`, `de.json` ou `pt-BR.json`.

Il est disponible immédiatement, sans redémarrage. Un fichier qui ne peut pas être lu est listé sur la page Langue avec la raison, et le reste de LarisVMS continue de fonctionner.

### Ce qu’il faut laisser tel quel

- **Tout ce qui est entre {accolades}** est renseigné par LarisVMS, comme `{camera}` ou `{count}`. Déplacez-le à l’endroit où il doit se trouver dans votre phrase, mais ne le traduisez pas et ne le renommez pas.
- **Quelques balises** peuvent apparaître dans le texte : `<code>`, `<strong>`, `<em>`, `<b>`, `<i>`, `<kbd>`, `<br>` et `<a href="…">`. Gardez-les autour des mêmes mots. Les autres éléments HTML ne sont pas autorisés et s’affichent comme du texte simple.
- La section **_meta** en haut contient le nom de la langue tel qu’il apparaît dans le menu 🌐 (par exemple « Français »), et votre nom, si vous souhaitez être cité.

### Pluriels

Certains textes changent avec un nombre. En anglais, il y a deux formes :

```json
"cameras.count_one": "{count} camera",
"cameras.count_other": "{count} cameras"
```

Ajoutez les formes dont votre langue a besoin, avec ces terminaisons : `_zero`, `_one`, `_two`, `_few`, `_many` et `_other`. Le russe, par exemple, utilise `_one`, `_few` et `_many`. Incluez toujours `_other` ; elle est utilisée quand aucune autre forme ne convient.

### Pages d’aide

Les pages d’aide se traduisent fichier par fichier. Chaque page garde le nom du fichier anglais, donc son adresse ne change pas, et les pages que vous n’avez pas traduites restent en anglais. Une langue régionale (`es-AR`) utilise d’abord ses propres pages, puis celles de la langue générale (`es`), puis l’anglais.

- **Pour votre propre serveur :** copiez le Markdown d’une page dans `locales\<code>\help\` avec le même nom de fichier (par exemple `locales\fr\help\03-cameras.md`) et traduisez-le.
- **Pour la proposer à LarisVMS :** voir [CONTRIBUTING.md](https://github.com/billacuda/LarisVMS/blob/main/CONTRIBUTING.md) pour savoir comment l’envoyer. Les pages traduites se trouvent dans `src/LarisVMS.Web/Help/<code>/` dans les sources (par exemple `Help/es-AR/03-cameras.md`), à côté des pages anglaises dans `Help/en/`. Les pages proposées sont livrées avec LarisVMS et apparaissent aussi sur [larisvms.com/docs](https://www.larisvms.com/docs/), où les visiteurs peuvent choisir la langue.

L’aide s’affiche dans une langue dès que cette langue est installée (son fichier `.json`). Le HTML dans une page est affiché comme du texte simple, utilisez donc Markdown pour la mise en forme.

### Mettre à jour une traduction

Les nouvelles versions de LarisVMS ajoutent du texte. La colonne **Traduit** de la page Langue indique quelle part du texte actuel chaque langue couvre, et **Pages d’aide** pour combien de pages d’aide elle a son propre texte. **Télécharger** à côté d’une langue vous donne son fichier avec le texte manquant complété depuis l’anglais, prêt à être traduit.

Pour vérifier un fichier avant de le téléverser, exécutez `tools\locale-check.ps1 fr.json`. Il liste les clés manquantes, les clés que cette version n’utilise pas et les {espaces réservés} qui ne correspondent pas au texte anglais. Ajoutez `-Help` avec un dossier de pages d’aide traduites (par exemple `-Help locales\fr\help`) pour vérifier celles-ci aussi : pages pas encore traduites, noms de fichiers que LarisVMS n’utilise pas, HTML et titres qui ne correspondent pas à la page anglaise.

### Tester une traduction

Activez **Proposer la pseudo-langue dans le menu des langues** sous **Paramètres → Langue**, puis choisissez-la dans le menu 🌐. Chaque texte est affiché avec des accents et environ un tiers plus long, comme `[Ŝàṽé ··]`. Le texte qui s’affiche encore en anglais simple ne peut pas encore être traduit ; merci de le signaler. Le texte qui déborde de son bouton ou de sa colonne nécessite une traduction plus courte, ou une correction de la mise en page.

### Langues de droite à gauche

L’arabe, l’hébreu, le persan et d’autres langues de droite à gauche reçoivent une mise en page en miroir : le menu passe à droite, et le texte et les boutons s’alignent depuis la droite. Les images des caméras, les chronologies et les cartes ne sont pas inversées.

### Traduire les programmes d’installation

Les programmes d’installation Web, Node et Proxy ont leur propre texte, bien plus court, dans `installer\Common\Strings.en-US.wxl` dans le code source. Copiez-le dans un fichier nommé d’après la langue (par exemple `Strings.fr-FR.wxl`) et suivez les notes en haut du fichier. Les programmes d’installation sont ensuite construits avec `.\build-installers.ps1 -Cultures en-US,fr-FR`, ce qui produit un jeu de programmes d’installation pour chaque langue. Les boutons propres à l’installateur Windows (Suivant, Précédent, Annuler, etc.) sont déjà traduits.

## Où se trouvent les fichiers de langue

- `%ProgramData%\LarisVMS\locales` contient les langues ajoutées sur ce serveur. Il survit aux mises à niveau.
- `locales` dans le dossier d’installation du serveur contient les langues fournies avec LarisVMS. Un fichier ajouté sur ce serveur l’emporte, ligne par ligne, sur un fichier du même nom ici.
- L’anglais est intégré et ne peut pas être remplacé.

Le téléversement ou la suppression d’une langue est consigné dans le journal d’audit, et nécessite l’autorisation de modifier les paramètres.
