"""Metriche per immagine di un riconoscitore di monete, indipendenti dal modello usato.

Le monete sono rappresentate dal loro indice di classe 0-7 (1ct, 2ct, 5ct, 10ct, 20ct, 50ct, 1€, 2€).
Il file non ha dipendenze esterne: viaggia insieme al dataset su Kaggle.
"""
from collections import Counter

CLASS_CENTS = [1, 2, 5, 10, 20, 50, 100, 200]
CLASS_NAMES = ["1 ct", "2 ct", "5 ct", "10 ct", "20 ct", "50 ct", "1 euro", "2 euro"]


def total_cents(classes: list[int]) -> int:
    return sum(CLASS_CENTS[c] for c in classes)


def score(predictions: dict[str, list[int]], truth: dict[str, list[int]]) -> dict:
    """
    predictions / truth: nome immagine -> lista di classi, una per moneta.
    - total_exact: % di immagini con il totale in centesimi esatto (la metrica che interessa all'utente)
    - counts_exact: % di immagini con il numero di monete giusto per ogni taglio
    - mean_abs_error_cents: errore medio assoluto sul totale
    - per_class: richiamo e precisione sui conteggi (somma su tutte le immagini)
    """
    names = sorted(truth)
    total_ok = counts_ok = 0
    abs_error = 0
    matched = Counter()
    pred_count = Counter()
    true_count = Counter()

    for name in names:
        predicted = Counter(predictions.get(name, []))
        actual = Counter(truth[name])

        if total_cents(list(predicted.elements())) == total_cents(list(actual.elements())):
            total_ok += 1
        if predicted == actual:
            counts_ok += 1
        abs_error += abs(total_cents(list(predicted.elements())) - total_cents(list(actual.elements())))

        for c in range(len(CLASS_CENTS)):
            matched[c] += min(predicted[c], actual[c])
            pred_count[c] += predicted[c]
            true_count[c] += actual[c]

    n = max(len(names), 1)
    per_class = {
        CLASS_NAMES[c]: {
            "recall": round(matched[c] / true_count[c], 3) if true_count[c] else None,
            "precision": round(matched[c] / pred_count[c], 3) if pred_count[c] else None,
            "true": true_count[c],
            "predicted": pred_count[c],
        }
        for c in range(len(CLASS_CENTS))
    }

    return {
        "images": len(names),
        "total_exact": round(total_ok / n, 3),
        "counts_exact": round(counts_ok / n, 3),
        "mean_abs_error_cents": round(abs_error / n, 1),
        "per_class": per_class,
    }


def read_truth(labels_dir) -> dict[str, list[int]]:
    """Legge le etichette YOLO (una riga per moneta, prima colonna = classe)."""
    from pathlib import Path

    truth = {}
    for path in Path(labels_dir).glob("*.txt"):
        lines = [l.split() for l in path.read_text(encoding="utf-8").splitlines() if l.strip()]
        truth[path.stem] = [int(parts[0]) for parts in lines]
    return truth
