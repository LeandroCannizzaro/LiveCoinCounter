"""Statistiche di un dataset in formato YOLO: immagini, monete per classe, anomalie nelle etichette.

Uso: python -m coin_ml.yolo_report <cartella_con_images_e_labels> [--source kaggle-janstaffa]
Con --source, gli indici di classe sono tradotti con la mappatura di datasets/classes.yaml.
"""
import argparse
from collections import Counter
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parent.parent


def load_classes():
    return yaml.safe_load((ROOT / "datasets" / "classes.yaml").read_text(encoding="utf-8"))


def report(dataset: Path, source: str | None = None):
    config = load_classes()
    names = {i: c["name"] for i, c in config["classes"].items()}
    cents_to_index = {c["cents"]: i for i, c in config["classes"].items()}
    mapping = config["sources"].get(source) if source else None

    images = [p for p in (dataset / "images").rglob("*") if p.suffix.lower() in {".jpg", ".jpeg", ".png"}]
    counts = Counter()
    problems = []
    no_label = 0

    for image in images:
        label_file = dataset / "labels" / f"{image.stem}.txt"
        if not label_file.exists():
            no_label += 1
            continue

        for n, line in enumerate(label_file.read_text(encoding="utf-8").splitlines(), 1):
            parts = line.split()
            if not parts:
                continue
            if len(parts) != 5:
                problems.append(f"{label_file.name}:{n} riga non valida")
                continue

            raw = int(parts[0]) if mapping is None else mapping.get(int(parts[0]), mapping.get(parts[0]))
            index = raw if mapping is None else cents_to_index.get(raw)
            if index is None or index not in names:
                problems.append(f"{label_file.name}:{n} classe sconosciuta {parts[0]}")
                continue

            cx, cy, w, h = (float(v) for v in parts[1:])
            if not (0 <= cx <= 1 and 0 <= cy <= 1 and 0 < w <= 1 and 0 < h <= 1):
                problems.append(f"{label_file.name}:{n} box fuori dall'immagine")
            counts[index] += 1

    print(f"Immagini: {len(images)}  (senza file di etichette: {no_label})")
    print(f"Monete totali: {sum(counts.values())}")
    for index in sorted(names):
        print(f"  {names[index]:>8}: {counts[index]}")
    print(f"Anomalie: {len(problems)}")
    for p in problems[:20]:
        print("  -", p)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("dataset", type=Path)
    parser.add_argument("--source")
    args = parser.parse_args()
    report(args.dataset, args.source)
