# Dataset pubblici

I dati scaricati vanno in `datasets/raw/` (ignorata da git). Mappatura delle classi: `classes.yaml`.

| Fonte | Licenza | Contenuto | Stato |
|---|---|---|---|
| Kaggle `janstaffa/euro-coins-dataset` | Apache-2.0 (dichiarata da Kaggle) | 150 foto 720×960, 336 monete, box YOLO, 8 classi | Scaricato e controllato |
| GitHub `SuperDiodo/euro-coin-dataset` | Nessun file di licenza nel repository (tutti i diritti riservati per default) | Foto per cartella di taglio, senza box | Download in corso (repository molto grande) |
| Roboflow `euro-currency/euro-coin-detection-tfngl` | Non indicata sulla pagina | 459 immagini originali, 8 classi | Serve account e chiave API Roboflow |
| GitHub `Tanwar-12/Euro-Coin-Detection` | Nessun file di licenza | ~1000 immagini, una sola classe "Coin" | Non scaricato |

## Scaricare

```bash
python -m kaggle auth login        # una volta
python -m kaggle datasets download -d janstaffa/euro-coins-dataset -p datasets/raw/kaggle-janstaffa --unzip
git clone --depth 1 https://github.com/SuperDiodo/euro-coin-dataset datasets/raw/superdiodo
```

## Controllare

```bash
python -m coin_ml.yolo_report datasets/raw/kaggle-janstaffa --source kaggle-janstaffa
python -m coin_ml.dedupe datasets/raw/kaggle-janstaffa datasets/raw/superdiodo
```

## Note sul controllo del dataset Kaggle

- 150 immagini, tutte 720×960; 336 monete (1 ct: 41, 2 ct: 51, 5 ct: 56, 10 ct: 32, 20 ct: 50, 50 ct: 27, 1 €: 51, 2 €: 28); nessuna anomalia nelle etichette.
- Controllo visivo su 4 immagini: i box coincidono con le monete e le classi sono corrette nei casi guardati.
- Limiti: poche monete per foto (1-3 nei campioni), sfondi chiari e uniformi, monete spesso isolate. Non basta da solo per foto con molte monete o sovrapposte.
