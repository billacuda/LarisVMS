# Alerts, notifications, and email

Alert rules, notifications to users, team channels, and email provider setup.

## Alert rules

**Settings → Alerts** holds the shared rules. Each rule watches for one condition:

- **Camera not reporting**: the camera has stopped sending health reports.
- **Node offline**: the node has stopped checking in.
- **Storage low**: free space has dropped below a percentage.
- **Node failover**: a node's cameras moved to its backup node.
- **Motion detected**: motion on a camera, or in one of its motion zones.
- **Object detected**: AI detection saw a matching object.
- **Speed over a limit**: an object crossed a [speed trap](/Help/ai-detection#speed-traps) at or over a set speed.
- **Evidence lock ending soon** and **Evidence lock removed**: see [evidence locks](/Help/live-and-playback#evidence-locks).

Health rules are checked once a minute. The **cooldown** is the minimum time between repeat alerts, so a condition that stays true doesn't alert every minute.

### Detection alerts

**Object detected** and **Speed over a limit** fire within seconds of the detection. Every filter is optional, and all the ones you set must match:

- **Camera**: one camera, the cameras on one node, or any camera.
- **Category** and **label**: for example Vehicle, or just trucks.
- **Color**: the object's color, or a person's shirt or pants color. See [object colors](/Help/ai-detection#object-colors).
- **Zone**: only objects standing inside one of the camera's zones (judged by the bottom of the box).
- **Active**: days of the week and a time window, in the time zone of whoever saved the rule. A window like 22:00 to 06:00 runs overnight.

One detection alerts once. On a rule watching several cameras, the cooldown counts separately for each camera, so a busy camera doesn't hold back the others.

### Motion alerts

**Motion detected** fires within seconds of motion, from the server's motion detection or the camera's own motion events. Choose one camera, the cameras on one node, or any camera. With one camera chosen, **Zone** limits it to one of that camera's **Server motion** zones; leave it on **(whole camera)** for motion anywhere. The **Active** days and times work the same as for detection alerts. One stretch of motion alerts once, and the cooldown holds back the next one.

### Team channels

Each rule can also send to email lists, webhooks, ntfy, Pushover, Slack, and Teams. Turn on a channel and fill in its fields.

## Notifications

Notifications go to individual people, in their browser or by email, instead of to a team channel. Each person picks what they want under **Notifications** in the sidebar (also the 🔔 next to your name). A notification only reaches someone for cameras they can view.

- **Shared notifications**: on an alert rule, **Let users subscribe** lists it under **My notifications → Available**, where anyone can turn it on. Admins can also add a user, or everyone in a role, under **Subscribers** on the rule.
- **Your own notifications**: **My notifications → Mine → New notification** builds one just for you, with the same choices as an alert rule. For example, animals on the back yard camera between 10 pm and 6 am. Only you see it.
- **Mute** a notification for an hour, 8 hours, a day, or until you turn it back on, without unsubscribing.

**Settings → Notifications** turns each kind of notification on or off for users. Turning a kind off doesn't stop the team channels on alert rules.

Subscribing needs the **Subscribe to shared notifications** permission, and making your own needs **Create personal notifications**. Every role has both by default.

### Browser notifications

Turn them on in each browser under **My notifications → This browser and devices**, then use **Send a test**. **Settings → Notifications** chooses how they're delivered:

| Delivery | Works when | Needs |
|---|---|---|
| **While LarisVMS is open** | A LarisVMS tab or the installed app is open | Nothing. Stays on your network. |
| **Web Push** | No LarisVMS page open (Chrome, Edge, Firefox, and Safari). On a computer, the browser itself has to be running; see below | **Generate Web Push keys** once, and outbound internet access from the server to the browser makers' push services. No account. |
| **Firebase Cloud Messaging** | No LarisVMS page open, with the same limit on computers | Your own Firebase project: its web app config, web push certificate, and a service account key. Used only when Web Push is off. |

Without permission to show notifications, they appear inside open LarisVMS pages instead. On a phone, install LarisVMS as an app first (see [the overview](/Help/overview)).

Phones get push notifications even with the browser closed. On a computer, a browser only receives them while it's running, and closing its last window usually quits it; anything sent meanwhile shows when it starts again. To keep it running in the background, turn on **Continue running background apps when Google Chrome is closed** (Chrome: Settings → System) or **Continue running background extensions and apps when Microsoft Edge is closed** (Edge: Settings → System and performance). Use email for anyone who needs alerts with no browser running.

### Email notifications

People can also choose email for a notification. It goes to the address on their account, through the email set up below. **Settings → Notifications** can turn email notifications off.

## Email

Email alerts and notifications are sent through the provider set up under **Settings → Email**. **Save and send test** checks the saved settings.

- **SMTP**: host, port, SSL/TLS, and credentials.
- **Microsoft Graph**: register an app in Entra ID, grant it the `Mail.Send` **application** permission (not delegated) with admin consent, and enter its tenant ID, client ID, and secret. There's no per-user sign-in.
- **Gmail**: create an OAuth client of type *Web application* in Google Cloud. Add `https://<your-host>/Admin/OAuthCallback` as an authorized redirect URI, and enable the `https://mail.google.com/` scope. Then use **Save and connect to Google** once to grant consent.

Saved secrets are encrypted. If the server's data-protection keys are lost, re-enter them, and reconnect Gmail.
