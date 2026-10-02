# Users & access

Accounts, roles, permissions, camera access and Microsoft sign-in.

## Users

Self-registration is off. Accounts are created under **Settings → Users**, where you can also assign roles, disable an account and reset a password.

## Roles

A role is a named set of permissions, managed under **Settings → Roles**. Each role also has:

- a **scope tier**;
- an **expiry** for time-limited access (for example, 480 minutes for an 8-hour shift);
- a **session lifetime** (0 never expires);
- a **PTZ priority**, which decides who wins when two users move the same camera.

## Permissions

**Settings → Permissions matrix** sets which actions each role is allowed (view cameras, playback, export, edit settings and so on). The built-in **Super Admin** role always has every permission.

## Camera access

By default a role can use every camera its permissions allow. **Settings → Camera access** narrows a role down to specific cameras or groups, separately for View, Playback, Export and PTZ. A role with **no grants is unrestricted**, so adding the first grant is what limits it. Talk and Configure can be granted but aren't enforced yet.

## Entra

**Settings → Security → Entra ID sign-in** adds a “Sign in with Microsoft” button for existing accounts whose email matches. It never creates accounts. Register an app in Entra with the redirect URI shown on that page, then enter its tenant ID, client ID and secret. Changes take effect immediately.

## Audit log

Everything users do is recorded under **Logs → Audit logs**: changes (with old and new values; secrets are recorded only as “changed”), viewing cameras, starting playback and exporting.
