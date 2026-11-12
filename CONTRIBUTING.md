# Contributing to LarisVMS

Thanks for helping. Fixes, features, documentation, and translations are all welcome. This page
explains how to send them and the terms they're accepted under.

LarisVMS is maintained by one person, so please be patient with reviews. For anything bigger than
a small fix, open an issue first to talk it over, so nobody spends time on something that can't be
merged.

## Licensing of contributions

LarisVMS is licensed under the [Apache License 2.0](LICENSE). By sending a contribution, you agree
that:

- It's licensed under the Apache License 2.0, the same as the rest of the project (see section 5
  of the license). This applies however you send it: a pull request, an issue, an email, or files
  shared some other way.
- You wrote it, or otherwise have the right to contribute it under that license.
- You keep the copyright in your own contribution. The project, and everyone who uses it, can use,
  change, and redistribute it under the license.

**Pull requests** (not accepted at this time; see [Pull requests](#pull-requests)): sign off each
commit (`git commit -s`). This adds a `Signed-off-by:` line, which certifies the
[Developer Certificate of Origin](https://developercertificate.org/).

**Contributions sent outside GitHub** (by email or as files): please include one line in writing,
such as "I contribute these files to LarisVMS under the Apache License 2.0."

Contributors are credited by name in the [changelog](CHANGELOG.md), with a link to their GitHub
profile if they have one. If you'd rather not be credited, please say so.

## Reporting bugs and security problems

- **Bugs and feature ideas:** open an issue using one of the templates.
- **Security problems:** don't open a public issue. Follow [SECURITY.md](SECURITY.md) instead.

## Translations

A translation has two parts, and you can send either or both. Translations are one of the most
helpful contributions.

### The interface

1. In LarisVMS, go to **Settings → Language** and download the English template (or an existing
   language's file to continue it). The file is named after its language code, for example
   `es-AR.json`.
2. Translate the values. Keep the keys, the `{placeholders}`, and the few allowed tags (`<code>`,
   `<strong>`, `<em>`, `<b>`, `<i>`, `<kbd>`, `<br>`, and `<a href="…">` for a link within LarisVMS)
   as they are.
3. Check it: `.\tools\locale-check.ps1 es-AR.json`. It lists missing keys, keys this version
   doesn't use, and placeholders that don't match the English text.
4. Try it on your own server by uploading it under **Settings → Language**, with the pseudo-locale
   to spot anything that isn't translatable yet.
5. Open an issue to say you'd like to contribute it, and the maintainer will arrange how to send it.
   Include the written statement from [Licensing of contributions](#licensing-of-contributions).
   It's added to the source as `src/LarisVMS.Web/locales/<code>.json`.

### Help pages

The English Help pages are in `src/LarisVMS.Web/Help/en/`. Translations go in
`src/LarisVMS.Web/Help/<code>/`, for example `Help/es-AR/03-cameras.md`.

- **Keep each file's name.** It decides the page's address, so a translation that renames a file
  isn't used.
- **Keep the same headings in the same order and at the same levels**, translating only their text.
  Links across the app ("Learn more") and between pages point at sections by their English
  heading, and a translated page with matching headings keeps those links working. A test checks
  this.
- **Use Markdown only.** HTML in a Help page is shown as plain text.
- **Untranslated pages stay in English**, so you can translate a few pages at a time.
- **Check them:** `.\tools\locale-check.ps1 -Help src\LarisVMS.Web\Help\es-AR` lists pages not
  translated yet, file names that aren't used, HTML, and headings that don't line up with the
  English page.

Translated Help ships with the app. It's also published on
[larisvms.com/docs](https://www.larisvms.com/docs/), where visitors can choose the language.

## Code

### Building and testing

See [Building from source](README.md#-building-from-source) in the README. You need the .NET SDK
version in `global.json` (the one releases are built with) or a newer .NET 10 SDK. In short:

```powershell
dotnet tool restore
dotnet build
dotnet test --filter "FullyQualifiedName~<area you changed>"
```

The full test suite is large. While you work, run the tests for the area you changed, and say which
tests you ran when you send the change.

### Pull requests

> **Pull requests aren't accepted at this time.** LarisVMS doesn't take contributions through GitHub
> right now. If it opens up to official contributors later, pull requests will be accepted under the
> terms on this page, including [Licensing of contributions](#licensing-of-contributions) and the
> sign-off. Until then, open an issue to suggest a change, or to ask about contributing a
> translation.

When pull requests are open:

- Keep each pull request to one change, with a description of what it does and why.
- Add tests for fixes and new behavior where practical.
- Database changes need a migration for both providers: `.\tools\add-migration.ps1 <Name>`.
- Package changes go in `Directory.Packages.props`. Build once in Debug so the `packages.lock.json`
  files update, and include them in the change. Release builds restore only what the lock files
  list, so a stale lock file stops the release build.
- Add a line to `CHANGELOG.md` under **[Unreleased]**, in the matching section (🛠️ Fixed,
  ⚙️ Changed, ➕ Added, ➖ Removed, or 🔒 Security). Follow the existing style:
  `- **Short title.** One or two plain sentences about what changed for the person using it.`
- User-facing text (interface, Help, README, and changelog) uses the serial comma: "cameras,
  nodes, and proxies."
- Interface text goes through the language files, not hard-coded English. See the existing pages
  for how `L["…"]` and `larisvmsI18n.t('…')` are used.

### AI-assisted contributions

AI tools are fine to use, but you must say so. When you send a contribution, state whether AI tools
were used and, if they were, which ones (for example, the assistant or model and its version) and
what they were used for: writing code, translating, reviewing, or something else. A contribution
that doesn't say isn't accepted.

As with any contribution, you're responsible for what you send: review it, test it, and make sure
you have the right to contribute it. 