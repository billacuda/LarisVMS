# Home Assistant

카메라, 감지, 녹화 노드를 Home Assistant에 표시하고, Home Assistant에서 녹화를 시작하거나 북마크를 추가할 수 있습니다.

> **실험적 기능.** Home Assistant 연동은 새로운 기능이며, 사람마다 Home Assistant를 쓰는 방식이 매우 다양합니다. [사용해 본 결과를 알려 주세요](https://github.com/billacuda/LarisVMS/discussions/new?category=general&title=Home%20Assistant%20feedback). 어디에 쓰는지, 잘 되는 점, 문제가 된 점을 알려 주시면 됩니다.

## 작동 방식

LarisVMS는 Home Assistant가 이미 사용하는 MQTT 브로커로 보냅니다. 그러면 Home Assistant의 MQTT 검색이 카메라와 녹화 노드마다 기기를 추가합니다. YAML을 작성할 필요가 없습니다.

Home Assistant는 API 키와 마찬가지로 선택한 역할로 동작합니다.

- **설정 → 카메라 접근**에서 그 역할이 볼 수 있는 카메라만 받습니다.
- 그 역할에 허용된 작업만 할 수 있습니다. 예를 들어 녹화 스위치는 역할에 **수동 녹화 시작 및 중지**와 **녹화** 카메라 접근이 있을 때만 나타납니다.
- Home Assistant가 한 작업은 감사 로그에 **Home Assistant**로 기록됩니다.

## 설정

1. 아직 사용하지 않는다면 Home Assistant에서 **Mosquitto broker** 애드온과 **MQTT** 통합을 설치하세요. `larisvms`라는 Home Assistant 사용자처럼 LarisVMS용 브로커 로그인을 만드세요.
2. LarisVMS의 **설정 → 권한 → 역할**에서 Home Assistant용 역할을 만드세요. **카메라 보기**를 주고, **설정 → 카메라 접근**에서 원하는 카메라에 대한 접근을 주세요. Home Assistant에서 녹화하거나 북마크를 추가하려면 **수동 녹화 시작 및 중지**, 또는 **녹화된 영상 재생**과 **녹화된 영상에 북마크 만들기**를 추가하세요.
3. **설정 → Home Assistant**를 열고 켜세요. 브로커 주소(애드온을 쓰는 경우 Home Assistant 주소), 포트(보통 `1883`), 로그인, 역할을 입력하세요. **연결 테스트**를 선택한 다음 **저장**을 선택하세요.
4. Home Assistant에서 **설정 → 기기 및 서비스 → MQTT**를 여세요. 카메라가 기기로 나타납니다.

**설정 → Home Assistant**의 상태에는 LarisVMS가 연결되어 있는지와 보내고 있는 카메라 수가 표시됩니다. 브로커가 다시 시작되거나 네트워크가 끊겨도 LarisVMS가 알아서 다시 연결합니다. Home Assistant가 다시 시작되면 LarisVMS가 모든 것을 다시 보냅니다.

## Home Assistant에 나타나는 항목

각 카메라에는 다음이 있습니다.

| 엔터티 | 표시 내용 |
|---|---|
| **움직임** | 움직임이나 감지가 있는 동안 켜짐 |
| **사람**, **차량**, **동물** | 그 종류의 물체가 감지되는 동안 켜짐 |
| **온라인** | 카메라가 영상을 보내고 있는지 여부 |
| **최근 감지 이미지** | 마지막으로 감지된 물체의 사진 |
| **마지막 감지**, **마지막 이벤트** | 마지막으로 감지된 것과 그 시각 |
| **마지막 속도** | [속도 트랩](/Help/ai-detection)에서 마지막으로 측정한 속도(**설정 → AI 감지**의 단위) |
| **수동 녹화** | 역할이 허용하면 수동 녹화를 시작하고 중지하는 스위치 |
| **북마크** | 역할이 허용하면 현재 시각에 북마크를 추가하는 버튼 |

각 녹화 노드에는 **온라인**과 **여유 공간**이 있습니다.

센서는 감지 후 1~2초 안에 켜지고, 감지가 끝나고 약 25초 뒤에 꺼집니다. **라이브**의 움직임 배지와 같습니다.

## 라이브 영상

Home Assistant의 **Generic Camera**로 LarisVMS의 라이브 영상을 볼 수 있습니다. 웹 페이지와 같은 스트림을 쓰므로 카메라에 두 번째 스트림을 요청하지 않습니다.

1. Home Assistant 역할에 **다른 프로그램으로 라이브 영상 스트리밍** 권한을 주세요. 부여하기 전까지 어떤 역할에도 이 권한이 없습니다.
2. **관리 → API 키**에서 그 역할에 연결된 키를 추가하세요.
3. Home Assistant에서 카메라마다 **Generic Camera**를 추가하세요. **설정 → Home Assistant**에 각 카메라의 **정지 이미지 URL**과 **스트림 소스**가 나와 있습니다. 인증으로 **Basic**을 선택하고, 사용자 이름에 `apikey`, 비밀번호에 키를 입력하세요.

스트림은 [API](/Help/api)에서 설명합니다.

## 웹훅 알림

알림 규칙의 웹훅으로 Home Assistant에 보낼 수도 있습니다. Home Assistant에서 **Webhook** 트리거를 쓰는 자동화를 만들고, 그 주소를 규칙의 웹훅에 넣으세요. 예: `http://homeassistant.local:8123/api/webhook/larisvms-front-door`

웹훅은 규칙, 메시지, 시각이 담긴 JSON을 보내며, 알림에 있으면 이벤트, 카메라, 노드, 감지된 물체, 속도, `snapshotPath`도 보냅니다. LarisVMS 주소에 `snapshotPath`를 붙이면 API 키로 감지 이미지를 받을 수 있습니다.

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

## 무언가가 보이지 않을 때

- **연결되지 않음:** **연결 테스트**로 주소, 포트, 로그인을 확인하세요. TLS가 필요한 브로커는 보통 포트 `8883`을 씁니다.
- **Home Assistant에 기기가 없음:** MQTT 통합이 설정되어 있는지, 검색 접두사가 Home Assistant와 같은지(바꾸지 않았다면 `homeassistant`) 확인하세요.
- **카메라가 없음:** 역할이 그 카메라를 볼 수 없거나 카메라가 꺼져 있습니다.
- **녹화 스위치나 북마크 버튼이 없음:** 역할에 필요한 권한이나 카메라 접근이 없습니다.
- **웹훅이 오지 않음:** LarisVMS는 `localhost`로 웹훅을 보내지 않습니다. 같은 컴퓨터에서 실행 중이어도 Home Assistant의 네트워크 주소를 사용하세요.
