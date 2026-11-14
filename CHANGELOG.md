# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/2.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.221.4] - 2026-11-14

The latest .NET 10 patch: runtime and libraries at 10.0.15.

### ⚙️ Changed

- **.NET 10.0.15.** The installers include the .NET 10.0.15 runtime, and LarisVMS uses the 10.0.15
  ASP.NET Core, Entity Framework Core, and other .NET libraries. Built with the .NET SDK 10.0.404,
  which `global.json` now requires.

## [0.221.3] - 2026-11-14

A maintenance update: a clean build and a working .NET 10 code scan.

### 🔒 Security

- **Small hardening.** Connecting a Gmail account in Settings → Email only ever returns you to
  LarisVMS, and preparing a translated Help page has a time limit.

### ⚙️ Changed

- **Clean build.** Release builds finish without warnings; reviewed analyzer findings are marked
  with their reasons in the code.
- **.NET 10 code scan.** The scan builds again when GitHub's runner has a newer .NET SDK than the
  one the release lock files were made with.

## [0.221.2] - 2026-11-14

Another small security update: page text escaped everywhere, a few labels that stayed in English
now translated, and more code scanning.

### 🔒 Security

- **Escaped page text.** The Activity page, Dashboard, Map, Export check, and the schedule and zone
  editors now escape every translated label, count, and ID before showing it, like the rest of
  their text.

### 🛠️ Fixed

- **Labels in English.** The online/offline badges on the Dashboard and Nodes pages, "unassigned"
  on the Dashboard, the sort button's tooltip on dashboard widgets, and "(floorplan)" in the map
  picker now follow your language.

### ⚙️ Changed

- **More code scanning.** DevSkim, ESLint (security rules), SecurityCodeScan, Semgrep, OSV-Scanner,
  and PSScriptAnalyzer now check every push, alongside CodeQL.

## [0.221.1] - 2026-11-13

A small security update: three code scanning findings fixed.

### 🔒 Security

- **Code scanning fixes.** The view picker only goes to known pages, remembered filters are
  re-encoded before they're restored, and app codes and recovery codes sign in through separate
  handlers.

## [0.221.0] - 2026-11-11

LarisVMS in nine more languages, Help included, and a big round of security work: two-factor sign-in,
sessions you can see and end, signed updates, and a tamper-evident audit log.

### ➕ Added

- **Help translations in the source.** Translated Help pages can be contributed in
  `src/LarisVMS.Web/Help/<language>/`, next to the English pages (now in `Help/en/`), and ship with
  LarisVMS. A regional language uses its own pages, then the general language's, then English.
  The website's docs offer each translation too, with a language menu. ([#11](https://github.com/billacuda/LarisVMS/issues/11))
- **Help coverage per language.** Settings → Language shows how many Help pages each language has
  translated, and `tools\locale-check.ps1 -Help <folder>` checks translated Help pages: pages not
  translated yet, file names LarisVMS doesn't use, HTML, and headings that don't line up with the
  English page. ([#11](https://github.com/billacuda/LarisVMS/issues/11))
- **More languages.** The interface and every Help page are available in German, French, Italian,
  Portuguese (Brazil), Japanese, Korean, and Chinese (Simplified and Traditional). They are
  machine-translated, so native-speaker review is welcome; Settings → Language marks them.
- **Spanish (Argentina).** The interface and every Help page are available in Spanish (Argentina),
  translated by Luis A. Uriarte ([@luisuriarte](https://github.com/luisuriarte)). ([#11](https://github.com/billacuda/LarisVMS/issues/11))
- **Reset a scene heatmap.** After a camera is moved or re-aimed, **Reset heatmap** on the Activity
  page's Scene view clears its heat and counts only what happens from then on.
- **Encryption on the Nodes page.** A new **Encrypted** column shows 🔒 for nodes with encrypted
  connections from the server, and ⏳ for ones that still need a restart.

### ⚙️ Changed

- **Help in alphabetical order.** Help pages, in the app and on the website, are listed alphabetically
  in your language, after Overview and Installation.
- **Minimum .NET SDK.** Building from source needs the SDK the releases are built with (10.0.402) or
  any newer .NET 10 SDK, set in `global.json`; a newer major version isn't used on its own.

### 🛠️ Fixed

- **Help links in translations.** Links to a section of a Help page ("Learn more" across the app, and
  links between pages) now land on that section when the page is translated, and on the website.
- **Troubleshooting Help names the right tab.** Log level and retention are under Settings → Logs,
  not "Settings → Logging".
- **Activity page translated.** The range buttons, object types, hours, legend, and label counts on
  the Activity page were always in English. They now follow your language.
- **Object names and colors in your language.** Speed badges in Playback ("car (gray) 25 mph") and
  the labels on live-view boxes show the object, its category, and its color in your language.
- **Decimals follow your language.** Sizes and rates on the dashboard, node cards, and exports use
  your language's decimal separator, such as 1,2 GB in German.
- **Old exports disappear with their files.** A node deletes a finished export 7 days after writing it,
  but the Exports page kept offering its Download, which then failed with "not found". Exports are now
  removed from the list once their files are gone. The Download button is also translated.
- **Timeline gestures stay on the timeline.** On a phone or tablet, pinching or dragging a playback
  timeline no longer also zooms or pans the video behind it, or the page.
- **Space before "Learn more".** A setting's hint ran straight into its "Learn more" link.
- **Bundled build note translated.** "Bundled with the web install" on the Nodes page builds list
  now shows in your language.

### 🔒 Security

- **Help pages show HTML as text.** HTML in a Help page, translated or not, is no longer rendered.
- **Snapshots keep privacy masks.** A live snapshot, including the API's, now blacks out the camera's
  privacy areas like its recordings. Only the zone editor gets the whole picture, to draw them, and
  every snapshot is in the audit log. Upgrade nodes too: an older node still sends it unmasked.
- **Live video ends when access does.** Removing someone's role, or their access to a camera, ends
  their relayed live video within a minute. Video through a media proxy, or straight from a node,
  reconnects at least every 20 minutes, which needs access that's still valid.
- **Role changes sign people out.** Someone who loses a role is signed out on their next request,
  instead of keeping it for up to half an hour.
- **Session lifetime limits.** A role's session lifetime is at most 30 days, or 0 to never expire,
  and only a Super Admin can change the Super Admin role's.
- **Strict script policy by default.** The strict script policy is on unless it's turned off, and
  upgrading turns it on, including on a server that had it off. If something stops working, turn it
  off under Settings → Security and check the reports listed there.
- **Pin the server certificate at install.** Settings → Nodes shows the server certificate's
  thumbprint, and the node and proxy installers take it as `-ServerCertThumbprint` (MSI:
  `SERVERCERTTHUMBPRINT`), so a new node checks the server from its first connection instead of
  trusting the first certificate it sees. The Setup and Media proxies install commands include it.
- **Change your own password.** Select your name at the top of the page to change your password. It
  needs the current one, wrong guesses count toward lockout, and your other sessions are signed out.
- **Temporary passwords.** When you create an account or reset a password under Settings → Users,
  **Must change it at next sign-in** makes the person choose their own password before doing
  anything else. Settings → Security → **Temporary admin-set passwords** checks it by default.
- **Easy passwords refused.** Common passwords, runs like `123456789012`, and ones containing the
  person's name or email address are refused. Add more in
  `%ProgramData%\LarisVMS\password-blocklist.txt`, one per line.
- **Two-factor sign-in.** Local and Active Directory accounts can turn on codes from an authenticator
  app (select your name, then Two-factor sign-in), with ten one-time recovery codes. Wrong codes
  count toward lockout, and changing it later takes a current code. Admins can turn it off for
  someone who lost their phone under Settings → Users.
- **Require two-factor sign-in.** Settings → Security can make everyone who signs in with a password
  set it up before doing anything else. Microsoft sign-ins are left to Entra.
- **Sessions.** Select your name, then Sessions, to see every browser you're signed in on and sign
  any of them out. Under Settings → Users, **Sign out** signs someone out everywhere. Changing your
  password, turning on two-factor sign-in, and an admin's password or two-factor reset sign out
  other sessions at once instead of up to 30 minutes later.
- **Confirm your password for admin pages.** With Settings → Security → **Confirm password for
  admin pages** on (off by default), admin pages, and changes through the admin and encryption
  APIs, ask for the password again (and a code, with two-factor sign-in on) unless it was given in
  the last 15 minutes; using them keeps that going. Microsoft sign-ins sign in again.
- **Admins can't hand out more than they have.** Assigning a role (to a person, an API key, or an
  AD group) needs every permission the role grants, not only a wide enough tier. Resetting the
  password of, disabling, or signing out an account that has permissions you don't is refused.
- **Wrong passwords slow the guesser, not the person.** Five wrong passwords for one account from
  one address turn that address away for 15 minutes; the account itself locks only after 20 from
  anywhere, and Super Admins are never locked out at sign-in. A locked account gets the same
  message as a wrong password, so the sign-in page no longer confirms which accounts exist.
  Two-factor codes are limited the same way.
- **Setup token.** From outside the local network, the setup wizard first asks for the token the
  server writes to `%ProgramData%\LarisVMS\setup-token.txt` (administrators only), so nobody on the
  internet can claim a new install. A browser on the server itself or on the local network doesn't
  need it, unless the request came through a proxy; `"Setup": { "AlwaysRequireToken": true }` in
  `appsettings.Production.json` asks every other computer for it.
- **Node and Vision Service check each other.** Their control channel on the node (start and stop
  detection, log level, detection reports) now needs a secret the node makes at start and gives
  only to the Vision Service it launches, not just a connection from the same machine.
- **Idle timeout.** Each role can sign its users out after a number of minutes without opening a
  page (Settings → Roles; off by default).
- **Code scanning fixes.** Live view's object paths can't be fed a track id that changes built-in
  object behavior; saved list filters and the view picker only ever navigate within this site; a
  form field's name can't break the lookup of its error message; an inference service address
  can't add lines to the log; and Active Directory group ids are checked without a regular
  expression that a long value could slow down.
- **Tamper-evident audit log.** Each audit log entry is sealed together with the one before it, using
  a key the database alone doesn't hold. The log is checked every 6 hours and on request (**Check
  now** on the audit log page). An entry changed, removed, or added directly in the database is
  named on the page, and Super Admins are notified. A record kept outside the database
  (`%ProgramData%\LarisVMS\audit-chain.dat`) catches the log being resealed with another key or
  wiped and restarted, and each retention removal is itself an entry. Entries from before this
  update aren't covered.
- **Forward the audit log to syslog.** Settings → Logs can send every new audit log entry to a
  syslog server over UDP, TCP, or TLS, so a copy is kept off the server. Entries wait and are sent
  once an unreachable syslog server is back. Only a Super Admin can change or turn off forwarding,
  and the change itself still goes to the old destination.
- **One-time registration codes.** Nodes → Updates and registration can create a code that
  registers a single node or proxy, then stops working, and expires if unused (after 1 hour, 24
  hours, or 7 days). Installers take it in place of the registration key. Once you use codes, the
  shared key can be turned off.
- **Publisher-signed updates.** Builds can be signed with an offline publisher key
  (`tools\publisher-key.ps1`, then `-PublisherKeyPath`). Nodes and proxies built that way install an
  update only if the publisher signed it as well as the server, so a server that's been broken into
  can't push its own code to them; that includes the CUDA library nodes download. Optional: other
  builds, from source included, are unsigned as before.
- **Pin camera certificates.** Settings → Security can remember each HTTPS camera's certificate the
  first time the server connects, and refuse a different one from then on, on the server and on the
  nodes. Off by default; **Forget** lets a camera present a new certificate.
- **Encrypted connections to nodes.** **Encrypt connections from the server** on a node's page sends
  the server's and media proxies' video and control traffic to that node over HTTPS, accepting only
  the node's own certificate (self-signed is fine), with the media token out of the address. The
  node's plain port then answers only `/health`. Off by default; restart the node once after
  turning it on.
- **Extra protection for businesses.** Protections that add a step are off by default, so a home
  install just works. Settings → Security groups them under **Extra protection**, and the Security
  Help page lists every one, with where to turn it on.
- **Keys stored encrypted stay that way.** The audit log's seal key, the tamper-evidence signing key,
  and the footage master key are refused if found unencrypted in the database, so one planted there
  directly isn't used.
- **Data folder for LarisVMS only.** Each service, at startup, limits `%ProgramData%\LarisVMS` to
  SYSTEM, Administrators, and the accounts the LarisVMS services run as, and makes Administrators its
  owner. Any other access is removed, not just broad groups such as Users, so a local user who created
  the folder before installing, or added themselves to it, can't read the server's keys. The web
  server locks down its key folder before first writing to it.
- **Two-way audio size limit.** One message from the browser is limited to 64 KB, so someone with
  Talk access can't make a node run out of memory.
- **Failover votes from assigned voters only.** A node's health vote counts only from its backup node
  (approved and not disabled) or an enabled proxy assigned to it, and a vote dated in the future is
  ignored, so a newly registered node can't fail other nodes over or keep a dead one up.
- **Active Directory connection for Super Admins.** Changing the domain, how LarisVMS connects to it,
  the lookup account, or local sign-in needs a Super Admin.
- **Password guessing from many networks.** Super Admins are told when one account fails 10 times
  from 3 or more networks, or the server sees 50 failures from 10 or more networks, within 15
  minutes: guessing spread out so no single address reaches the per-address limits.
- **Two-factor codes work once.** A code from an authenticator app can't be used a second time, so
  one someone sees being typed is no use to them.
- **Shared views stay their owner's.** Anyone who sees views can use a shared one, but only its owner
  or an administrator can change, un-share, or delete it.
- **Export size limit.** One export covers up to 64 cameras, 31 days, and 1,000 camera-hours, so a
  single request can't tie up the recorders.
- **Refused camera certificates are reported.** With camera certificate pinning on, a refused
  certificate, on the server or a node, is recorded in the audit log and Super Admins are told.
- **Sign-in guessing limits hold.** Wrong passwords are counted per account however the domain is
  written (`CORP\bob`, `bob`), and per IPv6 network (/64) rather than per address.
- **Language files and Help pages can't add script.** Page-script text with `<`, `>`, or `"` is
  ignored (it shows in English), Help pages keep only web, mail, and in-app links, with no
  `{attribute}` blocks, and links in interface text can't lead to another site.
- **Updates come from your server.** Nodes and proxies download an update only from the server
  they're registered with, and an offer whose version can't be read is refused.
- **Camera stream addresses.** The RTSP address a camera reports is used on the camera's own host
  (keeping its port and path), so a spoofed camera can't send the recorder, with its password,
  somewhere else.
- **Outbound checks behind a proxy.** With a system web proxy, alert, push, and camera requests are
  still checked against where they're really going, and IPv6 addresses that carry an IPv4 one are
  judged by that address.
- **Smaller hardening.**
  - Admins change their own password on the Change password page, not under Settings → Users.
  - Editing or deleting a map needs access to every camera on it.
  - The street map tile server needs the Settings edit permission too.
  - API responses and downloads aren't kept in the browser's cache.
  - Exporting the audit log is recorded.
  - Installers lock the registration key file before writing the key.
  - Unexpected errors show a general message, with details in the server log.
  - Event tag colors must be `#rrggbb`.
  - Notification links stay on this site.
  - Export segment paths with control characters are refused.
  - An `ffmpeg.exe` on PATH in a folder ordinary users can write to isn't used.
  - The MariaDB dump tool for backups must be on this server, in a folder only administrators can
    change; a bare `mariadb-dump.exe` from any PATH folder is no longer run.
  - Someone who must change their password or set up two-factor sign-in first can only load the
    site's own scripts and styles until then, not other files.
  - `tools\export-models\fetch_yolox.py` and `fetch_dfine.py` check each model's SHA-256 before saving it.
  - Signing in takes as long for a name with no account as for a wrong password, so timing doesn't
    reveal which accounts exist.
  - A sign-in link that points off the site (or is malformed) goes to the start page instead of an
    error.
- **Locked package restore.** Release builds restore exactly the packages, and package checksums,
  recorded in the lock files, and stop if anything differs. The lock files no longer change
  depending on how a project was last built.
- **Firebase 13.1.0.** The Firebase files behind browser push notifications are updated from 10.12.2
  to 13.1.0.

## [0.220.0] - 2026-11-06

Record on demand from a live tile or the API, control LarisVMS from your own tools, and use it in your
language, from the keyboard, or with a screen reader.

### ➕ Added

- **Accessibility help.** A new Help page covers keyboard use, screen readers, display settings, and
  known limitations.
- **Manual recording.** A record button on live tiles keeps a camera's footage until someone stops it,
  through restarts and upgrades, whatever the camera's recording mode is. In a view, it can record just
  that camera or all of them. It shows as a band on the timeline, and the color can be changed under
  Settings → Events. A new permission and Record camera access control who can use it, and roles with
  PTZ access get both automatically. Upgrade nodes along with the server: an older node doesn't know
  about manual recordings and can discard that footage. ([#13](https://github.com/billacuda/LarisVMS/issues/13))
- **Manual only recording mode.** A camera can be live view only, keeping footage only during a manual
  recording. ([#13](https://github.com/billacuda/LarisVMS/issues/13))
- **Recording indicators on live tiles.** 🔴 shows when a camera's footage is being kept, for any
  reason, and Ⓜ️ shows when a manual recording is on.
- **More of the API.** API keys can now start and stop recording, move PTZ cameras, read timelines and
  footage lists, add bookmarks, run and download exports, manage evidence locks, and fetch images.
- **Limits on API keys.** A key can be limited to some of its role's permissions, so several keys can
  do different things without a role for each. A key's name and limits can be edited after it's created.
- **API documentation.** A new Help page lists every API command, by category, with examples.
- **Languages.** LarisVMS can be shown in other languages, picked from the 🌐 menu on any page
  (the sign-in page too) and remembered per user. Admins set the default under Settings → Language.
  A translation is one text file: download the English text there, translate it, and upload it or
  drop it into the server's locales folder. It's available straight away, without a restart, and
  anything not translated yet shows in English. Help pages can be translated one at a time. Dates
  and numbers follow the chosen language, and alerts and notifications reach each person in theirs.
  A pseudo-locale and a checker script help translators spot what's missing. Right-to-left
  languages get a mirrored layout, and the installers' text can be translated too.
- **Ignored objects.** Kinds of object can be switched off so they're never detected, boxed, or
  snapshotted, for example a deck the model keeps calling an airplane. Set it for every camera under
  Settings → AI detection, or for one camera on its Edit page. A camera can also turn it off to
  detect everything.
- **One-way speed traps.** A speed trap can time only traffic crossing A then B, or B then A, for
  example one lane of a two-way road.
- **API key expiry.** A new API key can expire after 30, 90, or 365 days, or never. The API keys list
  shows when each one expires.
- **Reverse proxy support.** Behind a reverse proxy, list its address under `ReverseProxy` in
  `appsettings.Production.json` and the server uses the client's real address for allow lists and
  the audit log.

### ⚙️ Changed

- **Breaking:** An IP allow list with entries but no valid address or range now blocks every address
  instead of allowing all of them. A blank list doesn't lock anyone out, so only a list with invalid
  entries is affected. **Double-check your lists for errors or typos before upgrading.**
- **MariaDB.** MariaDB support is no longer experimental.
- **Discarded footage.** Footage a camera wouldn't keep is now deleted 45 seconds after its segment
  ends, so a manual recording started just before can't lose it.
- **Moving points.** On zones, speed traps, and redaction, clicking a point without dragging picks it
  up, and the next click puts it down. Clicking twice draws a redaction rectangle.
- **Idle boxes on live view.** Idle objects are drawn dashed and faded, so moving ones stand out
  when both are shown.
- **AI detection settings.** Aspect fitting, Slice, and GPU frame preprocessing have their own
  "Detection input" section.
- **Accessibility.** Pages have a "Skip to main content" link for keyboard users. Sortable table
  headers can be sorted from the keyboard and announce their order to screen readers. Role headers
  on the Roles page open from the keyboard. Each PTZ button says which way it moves. Primary buttons
  in dark mode are darker, so their text is easier to read.
- **Keyboard access.** Live and Playback tile controls, PTZ, the timeline, and exact-time entry work
  from the keyboard. So do the zone, privacy mask, speed trap, and redaction editors, the Map, and
  Dashboard and view layouts, which also gain buttons to move and resize items without dragging.
- **Color and contrast.** Links, buttons, form fields, selected items, and focus rings are easier to
  see in both themes. The default accent color is slightly darker, and timeline stretches with motion or
  events are striped, so they don't rely on color. Branding warns about hard-to-read colors.
- **Display settings.** The browser's default font size, reduced motion, and Windows contrast themes
  are followed. The Dashboard can pause its updates.

### 🛠️ Fixed

- **Screen readers and forms.** Form labels are tied to their fields, errors are announced, and videos,
  dialogs, and page headings are named.
- **Skip link on some pages.** It went to the Dashboard instead of the page's content, as did other
  in-page links.
- **Evidence locks after an outage restart.** A node starting from its saved settings while the server
  is unreachable now honors evidence locks when discarding footage.
- **Repeated clothing colors on snapshots.** A person who walks through now shows one shirt and one
  pants color, the most confident ones seen, instead of a badge for every time the tracker lost and
  re-found them. Two people show two of each.

### 🔒 Security

- **Camera edit access.** A user who can configure one camera can no longer change, delete, or
  re-probe a different camera, or turn its streams on or off, from that camera's Edit page.
- **Setup wizard stays closed.** The setup wizard no longer reopens when the database can't be
  reached at startup. It shows a "database unavailable" message until the database is back.
- **Camera stream addresses.** Only `rtsp://` and `rtsps://` stream addresses are accepted from a
  camera, so a camera can't point a recorder at local files or internal hosts. Snapshot addresses
  must be `http://` or `https://`.
- **Failed sign-ins audited.** Failed sign-ins and locked-out accounts are now recorded in the
  audit log, for local and Active Directory accounts. Super Admins get a notice when an account is
  locked out, or when one address fails to sign in 10 times within a minute.
- **Refused machine sign-ins logged.** A bad node token, an invalid API key, and a request from
  outside an IP allow list are now logged. An address that keeps sending invalid API keys is slowed
  down.
- **Microsoft sign-in off means off.** Turning off Microsoft (Entra) sign-in now stops linked
  accounts from signing in that way, not just hides the button.
- **Shared views.** Deleting someone else's shared view now needs permission to edit views.
- **Alert and inference addresses.** Webhook, ntfy, Pushover, Slack, and Teams alerts can no longer
  be sent to this server itself or to link-local addresses (where cloud metadata services answer),
  and the inference service check can't reach link-local addresses. Addresses on the local network
  still work.
- **No update downgrades.** Nodes and proxies refuse an update that isn't newer than what they run.
- **Detection model downloads.** Built-in detection models are checked against a known SHA-256 when
  the server downloads them, and a custom download address must use HTTPS.
- **Sessions for users without a role.** These now expire after the default session lifetime
  instead of lasting until the cookie runs out.
- **Plain-HTTP pages.** A plain-HTTP page can no longer use the sign-in cookie for API calls or live
  video on an HTTPS site.
- **Notification devices.** A browser's push registration can only move to another user from that
  same browser.
- **CUDA library signature.** A node installs the CUDA provider library only after it has the
  server's signing key, never unsigned.
- **Camera response limits.** A camera's ONVIF replies and smart-event lines are now capped in size
  on the recorder, so a faulty camera can't exhaust its memory.
- **Audit export.** Cells in the audit log CSV export can no longer run as spreadsheet formulas.
- **Proxy errors.** The proxy no longer includes a recorder node's internal address in error
  responses.
- **Demo key removed.** The demo site no longer ships a TLS private key.
- **Camera edit check, repeated fields.** Sending a camera's id twice in a form can no longer skip
  the check that the user may configure that camera.
- **Super Admin.** Only a Super Admin can make someone a Super Admin or take it away.
- **Redacted exports.** An export whose redaction areas can't be read now fails instead of going out
  with nothing blacked out.
- **Camera lookups.** Camera pages can no longer be used to reach cloud metadata services.
- **Update downloads.** Node and proxy update downloads always check the server's certificate.
- **More audited.** Refused Microsoft sign-ins, map edits, camera placements, and map tile settings
  are now recorded in the audit log.
- **Log lines.** A browser can no longer write arbitrary text into the media timing log.

## [0.219.0] - 2026-10-27

### ➕ Added

- **Path dissolve time.** Settings → Live view sets how long a travel path takes to fade away
  once its linger time is up. The default is now 3 seconds, up from 1.5.
- **MariaDB support (experimental).** MariaDB 10.11 and later are now supported: the setup wizard
  can use MariaDB instead of SQL Server, with a MariaDB user and password. Backups use
  `mariadb-dump`. This is new, so for an installation that needs to be stable, use SQL Server for
  now, and please report any issues.

### 🔒 Security

- **Database password encrypted.** A SQL login's password is no longer kept in plain text in
  `setup-generated.json`. It's encrypted for this machine, and an existing one is moved over at the
  next start.

### ⚙️ Changed

- **Removing an evidence lock.** The reason now goes in a pop-up box instead of a dropdown squeezed
  into the lock's row.

## [0.218.0] - 2026-10-19

### ➕ Added

- **Scene heatmaps.** Activity → Scene lays a heat layer over a camera's picture showing where
  objects moved, or where grid motion happened, from red for the busiest spots to blue for the
  quietest. Narrow it by object type, range, and time of day, show one camera or every camera in a
  view, and use a sharp frame from the camera or upload your own picture. Paths are kept with their
  events, and the heat as long as the activity history.
- **Speed and color switches.** The Live toolbar can hide each box's speed and color, like
  Confidence.
- **AI backend warning.** The Nodes page shows when a node's AI detection is running on a slower
  backend than the one asked for (DirectML instead of CUDA, say), with the reason, and Super Admins
  get a notification.
- **Moving objects.** Settings → AI detection chooses which kinds of object can count as moving:
  people, vehicles, and animals by default, plus any other objects you opt in to, such as packages.
  Anything else is only ever idle, so it never starts a snapshot or alert.
- **Travel paths.** A Path switch on the Live toolbar draws the line each moving object has taken,
  from the centre of its box, in the box's color and line width. The line stays while the
  object moves, lingers once it stops or leaves (Settings → Live view sets how long), then dissolves
  from its start to its last position.
- **Camera device settings.** A camera's Device settings page changes its stream settings (codec,
  resolution, frame rate, bitrate, CBR/VBR, quality, and keyframe interval), image settings (brightness,
  contrast, day/night, WDR, white balance, and more), and audio settings over ONVIF, without signing in
  to the camera. Only the settings the camera supports are shown, and recording picks up a new stream
  format within seconds.
- **Bulk device settings.** The same change can be applied to several cameras of the same make and
  model at once, from the camera list or from one camera's Device settings page. Only the changed
  settings are sent, and each camera reports whether it was applied, skipped, or failed.
- **Camera list filters.** Every column on the Cameras page can be filtered, and Make and Model are
  now separate columns. Filters are saved per user, like the column choices, and Select all only
  selects the cameras that match.
- **Evidence locks.** Lock footage from Playback for 1, 3, 7, 14, or 30 days, until a date, or
  indefinitely, with a reason. Retention, quotas, archive expiry, and even a full disk never delete
  locked footage; locks over 30 days warn about storage and offer to export instead. The person who
  placed a lock, or a Super Admin, can remove it, and the footage stays protected for 24 more hours
  in case that was a mistake. Every step is in the audit log.
- **Notifications you subscribe to.** Under My notifications, people subscribe to shared alert rules
  an admin opens up, or make their own (for example, animals on one camera between 10 pm and 6 am),
  scoped to a camera, a node, or every camera, and can mute them. Each kind of notification can be
  turned off for users in Settings.
- **Motion alerts.** Alert rules and personal notifications can fire on motion, on the whole camera
  or only in one of its server motion zones, with the same cameras, nodes, and schedule choices as
  detection alerts.
- **Browser notifications.** Notifications reach the browser while LarisVMS is open (fully
  on-premises), or with no LarisVMS page open, through standard Web Push or your own Firebase
  project; on a computer the browser itself has to be running. Email works too.

### 🛠️ Fixed

- **Duplicate segment errors in the log.** Footage being moved to the archive was reported again
  as new footage, which could also drop other new footage reported with it until the next hourly
  check.
- **Objects counted twice after passing behind something.** An object that disappears behind a tree
  or post and comes out further along the same line, with the same color, is now the same object.
- **Pages kept old scripts after an update.** Activity, the dashboard, Snapshots, Map, Exports, and
  some settings pages needed a hard refresh to pick up a new version; their scripts are now
  version-stamped.
- **Node installer removed the CUDA libraries.** Installing over a script-installed node, or
  upgrading, deleted the CUDA and cuDNN files copied next to the AI backend, and the installer then
  looked for them in the wrong Program Files folder. AI detection silently fell back to DirectML,
  several times slower. The installer now keeps them and finds the machine's own copies, and the
  node puts them back on its own when they go missing.
- **Parked vehicles showed a speed.** A parked car's box that jumped, cut at a slice seam or matched
  to the car beside it, could cross both speed-trap lines, and a speed reading alone made it count as
  moving. A reading now only counts for an object that is actually moving.
- **Fast objects weren't tracked.** A person or car that moved further than its own width between
  analysed frames never got a track, so no box showed at all. The tracker now follows it from its
  second frame.
- **Camera Access couldn't grant a single camera or a site.** Choosing "A single camera" saved no
  camera, so it was refused with "Choose a camera.", and Group-tier roles (such as Guest/Viewer) were
  refused every top-level camera group. Any role can now be granted any camera group or single
  cameras; only "every camera" still needs an Org-tier role.
- **More times showed in the server's time zone.** Times on the camera list, nodes, node builds,
  exports, bookmarks, snapshots, audit log, backups, alerts, API keys, encryption, and Active
  Directory pages were formatted on the server. They're now shown in your local time.
- **Alert schedules used the server's time zone.** A rule's active hours are now read in the time
  zone of whoever saved it.
- **An "any camera" detection alert went quiet after one camera fired.** The cooldown now counts
  separately for each camera.
- **Motion detection kept watching an old Sub stream.** Server motion detection now restarts on the
  new stream when a re-probe changes a camera's Sub stream address.
- **Pages were slow to open.** With several LarisVMS tabs open, clicking around could stall before
  a page started loading.

### 🔒 Security

- **Setup wizard closed after the admin is created.** The remaining setup steps now need a signed-in
  Super Admin, and the database can no longer be changed from setup once an admin exists.
- **Live-view tokens only open live view.** Two-way audio, snapshots, and the motion zone overlay on a
  node each need their own token, so a live-view token can't be reused for them. Export tokens are
  single-use.
- **Camera list changes need edit rights.** Enabling, disabling, re-probing, and moving cameras to a
  node need Cameras.Edit and configuration access to each camera.
- **Admins can't take over higher-level accounts.** Resetting a password, disabling an account, and
  changing roles are refused for a Super Admin (unless you are one) or an account with wider access
  than yours, and nobody changes their own roles or disables themselves. A role you can't assign,
  you can't remove.
- **Exports are limited to cameras you can export.** The Exports list shows your own exports and
  exports of cameras you can export; downloads and manifests need export access to the camera; only
  the person who made an export, or a Super Admin, can delete or retry it.
- **Camera groups can't widen access.** Moving a camera into or out of a group needs configuration
  access to that camera.
- **Camera access fails closed.** A role or user with no camera access grants now has no cameras
  (Super Admin always has every camera). Upgrading adds grants that keep everyone's current access
  the same; new installs give Guest/Viewer view-only access, and Guest/Viewer no longer sees the
  audit log. Role changes apply to camera access straight away.
- **New nodes and proxies need approval.** A newly registered node waits on the Nodes page until an
  admin approves it, and a new proxy starts turned off; until then they only check in. Nodes can only
  report recordings, motion, and events for their own cameras, a node that has used the check-in
  nonce can no longer leave it out, and a node's address only changes on a verified check-in (each
  change is audited). Node and proxy names must be hostnames, and node names in prompts can no longer
  inject script.
- **Local data locked down.** The web server, nodes, and proxies limit `%ProgramData%\LarisVMS`
  (keys, node settings, certificates, update downloads) to SYSTEM, Administrators, and the accounts
  the LarisVMS services on that machine run as, and the setup and settings files next to the web server to the same. Each update
  downloads into a fresh folder, so nothing placed there beforehand gets installed.
- **Signed updates.** The server signs node and proxy updates and the CUDA library it hands out
  with its signing key. Nodes and proxies keep that key from registration (or their first signed
  update) and refuse anything unsigned or signed with another key.
- **Web Push can't reach the server's own network.** A browser's push address must belong to a
  browser maker's push service, the server only connects to public addresses for it, error replies
  aren't passed back, and each person keeps at most 10 registered browsers.
- **Camera settings need access to that camera.** Zones, privacy, speed traps, the motion region,
  event tags, schedules, map placements, and the camera's edit and device settings pages need
  configuration access to the camera, not just Cameras.Edit. Moving a camera to a different host
  needs its saved password typed again, so the address can't be pointed somewhere else to collect it.
- **Lists only show cameras you can use.** Snapshots, bookmarks (and the Playback timeline's
  bookmark marks), the view editor's camera list, and floor plan images follow camera access. Only the
  person who made a bookmark, or a Super Admin, can delete it. The dashboard's data needs Dashboard.View;
  without it the home page shows a short welcome instead.
- **Microsoft sign-in only for linked accounts.** Entra ID sign-in no longer links accounts by email
  address. An admin links each account to the person's Entra object ID under **Microsoft sign-in** on
  the Users page, and their first sign-in completes it; accounts already linked keep working. The
  tenant must be your own directory, not a multi-tenant value.
- **Admin tools can't be used to climb.** Unless you're a Super Admin, you can't change a role you
  hold (its permissions, reach, or camera access), give a role wider reach than your own or a
  permission you don't have, or bind the Super Admin role, or a role you couldn't assign, to an API
  key or an AD group.
- **Unused sign-in pages closed.** The built-in pages LarisVMS doesn't use (forgot and reset password,
  registration confirmation, which told anyone whether an email had an account, and the account
  management pages) now go to the sign-in page.
- **Limits against abuse.** Sign-in and node or proxy registration attempts are limited per address,
  and test notifications and new exports per person. Requests are capped at 10 MB (more only where
  needed: floor plans and node reports), each person keeps at most 10 open notification streams
  (opening another closes the oldest) and 500 saved preferences, and the Active Directory sign-in no longer tells a wrong password apart from
  an account without access.
- **Only valid hosts in the page's security policy.** Proxy and node client endpoint hosts must be
  hostnames or IP addresses, and only enabled proxies are allowed in the Content-Security-Policy, so a
  bad entry can't widen it or break every page.
- **Cross-site requests refused.** Live video, talk, and overlay connections, and every change made
  through the web API, must come from this site's own pages (or the configured public origin), so
  another site can't use a signed-in browser to act on LarisVMS.
- **Direct streaming follows the live view rules.** Getting a direct-to-node live ticket is held to the
  Live View IP allow list, and a browser's direct connection to a node lasts at most 20 minutes before
  the player reconnects with a new ticket, so a disabled account can't keep watching.
- **Dependencies brought up to date.** ASP.NET Core, Entity Framework Core, and the other .NET packages
  are on the latest servicing release, builds use the newest .NET SDK installed (10.0.400 at least), and building the installers stops if
  any package has a known High or Critical vulnerability. The Firebase scripts are served by
  LarisVMS instead of being loaded from Google at run time.
- **Encrypted connections stay encrypted.** Email sign-in now requires TLS (port 465, or STARTTLS
  required instead of only used when offered). A new install checks a remote SQL Server's certificate
  unless you tick **Trust this server's certificate** (a SQL Server on the same machine is always
  trusted), and the install scripts keep the connection's encryption. Only a Super Admin can change
  the backup folder; `Backups:AllowedRoots` in appsettings can limit it to chosen folders or shares;
  and backups can be encrypted with a SQL Server certificate.
- **Nodes check the server's certificate.** Nodes and proxies no longer accept any certificate.
  Under **Nodes → Server certificate check** an admin chooses Trust on first use (the default:
  trusted CA certificates always work, and a self-signed one is remembered and keeps working even
  past its expiry), Trusted certificate only, or Don't check. The server's self-signed certificate is
  no longer replaced on its own; replacing it is an explicit admin action, the Nodes page warns that
  nodes then need re-registering and lists the ones that haven't checked in since, and
  `--reset-server-pin` re-trusts on the node itself.
- **Smaller hardening.** Every page and endpoint without its own permission now needs a signed-in user.
  Static files get the security headers too, the `Server` header is gone, and pages only load
  images from this site and the map tile server. Errors reaching a recorder node no longer show its
  internal address. XML from cameras can't use entity tricks, rule and camera names can't inject
  mentions or links into Slack and Teams messages, and browser reports can't forge log lines.
  Passwords need at least 12 characters (for new and changed passwords). A role's session limit now
  counts from sign-in instead of resetting as the session is used. Removing or restoring an evidence
  lock also needs permission to lock footage and playback access to the camera.
- **More checks.** Each person can hold at most 200 live video, talk, and overlay connections through
  the server. Active Directory sign-in confirms the account it actually signed in as is the one typed,
  so an enabled domain Guest account can't stand in for it. Installers no longer put the
  registration key on the node or proxy service's command line (readable by every local user): it goes
  in a locked-down file that the service deletes once it has registered. Super Admins get a
  notification when the backup folder or its encryption changes, and when a new node or proxy
  registers and waits for approval.
- **Strict script policy.** Pages no longer use inline event handlers, and each page's own scripts
  carry a per-request nonce. Settings → Security → Strict script policy makes browsers run only those
  scripts, so script injected into a page can't run. Until it's turned on, browsers report what it
  would block, and the latest reports are listed under the switch.
- **Last loose ends.** A self-signed certificate's file now has its own random password, protected
  so a copy taken off the machine doesn't give away its key; existing files keep the same
  certificate. A node only serves a recording outside its storage folders from a local drive, never
  from a network path. Notifications about a node are only listed, subscribed to, and delivered for
  people who can still see that node. The node installer can limit its live port to the server and
  proxies (`LIVEALLOWFROM`, or `-LiveAllowFrom`).

### ⚙️ Changed

- **Faster database.** New indexes for the busiest node and Live queries, nightly index and
  statistics upkeep, and old raw camera events are cleared after 30 days (adjustable under
  Settings → Logging).
- **Movement detection.** Whether a tracked object is moving is now judged from the shape of its
  recent path rather than how far its centre went in 2 seconds. Anything that moves steadily counts
  within a few frames, from a car crossing the view to a truck pulling out, because each object's
  steps are compared with its own box wobble rather than with its size. Objects entering at
  the edge of the picture or from behind something are judged by the side in view. Box wobble, and a
  box that only changes shape (a potted plant, or a parked car as something passes in front of it),
  doesn't count. Movement sensitivity (Low,
  Normal, or High) tunes it per camera, and Movement detection → Legacy keeps the previous rule.
- **HTTP/2 and HTTP/3.** The web server uses HTTP/2, so a browser's requests share one connection,
  and adds HTTP/3 on Windows 11, Windows Server 2022, and later. HTTP/3 can be turned off under
  Settings → Security. The installer also opens the HTTPS port for UDP.
- **Export camera picker.** Cameras to export are chosen from a dropdown list instead of a row of
  checkboxes, the same as for evidence locks.
- **Help and on-screen wording.** Lists in the Help pages and on-screen text consistently use the
  serial comma.

## [0.217.0] - 2026-10-15

### ➕ Added

- **Customizable dashboard.** The dashboard is now made of widgets that each user can add, remove, move
  and resize; the layout is saved per user. New top-10 widgets cover cameras by storage used, frame
  rate, bandwidth, resolution, oldest footage, time offline, time since last recording (flagging
  Continuous cameras that have stopped), reconnects, quota use, AI detection problems, and motion,
  human, vehicle, animal and object events. Node widgets cover camera count, offline cameras, storage
  used, frame rate, bandwidth, CPU, least storage time left and health issues; nodes with no cameras
  are left out where their numbers don't mean anything. Recent alerts and latest detections are also
  available; latest detections has a ⚙ button to choose which detection types it shows, including
  plain motion, and new snapshots slide in without the strip redrawing. Clicking a widget's title opens
  a full report with filtering, sorting, 100 rows per page and CSV download.

- **Two-way audio.** Hold the 🎙️ button on a live tile to talk through the camera's speaker, using
  any microphone the browser can access (chosen per user). Works with cameras that support the ONVIF
  audio backchannel, or Dahua-family cameras through their own audio API when the ONVIF one isn't
  available; one person talks through a camera at a time; needs the camera's Talk permission and HTTPS.
  Both directions are recorded: each press is saved next to the footage, marked on the playback
  timeline, played back in sync with the video, and mixed into exports. Talk sessions are audited.

- **Activity heatmaps.** A new Activity page shows when things happen on a camera, or on all cameras
  combined, for ranges from 24 hours to a year. "Typical week" shows each weekday and hour across the
  range (with per-day averages on hover); "By day" lays out every date with the hours top to bottom.
  The busiest day and hour are called out. Filter to event types or specific labels (Animal → cat); the
  choice is remembered across cameras. Activity history is kept for 400 days, well beyond footage
  retention, and finished hours are stored once so long ranges load quickly. A Camera activity
  dashboard widget shows the last 7 days.

- **Privacy masks.** Privacy zones black out an area in recordings, live view (both streams),
  snapshots and exports, following the shape you draw rather than a box around it. Events behind a mask
  are still recorded (with masked snapshots) unless the camera's "Also ignore detections inside privacy
  masks" option is turned on. They're drawn under a new 🔒 Privacy masks view on the zones page, which
  works whichever motion method the camera uses. A masked camera keeps its own codec (H.264 or H.265)
  and is re-encoded at the camera's own bitrate, so quality and storage use stay the same.
- **Export redaction.** When exporting from Playback, ✏ Redact next to a camera blacks out rectangles or
  polygons drawn over a frame of that camera, in its export only. Redacted exports are marked on the
  Exports page and in the audit log.
- **Easier zone editing.** Drag a zone's corners to reshape it, right-click to undo the last point while
  drawing, and hover a zone in the list to highlight it on the picture.
- **Camera map.** A new Map page shows cameras on a street map (OpenStreetMap, or your own tile server)
  or on uploaded floorplans, each with a cone for where it points, how wide it sees and how far, coloured
  by whether it's recording, offline or disabled. Click a camera for its latest picture and links to
  Live and Playback. People who can edit cameras drag them onto the map, then move and aim them.

- **Object colors.** AI detection names each object's color, and a person's shirt and pants colors.
  Snapshots show badges painted in the detected color, and the Snapshots filter has Color, Shirt and
  Pants sections. No colors are recorded while a camera is in black-and-white night mode.

- **Speed traps.** Draw two lines across a road on a camera's new Speed traps page and enter the
  distance between them; objects are timed between the lines in either direction. Speeds show on live
  boxes and snapshots, in km/h or mph.

- **Footage encryption.** Recordings (and optionally snapshots and thumbnails) can be encrypted on the
  recorders' disks, per camera. Settings → Encryption and signing creates the master key and requires a
  passphrase-protected backup before it can be used; the backup restores it on a rebuilt server.
  Playback, snapshots and exports work as before, and existing footage can be encrypted on request.

- **Tamper evidence.** Every recorded segment is fingerprinted as it's written and linked into a
  per-camera chain that the server signs hourly, so altered, removed or reordered footage shows. Each
  export gets a signed manifest, and the new Verify an export page checks an exported video against
  it in the browser; the signature can also be checked with openssl.

- **Speed log.** A new dashboard widget lists the latest speed-trap measurements; its full report has
  time, camera, trap, object, color and speed columns, with sorting, filtering and CSV download.

- **Speeds in Playback.** Each measurement is marked on the timeline (red when over the trap's limit,
  with the speed on hover), and the speed shows over the video while the object is on screen.

- **Snapshots speed filter.** Show only snapshots with an object at or over a chosen speed, alone or
  together with a color.

- **Speed heatmap.** The Activity page's new Speeds view shows average speed, top speed, number over
  the limit or number measured by day and hour, per speed trap. Speed traps can now have a speed limit.

- **Detection alerts.** Alert rules can fire when an object is detected, or when one passes a speed
  trap over a limit, filtered by camera, category, label, color, zone, and active days and hours. They
  fire within seconds of the detection.

### 🛠️ Fixed

- **The map forgot its view.** It now reopens where you left it, and Done editing saves where it
  opens for everyone else, floorplans included.
- **Speed traps missed some vehicles.** An object that crossed both lines before it was recognised as
  moving lost its speed. Crossing a speed trap now counts as moving, so the speed and the object are
  always recorded.
- **Object colors picked up the background.** The scene behind an object (a lawn, the road) could
  outweigh the object itself, so a brown dog on grass read as green. Colors around the box are now
  sampled and set aside.
- **Leftover footage on nodes without cameras.** A node with no cameras now deletes its expired footage from old storage locations, so its leftover-footage warning clears.
- **H.265 cameras with a privacy mask kept reconnecting in live view.** Live view and playback now
  use the codec the video actually arrives in, rather than assuming the camera's.

- **Going back to a page after saving it showed ERR_CACHE_MISS.** After saving a settings page (or any
  form), leaving it and pressing Back now just reloads the page instead of showing an error.

- **The alert editor hid the camera and node pickers.** Choosing a condition now shows the fields it
  needs.

- **Privacy masks broke recording, so they'd been switched off.** A camera with a Privacy zone never
  produced footage and kept reconnecting: the masked video was written without the headers its files
  need. That's fixed, and privacy masks work again (see Added).
- **Pages sometimes didn't load properly until refreshed.** After clicking a link, some pages (the
  dashboard layout, Activity, Live and Views among them) came up half-built because their scripts
  didn't run. Every link is now a normal page load.
- **Some times showed in UTC instead of local time.** Times that reached the browser without a time
  zone were read as local clock readings, so they showed hours off (Latest detections on the dashboard
  among them). Every time from the server is now marked as UTC and shown in your local time, including
  the Alerts, API keys and role-expiry times that were printed in UTC.
- **Leftover footage on a node never cleared.** Footage left on a node by a camera that moved to
  another node (or under an old storage path) was never deleted when that camera had archiving turned
  on, so the node's warning stayed overdue indefinitely. It's now removed once it's past the archive
  retention.
- **Cameras with a speaker weren't detected as supporting two-way audio.** Re-probing now finds the
  audio output where ONVIF cameras actually report it. Re-probe the camera to pick it up.
- **Cameras with G.711 audio wouldn't play.** Browsers can't play G.711 (or other non-AAC) audio in
  live view or playback, so those cameras' tiles failed to load. Their audio is now converted to AAC
  as it's recorded; video is still copied untouched. Footage recorded before this keeps its original
  audio.

### 🔒 Security

- **Updated a vulnerable cryptography package.** System.Security.Cryptography.Pkcs, used for email, is
  updated to 10.0.13 for two security advisories.

### ⚙️ Changed

- **One Nodes page.** Node builds and node settings are merged into Nodes: the current build and any waiting for approval, a build history pop-up, and auto-update and registration-key settings. Nodes moved from Settings to the sidebar, and the Settings page no longer repeats Cameras. Old links redirect.
- **Encryption and signing settings.** The Encryption settings page is on the Settings home, and it also shows the signing key with a public key download.
- **Dashboard thumbnails hidden.** The camera table's thumbnail option is turned off for now.

## [0.216.0] - 2026-10-09

### ➕ Added

- **Active Directory sign-in and group sync.** Domain users sign in with their AD username (no
  domain or @); local email accounts keep working and can be turned off. AD groups, including nested
  ones, are linked to roles under Settings → Active Directory, with type-ahead group search. Members
  are created and synced immediately, then every 30 minutes (5 minutes to 24 hours). Groups and users
  are matched by SID, so renames carry over. Anyone disabled in AD or removed from every linked group
  is disabled and signed out. Uses LDAPS by default, with integrated security or a service account.

### 🛠️ Fixed

- **Disabling a user didn't end their sessions.** They stayed signed in, and their live video kept
  playing, until the session expired. They're now signed out on their next request and their live
  streams close.

## [0.215.1] - 2026-10-08

### 🛠️ Fixed

- **Live view and playback stayed on "connecting" after installing a node with the MSI.** Taking over
  or upgrading an install removed the firewall rule for its direct-streaming endpoint and only made a
  new one when the port was given again, so browsers couldn't reach the node. The Node and Proxy
  installers now read the existing endpoint's port and settings and keep the rule; a proxy also no
  longer falls back to the default port.

## [0.215.0] - 2026-10-07

### ➕ Added

- **New-version notice.** Once a day, and when the service starts, the server checks LarisVMS's
  GitHub releases; when a newer version is out, people who can change settings see "✨ Version X is
  available" in the top bar, linking to the release. On by default; turn it off under Settings →
  Node defaults → New versions.

### ⚙️ Changed

- **The sidebar and top bar stay in place.** Only the page content scrolls, so the logo, menu, theme
  button and account stay on screen.
- **Page titles are in the top bar.** Each page's title now shows in the top bar instead of at the top
  of the page.
- **ONNX Runtime telemetry is turned off.** The AI detection engine no longer sends usage events
  through Windows' diagnostic data channel.

### 🛠️ Fixed

- **A web font was requested from Google.** Every page tried to load a font from Google Fonts (blocked
  by the site's own security policy). The request is gone; the system font is used as before.

## [0.214.0] - 2026-10-07

### ⚙️ Changed

- **Only the newest node and proxy builds await approval.** Older pending builds, and any not newer
  than what's already approved, move to History as Superseded, including after an approval and for
  builds registered by deploy.ps1.
- **Installer upgrades skip the settings pages.** Upgrading an existing install (MSI or script) offers
  the upgrade directly; a page is shown only if something is missing, such as the service account
  password.
- **Certificates are checked in the installer.** The path and password are checked when you click
  Next, so a wrong password or an unreadable file is reported there instead of when the service starts.
- **The Web installer reads an existing configuration.** An existing appsettings.Production.json fills
  in the port and certificate path. A saved certificate password is kept (the field stays blank, with
  a note) unless a new one is entered.
- **The setup wizard asks for the database.** A new install no longer writes the example's
  local-server connection string.
- **The setup wizard reuses an existing database.** Pointing it at an existing, set-up LarisVMS
  database uses it as it is (upgraded if needed, nothing seeded or overwritten) and skips the
  remaining steps. An existing database that isn't a LarisVMS one is refused.

### 🛠️ Fixed

- **Settings pages didn't save.** Saving Branding, Cameras, Events, Live view, Logs, Nodes, Recording,
  Security, or Storage & retention silently kept the old values.
- **Auto-update could leave a node or proxy stopped.** Windows reports the service stopped before its
  process has let go of the exe, so the updater's swap failed and it gave up. The node and proxy now
  swap in their own binary before stopping and the updater only restarts the service; the updater also
  waits for the process to exit, retries, and restarts on the previous binary if the swap still fails.
  An update that never got applied is reported in the node's or proxy's own log on its next start.
- **A bad certificate crashed the Web service.** A wrong password or path now logs the error and falls
  back to the self-signed certificate.
- **The Web service could time out on start**, leaving the installer stuck on "Starting services",
  when the database was slow or unreachable. Database startup work now runs after the service has
  started, and connection problems are logged.
- **The setup wizard stayed reachable on a set-up site**, e.g. after deploying over an existing
  install, where it could repoint the database or overwrite branding. It now redirects home once setup
  is complete, and existing sites with users are marked complete at startup.
- **A failed install left its config behind.** A failed or cancelled Web install no longer leaves
  appsettings.Production.json (and the certificate password in it); an upgrade's existing file is
  restored.
- **Quoted paths are accepted.** Certificate and storage paths with surrounding quotes (as "Copy as
  path" gives) work.
- **The install scripts reported the wrong service account.** An upgrade without -ServiceCredential
  said "Running as LocalSystem" even when the service runs as a service account (which it keeps); the
  web and node scripts now report the account actually in use.

## [0.213.1] - 2026-10-04

### ⚙️ Changed

- Zones editor: a note that changes can take up to 30 seconds to take effect on the recorder node.

## [0.213.0] - 2026-10-04

### 🛠️ Fixed

- Zones editor: the motion wash trailed moving objects slightly. Motion frames are now stamped with
  their stream arrival time (as live AI frames are), and motion decoding uses low-latency settings.

## [0.212.0] - 2026-10-04

### ➕ Added

- Settings → Events: "Reset all object colors to defaults" button.
- Dashboard: a card per node with CPU, memory and network load, total recording fps, and (with object
  detection on) events and human/vehicle/animal counts for the last hour, 24 hours, 7 days and 30 days.
- Live and View tiles: drag a box to zoom into it, the same as Playback. While zoomed, a reset button
  shows in the tile's lower right, and right-clicking the video goes back to 1×.

### ⚙️ Changed

- Playback "Now" jumps to the end of the newest recorded footage instead of the current time, which
  usually had nothing playable yet.

- Live view: a moving object keeps its AI box and badge through pauses, until it has been still for
  the detection idle timeout, instead of blinking off whenever it stops.
- Vision Service: cameras running the same model variant can share ONNX Runtime sessions, up to
  `Vision:MaxCamerasPerSession` cameras per session (default 1, no sharing). Raising it trades
  per-frame inference latency for lower memory use.

### 🛠️ Fixed

- Playback drag-to-zoom: after zooming back out (or leaving fullscreen), dragging panned instead of
  selecting a new area. Tiles now keep a single zoom state, and panning stays within the frame.
- Live AI box labels running off the right edge of the video or overlapping each other.
- Zones editor: the motion wash on grid cells and polygon zones ran ahead of the live video. It is now
  held back to line up with the video, the same way live AI boxes are.
- Footage outliving its retention when it was outside a node's current storage folders (a storage
  path that changed, or a camera that moved to another node). The server now lists overdue footage
  to the node that owns it, wherever it is, and the record is removed only once the node confirms the
  file is gone. Nodes also remember unreported deletions across restarts, and storage-pressure cleanup
  removes leftover footage from moved cameras before an active camera's.
- Admin → Nodes: leftover-footage warnings show keep-forever and archive retention correctly, flag
  footage the node hasn't confirmed deleting, ignore a failover backup's own recordings, and can
  forget records left on a deleted node.
- Settings → Events: a color's Reset button now counts as an unsaved change.
- GPU-batched slice mode rejected for generic ONNX models whose outputs are in pixel units or
  sorted by score, over sub-pixel rounding differences. Its self-check now compares each tile's
  detections as a set, and falls back to per-tile inference whenever it can't prove each slot carries
  its own tile. A batched graph that fails is not rebuilt for other cameras on the same slice layout.
- Live view, playback, snapshots, exports and node control calls failing ("Invalid URI: The hostname
  could not be parsed") for nodes and media proxies that check in over IPv6: their address is now
  stored bracketed.
- Live AI boxes trailing moving objects: vision frames are stamped with their stream arrival time
  rather than when decoding finished, and the Web relay's own handling time is counted. The vision
  cadence log line now reports the decode delay this removes.
- Vision Service GC churn for generic ONNX models on the CPU-per-tile slice path: each tile is now
  preprocessed straight from the frame instead of through two new large buffers per tile per frame.
- Fewer vision frame drops: the Vision Service no longer copies model outputs every frame. Frames
  published while the detection engine is still building no longer count as drops. The cadence log
  line adds GC pause time and memory load.
- Live tiles: object badges no longer cover the Motion badge. Motion comes first, the object badges
  follow it on the same row, and they wrap only when they would run off the tile.

## [0.211.0] - 2026-10-02

### ➕ Added

- Installable as an app on phones: web app manifest (named after Branding), app icons, favicon and a
  minimal service worker (no caching).

### ⚙️ Changed

- Phone layout: the sidebar is an overlay drawer below desktop width instead of squeezing the page,
  shell padding is tighter, the topbar username is hidden on small screens, page headers and toolbars
  wrap, and long tab rows scroll sideways.

## [0.210.0] - 2026-10-02

### ➕ Added

- MSI installers for the web server, recorder node and media proxy (`build-installers.ps1`). They take
  the same settings as the install scripts on the command line, prompt for anything missing, remember
  settings for upgrades, and take over script-installed machines in place.

### ⚙️ Changed

- The web server applies database migrations itself on startup; upgrades no longer need the repo.
- The web server is published self-contained, so no ASP.NET Core runtime is needed.
- The web server registers the node and proxy builds bundled with it as Pending on startup, replacing
  install-web.ps1's direct database writes.
- Setup wizard: the database step explains that SQL Server Express needs `.\SQLEXPRESS` as the server
  name, both as a hint and in the error when the server can't be found.

### 🛠️ Fixed

- Fresh installs: the setup wizard failed with "too many redirects", then an error on every page,
  because settings and Entra sign-in were read from a database that didn't exist yet.
- Fresh installs: creating the admin account failed with "Role SUPER ADMIN does not exist", and left a
  half-created account that locked the wizard. Roles are now seeded before the admin is created, and
  a failed role assignment removes the account.
- Live view stayed on "Reconnecting" for a node on the same machine as the web server that connects
  through `localhost` (IPv6 `::1`).
- Live AI boxes lagged moving objects by ~700 ms: detection frames were decoded frame-threaded
  (~600 ms) and rate-limited with a look-ahead filter (~80 ms), and each frame was timestamped after
  both. Decoding is now low-latency and the rate limit keeps frames as they arrive.
- Live AI boxes moved in steps two or three times a second instead of smoothly; they now follow
  objects at the display's frame rate.
- Choosing DirectML as a node's AI accelerator turned AI detection off on NVIDIA and Intel GPUs.
- In-progress motion in Grid mode was rejected by the server and only appeared on the timeline once it
  ended.

## [0.209.0] - 2026-10-01

### ➕ Added

- Help section (left nav, below Settings): documentation for every part of the system, split into
  topics, with "Learn more" links from settings pages.
- Node edit page (Settings → Nodes → a node). All node settings, grouped into sections, replace the
  inline table editing.

### ⚙️ Changed

- UI cleanup. Settings, node and camera pages use grouped sections with a one-line hint per field
  instead of ℹ️ popovers; long explanations moved to Help. Fields that don't apply to the current
  choice are hidden, Save stays visible at the bottom, and leaving with unsaved changes asks first.
- Nodes list shows only the key columns; node actions moved to a ⋯ menu that floats over the table.
- Camera edit page grouped into General, Connection, Recording, Storage, AI detection and Motion;
  capabilities and streams are under a collapsible Device panel.
- Snapshots: the play button sits on the camera name/time row instead of its own footer.
- Max detection frame rate defaults to 7 fps (was 10) on new installs; existing installs keep 10.
- Settings tiles are alphabetical; added a Camera settings tile and renamed the duplicate-sounding
  ones (Logging, Node defaults, Recording defaults, Security).

### 🛠️ Fixed

- Dashboard's Nodes online only counted nodes that had cameras the user could see. It now counts
  every registered node.
- Settings → Email showed none of the provider fields (SMTP, Graph, Gmail).
- Blazor pages could keep using stale CSS/JS after an update; asset URLs are now versioned.
- Capability badges on the camera page ignored dark mode.

## [0.208.0] - 2026-09-24

### 🛠️ Fixed

- AI detection stayed dead until a manual node restart after a GPU driver reset. The vision process
  kept running with every camera's inference failing. It now exits after 60s with no successful
  inference on any camera while frames keep arriving, and the node restarts it.
- A camera whose inference failed on every frame stopped logging its detection cadence line, so
  the failure count that line is meant to carry never appeared. It now logs every 30s regardless.

## [0.207.0] - 2026-09-22

### 🛠️ Fixed

- Live view could freeze for tens of seconds and reconnect every tile at once. Caused by node-side
  thread-pool starvation; mitigated by raising the pool's minimum thread count.
- AI detection boxes led or lagged the object they marked. Boxes are now scheduled against a
  measured video latency instead of a fixed delay, and interpolated between detection ticks so
  motion is smooth instead of stepped.
- A stalled live tile could roll back several minutes of buffer and force a full reconnect.
  Old buffered video is now trimmed as playback advances.
- Saves on Admin → Settings → Detection and Admin → Settings → Email silently did nothing after
  the Blazor rewrite of those pages — the page reloaded stored values over the submitted ones
  before the save ran. Fixed; audited the rest of that migration and found no other pages affected.

### ➕ Added

- Live-view health telemetry (stream stalls, catch-ups, decode throughput, resyncs, box alignment)
  logged server-side instead of only to the browser console.
- A node process health heartbeat that flags when it stalls and whether GC or thread starvation is
  the cause.
- Live view now recovers automatically from an unexpected playback pause instead of waiting to
  drift out of sync.

## [0.206.0] - 2026-09-18

### ⚙️ Changed

- Migrated the remaining multi-handler-form pages (Cameras/Index, Cameras/Groups, Logs/AuditLogs,
  Admin/Settings/Email, Admin/Settings/Detection, Admin/Nodes, Permissions) from Razor Pages to
  Blazor.

### 🛠️ Fixed

- Snapshots' and Audit Logs' pagination links could land on the Dashboard instead of the current
  page.
- Snapshots' filter checkboxes stopped surviving a refresh or new login after the move to Blazor's
  client-side navigation.
- Blazor forms across the site returned errors after the migration: duplicate antiforgery tokens,
  forms sharing one name across a list of rows, and dropdowns resetting after save. All fixed.

## [0.205.0] - 2026-09-15

### ➕ Added

- New Settings hub (`Admin → Settings`) replacing the old Admin nav dropdown and top-level buttons.

### ⚙️ Changed

- Reworked the web UI to a sidebar + topbar shell with new colors/typography over Bootstrap.

### 🛠️ Fixed

- Several dark-mode/theme bugs: sidebar accent color override, primary-button hover direction,
  missing RGB theme variables, capability badges, Setup wizard dark mode, and fullscreen kiosk mode
  targeting the old navbar.

## [0.204.0] - 2026-09-15

### ⚙️ Changed

- Web no longer requires IIS — self-hosts Kestrel directly as its own Windows Service, the same way
  the recorder node does. New `install-web.ps1` install/upgrade path.

### 🛠️ Fixed

- Several issues surfaced by the IIS → Kestrel migration: IPv4-mapped-IPv6 addresses breaking media
  proxying, HTTP/2 WebSocket multiplexing breaking the live detection overlay, a missing example
  config file in published builds, and `install-node.ps1` always touching the firewall rule.

## [0.203.0] - 2026-09-14

### 🛠️ Fixed

- Slice-mode detection regressions from the previous release: objects at the true image edge could
  vanish, and a fast detection path could mislabel position. Also fixed a pre-existing bug where a
  clipped object could show a stray duplicate box.

## [0.202.0] - 2026-09-14

### 🛠️ Fixed

- Better ONNX model metadata detection. Custom models now work in Slice mode. Fixed duplicate/split
  detections when two tiles disagreed on an object's class.

### ➕ Added

- Custom ONNX models can use a faster single-pass detection mode when the model supports it, with
  automatic fallback to the safe per-tile path.

## [0.201.0] - 2026-09-13

### ⚙️ Changed

- Relicensed from MIT to Apache-2.0, matching sibling project SideGlance's own relicensing, so code
  and patterns can be shared between the two freely.

### ➕ Added

- Dashboard shows a spinner while a camera's AI detection engine is still cold-building.
- Settings changes now reach a running camera pipeline within seconds instead of up to 30.
- AI backend dropdown gains TensorRT and OpenVINO options, fixing a bug where Intel and AMD both
  silently mapped to DirectML.
- D-FINE FP16 via TensorRT, with automatic FP32 fallback if a camera's engine overflows.
- New "Custom" model family runs any ONNX model dropped into the node's models folder.
- Models are no longer bundled into the node build; drop them into the models folder instead.
- The external HTTP inference backend can send raw pixels instead of JPEG for lower overhead, and
  reports a per-stage timing breakdown.

### ⚙️ Changed

- External inference traffic now uses its own connection pool, separate from node report traffic.
- The external backend sends raw JPEG bytes instead of base64-encoded JSON.
- Detection frame-rate cap (`Detection.MaxFps`) is now per-camera, not just per-node.

### 🛠️ Fixed

- The external inference model picklist ignored the real input size due to a JSON casing mismatch,
  and its health probe always failed.
- Snapshot crop margin was too tight in Slice mode with the external backend.

## [0.200.0] - 2026-09-11

### 🛠️ Fixed

- A per-node settings override could be silently lost when saving from a stale page — saving now
  checks the node hasn't changed underneath since the page loaded, and rejects the save instead of
  overwriting.
- Admin → Settings → AI Detection (and Cameras → Edit) could silently fail to save after clicking
  Test Connection/Re-probe, since Save reused that handler instead of its own.

## [0.199.0] - 2026-09-09

### ⚙️ Changed

- Snapshots now shows detections only, not plain motion.

### 🛠️ Fixed

- An AI detection could be missing from Snapshots if the retired Motion toggle had been unchecked.

## [0.198.0] - 2026-09-09

### 🛠️ Fixed

- Live view stuttered and repeatedly caught up compared to smooth recorded playback. The live
  stream flushed a fragment only per keyframe (bursty); it now flushes every ~500ms. The client also
  chased the live edge too aggressively; it now holds a small jitter buffer with gentle rate
  correction instead.
- A slow viewer's connection could corrupt its own stream and force a jarring reconnect. The node
  now disconnects that viewer cleanly instead.

## [0.197.0] - 2026-09-09

### 🛠️ Fixed

- A fast-moving object could show no live detection box unless "Idle" was also enabled — a
  movement-tracking gap on a briefly missed frame (motion blur, occlusion) wiped its history and
  reset it to Idle. Track state now survives ByteTrack's coast-through-miss window.

## [0.196.0] - 2026-09-08

### 🛠️ Fixed

- The storage watermark backstop deleted archive-enabled footage even when the archive volume was
  healthy, and gave up too easily on transient SMB/USB blips. Retries added; the watermark pass now
  moves footage to archive when possible, falling back to delete only when necessary.

## [0.195.0] - 2026-09-08

### ⚙️ Changed

- Detection-box jitter rejection is now its own opt-in, per-camera, tunable setting
  (`Detection.RejectMotionJitter`), off by default — it helped some cameras but made a noisier
  model misclassify parked vehicles as moving all night. Snapshot finalization grace period is now
  its own setting too (`Detection.DepartureGraceSeconds`).

### 🛠️ Fixed

- Motion-span reporting could log duplicate-key errors when a batch reported the same span twice.
- A recorder node's primary drive could fill to 100% when its archive volume was unreachable — the
  watermark pass now deletes to free space in that case (superseded by 0.196.0, which restores
  archiving once the volume is healthy again).

## [0.194.0] - 2026-09-08

### ➕ Added

- Recording failover — assign a recorder node a backup node; if it goes down, its cameras move to
  the backup for recording and live view until it recovers. The failover decision uses a
  health-check quorum to avoid flapping on a flaky link. New maintenance mode and "recording only"
  (AI off) per-node switches.

## [0.193.0] - 2026-09-07

### ➕ Added

- Media proxies now auto-update, the same way recorder nodes do.

### 🛠️ Fixed

- Enabling archive storage on a node with a large existing footage backlog could stall; reports now
  go out in batches.

## [0.192.0] - 2026-09-07

### ➕ Added

- Media proxy tier — a standalone TLS-terminating relay between browsers and recorder nodes for
  live view and playback, useful when a proxy machine can hold a real certificate but the nodes
  can't. Nodes can be assigned a primary/backup proxy with automatic fallback to direct-to-node or
  through this server.

## [0.191.0] - 2026-09-06

### ➕ Added

- Direct-to-node streaming — live view and playback can connect a browser straight to the recorder
  node instead of relaying through this server. Off by default; falls back safely to the proxy path
  when a node isn't ready.

### 🛠️ Fixed

- Zooming the timeline in fullscreen also zoomed the video underneath it.

## [0.190.0] - 2026-09-06

### ➕ Added

- Recorder-node check-ins are now replay-hardened (rolling nonce). Bearer-secret rotation support
  (dormant by default). One-shot media tokens (playback, thumbnails, snapshot crops) are now
  refused on reuse.

## [0.189.0] - 2026-09-06

### 🛠️ Fixed

- Moving people/animals could be misclassified as idle by an overly strict directedness check added
  in 0.188.0; reverted to a simpler centroid-displacement check.
- The node registration key field on Admin → Settings → Nodes rendered blank instead of masked.

### ⚙️ Changed

- Hover thumbnails and AI snapshot images are now stored as WebP instead of JPEG.

## [0.188.0] - 2026-09-06

### ⚙️ Changed

- Storage configuration (recording/archive paths) is now per recorder node instead of one global
  setting.

### ➕ Added

- Archive storage tier — a node can move aging footage to a secondary volume instead of deleting
  it, with its own retention window; playback and thumbnails work transparently from either volume.
- Node build numbers, so a rebuild without a version bump still registers as newer for auto-update.
- Snapshot badges now show object counts (e.g. "Human ×2").

### 🛠️ Fixed

- A redundant "Person" entry appeared under "Human" in the Snapshots filter tree.
- A parked vehicle could intermittently register as moving.
- A departing object's snapshot could stay open long enough to merge with an unrelated later object
  of the same type.

## [0.187.2] - 2026-09-05

### 🛠️ Fixed

- Dragging the recording timeline flashed the video black between positions.

### ⚙️ Changed

- Camera queries that load groups/streams together now run as split queries for performance.

## [0.187.1] - 2026-09-05

### 🛠️ Fixed

- A snapshot with several tagged objects now crops around all of them instead of just the
  highest-confidence one.

### ⚙️ Changed

- AI detections of people are now labeled "Human" instead of "Human — person".

## [0.187.0] - 2026-09-05

### ➕ Added

- D-FINE can now run on TensorRT at FP32 (opt-in). FP16 support is wired but not yet selectable —
  no working mixed-precision model export exists yet.

### ⚙️ Changed

- D-FINE is now labeled "experimental"; YOLOX remains the recommended default.

## [0.186.3] - 2026-09-05

### ⚙️ Changed

- The vision log now identifies cameras by name instead of GUID.

## [0.186.2] - 2026-09-05

### 🛠️ Fixed

- On a node with TensorRT enabled, only the first camera in Slice mode worked — every other camera
  failed on every frame due to a TensorRT engine cache collision that didn't account for per-camera
  shape differences. Also fixed misleading "warm cache" logging and excessive per-frame error spam.

### ➕ Added

- The vision log now reports a Slice camera's resolved tile geometry and per-slice
  detection/merge counts.

## [0.186.1] - 2026-09-05

### 🛠️ Fixed

- Clicking Play on Playback before a camera's stream finished loading could leave it silently
  paused.

## [0.186.0] - 2026-09-05

### ➕ Added

- New "Slice" aspect-fitting option for AI detection — cuts a wide/tall camera's frame into
  overlapping tiles run at full resolution instead of letterboxing and downscaling, to catch small
  or distant objects. Can run entirely on GPU.

### ⚙️ Changed

- Slice mode forces GPU frame preprocessing on for that camera.

## [0.185.0] - 2026-09-05

### ⚙️ Changed

- Internal groundwork for batched AI inference — no behavior or performance change yet.

## [0.184.0] - 2026-09-05

### ⚙️ Changed

- AI-detection snapshots now keep upgrading to the clearest view of a moving object instead of
  freezing on its first sighting.

## [0.183.0] - 2026-09-05

### ➖ Removed

- High-resolution re-detection and high-resolution snapshots — neither worked reliably, and the
  former was the largest CPU cost measured on a busy node. Also removed the vision debug-image
  setting that existed only to diagnose it.

## [0.182.0] - 2026-09-04

### 🛠️ Fixed

- D-FINE on a TensorRT-enabled node produced no detections at all, with nothing in any log
  (FP16 overflow). D-FINE now runs on plain CUDA by default; TensorRT is opt-in per node. Also
  fixed misleading "cold cache" logging.

### ⚙️ Changed

- Camera row actions (Zones, Event tags, Schedule, Re-probe) are now emoji-only buttons with
  tooltips.

## [0.181.0] - 2026-09-04

### ➕ Added

- Snapshot badges now show each AI detection's confidence score.

### 🛠️ Fixed

- Snapshot badges and card borders now follow the Events settings colors instead of an internal
  auto-assigned color.

## [0.180.0] - 2026-09-03

### ➕ Added

- Live view can label AI detection boxes with their confidence score.

### ⚙️ Changed

- Live-view Moving/Idle detection controls are now switches, saved per account.

## [0.179.1] - 2026-09-03

### 🛠️ Fixed

- Enabling TensorRT could stop recording and live view entirely — engine builds ran on the
  recording pipeline's own thread and starved it. Builds now run off that path, one at a time.
  Also fixed overlapping start requests tearing down in-progress builds, and a too-generous
  timeout starving the live detection overlay.

### ⚙️ Changed

- TensorRT's GPU workspace is now bounded instead of unconstrained.

## [0.179.0] - 2026-09-03

### ➕ Added

- One recorder node package now runs on any hardware — picks CUDA, DirectML, or CPU at startup,
  and the CUDA provider library downloads on demand instead of always being bundled.

### 🛠️ Fixed

- An Intel-iGPU node built for CUDA failed AI detection outright.
- TensorRT was calling an API newer ONNX Runtime no longer accepts.

## [0.178.0] - 2026-09-02

### ⚙️ Changed

- Playback timeline bookmark markers now have an outline for legibility over colored backgrounds.

## [0.177.0] - 2026-09-02

### ➕ Added

- The Snapshots filter tree now remembers its collapsed/unchecked state per user.

## [0.176.0] - 2026-09-02

### ⚙️ Changed

- Snapshot cards are framed more prominently in their badge color(s), including a gradient border
  for cards spanning multiple detection types.

## [0.175.0] - 2026-09-02

### 🛠️ Fixed

- Lowering the AI detection confidence below 0.6 had no effect — a separate, fixed tracking
  threshold silently discarded anything under it regardless of the configured confidence.

### ➕ Added

- Detection cadence log line now reports what the model found vs. what survived tracking, to
  distinguish "no detection" from "detected but rejected."

## [0.174.2] - 2026-09-02

### ⚙️ Changed

- AI detection no longer allocates its model input buffer per frame, cutting a major source of GC
  pressure on a busy node.

## [0.174.1] - 2026-09-02

### ⚙️ Changed

- AI detection no longer allocates two large buffers per frame, cutting GC pressure further. The
  detection cadence line now also reports GC/allocation stats.

## [0.174.0] - 2026-09-02

### ➕ Added

- Per-camera AI detection orientation override, for cameras that advertise the wrong stream shape
  over ONVIF.
- Per-camera detection cadence log line (frames captured/processed/dropped, inference time).

### 🛠️ Fixed

- AI detection could restart a camera's watch in a loop after a routine re-probe overwrote a
  resolution correction.

## [0.173.1] - 2026-09-01

### 🔒 Security

- Camera RTSP credentials were being written into node log files via ffmpeg's own output; now
  scrubbed. Rotate existing logs.

## [0.173.0] - 2026-09-01

### ➕ Added

- A medium D-FINE model option, alongside the existing small variants.

## [0.172.2] - 2026-09-01

### ⚙️ Changed

- Detection span reports are now processed one batch at a time per node, merging conflicts instead
  of dropping them on a race. Hardening only.

## [0.172.1] - 2026-09-01

### 🛠️ Fixed

- AI detection could silently stop producing spans/snapshots for a busy camera due to a
  unique-index violation in span coalescing.

## [0.172.0] - 2026-09-01

### 🛠️ Fixed

- Portrait/corridor-mounted cameras produced severely stretched AI-detection snapshot crops when
  their watch stream had never been resolution-probed.

## [0.171.0] - 2026-09-01

### ➕ Added

- Detection frame-rate cap setting.
- YOLOX is now the default detection engine (was D-FINE), with a per-node model-size picker.
- Playback "Now" button.
- A "catching up" badge for fast-forward/drift-correction.
- Snapshots pagination above the grid as well as below.
- Optional high-resolution snapshots.

### 🛠️ Fixed

- AI-detection snapshots are now cropped from the exact detected frame instead of a later
  timestamp seek, and are no longer taken a fraction of a second late.
- Overlapping detections on one camera now show as a single card.

### ⚙️ Changed

- Node registration key is now masked.
- Framework log noise is hidden by default.
- FFmpeg is no longer bundled with the node — install it separately.
- Single-camera playback hides the redundant merged timeline.

## [0.170.0] - 2026-08-31

### ⚙️ Changed

- High-resolution re-detection now runs its pixel work on the GPU instead of the CPU, cutting a
  major CPU cost on multi-camera nodes.

### ➕ Added

- Deployment-wide log level setting, applied without a restart.

## [0.169.1] - 2026-08-31

### ⚙️ Changed

- High-res re-detection's keyframe decode now runs on NVDEC where available, cutting CPU load
  further.

## [0.169.0] - 2026-08-30

### ➕ Added

- GPU frame preprocessing option, moving per-frame color conversion/normalization off the CPU.
  Vendor-neutral, off by default.

## [0.168.0] - 2026-08-30

### ➕ Added

- Vision debug images are now a toggle instead of always-on.

### ⚙️ Changed

- Snapshot images are cropped from the exact detection frame when high-res re-detection is
  enabled, with millisecond-precision fallback seeking.

### 🛠️ Fixed

- Duplicate Snapshots cards for a single object, caused by tracker-ID churn.
- Camera-native and LarisVMS AI detections of the same object no longer double up in Snapshots.

## [0.167.3] - 2026-08-30

### 🛠️ Fixed

- Grid mode's mask/size/sensitivity/active-mode choice never survived a page refresh — a stale
  field projection silently dropped them on read-back (the save itself always worked).

## [0.167.2] - 2026-08-30

### ➕ Added

- Drag-select for the Grid editor's cells instead of one click per cell.

## [0.167.1] - 2026-08-30

### ⚙️ Changed

- Merged the Grid and Polygon zone editors onto one Zones page.
- Grid cell edits now require an explicit Save.
- New cameras default to Grid mode.

## [0.167.0] - 2026-08-30

### ➕ Added

- Grid editor UI — live video with a click-to-mask cell grid, size selector, and sensitivity
  slider.

## [0.166.0] - 2026-08-30

### ➕ Added

- Backend for Grid-mode motion detection, an alternative to hand-drawn polygon zones and the
  primary tuning mechanism on nodes without a GPU. Editor UI ships in a later release.

## [0.165.1] - 2026-08-30

### 🛠️ Fixed

- Zone Kind serialized as a bare integer instead of its name over the API, breaking zone colors
  and the edit form's Kind selector.
- Live per-zone motion wash is now fully transparent at rest instead of a continuous fade, so it's
  obvious when the feed isn't reaching the browser.

## [0.165.0] - 2026-08-30

### ➕ Added

- The Zones editor now shows real live video with each Motion zone washed by its own live motion
  score, instead of a static snapshot with fixed-opacity polygons.

## [0.164.0] - 2026-08-30

### ➕ Added

- High-resolution re-detection's result now actually feeds the snapshot a viewer sees, instead of
  only being logged.

## [0.163.4] - 2026-08-30

### 🛠️ Fixed

- Nothing bounded how many high-res re-detection triggers could run at once across cameras,
  risking CPU pegging and duplicate/fragmented snapshots. Now limited to one at a time
  process-wide.

## [0.163.3] - 2026-08-30

### 🛠️ Fixed

- High-res re-detection's debug image dump silently failed to write anything, with no indication
  why (logged below the file logger's minimum level).

## [0.163.2] - 2026-08-30

### 🛠️ Fixed

- The node process could crash entirely after an ordinary HTTPS timeout talking to the web tier —
  a cancellation-exception check incorrectly treated a timeout the same as a real shutdown.

### ➕ Added

- Temporary diagnostic dump of high-res re-detection's decoded/cropped frames as JPEGs, to
  visually confirm box alignment.

## [0.163.1] - 2026-08-30

### 🛠️ Fixed

- Vision Service never had its own log file — its console output was captured at the wrong log
  level and silently dropped before reaching disk.

## [0.163.0] - 2026-08-30

### ➕ Added

- Motion-guided native-scale re-detection — an opt-in pass that re-runs detection at full
  resolution on a newly-moving object's first frame, for better accuracy than the continuous
  lower-resolution pass.

## [0.162.0] - 2026-08-30

### ➕ Added

- Main-stream fragment ring buffer — eager in-memory capture from the high-res stream at detection
  time, replacing lazy after-the-fact segment seeking. Buffers only in this release; nothing
  consumes it yet.

## [0.161.6] - 2026-08-30

### 🛠️ Fixed

- A few remaining Snapshots cards still 502'd — a segment-duration estimate could be slightly
  inflated, landing past the file's real content. Now retries at offset 0 on failure, same as the
  thumbnail path already did.

## [0.161.5] - 2026-08-30

### 🛠️ Fixed

- "No thumbnail available" on some Snapshots cards was concurrency, not data — the capture gate
  allowed only 2 concurrent ffmpeg captures with a 3s timeout, inherited from an unrelated
  hover-preview feature, so most of a 24-card page burst timed out. Raised to 6 concurrent / 30s
  for the Snapshots case.

## [0.161.4] - 2026-08-30

### ➕ Added

- Diagnostic logging for "No thumbnail available" cards that don't self-heal — the failure routes
  previously returned a bare error with no explanation.

## [0.161.3] - 2026-08-30

### 🛠️ Fixed

- The Snapshots page still timed out after 0.161.2's index — the real cause was an interval-overlap
  query no index can fully seek. The coverage check now runs after pagination, over only the rows
  being rendered.

## [0.161.2] - 2026-08-30

### 🛠️ Fixed

- Attempted fix for Snapshots page timeouts (added an index) — did not fix it; see 0.161.3 for the
  actual cause. Index kept since it helps the real fix.

## [0.161.1] - 2026-08-30

### 🛠️ Fixed

- The Snapshots page threw an unhandled exception on every load due to a query EF Core couldn't
  translate to SQL; rewritten to a simpler shape (still timed out under load — fixed in 0.161.2).
- Many plain-motion Snapshots cards showed "No thumbnail available" even after the page loaded, due
  to an incomplete footage-coverage check.

## [0.161.0] - 2026-08-29

### 🛠️ Fixed

- One moving object could produce several snapshots when the detection model's per-frame class
  guess flickered; detections are now arbitrated to one stable label per tracked object.
- Clicking a Snapshots card jumped straight to the detection instant with no lead-in; now starts
  from the camera's configured pre-roll.
- A motion-span row could live forever after its footage aged out, leaving a permanent broken
  placeholder card; now cleaned up on a retention sweep.

## [0.160.0] - 2026-08-29

### ⚙️ Changed

- AI detection now fits each camera's own real aspect ratio into the model's square input instead
  of stretching every camera to one fixed global resolution — new Letterbox (default) and Stretch
  modes. Live-detection overlay boxes now line up correctly on non-16:9 cameras.

## [0.159.0] - 2026-08-29

### ⚙️ Changed

- Server-side motion detection now runs on GPU decode where available, cutting a major CPU cost.
  New per-camera toggle to disable it entirely on cameras that don't need the fallback.

## [0.158.0] - 2026-08-26

### 🛠️ Fixed

- Vision Service CPU usage went up, not down, after switching to D-FINE, due to unnecessary
  per-frame math.
- AI-detection snapshot crops could miss the detected object entirely once D-FINE's tighter boxes
  shipped; crop margin increased.
- Login was broken site-wide (password field showed as plain text, every sign-in failed) due to a
  missing Razor view-imports file in the Identity area.
- AI-detection snapshots could fragment a single sighting into several due to too-strict span
  closing; added a grace period.
- Best-frame snapshot cropping could pick the wrong object when multiple detections shared a label.
- Snapshot emoji was generic regardless of category.
- AI-detection boxes didn't track zoom/pan on a fullscreened live tile.
- Live-view tiles never showed a badge for AI-detected objects.
- Clicking a snapshot deep link could land on a paused tile needing a manual Pause/Play click to
  actually start.

### ⚙️ Changed

- Replaced YOLOv9 with D-FINE as the default AI detection model (licensing).
- Detection confidence/IoU thresholds and which stream feeds detection are now real, editable
  settings.
- Unified the "person detected" vocabulary between camera-native and AI detections.

### ➕ Added

- AI detection settings tab with a global-default + per-camera-override pattern.
- Camera list shows which cameras have AI detection enabled and which stream.
- Snapshots filter is now a collapsible tree instead of a flat checkbox row.

## [0.157.1] - 2026-08-25

### 🛠️ Fixed

- PowerShell strict-mode bugs in `build-node.ps1`/`install-node.ps1` when exactly one file/process
  matched a query.
- The live-view AI-detection box overlay toggle (shipped earlier) was never actually wired up in
  the UI; added.
- Vision Service couldn't find ffmpeg — its path was never passed through to the child process.
- `build-node.ps1` never bundled an exported model — a non-recursive search missed it.
- False gaps in recording coverage: Playback reported "no recording available" for windows that
  were actually covered, due to a segment-boundary timestamp bug. Retroactively repaired affected
  segments.
- Live-view detection boxes never drew despite real data streaming, due to a JSON casing mismatch.
- AI detection could never find its model on any node, due to a hardcoded wrong default filename.
- Stopping the node service left the Vision Service process running, risking a failed in-place
  upgrade.
- `install-node.ps1` now checks and can fix missing CUDA/cuDNN dependencies at install time.
- A Vision Service startup failure was undiagnosable from the node's own log; now surfaced.
- Camera edit fields for AI detection/motion source always reset to defaults on reload despite the
  save working correctly (a read-side projection gap).

## [0.157.0] - 2026-08-24

### ➕ Added

- Native AI object detection — a real-time YOLO/ByteTrack pipeline running per node, independent of
  onboard camera analytics. Runs as a separate sibling process so a bad GPU/driver interaction can't
  affect recording. Hardware accelerator is a per-node, auto-detected setting. Detected classes
  bucket into a small color-coded category set. Live-view bounding box overlay via its own
  WebSocket. Motion-mode recording gains one designated primary source when multiple signals are
  configured. AI-detected objects get their own cropped snapshot images. Recorder-node auto-update
  now also keeps the Vision Service binary current. Unverified against real GPU hardware or an
  actual camera end-to-end at ship time.

## [0.156.1] - 2026-08-23

### 🛠️ Fixed

- Every page load threw an exception once Entra SSO shipped, due to a DI registration bug and a
  validation edge case in an unconfigured deployment.

## [0.156.0] - 2026-08-23

### 🛠️ Fixed

- Some snapshots stalled forever on Play, looping the same recovery attempt; now gives up
  gracefully after repeated failures instead of looping indefinitely.

### ➕ Added

- Snapshots search can now filter by camera group or saved view, not just single camera/all.
- Playback timeline is taller and auto-hides on mobile.

## [0.155.0] - 2026-08-23

### 🛠️ Fixed

- Changing an already-recording camera's ONVIF device URI never took effect — it kept recording
  from the old source until deleted and re-added.

## [0.154.0] - 2026-08-23

### ➕ Added

- "Sign in with Microsoft" (Entra ID) — sign-in only, matched against an existing
  admin-provisioned account; no self-registration. New Security settings section; takes effect
  without a restart.

## [0.153.0] - 2026-08-23

### ⚙️ Changed

- Field-level help text now collapses behind an info icon instead of always showing, across most
  admin/settings pages.
- `Cameras/Edit` no longer edits group membership directly — links to the Groups page instead.

## Archived history

Only the 10 most recent versions are kept in this file. Older entries are archived in full below
(nothing summarized or dropped), newest first, at most 20 versions per file:

- [v0.144.0 – v0.152.0](changelog-archive/v0.144.0-to-v0.152.0.md) (2026-08-23)
- [v0.131.0 – v0.143.0](changelog-archive/v0.131.0-to-v0.143.0.md) (2026-08-21 – 2026-08-23)
- [v0.111.0 – v0.130.0](changelog-archive/v0.111.0-to-v0.130.0.md) (2026-08-19 – 2026-08-21)
- [v0.91.0 – v0.110.0](changelog-archive/v0.91.0-to-v0.110.0.md) (2026-08-17 – 2026-08-19)
- [v0.81.1 – v0.90.0](changelog-archive/v0.81.1-to-v0.90.0.md) (2026-08-15 – 2026-08-17)
- [v0.61.0 – v0.81.0](changelog-archive/v0.61.0-to-v0.81.0.md) (2026-08-14 – 2026-08-15)
- [v0.41.0 – v0.60.0](changelog-archive/v0.41.0-to-v0.60.0.md) (2026-08-11 – 2026-08-14)
- [v0.21.1 – v0.40.0](changelog-archive/v0.21.1-to-v0.40.0.md) (2026-08-09 – 2026-08-11)
- [v0.1.0 – v0.21.0](changelog-archive/v0.1.0-to-v0.21.0.md) (2026-08-08 – 2026-08-09)

[Unreleased]: https://github.com/billacuda/LarisVMS/compare/v0.221.4...HEAD
[0.221.4]: https://github.com/billacuda/LarisVMS/compare/v0.221.3...v0.221.4
[0.221.3]: https://github.com/billacuda/LarisVMS/compare/v0.221.2...v0.221.3
[0.221.2]: https://github.com/billacuda/LarisVMS/compare/v0.221.1...v0.221.2
[0.221.1]: https://github.com/billacuda/LarisVMS/compare/v0.221.0...v0.221.1
[0.221.0]: https://github.com/billacuda/LarisVMS/compare/v0.220.0...v0.221.0
[0.220.0]: https://github.com/billacuda/LarisVMS/compare/v0.219.0-beta...v0.220.0
