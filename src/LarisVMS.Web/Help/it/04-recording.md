# Registrazione

Modalità di registrazione, pianificazioni, tag evento, pre/post-registrazione e durata dei segmenti.

## Modalità

Ogni telecamera registra in continuo sul proprio nodo. La **modalità di registrazione** decide quali filmati vengono *conservati*:

| Modalità | Conserva |
|---|---|
| **Continua** | Tutto. |
| **Movimento** | I filmati attorno al movimento: da una zona di movimento, dagli eventi di movimento della telecamera, da un’integrazione o dal rilevamento IA (vedere [Movimento](/Help/motion#source)). |
| **Pianificazione** | I filmati che iniziano all’interno di una fascia di pianificazione attiva. |
| **Evento** | I filmati attorno a una regola di tag evento con *Guida la registrazione* attivato. |
| **Solo manuale** | Nulla, a meno che sia attiva una [registrazione manuale](#manual-recording). La telecamera è solo in visualizzazione dal vivo il resto del tempo. |

I filmati che non vengono conservati sono eliminati poco dopo la scrittura, invece di attendere la conservazione.

Le modalità Movimento, Pianificazione ed Evento **sono permissive per impostazione predefinita (fail open)**. Finché una telecamera non ha una zona di movimento, una fascia di pianificazione o una regola che guida la registrazione, conserva tutto invece di scartarlo. La pagina della telecamera avvisa quando succede. Solo manuale non è permissiva, perché non conservare nulla è il suo scopo.

Imposti la modalità globale in **Impostazioni → Valori predefiniti di registrazione** e la sostituisca nella pagina di una telecamera.

## Registrazione manuale

Una registrazione manuale conserva i filmati di una telecamera finché qualcuno non la interrompe, qualunque sia la modalità di registrazione della telecamera. Ne avvii una con il pulsante di registrazione su un [riquadro dal vivo](/Help/live-and-playback#live-tiles) o tramite l’[API](/Help/api#recording). Resta attiva attraverso riavvii e aggiornamenti.

Funziona insieme alla modalità della telecamera. Una telecamera in modalità Movimento conserva i filmati quando vede movimento *oppure* quando è attiva una registrazione manuale, e gli eventi continuano a etichettare la timeline come al solito. Su una telecamera in modalità Continua, segna soltanto la timeline.

- I filmati sono conservati per segmenti interi, quindi viene salvato anche fino a un segmento prima e dopo la registrazione. Pre-registrazione e post-registrazione non si applicano.
- I filmati salvati seguono la normale conservazione della telecamera. Aggiunga un [blocco delle prove](/Help/live-and-playback#evidence-locks) per conservarli più a lungo.
- Compare sulla timeline come una banda rossa. Cambi il colore in **Impostazioni → Eventi**.
- Una telecamera offline non salva nulla finché non torna. La registrazione resta attiva.
- Aggiorni i nodi di registrazione insieme al server. Un nodo più vecchio non conosce le registrazioni manuali.

## Pre-registrazione e post-registrazione

Nelle modalità Movimento ed Evento, la **pre-registrazione** è quanti filmati prima dell’attività vengono conservati (per vedere qualcuno entrare) e la **post-registrazione** quanti dopo la sua fine.

## Durata dei segmenti

I filmati sono scritti come file di durata fissa (60 secondi per impostazione predefinita). La riproduzione deve scaricare parte di un segmento prima di poter partire dal momento esatto che sceglie. Segmenti più brevi rendono più rapida la ricerca sulle telecamere ad alta risoluzione, ma creano più file. Cambiare la durata riavvia brevemente la registrazione della telecamera.

## Pianificazione

La pagina **Pianificazione** di una telecamera contiene le sue fasce: giorni, un’ora di inizio e un’ora di fine. Un’ora di fine precedente all’ora di inizio attraversa la mezzanotte. Gli orari usano l’**orologio locale del nodo di registrazione**. Le fasce contano solo in modalità Pianificazione.

## Tag evento

La pagina **Tag evento** di una telecamera trasforma gli eventi ONVIF della telecamera (movimento, allarmi, analisi, manomissione) in segni colorati sulla timeline.

- **Argomento di avvio**: l’evento che avvia il tag. Ne scelga uno da *Argomenti osservati*, che elenca tutto ciò che la telecamera ha effettivamente inviato.
- **Argomento di arresto**: facoltativo. Lo lasci vuoto quando l’argomento di avvio porta con sé il proprio stato vero/falso (la maggior parte degli argomenti di movimento e allarme lo fa). Lo imposti quando la telecamera invia eventi di avvio e arresto separati.
- **Guida la registrazione**: in modalità Evento o Movimento, i filmati attorno a questo tag vengono conservati.

Un tag termina solo quando arriva il suo evento di arresto, mai per timeout.
