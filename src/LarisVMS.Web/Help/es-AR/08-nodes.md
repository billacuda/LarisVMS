# Nodos

Nodos grabadores: estado, rutas de streaming, proxies de medios, conmutación por error, mantenimiento y actualizaciones.

**Nodos** en la barra lateral enumera cada nodo con su estado, dirección, versión, cantidad de cámaras y uso de disco. Hacé clic en un nodo para editar su configuración. El menú **⋯** tiene estas acciones:

- **Reiniciar servicio**: reinicia el servicio de Windows del nodo, no la máquina. La grabación se pausa unos segundos.
- **Restablecer auth**: borra el token de chequeo del nodo, para un nodo atascado con fallas de autenticación después de un heartbeat perdido. No tiene efecto en un nodo sano.
- **Mantenimiento**: mueve las cámaras del nodo a su nodo de respaldo hasta que desactivás el mantenimiento.
- **Eliminar**: quita el nodo. Sus cámaras quedan sin asignar. Las grabaciones no se eliminan.

Un nodo está **en línea** si se reportó en los últimos 2 minutos. Una advertencia ⚠️ de **reloj** significa que el reloj del nodo está más de un minuto desfasado del servidor, así que revisá su sincronización horaria.

## Streaming directo

Por defecto todo el video en vivo y de reproducción se retransmite por el servidor web (**Proxy**). Con **Directo**, los navegadores se conectan directo al endpoint HTTPS propio de cada nodo, lo que le quita carga al servidor. Configurá el modo en **Configuración → Vista en vivo** y anulalo por nodo.

El modo directo necesita un **host de endpoint de cliente** que los navegadores puedan alcanzar, y un certificado (una ruta `.pfx` en la página del nodo). **Permitir autofirmado** es solo para pruebas, porque los espectadores reciben una advertencia del navegador. Una cámara cuyo nodo no tiene un endpoint sano vuelve al proxy automáticamente.

## Conexiones cifradas desde el servidor

El servidor y los proxies de medios llegan a las rutas de video y de control de cada nodo por HTTP sin cifrar en su puerto en vivo (8554), lo que está bien en una red de confianza. Para cifrar ese tráfico, activá **Cifrar las conexiones desde el servidor** en la página del nodo y después reiniciá el nodo una vez (**Reiniciar**). El nodo inicia su endpoint HTTPS (con su propio certificado autofirmado si no tiene un `.pfx`) e informa el certificado, que el servidor fija: el servidor y los proxies aceptan solo ese certificado del nodo, y el token de medios deja de ir en la dirección. Una vez cifrado, el puerto sin cifrar del nodo solo responde `/health`.

La columna **Cifrado** de la página Nodos muestra 🔒 para cada nodo cifrado, y ⏳ para uno que lo tiene activado pero todavía necesita reiniciarse. El estado debajo del interruptor muestra el certificado en uso, o que el servidor todavía lo está esperando (hasta entonces el servidor sigue usando HTTP sin cifrar, así que nada deja de funcionar). Viene desactivado. Los certificados autofirmados que se usan para esto nunca se ofrecen a los navegadores; el streaming directo sigue necesitando uno real.

## Proxies

Un **proxy de medios** es un relé separado (`LarisVMS.Proxy`) entre los navegadores y los nodos, para sitios remotos o enlaces de bajo ancho de banda. Registrá proxies en **Configuración → Proxies de medios**, y después elegí un proxy primario y uno de respaldo en la página de cada nodo. Los navegadores usan el primero sano.

## Conmutación por error

Dale a un nodo un **nodo grabador de respaldo**, y cuando se cae sus cámaras las graba el respaldo hasta que vuelve. «Caído» se decide por votación entre el compañero del nodo, el servidor y su proxy. **Mantenimiento** mueve sus cámaras a propósito y las retiene ahí.

La página del nodo avisa cuando el respaldo tiene hardware de IA distinto, o es solo grabación. En esos casos la detección puede ser más lenta, o quedar en pausa, durante una conmutación por error.

## Actualizaciones

Los nodos y proxies se actualizan solos desde compilaciones aprobadas (ver [Instalación](/Help/installation#updates)). **Nodos → Actualizaciones y registro → Actualización automática** lo activa o desactiva para cada nodo.

La sección **Compilaciones** de la página de Nodos muestra hacia qué compilación va cada tipo de nodo y cualquier compilación esperando aprobación, con **Aprobar** y **Rechazar**. **Historial de compilaciones** enumera cada compilación, incluidas las aprobadas, reemplazadas y rechazadas.

## Certificado del servidor

**Nodos → Actualizaciones y registro → Verificación del certificado del servidor** decide cómo los nodos y proxies verifican el certificado HTTPS de este servidor:

- **Confiar en el primer uso** (el predeterminado): un certificado de una CA de confianza siempre funciona, incluidos los renovados y reemplazados. Un certificado autofirmado se recuerda la primera vez que un nodo se conecta y sigue funcionando mientras no cambie, aun pasado su vencimiento. Si cambia, los nodos y proxies lo rechazan hasta que se vuelven a registrar.
- **Solo certificado de confianza**: solo un certificado de una CA de confianza que coincida con el nombre del servidor. Instalá uno antes de elegir esto.
- **No verificar**: cualquier certificado. No es seguro: cualquiera en el camino de red podría hacerse pasar por el servidor.

Con confianza en el primer uso, un nodo confía en el certificado que ve la primera vez que se conecta. Para que lo verifique desde la primera conexión, pasale al instalador la huella que se muestra en **Verificación del certificado del servidor**: `-ServerCertThumbprint` para `install-node.ps1` e `install-proxy.ps1`, o `SERVERCERTTHUMBPRINT=` para el MSI. Los comandos de instalación de las páginas de Configuración inicial y Proxies de medios ya la incluyen. Detrás de un proxy inverso, usá en cambio la huella del certificado que presenta el proxy inverso.

El certificado autofirmado que el servidor se hace a sí mismo dura 10 años y no se reemplaza solo. **Regenerar certificado autofirmado** lo reemplaza; cada nodo y proxy después tiene que volver a registrarse, y la página de Nodos enumera los que no se reportaron desde entonces. Para registrar un nodo de nuevo, ejecutá el instalador del nodo otra vez, o ejecutá `LarisVMS.Node.exe --reset-server-pin` en él y reiniciá su servicio (los proxies tienen la misma opción).

## Códigos de registro de un solo uso

Los nodos y proxies nuevos se registran con la **clave de registro** compartida, o con un código de un solo uso. Un código de un solo uso registra un único nodo o proxy y después deja de funcionar. Si no se usa, vence después de 1 hora, 24 horas o 7 días. Para crear uno, andá a **Nodos → Actualizaciones y registro**, elegí cuánto dura y seleccioná **Crear código**. El código se muestra una sola vez. Usalo donde el instalador pide la clave de registro. **Revocar** anula un código que todavía no se usó.

La clave compartida sirve para cualquier instalación hasta que se cambia. Cuando empieces a usar códigos de un solo uso, desmarcá **Aceptar la clave de registro compartida** para que una copia de la clave en un script o correo viejo no pueda registrar nada. Los nodos y proxies ya registrados no se ven afectados. En cualquier caso, un nodo nuevo sigue esperando aprobación antes de recibir cámaras.
