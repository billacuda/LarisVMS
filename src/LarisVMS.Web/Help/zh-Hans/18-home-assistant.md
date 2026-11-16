# Home Assistant

在 Home Assistant 中显示你的摄像头、检测和录像节点，并从 Home Assistant 开始录像或添加书签。

> **实验性。** Home Assistant 支持是新功能，而大家使用 Home Assistant 的方式各不相同。请[告诉我们你的使用情况](https://github.com/billacuda/LarisVMS/discussions/new?category=general&title=Home%20Assistant%20feedback)：你用它做什么、哪些效果好、遇到了哪些问题。

## 工作方式

LarisVMS 会发送到 Home Assistant 已在使用的 MQTT 代理。然后 Home Assistant 的 MQTT 发现会为每个摄像头和每个录像节点添加一个设备，无需编写 YAML。

Home Assistant 以你选择的角色运行，与 API 密钥相同：

- 它只会获得该角色在 **设置 → 摄像头访问** 中可以查看的摄像头。
- 它只能执行该角色允许的操作。例如，只有当角色拥有 **开始和停止手动录制** 和 **录制** 摄像头访问权限时，录像开关才会出现。
- 它执行的操作会在审计日志中显示为 **Home Assistant**。

## 设置

1. 在 Home Assistant 中，如果还没有使用，请安装 **Mosquitto broker** 加载项和 **MQTT** 集成。为 LarisVMS 创建一个代理登录，例如名为 `larisvms` 的 Home Assistant 用户。
2. 在 LarisVMS 的 **设置 → 权限 → 角色** 中，为 Home Assistant 创建一个角色。授予它 **查看摄像头**，并在 **设置 → 摄像头访问** 中授予它所需摄像头的访问权限。如果希望 Home Assistant 能录像或添加书签，请添加 **开始和停止手动录制**，或 **回放已录制的录像** 和 **在已录制的录像上创建书签**。
3. 打开 **设置 → Home Assistant** 并启用。输入代理地址（使用加载项时就是你的 Home Assistant 地址）、端口（通常为 `1883`）、登录信息和角色。选择 **测试连接**，然后选择 **保存**。
4. 在 Home Assistant 中打开 **设置 → 设备与服务 → MQTT**。你的摄像头会作为设备显示在那里。

**设置 → Home Assistant** 中的状态会显示 LarisVMS 是否已连接，以及正在发送多少个摄像头。代理重启或网络中断时，LarisVMS 会自动重新连接。Home Assistant 重启时，LarisVMS 会重新发送所有内容。

## Home Assistant 中显示的内容

每个摄像头都会有：

| 实体 | 显示内容 |
|---|---|
| **移动** | 有移动或检测时为开 |
| **人员**、**车辆**、**动物** | 检测到该类物体时为开 |
| **在线** | 摄像头是否在发送视频 |
| **最新检测图像** | 最近检测到的物体的图片 |
| **最近检测**、**最近事件** | 最近检测到了什么，以及时间 |
| **最近速度** | [测速区](/Help/ai-detection) 最近测得的速度，单位按 **设置 → AI 检测** 中的设置 |
| **手动录像** | 角色允许时，用于开始和停止手动录像的开关 |
| **添加书签** | 角色允许时，在当前时间添加书签的按钮 |

每个录像节点都有 **在线** 和 **可用存储**。

传感器会在检测后一两秒内打开，并在检测结束约 25 秒后关闭。这与 **实时** 页面上的移动标记相同。

## 实时视频

Home Assistant 的 **Generic Camera** 可以显示 LarisVMS 的实时视频。它使用与网页相同的视频流，因此不会向摄像头再要一路流。

1. 为 Home Assistant 角色授予 **向其他程序推送实时视频** 权限。在你授予之前，任何角色都没有此权限。
2. 在 **管理 → API 密钥** 中，添加一个绑定到该角色的密钥。
3. 在 Home Assistant 中，为每个摄像头添加一个 **Generic Camera**。**设置 → Home Assistant** 会列出每个摄像头的 **静态图像 URL** 和 **视频流来源**。身份验证选择 **Basic**，用户名填 `apikey`，密码填该密钥。

[API](/Help/api) 中介绍了该视频流。

## 通过 Webhook 发送警报

警报规则的 Webhook 也可以发送到 Home Assistant。在 Home Assistant 中创建一个使用 **Webhook** 触发器的自动化，然后把它的地址填入规则的 Webhook，例如 `http://homeassistant.local:8123/api/webhook/larisvms-front-door`。

Webhook 发送的 JSON 包含规则、消息和时间；如果警报包含这些信息，还会包含事件、摄像头、节点、检测到的物体、速度和 `snapshotPath`。把 `snapshotPath` 加在你的 LarisVMS 地址后面，即可用 API 密钥获取检测图像。

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

## 如果缺少内容

- **未连接：** 用 **测试连接** 检查地址、端口和登录信息。要求 TLS 的代理通常使用 `8883` 端口。
- **Home Assistant 中没有设备：** 检查 MQTT 集成是否已设置，以及发现前缀是否与 Home Assistant 的一致（除非你改过，否则为 `homeassistant`）。
- **缺少某个摄像头：** 角色无法查看它，或者该摄像头已关闭。
- **没有录像开关或书签按钮：** 角色缺少相应的权限或摄像头访问权限。
- **收不到 Webhook：** LarisVMS 不会向 `localhost` 发送 Webhook。即使 Home Assistant 运行在同一台机器上，也请使用它的网络地址。
