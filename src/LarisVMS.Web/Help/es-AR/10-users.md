# Usuarios y acceso

Cuentas, roles, permisos, acceso a cámaras, Active Directory e inicio de sesión con Microsoft.

## Usuarios

El registro propio está desactivado. Las cuentas se crean en **Configuración → Usuarios**, donde también podés asignar roles, deshabilitar una cuenta y restablecer una contraseña. Las cuentas locales inician sesión con su dirección de correo. Deshabilitar una cuenta la cierra en todos lados enseguida y corta su video en vivo.

## Contraseñas

Las contraseñas tienen al menos 12 caracteres. Se rechazan las comunes, las del tipo `123456789012` y las que contienen el nombre o el correo de la persona. Para rechazar más, ponelas en `%ProgramData%\LarisVMS\password-blocklist.txt` en el servidor web, una por línea (sirve una lista publicada de contraseñas filtradas). Se toma sin reiniciar.

Quien tiene una cuenta local cambia su propia contraseña seleccionando su nombre arriba de la página. Se pide la contraseña actual, y se cierran todas sus otras sesiones. Los administradores cambian la suya de la misma forma, no en **Configuración → Usuarios**. Las cuentas de Active Directory la cambian en Windows.

Cuando creás una cuenta o restablecés una contraseña en **Configuración → Usuarios**, podés marcar **Debe cambiarla en el próximo inicio de sesión**. La persona tiene que elegir su propia contraseña antes de poder hacer cualquier otra cosa. Con **Configuración → Seguridad → Contraseñas temporales puestas por un administrador** activado, empieza marcado.

Después de 5 contraseñas incorrectas para una cuenta desde una misma dirección en 15 minutos, esa dirección tiene que esperar esos 15 minutos; la persona puede seguir iniciando sesión desde cualquier otro lado. Una cuenta se bloquea 15 minutos solo después de 20 intentos incorrectos desde cualquier lado, y los Super Admins nunca se bloquean por contraseñas incorrectas al iniciar sesión. Los Super Admins reciben aviso de los fallos repetidos y los bloqueos, y de los intentos repartidos entre muchas redes: una cuenta que falla 10 veces desde 3 o más redes, o 50 fallos desde 10 o más redes en todo el servidor, en 15 minutos.

## Inicio de sesión en dos pasos

Quien inicia sesión con contraseña, local o de Active Directory, puede pedir además un código de seis dígitos de una app de autenticación en el teléfono (Microsoft Authenticator, Google Authenticator, 1Password y otras). Cada código sirve una sola vez, así uno que alguien vea escribir no se puede volver a usar. Para configurarlo, seleccioná tu nombre arriba de la página, después **Inicio de sesión en dos pasos**, escaneá el código QR e ingresá el código que muestra la app. Recibís diez códigos de recuperación; guardalos en un lugar seguro, porque cada uno te deja iniciar sesión una vez si perdés el teléfono. Cualquier cambio posterior, como códigos de recuperación nuevos o desactivarlo, pide un código actual.

Al iniciar sesión, **No volver a preguntar en este navegador por 14 días** saltea el código en ese navegador hasta que cambie la contraseña o la configuración en dos pasos.

Si alguien pierde el teléfono y sus códigos de recuperación, **Desactivar 2FA** junto a esa persona en **Configuración → Usuarios** le permite iniciar sesión solo con la contraseña y configurarlo de nuevo.

**Configuración → Seguridad → Exigir inicio de sesión en dos pasos** obliga a todos los que inician sesión con contraseña a configurarlo antes de poder hacer cualquier otra cosa, y no les deja desactivarlo. Los inicios de sesión con Microsoft quedan a cargo de los controles de Entra.

## Sesiones

Seleccioná tu nombre arriba de la página y después **Sesiones** para ver todos los navegadores donde tenés la sesión iniciada, con su dirección y cuándo se usaron por última vez. Cerrá las que no reconozcas, o todas menos la que estás usando, y después cambiá tu contraseña. Cambiar la contraseña o activar el inicio de sesión en dos pasos cierra tus otras sesiones al instante.

Con **Configuración → Seguridad → Confirmar la contraseña en las páginas de administración** activado, las páginas de administración vuelven a pedir tu contraseña (y un código, con el inicio de sesión en dos pasos activado) salvo que hayas iniciado sesión o la hayas confirmado en los últimos 15 minutos; usarlas mantiene ese plazo. Así, alguien que encuentre tu navegador con la sesión iniciada puede usar el sistema pero no cambiar su configuración, sus usuarios ni sus claves. Los inicios de sesión con Microsoft vuelven a iniciar sesión. Viene desactivado.

En **Configuración → Usuarios**, **Cerrar sesión** junto a una persona cierra su sesión en todos los navegadores. Puede volver a iniciar sesión; deshabilitá la cuenta para que no pueda entrar.

## Roles

Un rol es un conjunto de permisos con nombre, gestionado en **Configuración → Roles**. Cada rol también tiene:

- un **nivel de alcance**;
- un **vencimiento** para acceso por tiempo limitado (por ejemplo, 480 minutos para un turno de 8 horas);
- una **duración de sesión**: horas después de iniciar sesión antes de tener que iniciar sesión de nuevo, por más activo que esté (como máximo 30 días, o 0 para que no venza nunca);
- un **tiempo de inactividad**: minutos sin abrir una página antes de que se cierre la sesión (0 lo desactiva). Una pantalla que solo muestra video en vivo cuenta como inactiva, así que dejalo desactivado para los roles que miran un videowall;
- una **prioridad PTZ**, que decide quién gana cuando dos usuarios mueven la misma cámara.

## Permisos

**Configuración → Matriz de permisos** configura qué acciones puede hacer cada rol (ver cámaras, reproducción, exportación, editar configuración, y así). El rol integrado **Super Administrador** siempre tiene cada permiso.

**Iniciar y detener grabación manual** controla el botón de grabación en los mosaicos en vivo. También necesita el acceso a cámara **Grabar** de abajo.

## Acceso a cámaras

**Configuración → Acceso a cámaras** decide qué cámaras puede usar cada rol: todas las cámaras, un grupo o cámaras individuales, por separado para Ver, Reproducción, Exportación, PTZ, Hablar (audio bidireccional), Configurar y Grabar (grabación manual). Un rol **sin concesiones no tiene cámaras**; Super Administrador siempre tiene todas las cámaras. Exportación nunca llega más lejos que Reproducción. Actualizar desde una versión donde los roles sin concesiones podían usar todas las cámaras agrega una concesión de «todas las cámaras» para mantener igual el acceso de cada rol y usuario.

## Claves de API

**Admin → Claves de API** emite claves para la [API](/Help/api). Cada clave actúa con el rol al que está vinculada, y se puede limitar a algunos de los permisos de ese rol, así varias claves pueden hacer cosas distintas. El acceso a cámaras del rol decide qué cámaras puede usar una clave.

## Entra

**Configuración → Seguridad → Inicio de sesión con Entra ID** agrega un botón «Iniciar sesión con Microsoft». Nunca crea cuentas, y no empareja cuentas por correo: en **Configuración → Usuarios**, usá **Inicio de sesión con Microsoft** en la fila de una persona para ingresar su **ID de objeto** de Entra (desde su página de usuario en el centro de administración de Microsoft Entra). Su primer inicio de sesión con Microsoft completa el vínculo; vacío lo quita. Registrá una app en Entra con la URI de redirección que muestra la página de Seguridad, y después ingresá su ID de tenant (el GUID o dominio de tu directorio, no `common`), ID de cliente y secreto. Los cambios se aplican enseguida. Las cuentas ya vinculadas siguen funcionando.

## Active Directory

**Configuración → Active Directory** deja que los usuarios de dominio inicien sesión con su nombre de usuario y contraseña de AD, y les da roles desde sus grupos de AD. Solo un Super Admin puede cambiar el dominio, cómo se conecta LarisVMS a él, o el inicio de sesión local, porque eso decide quién puede iniciar sesión como cualquiera; los demás administradores pueden cambiar el intervalo de sincronización y los vínculos de grupos.

- **Dominio:** solo el nombre del dominio, como `corp.example.com`.
- **Cifrado:** LDAPS en el puerto 636 es el predeterminado y necesita un certificado en tus controladores de dominio en el que este servidor confíe. LDAP simple en el puerto 389 funciona sin uno, pero es inseguro y no se recomienda.
- **Cuenta de búsqueda:** la seguridad integrada (el predeterminado) usa la cuenta propia del servicio LarisVMS Web. Cuando el servicio corre como LocalSystem en un servidor unido al dominio, esa es la cuenta de la computadora. O ingresá una cuenta de servicio como `DOMAIN\username` con su contraseña.
- **Inicio de sesión:** un nombre con `@` es una cuenta local. Cualquier otra cosa es un nombre de usuario AD, escrito sin el dominio.

**Vínculos grupo → rol:** empezá a escribir un nombre de grupo y elegí de la lista (Tab toma la primera coincidencia). Los miembros del grupo reciben el rol, incluidos los miembros de grupos anidados. Las cuentas se crean la primera vez que se necesitan, en cuanto se vincula un grupo o cuando el usuario inicia sesión por primera vez. Los grupos y usuarios se rastrean por SID, así que renombrar cualquiera en AD se traslada en la próxima sincronización. Los roles de las cuentas AD vienen solo de los vínculos de grupo y no se pueden editar en la página de Usuarios.

**Sincronizar** corre cada 30 minutos por defecto (5 minutos a 24 horas), o enseguida con **Sincronizar ahora**. Una cuenta AD que está deshabilitada o vencida en AD, o que ya no está en ningún grupo vinculado, se deshabilita acá y se cierra la sesión. Si no se puede llegar a AD, la sincronización no cambia nada y lo reintenta en 5 minutos. Una cuenta deshabilitada por la sincronización se vuelve a habilitar cuando vuelve a calificar. Una deshabilitada a mano en la página de Usuarios queda deshabilitada.

El **inicio de sesión local** se puede desactivar mientras el inicio de sesión AD está activado, una vez que al menos una cuenta AD tiene Super Administrador. Guardá en un lugar seguro la cuenta Super Administrador creada durante la configuración, y su contraseña. Si AD queda no disponible, ejecutá esto contra la base de datos LarisVMS e iniciá sesión con esa cuenta. En SQL Server:

```sql
UPDATE ActiveDirectorySettings SET LocalLoginsEnabled = 1
```

En MariaDB:

```sql
UPDATE `ActiveDirectorySettings` SET `LocalLoginsEnabled` = 1;
```

## Registro de auditoría

Todo lo que hacen los usuarios se registra en **Registros → Registros de auditoría**: cambios (con valores viejos y nuevos; los secretos se registran solo como «cambiado»), ver cámaras, iniciar reproducción y exportar.

**A prueba de modificaciones.** Cada entrada se sella junto con la anterior, con una clave que no está solo en la base de datos. Cada 6 horas (y cuando seleccionás **Verificar ahora** en la página del registro de auditoría) se verifica todo el registro. Si una entrada se modificó, se eliminó o se agregó directamente en la base de datos, la página indica cuál y se avisa a los Super Admins. Las entradas que elimina la retención son esperables y no cuentan, y cada eliminación queda registrada (**AuditLog.Pruned**). Las entradas de antes de esta actualización no quedan cubiertas.

El servidor también guarda un pequeño registro fuera de la base de datos, en `%ProgramData%\LarisVMS\audit-chain.dat`, con la clave del sello y dónde empezó el registro, así no se puede volver a sellar con otra clave ni borrar y empezar de nuevo sin que se note. Si a propósito empezás de nuevo con una base de datos nueva y vacía en el mismo servidor, borrá ese archivo una vez; se crea uno nuevo con la próxima entrada.

**Reenviar a syslog.** Para conservar una copia del registro de auditoría fuera del servidor, ingresá un servidor syslog en **Configuración → Registros**. Cada entrada nueva se envía en el momento en que se escribe (RFC 5424, facility *log audit*, con la entrada en JSON). Elegí UDP, TCP o TLS. Con TLS, el servidor web debe confiar en el certificado del servidor syslog. Si no se puede conectar con el servidor syslog, las entradas se conservan y se envían cuando vuelva a estar disponible. El reenvío empieza con la próxima entrada, no con el historial existente. Solo un Super Admin puede cambiar a dónde se reenvía el registro, o desactivar el reenvío; ese cambio se envía primero al destino anterior.
