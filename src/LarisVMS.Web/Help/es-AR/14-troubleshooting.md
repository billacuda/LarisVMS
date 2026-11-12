# Solución de problemas

Problemas comunes, y dónde buscar.

## Dónde están los registros

- **Servidor**: **Registros → Registros del sistema**. Configurá el nivel y la retención en **Configuración → Registros**. Depurar y Trazar agregan detalle pero hacen archivos grandes.
- **Registro de eventos de cámara**: los eventos crudos que envían las cámaras, usados solo para listar los temas de eventos de una cámara para las etiquetas de eventos. Se conservan 30 días por defecto (**Configuración → Registros**, 0 los conserva para siempre); borrarlos no toca eventos, capturas ni grabaciones.
- **Mantenimiento de la base de datos**: cada noche alrededor de las 3 AM el servidor ordena los índices y estadísticas de la base de datos, y registra una línea diciendo qué hizo. Si su inicio de sesión a la base no puede, registra una advertencia en su lugar. En MariaDB esto se omite; MariaDB mantiene sus propias estadísticas.
- **Nodos**: `%ProgramData%\LarisVMS\logs\node-*.log` en cada nodo.
- **Detección de IA**: `%ProgramData%\LarisVMS\logs\vision-*.log` en cada nodo.
- **Actualización automática**: `%ProgramData%\LarisVMS\logs\updater-*.log` en cada nodo o proxy.

## Una cámara no está grabando

1. ¿Está **asignada a un nodo**, y ese nodo está **en línea**?
2. ¿El nodo tiene una **ruta de almacenamiento**? La lista de nodos marca los nodos sin una.
3. ¿La cámara está **habilitada**, y sus streams se ven bien? **Volvé a sondear** después de cualquier cambio en la propia cámara.
4. En modo Movimiento, Horario o Evento, verificá si las grabaciones se están descartando (ver [Grabación](/Help/recording#modes)).

## La vista en vivo y la reproducción quedan en «conectando»

Cuando un nodo transmite directo a los navegadores (ver [Streaming directo](/Help/nodes#direct-streaming)), el navegador se conecta al puerto de cliente del nodo, 4200 por defecto. Si la consola del navegador muestra una conexión fallida `wss://<node>:<port>/live/…`, verificá que el firewall del nodo permita ese puerto: el instalador crea una regla de entrada llamada **LarisVMS Node Client Endpoint**. Los instaladores anteriores a 0.215.1 podían perder esa regla al actualizar o tomar un nodo; ejecutá el instalador de Nodo actual de nuevo, o agregá la regla a mano.

## Falla el inicio de sesión o la sincronización de Active Directory

Usá **Probar conexión** en **Configuración → Active Directory**; el error de la última sincronización se muestra en la misma página.

- **No se puede llegar a un controlador de dominio:** verificá que el nombre del dominio se resuelva desde el servidor y que el puerto 636 (LDAPS) o 389 (LDAP) esté abierto a un controlador de dominio. Con LDAPS, el controlador de dominio necesita un certificado en el que este servidor confíe.
- **La identidad propia del servidor fue rechazada:** la seguridad integrada solo funciona cuando el servicio LarisVMS Web corre en un servidor unido al dominio, como LocalSystem o una cuenta de dominio. Si no, usá una cuenta de servicio.
- **«No está en ningún grupo que tenga acceso»:** el usuario inició sesión correctamente pero no está en un grupo vinculado. Los grupos se emparejan como grupos de seguridad, incluidos los anidados.
- **Bloqueado fuera de las páginas de administración después de desactivar el inicio de sesión local:** ver [Active Directory](/Help/users#active-directory) para el comando de base de datos que lo vuelve a activar.

## La vista en vivo se pausa en una ventana en segundo plano

Chrome limita las ventanas que no se ven, así que un muro en vivo en un segundo monitor puede congelarse y después ponerse al día. La vista en vivo se recupera sola. Para evitarlo, agregá esto al destino del acceso directo de Chrome:

```
--disable-backgrounding-occluded-windows --disable-background-timer-throttling
```

## Cada mosaico se congela, y después reconecta todos a la vez

El nodo se trabó, no el navegador ni la red. Buscá en el registro del nodo `Node process health: tick … ms late`. Un nodo sano registra esto unas una vez por minuto con latencia en milisegundos de un solo dígito.

- Si `gcPauseMs` está cerca de la latencia, la causa es la recolección de basura.
- Si `gcPauseMs` es chico pero `pendingWorkItems` está en decenas, hay hilos bloqueados. Capturá un dump mientras se traba: `dotnet-dump collect -p <LarisVMS.Node pid>`.

## La detección de IA se detiene pero la grabación sigue

Normalmente el driver de GPU se restableció. El registro de eventos del Sistema de Windows muestra el evento 153 de `nvlddmkm` o el 4101 de `Display` en ese momento. El servicio de visión se reinicia solo después de 60 segundos sin un resultado. Los restablecimientos repetidos apuntan a la GPU, su driver, la refrigeración o la alimentación.

## Un nodo o proxy no volvió después de una actualización

Iniciá su servicio (`sc start LarisVMSNode`, o `LarisVMSProxy`), y después revisá `updater-*.log` para ver por qué falló el reinicio. Una actualización descargada que nunca se aplicó también se nota en el registro propio del nodo o proxy cuando arranca la próxima vez, y se ofrece de nuevo en un chequeo posterior.

## Un nodo muestra «reloj»

El reloj del nodo está más de un minuto desfasado del servidor. Arreglá su sincronización horaria. La diferencia de reloj afecta los horarios y la línea de tiempo.

## Un nodo sigue sin poder reportarse

Usá **Restablecer auth** desde el menú **⋯** del nodo. Borra el token de chequeo del nodo, y el nodo se resincroniza en su próximo heartbeat.
