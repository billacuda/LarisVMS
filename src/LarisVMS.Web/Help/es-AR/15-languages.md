# Idiomas

Mostrá LarisVMS en otro idioma, y agregá tu propia traducción.

## Elegir un idioma

Elegí un idioma desde el menú 🌐 arriba de cualquier página, incluida la página de inicio de sesión. Tu elección te sigue a cada dispositivo donde iniciás sesión. El menú solo aparece una vez que hay más de un idioma instalado.

Un administrador configura el idioma para todos los que no eligieron uno en **Configuración → Idioma**. Dejalo en **El idioma del navegador** para seguir el navegador de cada persona.

Las fechas, las horas y los números también siguen el idioma. Si tu navegador usa el mismo idioma con un formato regional (inglés británico, digamos), se conserva el formato regional.

Las alertas y notificaciones llegan a cada persona en su propio idioma. Los canales de equipo (correo a una dirección fija, webhook, ntfy, Pushover, Slack y Teams) usan el idioma predeterminado. El registro de auditoría queda en inglés.

El texto que un idioma todavía no traduce se muestra en inglés.

## Agregar un idioma

No hace falta programar: un idioma es un archivo de texto.

1. En **Configuración → Idioma**, descargá **en.json**, el texto en inglés.
2. Traducí el texto a la derecha de cada línea. Conservá la clave de la izquierda exactamente como está.
3. Subí el archivo en **Configuración → Idioma**, o copialo a `%ProgramData%\LarisVMS\locales` en el servidor. Nombralo por su código de idioma, por ejemplo `fr.json`, `de.json` o `pt-BR.json`.

Está disponible enseguida, sin reiniciar. Un archivo que no se puede leer se enumera en la página de Idioma con el motivo, y el resto de LarisVMS sigue funcionando.

### Qué conservar tal cual

- **Cualquier cosa entre {llaves}** la completa LarisVMS, como `{camera}` o `{count}`. Movela donde corresponda en tu oración, pero no la traduzcas ni la renombres.
- **Algunas etiquetas** pueden aparecer en el texto: `<code>`, `<strong>`, `<em>`, `<b>`, `<i>`, `<kbd>`, `<br>` y `<a href="…">`. Conservalas alrededor de las mismas palabras. El otro HTML no está permitido y se muestra como texto simple.
- La sección **_meta** arriba guarda el nombre del idioma como aparece en el menú 🌐 (por ejemplo «Français»), y tu nombre, si querés crédito.

### Plurales

Algún texto cambia con un número. En inglés hay dos formas:

```json
"cameras.count_one": "{count} camera",
"cameras.count_other": "{count} cameras"
```

Agregá las formas que tu idioma necesita, con estas terminaciones: `_zero`, `_one`, `_two`, `_few`, `_many` y `_other`. El ruso, por ejemplo, usa `_one`, `_few` y `_many`. Incluí siempre `_other`; se usa cuando ninguna otra forma encaja.

### Páginas de ayuda

Las páginas de ayuda se traducen un archivo a la vez. Copiá el Markdown de una página a `locales\<code>\help\` con el mismo nombre de archivo (por ejemplo `locales\fr\help\03-cameras.md`) y traducira. Las páginas que no tradujiste quedan en inglés.

### Actualizar una traducción

Las versiones nuevas de LarisVMS agregan texto. La columna **Traducido** en la página de Idioma muestra cuánto del texto actual cubre cada idioma. **Descargar** junto a un idioma te da su archivo con el texto faltante completo desde el inglés, listo para traducir.

Para verificar un archivo antes de subirlo, ejecutá `tools\locale-check.ps1 fr.json`. Enumera claves faltantes, claves que esta versión no usa, y {marcadores} que no coinciden con el texto en inglés. Agregá `-Help` con una carpeta de páginas de Ayuda traducidas (por ejemplo `-Help locales\fr\help`) para verificarlas también: páginas sin traducir, nombres de archivo que LarisVMS no usa, HTML, y encabezados que no coinciden con la página en inglés.

### Probar una traducción

Activá **Ofrecer el idioma pseudo en el menú de idiomas** en **Configuración → Idioma**, y después elegilo desde el menú 🌐. Cada texto se muestra acentuado y como un tercio más largo, como `[Ŝàṽé ··]`. El texto que todavía se muestra en inglés simple no se puede traducir aún; por favor reportalo. El texto que desborda su botón o columna necesita una traducción más corta, o un arreglo de diseño.

### Idiomas de derecha a izquierda

El árabe, el hebreo, el persa y otros idiomas de derecha a izquierda reciben un diseño espejado: el menú se mueve a la derecha, y el texto y los botones se alinean desde la derecha. Las imágenes de cámaras, las líneas de tiempo y los mapas no se espejan.

### Traducir los instaladores

Los instaladores Web, Nodo y Proxy tienen su propio texto, mucho más corto, en `installer\Common\Strings.en-US.wxl` en el código fuente. Copialo a un archivo nombrado por el idioma (por ejemplo `Strings.fr-FR.wxl`), y seguí las notas arriba del archivo. Los instaladores después se compilan con `.\build-installers.ps1 -Cultures en-US,fr-FR`, que hace un conjunto de instaladores para cada idioma. Los botones propios del instalador de Windows (Siguiente, Atrás, Cancelar, y así) ya están traducidos.

## Dónde viven los archivos de idioma

- `%ProgramData%\LarisVMS\locales` guarda los idiomas agregados en este servidor. Sobrevive actualizaciones.
- `locales` en la carpeta de instalación del servidor guarda los idiomas que vienen con LarisVMS. Un archivo agregado en este servidor gana sobre uno con el mismo nombre acá, línea por línea.
- El inglés está integrado y no se puede reemplazar.

Subir o quitar un idioma se registra en el registro de auditoría, y necesita permiso para cambiar la configuración.
