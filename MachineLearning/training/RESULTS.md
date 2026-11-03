# Risultati dell'addestramento

## Baseline 1 (2026-11-03)

YOLO11n (`yolo11n.pt`), 640 px, 100 epoche, GPU Kaggle, 10,8 minuti. Dati: Kaggle `janstaffa` + Roboflow `euro-coin-detection-tfngl` v3 (copie aumentate rimosse), divisi da `coin_ml.make_dataset` (seme 42):

| Insieme | Foto | Monete |
|---|---|---|
| train | 485 (365 Roboflow + 120 Kaggle) | 2352 |
| val | 65 (50 + 15) | 333 |
| test | 59 (44 + 15) | 292 |

Soglia di confidenza 0,25. "Totale esatto" = la somma in centesimi della foto è giusta.

| | mAP@0.5 | mAP@0.5-0.95 | Totale esatto | Conteggi esatti per taglio | Errore medio sul totale |
|---|---|---|---|---|---|
| val (65 foto) | 0,994 | 0,905 | 92,3% | 92,3% | 10,9 ct |
| test (59 foto) | 0,995 | 0,912 | **98,3%** (58/59) | 98,3% | 0,3 ct |
| test, solo Kaggle (15) | | | 93,3% | 93,3% | 1,3 ct |
| test, solo Roboflow (44) | | | 100% | 100% | 0,0 ct |

Sul test il richiamo è 1,0 per tutti gli 8 tagli; l'unico errore è una moneta da 20 ct in più (precisione 0,969).

File prodotti (non nel repository, ignorati): `runs/baseline1/best.pt` (5,5 MB), `best.onnx` (10,7 MB), `metrics.json`.

## Come leggere questi numeri

**Non sono una stima dell'accuratezza reale.** Servono a dire che la pipeline funziona e che il modello impara.

- Le foto di val e test non sono duplicati di quelle di training (nessuna sotto la soglia di somiglianza con hash percettivo 16×16, distanza ≤ 40 su 256 bit). Però sono **più vicine al training di due foto a caso**: distanza minima mediana dal training 94-96, contro una mediana di 128 tra due foto di training a caso. Probabilmente le stesse monete fisiche e gli stessi sfondi compaiono in più insiemi.
- Il modello non ha mai visto monete italiane consumate fotografate con il tuo telefono: quella generalizzazione **non è ancora misurata**.
- Il test Roboflow (44 foto, 100%) è il più sospetto di somiglianza con il training.
- 59 foto di test sono poche: un errore sposta il risultato di circa 1,7 punti.

Per questo il test che conta è il **set riservato di foto proprie** (Fasi 4-5 dell'issue #2). Fino ad allora il numero da riportare è: "funziona sui dataset pubblici", non "è affidabile".

## Come rifarlo

```bash
python -m coin_ml.make_dataset datasets/raw/kaggle-janstaffa datasets/processed/roboflow-euro-currency datasets/processed/combined --kaggle-id canlea/coin-dataset
python -m kaggle datasets create -p "<percorso assoluto>\datasets\processed\combined" -r zip     # primo caricamento
python -m kaggle kernels push -p "<percorso assoluto>\training\kaggle"
python -m kaggle kernels status canlea/coin-yolo-baseline
python -m kaggle kernels output canlea/coin-yolo-baseline -p "<percorso assoluto>\runs\baseline1"
```
Su Windows servono percorsi **assoluti**: con quelli relativi la CLI di Kaggle fallisce. Per aggiornare un dataset già creato si usa `datasets version` al posto di `create`.
