# Vista en vivo y reproducción

Vistas, mosaicos en vivo, la línea de tiempo de reproducción, exportaciones, marcadores, bloqueos de evidencia, capturas, actividad y el mapa.

## Vistas

Una **vista** es un diseño de cámaras guardado. Armá vistas en **Vistas** arrastrando y cambiando el tamaño de mosaicos. **En vivo** y **Reproducción** muestran una vista. En teléfonos, el diseño se colapsa a una o dos columnas. Las vistas pueden correr a pantalla completa en modo kiosco, o rotar entre varias vistas con un temporizador. Una vista compartida la puede usar cualquiera que vea vistas, pero solo su dueño o un administrador puede cambiarla o eliminarla.

## Mosaicos en vivo

- Hacé doble clic en un mosaico para pantalla completa. En pantalla completa, hacé scroll o pellizcá para acercar y arrastrá para mover.
- Los controles de un mosaico aparecen cuando pasás el mouse sobre él, o cuando llegás a ellos con Tab. Ver [Accesibilidad](/Help/accessibility).
- Cada mosaico con audio tiene su propio control de silencio y volumen. Los mosaicos siempre arrancan silenciados.
- El **streaming adaptativo** (en **Configuración → Vista en vivo**) muestra mosaicos pequeños con el stream Sub y cambia a Principal cuando el mosaico es grande o está a pantalla completa. Cada mosaico también tiene una elección manual Auto/HD/SD.
- Las insignias muestran movimiento y objetos detectados. **Configuración → Eventos** configura sus colores y esquina.
- Los interruptores de IA de la barra de herramientas dibujan una caja alrededor de cada objeto detectado: **En movimiento**, **Inactivo**, o ambos. **Confianza** agrega el puntaje de cada caja, y **Velocidad** y **Color** (activados por defecto) agregan su velocidad, una vez que una trampa de velocidad lo cronometró, y su color. **Trayectoria** (con En movimiento activado) dibuja la línea que cada objeto en movimiento tomó desde el centro de su caja, en el color de la caja. La línea queda mientras el objeto sigue moviéndose. Cuando se detiene o sale, la línea queda durante el tiempo de **Persistencia de trayectoria** (**Configuración → Vista en vivo**, 5 segundos por defecto), y después se disuelve desde su inicio durante el tiempo de **Disolución de trayectoria** (misma página, 3 segundos por defecto).
- Las cámaras PTZ reciben un teclado direccional y zoom.
- 🔴 abajo a la izquierda significa que las grabaciones de la cámara se están conservando ahora mismo, por cualquier motivo. Las cámaras Continuas lo muestran todo el tiempo. Puede tardar hasta 20 segundos en cambiar después de un movimiento o un evento.
- Ⓜ️ junto a él significa que hay una [grabación manual](/Help/recording#manual-recording) en curso. Ⓜ️ solo significa que la grabación está en curso, pero la cámara está sin conexión y no se está guardando nada.
- El botón ⏺ inicia una grabación manual, y ⏹ la detiene. En una vista con otras cámaras, pregunta si te referís a esta cámara o a todas. Cualquiera con permiso puede detener una grabación, quienquiera que la haya iniciado.

## Hablar por una cámara

Las cámaras con altavoz muestran un botón 🎙️ en su mosaico en vivo. Mantenelo presionado (o enfocálo y mantené Espacio) para hablar, y soltalo para parar. **▾** junto a él elige el micrófono: un auricular, una webcam o cualquier otra entrada que el navegador pueda usar.

- Tu navegador pide acceso al micrófono la primera vez. Solo lo permite cuando el sitio se abre por **HTTPS**; ver [Certificados](/Help/security#certificates).
- Solo una persona puede hablar por una cámara a la vez.
- Necesita el permiso **Hablar** en la cámara (**Configuración → Acceso a cámaras**). La cámara debe soportar audio bidireccional ONVIF (una salida de audio en su perfil ONVIF). Las cámaras Dahua, Amcrest, Lorex y EmpireTech cuyo firmware no ofrece eso vuelven a la API de audio propia de la cámara.
- Todo lo que se dice se graba. El micrófono propio de la cámara es parte de la grabación de video como siempre, y cada presión del botón se guarda junto a las grabaciones. Las presiones se muestran en la línea de tiempo de reproducción en verde azulado y se reproducen sincronizadas con el video, usando el volumen del mosaico. Las exportaciones las incluyen, mezcladas con el audio propio de la cámara. Se conservan tanto como las grabaciones de la cámara.
- Cada sesión se escribe en el registro de auditoría.

## Línea de tiempo

La reproducción tiene dos líneas de tiempo: una para la cámara seleccionada y una combinada de todas las cámaras. Ambas están coloreadas por movimiento, detecciones, etiquetas de eventos y audio bidireccional.

- Hacé scroll para acercar (de semanas a segundos), arrastrá para mover y hacé clic para saltar cada mosaico a ese momento.
- Hacé clic en la hora arriba de la línea de tiempo para escribir una hora exacta.
- Las flechas avanzan un segundo mientras está en pausa; Mayús+flecha avanza diez.
- Las velocidades hasta 8× se reproducen suave. Las velocidades más rápidas pasan las grabaciones como un pase de diapositivas.
- Pasá el mouse sobre la línea de tiempo para una miniatura de vista previa.
- Los tramos con movimiento o un evento están rayados, así que no dependen solo del color.
- Los botones − y + junto a la hora también acercan, y la línea de tiempo funciona desde el teclado. Ver [Accesibilidad](/Help/accessibility).

Tu posición y tu zoom se recuerdan entre visitas.

## Exportaciones

**Exportar…** en Reproducción crea un archivo de video para cada cámara seleccionada en un rango de tiempo. Una exportación abarca hasta 64 cámaras, 31 días y 1.000 horas-cámara en total (4 cámaras durante una semana son 672); para más, hacé varias. Las exportaciones corren en segundo plano y aparecen en la página **Exportaciones**, donde se pueden descargar, reintentar o eliminar. Las exportaciones terminadas se conservan 7 días y después se eliminan junto con sus archivos.

- **✏ Difuminar** junto a una cámara tapa áreas con negro solo en la exportación de esa cámara, como la ventana de un vecino o un transeúnte. Dibujá rectángulos (arrastrando) o polígonos (haciendo clic en cada esquina; hacé clic en el primer punto, doble clic o presioná Enter para terminar; el clic derecho quita el último punto) sobre un cuadro del inicio del rango de exportación. Las grabaciones mismas no cambian.
- Una exportación difuminada se re-codifica, así que tarda más que una simple. Se marca **Difuminada** en la página de Exportaciones, y el registro de auditoría registra que se taparon áreas.
- Las [máscaras de privacidad](/Help/motion) propias de una cámara ya son parte de sus grabaciones, así que cada exportación las incluye.
- Cualquier audio bidireccional dicho por la cámara durante el rango se mezcla en el audio de la exportación.

### Verificar una exportación

Cada segmento grabado se sella con huella digital (SHA-256) al escribirse, y los segmentos de cada cámara se enlazan en una cadena: cada eslabón cubre la huella de su segmento y el eslabón anterior, así que cambiar, quitar o reordenar grabaciones rompe la cadena. Una vez por hora el servidor firma hasta dónde llegó la cadena de cada cámara.

Cada exportación terminada también recibe un **🔏 Manifiesto** firmado en la página de Exportaciones. Registra la huella del archivo exportado, las grabaciones de origen y su lugar en la cadena, y si cada archivo de origen todavía coincidía con su huella cuando se exportó.

Para verificar una exportación, abrí **Exportaciones → Verificar una exportación** y elegí el manifiesto y el video. La verificación corre en tu navegador y nada se sube. Confirma:

- que el manifiesto fue firmado por este servidor y no fue editado;
- que el video es exactamente el archivo exportado;
- que las grabaciones de origen no habían cambiado desde que se grabaron, y se enlazan a un punto de control firmado.

Quedate con el manifiesto junto al video cuando entregás una exportación. Cualquiera puede verificar la firma sin LarisVMS: guardá el `publicKeyPem` del manifiesto como `key.pem`, su texto `signedPayload` como `payload.txt`, y su `signatureDer` decodificado de base64 como `sig.der`, y después ejecutá `openssl dgst -sha256 -verify key.pem -signature sig.der payload.txt`. Las grabaciones hechas antes de esta función no tienen huellas, así que solo se puede verificar la propia exportación.

La firma siempre está activada. La clave de firma del servidor, con una descarga de su clave pública, está en **Configuración → Cifrado y firma**.

## Marcadores

Marcá un momento durante la Reproducción con una nota. **Marcadores** los enumera a todos, con un enlace de vuelta al momento exacto. Los marcadores se comparten con todos los que pueden ver la reproducción, y vencen cuando sus grabaciones vencen.

## Bloqueos de evidencia

Un bloqueo de evidencia evita que las grabaciones se eliminen por retención. En Reproducción, hacé clic en **🔒 Bloquear…**, elegí las cámaras y el inicio y el fin, elegí cuánto conservarlo (1, 3, 7, 14 o 30 días, hasta una fecha, o indefinidamente), y dá un motivo. Los tramos bloqueados se muestran como una banda violeta arriba de la línea de tiempo. Un bloqueo cubre hasta 7 días de grabaciones.

- **Las grabaciones bloqueadas nunca se eliminan**, ni por retención, cuotas o vencimiento de archivo, ni siquiera cuando el disco está lleno. Si un grabador se llena de grabaciones bloqueadas, su entrada en Nodos muestra **lleno de grabaciones bloqueadas**, y la grabación se detiene una vez que el disco está lleno. Terminá o quitá bloqueos, exportá las grabaciones, o agregá almacenamiento.
- **Bloqueos largos**: cualquier cosa sobre 30 días muestra una advertencia. Cuando el almacenamiento es justo, es mejor exportar el video (**Exportar este rango en su lugar**) y conservarlo en otro lado.
- **Quitar un bloqueo**: **Bloqueos de evidencia** enumera cada bloqueo con su tamaño y vencimiento. La persona que puso un bloqueo, o un Super Administrador, puede quitarlo con un motivo. Las grabaciones quedan protegidas otros **24 horas** por si acaso la quita fue un error, y **Deshacer quita** restaura el bloqueo hasta entonces.
- **Registrado**: colocar, quitar, restaurar y vencer un bloqueo se escriben todos en el registro de auditoría.

Una cámara con grabaciones todavía bajo un bloqueo no se puede eliminar. Colocar bloqueos necesita el permiso **Bloquear grabaciones**, que los roles que pueden exportar tienen por defecto. Las [Notificaciones](/Help/alerts#notifications) pueden avisarte cuando un bloqueo está por vencer o fue quitado.

## Capturas

**Capturas** enumera cada detección y etiqueta de evento como una miniatura, con filtros por cámara, grupo, vista, tipo de objeto y tiempo. El movimiento simple solo aparece en la línea de tiempo. ▶ abre el momento en Reproducción.

## Actividad

**Actividad** muestra cuándo pasan cosas en una cámara, o en todas las cámaras juntas, como un mapa de calor por hora del día. Elegí la cámara (o **Todas las cámaras**) y un rango: 24 horas, 7, 30 o 90 días, o un año.

- **Semana típica** (el predeterminado): las filas son días de la semana y las columnas son horas, totalizados en el rango, así que una celda oscura es una hora que suele estar ocupada, como los sábados 18–19. En más de una semana, pasar el mouse sobre una celda también da el promedio por día (por ejemplo, «8,8 por sábado en promedio»).
- **Por día** (30 días en adelante): cada fecha corre de izquierda a derecha con las horas de arriba a abajo, y una franja de totales diarios debajo, así podés distinguir días particulares.
- El día más ocupado del rango y la hora más ocupada se nombran arriba del gráfico.
- Para las últimas 24 horas, hay una fila para cada tipo de evento.
- **Tipos de evento** lo acota a lo que te importa: un tipo completo (Animal) o solo algunas de sus etiquetas (Animal → gato). El movimiento simple está desactivado por defecto para no ahogar los objetos. Tu elección se recuerda y queda cuando cambiás de cámara.
- Pasá el mouse sobre una celda para su conteo exacto, o activá **Mostrar como tabla** para los números.
- Las horas están en la zona horaria de la cámara (configurada en la cámara), o la del servidor si no tiene. Todas las cámaras usa la zona horaria del servidor.
- El historial de actividad se conserva 400 días (configuración `Activity.HistoryDays`), aun después de que las propias grabaciones se eliminaron, así los rangos más largos muestran todo el período. El historial se acumula desde que se instaló: los eventos ya eliminados junto a sus grabaciones no se pueden recuperar.
- La vista está en la dirección de la página, así podés marcarla o compartirla. El widget **Actividad de la cámara** del tablero muestra los últimos 7 días de una cámara o de todas.

### Escena

**Escena** muestra *dónde* en la imagen de una cámara se mueven las cosas: una capa de calor sobre un cuadro reciente a tamaño completo de las grabaciones de la cámara. Rojo es donde más pasa, después naranja, amarillo, verde y azul para muy poco. Los lugares sin nada quedan libres.

- **Objetos en movimiento** cuenta cuántos objetos pasaron por cada lugar. Acotalo a **Personas**, **Vehículos**, **Animales** u **Otros**.
- **Movimiento** muestra dónde la detección de movimiento del lado del servidor de la cámara vio movimiento, para cámaras que lo usan en modo **Grilla**.
- Elegí un rango, y opcionalmente una **Hora del día** (por ejemplo 22 a 6 para las noches).
- El calor se suma una vez por hora (movimiento cada 10 minutos) y se conserva tanto como el resto del historial de actividad. Las trayectorias detrás se conservan con sus eventos, así que se van cuando se van las grabaciones.
- Elegí una cámara, o una **vista** (listada en **Vistas** en la lista de cámaras) para ver un mapa de calor para cada cámara en ella, lado a lado. Cada cámara tiene su propia escala, así que una cámara tranquila igual muestra dónde está su actividad. No se puede mostrar para todas las cámaras a la vez.
- **Fondo**: si podés configurar la cámara, podés reemplazar su cuadro por tu propia imagen (una foto diurna despejada, digamos) con **Usar mi propia imagen…**. Tiene que ser de la misma cámara con el mismo encuadre, a su ancho completo o la misma forma, para que el calor coincida. **Usar la imagen de la cámara** vuelve al cuadro automático.
- **Reiniciar mapa de calor**: después de mover o reorientar una cámara, su calor anterior ya no coincide con la imagen. Si podés configurar la cámara, **Reiniciar mapa de calor** lo borra y cuenta solo lo que pase desde entonces; la tarjeta muestra desde cuándo se cuenta.

## Mapa

**Mapa** (en En vivo) muestra dónde están tus cámaras y qué miran. Cada cámara es un ícono con un cono para su vista: la dirección a la que apunta, qué tan ancho ve y qué tan lejos. El color muestra su estado: verde es grabando, rojo es sin conexión, gris es deshabilitada. Hacé clic en una cámara para su última imagen, con enlaces a En vivo y Reproducción. Los estados se actualizan cada 30 segundos. El mapa se reabre donde lo dejaste, por separado para cada persona y cada mapa.

Hay dos tipos de mapa, y podés tener tantos de cada uno como quieras:

- **Mapa callejero**: un mapa normal de la zona (OpenStreetMap por defecto). El rango está en metros.
- **Plano de planta**: una imagen que subís (PNG, JPEG, WebP o GIF, hasta 10 MB), como un plano de un edificio o un dibujo del sitio. El rango está en píxeles de esa imagen.

Quienes pueden editar cámaras pueden configurar mapas:

1. **Mapa nuevo → Mapa callejero** o **Plano de planta**. Un mapa callejero nuevo se abre donde está mostrando el actual, o en el mundo entero; mové y acercá hasta tu sitio. Cuando presionás **Listo para editar**, el mapa se guarda para abrirse donde está mostrando para todos; **Gestionar → Abrir este mapa acá para todos** hace lo mismo sin editar. Esto también funciona para planos de planta.
2. **Editar mapa**, y después arrastrá cámaras desde **Cámaras que no están en este mapa** hasta el mapa. O hacé clic en una para colocarla en el medio del mapa.
3. Hacé clic en una cámara para seleccionarla. Arrastrá el ícono para moverla, y arrastrá el asa blanca en la punta de su cono para apuntarla (dirección y rango). El panel lateral configura el campo de visión, o la dirección y el rango exactos, y **Quitar de este mapa** la quita. Los cambios se guardan a medida que los hacés. Los botones de flecha en el panel la mueven sin arrastrar.

Una cámara puede estar en varios mapas, como un mapa callejero y el plano de planta de su edificio. La gente solo ve las cámaras que puede ver.

Las imágenes del mapa callejero las carga tu navegador desde el servidor de tiles, así que un mapa callejero necesita acceso a internet desde el navegador. Para un sitio sin conexión, o para otro estilo de mapa, configurá tu propio servidor de tiles https en **Gestionar → Tiles del mapa callejero**, y su atribución (la mayoría de los términos de los servidores de tiles la exigen en el mapa). Cambiarlo también requiere el permiso para editar la configuración, porque se aplica a todo el servidor.
