"""Importa un export Roboflow (formato YOLO) nel formato del progetto.

- Traduce gli indici di classe: Roboflow ordina i nomi alfabeticamente (['1','10','100','2',...]), il progetto per valore.
- Nel training Roboflow salva più copie aumentate (capovolte, ritagliate, con colori alterati) della stessa foto, con
  lo stesso nome base: se ne tiene una sola per foto, così non si falsa la valutazione né si "pesa" di più una foto.
- Controlla che ogni riga sia un box (5 campi) e non un poligono.

Uso: python -m coin_ml.import_roboflow <cartella_export> <cartella_output> [--keep-augmented]
Crea <output>/{train,valid,test}/{images,labels}.
"""
import argparse
import re
import shutil
from pathlib import Path

import yaml

# Indice YOLO del progetto per ogni valore in centesimi (stesso ordine di datasets/classes.yaml)
CENTS_TO_CLASS = {1: 0, 2: 1, 5: 2, 10: 3, 20: 4, 50: 5, 100: 6, 200: 7}

AUGMENTED_NAME = re.compile(r"^(?P<stem>.+?)\.rf\.[0-9a-f]+$")


def class_map(names: list[str]) -> dict[int, int]:
    """Indice Roboflow -> indice del progetto, leggendo i nomi (valori in centesimi) da data.yaml."""
    mapping = {}
    for index, name in enumerate(names):
        cents = int(name)
        if cents not in CENTS_TO_CLASS:
            raise ValueError(f"Nome di classe non riconosciuto: {name!r}")
        mapping[index] = CENTS_TO_CLASS[cents]
    return mapping


def source_key(image_stem: str) -> str:
    """Nome della foto di origine: toglie il suffisso '.rf.<hash>' che distingue le copie aumentate."""
    match = AUGMENTED_NAME.match(image_stem)
    return match.group("stem") if match else image_stem


def convert_label(text: str, mapping: dict[int, int]) -> list[str]:
    lines = []
    for number, line in enumerate(text.splitlines(), 1):
        parts = line.split()
        if not parts:
            continue
        if len(parts) != 5:
            raise ValueError(f"riga {number}: attesi 5 campi (box), trovati {len(parts)} (poligono?)")
        lines.append(" ".join([str(mapping[int(parts[0])]), *parts[1:]]))
    return lines


def import_export(src: Path, dst: Path, keep_augmented: bool = False) -> dict[str, tuple[int, int]]:
    config = yaml.safe_load((src / "data.yaml").read_text(encoding="utf-8"))
    mapping = class_map(config["names"])
    summary = {}

    for split in ("train", "valid", "test"):
        images = sorted((src / split / "images").glob("*"))
        seen = set()
        kept = 0
        (dst / split / "images").mkdir(parents=True, exist_ok=True)
        (dst / split / "labels").mkdir(parents=True, exist_ok=True)

        for image in images:
            key = source_key(image.stem)
            if not keep_augmented and key in seen:
                continue
            seen.add(key)

            label_file = src / split / "labels" / f"{image.stem}.txt"
            lines = convert_label(label_file.read_text(encoding="utf-8"), mapping) if label_file.exists() else []
            (dst / split / "labels" / f"{image.stem}.txt").write_text("\n".join(lines), encoding="utf-8")
            shutil.copy2(image, dst / split / "images" / image.name)
            kept += 1

        summary[split] = (len(images), kept)
    return summary


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("export", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--keep-augmented", action="store_true", help="tiene anche le copie aumentate")
    args = parser.parse_args()

    for split, (total, kept) in import_export(args.export, args.output, args.keep_augmented).items():
        print(f"{split}: {total} immagini nell'export, {kept} importate")
