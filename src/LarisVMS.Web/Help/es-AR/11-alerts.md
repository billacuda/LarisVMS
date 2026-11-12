# Alertas, notificaciones y correo

Reglas de alerta, notificaciones a usuarios, canales de equipo y configuración del proveedor de correo.

## Reglas de alerta

**Configuración → Alertas** guarda las reglas compartidas. Cada regla vigila una condición:

- **Cámara sin reportar**: la cámara dejó de enviar reportes de estado.
- **Nodo sin conexión**: el nodo dejó de reportarse.
- **Almacenamiento bajo**: el espacio libre bajó de un porcentaje.
- **Conmutación por error de nodo**: las cámaras de un nodo se movieron a su nodo de respaldo.
- **Movimiento detectado**: movimiento en una cámara, o en una de sus zonas de movimiento.
- **Objeto detectado**: la detección de IA vio un objeto coincidente.
- **Velocidad sobre un límite**: un objeto cruzó una [trampa de velocidad](/Help/ai-detection#speed-traps) igual o sobre una velocidad configurada.
- **Bloqueo de evidencia por finalizar** y **Bloqueo de evidencia eliminado**: ver [bloqueos de evidencia](/Help/live-and-playback#evidence-locks).

Las reglas de estado se verifican una vez por minuto. El **enfriamiento** es el tiempo mínimo entre alertas repetidas, así una condición que sigue verdadera no alerta cada minuto.

### Alertas de detección

**Objeto detectado** y **Velocidad sobre un límite** se disparan en segundos de la detección. Cada filtro es opcional, y todos los que configurás deben coincidir:

- **Cámara**: una cámara, las cámaras de un nodo, o cualquier cámara.
- **Categoría** y **etiqueta**: por ejemplo Vehículo, o solo camiones.
- **Color**: el color del objeto, o el color de camisa o pantalón de una persona. Ver [colores de objetos](/Help/ai-detection#object-colors).
- **Zona**: solo objetos parados dentro de una de las zonas de la cámara (juzgado por la parte inferior de la caja).
- **Activa**: días de la semana y una ventana de tiempo, en la zona horaria de quien guardó la regla. Una ventana como 22:00 a 06:00 corre de noche.

Una detección alerta una vez. En una regla que vigila varias cámaras, el enfriamiento cuenta por separado para cada cámara, así una cámara ocupada no frena a las demás.

### Alertas de movimiento

**Movimiento detectado** se dispara en segundos del movimiento, desde la detección de movimiento del servidor o los eventos de movimiento propios de la cámara. Elegí una cámara, las cámaras de un nodo o cualquier cámara. Con una cámara elegida, **Zona** lo limita a una de las zonas de **Movimiento del servidor** de esa cámara; dejalo en **(toda la cámara)** para movimiento en cualquier lugar. Los días y horas **Activa** funcionan igual que para las alertas de detección. Un tramo de movimiento alerta una vez, y el enfriamiento frena el siguiente.

### Canales de equipo

Cada regla también puede enviar a listas de correo, webhooks, ntfy, Pushover, Slack y Teams. Activá un canal y completá sus campos.

## Notificaciones

Las notificaciones van a personas individuales, en su navegador o por correo, en lugar de a un canal de equipo. Cada persona elige lo que quiere en **Notificaciones** en la barra lateral (también el 🔔 junto a tu nombre). Una notificación solo llega a alguien por cámaras que puede ver.

- **Notificaciones compartidas**: en una regla de alerta, **Permitir que los usuarios se suscriban** la enumera en **Mis notificaciones → Disponibles**, donde cualquiera puede activarla. Los administradores también pueden agregar un usuario, o a todos en un rol, en **Suscriptores** en la regla.
- **Tus propias notificaciones**: **Mis notificaciones → Mías → Nueva notificación** arma una solo para vos, con las mismas opciones que una regla de alerta. Por ejemplo, animales en la cámara del fondo entre las 22 y las 6. Solo vos la ves.
- **Silenciar** una notificación por una hora, 8 horas, un día, o hasta que la volvás a activar, sin darte de baja.

**Configuración → Notificaciones** activa o desactiva cada tipo de notificación para los usuarios. Desactivar un tipo no detiene los canales de equipo en las reglas de alerta.

Suscribirse necesita el permiso **Suscribirse a notificaciones compartidas**, y hacer la propia necesita **Crear notificaciones personales**. Cada rol tiene ambos por defecto.

### Notificaciones del navegador

Activalas en cada navegador en **Mis notificaciones → Este navegador y dispositivos**, y después usá **Enviar una prueba**. **Configuración → Notificaciones** elige cómo se entregan:

| Entrega | Funciona cuando | Necesita |
|---|---|---|
| **Mientras LarisVMS está abierto** | Una pestaña de LarisVMS o la app instalada está abierta | Nada. Queda en tu red. |
| **Web Push** | Ninguna página de LarisVMS abierta (Chrome, Edge, Firefox y Safari). En una computadora, el propio navegador tiene que estar corriendo; ver abajo | **Generar claves de Web Push** una vez, y acceso saliente a internet desde el servidor a los servicios push de los fabricantes de navegadores. Sin cuenta. |
| **Firebase Cloud Messaging** | Ninguna página de LarisVMS abierta, con el mismo límite en computadoras | Tu propio proyecto de Firebase: su configuración de app web, su certificado de web push y una clave de cuenta de servicio. Solo se usa cuando Web Push está desactivado. |

Sin permiso para mostrar notificaciones, aparecen dentro de las páginas abiertas de LarisVMS. En un teléfono, primero instalá LarisVMS como app (ver [el resumen](/Help/overview)).

Los teléfonos reciben notificaciones push aun con el navegador cerrado. En una computadora, un navegador solo las recibe mientras está corriendo, y cerrar su última ventana suele cerrarlo; lo que se envía mientras tanto se muestra cuando vuelve a arrancar. Para mantenerlo corriendo en segundo plano, activá **Seguir ejecutando apps en segundo plano cuando Google Chrome está cerrado** (Chrome: Configuración → Sistema) o **Seguir ejecutando extensiones y apps en segundo plano cuando Microsoft Edge está cerrado** (Edge: Configuración → Sistema y rendimiento). Usá correo para quien necesite alertas sin ningún navegador corriendo.

### Notificaciones por correo

La gente también puede elegir correo para una notificación. Va a la dirección de su cuenta, a través del correo configurado abajo. **Configuración → Notificaciones** puede desactivar las notificaciones por correo.

## Correo

Las alertas y notificaciones por correo se envían a través del proveedor configurado en **Configuración → Correo**. **Guardar y enviar prueba** verifica la configuración guardada.

- **SMTP**: host, puerto, SSL/TLS y credenciales.
- **Microsoft Graph**: registrá una app en Entra ID, dale el permiso de **aplicación** `Mail.Send` (no delegado) con consentimiento de administrador, e ingresá su ID de tenant, ID de cliente y secreto. No hay inicio de sesión por usuario.
- **Gmail**: creá un cliente OAuth de tipo *Aplicación web* en Google Cloud. Agregá `https://<your-host>/Admin/OAuthCallback` como URI de redirección autorizada, y habilitá el alcance `https://mail.google.com/`. Después usá **Guardar y conectar con Google** una vez para dar el consentimiento.

Los secretos guardados están cifrados. Si se pierden las claves de protección de datos del servidor, volvé a ingresarlos y reconectá Gmail.
