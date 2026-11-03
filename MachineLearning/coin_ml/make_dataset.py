"""Unisce i dataset importati in uno solo per l'addestramento, con divisione train / val / test senza fughe.

- Roboflow: si tengono le sue divisioni originali (train / valid / test).
- Kaggle: le foto molto simili (stessa scena ripresa più volte, hash percettivo) restano nello stesso gruppo e
  quindi nello stesso insieme; i gruppi sono assegnati a caso (seme fisso) a train / val / test.

Uso: python -m coin_ml.make_dataset <kaggle_dir> <roboflow_processed_dir> <output> [--seed 42]
Crea <output>/images/{train,val,test}, <output>/labels/..., <output>/splits.json e <output>/code/scoring.py
(il file di valutazione viaggia col dataset così l'addestramento su Kaggle lo trova).
"""
import argparse
import json
import random
import shutil
from pathlib import Path

import imagehash
from PIL import Image

IMAGE_SUFFIXES = {".jpg", ".jpeg", ".png"}


def group_similar(images: list[Path], max_distance: int = 4) -> list[list[Path]]:
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


def copy_pair(image: Path, labels_dir: Path, out: Path, split: str, prefix: str) -> None:
    label = labels_dir / f"{image.stem}.txt"
    name = f"{prefix}_{image.stem}"
    (out / "images" / split).mkdir(parents=True, exist_ok=True)
    (out / "labels" / split).mkdir(parents=True, exist_ok=True)
    shutil.copy2(image, out / "images" / split / f"{name}{image.suffix}")
    text = label.read_text(encoding="utf-8") if label.exists() else ""
    (out / "labels" / split / f"{name}.txt").write_text(text, encoding="utf-8")


def build(kaggle: Path, roboflow: Path, out: Path, seed: int = 42) -> dict:
    if out.exists():
        shutil.rmtree(out)

    manifest: dict[str, dict[str, int]] = {"train": {}, "val": {}, "test": {}}

    # Roboflow: divisioni originali
    for rf_split, split in (("train", "train"), ("valid", "val"), ("test", "test")):
        images = [p for p in sorted((roboflow / rf_split / "images").glob("*")) if p.suffix.lower() in IMAGE_SUFFIXES]
        for image in images:
            copy_pair(image, roboflow / rf_split / "labels", out, split, "rf")
        manifest[split]["roboflow"] = len(images)

    # Kaggle: divisione per gruppi di foto simili
    kaggle_images = [p for p in sorted((kaggle / "images").glob("*")) if p.suffix.lower() in IMAGE_SUFFIXES]
    groups = group_similar(kaggle_images)
    for split, images in split_groups(groups, 0.1, 0.1, seed).items():
        for image in images:
            copy_pair(image, kaggle / "labels", out, split, "kg")
        manifest[split]["kaggle"] = len(images)

    (out / "splits.json").write_text(json.dumps({"seed": seed, "images": manifest}, indent=2), encoding="utf-8")
    (out / "code").mkdir(parents=True, exist_ok=True)
    shutil.copy2(Path(__file__).with_name("scoring.py"), out / "code" / "scoring.py")
    return manifest


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("kaggle", type=Path)
    parser.add_argument("roboflow", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--seed", type=int, default=42)
    parser.add_argument("--kaggle-id", help="es. utente/coin-dataset: scrive dataset-metadata.json per il caricamento su Kaggle")
    args = parser.parse_args()

    for split, counts in build(args.kaggle, args.roboflow, args.output, args.seed).items():
        print(f"{split}: {sum(counts.values())} immagini  {counts}")

    if args.kaggle_id:
        (args.output / "dataset-metadata.json").write_text(json.dumps({
            "title": args.kaggle_id.split("/")[1],
            "id": args.kaggle_id,
            "licenses": [{"name": "other"}],
            "description": "Kaggle janstaffa (Apache-2.0) + Roboflow euro-currency (CC BY 4.0); divisione train/val/test senza fughe."
        }, indent=2), encoding="utf-8")
