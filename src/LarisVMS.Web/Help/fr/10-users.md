# Utilisateurs et accès

Comptes, rôles, autorisations, accès aux caméras, Active Directory et connexion Microsoft.

## Utilisateurs

L’auto-inscription est désactivée. Les comptes sont créés sous **Paramètres → Utilisateurs**, où vous pouvez aussi attribuer des rôles, désactiver un compte et réinitialiser un mot de passe. Les comptes locaux se connectent avec leur adresse e-mail. Désactiver un compte le déconnecte aussitôt partout et ferme sa vidéo en direct.

## Mots de passe

Les mots de passe font au moins 12 caractères. Les mots de passe courants, ceux du type `123456789012` et ceux contenant le nom ou l’adresse e-mail de la personne sont refusés. Pour en refuser davantage, placez-les dans `%ProgramData%\LarisVMS\password-blocklist.txt` sur le serveur web, un par ligne (une liste publiée de mots de passe compromis convient). Il est pris en compte sans redémarrage.

Toute personne ayant un compte local change son propre mot de passe en sélectionnant son nom en haut de la page. Le mot de passe actuel est exigé, et toutes ses autres sessions sont déconnectées. Les administrateurs changent leur propre mot de passe de la même façon, pas sous **Paramètres → Utilisateurs**. Les comptes Active Directory changent le leur dans Windows.

Lorsque vous créez un compte ou réinitialisez un mot de passe sous **Paramètres → Utilisateurs**, vous pouvez cocher **Doit le modifier à la prochaine connexion**. La personne doit alors choisir son propre mot de passe avant de pouvoir faire quoi que ce soit d’autre. Avec **Paramètres → Sécurité → Mots de passe temporaires définis par un administrateur** activé, la case est cochée au départ.

Après 5 mots de passe erronés pour un compte depuis une même adresse en 15 minutes, cette adresse doit attendre la fin des 15 minutes ; la personne peut toujours se connecter depuis n’importe où ailleurs. Un compte n’est verrouillé pendant 15 minutes qu’après 20 essais erronés depuis n’importe où, et les super administrateurs ne sont jamais verrouillés par de mauvais mots de passe à la connexion. Les super administrateurs sont informés des échecs répétés et des verrouillages, ainsi que des tentatives réparties sur de nombreux réseaux : un compte échouant 10 fois depuis 3 réseaux ou plus, ou 50 échecs depuis 10 réseaux ou plus sur l’ensemble du serveur, en 15 minutes.

## Connexion en deux étapes

Toute personne qui se connecte avec un mot de passe, local ou Active Directory, peut aussi exiger un code à six chiffres issu d’une application d’authentification sur son téléphone (Microsoft Authenticator, Google Authenticator, 1Password et d’autres). Chaque code ne fonctionne qu’une fois, donc un code que quelqu’un voit saisir ne peut pas être réutilisé. Pour la configurer, sélectionnez votre nom en haut de la page, puis **Connexion en deux étapes**, scannez le code QR et saisissez le code affiché par l’application. Vous recevez dix codes de récupération ; conservez-les en lieu sûr, car chacun vous connecte une fois si vous perdez votre téléphone. Modifier quoi que ce soit plus tard, comme de nouveaux codes de récupération ou la désactivation, exige un code actuel.

À la connexion, **Ne plus demander sur ce navigateur pendant 14 jours** évite le code sur ce navigateur jusqu’à ce que le mot de passe ou la configuration en deux étapes change.

Si quelqu’un perd son téléphone et ses codes de récupération, **Désactiver la 2FA** à côté de son nom sous **Paramètres → Utilisateurs** lui permet de se connecter avec son seul mot de passe et de la configurer de nouveau.

**Paramètres → Sécurité → Connexion en deux étapes** oblige toutes les personnes qui se connectent avec un mot de passe à la configurer avant de pouvoir faire quoi que ce soit d’autre, et les empêche de la désactiver. Les connexions Microsoft relèvent des propres contrôles d’Entra.

## Sessions

Sélectionnez votre nom en haut de la page, puis **Sessions**, pour voir chaque navigateur sur lequel vous êtes connecté, avec son adresse et sa dernière utilisation. Déconnectez celles que vous ne reconnaissez pas, ou toutes sauf celle que vous utilisez, puis changez votre mot de passe. Changer votre mot de passe ou activer la connexion en deux étapes déconnecte aussitôt vos autres sessions.

Avec **Paramètres → Sécurité → Confirmer le mot de passe pour les pages d’administration** activé, les pages d’administration redemandent votre mot de passe (et un code, si la connexion en deux étapes est activée) sauf si vous vous êtes connecté ou l’avez confirmé au cours des 15 dernières minutes ; leur utilisation prolonge ce délai. Quelqu’un qui trouve votre navigateur connecté peut alors utiliser le système mais pas modifier ses paramètres, ses utilisateurs ou ses clés. Les connexions Microsoft se reconnectent à la place. C’est désactivé par défaut.

Sous **Paramètres → Utilisateurs**, **Déconnecter** à côté d’une personne la déconnecte sur tous les navigateurs. Elle peut se reconnecter ; désactivez le compte pour l’en empêcher.

## Rôles

Un rôle est un ensemble nommé d’autorisations, géré sous **Paramètres → Rôles**. Chaque rôle a aussi :

- un **niveau de portée** ;
- une **expiration** pour un accès limité dans le temps (par exemple, 480 minutes pour un poste de 8 heures) ;
- une **durée de session** : heures après la connexion avant que l’utilisateur doive se reconnecter, quelle que soit son activité (30 jours au maximum, ou 0 pour ne jamais expirer) ;
- un **délai d’inactivité** : minutes sans ouvrir de page avant que l’utilisateur soit déconnecté (0 désactive). Un écran qui affiche de la vidéo en direct compte comme inactif ; laissez-le donc désactivé pour les rôles qui surveillent un mur vidéo ;
- une **priorité PTZ**, qui décide qui l’emporte quand deux utilisateurs déplacent la même caméra.

## Autorisations

**Paramètres → Matrice des autorisations** définit les actions autorisées pour chaque rôle (voir les caméras, lecture, export, modifier les paramètres, etc.). Le rôle intégré **Super administrateur** a toujours toutes les autorisations.

**Démarrer et arrêter l’enregistrement manuel** contrôle le bouton d’enregistrement des tuiles en direct. Il nécessite aussi l’accès **Enregistrement** aux caméras ci-dessous.

## Accès aux caméras

**Paramètres → Accès aux caméras** décide quelles caméras chaque rôle peut utiliser : toutes les caméras, un groupe ou des caméras individuelles, séparément pour Voir, Lecture, Export, PTZ, Parler (audio bidirectionnel), Configurer et Enregistrer (enregistrement manuel). Un rôle **sans autorisation n’a aucune caméra** ; le super administrateur a toujours toutes les caméras. L’export ne va jamais plus loin que la lecture. La mise à niveau depuis une version où les rôles sans autorisation pouvaient utiliser toutes les caméras ajoute une autorisation « toutes les caméras » pour conserver l’accès de chaque rôle et utilisateur.

## Clés API

**Admin → Clés API** émet des clés pour l’[API](/Help/api). Chaque clé agit avec le rôle auquel elle est liée, et peut être limitée à certaines des autorisations de ce rôle, donc plusieurs clés peuvent faire des choses différentes. L’accès aux caméras du rôle décide quelles caméras une clé peut utiliser.

## Entra

**Paramètres → Sécurité → Connexion Entra ID** ajoute un bouton « Se connecter avec Microsoft ». Il ne crée jamais de comptes, et il n’associe pas les comptes par e-mail : sous **Paramètres → Utilisateurs**, utilisez **Connexion Microsoft** sur la ligne d’une personne pour saisir son **ID d’objet** Entra (depuis sa page d’utilisateur dans le centre d’administration Microsoft Entra). Sa première connexion Microsoft finalise le lien ; vide : le supprime. Enregistrez une application dans Entra avec l’URI de redirection affiché sur la page Sécurité, puis saisissez son ID de locataire (le GUID ou le domaine de votre annuaire, pas `common`), son ID client et son secret. Les modifications prennent effet immédiatement. Les comptes déjà liés continuent de fonctionner.

## Active Directory

**Paramètres → Active Directory** permet aux utilisateurs du domaine de se connecter avec leur nom d’utilisateur et leur mot de passe AD, et leur donne des rôles d’après leurs groupes AD. Seul un super administrateur peut modifier le domaine, la façon dont LarisVMS s’y connecte ou la connexion locale, car ces éléments décident qui peut se connecter en tant que n’importe qui ; les autres administrateurs peuvent modifier l’intervalle de synchronisation et les liens de groupes.

- **Domaine :** uniquement le nom de domaine, comme `corp.example.com`.
- **Chiffrement :** LDAPS sur le port 636 est la valeur par défaut et nécessite sur vos contrôleurs de domaine un certificat auquel ce serveur fait confiance. LDAP simple sur le port 389 fonctionne sans, mais n’est pas sécurisé et n’est pas recommandé.
- **Compte de recherche :** la sécurité intégrée (par défaut) utilise le compte propre du service LarisVMS Web. Quand le service s’exécute sous LocalSystem sur un serveur joint au domaine, c’est le compte d’ordinateur. Ou saisissez un compte de service sous la forme `DOMAINE\nom_utilisateur` avec son mot de passe.
- **Connexion :** un nom avec un `@` est un compte local. Tout le reste est un nom d’utilisateur AD, saisi sans le domaine.

**Liens groupe → rôle :** commencez à saisir un nom de groupe et choisissez dans la liste (Tab prend la première correspondance). Les membres du groupe reçoivent le rôle, y compris les membres des groupes imbriqués. Les comptes sont créés la première fois qu’ils sont nécessaires, dès qu’un groupe est lié ou lors de la première connexion de l’utilisateur. Les groupes et les utilisateurs sont suivis par SID, donc renommer l’un ou l’autre dans AD est pris en compte à la prochaine synchronisation. Les rôles des comptes AD proviennent uniquement des liens de groupes et ne peuvent pas être modifiés sur la page Utilisateurs.

**La synchronisation** s’exécute toutes les 30 minutes par défaut (de 5 minutes à 24 heures), ou aussitôt avec **Synchroniser maintenant**. Un compte AD désactivé ou expiré dans AD, ou qui n’appartient plus à aucun groupe lié, est désactivé ici et déconnecté. Si AD est injoignable, la synchronisation ne modifie rien et réessaie dans 5 minutes. Un compte désactivé par la synchronisation est réactivé lorsqu’il remplit de nouveau les conditions. Un compte désactivé à la main sur la page Utilisateurs reste désactivé.

**La connexion locale** peut être désactivée tant que la connexion AD est activée, une fois qu’au moins un compte AD est super administrateur. Conservez en lieu sûr le compte de super administrateur créé lors de l’installation, et son mot de passe. Si AD devient indisponible, exécutez ceci sur la base de données LarisVMS et connectez-vous avec ce compte. Sur SQL Server :

```sql
UPDATE ActiveDirectorySettings SET LocalLoginsEnabled = 1
```

Sur MariaDB :

```sql
UPDATE `ActiveDirectorySettings` SET `LocalLoginsEnabled` = 1;
```

## Journal d’audit

Tout ce que font les utilisateurs est consigné sous **Journaux → Journaux d’audit** : modifications (avec anciennes et nouvelles valeurs ; les secrets ne sont consignés que comme « modifié »), consultation des caméras, démarrage de la lecture et exports.

**Inviolable.** Chaque entrée est scellée avec la précédente, à l’aide d’une clé que la base de données seule ne détient pas. Toutes les 6 heures (et lorsque vous sélectionnez **Vérifier maintenant** sur la page du journal d’audit), l’ensemble du journal est vérifié. Si une entrée a été modifiée, supprimée ou ajoutée directement dans la base de données, la page indique laquelle et les super administrateurs sont notifiés. Les entrées supprimées par la conservation sont attendues et ne comptent pas, et chaque suppression est elle-même consignée (**AuditLog.Pruned**). Les entrées antérieures à cette mise à jour ne sont pas couvertes.

Le serveur conserve aussi un petit enregistrement en dehors de la base de données, dans `%ProgramData%\LarisVMS\audit-chain.dat`, de la clé de scellement et du point de départ du journal, de sorte que le journal ne puisse pas être rescellé avec une autre clé ni effacé et redémarré sans que cela se voie. Si vous repartez volontairement d’une base de données neuve et vide sur le même serveur, supprimez ce fichier une fois ; un nouveau est créé avec l’entrée suivante.

**Transfert vers syslog.** Pour conserver une copie du journal d’audit hors du serveur, saisissez un serveur syslog sous **Paramètres → Journaux**. Chaque nouvelle entrée est envoyée au moment de son écriture (RFC 5424, facility *log audit*, avec l’entrée en JSON). Choisissez UDP, TCP ou TLS. TLS exige que le certificat du serveur syslog soit approuvé par le serveur web. Si le serveur syslog est injoignable, les entrées sont conservées et envoyées dès son retour. Le transfert commence avec l’entrée suivante, pas avec l’historique existant. Seul un super administrateur peut modifier la destination du transfert ou le désactiver ; ce changement est lui-même d’abord envoyé à l’ancienne destination.
