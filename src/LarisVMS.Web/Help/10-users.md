# Users & access

Accounts, roles, permissions, camera access, Active Directory, and Microsoft sign-in.

## Users

Self-registration is off. Accounts are created under **Settings → Users**, where you can also assign roles, disable an account, and reset a password. Local accounts sign in with their email address. Disabling an account signs it out everywhere straight away and closes its live video.

## Roles

A role is a named set of permissions, managed under **Settings → Roles**. Each role also has:

- a **scope tier**;
- an **expiry** for time-limited access (for example, 480 minutes for an 8-hour shift);
- a **session lifetime**: hours after signing in before the user has to sign in again, however active they are (0 never expires);
- a **PTZ priority**, which decides who wins when two users move the same camera.

## Permissions

**Settings → Permissions matrix** sets which actions each role is allowed (view cameras, playback, export, edit settings, and so on). The built-in **Super Admin** role always has every permission.

## Camera access

**Settings → Camera access** decides which cameras each role can use: every camera, a group, or single cameras, separately for View, Playback, Export, PTZ, Talk (two-way audio), and Configure. A role with **no grants has no cameras**; Super Admin always has every camera. Export never reaches further than Playback. Upgrading from a version where roles without grants could use every camera adds an "all cameras" grant to keep each role and user's access the same.

## Entra

**Settings → Security → Entra ID sign-in** adds a “Sign in with Microsoft” button. It never creates accounts, and it doesn't match accounts by email: on **Settings → Users**, use **Microsoft sign-in** on a person's row to enter their Entra **object ID** (from their user page in the Microsoft Entra admin center). Their first Microsoft sign-in completes the link; blank removes it. Register an app in Entra with the redirect URI shown on the Security page, then enter its tenant ID (your directory's GUID or domain, not `common`), client ID, and secret. Changes take effect immediately. Accounts already linked keep working.

## Active Directory

**Settings → Active Directory** lets domain users sign in with their AD username and password, and gives them roles from their AD groups.

- **Domain:** just the domain name, like `corp.example.com`.
- **Encryption:** LDAPS on port 636 is the default and needs a certificate on your domain controllers that this server trusts. Plain LDAP on port 389 works without one, but it's insecure and not recommended.
- **Lookup account:** integrated security (the default) uses the LarisVMS Web service's own account. When the service runs as LocalSystem on a domain-joined server, that's the computer account. Or enter a service account as `DOMAIN\username` with its password.
- **Signing in:** a name with an `@` is a local account. Anything else is an AD username, typed without the domain.

**Group → role links:** start typing a group name and pick from the list (Tab takes the first match). Members of the group get the role, including members of nested groups. Accounts are created the first time they're needed, as soon as a group is linked or when the user first signs in. Groups and users are tracked by SID, so renaming either in AD carries over at the next sync. Roles for AD accounts come only from group links and can't be edited on the Users page.

**Sync** runs every 30 minutes by default (5 minutes to 24 hours), or straight away with **Sync now**. An AD account that's disabled or expired in AD, or that's no longer in any linked group, is disabled here and signed out. If AD can't be reached, the sync changes nothing and tries again in 5 minutes. An account disabled by sync is re-enabled when it qualifies again. One disabled by hand on the Users page stays disabled.

**Local sign-in** can be turned off while AD sign-in is on, once at least one AD account has Super Admin. Keep the Super Admin account created during setup, and its password, somewhere safe. If AD becomes unavailable, run this against the LarisVMS database and sign in with that account:

```sql
UPDATE ActiveDirectorySettings SET LocalLoginsEnabled = 1
```

## Audit log

Everything users do is recorded under **Logs → Audit logs**: changes (with old and new values; secrets are recorded only as “changed”), viewing cameras, starting playback, and exporting.
