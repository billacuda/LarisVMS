# Instalación y actualizaciones

Instalar el servidor web, los nodos grabadores y los proxies de medios, y mantenerlos al día.

Cada parte tiene su propio instalador: `LarisVMS-Web`, `LarisVMS-Node` y `LarisVMS-Proxy` (`.msi`). Hacé doble clic en uno para que te pida su configuración, o pasala desde la línea de comandos con `msiexec /i <file>.msi NAME=value … /qn`. Todo lo que no pases te lo pide, y una actualización reutiliza la configuración de la instalación anterior.

## Servidor web

1. Tené un SQL Server accesible desde el servidor (seguridad integrada o autenticación SQL), o un servidor MariaDB (ver [MariaDB](#mariadb)).
2. Ejecutá `LarisVMS-Web.msi`. Configuración:
   - `HTTPSPORT`: el puerto HTTPS (predeterminado 8444). El instalador lo abre en el firewall, para TCP y para UDP ([HTTP/3](/Help/security#http3)).
   - `CERTPATH`, `CERTPASSWORD`: el certificado del servidor (`.pfx`). Dejalo vacío para usar un certificado autofirmado hasta que tengas uno. El instalador verifica que el archivo se abra con la contraseña antes de continuar; un recurso compartido también tiene que ser legible por la cuenta del servicio.
   - `SERVICEACCOUNT`, `SERVICEPASSWORD`: ejecutar el servicio como una cuenta específica, por ejemplo una con derechos de seguridad integrada de SQL. Vacío lo ejecuta como LocalSystem. No hace falta para MariaDB.
3. Abrí `https://<host>:8444/` en el navegador. El asistente de configuración te guía por la base de datos, la primera cuenta de administrador, el almacenamiento y el registro de nodos. Un navegador en el mismo servidor o en la red local entra directamente. Desde cualquier otro lugar primero pide el token de configuración que está en `%ProgramData%\LarisVMS\setup-token.txt` en el servidor (solo los administradores pueden leerlo), así nadie en internet puede configurarlo antes. Para pedir el token desde cualquier otra computadora, incluida la red local, poné `"Setup": { "AlwaysRequireToken": true }` en `appsettings.Production.json` antes de abrir el asistente. El archivo se borra cuando ya existe la cuenta de administrador.

La contraseña de un inicio de sesión SQL, o de MariaDB, se guarda cifrada en el servidor (DPAPI, solo en esta máquina), así que `setup-generated.json` no se puede copiar a otra máquina con la contraseña funcionando. En su lugar, ejecutá el asistente de nuevo ahí.

Si apuntás el asistente a una base de datos LarisVMS existente (por ejemplo después de reinstalar el servidor), la usa tal cual: la actualiza si hace falta, nada se siembra ni se sobrescribe, y los pasos restantes se omiten. Una base que no es de LarisVMS se rechaza. Una vez completada la configuración, el asistente no se puede volver a abrir.

El servidor no necesita runtime de .NET; ya está incluido. El certificado también se puede cambiar después en `appsettings.Production.json` en la carpeta de instalación. Un `.pfx` renovado en la misma ruta se detecta en un minuto, sin reiniciar.

## MariaDB

LarisVMS puede usar MariaDB 10.11 o posterior en lugar de SQL Server. En el asistente de configuración, elegí **MariaDB** e ingresá el host, el puerto (predeterminado 3306), el nombre de la base de datos, y un usuario y contraseña de MariaDB. MariaDB no tiene inicio de sesión de Windows para que LarisVMS lo use, así que siempre hacen falta un usuario y una contraseña.

- **El usuario** necesita permiso para crear la base de datos. O creala vos primero, con el juego de caracteres `utf8mb4`, y dale al usuario `ALL` sobre ella:

  ```sql
  CREATE DATABASE larisvms CHARACTER SET utf8mb4 COLLATE utf8mb4_uca1400_as_ci;
  CREATE USER 'larisvms'@'%' IDENTIFIED BY 'a-strong-password';
  GRANT ALL ON larisvms.* TO 'larisvms'@'%';
  ```

- **Cifrado:** la conexión usa TLS. El certificado del servidor se verifica, salvo que MariaDB esté en esta máquina, o que marques **Confiar en el certificado de este servidor** (para un certificado autofirmado en otra máquina).
- **MariaDB anterior a 11.4 tiene TLS desactivado** hasta que recibe un certificado, y LarisVMS no va a enviar la contraseña a otra máquina sin él. Agregá el certificado y su clave a la sección `[mysqld]` de `my.ini`, reiniciá MariaDB, y marcá **Confiar en el certificado de este servidor** si es autofirmado:

  ```ini
  [mysqld]
  ssl_cert = C:/MariaDB/certs/server-cert.pem
  ssl_key = C:/MariaDB/certs/server-key.pem
  ```

  MariaDB 11.4 y posteriores configuran un certificado solos.
- Los **respaldos** necesitan las herramientas de cliente de MariaDB en el servidor LarisVMS (ver [Respaldos](/Help/backups)).
- Una instalación existente de SQL Server no se puede mover a MariaDB; MariaDB es para instalaciones nuevas.

## Nodos

Ejecutá `LarisVMS-Node.msi` en cada máquina que graba. Necesita:

- `SERVERURL` y `REGISTRATIONKEY`: la dirección del servidor y la **clave de registro** en **Nodos → Actualizaciones y registro**. Solo se usan la primera vez que el nodo se registra. Cambiar la clave no afecta a los nodos existentes.
- **FFmpeg**, que no viene incluido. Instalalo primero con `winget install ffmpeg --scope machine`. El instalador lo encuentra, o configurá `FFMPEGPATH`.
- `STORAGEROOT`: dónde graba el nodo. También se puede configurar después en la página de edición del nodo.

Opcional: `ARCHIVEROOT`, `LIVEPORT` (predeterminado 8554), `LIVEALLOWFROM` (direcciones permitidas por el firewall hacia el puerto en vivo, como IPs o bloques CIDR separados por comas: el servidor LarisVMS y los proxies que haya; vacío permite cualquiera), `INSECURETLS=1` (ya no hace falta: los nodos confían en el certificado autofirmado del servidor en el primer uso, ver [Certificado del servidor](/Help/nodes#server-certificate)), el endpoint de streaming directo (`CLIENTPORT`, `CLIENTENDPOINTHOST`, `CLIENTPFXPATH`, `CLIENTPFXPASSWORD`), y `SERVICEACCOUNT` / `SERVICEPASSWORD` para una cuenta que pueda llegar al almacenamiento SMB.

Para la detección de IA, las máquinas NVIDIA también necesitan el CUDA Toolkit 12.x y cuDNN 9.x; el instalador copia sus DLL en su lugar cuando los encuentra. Ver [Detección de IA](/Help/ai-detection#hardware).

Después de instalar, el nodo aparece en **Nodos** como **esperando aprobación**. No graba nada ni recibe cámaras hasta que hacés clic en **Aprobar**, así que solo aprobá un nodo que instalaste vos mismo. Después asignale cámaras desde la página de edición de cada cámara.

## Proxies de medios

Ejecutá `LarisVMS-Proxy.msi` con `SERVERURL`, `REGISTRATIONKEY` y `CLIENTPORT` (predeterminado 4443), más las mismas opciones opcionales de certificado que un nodo. Un proxy nuevo arranca desactivado: activá **Habilitado** en **Configuración → Proxies de medios**, y después elegilo en la página de cada nodo (ver [Nodos](/Help/nodes#proxies)).

## Actualizaciones

**Actualización:** ejecutá el `.msi` más nuevo. Encuentra la instalación existente y va directo a **Listo para actualizar**; la configuración se recuerda, así que no hay que ingresar nada de nuevo salvo la contraseña de una cuenta de servicio de dominio. El servidor web aplica los cambios de base de datos solo al arrancar. Las actualizaciones nunca tocan las grabaciones, los archivos de configuración, el registro de nodos ni las claves de protección de datos, y una instalación fallida se revierte.

**Los nodos y proxies se actualizan solos.** Una instalación web nueva registra las compilaciones de nodo y de proxy que trae como **Pendientes** en la sección **Compilaciones** de **Nodos**. Solo la compilación de nodo más nueva y la de proxy más nueva esperan aprobación; las anteriores quedan **Reemplazadas**. **Historial de compilaciones** enumera todas las compilaciones. Aprobá una, y cada nodo anterior descarga, verifica e instala su compilación en su próximo chequeo. El servidor firma cada actualización con su clave de firma (ver **Configuración → Cifrado y firma**); los nodos y proxies conservan esa clave desde que se registraron y no instalan nada que no esté firmado con ella, aunque su conexión con el servidor fuera manipulada. Desactivá esto con **Actualización automática** en **Nodos → Actualizaciones y registro**.

**Versiones nuevas:** una vez por día (y cuando arranca el servicio) el servidor verifica las versiones de LarisVMS en GitHub. Cuando hay una versión más nueva, quienes pueden cambiar la configuración ven **✨ La versión X está disponible** en la barra superior, con un enlace a sus notas de versión. Nada de tu instalación se envía. Desactivalo con **Versiones nuevas** en **Nodos → Actualizaciones y registro**.

**Desinstalar** quita el programa, su servicio y las reglas del firewall. Las grabaciones, la configuración y `%ProgramData%\LarisVMS` se conservan.

## Instalación desde el código fuente

Los desarrolladores pueden compilar e instalar desde el repositorio con PowerShell: `install-web.ps1` (servidor), y los scripts `install-node.ps1` / `install-proxy.ps1` en los paquetes que compilan `build-node.ps1` / `build-proxy.ps1`. `build-installers.ps1` compila los tres MSI. Un MSI actualiza en el lugar una máquina instalada con scripts, conservando su configuración, su registro y cualquier endpoint de streaming directo con su regla de firewall.

## Certificados

`Kestrel:Certificates:Default:Path` y `:Password` en `appsettings.Production.json` apuntan a un archivo `.pfx`. El servidor lo revisa cada 60 segundos y cambia a un certificado renovado sin reiniciar.
