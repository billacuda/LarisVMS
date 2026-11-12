# Users & access

Accounts, roles, permissions, camera access, Active Directory, and Microsoft sign-in.

## Users

Self-registration is off. Accounts are created under **Settings → Users**, where you can also assign roles, disable an account, and reset a password. Local accounts sign in with their email address. Disabling an account signs it out everywhere straight away and closes its live video.

## Passwords

Passwords are at least 12 characters. Common ones, ones like `123456789012`, and ones containing the person's name or email address are refused. To refuse more, put them in `%ProgramData%\LarisVMS\password-blocklist.txt` on the web server, one per line (a published list of breached passwords works). It's picked up without a restart.

Anyone with a local account changes their own password by selecting their name at the top of the page. The current password is required, and every other session of theirs is signed out. Admins change their own password the same way, not under **Settings → Users**. Active Directory accounts change theirs in Windows.

When you create an account or reset a password under **Settings → Users**, you can check **Must change it at next sign-in**. The person then has to choose their own password before they can do anything else. With **Settings → Security → Temporary admin-set passwords** on, it starts checked.

After 5 wrong passwords for one account from one address within 15 minutes, that address has to wait out the 15 minutes; the person can still sign in from anywhere else. An account is locked for 15 minutes only after 20 wrong tries from anywhere, and Super Admins are never locked by wrong passwords at sign-in. Super Admins hear about repeated failures and lockouts, and about guessing spread over many networks: one account failing 10 times from 3 or more networks, or 50 failures from 10 or more networks across the server, within 15 minutes.

## Two-factor sign-in

Anyone who signs in with a password, local or Active Directory, can also require a six-digit code from an authenticator app on their phone (Microsoft Authenticator, Google Authenticator, 1Password, and others). Each code works once, so one someone sees being typed can't be used again. To set it up, select your name at the top of the page, then **Two-factor sign-in**, scan the QR code, and enter the code the app shows. You get ten recovery codes; keep them somewhere safe, since each one signs you in once if you lose your phone. Changing anything later, such as new recovery codes or turning it off, takes a current code.

At sign-in, **Don't ask again on this browser for 14 days** skips the code on that browser until the password or two-factor setup changes.

If someone loses their phone and their recovery codes, **Turn off 2FA** next to them under **Settings → Users** lets them sign in with just their password and set it up again.

**Settings → Security → Require two-factor sign-in** makes everyone who signs in with a password set it up before they can do anything else, and stops them turning it off. Microsoft sign-ins are left to Entra's own checks.

## Sessions

Select your name at the top of the page, then **Sessions**, to see every browser you're signed in on, with its address and when it was last used. Sign out any you don't recognize, or all but the one you're using, then change your password. Changing your password or turning on two-factor sign-in signs out your other sessions right away.

With **Settings → Security → Confirm password for admin pages** on, admin pages ask for your password again (and a code, with two-factor sign-in on) unless you signed in or confirmed it in the last 15 minutes; using them keeps that going. Someone who finds your browser signed in can then use the system but not change its settings, users, or keys. Microsoft sign-ins sign in again instead. It's off by default.

Under **Settings → Users**, **Sign out** next to someone signs them out on every browser. They can sign in again; disable the account to keep them out.

## Roles

A role is a named set of permissions, managed under **Settings → Roles**. Each role also has:

- a **scope tier**;
- an **expiry** for time-limited access (for example, 480 minutes for an 8-hour shift);
- a **session lifetime**: hours after signing in before the user has to sign in again, however active they are (at most 30 days, or 0 to never expire);
- an **idle timeout**: minutes without opening a page before the user is signed out (0 is off). A screen left showing live video counts as idle, so leave it off for roles that watch a video wall;
- a **PTZ priority**, which decides who wins when two users move the same camera.

## Permissions

**Settings → Permissions matrix** sets which actions each role is allowed (view cameras, playback, export, edit settings, and so on). The built-in **Super Admin** role always has every permission.

**Start and stop manual recording** controls the record button on live tiles. It needs the **Record** camera access below too.

## Camera access

**Settings → Camera access** decides which cameras each role can use: every camera, a group, or single cameras, separately for View, Playback, Export, PTZ, Talk (two-way audio), Configure, and Record (manual recording). A role with **no grants has no cameras**; Super Admin always has every camera. Export never reaches further than Playback. Upgrading from a version where roles without grants could use every camera adds an "all cameras" grant to keep each role and user's access the same.

## API keys

**Admin → API keys** issues keys for the [API](/Help/api). Each key acts with the role it's bound to, and can be limited to some of that role's permissions, so several keys can do different things. The role's camera access decides which cameras a key can use.

## Entra

**Settings → Security → Entra ID sign-in** adds a “Sign in with Microsoft” button. It never creates accounts, and it doesn't match accounts by email: on **Settings → Users**, use **Microsoft sign-in** on a person's row to enter their Entra **object ID** (from their user page in the Microsoft Entra admin center). Their first Microsoft sign-in completes the link; blank removes it. Register an app in Entra with the redirect URI shown on the Security page, then enter its tenant ID (your directory's GUID or domain, not `common`), client ID, and secret. Changes take effect immediately. Accounts already linked keep working.

## Active Directory

**Settings → Active Directory** lets domain users sign in with their AD username and password, and gives them roles from their AD groups. Only a Super Admin can change the domain, how LarisVMS connects to it, or local sign-in, since those decide who can sign in as anyone; other admins can change the sync interval and group links.

- **Domain:** just the domain name, like `corp.example.com`.
- **Encryption:** LDAPS on port 636 is the default and needs a certificate on your domain controllers that this server trusts. Plain LDAP on port 389 works without one, but it's insecure and not recommended.
- **Lookup account:** integrated security (the default) uses the LarisVMS Web service's own account. When the service runs as LocalSystem on a domain-joined server, that's the computer account. Or enter a service account as `DOMAIN\username` with its password.
- **Signing in:** a name with an `@` is a local account. Anything else is an AD username, typed without the domain.

**Group → role links:** start typing a group name and pick from the list (Tab takes the first match). Members of the group get the role, including members of nested groups. Accounts are created the first time they're needed, as soon as a group is linked or when the user first signs in. Groups and users are tracked by SID, so renaming either in AD carries over at the next sync. Roles for AD accounts come only from group links and can't be edited on the Users page.

**Sync** runs every 30 minutes by default (5 minutes to 24 hours), or straight away with **Sync now**. An AD account that's disabled or expired in AD, or that's no longer in any linked group, is disabled here and signed out. If AD can't be reached, the sync changes nothing and tries again in 5 minutes. An account disabled by sync is re-enabled when it qualifies again. One disabled by hand on the Users page stays disabled.

**Local sign-in** can be turned off while AD sign-in is on, once at least one AD account has Super Admin. Keep the Super Admin account created during setup, and its password, somewhere safe. If AD becomes unavailable, run this against the LarisVMS database and sign in with that account. On SQL Server:

```sql
UPDATE ActiveDirectorySettings SET LocalLoginsEnabled = 1
```

On MariaDB:

```sql
UPDATE `ActiveDirectorySettings` SET `LocalLoginsEnabled` = 1;
```

## Audit log

Everything users do is recorded under **Logs → Audit logs**: changes (with old and new values; secrets are recorded only as “changed”), viewing cameras, starting playback, and exporting.

**Tamper-evident.** Each entry is sealed together with the one before it, using a key the database alone doesn't hold. Every 6 hours (and when you select **Check now** on the audit log page) the whole log is checked. If an entry was changed, removed, or added directly in the database, the page says which one and Super Admins are notified. Entries removed by retention are expected and don't count, and each removal is itself recorded (**AuditLog.Pruned**). Entries from before this update aren't covered.

The server also keeps a small record outside the database, in `%ProgramData%\LarisVMS\audit-chain.dat`, of the seal's key and where the log started, so the log can't be resealed with another key or wiped and restarted without that showing. If you deliberately start over with a new, empty database on the same server, delete that file once; a new one is made with the next entry.

**Forward to syslog.** To keep a copy of the audit log off the server, enter a syslog server under **Settings → Logs**. Every new entry is sent as it's written (RFC 5424, facility *log audit*, with the entry as JSON). Choose UDP, TCP, or TLS. TLS needs the syslog server's certificate to be trusted by the web server. If the syslog server can't be reached, entries are kept and sent once it's back. Forwarding starts with the next entry, not the existing history. Only a Super Admin can change where the log is forwarded, or turn forwarding off; that change is itself sent to the old destination first.
