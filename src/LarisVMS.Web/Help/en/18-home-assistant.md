# Home Assistant

Show your cameras, detections, and recording nodes in Home Assistant, and start recordings or add bookmarks from it.

> **Experimental.** Home Assistant support is new, and people use Home Assistant for many different things. Please [share how it works for you](https://github.com/billacuda/LarisVMS/discussions/new?category=general&title=Home%20Assistant%20feedback): what you use it for, what works well, and anything that goes wrong.

## How it works

LarisVMS sends to the MQTT broker Home Assistant already uses. Home Assistant's MQTT discovery then adds a device for each camera and each recording node, with no YAML to write.

Home Assistant acts as a role you choose, the same way an API key does:

- It only gets the cameras that role can view under **Settings → Camera access**.
- It can only do what that role is allowed to. For example, the recording switch only appears when the role has **Start and stop manual recording** and the **Record** camera access.
- What it does appears in the audit log as **Home Assistant**.

## Set it up

1. In Home Assistant, install the **Mosquitto broker** add-on and the **MQTT** integration, if you don't use them already. Make a broker login for LarisVMS, such as a Home Assistant user named `larisvms`.
2. In LarisVMS, make a role for Home Assistant under **Settings → Permissions → Roles**. Give it **View cameras**, and give it access to the cameras you want under **Settings → Camera access**. Add **Start and stop manual recording**, or **Play back recorded footage** and **Create bookmarks on recorded footage**, if Home Assistant should be able to record or bookmark.
3. Open **Settings → Home Assistant** and turn it on. Enter the broker's address (your Home Assistant's address when you use the add-on), the port (usually `1883`), the login, and the role. Select **Test connection**, then **Save**.
4. In Home Assistant, open **Settings → Devices & services → MQTT**. Your cameras appear there as devices.

The status under **Settings → Home Assistant** shows whether LarisVMS is connected, and how many cameras it's sending. If the broker restarts or the network drops, LarisVMS reconnects on its own. When Home Assistant restarts, LarisVMS sends everything again.

## What appears in Home Assistant

Each camera gets:

| Entity | Shows |
|---|---|
| **Motion** | On while there's motion or a detection |
| **Person**, **Vehicle**, **Animal** | On while that kind of object is detected |
| **Online** | Whether the camera is sending video |
| **Latest detection** | A picture of the latest detected object |
| **Last detection**, **Last event** | What was last detected, and when |
| **Last speed** | The last measured speed, from [speed traps](/Help/ai-detection), in the units under **Settings → Detection** |
| **Manual recording** | A switch that starts and stops a manual recording, when the role allows it |
| **Bookmark** | A button that adds a bookmark at the current time, when the role allows it |

Each recording node gets **Online** and **Free storage**.

A sensor turns on within a second or two of a detection, and off about 25 seconds after it ends. That's the same as the motion badge on **Live**.

## Live video

Home Assistant's **Generic Camera** can show live video from LarisVMS. It uses the same stream as the web pages, so the camera isn't asked for a second one.

1. Give the Home Assistant role the **Stream live video to other programs** permission. No role has it until you give it one.
2. Under **Admin → API keys**, add a key bound to that role.
3. In Home Assistant, add a **Generic Camera** for each camera. **Settings → Home Assistant** lists each camera's **Still image URL** and **Stream source**. Choose **Basic** authentication, enter `apikey` as the user name, and the key as the password.

The [API](/Help/api) describes the stream.

## Alerts by webhook

An alert rule's webhook can also send to Home Assistant. In Home Assistant, make an automation with a **Webhook** trigger, then put its address in the rule's webhook, such as `http://homeassistant.local:8123/api/webhook/larisvms-front-door`.

The webhook sends JSON with the rule, message, and time, plus, when the alert has them, the event, camera, node, detected object, speed, and `snapshotPath`. Add `snapshotPath` to your LarisVMS address to get the detection's picture with an API key.

```yaml
triggers:
  - trigger: webhook
    webhook_id: larisvms-front-door
    local_only: true
actions:
  - action: notify.notify
    data:
      message: "{{ trigger.json.message }}"
```

## If something's missing

- **Not connected:** check the address, port, and login with **Test connection**. A broker that needs TLS usually uses port `8883`.
- **No devices in Home Assistant:** check the MQTT integration is set up, and that the discovery prefix matches Home Assistant's (`homeassistant` unless you changed it).
- **A camera is missing:** the role can't view it, or the camera is turned off.
- **No recording switch or bookmark button:** the role doesn't have the permission or camera access for it.
- **The webhook doesn't arrive:** LarisVMS won't send webhooks to `localhost`, so use Home Assistant's network address, even when it runs on the same machine.
