"""Converte coppie immagine + JSON (schema/coin_label.schema.json) in etichette YOLO.

Uso: python -m coin_ml.to_yolo <cartella_input> <cartella_output> [--status auto,review]
Per ogni <nome>.json crea <output>/labels/<nome>.txt e copia l'immagine in <output>/images/.
Le monete senza etichetta (label null) vengono saltate; con --status si convertono solo le immagini con quello stato
(es. "auto" per i soli risultati su cui i provider concordano).
"""
import json
import shutil
import sys
from pathlib import Path

# Indice YOLO per ogni valore in centesimi (stesso ordine di datasets/classes.yaml)
CENTS_TO_CLASS = {1: 0, 2: 1, 5: 2, 10: 3, 20: 4, 50: 5, 100: 6, 200: 7}


def label_to_yolo_lines(label: dict) -> list[str]:
    """Una riga `class cx cy w h` (coordinate normalizzate 0-1) per ogni moneta."""
    width, height = label["width"], label["height"]
    lines = []
    for coin in label["coins"]:
        cents = coin["label"]
        if cents is None:
            continue
        if cents not in CENTS_TO_CLASS:
            raise ValueError(f"Valore non valido: {cents}")
        x, y, w, h = coin["bbox"]
        cx = min(max((x + w / 2) / width, 0.0), 1.0)
        cy = min(max((y + h / 2) / height, 0.0), 1.0)
        nw = min(max(w / width, 0.0), 1.0)
        nh = min(max(h / height, 0.0), 1.0)
        lines.append(f"{CENTS_TO_CLASS[cents]} {cx:.6f} {cy:.6f} {nw:.6f} {nh:.6f}")
    return lines


def convert_folder(src: Path, dst: Path, statuses: set[str] | None = None) -> int:
    (dst / "images").mkdir(parents=True, exist_ok=True)
    (dst / "labels").mkdir(parents=True, exist_ok=True)

    count = 0
    for json_path in sorted(src.glob("*.json")):
        label = json.loads(json_path.read_text(encoding="utf-8"))
        if statuses is not None and label.get("status") not in statuses:
            continue
        image_path = src / label["image"]
        if not image_path.exists():
            print(f"Immagine mancante, salto: {label['image']}", file=sys.stderr)
            continue

        lines = label_to_yolo_lines(label)
        (dst / "labels" / f"{image_path.stem}.txt").write_text("\n".join(lines), encoding="utf-8")
        shutil.copy2(image_path, dst / "images" / image_path.name)
        count += 1
    return count


if __name__ == "__main__":
    args = sys.argv[1:]
    statuses = None
    if "--status" in args:
        i = args.index("--status")
        statuses = set(args[i + 1].split(","))
        del args[i:i + 2]
    if len(args) != 2:
        sys.exit(__doc__)
    n = convert_folder(Path(args[0]), Path(args[1]), statuses)
    print(f"Convertite {n} immagini")
