# Resumen

Cómo encaja LarisVMS: el servidor web, los nodos grabadores, los proxies de medios y las cámaras.

## Las piezas

| Pieza | Qué hace |
|---|---|
| **Servidor web** | La app web de LarisVMS. Guarda la configuración en SQL Server o MariaDB, sirve la interfaz y la API, y retransmite el video en vivo y de reproducción, salvo que el streaming directo esté activado. Corre como el servicio de Windows *LarisVMS Web*. |
| **Nodo grabador** | Un servicio de Windows en cada máquina que graba. Toma el RTSP de sus cámaras, escribe los segmentos en su ruta de almacenamiento, aplica la retención, corre la detección de movimiento y de IA, e informa su estado. Un servidor puede tener muchos nodos. |
| **Servicio de visión** | Proceso hijo opcional de un nodo que corre la detección de objetos por IA en la GPU del nodo (o en la CPU). Si se cae, la grabación nunca se interrumpe. |
| **Proxy de medios** | Relé opcional entre los navegadores y los nodos, para sitios remotos o enlaces de bajo ancho de banda. |
| **Cámaras** | Cámaras ONVIF. Cada una está asignada a un nodo grabador. |

## Cómo fluye el video

1. Un nodo abre una sesión RTSP con cada una de sus cámaras y graba con `ffmpeg -c copy`, así que no hay re-codificación.
2. Las grabaciones se escriben como archivos de segmentos cortos (ver [Grabación](/Help/recording)) en la ruta de almacenamiento del nodo.
3. La vista en vivo y la reproducción las sirve el nodo y se retransmiten a través de este servidor, de un proxy de medios o directo al navegador (ver [Nodos](/Help/nodes#direct-streaming)).

## Privacidad

LarisVMS es autohospedado: tu video y tu configuración quedan en tus propias máquinas, y nada de tu instalación se recolecta ni se comparte. No hay telemetría ni llamadas externas. La única solicitud saliente de rutina es una verificación diaria de las versiones de LarisVMS en GitHub para ver si hay una nueva (ver [Actualizaciones](/Help/installation#updates)); se puede desactivar. La otra descarga integrada es un modelo de IA YOLOX desde las versiones de GitHub de ese proyecto, la primera vez que una detección lo usa, salvo que pongas los archivos en el servidor vos mismo. Los mapas callejeros de la página [Mapa](/Help/live-and-playback#map) cargan sus imágenes de mapa en tu navegador desde OpenStreetMap, o desde el servidor de tiles que elijas; los planos de planta, no. Todo lo demás solo va a tus cámaras, a tus propios servidores y a los servicios que vos configures, como el correo y los canales de alerta.

## Herencia de configuración

La mayoría de las configuraciones existen en tres niveles. Gana la más específica:

1. **Cámara**: en la página de edición de la propia cámara.
2. **Nodo**: en la página de edición del nodo, en **Nodos**.
3. **Global**: en la página de **Configuración** correspondiente.

Un campo vacío (o una opción de «heredar — …») usa el nivel siguiente y muestra el valor que está heredando en ese momento.

## Tablero

El tablero está hecho de widgets. Cada usuario elige los suyos, y el diseño se guarda en su cuenta, así que lo sigue en otros navegadores.

- **Personalizar** (arriba a la derecha; se oculta cuando la ventana es demasiado angosta para la grilla completa) desbloquea el diseño. Arrastrá un widget desde su barra de título, cambiale el tamaño desde la esquina o quitálo con ×. **Agregar widget** enumera todo lo que podés agregar, y **Restablecer diseño** vuelve al predeterminado. Hacé clic en **Listo** cuando termines.
- Mientras personalizás, cada widget tiene botones para moverlo y cambiarle el tamaño sin arrastrar.
- **Pausar actualizaciones** detiene los cambios de los widgets mientras los leés.
- La mayoría de los widgets son listas top-10 de cámaras o nodos, como espacio de almacenamiento usado, velocidad de cuadros, ancho de banda, grabaciones más antiguas, cámaras sin conexión, tiempo desde la última grabación y detecciones. Algunos tienen una ventana de tiempo (1 h, 24 h, 7 d, 30 d), y algunos pueden alternar entre mayor y menor primero.
- Hacé clic en el título de un widget para abrir su informe completo: todas las cámaras o nodos, 100 por página, con un cuadro de filtro, un filtro por nodo, columnas ordenables y una descarga CSV.
- **Actividad de la cámara** muestra los últimos 7 días de una cámara como un mapa de calor por hora del día; elegí la cámara en su barra de título y hacé clic en el título para ver la página completa de [Actividad](/Help/live-and-playback#activity).
- **Últimas detecciones** muestra las capturas más nuevas. Su botón ⚙ elige qué tipos de detección incluir; el movimiento simple (nada identificado) está desactivado salvo que lo marques.
- **Tiempo desde la última grabación** marca una cámara configurada en grabación Continua que no grabó en más de 5 minutos.
- Los widgets de nodos basados en cámaras (cantidad de cámaras, cámaras sin conexión, velocidad de cuadros, tiempo de almacenamiento restante) dejan fuera los nodos que no tienen cámaras.
- Solo ves las cámaras a las que tenés acceso. **Alertas recientes** solo se ofrece a los usuarios que pueden gestionar reglas de alerta.

Los widgets se actualizan cada minuto.

## En el teléfono

Todo el sitio funciona en el navegador de un teléfono. La barra lateral se abre desde el botón ☰ arriba a la izquierda.

Para instalarlo como app, abrí el sitio en Chrome en Android y elegí **Instalar app** desde el menú ⋮. En iOS, abrilo en Safari y elegí **Compartir → Agregar a la pantalla de inicio**. La app se abre a pantalla completa, sin la barra de direcciones del navegador.

Instalarlo necesita un certificado en el que el teléfono confíe. Los navegadores no instalan un sitio que usa el certificado autofirmado con el que arranca una instalación nueva; ver [Certificados](/Help/security#certificates).

## Por dónde empezar

- Instalación nueva: [Instalación](/Help/installation)
- Agregar cámaras: [Cámaras](/Help/cameras)
- Elegir qué conservar: [Grabación](/Help/recording) y [Almacenamiento y retención](/Help/storage)
- Conectar tus propias herramientas: [API](/Help/api)
- Usar otro idioma: [Idiomas](/Help/languages)
- Teclado y lectores de pantalla: [Accesibilidad](/Help/accessibility)
- Algo anda mal: [Solución de problemas](/Help/troubleshooting)
