# API

Cada comando de la API REST, con qué hace, qué necesita, y un ejemplo de uso.

Los ejemplos usan valores inventados. Reemplazá `https://vms.example.com` con la dirección de tu servidor, `lvk_EXAMPLEKEY` con tu clave de API, e IDs como `00000000-0000-0000-0000-000000000001` con reales.

## Primeros pasos

1. En **Admin → Claves de API**, elegí **Agregar clave**. Elegí un rol, y copiá la clave cuando se muestra. Solo la ves una vez.
2. Envía la clave en un encabezado `X-Api-Key` en cada solicitud.
3. Llamá a `/api/v1/status` para verificar que la clave funciona y obtener los IDs de tus cámaras.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" https://vms.example.com/api/v1/status
```

Cada cámara en la respuesta tiene un `cameraId`, que los demás comandos usan.

Solo las rutas que empiezan con `/api/v1` aceptan claves de API. Las demás rutas son internas de la interfaz web y pueden cambiar sin aviso.

## Claves y permisos

Podés tener tantas claves como quieras, cada una con sus propios permisos. El acceso de una clave viene de tres cosas:

- **Rol:** la clave tiene los permisos del rol al que está vinculada.
- **Límites:** marcá **Limitar esta clave** para permitir solo algunos de esos permisos. Una clave nunca puede hacer más que su rol.
- **Acceso a cámaras:** el acceso a cámaras del rol en **Configuración → Acceso a cámaras** decide qué cámaras puede usar la clave. Por ejemplo, la acción **Grabar** debe estar activada para una cámara antes de que una clave pueda grabarla. Para limitar una clave a ciertas cámaras, vincuiala a un rol con acceso solo a esas cámaras.

Cada comando abajo enumera el permiso que necesita. Una clave limitada debe tener ese permiso marcado.

Por ejemplo, estas dos claves podrían compartir un rol con acceso amplio:

| Clave | Límites | Puede hacer |
|---|---|---|
| Monitoreo | Ver el tablero de estado de cámaras | Leer `/api/v1/status`, y nada más |
| Botón del taller | Ver cámaras, Iniciar y detener grabación manual | Iniciar, detener y leer grabaciones |

Editá el nombre y los límites de una clave en **Admin → Claves de API**. La clave misma no cambia, así que lo que la usa sigue funcionando. El rol y el vencimiento de una clave no se pueden cambiar; creá una clave nueva en su lugar. **Revocar** detiene una clave enseguida.

Una clave limitada nunca se trata como administradora, aun si su rol es **Super Administrador**.

Las acciones de una clave aparecen en el registro de auditoría como `API key: {name}`.

## Convenciones

- Las solicitudes y respuestas son JSON, salvo donde un comando devuelve una imagen o un archivo.
- Las horas son UTC, en formato ISO 8601, como `2026-11-06T14:30:00Z`.
- Los nombres de campo en JSON están en camelCase.
- **Configuración → Seguridad** también aplica acá. La lista de permitidos de gestión cubre la API. Las imágenes y descargas cuentan como tráfico de vista en vivo y reproducción, así que siguen esa lista de permitidos y, si configuraste uno, el puerto separado.

| Estado | Significado |
|---|---|
| `200` | Funcionó. |
| `400` | La solicitud no es válida, como una hora de fin anterior a la de inicio. |
| `401` | La clave falta, es incorrecta, está revocada o vencida. |
| `403` | A la clave le falta el permiso, o no tiene acceso a una cámara. |
| `404` | Ese elemento no existe. |
| `409` | La cámara no puede hacer eso ahora, como grabar en una cámara deshabilitada, o mover una cámara PTZ que alguien con mayor prioridad está usando. |
| `429` | Demasiadas solicitudes. Esperá y probá de nuevo. Revisá el encabezado `Retry-After`. |
| `502`, `503`, `504` | No se pudo llegar al nodo grabador o no respondió a tiempo. |

## Monitoreo

### GET /health

Si el servidor está arriba. No necesita clave, para uso de balanceadores de carga y monitores de uptime.

```bash
curl https://vms.example.com/health
```

```text
Healthy
```

### GET /api/v1/status

El estado de cada cámara y el estado de cada nodo.

**Permiso:** Ver el tablero de estado de cámaras. Enumera solo cámaras que la clave puede ver.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" https://vms.example.com/api/v1/status
```

```json
{
  "cameras": [
    {
      "cameraId": "00000000-0000-0000-0000-000000000001",
      "cameraName": "Workshop",
      "cameraEnabled": true,
      "nodeName": "Recorder-1",
      "nodeOnline": true,
      "fps": 15,
      "bitrateKbps": 4096,
      "healthFresh": true
    }
  ],
  "nodes": []
}
```

### GET /api/v1/cameras/motion-state

Los IDs de cámaras que ven movimiento ahora.

**Permiso:** Ver cámaras.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" https://vms.example.com/api/v1/cameras/motion-state
```

```json
["00000000-0000-0000-0000-000000000001"]
```

### GET /api/v1/cameras/detection-state

Qué cámaras ven un objeto detectado ahora, y qué es.

**Permiso:** Ver cámaras.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" https://vms.example.com/api/v1/cameras/detection-state
```

```json
[
  {
    "cameraId": "00000000-0000-0000-0000-000000000001",
    "detections": [
      { "kind": "Human", "label": "Person", "emoji": "🧍", "colorHex": "#ff9f1c" }
    ]
  }
]
```

## Grabación

Iniciar y detener grabación manual, que conserva grabaciones sea cual sea el modo de grabación de la cámara. Una grabación corre hasta que algo la detiene, incluidos reinicios y actualizaciones. Ver [Grabación](/Help/recording#manual-recording).

Iniciar una cámara que ya está grabando, o detener una que no lo está, es seguro y no cambia nada.

Los comandos de inicio y fin devuelven qué cámaras `changed`, cuáles quedaron `unchanged`, y cuáles fueron `skipped` porque están deshabilitadas o no existen.

### POST /api/v1/cameras/{cameraId}/recording/start

Inicia la grabación de una cámara. Devuelve `409` si la cámara está deshabilitada o no existe.

**Permiso:** Iniciar y detener grabación manual, más acceso a cámara **Grabar**.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/recording/start
```

```json
{
  "changed": ["00000000-0000-0000-0000-000000000001"],
  "unchanged": [],
  "skipped": []
}
```

### POST /api/v1/cameras/{cameraId}/recording/stop

Detiene la grabación de una cámara.

**Permiso:** Iniciar y detener grabación manual, más acceso a cámara **Grabar**.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/recording/stop
```

### POST /api/v1/recording/start

Inicia la grabación de varias cámaras. Devuelve `403`, y no cambia nada, si a la clave le falta acceso a alguna.

**Permiso:** Iniciar y detener grabación manual, más acceso a cámara **Grabar**.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraIds":["00000000-0000-0000-0000-000000000001","00000000-0000-0000-0000-000000000002"]}' \
  https://vms.example.com/api/v1/recording/start
```

### POST /api/v1/recording/stop

Detiene la grabación de varias cámaras.

**Permiso:** Iniciar y detener grabación manual, más acceso a cámara **Grabar**.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraIds":["00000000-0000-0000-0000-000000000001","00000000-0000-0000-0000-000000000002"]}' \
  https://vms.example.com/api/v1/recording/stop
```

### GET /api/v1/recording

Qué cámaras están conservando grabaciones ahora. Las cámaras sin nada en curso no se listan.

`keeping` significa que se están guardando grabaciones en este momento, por cualquier motivo. `manual` significa que hay una grabación manual en curso. Una cámara sin conexión con una grabación manual en curso muestra `manual` sin `keeping`.

**Permiso:** Ver cámaras.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" https://vms.example.com/api/v1/recording
```

```json
[
  {
    "cameraId": "00000000-0000-0000-0000-000000000001",
    "keeping": true,
    "manual": true,
    "manualStartedUtc": "2026-11-06T14:30:00Z",
    "manualStartedBy": "API key: Workshop button"
  }
]
```

## Imágenes

### GET /api/v1/cameras/{cameraId}/snapshot

Un JPEG tomado de la cámara ahora. Esto abre una conexión corta con la cámara, así que usá la última miniatura abajo si estás verificando seguido.

**Permiso:** Agregar, editar y eliminar cámaras, más acceso a cámara **Configurar**.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" -o snapshot.jpg \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/snapshot
```

### GET /api/v1/cameras/{cameraId}/thumbnail/latest

Una imagen de las grabaciones guardadas más recientes de la cámara. Una cámara sin grabaciones guardadas no tiene miniatura.

**Permiso:** Ver cámaras.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" -o latest.jpg \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/thumbnail/latest
```

## PTZ

### POST /api/v1/cameras/{cameraId}/ptz/move

Inicia el movimiento de la cámara. Los valores van de `-1` a `1`, donde `0` es sin movimiento. La cámara sigue moviéndose hasta que llamás a detener.

| Campo | Significado |
|---|---|
| `panX` | Izquierda (negativo) o derecha (positivo). |
| `tiltY` | Abajo (negativo) o arriba (positivo). |
| `zoomX` | Afuera (negativo) o adentro (positivo). |

**Permiso:** Ver cámaras, más acceso a cámara **PTZ**.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"panX":0.5,"tiltY":0,"zoomX":0}' \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/ptz/move
```

### POST /api/v1/cameras/{cameraId}/ptz/stop

Detiene el movimiento de la cámara.

**Permiso:** Ver cámaras, más acceso a cámara **PTZ**.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/ptz/stop
```

## Datos de reproducción

### GET /api/v1/cameras/{cameraId}/timeline

Un resumen de un rango de tiempo, dividido en segmentos de igual tamaño: si cada uno tiene grabaciones o movimiento, y los colores de cualquier evento.

| Parámetro | Significado |
|---|---|
| `from`, `to` | El rango de tiempo. Obligatorio. |
| `buckets` | Cuántos segmentos. Predeterminado 200. |

**Permiso:** Reproducir grabaciones, más acceso a cámara **Reproducción**.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" \
  "https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/timeline?from=2026-11-06T00:00:00Z&to=2026-11-07T00:00:00Z&buckets=24"
```

```json
[
  {
    "startUtc": "2026-11-06T00:00:00Z",
    "endUtc": "2026-11-06T01:00:00Z",
    "hasRecording": true,
    "hasMotion": false,
    "tagColorHex": "#e5383b",
    "tagColorHexes": ["#e5383b"]
  }
]
```

### GET /api/v1/cameras/{cameraId}/segments

Los archivos de grabaciones guardadas en un rango de tiempo.

| Parámetro | Significado |
|---|---|
| `from`, `to` | El rango de tiempo. Obligatorio. |

**Permiso:** Reproducir grabaciones, más acceso a cámara **Reproducción**.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" \
  "https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/segments?from=2026-11-06T14:00:00Z&to=2026-11-06T15:00:00Z"
```

```json
[
  {
    "id": 1234,
    "startUtc": "2026-11-06T14:30:00Z",
    "endUtc": "2026-11-06T14:31:00Z",
    "isArchived": false
  }
]
```

### GET /api/v1/cameras/{cameraId}/activity

Cuándo suelen pasar eventos en esta cámara, por hora del día.

| Parámetro | Significado |
|---|---|
| `range` | Qué tan atrás, como `7d` (el predeterminado) o `30d`. |
| `view` | `calendar` lo muestra por día, para 30 días o más. |
| `sel` | Un tipo de evento o etiqueta para incluir, como `Animal` o `Animal:cat`. Repetir para varios. |

**Permiso:** Reproducir grabaciones, más acceso a cámara **Reproducción**.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" \
  "https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/activity?range=30d"
```

## Marcadores

### GET /api/v1/cameras/{cameraId}/bookmarks

Los marcadores de una cámara en un rango de tiempo.

| Parámetro | Significado |
|---|---|
| `from`, `to` | El rango de tiempo. Obligatorio. |

**Permiso:** Reproducir grabaciones, más acceso a cámara **Reproducción**.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" \
  "https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/bookmarks?from=2026-11-06T00:00:00Z&to=2026-11-07T00:00:00Z"
```

```json
[
  {
    "id": "00000000-0000-0000-0000-00000000000b",
    "cameraId": "00000000-0000-0000-0000-000000000001",
    "cameraName": "Workshop",
    "timestampUtc": "2026-11-06T14:30:00Z",
    "note": "Print failed here",
    "createdByUserName": "API key: Workshop button",
    "createdAt": "2026-11-06T14:35:00Z"
  }
]
```

### POST /api/v1/bookmarks

Agrega un marcador en un momento de las grabaciones de una cámara.

**Permiso:** Reproducir grabaciones, y Crear marcadores en grabaciones, más acceso a cámara **Reproducción**.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraId":"00000000-0000-0000-0000-000000000001","timestampUtc":"2026-11-06T14:30:00Z","note":"Print failed here"}' \
  https://vms.example.com/api/v1/bookmarks
```

```json
{ "id": "00000000-0000-0000-0000-00000000000b" }
```

## Exportaciones

Una exportación guarda grabaciones de una o más cámaras como un archivo para cada cámara. Las exportaciones corren en segundo plano: creá una, verificala, y descargá cada archivo cuando termina.

### POST /api/v1/exports

Inicia una exportación. Limitada a 20 solicitudes por minuto por clave.

**Permiso:** Crear y descargar exportaciones, más acceso a cámara **Exportar**.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraIds":["00000000-0000-0000-0000-000000000001"],"fromUtc":"2026-11-06T14:00:00Z","toUtc":"2026-11-06T14:30:00Z"}' \
  https://vms.example.com/api/v1/exports
```

```json
{ "jobId": "00000000-0000-0000-0000-00000000000e" }
```

### GET /api/v1/exports

Enumera exportaciones y su progreso. Cada exportación tiene `items`, uno por cada cámara. El `id` de un elemento es lo que usás para descargarlo o reintentarlo.

**Permiso:** Crear y descargar exportaciones.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" https://vms.example.com/api/v1/exports
```

```json
[
  {
    "id": "00000000-0000-0000-0000-00000000000e",
    "createdUtc": "2026-11-06T14:40:00Z",
    "requestedByUserName": "API key: Workshop button",
    "fromUtc": "2026-11-06T14:00:00Z",
    "toUtc": "2026-11-06T14:30:00Z",
    "status": "Completed",
    "items": [
      {
        "id": "00000000-0000-0000-0000-00000000000f",
        "cameraId": "00000000-0000-0000-0000-000000000001",
        "cameraName": "Workshop",
        "nodeName": "Recorder-1",
        "status": "Completed",
        "errorMessage": null,
        "outputSizeBytes": 104857600,
        "redacted": false
      }
    ]
  }
]
```

### GET /api/v1/exports/items/{exportItemId}/download

Descarga un archivo de exportación terminado.

**Permiso:** Crear y descargar exportaciones, más acceso a cámara **Exportar**.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" -o workshop.mp4 \
  https://vms.example.com/api/v1/exports/items/00000000-0000-0000-0000-00000000000f/download
```

### POST /api/v1/exports/{itemId}/retry

Reintenta un elemento de exportación fallido.

**Permiso:** Crear y descargar exportaciones.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/exports/00000000-0000-0000-0000-00000000000f/retry
```

### DELETE /api/v1/exports/{jobId}

Elimina una exportación y sus archivos. Una clave puede eliminar exportaciones que creó. Solo un administrador puede eliminar las de otros.

**Permiso:** Crear y descargar exportaciones.

```bash
curl -X DELETE -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/exports/00000000-0000-0000-0000-00000000000e
```

## Bloqueos de evidencia

Un bloqueo de evidencia evita que las grabaciones se eliminen. Ver [Bloqueos de evidencia](/Help/live-and-playback#evidence-locks).

### GET /api/v1/evidence-locks

Enumera bloqueos.

| Parámetro | Significado |
|---|---|
| `cameraId` | Solo esta cámara. Repetir para varias. |
| `fromUtc`, `toUtc` | Solo bloqueos en grabaciones de este rango de tiempo. |
| `includeInactive` | `true` incluye bloqueos que terminaron. |

**Permiso:** Reproducir grabaciones, más acceso a cámara **Reproducción**.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" \
  "https://vms.example.com/api/v1/evidence-locks?cameraId=00000000-0000-0000-0000-000000000001"
```

### POST /api/v1/evidence-locks

Bloquea grabaciones en una o más cámaras.

| Campo | Significado |
|---|---|
| `cameraIds` | Las cámaras. |
| `fromUtc`, `toUtc` | Las grabaciones a bloquear. |
| `duration` | `1d`, `3d`, `7d`, `14d`, `30d`, `custom` o `indefinite`. |
| `customUntilUtc` | Cuándo termina un bloqueo `custom`. |
| `reason` | Por qué está bloqueado. |

**Permiso:** Reproducir grabaciones, y Bloquear grabaciones para que la retención no pueda eliminarlas, más acceso a cámara **Reproducción**.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraIds":["00000000-0000-0000-0000-000000000001"],"fromUtc":"2026-11-06T14:00:00Z","toUtc":"2026-11-06T14:30:00Z","duration":"30d","reason":"Incident 1042"}' \
  https://vms.example.com/api/v1/evidence-locks
```

### POST /api/v1/evidence-locks/{id}/remove

Quita un bloqueo. Se requiere un motivo.

**Permiso:** Reproducir grabaciones, y las mismas reglas que quitar un bloqueo en la interfaz web.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"reason":"Case closed"}' \
  https://vms.example.com/api/v1/evidence-locks/00000000-0000-0000-0000-00000000000c/remove
```

### POST /api/v1/evidence-locks/{id}/restore

Restaura un bloqueo quitado durante su período de gracia.

**Permiso:** Reproducir grabaciones, y las mismas reglas que restaurar un bloqueo en la interfaz web.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/evidence-locks/00000000-0000-0000-0000-00000000000c/restore
```

## No está en la API

Esto no está disponible para claves de API:

- Video en vivo. Usá la interfaz web, o las imágenes de arriba.
- Reproducir grabaciones directamente. Usá una exportación.
- Configuración, usuarios, roles, claves de API, cifrado y respaldos.
- Configuración de cámaras, como zonas, horarios y configuración del dispositivo.
