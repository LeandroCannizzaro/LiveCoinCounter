"""Unisce i dataset importati in uno solo per l'addestramento, con divisione train / val / test senza fughe.

Tutte le sorgenti vengono messe insieme e le foto molto simili (stessa scena ripresa più volte, hash percettivo)
restano nello stesso gruppo, quindi nello stesso insieme: i gruppi sono assegnati a caso (seme fisso) a
train / val / test. Le divisioni originali delle sorgenti si ignorano apposta: tra train e test di Roboflow
c'erano scene quasi uguali.

Una sorgente è una cartella con images/ e labels/, oppure con train|valid|test/{images,labels} (export Roboflow).

Uso: python -m coin_ml.make_dataset <output> --source kg <cartella> --source rf <cartella> [--seed 42]
Crea <output>/images/{train,val,test}, <output>/labels/..., i banchi di prova con ombre
(<output>/images/{val,test}_shadow), <output>/splits.json e <output>/code/scoring.py
(il file di valutazione viaggia col dataset così l'addestramento su Kaggle lo trova).
"""
import argparse
import json
import random
import shutil
from pathlib import Path

import cv2
import imagehash
import numpy as np
from PIL import Image

from coin_ml.stress import stress

IMAGE_SUFFIXES = {".jpg", ".jpeg", ".png"}


def group_similar(images: list[Path], max_distance: int = 10) -> list[list[Path]]:
    """Raggruppa le immagini simili (componenti connesse sulla distanza dell'hash percettivo)."""
    hashes = []
    for path in images:
        with Image.open(path) as img:
            hashes.append(imagehash.phash(img))

    parent = list(range(len(images)))

    def find(i):
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    for i in range(len(images)):
        for j in range(i + 1, len(images)):
            if hashes[i] - hashes[j] <= max_distance:
                parent[find(i)] = find(j)

    groups: dict[int, list[Path]] = {}
    for i, path in enumerate(images):
        groups.setdefault(find(i), []).append(path)
    return list(groups.values())


def split_groups(groups: list[list[Path]], val_fraction: float, test_fraction: float, seed: int) -> dict[str, list[Path]]:
    """Assegna interi gruppi a val / test finché non si raggiunge la quota, il resto va in train."""
    rng = random.Random(seed)
    shuffled = groups[:]
    rng.shuffle(shuffled)

    total = sum(len(g) for g in groups)
    targets = {"val": total * val_fraction, "test": total * test_fraction}
    result: dict[str, list[Path]] = {"train": [], "val": [], "test": []}

    for group in shuffled:
        for name in ("val", "test"):
            if len(result[name]) < targets[name]:
                result[name].extend(group)
                break
        else:
            result["train"].extend(group)
    return result


def find_pairs(source: Path) -> list[tuple[Path, Path]]:
    """(immagine, file etichette) di una sorgente, con o senza le sue divisioni."""
    folders = [source] if (source / "images").exists() else [source / n for n in ("train", "valid", "test")]
    pairs = []
    for folder in folders:
        for image in sorted((folder / "images").glob("*")):
            if image.suffix.lower() in IMAGE_SUFFIXES:
                pairs.append((image, folder / "labels" / f"{image.stem}.txt"))
    return pairs


def copy_pair(image: Path, label: Path, out: Path, split: str, name: str) -> None:
    (out / "images" / split).mkdir(parents=True, exist_ok=True)
    (out / "labels" / split).mkdir(parents=True, exist_ok=True)
    shutil.copy2(image, out / "images" / split / f"{name}{image.suffix}")
    text = label.read_text(encoding="utf-8") if label.exists() else ""
    (out / "labels" / split / f"{name}.txt").write_text(text, encoding="utf-8")


def make_shadow_sets(out: Path, seed: int) -> None:
    """Banco di prova con ombre: le foto di val e test peggiorate da codice (stesse etichette, sempre uguali)."""
    rng = np.random.default_rng(seed)
    for split in ("val", "test"):
        target_images = out / "images" / f"{split}_shadow"
        target_labels = out / "labels" / f"{split}_shadow"
        target_images.mkdir(parents=True, exist_ok=True)
        target_labels.mkdir(parents=True, exist_ok=True)
        for image_path in sorted((out / "images" / split).glob("*")):
            image = cv2.imread(str(image_path))
            cv2.imwrite(str(target_images / f"{image_path.stem}.jpg"), stress(image, rng), [cv2.IMWRITE_JPEG_QUALITY, 92])
            shutil.copy2(out / "labels" / split / f"{image_path.stem}.txt", target_labels / f"{image_path.stem}.txt")


def build(sources: dict[str, Path], out: Path, seed: int = 42, max_distance: int = 10) -> dict:
    if out.exists():
        shutil.rmtree(out)

    entries: dict[Path, tuple[str, Path]] = {}   # immagine -> (nome nel dataset, etichette)
    for prefix, source in sources.items():
        for image, label in find_pairs(source):
            entries[image] = (f"{prefix}_{image.stem}", label)

    groups = group_similar(sorted(entries), max_distance)
    manifest: dict[str, dict[str, int]] = {"train": {}, "val": {}, "test": {}}
    for split, images in split_groups(groups, 0.1, 0.1, seed).items():
        for image in images:
            name, label = entries[image]
            copy_pair(image, label, out, split, name)
            prefix = name.split("_", 1)[0]
            manifest[split][prefix] = manifest[split].get(prefix, 0) + 1

    make_shadow_sets(out, seed)
    (out / "splits.json").write_text(json.dumps({"seed": seed, "max_distance": max_distance, "images": manifest}, indent=2), encoding="utf-8")
    (out / "code").mkdir(parents=True, exist_ok=True)
    shutil.copy2(Path(__file__).with_name("scoring.py"), out / "code" / "scoring.py")
    return manifest


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    parser.add_argument("--source", nargs=2, action="append", metavar=("PREFISSO", "CARTELLA"), required=True)
    parser.add_argument("--seed", type=int, default=42)
    parser.add_argument("--max-distance", type=int, default=10, help="distanza massima dell'hash percettivo tra foto dello stesso gruppo")
    parser.add_argument("--kaggle-id", help="es. utente/coin-dataset: scrive dataset-metadata.json per il caricamento su Kaggle")
    args = parser.parse_args()

    for split, counts in build({p: Path(d) for p, d in args.source}, args.output, args.seed, args.max_distance).items():
        print(f"{split}: {sum(counts.values())} immagini  {counts}")

    if args.kaggle_id:
        (args.output / "dataset-metadata.json").write_text(json.dumps({
            "title": args.kaggle_id.split("/")[1],
            "id": args.kaggle_id,
            "licenses": [{"name": "other"}],
            "description": "Dataset di monete euro (fonti: vedi datasets/README.md); divisione per gruppi di foto simili, con banchi di prova con ombre."
        }, indent=2), encoding="utf-8")
