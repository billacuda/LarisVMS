# Alerts & email

Alert rules, notification channels and email provider setup.

## Alert rules

**Settings → Alerts** holds rules. Each rule watches one camera or node for a condition:

- **Camera not reporting**: the camera has stopped sending health reports.
- **Node offline**: the node has stopped checking in.
- **Storage low**: free space has dropped below a percentage.

Rules are checked once a minute. The **cooldown** is the minimum time between repeat alerts, so a condition that stays true doesn't alert every minute.

Each rule can send to email, webhook, ntfy, Pushover, Slack and Teams. Turn on a channel and fill in its fields.

## Email

Email alerts are sent through the provider set up under **Settings → Email**. **Save and send test** checks the saved settings.

- **SMTP**: host, port, SSL/TLS and credentials.
- **Microsoft Graph**: register an app in Entra ID, grant it the `Mail.Send` **application** permission (not delegated) with admin consent, and enter its tenant ID, client ID and secret. There's no per-user sign-in.
- **Gmail**: create an OAuth client of type *Web application* in Google Cloud. Add `https://<your-host>/Admin/OAuthCallback` as an authorized redirect URI, and enable the `https://mail.google.com/` scope. Then use **Save and connect to Google** once to grant consent.

Saved secrets are encrypted. If the server's data-protection keys are lost, re-enter them, and reconnect Gmail.
