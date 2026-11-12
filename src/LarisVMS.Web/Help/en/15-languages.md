# Languages

Show LarisVMS in another language, and add your own translation.

## Choosing a language

Pick a language from the 🌐 menu at the top of any page, including the sign-in page. Your choice follows you to every device you sign in on. The menu only appears once more than one language is installed.

An admin sets the language for everyone who hasn't picked one under **Settings → Language**. Leave it on **The browser's language** to follow each person's browser instead.

Dates, times, and numbers follow the language too. If your browser uses the same language with a regional format (British English, say), the regional format is kept.

Alerts and notifications reach each person in their own language. Team channels (email to a fixed address, webhook, ntfy, Pushover, Slack, and Teams) use the default language. The audit log stays in English.

Text a language doesn't translate yet shows in English.

## Adding a language

No programming is needed: a language is one text file.

1. Under **Settings → Language**, download **en.json**, the English text.
2. Translate the text on the right of each line. Keep the key on the left exactly as it is.
3. Upload the file under **Settings → Language**, or copy it to `%ProgramData%\LarisVMS\locales` on the server. Name it for its language code, for example `fr.json`, `de.json`, or `pt-BR.json`.

It's available straight away, with no restart. A file that can't be read is listed on the Language page with the reason, and the rest of LarisVMS keeps working.

### What to keep as it is

- **Anything in {curly braces}** is filled in by LarisVMS, such as `{camera}` or `{count}`. Move it wherever it belongs in your sentence, but don't translate or rename it.
- **A few tags** may appear in the text: `<code>`, `<strong>`, `<em>`, `<b>`, `<i>`, `<kbd>`, `<br>`, and `<a href="…">`. Keep them around the same words. Other HTML isn't allowed and shows as plain text.
- The **_meta** section at the top holds the language's name as it appears in the 🌐 menu (for example "Français"), and your name, if you'd like credit.

### Plurals

Some text changes with a number. In English there are two forms:

```json
"cameras.count_one": "{count} camera",
"cameras.count_other": "{count} cameras"
```

Add the forms your language needs, with these endings: `_zero`, `_one`, `_two`, `_few`, `_many`, and `_other`. Russian, for example, uses `_one`, `_few`, and `_many`. Always include `_other`; it's used when no other form fits.

### Help pages

Help pages are translated one file at a time. Copy a page's Markdown into `locales\<code>\help\` with the same file name (for example `locales\fr\help\03-cameras.md`) and translate it. Pages you haven't translated stay in English.

### Updating a translation

New versions of LarisVMS add text. The **Translated** column on the Language page shows how much of the current text each language covers. **Download** next to a language gives you its file with the missing text filled in from English, ready to translate.

To check a file before uploading it, run `tools\locale-check.ps1 fr.json`. It lists missing keys, keys this version doesn't use, and {placeholders} that don't match the English text.

### Testing a translation

Turn on **Offer the pseudo-locale in the language menu** under **Settings → Language**, then choose it from the 🌐 menu. Every piece of text is shown accented and about a third longer, like `[Ŝàṽé ··]`. Text that still shows in plain English can't be translated yet; please report it. Text that overflows its button or column needs a shorter translation, or a layout fix.

### Right-to-left languages

Arabic, Hebrew, Persian, and other right-to-left languages get a mirrored layout: the menu moves to the right, and text and buttons line up from the right. Camera pictures, timelines, and maps aren't mirrored.

### Translating the installers

The Web, Node, and Proxy installers have their own, much shorter text, in `installer\Common\Strings.en-US.wxl` in the source code. Copy it to a file named for the language (for example `Strings.fr-FR.wxl`), and follow the notes at the top of the file. The installers are then built with `.\build-installers.ps1 -Cultures en-US,fr-FR`, which makes a set of installers for each language. Windows' own installer buttons (Next, Back, Cancel, and so on) are already translated.

## Where language files live

- `%ProgramData%\LarisVMS\locales` holds languages added on this server. It survives upgrades.
- `locales` in the server's install folder holds languages that come with LarisVMS. A file added on this server wins over one with the same name here, line by line.
- English is built in and can't be replaced.

Uploading or removing a language is recorded in the audit log, and needs permission to change settings.
