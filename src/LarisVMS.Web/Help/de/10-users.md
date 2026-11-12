# Benutzer und Zugriff

Konten, Rollen, Berechtigungen, Kamerazugriff, Active Directory und Microsoft-Anmeldung.

## Benutzer

Die Selbstregistrierung ist deaktiviert. Konten werden unter **Einstellungen → Benutzer** erstellt, wo Sie auch Rollen zuweisen, ein Konto deaktivieren und ein Passwort zurücksetzen können. Lokale Konten melden sich mit ihrer E-Mail-Adresse an. Das Deaktivieren eines Kontos meldet es sofort überall ab und schließt sein Live-Video.

## Passwörter

Passwörter haben mindestens 12 Zeichen. Gängige, solche wie `123456789012` und solche, die den Namen oder die E-Mail-Adresse der Person enthalten, werden abgelehnt. Um weitere abzulehnen, tragen Sie sie in `%ProgramData%\LarisVMS\password-blocklist.txt` auf dem Webserver ein, eines pro Zeile (eine veröffentlichte Liste kompromittierter Passwörter funktioniert). Sie wird ohne Neustart übernommen.

Jeder mit einem lokalen Konto ändert sein eigenes Passwort, indem er oben auf der Seite seinen Namen auswählt. Das aktuelle Passwort ist erforderlich, und alle anderen Sitzungen der Person werden abgemeldet. Administratoren ändern ihr eigenes Passwort auf dieselbe Weise, nicht unter **Einstellungen → Benutzer**. Active-Directory-Konten ändern ihres in Windows.

Wenn Sie unter **Einstellungen → Benutzer** ein Konto erstellen oder ein Passwort zurücksetzen, können Sie **Muss es bei der nächsten Anmeldung ändern** ankreuzen. Die Person muss dann ein eigenes Passwort wählen, bevor sie etwas anderes tun kann. Bei aktivierter Option **Einstellungen → Sicherheit → Vorläufige, vom Administrator gesetzte Passwörter** ist es zunächst angekreuzt.

Nach 5 falschen Passwörtern für ein Konto von einer Adresse innerhalb von 15 Minuten muss diese Adresse die 15 Minuten abwarten; die Person kann sich weiterhin von überall sonst anmelden. Ein Konto wird erst nach 20 Fehlversuchen von beliebiger Stelle für 15 Minuten gesperrt, und Super-Admins werden bei der Anmeldung nie durch falsche Passwörter gesperrt. Super-Admins erfahren von wiederholten Fehlschlägen und Sperrungen sowie von Raten, das über viele Netzwerke verteilt ist: ein Konto, das 10-mal aus 3 oder mehr Netzwerken scheitert, oder 50 Fehlschläge aus 10 oder mehr Netzwerken serverweit, innerhalb von 15 Minuten.

## Zwei-Faktor-Anmeldung

Jeder, der sich mit einem Passwort anmeldet, ob lokal oder über Active Directory, kann zusätzlich einen sechsstelligen Code aus einer Authenticator-App auf seinem Telefon verlangen (Microsoft Authenticator, Google Authenticator, 1Password und andere). Jeder Code funktioniert nur einmal, sodass einer, den jemand beim Eintippen sieht, nicht erneut verwendet werden kann. Um sie einzurichten, wählen Sie oben auf der Seite Ihren Namen, dann **Zwei-Faktor-Anmeldung**, scannen Sie den QR-Code und geben Sie den von der App angezeigten Code ein. Sie erhalten zehn Wiederherstellungscodes; bewahren Sie sie sicher auf, denn jeder meldet Sie einmal an, falls Sie Ihr Telefon verlieren. Spätere Änderungen, etwa neue Wiederherstellungscodes oder das Deaktivieren, erfordern einen aktuellen Code.

Bei der Anmeldung überspringt **In diesem Browser 14 Tage lang nicht erneut fragen** den Code in diesem Browser, bis sich das Passwort oder die Zwei-Faktor-Einrichtung ändert.

Verliert jemand sein Telefon und seine Wiederherstellungscodes, lässt **2FA deaktivieren** neben seinem Namen unter **Einstellungen → Benutzer** ihn nur mit dem Passwort anmelden und sie erneut einrichten.

**Einstellungen → Sicherheit → Zwei-Faktor-Anmeldung** zwingt jeden, der sich mit einem Passwort anmeldet, sie einzurichten, bevor er etwas anderes tun kann, und verhindert, dass er sie deaktiviert. Microsoft-Anmeldungen bleiben den eigenen Prüfungen von Entra überlassen.

## Sitzungen

Wählen Sie oben auf der Seite Ihren Namen, dann **Sitzungen**, um jeden Browser zu sehen, in dem Sie angemeldet sind, mit seiner Adresse und wann er zuletzt verwendet wurde. Melden Sie alle ab, die Sie nicht kennen, oder alle außer der, die Sie gerade nutzen, und ändern Sie dann Ihr Passwort. Das Ändern Ihres Passworts oder das Aktivieren der Zwei-Faktor-Anmeldung meldet Ihre anderen Sitzungen sofort ab.

Bei aktivierter Option **Einstellungen → Sicherheit → Passwort für Administrationsseiten bestätigen** fragen Administrationsseiten erneut nach Ihrem Passwort (und einem Code, wenn die Zwei-Faktor-Anmeldung aktiv ist), sofern Sie sich nicht in den letzten 15 Minuten angemeldet oder es bestätigt haben; ihre Nutzung hält das aufrecht. Jemand, der Ihren angemeldeten Browser vorfindet, kann das System dann nutzen, aber nicht seine Einstellungen, Benutzer oder Schlüssel ändern. Microsoft-Anmeldungen melden sich stattdessen erneut an. Standardmäßig ist es aus.

Unter **Einstellungen → Benutzer** meldet **Abmelden** neben einer Person sie in jedem Browser ab. Sie kann sich erneut anmelden; deaktivieren Sie das Konto, um sie fernzuhalten.

## Rollen

Eine Rolle ist ein benannter Satz von Berechtigungen, verwaltet unter **Einstellungen → Rollen**. Jede Rolle hat außerdem:

- eine **Bereichsstufe**;
- einen **Ablauf** für zeitlich begrenzten Zugriff (zum Beispiel 480 Minuten für eine 8-Stunden-Schicht);
- eine **Sitzungsdauer**: Stunden nach der Anmeldung, bis sich der Benutzer erneut anmelden muss, egal wie aktiv er ist (höchstens 30 Tage, oder 0 für kein Ablaufen);
- ein **Inaktivitäts-Timeout**: Minuten ohne Öffnen einer Seite, bevor der Benutzer abgemeldet wird (0 ist aus). Ein Bildschirm, der Live-Video zeigt, gilt als inaktiv, lassen Sie es also bei Rollen aus, die eine Videowand betrachten;
- eine **PTZ-Priorität**, die entscheidet, wer gewinnt, wenn zwei Benutzer dieselbe Kamera bewegen.

## Berechtigungen

Die **Einstellungen → Berechtigungsmatrix** legt fest, welche Aktionen jede Rolle ausführen darf (Kameras ansehen, Wiedergabe, Export, Einstellungen bearbeiten und so weiter). Die integrierte Rolle **Super-Admin** hat immer jede Berechtigung.

**Manuelle Aufzeichnung starten und stoppen** steuert die Aufnahmetaste auf Live-Kacheln. Es benötigt außerdem den unten genannten Kamerazugriff **Aufzeichnung**.

## Kamerazugriff

**Einstellungen → Kamerazugriff** bestimmt, welche Kameras jede Rolle nutzen darf: jede Kamera, eine Gruppe oder einzelne Kameras, getrennt für Ansehen, Wiedergabe, Export, PTZ, Sprechen (Gegensprechen), Konfigurieren und Aufzeichnung (manuelle Aufzeichnung). Eine Rolle **ohne Freigaben hat keine Kameras**; Super-Admin hat immer jede Kamera. Der Export reicht nie weiter als die Wiedergabe. Beim Upgrade von einer Version, in der Rollen ohne Freigaben jede Kamera nutzen konnten, wird eine Freigabe „Alle Kameras“ hinzugefügt, damit der Zugriff jeder Rolle und jedes Benutzers gleich bleibt.

## API-Schlüssel

**Administration → API-Schlüssel** stellt Schlüssel für die [API](/Help/api) aus. Jeder Schlüssel handelt mit der Rolle, an die er gebunden ist, und kann auf einen Teil der Berechtigungen dieser Rolle beschränkt werden, sodass mehrere Schlüssel Verschiedenes tun können. Der Kamerazugriff der Rolle bestimmt, welche Kameras ein Schlüssel nutzen kann.

## Entra

**Einstellungen → Sicherheit → Entra-ID-Anmeldung** fügt eine Schaltfläche „Mit Microsoft anmelden“ hinzu. Sie erstellt nie Konten und ordnet Konten nicht anhand der E-Mail zu: Verwenden Sie unter **Einstellungen → Benutzer** **Microsoft-Anmeldung** in der Zeile einer Person, um ihre Entra-**Objekt-ID** einzugeben (von ihrer Benutzerseite im Microsoft Entra Admin Center). Ihre erste Microsoft-Anmeldung schließt die Verknüpfung ab; leer entfernt sie. Registrieren Sie in Entra eine App mit dem auf der Sicherheitsseite angezeigten Umleitungs-URI und geben Sie dann ihre Mandanten-ID (die GUID oder Domäne Ihres Verzeichnisses, nicht `common`), Client-ID und das Secret ein. Änderungen werden sofort wirksam. Bereits verknüpfte Konten funktionieren weiter.

## Active Directory

**Einstellungen → Active Directory** lässt Domänenbenutzer sich mit ihrem AD-Benutzernamen und -Passwort anmelden und gibt ihnen Rollen anhand ihrer AD-Gruppen. Nur ein Super-Admin kann die Domäne, die Verbindungsart von LarisVMS oder die lokale Anmeldung ändern, da diese entscheiden, wer sich als wer anmelden kann; andere Administratoren können das Synchronisierungsintervall und die Gruppenverknüpfungen ändern.

- **Domäne:** nur der Domänenname, etwa `corp.example.com`.
- **Verschlüsselung:** LDAPS auf Port 636 ist der Standard und erfordert ein Zertifikat auf Ihren Domänencontrollern, dem dieser Server vertraut. Einfaches LDAP auf Port 389 funktioniert ohne, ist aber unsicher und nicht empfohlen.
- **Konto zum Nachschlagen:** Integrierte Sicherheit (der Standard) verwendet das eigene Konto des Dienstes LarisVMS Web. Läuft der Dienst als LocalSystem auf einem domänenverbundenen Server, ist das das Computerkonto. Oder geben Sie ein Dienstkonto als `DOMAIN\username` mit seinem Passwort ein.
- **Anmeldung:** Ein Name mit einem `@` ist ein lokales Konto. Alles andere ist ein AD-Benutzername, ohne die Domäne eingegeben.

**Verknüpfungen Gruppe → Rolle:** Beginnen Sie, einen Gruppennamen einzugeben, und wählen Sie aus der Liste (Tab nimmt den ersten Treffer). Mitglieder der Gruppe erhalten die Rolle, auch Mitglieder verschachtelter Gruppen. Konten werden erstellt, sobald sie zum ersten Mal gebraucht werden, also sobald eine Gruppe verknüpft wird oder wenn der Benutzer sich zum ersten Mal anmeldet. Gruppen und Benutzer werden per SID verfolgt, sodass eine Umbenennung in AD bei der nächsten Synchronisierung übernommen wird. Rollen für AD-Konten stammen nur aus Gruppenverknüpfungen und können auf der Seite „Benutzer“ nicht bearbeitet werden.

Die **Synchronisierung** läuft standardmäßig alle 30 Minuten (5 Minuten bis 24 Stunden) oder sofort mit **Jetzt synchronisieren**. Ein AD-Konto, das in AD deaktiviert oder abgelaufen ist oder keiner verknüpften Gruppe mehr angehört, wird hier deaktiviert und abgemeldet. Ist AD nicht erreichbar, ändert die Synchronisierung nichts und versucht es in 5 Minuten erneut. Ein durch die Synchronisierung deaktiviertes Konto wird wieder aktiviert, sobald es die Voraussetzungen erneut erfüllt. Ein von Hand auf der Seite „Benutzer“ deaktiviertes bleibt deaktiviert.

Die **lokale Anmeldung** kann bei aktiver AD-Anmeldung deaktiviert werden, sobald mindestens ein AD-Konto Super-Admin ist. Bewahren Sie das bei der Einrichtung erstellte Super-Admin-Konto und sein Passwort sicher auf. Wird AD nicht verfügbar, führen Sie dies gegen die LarisVMS-Datenbank aus und melden Sie sich mit diesem Konto an. Unter SQL Server:

```sql
UPDATE ActiveDirectorySettings SET LocalLoginsEnabled = 1
```

Unter MariaDB:

```sql
UPDATE `ActiveDirectorySettings` SET `LocalLoginsEnabled` = 1;
```

## Audit-Protokoll

Alles, was Benutzer tun, wird unter **Protokolle → Audit-Protokolle** aufgezeichnet: Änderungen (mit alten und neuen Werten; Geheimnisse werden nur als „geändert“ erfasst), das Ansehen von Kameras, das Starten der Wiedergabe und Exporte.

**Manipulationssicher.** Jeder Eintrag wird zusammen mit dem vorherigen versiegelt, mit einem Schlüssel, den die Datenbank allein nicht besitzt. Alle 6 Stunden (und wenn Sie auf der Seite des Audit-Protokolls **Jetzt prüfen** wählen) wird das gesamte Protokoll geprüft. Wurde ein Eintrag direkt in der Datenbank geändert, entfernt oder hinzugefügt, nennt die Seite, welcher, und Super-Admins werden benachrichtigt. Durch die Aufbewahrung entfernte Einträge sind erwartet und zählen nicht, und jede Entfernung wird selbst aufgezeichnet (**AuditLog.Pruned**). Einträge aus der Zeit vor diesem Update sind nicht abgedeckt.

Der Server führt außerdem außerhalb der Datenbank, in `%ProgramData%\LarisVMS\audit-chain.dat`, einen kleinen Nachweis über den Schlüssel des Siegels und den Beginn des Protokolls, sodass das Protokoll nicht mit einem anderen Schlüssel neu versiegelt oder gelöscht und neu begonnen werden kann, ohne dass dies auffällt. Wenn Sie bewusst mit einer neuen, leeren Datenbank auf demselben Server neu beginnen, löschen Sie diese Datei einmal; mit dem nächsten Eintrag wird eine neue erstellt.

**An Syslog weiterleiten.** Um eine Kopie des Audit-Protokolls außerhalb des Servers aufzubewahren, geben Sie unter **Einstellungen → Protokolle** einen Syslog-Server ein. Jeder neue Eintrag wird beim Schreiben gesendet (RFC 5424, Facility *log audit*, mit dem Eintrag als JSON). Wählen Sie UDP, TCP oder TLS. TLS erfordert, dass der Webserver dem Zertifikat des Syslog-Servers vertraut. Ist der Syslog-Server nicht erreichbar, werden Einträge aufbewahrt und gesendet, sobald er wieder erreichbar ist. Die Weiterleitung beginnt mit dem nächsten Eintrag, nicht mit dem bestehenden Verlauf. Nur ein Super-Admin kann ändern, wohin das Protokoll weitergeleitet wird, oder die Weiterleitung deaktivieren; diese Änderung selbst wird zuerst an das alte Ziel gesendet.
