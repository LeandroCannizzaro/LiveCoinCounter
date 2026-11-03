"""Foto sintetiche di monete con ombre, per l'addestramento (le etichette sono esatte per costruzione).

Le monete si ritagliano dalle foto etichettate di *train* (mai da val/test), si portano alla stessa scala fisica
(pixel per millimetro, ricavata dai diametri noti delle monete euro) e si compongono su sfondi veri (pezzi delle foto di train senza monete) o procedurali, con ombra propria sotto ogni moneta e ombre/luce globali sopra la scena.

Uso: python -m coin_ml.synth <dataset> --count 1500 [--seed 0]
Aggiunge <dataset>/images/train/syn_*.jpg e <dataset>/labels/train/syn_*.txt
"""
import argparse
from dataclasses import dataclass
from pathlib import Path

import cv2
import numpy as np

from coin_ml.stress import add_light_gradient, add_shadow

# diametri reali in mm, nell'ordine delle classi 0-7 (1ct ... 2€)
DIAMETER_MM = [16.25, 18.75, 21.25, 19.75, 22.25, 24.25, 23.25, 25.75]
SIZE = 640


@dataclass
class Crop:
    cls: int
    patch: np.ndarray        # BGR, quadrato
    px_per_mm: float


def read_labels(path: Path) -> list[tuple[int, float, float, float, float]]:
    rows = []
    if path.exists():
        for line in path.read_text(encoding="utf-8").splitlines():
            parts = line.split()
            if len(parts) == 5:
                rows.append((int(parts[0]), *map(float, parts[1:])))
    return rows


def circle_mask(size: int, feather: float = 0.04) -> np.ndarray:
    """Maschera circolare 0..1 con bordo leggermente sfumato."""
    yy, xx = np.mgrid[0:size, 0:size].astype(np.float32)
    r = np.hypot(xx - (size - 1) / 2, yy - (size - 1) / 2) / (size / 2)
    return np.clip((1.0 - r) / feather, 0, 1)


def extract(image: np.ndarray, rows, rng: np.random.Generator) -> tuple[list[Crop], np.ndarray | None]:
    """Ritaglia le monete di una foto e restituisce anche un pezzo di sfondo senza monete (o None se non ce n'è)."""
    h, w = image.shape[:2]
    if not rows:
        return [], None

    # scala fisica della foto: mediana dei pixel per mm di tutte le sue monete
    scales = [((bw * w + bh * h) / 2) / DIAMETER_MM[cls] for cls, _, _, bw, bh in rows]
    px_per_mm = float(np.median(scales))

    crops = []
    boxes = []
    for cls, cx, cy, bw, bh in rows:
        d = int(round((bw * w + bh * h) / 2))
        x0, y0 = int(round(cx * w - d / 2)), int(round(cy * h - d / 2))
        boxes.append((x0 - d // 6, y0 - d // 6, x0 + d + d // 6, y0 + d + d // 6))
        if d < 24 or x0 < 0 or y0 < 0 or x0 + d > w or y0 + d > h:
            continue
        crops.append(Crop(cls, image[y0:y0 + d, x0:x0 + d].copy(), px_per_mm))

    return crops, free_window(image, boxes, rng)


def free_window(image: np.ndarray, boxes: list[tuple[int, int, int, int]], rng: np.random.Generator) -> np.ndarray | None:
    """Un rettangolo di sfondo che non tocca nessuna moneta, da riempire a specchio fino a 640 x 640."""
    h, w = image.shape[:2]
    for _ in range(60):
        side = int(rng.uniform(0.18, 0.4) * min(h, w))
        if side < 40:
            return None
        x, y = int(rng.integers(0, w - side)), int(rng.integers(0, h - side))
        if all(x + side <= bx0 or x >= bx1 or y + side <= by0 or y >= by1 for bx0, by0, bx1, by1 in boxes):
            tile = image[y:y + side, x:x + side]
            row = np.hstack([tile, tile[:, ::-1]])
            mosaic = np.vstack([row, row[::-1]])
            reps = SIZE // mosaic.shape[0] + 1
            return np.tile(mosaic, (reps, reps, 1))[:SIZE, :SIZE].copy()
    return None


def procedural_background(rng: np.random.Generator) -> np.ndarray:
    base = rng.uniform(40, 235, 3)
    bg = np.ones((SIZE, SIZE, 3), np.float32) * base
    noise = cv2.GaussianBlur(rng.normal(0, 1, (SIZE, SIZE)).astype(np.float32), (0, 0), rng.uniform(0.5, 6))
    bg += noise[..., None] * rng.uniform(3, 14)
    if rng.random() < 0.4:   # venature tipo legno / tessuto
        stripes = np.sin(np.arange(SIZE, dtype=np.float32) / rng.uniform(2, 12) + rng.uniform(0, 6))
        bg += (stripes[None, :, None] if rng.random() < 0.5 else stripes[:, None, None]) * rng.uniform(2, 9)
    return np.clip(bg, 0, 255).astype(np.uint8)


def paste(canvas: np.ndarray, crop: Crop, scale: float, rng: np.random.Generator, occupied: list) -> tuple | None:
    """Incolla una moneta in una posizione libera; restituisce (cls, cx, cy, w, h) normalizzati."""
    d = int(round(DIAMETER_MM[crop.cls] * scale))
    if d < 20 or d > SIZE // 2:
        return None

    patch = cv2.resize(crop.patch, (d, d), interpolation=cv2.INTER_AREA if d < crop.patch.shape[0] else cv2.INTER_LINEAR)
    patch = np.rot90(patch, int(rng.integers(0, 4))).copy()
    mask = circle_mask(d)

    for _ in range(40):
        x = int(rng.integers(0, SIZE - d))
        y = int(rng.integers(0, SIZE - d))
        cx, cy = x + d / 2, y + d / 2
        if all(np.hypot(cx - ox, cy - oy) > (d + od) / 2 * 1.05 for ox, oy, od in occupied):
            break
    else:
        return None

    # ombra propria: ellisse scuro sfocato, spostato dalla parte opposta alla luce
    shadow = np.zeros((SIZE, SIZE), np.float32)
    offset = int(d * rng.uniform(0.03, 0.18))
    angle = rng.uniform(0, 2 * np.pi)
    cv2.circle(shadow, (int(cx + np.cos(angle) * offset), int(cy + np.sin(angle) * offset)), int(d / 2 * 1.02), 1.0, -1)
    shadow = cv2.GaussianBlur(shadow, (0, 0), max(1.0, d * rng.uniform(0.03, 0.1)))
    canvas[:] = np.clip(canvas.astype(np.float32) * (1 - shadow[..., None] * rng.uniform(0.25, 0.6)), 0, 255)

    region = canvas[y:y + d, x:x + d].astype(np.float32)
    canvas[y:y + d, x:x + d] = np.clip(region * (1 - mask[..., None]) + patch.astype(np.float32) * mask[..., None], 0, 255)

    occupied.append((cx, cy, d))
    return crop.cls, cx / SIZE, cy / SIZE, d / SIZE, d / SIZE


def generate(dataset: Path, count: int, seed: int = 0) -> int:
    rng = np.random.default_rng(seed)
    images_dir = dataset / "images" / "train"
    labels_dir = dataset / "labels" / "train"

    crops: list[Crop] = []
    backgrounds: list[np.ndarray] = []
    for image_path in sorted(images_dir.glob("*")):
        if image_path.name.startswith("syn_"):
            continue
        image = cv2.imread(str(image_path))
        if image is None:
            continue
        if max(image.shape[:2]) > 1000:   # le foto grandi rallentano i filtri senza aggiungere nulla ai ritagli
            factor = 1000 / max(image.shape[:2])
            image = cv2.resize(image, None, fx=factor, fy=factor, interpolation=cv2.INTER_AREA)
        found, background = extract(image, read_labels(labels_dir / f"{image_path.stem}.txt"), rng)
        crops.extend(found)
        if background is not None:
            backgrounds.append(background)
    if not crops:
        raise SystemExit("Nessuna moneta ritagliabile nel train")

    by_class: dict[int, list[Crop]] = {}
    for crop in crops:
        by_class.setdefault(crop.cls, []).append(crop)
    classes = sorted(by_class)

    written = 0
    for i in range(count):
        if backgrounds and rng.random() < 0.6:
            canvas = backgrounds[int(rng.integers(len(backgrounds)))].copy()
            if rng.random() < 0.5:
                canvas = cv2.flip(canvas, int(rng.integers(-1, 2)))
        else:
            canvas = procedural_background(rng)

        scale = rng.uniform(3.2, 7.5)   # pixel per mm: 2€ da ~80 a ~190 px
        occupied: list = []
        labels = []
        for _ in range(int(rng.integers(2, 13))):
            cls = int(rng.choice(classes))
            crop = by_class[cls][int(rng.integers(len(by_class[cls])))]
            placed = paste(canvas, crop, scale, rng, occupied)
            if placed:
                labels.append(placed)
        if not labels:
            continue

        # luce e ombre globali, sopra tutta la scena
        for _ in range(int(rng.integers(0, 3))):
            canvas = add_shadow(canvas, rng)
        if rng.random() < 0.5:
            canvas = add_light_gradient(canvas, rng)

        name = f"syn_{seed}_{i:05d}"
        cv2.imwrite(str(images_dir / f"{name}.jpg"), canvas, [cv2.IMWRITE_JPEG_QUALITY, 90])
        (labels_dir / f"{name}.txt").write_text(
            "\n".join(f"{c} {cx:.6f} {cy:.6f} {w:.6f} {h:.6f}" for c, cx, cy, w, h in labels), encoding="utf-8")
        written += 1
    return written


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("dataset", type=Path)
    parser.add_argument("--count", type=int, default=1500)
    parser.add_argument("--seed", type=int, default=0)
    args = parser.parse_args()
    print(f"{generate(args.dataset, args.count, args.seed)} immagini sintetiche create")
