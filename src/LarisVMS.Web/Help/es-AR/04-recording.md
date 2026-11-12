# Grabación

Modos de grabación, horarios, etiquetas de eventos, pre/post-roll y duración de segmentos.

## Modos

Cada cámara graba continuamente en su nodo. El **modo de grabación** decide qué grabaciones se *conservan*:

| Modo | Conserva |
|---|---|
| **Continuo** | Todo. |
| **Movimiento** | Grabaciones alrededor de movimiento: desde una zona de movimiento, los eventos de movimiento propios de la cámara, una integración o la detección de IA (ver [Movimiento](/Help/motion#source)). |
| **Horario** | Grabaciones que empiezan dentro de una ventana de horario habilitada. |
| **Evento** | Grabaciones alrededor de una regla de etiqueta de evento que tenga *Impulsa grabación* activado. |
| **Solo manual** | Nada, salvo que haya una [grabación manual](#manual-recording) en curso. El resto del tiempo la cámara es solo vista en vivo. |

Las grabaciones que no se conservan se eliminan poco después de escribirse, en lugar de esperar a la retención.

Los modos Movimiento, Horario y Evento **fallan abiertos**. Hasta que una cámara tiene una zona de movimiento, una ventana de horario o una regla que impulsa, conserva todo en lugar de descartarlo. La página de la cámara avisa cuando esto está pasando. Solo manual no falla abierto, porque conservar nada es para lo que está.

Configurá el modo global en **Configuración → Valores predeterminados de grabación**, y anulalo en la página de una cámara.

## Grabación manual

Una grabación manual conserva las grabaciones de una cámara hasta que alguien la detiene, sea cual sea el modo de grabación de la cámara. Iniciá una con el botón de grabación en un [mosaico en vivo](/Help/live-and-playback#live-tiles) o a través de la [API](/Help/api#recording). Queda activa en reinicios y actualizaciones.

Funciona junto con el modo de la cámara. Una cámara en Movimiento conserva grabaciones cuando ve movimiento *o* cuando hay una grabación manual en curso, y los eventos igual etiquetan la línea de tiempo como siempre. En una cámara Continua, solo marca la línea de tiempo.

- Las grabaciones se conservan en segmentos completos, así que también se guarda hasta un segmento antes y después de la grabación. El pre-roll y el post-roll no aplican.
- Las grabaciones guardadas siguen la retención normal de la cámara. Agregá un [bloqueo de evidencia](/Help/live-and-playback#evidence-locks) para conservarlas más tiempo.
- Se muestra en la línea de tiempo como una banda roja. Cambiá el color en **Configuración → Eventos**.
- Una cámara sin conexión no guarda nada hasta que vuelve. La grabación queda en curso.
- Actualizá los nodos grabadores junto con el servidor. Un nodo anterior no conoce las grabaciones manuales.

## Pre-roll y post-roll

En los modos Movimiento y Evento, el **pre-roll** es cuánto de grabación anterior a la actividad se conserva (así ves a alguien entrar), y el **post-roll** es cuánto después de que termina.

## Duración de segmentos

Las grabaciones se escriben como archivos de duración fija (60 segundos por defecto). La reproducción tiene que descargar parte de un segmento antes de poder empezar en el momento exacto que elegís. Los segmentos más cortos hacen que buscar sea más rápido en cámaras de alta resolución, pero crean más archivos. Cambiar la duración reinicia brevemente la grabación de la cámara.

## Horario

La página **Horario** de una cámara guarda sus ventanas: días, una hora de inicio y una hora de fin. Una hora de fin anterior a la de inicio cruza la medianoche. Las horas usan el **reloj local del nodo grabador**. Las ventanas solo importan en el modo Horario.

## Etiquetas de eventos

La página **Etiquetas de eventos** de una cámara convierte los eventos ONVIF de la cámara (movimiento, alarmas, analíticas, manipulación) en marcas de color en la línea de tiempo.

- **Tema de inicio**: el evento que inicia la etiqueta. Elegí uno de *Temas observados*, que enumera todo lo que la cámara realmente envió.
- **Tema de fin**: opcional. Dejalo vacío cuando el tema de inicio trae su propio estado verdadero/falso (la mayoría de los temas de movimiento y alarma lo hacen). Configuralo cuando la cámara envía eventos separados de inicio y fin.
- **Impulsa grabación**: en el modo Evento o Movimiento, se conservan las grabaciones alrededor de esta etiqueta.

Una etiqueta termina solo cuando llega su evento de fin, nunca por un tiempo de espera.
