# Respaldos

Respaldos de la base de datos, y qué más hay que respaldar.

**Configuración → Respaldos** hace respaldos completos de SQL Server de la base de datos LarisVMS, en un horario diario o a pedido. Está pensado para SQL Express, que no tiene SQL Agent para programar sus propios respaldos.

- **Directorio de respaldo**: una carpeta en la que el *servicio de SQL Server* puede escribir, fuera de la carpeta de instalación de LarisVMS. Si SQL Server está en otra máquina, esta ruta debe existir en esa máquina, y los respaldos viejos no se limpian automáticamente.
- **Conservar últimos**: cuántos respaldos conservar (solo SQL Server local).

La restauración se hace fuera de LarisVMS, con SSMS o `sqlcmd`. Los respaldos son solo respaldos completos. En SQL Express, usá el modelo de recuperación SIMPLE.

## MariaDB

En MariaDB, los respaldos se hacen con `mariadb-dump` y se guardan como archivos `.sql.gz` comprimidos. `mariadb-dump` corre en el servidor LarisVMS, así que las herramientas de cliente de MariaDB deben estar instaladas ahí, aun cuando MariaDB misma corre en otra máquina. LarisVMS las encuentra en `C:\Program Files\MariaDB …`, o un Super Administrador puede configurar la ruta a `mariadb-dump.exe` en la página de Respaldos. Esa ruta tiene que estar en este servidor, en una carpeta que solo los administradores puedan cambiar (por ejemplo, dentro de Archivos de programa), porque LarisVMS la ejecuta con los permisos del propio servicio.

- **Directorio de respaldo**: una carpeta en el servidor LarisVMS (o un recurso compartido) en la que el *servicio LarisVMS* puede escribir, fuera de la carpeta de instalación. Los respaldos viejos siempre se limpian, corra MariaDB donde corra.
- El respaldo guarda toda la base de datos en una instantánea consistente, tomada sin detener la grabación.

Descomprimí el `.sql.gz` (7-Zip, por ejemplo), y después restaurálo con el cliente `mariadb`. El archivo crea la base de datos si falta y reemplaza sus tablas, así que primero detené el servicio web LarisVMS:

```powershell
cmd /c "mariadb -u root -p < larisvms_20261026_030000.sql"
```

## También respaldá

- **Las claves de protección de datos** en `%ProgramData%\LarisVMS\keys`. Las contraseñas de cámaras, los secretos de correo y las claves de nodo se cifran con ellas. Sin estas claves, una base de datos restaurada no puede descifrar ninguna.
- **`setup-generated.json`** en la carpeta de instalación, que guarda la cadena de conexión a la base de datos. Su contraseña solo funciona en la máquina que la guardó; en una máquina nueva, ejecutá el asistente de configuración de nuevo y apuntálo a la base de datos restaurada.

Las grabaciones no son parte del respaldo de la base de datos. Protegelas con el volumen de archivo o tus propios respaldos de almacenamiento.
