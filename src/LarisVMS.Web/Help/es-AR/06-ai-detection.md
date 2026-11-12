# Detección de IA

Detección de objetos en el nodo: modelos, hardware, umbrales, capturas y el backend externo.

Cada nodo puede correr detección de objetos en tiempo real en sus cámaras. Los objetos detectados reciben cajas delimitadoras en vivo, una marca de color en la línea de tiempo, una insignia en el mosaico en vivo y una captura recortada. Una detección también cuenta como movimiento para la grabación en modo Movimiento. La detección corre en un proceso separado, así que un problema de GPU o de driver nunca detiene la grabación.

Activala por cámara con **Vigilar esta cámara por objetos**. Solo hacen falta **5–7 fps por cámara** para una detección precisa, así que limitá ahí la [velocidad de cuadros](#thresholds) para mantener baja la carga de la GPU. Los valores predeterminados globales están en **Configuración → Detección de IA**. Los nodos y las cámaras pueden anular la mayoría de ellos.

## Hardware

Un paquete de nodo cubre cada máquina. Al arrancar, el servicio de visión elige un backend para el hardware que encuentra:

| Hardware | Backend | Necesita en el nodo |
|---|---|---|
| GPU NVIDIA | CUDA (vuelve a DirectML) | CUDA Toolkit 12.x y cuDNN 9.x |
| GPU AMD / Intel | DirectML | Un driver de GPU actual |
| Sin GPU | CPU | Nada |

La página de edición del nodo muestra los aceleradores detectados, y su configuración **Acelerador** puede forzar uno. Los modelos nunca vienen incluidos. Un nodo trae los modelos YOLOX integrados desde el servidor, y los otros modelos van en `C:\ProgramData\LarisVMS\models` en el nodo.

## Backend lento

Si un nodo NVIDIA no puede usar CUDA, la detección de IA vuelve a DirectML, que corre varias veces más lento. Se analizan menos cuadros, la GPU se calienta, y objetos rápidos como autos que pasan pueden no detectarse. La página **Nodos** después muestra **⚠️ IA en DirectML** junto al nodo, con el motivo, y los Super Administradores reciben una notificación.

La causa habitual es un cuDNN faltante. Instalá el CUDA Toolkit 12.x y cuDNN 9.x para CUDA 12 en el nodo, desde la descarga de cuDNN de NVIDIA o con `pip install nvidia-cudnn-cu12`. El nodo los encuentra en 10 minutos, los copia junto a su backend de IA y reinicia la detección en CUDA.

## Familias de modelos

El modelo corre una vez por nodo y sirve a todas las cámaras de ese nodo, así que la configuración del modelo es por nodo, no por cámara.

- **Auto / YOLOX**: el predeterminado. Rápido, no necesita letterboxing, y funciona bien en GPUs de bajo consumo y no NVIDIA. **Tamaño YOLOX** va de Nano (el más rápido) a X (el más preciso). Usá Nano o Tiny en máquinas de bajo consumo y L o X en GPUs potentes.
- **D-FINE**: experimental. Los resultados pueden ser impredecibles, y objetos que quedan a caballo entre dos mosaicos en el modo Slice pueden perderse o duplicarse. **Pesos**: Obj2Coco conoce 80 tipos de objeto (pequeño o mediano), y Obj365 conoce 365.
- **Personalizado**: cualquier modelo `.onnx` que pongas en la carpeta de modelos del nodo. Ver [Modelos personalizados](#custom-models).

Cambiar el modelo reinicia la detección de cada cámara.

## TensorRT

**D-FINE TensorRT** solo hace algo cuando el nodo tiene TensorRT habilitado (`Vision:EnableTensorRt`) y el SDK de TensorRT 10.x instalado. **FP32** da una aceleración segura. **FP16** es el más rápido; si el motor de una cámara se desborda en FP16, se reconstruye automáticamente en FP32. El primer arranque en cada resolución de cámara tarda varios minutos en compilar el motor.

## Modelos personalizados

Poné un archivo `.onnx` en `C:\ProgramData\LarisVMS\models` en el nodo. Si los metadatos propios del modelo describen su decodificador, su tamaño de entrada y sus etiquetas, funciona tal cual. Si no, agregá un archivo `.json` con el mismo nombre que provee lo que falta. **Actualizar** enumera cada modelo que encontraron los nodos, y marca los que tienen metadatos incompletos. Un modelo con una advertencia se puede seleccionar, pero no corre hasta que se corrige la advertencia.

## Ajuste de aspecto

Cómo encaja el cuadro de una cámara en la entrada cuadrada del modelo:

- **Letterbox** (predeterminado): conserva las proporciones y rellena con barras negras.
- **Estirar**: aplasta el cuadro para llenar el cuadrado, lo que distorsiona las formas.
- **Slice**: corta el cuadro en cuadrados superpuestos de resolución completa. Es mejor para objetos pequeños o lejanos en cámaras anchas, pero cuesta unas 2× el trabajo en una cámara 16:9. Necesita preprocesamiento en GPU, y lo activa automáticamente.

El **preprocesamiento de cuadros en GPU** mueve la conversión de color de la CPU a la GPU, lo que ayuda en nodos con muchas cámaras.

## Umbrales

- **Confianza**: el puntaje mínimo para que una detección cuente. Más bajo detecta más objetos pero agrega falsos positivos. Subilo en una cámara que sigue detectando mal algo.
- **IoU**: cuánto deben superponerse dos cajas para unirse como un objeto.
- **Stream de detección**: Sub es más liviano; Principal ve más detalle.
- **Orientación**: algunas cámaras montadas en pasillos reportan un stream apaisado pero envían video en retrato. Configurá **Retrato** en esas cámaras para que la detección y las capturas no se aplasten.
- **Velocidad de cuadros máxima de detección**: cuadros por segundo por cámara que llegan al modelo. **5–7 fps alcanzan para una detección y seguimiento precisos.** Las velocidades más altas agregan carga de GPU sin mejorar los resultados. 0 no es límite.

## Capturas

Cada objeto rastreado recibe una captura, recortada de su mejor cuadro.

- **Tiempo de inactividad**: cuánto tiempo un objeto puede pasar sin verse antes de que su captura se cierre.
- **Reportar objetos inactivos**: también reportar objetos presentes pero sin moverse, como un auto estacionado.
- **Precisión de movimiento de captura**: cierra una captura poco después de que su objeto sale, para que un objeto posterior del mismo tipo no se fusione en ella. **Gracia de salida** es cuánto esperar. Si es demasiado corto, un objeto que se detiene se divide en dos capturas.
- **Margen de recorte de captura**: espacio extra alrededor del objeto, como porcentaje de su caja.

Varios objetos del mismo tipo en una captura se muestran como un conteo (×2). Cada uno también se conserva por separado con sus propios colores y velocidad.

## Colores de objetos

El color de cada objeto rastreado se lee de su cuadro más claro: un color para vehículos, animales y otros objetos, y colores de camisa y pantalón para personas. Las capturas muestran una insignia para cada uno, pintada en ese color, y las secciones **Color**, **Camisa** y **Pantalón** del filtro de Capturas acotan los resultados a los colores que marcás. Las alertas de detección también pueden filtrar por color.

Los colores vienen de la imagen, no del modelo, así que funcionan con cualquier detector. Los colores justo fuera de la caja también se muestrean y se dejan de lado, para que el pasto alrededor de un perro o la ruta alrededor de un auto no ganen. Igual son estimaciones de mejor esfuerzo: las escenas cargadas, los objetos superpuestos y la luz pueden dar el color equivocado. La luz es lo que más importa: la sombra, las luces delanteras y el balance de blancos cambian los colores, y un auto gris claro puede leerse como plateado o blanco. Mientras una cámara está en modo nocturno blanco y negro (IR) no se registra ningún color. Desactivalo con **Colores de objetos** en **Configuración → Detección de IA**.

## Trampas de velocidad

Una trampa de velocidad cronometra objetos entre dos líneas a través de una ruta. En la página **Trampas de velocidad** de la cámara (desde **Cámaras → Editar**), hacé clic en **Nueva trampa de velocidad**, hacé clic en los dos extremos de la línea **A**, después en los dos extremos de la línea **B**, e ingresá la distancia real entre las líneas, medida a lo largo de la ruta. Cada objeto rastreado que cruza ambas líneas recibe una velocidad, mostrada en su caja en vivo y en su captura. **Dirección** cronometra en ambos sentidos por defecto; elegí **Solo de A a B** o **Solo de B a A** para cronometrar un carril de una ruta de doble sentido. **Unidades de velocidad** (km/h o mph) están en **Configuración → Detección de IA**.

Un objeto que una trampa cronometra se registra como en movimiento aun si de otro modo parece quieto, como un vehículo que viene directo hacia la cámara. El cronómetro usa el centro inferior de cada caja, donde el objeto toca el suelo, así que dibujá las líneas sobre la superficie de la ruta. Para una buena lectura:

- Dibujá las líneas a través de la dirección de viaje, sobre terreno plano, y tan separadas como permita la vista mientras los objetos siguen bien visibles.
- Medí la distancia con cuidado: pacéala, o usá la herramienta de medición de un mapa.
- Verificala una vez con un vehículo que pase a una velocidad conocida, y ajustá la distancia si la lectura está desviada.

Las lecturas son estimaciones. La velocidad de cuadros de detección de la cámara limita qué tan preciso es el cronómetro de un cruce, así que las trampas cortas y los vehículos rápidos son los menos precisos. Guardar una trampa reinicia la detección de IA de la cámara.

Una trampa puede tener un **límite de velocidad** (opcional). Las velocidades sobre él se muestran en rojo y se cuentan por separado en el mapa de calor de velocidades.

Las velocidades medidas aparecen en varios lugares:

- **En vivo**: en la caja de cada objeto mientras está a la vista.
- **Capturas**: una insignia ⏱ en la tarjeta, y un filtro **Velocidad** («igual o sobre») en la lista de filtros.
- **Reproducción**: una marca en la línea de tiempo en cada medición (roja cuando supera el límite; pasá el mouse para ver detalles), y una insignia sobre el video mientras el objeto está en pantalla.
- **Tablero**: el widget **Registro de velocidades** muestra las últimas mediciones. Hacé clic en su título para ver el registro completo, con orden, filtrado y descarga CSV.
- **Actividad**: cambiá a **Velocidades** para un mapa de calor de velocidad promedio, velocidad máxima, cantidad sobre el límite o cantidad medida, por día y hora. El historial se conserva tanto como el historial de actividad, mucho después de que las grabaciones se fueron.
- **Alertas**: una regla «Velocidad sobre un límite».

## Movimiento

Las capturas, las alertas y las cajas **En movimiento** en vivo solo cuentan objetos que van a algún lado. La caja de un detector tambalea un poco de cuadro a cuadro aun alrededor de un auto estacionado, así que unos pocos píxeles de cambio no alcanzan. La **Detección de movimiento** en **Configuración → Detección de IA**, o por cámara en **Cámaras → Editar**, decide cómo se juzga esto.

**Estándar** (el predeterminado) mira la forma de la trayectoria reciente de cada objeto:

- Cualquier cosa que se mueve sostenido en una dirección cuenta en pocos cuadros (medio segundo o así a 7 fps), ya sea un auto que cruza la vista en menos de un segundo o un camión que sale de una entrada. Los pasos de cada objeto se comparan con cuánto tambalea su propia caja, así que un objeto grande no tiene que viajar más que uno pequeño.
- Algo que avanza más lento de lo que tambalea su caja, una fracción de un paso caminando, cuenta una vez que su trayectoria mantuvo una línea sostenida durante un par de segundos. El tambaleo va y viene, así que nunca forma una línea.
- Cuánto tiene que avanzar un objeto crece con su tamaño, pero más lento que el tamaño mismo. Un camión grande no tiene que cruzar primero la mitad de su propio largo.
- Un objeto que entra por el borde de la imagen, o sale de detrás de un poste o un árbol, se juzga por el lado de su caja que está a la vista.
- Un objeto que va directo hacia la cámara o se aleja cuenta a medida que crece o se achica.
- La caja de un auto estacionado que cambia porque alguien pasa frente a él no cuenta.

La **Sensibilidad de movimiento** (solo Estándar) es **Normal** por defecto. **Alta** capta el movimiento antes y **Baja** ignora más tambaleo. Bajala en una cámara donde un auto estacionado o un arbusto que se mueve se reporta como en movimiento, y subila donde se pierden objetos lentos.

Una caja que solo cambia de forma, como la de una planta en maceta cuando el detector incluye las hojas un momento y no el siguiente, o la de un auto estacionado cuando algo pasa frente a él, no cuenta. El movimiento tiene que acumularse a través de los cuadros, no pasar en un salto.

**Legado** es la regla anterior: un objeto cuenta una vez que su centro se movió la mitad del tamaño de su caja en 2 segundos. Usala si Estándar se comporta mal en una cámara.

Un objeto que se movía y desaparece detrás de un árbol, un poste o un auto estacionado sigue siendo el mismo objeto cuando aparece más adelante en la misma línea dentro de unos 30 cuadros analizados (unos 5 segundos a 6 fps), así que se cuenta una vez y conserva una trayectoria. También tiene que ser el mismo tipo de objeto y un tamaño similar, y su color tiene que coincidir cuando ambos se pueden distinguir. Algo que vuelve a aparecer yendo en otra dirección, o después de más tiempo, es un objeto nuevo.

## Objetos en movimiento

La trayectoria de cada objeto en movimiento a través de la imagen se conserva con su evento, para el mapa de calor **Escena** en la página de Actividad.

**Objetos en movimiento** en **Configuración → Detección de IA** elige qué tipos de objeto pueden contar como en movimiento. Por defecto son **Personas**, **Vehículos** y **Animales**. Cualquier otra cosa igual se detecta, y se muestra como una caja inactiva con **Inactivo**, pero nunca cuenta como en movimiento, así que no puede iniciar una captura ni una alerta. Optá por otros objetos uno por uno en la lista **Otros objetos**: un paquete entregado suele aparecer como valija, mochila o bolso. Para un modelo personalizado, escribí sus propios nombres de objeto, separados por comas.

## Objetos ignorados

**Objetos ignorados** en **Configuración → Detección de IA** desactiva tipos enteros de objeto. Se descartan en cuanto el modelo los reporta, así que nunca reciben una caja, una captura, un evento ni una alerta. Usalo para cosas que el modelo sigue entendiendo mal en tus cámaras, como una plataforma que llama avión. Marcá categorías completas (**Personas**, **Vehículos**, **Animales** y **Otros objetos**) u objetos individuales de la lista. La ortografía de ambos modelos coincide, así que ignorar **airplane** también ignora **aeroplane**. Para un modelo personalizado, escribí sus propios nombres de objeto, separados por comas.

La página **Editar** de una cámara puede elegir su propia lista en lugar de la global: elegí **Elegir para esta cámara** en **Objetos ignorados**. No elegir nada ahí detecta todo en esa cámara, aun lo que ignora la lista global. Guardar reinicia la detección de esa cámara, lo que solo restablece el seguimiento.

## Jitter

El **umbral de jitter** es el movimiento más pequeño en píxeles que alguna vez cuenta, sea cual sea el tamaño del objeto. En el modo Legado, **Rechazo de jitter de objetos estacionarios** suaviza el tambaleo de un vehículo estacionado lejano; también puede retener como inactivo un objeto que se mueve de verdad lento, así que está desactivado por defecto. No aplica a Estándar, que maneja el tambaleo solo.

## Backend externo

**Servicio HTTP externo** no corre ningún modelo en el nodo. Cada cuadro se envía a un servidor de inferencia que vos corrés, que devuelve cajas como JSON. Usalo para modelos que LarisVMS no incluye, o para nodos sin GPU. El seguimiento, las capturas, la línea de tiempo y la superposición en vivo funcionan igual.

El servicio debe exponer `POST /v1/detect`, `GET /v1/models` y `GET /healthz`. **Probar** verifica la conexión y carga la lista de modelos. La **clave de API** se envía como token Bearer. **Transporte**: JPEG funciona con cualquier servicio. Los modos YUV crudo y BGRA omiten la codificación JPEG pero usan 4–10× el ancho de banda, y solo funcionan si el modelo los anuncia.
