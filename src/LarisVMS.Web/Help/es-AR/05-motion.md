# Detección de movimiento

Fuentes de movimiento, movimiento del lado del servidor, zonas y la grilla de movimiento.

## Fuente

Una cámara puede reportar movimiento de varias maneras:

- **Movimiento del lado del servidor**: el nodo compara cuadros de video dentro de zonas de movimiento o de la grilla de movimiento.
- **Eventos de cámara**: los eventos de movimiento ONVIF propios de la cámara.
- **Integración de proveedor**: por ejemplo, los eventos inteligentes de Dahua / Amcrest.
- **Detección de IA**: un objeto en movimiento detectado.

La **Fuente de movimiento** de la cámara elige cuál *una* de estas decide qué conserva el modo Movimiento, para que la línea de tiempo no reciba entradas duplicadas del mismo evento. **Auto** elige la más rica que esté configurada. Las reglas de etiquetas de eventos y las etiquetas de objetos de IA siempre etiquetan la línea de tiempo, sea cual sea esta elección.

## Movimiento del lado del servidor

**Movimiento del lado del servidor** en la página de la cámara activa o desactiva la detección de movimiento por píxeles propia del nodo. Dejalo activado para etiquetar el movimiento simple en la línea de tiempo como respaldo, aun cuando otra fuente condicione la grabación. Desactivalo para ahorrar la CPU que usa.

## Zonas

La página **Zonas** de una cámara tiene dos métodos. Solo uno está activo a la vez, y cambiar conserva la configuración del otro.

**Grilla**: hacé clic en celdas para enmascararlas (rojo). Las celdas se ponen ámbar mientras hay movimiento en ellas, lo que te ayuda a encontrar una rama que se mueve o una luz que parpadea para enmascarar. Cambiar el tamaño de la grilla borra la máscara.

**Zonas poligonales**:

- **Movimiento**: un área para vigilar.
- **Ignorar**: un área para excluir (un árbol, una vereda pública). Los píxeles en una zona Ignorar nunca cuentan, aun donde se superpone con una zona Movimiento.
- **Privacidad**: un área para tapar en negro. Dibujalas en **🔒 Máscaras de privacidad**, que funciona con cualquier método de movimiento (Grilla o Zonas poligonales) que use la cámara. Se oculta en grabaciones, vista en vivo (ambos streams), capturas y exportaciones. Los eventos igual ocurren detrás como siempre (sus capturas se enmascaran igual que el video), salvo que actives **También ignorar detecciones dentro de máscaras de privacidad** para esa cámara, en la vista de Máscaras de privacidad. La máscara sigue la forma que dibujás, con un pequeño margen para que no se vea ningún borde. Una cámara con una zona de privacidad re-codifica su video mientras graba, lo que usa algo de CPU (o la GPU, cuando el nodo tiene una compatible).
- Las zonas de **Movimiento del lado de la cámara** se pueden guardar, pero todavía no están activas.

Para remodelar una zona, seleccionala en la lista y arrastrá sus esquinas, después **Guardar**. Mientras dibujás, el clic derecho quita el último punto. Pasar el mouse sobre una zona en la lista la resalta en la imagen. También podés hacer clic en una esquina y después en dónde debería ir.

La **sensibilidad** es el porcentaje de los píxeles de la zona que deben cambiar. Más bajo es más sensible. Las zonas anchas necesitan valores más bajos, porque el movimiento real cubre solo una parte pequeña. Empezá bajo (alrededor del 3%) y subilo solo si el viento o los cambios de luz lo disparan.
