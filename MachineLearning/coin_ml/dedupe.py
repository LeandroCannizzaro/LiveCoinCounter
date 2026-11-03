"""Trova immagini duplicate (o quasi) tra cartelle diverse con un hash percettivo.

Serve prima di dividere train / validation / test: lo stesso scatto presente in due dataset
non deve finire sia nel training sia nel test.

Uso: python -m coin_ml.dedupe <cartella1> [<cartella2> ...] [--max-distance 4]
"""
import argparse
from pathlib import Path

import imagehash
from PIL import Image

IMAGE_SUFFIXES = {".jpg", ".jpeg", ".png", ".bmp", ".webp"}


def iter_images(folders):
    for folder in folders:
        for path in sorted(Path(folder).rglob("*")):
            if path.suffix.lower() in IMAGE_SUFFIXES:
                yield path


def find_duplicates(folders, max_distance=4):
    """Restituisce coppie (originale, duplicato) con distanza di Hamming <= max_distance."""
    seen = []  # (hash, path)
    duplicates = []
    for path in iter_images(folders):
        try:
            with Image.open(path) as img:
                h = imagehash.phash(img)
        except OSError:
            continue

        match = next((p for known, p in seen if known - h <= max_distance), None)
        if match is not None:
            duplicates.append((match, path))
        else:
            seen.append((h, path))
    return duplicates


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("folders", nargs="+")
    parser.add_argument("--max-distance", type=int, default=4)
    args = parser.parse_args()

    pairs = find_duplicates(args.folders, args.max_distance)
    for original, duplicate in pairs:
        print(f"{duplicate}  ==  {original}")
    print(f"{len(pairs)} duplicati")
