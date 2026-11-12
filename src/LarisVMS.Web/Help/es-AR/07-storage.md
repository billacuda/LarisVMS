# Almacenamiento y retención

Dónde viven las grabaciones, cuánto tiempo se conservan, la marca de agua y el volumen de archivo.

## Rutas de almacenamiento

Cada nodo graba en su propia **ruta de almacenamiento**, configurada en la página de edición del nodo o al instalar. No hay una ruta global. Un nodo sin ruta de almacenamiento no graba nada, y la lista de nodos lo marca.

## Retención

La **retención** es cuántos días se conservan las grabaciones. Se configura globalmente en **Configuración → Almacenamiento y retención**, y se puede anular por nodo y por cámara; gana el valor más específico. 0 conserva las grabaciones para siempre.

Una cámara también puede tener una **cuota de almacenamiento** en GB. Cuando la supera, primero se quitan sus grabaciones más antiguas.

## Marca de agua

La **marca de agua** es un límite duro de cuánto se puede llenar el disco de un nodo (90% por defecto). Arriba de él, el nodo archiva sus grabaciones más antiguas antes de tiempo, o las elimina si el archivado no está disponible, hasta que el disco vuelve a estar bajo el límite. La lista de nodos muestra **almacenamiento lleno** mientras esto pasa.

## Archivo

Un nodo puede tener una segunda **ruta de archivo** opcional, como un recurso SMB o un disco USB. Tiene que ser una ubicación distinta de la ruta de almacenamiento. Con el archivado activado, las grabaciones que vencen su retención se **mueven** al archivo en lugar de eliminarse, y se conservan hasta que se alcanza la **retención del archivo**. La retención del archivo se cuenta desde la fecha original de grabación, y 0 conserva para siempre.

La reproducción, las miniaturas y las exportaciones funcionan igual desde cualquier volumen. La lista de nodos muestra el espacio libre y una estimación de días restantes para ambos volúmenes. La estimación se basa en las últimas 24 horas de escritura.

## Grabaciones sobrantes

Cuando una cámara se mueve a otro nodo o se elimina, sus grabaciones viejas quedan en el nodo anterior hasta que vencen. El conteo ⚠️ junto al estado de un nodo muestra cuántas cámaras todavía tienen grabaciones ahí. Se limpia solo.

## Cifrado

Las grabaciones se pueden cifrar en los discos de los grabadores, así que un disco robado o descartado no muestra nada. Está desactivado por defecto.

**Configuralo** en **Configuración → Cifrado y firma**:

1. **Crear clave maestra.** Se conserva en este servidor y desbloquea cada grabación cifrada.
2. **Descargar un respaldo** de la clave maestra, protegido por una frase de contraseña que elegís. El cifrado no se puede activar hasta que tenés uno. Guardá el archivo y la frase en un lugar distinto de este servidor: si el servidor se reconstruye, **Restaurar** con el respaldo es la única manera de volver a leer las grabaciones cifradas. Perdé ambos y las grabaciones se fueron para siempre.
3. **Elegí qué cifrar:** **Video** (grabaciones y audio de intercomunicación bidireccional), o **Video, capturas y miniaturas**. Ese es el predeterminado para cada cámara; cada cámara puede anularlo en su propia página (**Cámaras → Editar**, Almacenamiento).

Cada segmento se cifra en el grabador un momento después de terminar de grabarse, así que solo el segmento que se está grabando queda sin cifrar en el disco. Las grabaciones hechas antes de activar el cifrado quedan como están; **cifrar grabaciones existentes** en la página de la cámara las convierte en segundo plano. Desactivar el cifrado después deja las grabaciones cifradas legibles.

La reproducción, las miniaturas, las capturas y las exportaciones funcionan igual que antes. Los archivos exportados no se cifran, ya que están pensados para entregar. El grabador trae la clave diaria de cada cámara desde este servidor cuando la necesita y la conserva solo en memoria, así que una grabación cifrada no se puede reproducir mientras el grabador no puede llegar al servidor.
