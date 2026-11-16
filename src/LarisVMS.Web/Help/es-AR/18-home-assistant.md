# Home Assistant

Mostrá tus cámaras, detecciones y nodos de grabación en Home Assistant, e iniciá grabaciones o agregá marcadores desde ahí.

> **Experimental.** La integración con Home Assistant es nueva, y la gente usa Home Assistant para muchas cosas distintas. [Contanos cómo te funciona](https://github.com/billacuda/LarisVMS/discussions/new?category=general&title=Home%20Assistant%20feedback): para qué la usás, qué anda bien y qué problemas tenés.

## Cómo funciona

LarisVMS envía al broker MQTT que Home Assistant ya usa. Después, el descubrimiento MQTT de Home Assistant agrega un dispositivo por cada cámara y cada nodo de grabación, sin escribir YAML.

Home Assistant actúa con un rol que elegís, igual que una clave de API:

- Solo recibe las cámaras que ese rol puede ver en **Configuración → Acceso a cámaras**.
- Solo puede hacer lo que ese rol tiene permitido. Por ejemplo, el interruptor de grabación solo aparece si el rol tiene **Iniciar y detener grabación manual** y el acceso a cámara **Grabar**.
- Lo que hace aparece en el registro de auditoría como **Home Assistant**.

## Configuración

1. En Home Assistant, instalá el complemento **Mosquitto broker** y la integración **MQTT**, si todavía no los usás. Creá un inicio de sesión del broker para LarisVMS, como un usuario de Home Assistant llamado `larisvms`.
2. En LarisVMS, creá un rol para Home Assistant en **Configuración → Permisos → Roles**. Dale **Ver cámaras**, y dale acceso a las cámaras que quieras en **Configuración → Acceso a cámaras**. Agregá **Iniciar y detener grabación manual**, o **Reproducir grabaciones** y **Crear marcadores en grabaciones**, si Home Assistant tiene que poder grabar o agregar marcadores.
3. Abrí **Configuración → Home Assistant** y activalo. Ingresá la dirección del broker (la de tu Home Assistant si usás el complemento), el puerto (normalmente `1883`), el inicio de sesión y el rol. Elegí **Probar la conexión** y después **Guardar**.
4. En Home Assistant, abrí **Configuración → Dispositivos y servicios → MQTT**. Tus cámaras aparecen ahí como dispositivos.

El estado en **Configuración → Home Assistant** muestra si LarisVMS está conectado y cuántas cámaras envía. Si el broker se reinicia o se cae la red, LarisVMS se vuelve a conectar solo. Cuando Home Assistant se reinicia, LarisVMS vuelve a enviar todo.

## Qué aparece en Home Assistant

Cada cámara recibe:

| Entidad | Muestra |
|---|---|
| **Movimiento** | Activo mientras hay movimiento o una detección |
| **Persona**, **Vehículo**, **Animal** | Activo mientras se detecta ese tipo de objeto |
| **En línea** | Si la cámara está enviando video |
| **Detección más reciente** | Una imagen del último objeto detectado |
| **Última detección**, **Último evento** | Qué se detectó por última vez, y cuándo |
| **Última velocidad** | La última velocidad medida por las [trampas de velocidad](/Help/ai-detection), en las unidades de **Configuración → Detección de IA** |
| **Grabación manual** | Un interruptor que inicia y detiene una grabación manual, si el rol lo permite |
| **Agregar marcador** | Un botón que agrega un marcador en la hora actual, si el rol lo permite |

Cada nodo de grabación recibe **En línea** y **Almacenamiento libre**.

Un sensor se activa uno o dos segundos después de una detección, y se desactiva unos 25 segundos después de que termina. Es lo mismo que el indicador de movimiento en **En vivo**.

## Video en vivo

La **Generic Camera** de Home Assistant puede mostrar video en vivo de LarisVMS. Usa el mismo stream que las páginas web, así que la cámara no tiene que enviar otro.

1. Dale al rol de Home Assistant el permiso **Transmitir video en vivo a otros programas**. Ningún rol lo tiene hasta que se lo des.
2. En **Admin → Claves de API**, agregá una clave vinculada a ese rol.
3. En Home Assistant, agregá una **Generic Camera** por cada cámara. **Configuración → Home Assistant** lista la **URL de imagen fija** y el **Origen del stream** de cada cámara. Elegí la autenticación **Basic**, ingresá `apikey` como usuario y la clave como contraseña.

La [API](/Help/api) describe el stream.

## Alertas por webhook

El webhook de una regla de alerta también puede enviar a Home Assistant. En Home Assistant, creá una automatización con un disparador **Webhook**, y poné su dirección en el webhook de la regla, por ejemplo `http://homeassistant.local:8123/api/webhook/larisvms-front-door`.

El webhook envía JSON con la regla, el mensaje y la hora, y, si la alerta los tiene, el evento, la cámara, el nodo, el objeto detectado, la velocidad y `snapshotPath`. Agregá `snapshotPath` a tu dirección de LarisVMS para obtener la imagen de la detección con una clave de API.

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

## Si falta algo

- **Sin conexión:** revisá la dirección, el puerto y el inicio de sesión con **Probar la conexión**. Un broker que exige TLS suele usar el puerto `8883`.
- **No hay dispositivos en Home Assistant:** revisá que la integración MQTT esté configurada, y que el prefijo de descubrimiento coincida con el de Home Assistant (`homeassistant` salvo que lo hayas cambiado).
- **Falta una cámara:** el rol no puede verla, o la cámara está desactivada.
- **No hay interruptor de grabación ni botón de marcador:** al rol le falta el permiso o el acceso a cámara para eso.
- **El webhook no llega:** LarisVMS no envía webhooks a `localhost`, así que usá la dirección de red de Home Assistant, aunque esté en el mismo equipo.
