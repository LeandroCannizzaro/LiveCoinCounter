"""Addestramento di un detector YOLO per le monete euro, da eseguire come script su Kaggle (GPU).

Legge il dataset prodotto da coin_ml.make_dataset (+ foto sintetiche di coin_ml.synth), esegue gli esperimenti di
EXPERIMENTS uno dopo l'altro e per ognuno valuta con le metriche che interessano davvero (totale esatto per foto) su
val e test normali e sui banchi di prova con ombre (val_shadow / test_shadow). Salva in /kaggle/working:
  <esperimento>/best.pt, <esperimento>/best.onnx, metrics.json (tutti gli esperimenti)
"""
import json
import shutil
import subprocess
import sys
import time
from pathlib import Path

CONF = 0.25

# Un esperimento = un addestramento. n_base: solo augmentation standard e foto reali (riferimento equo sulla nuova divisione);
# n_photo: + ombre e luce in addestramento; n_aug: + foto sintetiche; poi modello più grande e risoluzione maggiore.
EXPERIMENTS = [
    dict(name="n_base", model="yolo11n.pt", imgsz=640, epochs=100, synthetic=False, photometric=False),
    dict(name="n_photo", model="yolo11n.pt", imgsz=640, epochs=100, synthetic=False, photometric=True),
    dict(name="n_aug", model="yolo11n.pt", imgsz=640, epochs=100, synthetic=True, photometric=True),
    dict(name="s_aug", model="yolo11s.pt", imgsz=640, epochs=100, synthetic=True, photometric=True),
    dict(name="n_aug_960", model="yolo11n.pt", imgsz=960, epochs=100, synthetic=True, photometric=True, batch=8),
]

WORK = Path("/kaggle/working")


def pip_install(*packages):
    subprocess.check_call([sys.executable, "-m", "pip", "install", "-q", *packages])


def find_dataset() -> Path:
    for marker in Path("/kaggle/input").rglob("splits.json"):
        return marker.parent
    raise SystemExit("Dataset non trovato in /kaggle/input")


def photometric_augmentations():
    """Ombre, luce e contrasto variabili: il punto debole del modello. Se la libreria non c'è si va avanti senza."""
    try:
        import albumentations as A
        return [
            A.RandomShadow(shadow_roi=(0, 0, 1, 1), num_shadows_limit=(1, 3), shadow_dimension=5, p=0.5),
            A.RandomBrightnessContrast(brightness_limit=0.3, contrast_limit=0.3, p=0.5),
            A.RandomGamma(gamma_limit=(60, 140), p=0.3),
            A.GaussianBlur(blur_limit=(3, 5), p=0.1),
            A.ImageCompression(quality_range=(50, 95), p=0.2),
        ]
    except Exception as exc:  # versione diversa dell'API o libreria assente
        print("Augmentation fotometriche non disponibili:", exc)
        return None


def predict_dir(model, folder: Path, imgsz: int, **kwargs):
    predictions = {}
    for image in sorted(folder.glob("*")):
        result = model.predict(str(image), conf=CONF, imgsz=imgsz, verbose=False, **kwargs)[0]
        predictions[image.stem] = [int(c) for c in result.boxes.cls.tolist()]
    return predictions


def evaluate(model, data: Path, yaml_path: Path, imgsz: int, scoring) -> dict:
    metrics = {}
    for split in ("val", "test"):
        box = model.val(data=str(yaml_path), split=split, imgsz=imgsz, verbose=False).box
        metrics[f"{split}_map50"] = round(float(box.map50), 3)
        metrics[f"{split}_map50_95"] = round(float(box.map), 3)

    # per immagine: normale e con ombre, NMS per classe (come in app prima) e indifferente alla classe
    for split in ("val", "test", "val_shadow", "test_shadow"):
        truth = scoring.read_truth(data / "labels" / split)
        for label, kwargs in (("", dict(iou=0.7)), ("_agnostic", dict(iou=0.6, agnostic_nms=True))):
            predictions = predict_dir(model, data / "images" / split, imgsz, **kwargs)
            metrics[f"{split}{label}"] = scoring.score(predictions, truth)
            if label == "":
                for source, prefix in (("kaggle", "kg_"), ("roboflow", "rf_")):
                    subset = [n for n in truth if n.startswith(prefix)]
                    if subset:
                        metrics[f"{split}_{source}"] = scoring.score(
                            {n: predictions[n] for n in subset}, {n: truth[n] for n in subset})
    return metrics


def main():
    started = time.time()
    pip_install("ultralytics", "albumentations")
    from ultralytics import YOLO

    source = find_dataset()
    sys.path.insert(0, str(source / "code"))
    import scoring

    all_metrics = {}
    for experiment in EXPERIMENTS:
        name = experiment["name"]
        print(f"=== {name} ===", flush=True)
        t0 = time.time()

        # Copia in /kaggle/working: Ultralytics scrive file di cache accanto alle etichette
        data = WORK / "coins"
        shutil.rmtree(data, ignore_errors=True)
        shutil.copytree(source, data)
        if not experiment["synthetic"]:
            for path in list((data / "images" / "train").glob("syn_*")) + list((data / "labels" / "train").glob("syn_*")):
                path.unlink()

        yaml_path = WORK / "coins.yaml"
        yaml_path.write_text(
            f"path: {data}\ntrain: images/train\nval: images/val\ntest: images/test\nnames:\n"
            + "".join(f"  {i}: {n}\n" for i, n in enumerate(scoring.CLASS_NAMES)), encoding="utf-8")

        train_args = dict(data=str(yaml_path), epochs=experiment["epochs"], imgsz=experiment["imgsz"],
                          batch=experiment.get("batch", 16), patience=25, project=str(WORK / "runs"), name=name,
                          exist_ok=True, seed=0, workers=2, plots=False,
                          hsv_v=0.5, hsv_s=0.7, flipud=0.5, scale=0.5)
        if experiment["photometric"]:
            augmentations = photometric_augmentations()
            if augmentations:
                train_args["augmentations"] = augmentations

        YOLO(experiment["model"]).train(**train_args)

        best_path = WORK / "runs" / name / "weights" / "best.pt"
        best = YOLO(str(best_path))
        metrics = {**{k: v for k, v in experiment.items()}, "conf": CONF}
        metrics.update(evaluate(best, data, yaml_path, experiment["imgsz"], scoring))

        out = WORK / name
        out.mkdir(exist_ok=True)
        shutil.copy2(best_path, out / "best.pt")
        shutil.copy2(best.export(format="onnx", imgsz=experiment["imgsz"], simplify=True), out / "best.onnx")
        metrics["minutes"] = round((time.time() - t0) / 60, 1)
        all_metrics[name] = metrics
        (WORK / "metrics.json").write_text(json.dumps(all_metrics, indent=2), encoding="utf-8")

        summary = {k: metrics[k]["total_exact"] for k in ("val", "test", "val_shadow", "test_shadow")}
        print(name, summary, flush=True)

    shutil.rmtree(WORK / "coins", ignore_errors=True)  # non restituire il dataset tra i file di output
    shutil.rmtree(WORK / "runs", ignore_errors=True)
    print(f"Totale: {(time.time() - started) / 60:.0f} minuti")


if __name__ == "__main__":
    main()
