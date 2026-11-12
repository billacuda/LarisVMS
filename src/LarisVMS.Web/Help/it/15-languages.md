# Lingue

Mostrare LarisVMS in un’altra lingua e aggiungere la propria traduzione.

## Scegliere una lingua

Scelga una lingua dal menu 🌐 in alto in qualsiasi pagina, compresa la pagina di accesso. La Sua scelta La segue su ogni dispositivo su cui accede. Il menu compare solo quando è installata più di una lingua.

Un amministratore imposta in **Impostazioni → Lingua** la lingua per chi non ne ha scelta una. Lasci **La lingua del browser** per seguire invece il browser di ciascuno.

Anche date, ore e numeri seguono la lingua. Se il browser usa la stessa lingua con un formato regionale (ad esempio inglese britannico), il formato regionale viene mantenuto.

Avvisi e notifiche raggiungono ogni persona nella propria lingua. I canali del team (e-mail a un indirizzo fisso, webhook, ntfy, Pushover, Slack e Teams) usano la lingua predefinita. Il registro di audit resta in inglese.

Il testo che una lingua non ha ancora tradotto viene mostrato in inglese.

## Aggiungere una lingua

Non serve programmare: una lingua è un file di testo.

1. In **Impostazioni → Lingua**, scarichi **en.json**, il testo inglese.
2. Traduca il testo a destra di ogni riga. Lasci la chiave a sinistra esattamente com’è.
3. Carichi il file in **Impostazioni → Lingua**, oppure lo copi in `%ProgramData%\LarisVMS\locales` sul server. Lo chiami con il suo codice lingua, ad esempio `fr.json`, `de.json` o `pt-BR.json`.

È disponibile subito, senza riavvio. Un file che non può essere letto viene elencato nella pagina Lingua con il motivo, e il resto di LarisVMS continua a funzionare.

### Cosa lasciare com’è

- **Tutto ciò che è tra {parentesi graffe}** viene compilato da LarisVMS, come `{camera}` o `{count}`. Lo sposti dove serve nella Sua frase, ma non lo traduca né lo rinomini.
- **Alcuni tag** possono comparire nel testo: `<code>`, `<strong>`, `<em>`, `<b>`, `<i>`, `<kbd>`, `<br>` e `<a href="…">`. Li mantenga attorno alle stesse parole. Altro HTML non è consentito e viene mostrato come testo semplice.
- La sezione **_meta** in alto contiene il nome della lingua come compare nel menu 🌐 (ad esempio «Français») e il Suo nome, se desidera essere citato.

### Plurali

Alcuni testi cambiano con un numero. In inglese ci sono due forme:

```json
"cameras.count_one": "{count} camera",
"cameras.count_other": "{count} cameras"
```

Aggiunga le forme di cui la Sua lingua ha bisogno, con queste terminazioni: `_zero`, `_one`, `_two`, `_few`, `_many` e `_other`. Il russo, ad esempio, usa `_one`, `_few` e `_many`. Includa sempre `_other`; viene usata quando nessun’altra forma si adatta.

### Pagine della guida

Le pagine della guida si traducono un file alla volta. Ogni pagina mantiene il nome del file inglese, quindi il suo indirizzo non cambia, e le pagine che non ha tradotto restano in inglese. Una lingua regionale (`es-AR`) usa prima le proprie pagine, poi quelle della lingua generale (`es`), poi l’inglese.

- **Per il Suo server:** copi il Markdown di una pagina in `locales\<codice>\help\` con lo stesso nome di file (ad esempio `locales\fr\help\03-cameras.md`) e lo traduca.
- **Per contribuire a LarisVMS:** veda [CONTRIBUTING.md](https://github.com/billacuda/LarisVMS/blob/main/CONTRIBUTING.md) per come inviarlo. Le pagine tradotte si trovano in `src/LarisVMS.Web/Help/<codice>/` nel sorgente (ad esempio `Help/es-AR/03-cameras.md`), accanto alle pagine inglesi in `Help/en/`. Le pagine contribuite vengono distribuite con LarisVMS e compaiono anche su [larisvms.com/docs](https://www.larisvms.com/docs/), dove i visitatori possono scegliere la lingua.

La guida compare in una lingua una volta che quella lingua è installata (il suo file `.json`). L’HTML in una pagina viene mostrato come testo semplice, quindi usi Markdown per la formattazione.

### Aggiornare una traduzione

Le nuove versioni di LarisVMS aggiungono testo. La colonna **Tradotto** nella pagina Lingua mostra quanta parte del testo attuale ogni lingua copre, e **Pagine della guida** per quante pagine della guida ha un testo proprio. **Scarica** accanto a una lingua Le dà il suo file con il testo mancante riempito dall’inglese, pronto da tradurre.

Per controllare un file prima di caricarlo, esegua `tools\locale-check.ps1 fr.json`. Elenca chiavi mancanti, chiavi che questa versione non usa e {segnaposto} che non corrispondono al testo inglese. Aggiunga `-Help` con una cartella di pagine della guida tradotte (ad esempio `-Help locales\fr\help`) per controllare anche quelle: pagine non ancora tradotte, nomi di file che LarisVMS non usa, HTML e titoli che non corrispondono alla pagina inglese.

### Testare una traduzione

Attivi **Offri la pseudo-lingua nel menu delle lingue** in **Impostazioni → Lingua**, poi la scelga dal menu 🌐. Ogni testo viene mostrato con accenti e circa un terzo più lungo, come `[Ŝàṽé ··]`. Il testo che compare ancora in inglese semplice non può ancora essere tradotto; lo segnali. Il testo che esce dal suo pulsante o dalla sua colonna richiede una traduzione più breve, o una correzione del layout.

### Lingue da destra a sinistra

Arabo, ebraico, persiano e altre lingue da destra a sinistra ottengono un layout speculare: il menu passa a destra, e testo e pulsanti si allineano da destra. Immagini delle telecamere, timeline e mappe non vengono specchiate.

### Tradurre i programmi di installazione

I programmi di installazione di Web, Node e Proxy hanno un testo proprio, molto più breve, in `installer\Common\Strings.en-US.wxl` nel codice sorgente. Lo copi in un file con il nome della lingua (ad esempio `Strings.fr-FR.wxl`) e segua le note all’inizio del file. I programmi di installazione si compilano poi con `.\build-installers.ps1 -Cultures en-US,fr-FR`, che produce un set di programmi di installazione per ogni lingua. I pulsanti propri dell’installer di Windows (Avanti, Indietro, Annulla e così via) sono già tradotti.

## Dove si trovano i file di lingua

- `%ProgramData%\LarisVMS\locales` contiene le lingue aggiunte su questo server. Sopravvive agli aggiornamenti.
- `locales` nella cartella di installazione del server contiene le lingue incluse con LarisVMS. Un file aggiunto su questo server vince, riga per riga, su uno con lo stesso nome qui.
- L’inglese è integrato e non può essere sostituito.

Caricare o rimuovere una lingua viene registrato nel registro di audit e richiede l’autorizzazione a modificare le impostazioni.
