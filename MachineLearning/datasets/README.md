# Dataset pubblici

I dati scaricati vanno in `datasets/raw/` (ignorata da git). Mappatura delle classi: `classes.yaml`.

| Fonte | Licenza | Contenuto | Stato |
|---|---|---|---|
| Kaggle `janstaffa/euro-coins-dataset` | Apache-2.0 (dichiarata da Kaggle) | 150 foto 720×960, 336 monete, box YOLO, 8 classi | Scaricato e controllato |
| GitHub `SuperDiodo/euro-coin-dataset` | Nessun file di licenza nel repository (tutti i diritti riservati per default) | Foto per cartella di taglio, senza box | Non scaricato: il repository pesa più di 1,5 GB, non ha licenza e non ha box. Interrotto e rimosso |
| Roboflow `euro-currency/euro-coin-detection-tfngl` (v3) | **CC BY 4.0** (da `data.yaml`): citare l'autore "Euro Currency", Roboflow Universe | 459 foto originali, 2641 monete, 8 classi, box YOLO; export v3 con le copie aumentate nel training | Scaricato, importato con `coin_ml.import_roboflow` e controllato |
| GitHub `Tanwar-12/Euro-Coin-Detection` | Nessun file di licenza | ~1000 immagini, una sola classe "Coin" | Non scaricato |

## Scaricare

```bash
python -m kaggle auth login        # una volta
python -m kaggle datasets download -d janstaffa/euro-coins-dataset -p datasets/raw/kaggle-janstaffa --unzip
```

## Importare l'export Roboflow

```bash
python -m coin_ml.import_roboflow datasets/raw/roboflow-euro-currency datasets/processed/roboflow-euro-currency
```
Traduce gli indici di classe (Roboflow le ordina alfabeticamente: `['1','10','100','2',...]`) e tiene una sola copia per foto di origine: da 1095 a **365 immagini di training** (50 di validazione e 44 di test non sono aumentate).

## Controllare

```bash
python -m coin_ml.yolo_report datasets/raw/kaggle-janstaffa --source kaggle-janstaffa
python -m coin_ml.dedupe datasets/raw/kaggle-janstaffa datasets/raw/<altra_fonte>
```

## Note sul controllo del dataset Kaggle

- 150 immagini, tutte 720×960; 336 monete (1 ct: 41, 2 ct: 51, 5 ct: 56, 10 ct: 32, 20 ct: 50, 50 ct: 27, 1 €: 51, 2 €: 28); nessuna anomalia nelle etichette.
- Controllo visivo su 4 immagini: i box coincidono con le monete e le classi sono corrette nei casi guardati.
- Limiti: poche monete per foto (1-3 nei campioni), sfondi chiari e uniformi, monete spesso isolate. Non basta da solo per foto con molte monete o sovrapposte.

## Note sul controllo del dataset Roboflow

- 459 foto (365 train, 50 valid, 44 test), 2641 monete: 1 ct 366, 2 ct 439, 5 ct 434, 10 ct 429, 20 ct 232, 50 ct 248, 1 € 216, 2 € 277. Nessuna anomalia nelle etichette.
- Più denso di Kaggle: circa 5,8 monete per foto contro 2,2, con sfondi vari (scuri, stoffa rossa, bianco lucido).
- Controllo visivo su 4 foto: le classi rimappate sono coerenti, ma in una foto una moneta non ha il box: ci sono **etichette mancanti**, da tenere presente.
- Tutte le immagini sono **stirate a 640×640** (le foto originali non sono quadrate), quindi le monete risultano leggermente ovali.
- Nessun duplicato tra Roboflow e Kaggle né tra train, valid e test di Roboflow (hash percettivo, distanza ≤ 4).
- Il controllo di duplicati dentro Kaggle segnala 17 coppie, probabilmente falsi allarmi dovuti agli sfondi uniformi: da verificare prima di usarle per dividere train e test.
