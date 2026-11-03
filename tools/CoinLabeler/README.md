# CoinLabeler

Programma di servizio (.NET 10, console) che etichetta foto di monete euro con l'AI e prepara **coppie immagine + JSON** per l'addestramento del modello offline (vedi issue #2). Il formato JSON è descritto in `ml/schema/coin_label.schema.json`.

## Come funziona

1. **Box**: trova le monete con la trasformata di Hough per cerchi (OpenCvSharp), indipendente dall'AI, con un filtro che scarta cerchi sovrapposti o senza contrasto con lo sfondo.
2. **Taglio**: ogni ritaglio è inviato a ciascun provider AI scelto (stesse classi dell'app: Gemini, Claude, OpenAI-compatibili...).
3. **Controllo incrociato**: la foto intera è analizzata con il prompt dell'app e i conteggi devono coincidere con la somma dei ritagli.
4. **Consenso**: se tutti i provider concordano su ogni moneta e i conteggi tornano, lo stato è `auto`; altrimenti `review`.
5. **Output**: per ogni foto `nome.jpg` (già orientata secondo l'EXIF) + `nome.json`, più `report/index.html` con le miniature e i box (prima le foto da rivedere).

Le risposte dell'AI sono salvate in `<output>/.cache`: se il comando si interrompe, rilanciandolo non si rifanno (e non si ripagano) le chiamate già riuscite.

## Uso

```bash
cd tools/CoinLabeler

# Solo box, nessuna chiamata a pagamento
dotnet run -- label C:\foto C:\out --no-ai

# Con l'AI: le chiavi si passano come variabili d'ambiente, mai nel codice
$env:GEMINI_API_KEY = "..."
$env:ANTHROPIC_API_KEY = "..."
dotnet run -- label C:\foto C:\out --providers gemini,anthropic --limit 20

# Confronto con un dataset YOLO di riferimento (es. Kaggle janstaffa)
dotnet run -- eval C:\out C:\dataset_yolo

# Rigenera il report HTML
dotnet run -- report C:\out
```

Opzioni di `label`: `--providers` (predefinito `gemini`), `--limit N`, `--source nome` (origine scritta nel JSON), `--hough N` (soglia di Hough, predefinita 45; più alta = meno box falsi ma più monete perse), `--no-ai`.
Variabili opzionali per provider: `{ID}_MODEL`, `{ID}_BASE_URL` (es. `GEMINI_MODEL`).

**Prima di un'esecuzione grande**: lanciare con `--limit 20` per stimare costo e accuratezza, poi usare `eval` su un dataset con verità nota.

## Correggere le etichette

Il report HTML è di sola lettura: per correggere una moneta si modifica `label` nel file `.json` (e si imposta `labeler.reviewed` a `true`). Un editor più comodo (o l'importazione in Label Studio) è un'estensione possibile.

## Qualità misurata (box, senza AI)

Sul dataset Kaggle `janstaffa` (150 foto, 336 monete, `--hough 45`): **richiamo 96,1%** (323 monete su 336 trovate) e **precisione 47,1%** (686 box proposti, 323 corretti). I box in più sono quasi tutti cerchi dentro o a cavallo di monete vere. Con l'AI attiva, i box falsi dovrebbero emergere come disaccordi e finire in `review`: da verificare con una chiave reale.

## Test

```bash
dotnet test tools/CoinLabeler.Tests
```
Coprono la logica del consenso e il calcolo dell'IoU. L'integrazione con le API AI non è coperta da test automatici.
