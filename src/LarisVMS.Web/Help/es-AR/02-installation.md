# Instalación y actualizaciones

Instalar el servidor web, los nodos grabadores y los proxies de medios, y mantenerlos al día.

Cada parte tiene su propio instalador: `LarisVMS-Web`, `LarisVMS-Node` y `LarisVMS-Proxy` (`.msi`). Hacé doble clic en uno para que te pida su configuración, o pasala desde la línea de comandos con `msiexec /i <file>.msi NAME=value … /qn`. Todo lo que no pases te lo pide, y una actualización reutiliza la configuración de la instalación anterior.

Cada parte también funciona en Linux, desde un paquete `.deb` o `.rpm` (ver [Linux](#linux)), y se pueden mezclar: un servidor Windows puede tener nodos y proxies Linux, y al revés.

## Servidor web

1. Tené un SQL Server accesible desde el servidor (seguridad integrada o autenticación SQL), o un servidor MariaDB (ver [MariaDB](#mariadb)).
2. Ejecutá `LarisVMS-Web.msi`. Configuración:
   - `HTTPSPORT`: el puerto HTTPS (predeterminado 8444). El instalador lo abre en el firewall, para TCP y para UDP ([HTTP/3](/Help/security#http3)).
   - `CERTPATH`, `CERTPASSWORD`: el certificado del servidor (`.pfx`). Dejalo vacío para usar un certificado autofirmado hasta que tengas uno. El instalador verifica que el archivo se abra con la contraseña antes de continuar; un recurso compartido también tiene que ser legible por la cuenta del servicio.
   - `SERVICEACCOUNT`, `SERVICEPASSWORD`: ejecutar el servicio como una cuenta específica, por ejemplo una con derechos de seguridad integrada de SQL. Vacío lo ejecuta como LocalSystem. No hace falta para MariaDB.
3. Abrí `https://<host>:8444/` en el navegador. El asistente de configuración te guía por la base de datos, la primera cuenta de administrador, el almacenamiento y el registro de nodos. Un navegador en el mismo servidor o en la red local entra directamente. Desde cualquier otro lugar primero pide el token de configuración que está en `%ProgramData%\LarisVMS\setup-token.txt` en el servidor (`/var/lib/larisvms/setup-token.txt` en Linux; solo los administradores pueden leerlo), así nadie en internet puede configurarlo antes. Para pedir el token desde cualquier otra computadora, incluida la red local, poné `"Setup": { "AlwaysRequireToken": true }` en `appsettings.Production.json` antes de abrir el asistente. El archivo se borra cuando ya existe la cuenta de administrador.

La contraseña de un inicio de sesión SQL, o de MariaDB, se guarda cifrada en el servidor (DPAPI en Windows, o una clave atada al ID de la máquina en Linux; solo en esta máquina), así que `setup-generated.json` no se puede copiar a otra máquina con la contraseña funcionando. En su lugar, ejecutá el asistente de nuevo ahí.

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

Para la detección de IA, las máquinas NVIDIA también necesitan CUDA 12 y cuDNN 9; en Windows el instalador copia sus DLL en su lugar cuando los encuentra. Ver [Detección de IA](/Help/ai-detection#hardware).

Después de instalar, el nodo aparece en **Nodos** como **esperando aprobación**. No graba nada ni recibe cámaras hasta que hacés clic en **Aprobar**, así que solo aprobá un nodo que instalaste vos mismo. Después asignale cámaras desde la página de edición de cada cámara.

## Proxies de medios

Ejecutá `LarisVMS-Proxy.msi` con `SERVERURL`, `REGISTRATIONKEY` y `CLIENTPORT` (predeterminado 4443), más las mismas opciones opcionales de certificado que un nodo. Un proxy nuevo arranca desactivado: activá **Habilitado** en **Configuración → Proxies de medios**, y después elegilo en la página de cada nodo (ver [Nodos](/Help/nodes#proxies)).

## Linux

El servidor web, los nodos y los proxies se instalan desde paquetes `.deb` (Ubuntu 22.04, 24.04, 26.04, Debian 12, 13) o `.rpm` (Rocky Linux 9, 10 y otras derivadas de RHEL 9 o 10), para x86_64. Cada uno corre como servicio de systemd (`larisvms-web`, `larisvms-node`, `larisvms-proxy`) con su propia cuenta `larisvms`. El programa va en `/opt/larisvms`, la configuración en `/etc/larisvms`, los datos (claves, modelos, el token de configuración) en `/var/lib/larisvms` y los registros en `/var/log/larisvms`. No hace falta el runtime de .NET. Al instalar se abren los puertos del servicio en firewalld o ufw, si alguno está activo.

- **Servidor web:** `sudo apt install ./larisvms-web_<version>-1_amd64.deb` (o `sudo dnf install ./larisvms-web-<version>-1.x86_64.rpm`). Arranca enseguida; abrí `https://<host>:8444/` para el asistente de configuración. El puerto y el certificado se configuran en `/etc/larisvms/web.env` (`Kestrel__HttpsPort`, `Kestrel__Certificates__Default__Path`, `Kestrel__Certificates__Default__Password`; la cuenta larisvms tiene que poder leer el archivo), y después `sudo systemctl restart larisvms-web`. `appsettings.Production.json` y `setup-generated.json` también están en `/etc/larisvms`. SQL Server necesita un inicio de sesión SQL en Linux; MariaDB es la opción más simple ahí.
- **Nodos:** instalá `larisvms-node` y después vinculalo con `sudo larisvms-node setup --server-url https://<server>:8444 --registration-key <key>`. El paso Nodo del asistente de configuración muestra este comando con la clave y la huella del certificado del servidor ya puestas. Graba en `/var/lib/larisvms/recordings` salvo que agregues `--storage-root <folder>`; un recurso compartido de red tiene que estar montado antes (por ejemplo, en `/etc/fstab`) y la cuenta larisvms tiene que poder escribir en él. Otras opciones: `--archive-root`, `--live-port`, `--live-allow-from`, `--client-port`, `--client-pfx-path`, `--client-endpoint-host` y `--ffmpeg-path`. `sudo larisvms-node setup --help` las enumera todas, y ejecutar `setup` de nuevo las cambia.
- **ffmpeg** viene de la distribución y se instala con el nodo. En Rocky Linux viene de RPM Fusion, así que habilitá EPEL, CRB y RPM Fusion antes de instalar un nodo.
- **Proxies:** instalá `larisvms-proxy` y después `sudo larisvms-proxy setup --server-url https://<server>:8444 --registration-key <key> --client-port 4443`.

Revisá un servicio con `systemctl status larisvms-node` y `journalctl -u larisvms-node`, y mostrá su versión con `larisvms-node --version`.

**Docker:** cada versión también se publica como imágenes en el registro de contenedores de GitHub: `ghcr.io/billacuda/larisvms-web`, `larisvms-node` (con las etiquetas `-cuda` y `-tensorrt` para GPU NVIDIA) y `larisvms-proxy`. El servidor web necesita una base de datos que tengas vos (MariaDB 10.11 o posterior, o SQL Server con un inicio de sesión SQL). Instalalo de una de estas formas:

- **Docker Compose:** descargá [compose.yml](https://github.com/billacuda/LarisVMS/blob/main/docker/compose.yml) y ejecutá `docker compose up -d` en su carpeta. Arranca el servidor web; abrí `https://<host>:8444/` para el asistente de configuración. Después descomentá el nodo (y el proxy, si querés uno) en el archivo, completá la dirección del servidor, la clave de registro y la carpeta de grabaciones, y ejecutá `docker compose up -d` de nuevo.
- **Stack de Portainer:** en **Stacks → Add stack → Web editor**, pegá el mismo archivo y elegí **Deploy the stack**. Después del asistente, descomentá el nodo en la pestaña **Editor** del stack y elegí **Update the stack**. Usá un entorno Docker independiente, no un Swarm.
- **Docker solo:** arrancá el servidor web, ejecutá el asistente y después arrancá el nodo:

```bash
docker run -d --name larisvms-web --network host --restart unless-stopped \
  -e Kestrel__HttpsPort=8444 \
  -v /srv/larisvms/web/data:/var/lib/larisvms \
  -v /srv/larisvms/web/config:/etc/larisvms \
  -v /srv/larisvms/web/logs:/var/log/larisvms \
  ghcr.io/billacuda/larisvms-web:latest

docker run -d --name larisvms-node --hostname larisvms-node-1 --network host --restart unless-stopped \
  -e LARISVMS_SERVER_URL=https://<server address>:8444 \
  -e LARISVMS_REGISTRATION_KEY=<key> \
  -e LARISVMS_STORAGE_ROOT=/recordings \
  -v /srv/larisvms/node/data:/var/lib/larisvms \
  -v /srv/larisvms/node/config:/etc/larisvms \
  -v /srv/larisvms/node/logs:/var/log/larisvms \
  -v /srv/larisvms/recordings:/recordings \
  ghcr.io/billacuda/larisvms-node:latest
```

Después aprobá el nodo en **Nodos**. Como dirección del servidor usá su dirección o nombre en la LAN, no `localhost`, aun en la misma máquina. Si la máquina tiene firewall, permití TCP 8444 (servidor web), 8554 (nodo), 8555 (un nodo que transmite directo a los navegadores) y 4443 (proxy), y UDP 8444 si usás HTTP/3. Los contenedores corren como usuario y grupo 8444, dueño de sus carpetas en el host.

El paso Nodo del asistente de configuración y la página **Proxies de medios** muestran un stack de Compose para Portainer o `docker compose` con la dirección, la clave de registro y el certificado de este servidor ya puestos. Las configuraciones son las mismas variables de entorno que usan los servicios de Linux (`LARISVMS_SERVER_URL`, `LARISVMS_REGISTRATION_KEY`, `LARISVMS_STORAGE_ROOT`, etc.). Mantené `/var/lib/larisvms` en un volumen: ahí están las claves, el registro y el ID de máquina que permite leer las contraseñas guardadas cuando se vuelve a crear el contenedor. Usá la red del host, así funciona la búsqueda de cámaras. Para actualizar, ejecutá `docker compose pull` y `docker compose up -d`, elegí **Update the stack** con **Re-pull image and redeploy** activado en Portainer, o, con Docker solo, descargá la imagen, quitá el contenedor y ejecutá su comando de nuevo.

**Pasar una instalación de Linux a Docker:** copiá la carpeta de datos (sobre todo `keys`) y el ID de máquina antes del primer arranque del contenedor, y respaldá antes la base de datos. La contraseña de la base de datos y los registros de nodos se cifran con el ID de máquina, y las contraseñas y claves de nodo de la base de datos con `keys`; sin ellos, páginas como **Nodos** muestran un error. Una instalación de Windows no se puede pasar así. Para el servidor web:

```bash
sudo systemctl disable --now larisvms-web
sudo mkdir -p /srv/larisvms/web/data /srv/larisvms/web/config
sudo tar -C /var/lib/larisvms --exclude=./recordings --exclude=./node.config --exclude=./proxy.config -cf - . \
  | sudo tar -C /srv/larisvms/web/data -xf -
sudo cp -a /etc/larisvms/. /srv/larisvms/web/config/
sudo install -m 600 /etc/machine-id /srv/larisvms/web/data/machine-id
```

Pasá las configuraciones de `/etc/larisvms/web.env` al `environment` del stack y después arrancá el contenedor. Se conecta a la misma base de datos, sin el asistente. Nunca tengas el servicio viejo y el contenedor en marcha al mismo tiempo. Para un nodo, dejá las grabaciones donde están:

```bash
sudo systemctl disable --now larisvms-node
sudo mkdir -p /srv/larisvms/node/data
sudo tar -C /var/lib/larisvms --exclude=./recordings --exclude=./keys --exclude=./update --exclude=./proxy.config -cf - . \
  | sudo tar -C /srv/larisvms/node/data -xf -
sudo install -m 600 /etc/machine-id /srv/larisvms/node/data/machine-id

# The container's account needs to own the recordings (repeat for an archive folder):
sudo chown -R 8444:8444 /var/lib/larisvms/recordings
```

Pasá las configuraciones de `/etc/larisvms/node.env` al `environment` del stack, y montá cada carpeta de grabaciones y de archivo en la misma ruta dentro del contenedor (por ejemplo `/var/lib/larisvms/recordings:/var/lib/larisvms/recordings`, con `LARISVMS_STORAGE_ROOT` apuntando a ella). El nodo vuelve como él mismo, con sus cámaras y grabaciones, y no necesita clave de registro ni aprobación. Un proxy se pasa igual, sin las grabaciones. Al quitar después los paquetes viejos se cierran los puertos en el firewall, así que volvé a abrirlos para los contenedores.

Si ya ejecutaste el asistente en el contenedor con la base de datos vieja, solo faltan las claves. Copialas junto a las del contenedor y conservá ambas:

```bash
docker stop larisvms-web
sudo docker cp /var/lib/larisvms/keys/. larisvms-web:/var/lib/larisvms/keys/
docker start larisvms-web
```

Donde la Ayuda nombra una carpeta de Windows, la de Linux es: `%ProgramData%\LarisVMS` → `/var/lib/larisvms` (su carpeta `logs` → `/var/log/larisvms`), y `appsettings.Production.json` y `setup-generated.json` de la carpeta de instalación → `/etc/larisvms`. `Restart-Service LarisVMSWeb` es `sudo systemctl restart larisvms-web`.

## Actualizaciones

**Actualización:** ejecutá el `.msi` más nuevo. Encuentra la instalación existente y va directo a **Listo para actualizar**; la configuración se recuerda, así que no hay que ingresar nada de nuevo salvo la contraseña de una cuenta de servicio de dominio. El servidor web aplica los cambios de base de datos solo al arrancar. Las actualizaciones nunca tocan las grabaciones, los archivos de configuración, el registro de nodos ni las claves de protección de datos, y una instalación fallida se revierte.

**Los nodos y proxies se actualizan solos.** Una instalación web nueva registra las compilaciones de nodo y de proxy que trae como **Pendientes** en la sección **Compilaciones** de **Nodos**. Solo la compilación de nodo más nueva y la de proxy más nueva esperan aprobación; las anteriores quedan **Reemplazadas**. **Historial de compilaciones** enumera todas las compilaciones. Aprobá una, y cada nodo anterior descarga, verifica e instala su compilación en su próximo chequeo. El servidor firma cada actualización con su clave de firma (ver **Configuración → Cifrado y firma**); los nodos y proxies conservan esa clave desde que se registraron y no instalan nada que no esté firmado con ella, aunque su conexión con el servidor fuera manipulada. Desactivá esto con **Actualización automática** en **Nodos → Actualizaciones y registro**.

**Versiones nuevas:** una vez por día (y cuando arranca el servicio) el servidor verifica las versiones de LarisVMS en GitHub. Cuando hay una versión más nueva, quienes pueden cambiar la configuración ven **✨ La versión X está disponible** en la barra superior, con un enlace a sus notas de versión. Nada de tu instalación se envía. Desactivalo con **Versiones nuevas** en **Nodos → Actualizaciones y registro**.

**En Linux,** actualizá el servidor web instalando el paquete más nuevo de la misma forma; conserva `/etc/larisvms` y `/var/lib/larisvms`. Los nodos y proxies Linux se actualizan solos desde el servidor como los de Windows, una vez que aprobás la compilación: un pequeño ayudante que corre como root instala la actualización que el servicio descargó y verificó. Un servidor Linux ofrece a los nodos Windows su compilación de Windows, y un servidor Windows ofrece a los nodos Linux la suya.

**Desinstalar** quita el programa, su servicio y las reglas del firewall. Las grabaciones, la configuración y `%ProgramData%\LarisVMS` se conservan. En Linux, `apt remove` o `dnf remove` hace lo mismo y conserva `/var/lib/larisvms`, `/var/log/larisvms` y `/etc/larisvms`.

## Instalación desde el código fuente

Los desarrolladores pueden compilar e instalar desde el repositorio con PowerShell: `install-web.ps1` (servidor), y los scripts `install-node.ps1` / `install-proxy.ps1` en los paquetes que compilan `build-node.ps1` / `build-proxy.ps1`. `build-installers.ps1` compila los tres MSI, y los paquetes `.deb` y `.rpm` de Linux (con nFPM). Un MSI actualiza en el lugar una máquina instalada con scripts, conservando su configuración, su registro y cualquier endpoint de streaming directo con su regla de firewall.

## Certificados

`Kestrel:Certificates:Default:Path` y `:Password` en `appsettings.Production.json` apuntan a un archivo `.pfx` (en Linux, también se puede configurar en `/etc/larisvms/web.env` como `Kestrel__Certificates__Default__Path` y `__Password`). El servidor lo revisa cada 60 segundos y cambia a un certificado renovado sin reiniciar.

En Docker, montá la carpeta del certificado (`/srv/larisvms/certs:/certs:ro`) y usá la ruta dentro del contenedor, como `/certs/vms.pfx`. El usuario 8444 tiene que poder leerlo, y un `$` en la contraseña se escribe `$$` en un archivo de Compose. Un certificado que no se puede cargar se reemplaza por uno autofirmado, y `docker logs` dice por qué.
