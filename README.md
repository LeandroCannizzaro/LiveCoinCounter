# coin-ml

Dataset, addestramento e valutazione del modello offline di riconoscimento monete euro per l'app [LiveCoinCounter](https://github.com/LeandroCannizzaro/LiveCoinCounter).

Piano completo: issue [LiveCoinCounter#2](https://github.com/LeandroCannizzaro/LiveCoinCounter/issues/2).

> Repository **privato**: può contenere foto personali. Dataset, pesi e chiavi non vanno committati (vedi `.gitignore`).

## Struttura

| Cartella | Contenuto |
|---|---|
| `datasets/` | Mappatura unica delle classi (`classes.yaml`) e, in seguito, script di download / deduplica dei dataset pubblici |
| `schema/` | Schema JSON del file di etichette per immagine (`coin_label.schema.json`) |
| `coin_ml/` | Libreria Python: lettura delle etichette, conversione in formato YOLO |
| `training/` | Script e notebook di addestramento (Kaggle / Colab) |
| `eval/` | Valutazione sul test set riservato |
| `tests/` | Test della libreria |

## Contratto dei dati

Ogni immagine ha accanto un file `.json` con lo stesso nome (es. `IMG_0001.jpg` + `IMG_0001.json`), conforme a `schema/coin_label.schema.json`. L'etichetta di ogni moneta è il suo **valore in centesimi**: `1, 2, 5, 10, 20, 50, 100, 200`.

Il formato è prodotto da `tools/CoinLabeler` nel repository dell'app (Fase 3 dell'issue) e convertito per l'addestramento (con `--status auto` si prendono solo le immagini su cui i provider concordano):

```bash
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
- [ ] Fase 2: dataset pubblici (download, licenze, deduplica)
- [x] Fase 3: programma di etichettatura con AI (`tools/CoinLabeler` nel repository dell'app; provato solo per i box, non ancora con una chiave AI reale)
- [ ] Fasi 4-9: foto proprie, test set, addestramento, integrazione nell'app
