"""Addestramento di prova (baseline) di un detector YOLO per le monete euro, da eseguire come script su Kaggle (GPU).

Legge il dataset prodotto da coin_ml.make_dataset (caricato come dataset Kaggle privato), addestra, valuta sul test set
con le metriche che interessano davvero (totale esatto per foto) e salva in /kaggle/working:
  best.pt, best.onnx, metrics.json
"""
import json
import shutil
import subprocess
import sys
import time
from pathlib import Path

EPOCHS = 100
IMGSZ = 640
BATCH = 16
MODEL = "yolo11n.pt"
CONF = 0.25

WORK = Path("/kaggle/working")


def ensure_ultralytics():
    try:
        import ultralytics  # noqa: F401
    except ImportError:
        subprocess.check_call([sys.executable, "-m", "pip", "install", "-q", "ultralytics"])


def find_dataset() -> Path:
    for marker in Path("/kaggle/input").rglob("splits.json"):
        return marker.parent
    raise SystemExit("Dataset non trovato in /kaggle/input")


def main():
    started = time.time()
    ensure_ultralytics()
    from ultralytics import YOLO

    # Copia in /kaggle/working: Ultralytics scrive file di cache accanto alle etichette
    data = WORK / "coins"
    shutil.copytree(find_dataset(), data)
    sys.path.insert(0, str(data / "code"))
    import scoring

    names = {i: n for i, n in enumerate(scoring.CLASS_NAMES)}
    yaml_path = WORK / "coins.yaml"
    yaml_path.write_text(
        f"path: {data}\ntrain: images/train\nval: images/val\ntest: images/test\nnames:\n"
        + "".join(f"  {i}: {n}\n" for i, n in names.items()),
        encoding="utf-8",
    )

    model = YOLO(MODEL)
    model.train(data=str(yaml_path), epochs=EPOCHS, imgsz=IMGSZ, batch=BATCH, patience=20,
                project=str(WORK / "runs"), name="coins", exist_ok=True, seed=0, workers=2, plots=False)

    best_path = WORK / "runs" / "coins" / "weights" / "best.pt"
    best = YOLO(str(best_path))

    metrics = {"model": MODEL, "epochs_requested": EPOCHS, "imgsz": IMGSZ, "conf": CONF}
    for split in ("val", "test"):
        box = best.val(data=str(yaml_path), split=split, imgsz=IMGSZ, verbose=False).box
        metrics[f"{split}_map50"] = round(float(box.map50), 3)
        metrics[f"{split}_map50_95"] = round(float(box.map), 3)

        images = sorted((data / "images" / split).glob("*"))
        truth = scoring.read_truth(data / "labels" / split)
        predictions = {}
        for image in images:
            result = best.predict(str(image), conf=CONF, iou=0.5, imgsz=IMGSZ, verbose=False)[0]
            predictions[image.stem] = [int(c) for c in result.boxes.cls.tolist()]

        metrics[f"{split}_per_image"] = scoring.score(predictions, truth)
        for source, prefix in (("kaggle", "kg_"), ("roboflow", "rf_")):
            subset = [n for n in truth if n.startswith(prefix)]
            if subset:
                metrics[f"{split}_per_image_{source}"] = scoring.score(
                    {n: predictions[n] for n in subset}, {n: truth[n] for n in subset})

    onnx = best.export(format="onnx", imgsz=IMGSZ, simplify=True)
    shutil.copy2(best_path, WORK / "best.pt")
    shutil.copy2(onnx, WORK / "best.onnx")

    metrics["minutes"] = round((time.time() - started) / 60, 1)
    (WORK / "metrics.json").write_text(json.dumps(metrics, indent=2), encoding="utf-8")
    print(json.dumps(metrics, indent=2))

    shutil.rmtree(data, ignore_errors=True)  # non restituire il dataset tra i file di output


if __name__ == "__main__":
    main()
