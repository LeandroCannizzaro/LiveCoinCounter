"""Ombre e variazioni di luce sintetiche per le foto di monete.

Servono a due cose:
- costruire un banco di prova che misuri la robustezza alle ombre (stesse etichette, immagine peggiorata);
- dare ombre realistiche alle foto sintetiche (coin_ml.synth).

Tutto è deterministico dato il generatore casuale, così il banco di prova è sempre lo stesso.
"""
import cv2
import numpy as np


def _soft_mask(shape: tuple[int, int], rng: np.random.Generator) -> np.ndarray:
    """Maschera 0..1 di una zona d'ombra con bordo morbido: poligono grande o fascia (braccio, telefono, oggetto)."""
    h, w = shape
    mask = np.zeros((h, w), np.uint8)

    if rng.random() < 0.5:
        # fascia: segmento spesso che attraversa la scena
        p1 = (int(rng.uniform(-0.2, 1.2) * w), int(rng.uniform(-0.2, 1.2) * h))
        angle = rng.uniform(0, np.pi)
        length = max(h, w) * 2
        p2 = (int(p1[0] + np.cos(angle) * length), int(p1[1] + np.sin(angle) * length))
        p1 = (int(p1[0] - np.cos(angle) * length), int(p1[1] - np.sin(angle) * length))
        cv2.line(mask, p1, p2, 255, int(rng.uniform(0.15, 0.5) * min(h, w)))
    else:
        count = int(rng.integers(3, 7))
        center = np.array([rng.uniform(0, w), rng.uniform(0, h)])
        angles = np.sort(rng.uniform(0, 2 * np.pi, count))
        radius = rng.uniform(0.25, 0.8) * max(h, w)
        points = np.stack([center[0] + np.cos(angles) * radius * rng.uniform(0.5, 1.2, count),
                           center[1] + np.sin(angles) * radius * rng.uniform(0.5, 1.2, count)], axis=1)
        cv2.fillPoly(mask, [points.astype(np.int32)], 255)

    sigma = rng.uniform(0.02, 0.08) * max(h, w)
    return cv2.GaussianBlur(mask, (0, 0), sigma).astype(np.float32) / 255.0


def add_shadow(image: np.ndarray, rng: np.random.Generator, strength: tuple[float, float] = (0.3, 0.7)) -> np.ndarray:
    """Scurisce una zona dell'immagine BGR uint8 (leggermente più fredda, come un'ombra reale)."""
    mask = _soft_mask(image.shape[:2], rng)
    dark = rng.uniform(*strength)                       # quanto si scurisce al centro dell'ombra
    tint = np.array([1.0, 0.97, 0.93], np.float32)      # BGR: meno rosso
    gain = 1.0 - mask[..., None] * (1.0 - (1.0 - dark) * tint)
    return np.clip(image.astype(np.float32) * gain, 0, 255).astype(np.uint8)


def add_light_gradient(image: np.ndarray, rng: np.random.Generator) -> np.ndarray:
    """Luce non uniforme: una parte più chiara e una più scura, lungo una direzione casuale."""
    h, w = image.shape[:2]
    angle = rng.uniform(0, 2 * np.pi)
    ys, xs = np.mgrid[0:h, 0:w].astype(np.float32)
    t = (xs - w / 2) * np.cos(angle) + (ys - h / 2) * np.sin(angle)
    t = t / (np.abs(t).max() + 1e-6)
    gain = 1.0 + t * rng.uniform(0.15, 0.45)
    return np.clip(image.astype(np.float32) * gain[..., None], 0, 255).astype(np.uint8)


def stress(image: np.ndarray, rng: np.random.Generator) -> np.ndarray:
    """Peggiora una foto: da 1 a 2 ombre, luce non uniforme, un po' meno contrasto."""
    out = image
    for _ in range(int(rng.integers(1, 3))):
        out = add_shadow(out, rng)
    if rng.random() < 0.6:
        out = add_light_gradient(out, rng)
    alpha = rng.uniform(0.75, 1.0)
    mean = out.mean()
    return np.clip((out.astype(np.float32) - mean) * alpha + mean, 0, 255).astype(np.uint8)
