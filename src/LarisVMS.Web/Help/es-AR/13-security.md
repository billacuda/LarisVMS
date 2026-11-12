# Seguridad

Listas de IP permitidas, un puerto separado para en vivo/reproducción, la política estricta de scripts, certificados, nombres de host, actualizaciones, secretos cifrados y protección adicional para empresas.

## Listas de permitidos

**Configuración → Seguridad** tiene dos listas de IP permitidas. Ingresá una IP o bloque CIDR por línea (por ejemplo `10.0.0.5` o `10.0.0.0/24`); las líneas que empiezan con `#` son comentarios. Una lista vacía permite a todos.

- **Gestión / API**: las páginas de administración y la API REST. Una lista que bloquearía la dirección desde la que estás guardando se rechaza, así no podés dejarte afuera.
- **Vista en vivo / reproducción**: video en vivo, reproducción, miniaturas, descargas de exportación y capturas de cámaras.

## Puerto separado

**Configuración → Vista en vivo → Puerto personalizado** mueve el tráfico en vivo y de reproducción a su propio puerto HTTPS, usando el mismo certificado. Esto deja filtrarlo por firewall por separado, o exponer solo él fuera de la LAN. Se aplica después de `Restart-Service LarisVMSWeb`. Después de eso, el video solo responde en el puerto nuevo y todo lo demás solo en el puerto principal. Borrá el campo para volver a un puerto.

## HTTP/3

El servidor usa HTTP/2, y agrega HTTP/3 en Windows 11, Windows Server 2022 o posterior. Los navegadores arrancan en HTTP/2 y pasan a HTTP/3 solos cuando pueden llegar a él. HTTP/3 usa UDP en el puerto HTTPS (y el puerto personalizado, si está configurado). El instalador abre TCP y UDP; en una instalación anterior, agregá una regla UDP para el puerto o ejecutá el instalador actual de nuevo. Hasta entonces, todo sigue funcionando por HTTP/2. Para desactivar HTTP/3, borrá **Configuración → Seguridad → HTTP/3**, y después `Restart-Service LarisVMSWeb`.

## Política estricta de scripts

Con **Configuración → Seguridad → Política estricta de scripts** activada, los navegadores solo ejecutan los scripts que envía este servidor, así un script inyectado en una página no puede correr. Viene activada, y actualizar la activa. Si algo deja de funcionar, desactivala: los navegadores reportan entonces lo que la política estricta habría bloqueado, y los últimos reportes aparecen bajo el interruptor. Cuando no aparezca ningún reporte después de usar las páginas de las que dependés, volvé a activarla. Un cambio se aplica en la próxima carga de página.

## Certificados

El certificado del servidor es un archivo `.pfx` configurado en `appsettings.Production.json`. Un archivo renovado se detecta en un minuto sin reiniciar. Los nodos que usan streaming directo tienen su propia configuración de certificado en la página del nodo.

## Certificados de cámaras

Las cámaras a las que se accede por HTTPS tienen certificados autofirmados que no se pueden verificar contra nada, así que de forma predeterminada se acepta cualquier certificado. Activá **Configuración → Seguridad → Fijar certificados de cámaras** para que el servidor recuerde el certificado de cada dirección de cámara la primera vez que se conecta y, desde entonces, rechace uno distinto, en el servidor y en los nodos. Así, algo en la red que se haga pasar por una cámara no puede obtener su contraseña ni enviar eventos falsos.

Los certificados recordados aparecen debajo de la opción. Si el certificado de una cámara cambia de verdad (una actualización de firmware, un reinicio de fábrica o un certificado nuevo), seleccioná **Olvidar** junto a su dirección; se recuerda el próximo certificado que presente. Hasta entonces, cada conexión rechazada, en el servidor o en un nodo, queda en el registro de auditoría y se avisa a los Super Admins (como mucho una vez por hora por dirección de cámara), porque puede que algo en la red se esté haciendo pasar por la cámara. Una dirección de cámara se recuerda cuando el servidor la alcanza por primera vez, normalmente al agregar la cámara; un nodo acepta una dirección que el servidor todavía no alcanzó.

## Nombres de host

Por defecto el servidor responde cualquier nombre de host que use un navegador, así funciona igual por dirección IP, nombre corto y nombre completo. Para responder solo los nombres que usás, configurá `AllowedHosts` en `appsettings.Production.json` con ellos, separados por punto y coma (por ejemplo `"AllowedHosts": "vms.example.com;vms;localhost"`), y después `Restart-Service LarisVMSWeb`. Incluí cada nombre que usan los nodos, proxies y personas para llegar a él: cualquier otra cosa recibe un error.

Detrás de un proxy inverso (IIS ARR, nginx, y así), listá su dirección en `appsettings.Production.json` como `"ReverseProxy": { "KnownProxies": [ "10.0.0.2" ] }`, y después reiniciá. El servidor entonces toma la dirección real y el esquema del cliente desde los encabezados `X-Forwarded-For` y `X-Forwarded-Proto` de ese proxy, así las listas de permitidos y el registro de auditoría ven a la persona, no al proxy. Esos encabezados se ignoran desde cualquier cosa que no esté en la lista, y por completo mientras la lista está vacía.

## Actualizaciones

Estas verificaciones están entre una descarga y una instalación en ejecución:

- Las **descargas de versión** vienen con `SHA256SUMS.txt`. Verificá un instalador antes de ejecutarlo con `Get-FileHash <file> -Algorithm SHA256` y comparalo con la línea de ese archivo, o ejecutá `sha256sum -c SHA256SUMS.txt`. Cuando los instaladores están firmados con código, Windows también muestra el editor.
- Las **actualizaciones de nodo y proxy** las firma la clave de firma de este servidor. Un nodo o proxy conserva esa clave desde que se registró y no instala nada que no esté firmado con ella, aunque su conexión con el servidor fuera manipulada. También rechaza una compilación que no sea más nueva que la que corre. Ver [Instalación](/Help/installation#updates).
- **La firma del editor** (opcional): un nodo o proxy compilado con la clave del editor (`-PublisherKeyPath`) también necesita la firma del editor en cada actualización; las demás compilaciones omiten esta verificación. Esa clave se guarda fuera de línea y nunca en el servidor, así que ni siquiera un servidor vulnerado puede enviar su propio código a los nodos.
- **La conexión** al servidor se verifica como está configurado en **Nodos → Actualizaciones y registro → Verificación del certificado del servidor** (ver [Nodos](/Help/nodes#server-certificate)).

## Secretos cifrados

Las credenciales de cámaras, credenciales SMB, claves de nodo, secretos de correo, el secreto de cliente Entra y la contraseña de cuenta de servicio de Active Directory están cifrados en la base de datos, usando las claves de protección de datos en `%ProgramData%\LarisVMS\keys`. Solo SYSTEM, Administradores y las cuentas de los servicios de LarisVMS pueden abrir `%ProgramData%\LarisVMS`: cualquier otro acceso que haya, como una carpeta que un usuario local creó antes de instalar LarisVMS, se quita cuando inicia un servicio. Respaldá esa carpeta, y nunca la elimines durante un despliegue.

## Protección adicional para empresas

De fábrica, LarisVMS está configurado para que una instalación hogareña funcione sin pasos extra. Todo lo que sigue es opcional y viene desactivado; activá lo que le sirva a una empresa, o a un servidor que se pueda alcanzar desde internet:

- **Exigir inicio de sesión en dos pasos**: Configuración → Seguridad (ver [Inicio de sesión en dos pasos](/Help/users#two-factor-sign-in)).
- **Confirmar la contraseña en las páginas de administración**: Configuración → Seguridad (ver [Sesiones](/Help/users#sessions)).
- **Contraseñas temporales puestas por un administrador**: Configuración → Seguridad (ver [Contraseñas](/Help/users#passwords)).
- **Sesiones más cortas y un tiempo de inactividad**: cada rol en Configuración → Roles (ver [Roles](/Help/users#roles)).
- **Fijar certificados de cámaras**: Configuración → Seguridad (ver [Certificados de cámaras](#camera-certificates)).
- **Listas de permitidos**: Configuración → Seguridad (ver [Listas de permitidos](#allow-lists)).
- **El token de configuración desde cualquier otra computadora**: poné `"Setup": { "AlwaysRequireToken": true }` en `appsettings.Production.json` antes de usar el asistente de configuración (ver [Instalación](/Help/installation#web-server)).
- **Fijar el certificado del servidor al instalar un nodo o proxy**: `-ServerCertThumbprint` (ver [Certificado del servidor](/Help/nodes#server-certificate)).
- **Códigos de registro de un solo uso, con la clave compartida desactivada**: Nodos → Actualizaciones y registro (ver [Códigos de registro de un solo uso](/Help/nodes#one-time-registration-codes)).
- **Conexiones cifradas desde el servidor a los nodos**: en la página de cada nodo (ver [Conexiones cifradas desde el servidor](/Help/nodes#encrypted-connections-from-the-server)).
- **Reenviar el registro de auditoría a syslog**: Configuración → Registros (ver [Registro de auditoría](/Help/users#audit-log)).
- **Actualizaciones firmadas por el editor**, para compilaciones propias (ver [Actualizaciones](#updates)).
- **Una cuenta de servicio con menos privilegios.** Los servicios de LarisVMS se ejecutan como LocalSystem. Podés ejecutarlos con otra cuenta (`services.msc`, o `sc.exe config`), pero entonces te toca darle a esa cuenta lo que el servicio necesita: la carpeta de instalación, cada carpeta o recurso compartido de almacenamiento y de archivo, y los archivos de certificado. `%ProgramData%\LarisVMS` se resuelve solo: cada servicio de LarisVMS, al iniciar, lo limita a SYSTEM, Administradores y las cuentas con las que se ejecutan los servicios de LarisVMS, y quita cualquier otro acceso. Revisá el registro del servicio después del cambio.
