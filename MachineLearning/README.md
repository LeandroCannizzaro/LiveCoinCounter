# MachineLearning - dataset, addestramento e valutazione

Dataset, addestramento e valutazione del modello offline di riconoscimento monete euro. Questa cartella fa parte del repository di LiveCoinCounter (prima era il repository separato `coin-ml`).

Piano completo: issue [#2](https://github.com/LeandroCannizzaro/LiveCoinCounter/issues/2).

> Il repository è **privato**: può contenere foto personali. Dataset, pesi e chiavi non vanno committati (vedi `MachineLearning/.gitignore`).

## Struttura

| Cartella | Contenuto |
|---|---|
| `datasets/` | Mappatura unica delle classi (`classes.yaml`) e, in seguito, script di download / deduplica dei dataset pubblici |
| `schema/` | Schema JSON del file di etichette per immagine (`coin_label.schema.json`) |
| `coin_ml/` | Libreria Python: lettura delle etichette, conversione in formato YOLO |
| `training/` | Script e notebook di addestramento (Kaggle / Colab) |
| `eval/` | Valutazione sul test set riservato |
| `tests/` | Test della libreria Python |
| `CoinLabeler/`, `CoinLabeler.Tests/` | Programma .NET che etichetta le foto con l'AI (usa la libreria `MobileApp/Recognition.Core`) e i suoi test |

## Contratto dei dati

Ogni immagine ha accanto un file `.json` con lo stesso nome (es. `IMG_0001.jpg` + `IMG_0001.json`), conforme a `schema/coin_label.schema.json`. L'etichetta di ogni moneta è il suo **valore in centesimi**: `1, 2, 5, 10, 20, 50, 100, 200`.

Il formato è prodotto da `CoinLabeler/` (progetto .NET in questa cartella) (Fase 3 dell'issue) e convertito per l'addestramento (con `--status auto` si prendono solo le immagini su cui i provider concordano):

```bash
cd MachineLearning
python -m coin_ml.to_yolo cartella_con_immagini_e_json cartella_di_output
```

## Ambiente

```bash
python -m venv .venv
.venv\Scripts\activate        # Windows
pip install -r requirements.txt
python -m unittest discover tests
```

L'addestramento vero si fa su **Kaggle** o **Google Colab** (GPU gratuita); il PC locale serve per provare gli script su pochi esempi con la CPU.

## Stato

- [x] Fase 1: struttura, contratto dei dati, convertitore JSON → YOLO
- [x] Fase 2: dataset pubblici (Kaggle e Roboflow scaricati, controllati, importati; SuperDiodo scartato)
- [x] Fase 3: programma di etichettatura con AI (`CoinLabeler/` (progetto .NET in questa cartella); provato solo per i box, non ancora con una chiave AI reale)
- [x] Fase 6 (prima prova): baseline YOLO11n su Kaggle, test 98,3% di totale esatto su 59 foto dei dataset pubblici, **non** indicativo del caso reale (vedi `training/RESULTS.md`)
- [ ] Fasi 4, 5, 7-9: foto proprie, test set riservato, confronto strategie, integrazione nell'app
  - Foto proprie dalle sessioni dell'app: `CoinLabeler sessions` (vedi `CoinLabeler/README.md`) etichetta le sessioni fatte con un
    set di monete noto (nota della sessione «set1», «set2»...; composizioni in `datasets/coin-sets.json`).
    Uscita in `datasets/processed/own-phone-sessions/` (non va nel repository).
