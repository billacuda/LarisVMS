# Sprachen

LarisVMS in einer anderen Sprache anzeigen und eine eigene Übersetzung hinzufügen.

## Eine Sprache wählen

Wählen Sie eine Sprache im Menü 🌐 oben auf jeder Seite, auch auf der Anmeldeseite. Ihre Wahl folgt Ihnen auf jedes Gerät, auf dem Sie sich anmelden. Das Menü erscheint erst, wenn mehr als eine Sprache installiert ist.

Ein Administrator legt unter **Einstellungen → Sprache** die Sprache für alle fest, die keine gewählt haben. Lassen Sie es auf **Die Sprache des Browsers**, um stattdessen dem Browser jeder Person zu folgen.

Auch Datum, Uhrzeit und Zahlen folgen der Sprache. Verwendet Ihr Browser dieselbe Sprache mit einem regionalen Format (etwa britisches Englisch), bleibt das regionale Format erhalten.

Alarme und Benachrichtigungen erreichen jede Person in ihrer eigenen Sprache. Team-Kanäle (E-Mail an eine feste Adresse, Webhook, ntfy, Pushover, Slack und Teams) verwenden die Standardsprache. Das Audit-Protokoll bleibt auf Englisch.

Text, den eine Sprache noch nicht übersetzt, wird auf Englisch angezeigt.

## Eine Sprache hinzufügen

Es sind keine Programmierkenntnisse nötig: Eine Sprache ist eine Textdatei.

1. Laden Sie unter **Einstellungen → Sprache** **en.json** herunter, den englischen Text.
2. Übersetzen Sie den Text rechts in jeder Zeile. Lassen Sie den Schlüssel links genau so, wie er ist.
3. Laden Sie die Datei unter **Einstellungen → Sprache** hoch oder kopieren Sie sie auf dem Server nach `%ProgramData%\LarisVMS\locales`. Benennen Sie sie nach ihrem Sprachcode, zum Beispiel `fr.json`, `de.json` oder `pt-BR.json`.

Sie ist sofort verfügbar, ohne Neustart. Eine Datei, die nicht gelesen werden kann, wird mit der Ursache auf der Sprachseite aufgelistet, und der Rest von LarisVMS funktioniert weiter.

### Was unverändert bleibt

- **Alles in {geschweiften Klammern}** wird von LarisVMS eingesetzt, etwa `{camera}` oder `{count}`. Verschieben Sie es dorthin, wo es in Ihrem Satz hingehört, aber übersetzen oder benennen Sie es nicht um.
- **Einige Tags** können im Text vorkommen: `<code>`, `<strong>`, `<em>`, `<b>`, `<i>`, `<kbd>`, `<br>` und `<a href="…">`. Lassen Sie sie um dieselben Wörter stehen. Anderes HTML ist nicht erlaubt und wird als einfacher Text angezeigt.
- Der Abschnitt **_meta** oben enthält den Namen der Sprache, wie er im Menü 🌐 erscheint (zum Beispiel „Français“), und Ihren Namen, wenn Sie genannt werden möchten.

### Plurale

Manche Texte ändern sich mit einer Zahl. Im Englischen gibt es zwei Formen:

```json
"cameras.count_one": "{count} camera",
"cameras.count_other": "{count} cameras"
```

Fügen Sie die Formen hinzu, die Ihre Sprache braucht, mit diesen Endungen: `_zero`, `_one`, `_two`, `_few`, `_many` und `_other`. Russisch verwendet zum Beispiel `_one`, `_few` und `_many`. Nehmen Sie immer `_other` auf; sie wird verwendet, wenn keine andere Form passt.

### Hilfeseiten

Hilfeseiten werden Datei für Datei übersetzt. Jede Seite behält den Dateinamen der englischen Datei, sodass sich ihre Adresse nicht ändert, und nicht übersetzte Seiten bleiben auf Englisch. Eine regionale Sprache (`es-AR`) verwendet zuerst ihre eigenen Seiten, dann die der allgemeinen (`es`), dann Englisch.

- **Für Ihren eigenen Server:** Kopieren Sie das Markdown einer Seite mit demselben Dateinamen nach `locales\<code>\help\` (zum Beispiel `locales\fr\help\03-cameras.md`) und übersetzen Sie es.
- **Um es zu LarisVMS beizutragen:** Wie Sie es einsenden, steht in [CONTRIBUTING.md](https://github.com/billacuda/LarisVMS/blob/main/CONTRIBUTING.md). Übersetzte Seiten liegen im Quellcode in `src/LarisVMS.Web/Help/<code>/` (zum Beispiel `Help/es-AR/03-cameras.md`), neben den englischen Seiten in `Help/en/`. Beigetragene Seiten werden mit LarisVMS ausgeliefert und erscheinen auch auf [larisvms.com/docs](https://www.larisvms.com/docs/), wo Besucher die Sprache wählen können.

Die Hilfe erscheint in einer Sprache, sobald diese Sprache installiert ist (ihre `.json`-Datei). HTML in einer Seite wird als einfacher Text angezeigt, verwenden Sie also Markdown zur Formatierung.

### Eine Übersetzung aktualisieren

Neue Versionen von LarisVMS fügen Text hinzu. Die Spalte **Übersetzt** auf der Sprachseite zeigt, wie viel des aktuellen Textes jede Sprache abdeckt, und **Hilfeseiten**, für wie viele Hilfeseiten sie einen eigenen Text hat. **Herunterladen** neben einer Sprache liefert ihre Datei mit dem fehlenden Text aus dem Englischen aufgefüllt, bereit zum Übersetzen.

Um eine Datei vor dem Hochladen zu prüfen, führen Sie `tools\locale-check.ps1 fr.json` aus. Es listet fehlende Schlüssel, von dieser Version nicht verwendete Schlüssel und {Platzhalter} auf, die nicht zum englischen Text passen. Fügen Sie `-Help` mit einem Ordner übersetzter Hilfeseiten hinzu (zum Beispiel `-Help locales\fr\help`), um auch diese zu prüfen: noch nicht übersetzte Seiten, von LarisVMS nicht verwendete Dateinamen, HTML und Überschriften, die nicht zur englischen Seite passen.

### Eine Übersetzung testen

Aktivieren Sie unter **Einstellungen → Sprache** **Die Pseudo-Sprache im Sprachmenü anbieten** und wählen Sie sie dann im Menü 🌐. Jeder Text wird mit Akzenten und etwa ein Drittel länger angezeigt, etwa `[Ŝàṽé ··]`. Text, der noch in schlichtem Englisch erscheint, kann noch nicht übersetzt werden; bitte melden Sie ihn. Text, der aus seiner Schaltfläche oder Spalte läuft, braucht eine kürzere Übersetzung oder eine Layoutkorrektur.

### Rechts-nach-links-Sprachen

Arabisch, Hebräisch, Persisch und andere Rechts-nach-links-Sprachen erhalten ein gespiegeltes Layout: Das Menü wandert nach rechts, und Text und Schaltflächen richten sich von rechts aus. Kamerabilder, Zeitleisten und Karten werden nicht gespiegelt.

### Die Installationsprogramme übersetzen

Die Installationsprogramme für Web, Knoten und Proxy haben einen eigenen, viel kürzeren Text, in `installer\Common\Strings.en-US.wxl` im Quellcode. Kopieren Sie ihn in eine nach der Sprache benannte Datei (zum Beispiel `Strings.fr-FR.wxl`) und folgen Sie den Hinweisen am Anfang der Datei. Die Installationsprogramme werden dann mit `.\build-installers.ps1 -Cultures en-US,fr-FR` gebaut, was für jede Sprache einen Satz Installationsprogramme erzeugt. Die eigenen Schaltflächen des Windows-Installers (Weiter, Zurück, Abbrechen und so weiter) sind bereits übersetzt.

## Wo Sprachdateien liegen

- `%ProgramData%\LarisVMS\locales` enthält auf diesem Server hinzugefügte Sprachen. Es übersteht Upgrades.
- `locales` im Installationsordner des Servers enthält die mit LarisVMS gelieferten Sprachen. Eine auf diesem Server hinzugefügte Datei gewinnt Zeile für Zeile gegen eine gleichnamige hier.
- Englisch ist integriert und kann nicht ersetzt werden.

Das Hochladen oder Entfernen einer Sprache wird im Audit-Protokoll festgehalten und erfordert die Berechtigung zum Ändern von Einstellungen.
