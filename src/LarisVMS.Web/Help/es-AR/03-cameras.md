# Cámaras

Agregar cámaras, la lista de cámaras, los streams, la configuración del dispositivo, los grupos, los dispositivos mult lente y las integraciones de proveedores.

## Agregar una cámara

Usá **Cámaras → Descubrir** para escanear la LAN en busca de dispositivos ONVIF, o **Cámaras → Agregar** para ingresar una URL de servicio del dispositivo (por ejemplo `http://192.168.1.50/onvif/device_service`) y las credenciales. LarisVMS después **sondea** la cámara: lee sus capacidades y streams ONVIF y elige los streams Principal y Sub.

Asigná un **nodo grabador**, o la cámara no va a grabar.

## La lista de cámaras

**Cámaras** enumera todas las cámaras que podés ver. La fila debajo de los encabezados de columna filtra por cualquier columna, como Marca, Modelo, Nodo o Grupo, y el cuadro de búsqueda arriba de la tabla filtra por texto en cualquier parte de una fila. Los filtros se combinan, y **Limpiar filtros** los restablece. **Columnas** elige qué columnas se muestran. Un filtro sobre una columna oculta se ignora.

Tus filtros, columnas, orden y tamaño de página se guardan en tu cuenta, así que vuelven cuando iniciás sesión de nuevo, en cualquier dispositivo.

Marcá cámaras para actuar sobre varias a la vez: **Reasignar seleccionadas al nodo**, o **Configuración del dispositivo…** para cámaras de la misma marca y modelo (ver [Cambios en masa](#bulk-changes)). **Seleccionar todo** solo selecciona las cámaras que coinciden con los filtros actuales.

## Volver a sondear

**Volver a sondear** en la página de una cámara lee sus capacidades y streams de nuevo. Hacé esto después de cambiar la resolución, el códec o el firmware de la cámara en su propia página web. Los cambios hechos en [Configuración del dispositivo](#device-settings) vuelven a sondear automáticamente. Cambiar la URL del dispositivo vuelve a sondear automáticamente al guardar. **Configuración → Configuración de cámaras** puede volver a sondear cada cámara una vez al día. Las cámaras se sondean de a una, para que las cámaras baratas no corten sus sesiones de grabación. Los nombres de streams y sus marcas de habilitado se conservan en un nuevo sondeo.

## Streams

Cada cámara suele tener un stream **Principal** (resolución completa, usado para grabar) y un stream **Sub** (menor resolución, usado para mosaicos en vivo pequeños y la detección de IA). Expandí **Dispositivo** en la página de la cámara para renombrar o deshabilitar un stream. Pasá el mouse sobre un stream para ver su URL RTSP. LarisVMS usa la dirección con la que se agregó la cámara, conservando el puerto y la ruta que informa la cámara, así una cámara que informa una dirección interna (o alguien que se hace pasar por ella) no puede mandar el grabador a otro lado con su contraseña.

## Configuración del dispositivo

**Configuración del dispositivo** en la página de una cámara cambia la configuración propia de la cámara por ONVIF, así no tenés que iniciar sesión en la página web de la cámara. La configuración se lee en vivo desde la cámara, y solo se muestran las que soporta.

| Sección | Configuración |
|---|---|
| **Streams de video** | Para cada stream: códec (H.264, H.265 o MJPEG), perfil de codificación, resolución, velocidad de cuadros, modo de bitrate (CBR o VBR), bitrate, calidad e intervalo de fotogramas clave |
| **Imagen** | Brillo, contraste, saturación y nitidez; modo día/noche; WDR; compensación de contraluz; modo de exposición y balance de blancos |
| **Audio** | Códec, bitrate y frecuencia de muestreo |

Cada sección tiene su propio botón **Aplicar**, y solo se envía la configuración que cambiaste. Aplicar un cambio de stream o de audio hace que el stream se reconecte, lo que deja un hueco de unos segundos en la grabación; LarisVMS después vuelve a sondear la cámara y el nodo grabador toma el nuevo formato enseguida. Los cambios de imagen se aplican sin reconectar.

Algunas cámaras comparten un conjunto de configuración de codificador entre streams. La página lo indica, porque cambiar uno cambia el otro.

H.265 y la elección CBR/VBR necesitan una cámara con ONVIF Profile T (Media2). En cámaras más viejas esas opciones no aparecen.

## Cambios en masa

Para cambiar varias cámaras de la misma marca y modelo a la vez:

1. En **Cámaras**, filtrá la columna **Modelo**, y después marcá las cámaras (o **Seleccionar todo**).
2. Hacé clic en **Configuración del dispositivo…**. Solo está disponible cuando todas las cámaras marcadas son de la misma marca y modelo.
3. Cambiá lo que necesités y hacé clic en **Aplicar** en esa sección.

También podés empezar desde la página **Configuración del dispositivo** de una cámara y elegir otras cámaras del mismo modelo en **Aplicar a**. Una insignia de *el firmware difiere* marca cámaras con firmware distinto, que pueden ofrecer opciones levemente diferentes.

La página muestra la configuración actual de la primera cámara. Solo se envía a cada cámara la configuración que cambiaste, así que el resto de la configuración propia de cada cámara queda como está. Los streams se emparejan por rol (Principal, Sub o Tercero). Cada cámara verifica el cambio contra sus propias opciones, y la lista de resultados muestra, para cada cámara, si se aplicó, se aplicó en parte, se omitió (por ejemplo, una cámara sin stream Tercero) o falló (por ejemplo, sin conexión). **Reintentar fallidas** envía el cambio de nuevo a las cámaras que fallaron.

## Grupos

Los grupos organizan las cámaras en sitios, edificios y pisos. Un **grupo de primer nivel es un sitio**, y los grupos debajo de él son edificios o pisos. Una cámara puede estar en cualquier cantidad de grupos, siempre que todos pertenezcan al mismo sitio. Los grupos se usan para filtrar, para anulaciones de configuración y para [acceso a cámaras](/Help/users#camera-access). El grupo integrado *Todas las cámaras* siempre contiene cada cámara.

## Multicanal

Algunos dispositivos tienen varias lentes en una misma carcasa. Cuando un sondeo encuentra más de una fuente de video, la página de la cámara ofrece **Dividir en N cámaras**. Cada lente después se vuelve su propia cámara con su propia grabación y retención. La cámara existente se vuelve el canal 1 y conserva sus grabaciones.

## Integraciones

Algunas funciones no están disponibles por ONVIF. Las integraciones (listadas en **Configuración → Plugins**) se emparejan automáticamente desde la marca y el modelo de la cámara durante un sondeo. No hay nada que activar. Por ejemplo, la integración **Dahua / Amcrest smart events** lee las detecciones de personas y vehículos de las analíticas propias de la cámara. El panel **Dispositivo** de la cámara muestra cuándo hay una integración activa.
