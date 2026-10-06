"""Local YOLOv8 recovery/training runner. Never downloads dependencies, weights or data.

The app prepares the portable environment separately. Supported numeric ONNX
weights can be recovered after forward-output comparison; other exports need
explicitly trusted source .pt weights. Training uses reviewed local datasets.
Output is JSON; a missing prerequisite is a blocker, never a successful training.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import socket
import sys


def sha256(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def inside(root: Path, relative: str) -> Path:
    path = (root / relative).resolve()
    if not path.is_relative_to(root.resolve()):
        raise ValueError("DatasetPathEscapesRoot")
    return path


def validate_dataset(root: Path) -> dict:
    manifest = json.loads((root / "manifest.json").read_text(encoding="utf-8-sig"))
    if manifest.get("Schema") != 1 or manifest.get("AnnotationSource") != "HumanReviewed" or manifest.get("SplitUnit") != "RecordingSession":
        raise ValueError("VerifiedDatasetRequired")
    train, validation = manifest["Train"], manifest["Validation"]
    if len(train) < 20 or len(validation) < 20 or sum(len(frame["GroundTruth"]) for frame in validation) < 20:
        raise ValueError("InsufficientReviewedDataset")
    if {x["SessionId"] for x in train} & {x["SessionId"] for x in validation}:
        raise ValueError("SessionLeakage")
    if {x["ImageSha256"] for x in train} & {x["ImageSha256"] for x in validation}:
        raise ValueError("ImageLeakage")
    names = manifest["ClassNames"]
    if not names or len(names) > 999:
        raise ValueError("InvalidClassNames")
    for split_name, frames in (("train", train), ("val", validation)):
        for frame in frames:
            image = inside(root, frame["ImagePath"])
            if sha256(image) != frame["ImageSha256"]:
                raise ValueError("ImageHashMismatch")
            expected_lines = []
            for box in frame["GroundTruth"]:
                import math
                if not all(math.isfinite(box[key]) for key in ("X", "Y", "Width", "Height")):
                    raise ValueError("InvalidBox")
                if not 0 <= box["ClassId"] < len(names) or box["Width"] <= 0 or box["Height"] <= 0 or box["X"] < 0 or box["Y"] < 0 or box["X"] + box["Width"] > 1.000001 or box["Y"] + box["Height"] > 1.000001:
                    raise ValueError("InvalidBox")
                expected_lines.append([box["ClassId"], box["X"] + box["Width"] / 2, box["Y"] + box["Height"] / 2, box["Width"], box["Height"]])
            labels = inside(root, f"labels/{split_name}/{frame['CaptureId']}.txt")
            actual_lines = [[float(value) for value in line.split()] for line in labels.read_text().splitlines() if line.strip()]
            if len(actual_lines) != len(expected_lines) or any(len(a) != 5 or any(abs(x-y) > 1e-8 for x,y in zip(a,b)) for a,b in zip(actual_lines, expected_lines)):
                raise ValueError("LabelsDoNotMatchReviewedManifest")
    # Reconstruct the training descriptor; never execute a user-provided YAML download key.
    # Ultralytics uses its val split to select best.pt. Keep the independent
    # validation sessions out of that optimization loop entirely. Internal
    # training metrics on the training partition are NOT quality evidence;
    # only the app's later ONNX comparison uses the reserved images/val split.
    dataset = {"path": str(root.resolve()).replace("\\", "/"), "train": "images/train", "val": "images/train", "names": {i: name for i, name in enumerate(names)}}
    (root / "offline-dataset.yaml").write_text(json.dumps(dataset), encoding="utf-8")
    return manifest


def block_network() -> None:
    def forbidden(*args, **kwargs):
        raise RuntimeError("NetworkDisabledForLocalLearning")
    socket.create_connection = forbidden
    socket.socket.connect = forbidden
    socket.socket.connect_ex = forbidden
    socket.socket.sendto = forbidden
    os.environ.update({"YOLO_OFFLINE": "true", "YOLO_AUTOINSTALL": "false", "WANDB_MODE": "disabled", "COMET_MODE": "DISABLED", "CLEARML_OFFLINE_MODE": "1", "ULTRALYTICS_HUB": "0", "MPLBACKEND": "Agg"})


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--mode", choices=["inspect", "train", "recover"], default="inspect")
    parser.add_argument("--onnx", type=Path)
    parser.add_argument("--weights", type=Path)
    parser.add_argument("--dataset", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--trusted-local-weights", action="store_true")
    parser.add_argument("--epochs", type=int, default=10)
    parser.add_argument("--device", choices=["cpu", "0"], default="cpu")
    parser.add_argument("--image-size", type=int, choices=[160, 256, 320, 416, 512, 640], default=640)
    args = parser.parse_args()
    if args.mode == "recover":
        block_network()
        try:
            from recover_onnx import recover
            print(json.dumps(recover(args.onnx, args.output)))
            return 0
        except Exception as exc:
            print(json.dumps({"Status": "Blocked", "Blockers": [type(exc).__name__ + ":" + str(exc)]}))
            return 2
    packages = {name: importlib.util.find_spec(name) is not None for name in ("torch", "ultralytics", "onnx")}
    blockers = [f"MissingDependency:{name}" for name, available in packages.items() if not available]
    if not args.weights or not args.weights.is_file() or args.weights.suffix.lower() != ".pt":
        blockers.append("TrainableSourceWeightsMissing")
    if args.mode == "inspect":
        print(json.dumps({"Status": "Blocked" if blockers else "PrerequisitesPresent", "Blockers": blockers, "Packages": packages, "Network": "DisabledDuringTraining"}))
        return 0
    if not args.trusted_local_weights:
        blockers.append("ExplicitlyTrustedSourceWeightsRequired")
    if not args.dataset or not args.dataset.is_dir():
        blockers.append("ReviewedDatasetMissing")
    if args.epochs < 1 or args.epochs > 100:
        blockers.append("InvalidEpochBudget")
    if not args.output or args.output.exists():
        blockers.append("NewOutputDirectoryRequired")
    if blockers:
        print(json.dumps({"Status": "Blocked", "Blockers": blockers}))
        return 2
    block_network()
    try:
        manifest = validate_dataset(args.dataset)
        from ultralytics import YOLO, settings
        import torch
        torch.set_num_threads(2)
        torch.set_num_interop_threads(1)
        import ultralytics.utils
        ultralytics.utils.ONLINE = False
        ultralytics.utils.AUTOINSTALL = False
        settings.update({"sync": False, "hub": False, "wandb": False, "clearml": False, "comet": False, "mlflow": False, "neptune": False})
        args.output.mkdir(parents=True, exist_ok=False)
        model = YOLO(str(args.weights.resolve()), task="detect")
        if model.task != "detect" or len(model.names) != len(manifest["ClassNames"]):
            raise ValueError("SourceClassesDoNotMatchDataset")
        # Existing Aimmy supports raw YOLOv8 output; compatibility is checked by the
        # app's ONNX evaluator before any model is selected. No auto-download base model.
        result = model.train(data=str((args.dataset / "offline-dataset.yaml").resolve()),
            epochs=args.epochs, imgsz=args.image_size, device=args.device, batch=4, workers=0,
            project=str(args.output.resolve()), name="training", exist_ok=False, pretrained=False,
            plots=False, amp=False, cache=False, seed=0, deterministic=True)
        best = Path(result.save_dir) / "weights" / "best.pt"
        if not best.is_file():
            raise RuntimeError("TrainingDidNotProduceWeights")
        trained = YOLO(str(best))
        exported = Path(trained.export(format="onnx", imgsz=args.image_size, dynamic=False,
            simplify=False, opset=17, nms=False, device="cpu"))
        if not exported.is_file():
            raise RuntimeError("ExportDidNotProduceOnnx")
        summary = {"Status": "CandidateAwaitingIndependentEvaluation", "CandidatePath": str(exported.resolve()),
            "CandidateSha256": sha256(exported), "CandidateWeightsPath": str(best.resolve()),
            "CandidateWeightsSha256": sha256(best), "SourceSha256": sha256(args.weights), "DatasetSha256": sha256(args.dataset / "manifest.json"),
            "Promoted": False, "Network": "Disabled"}
        (args.output / "result.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
        print(json.dumps(summary))
        return 0
    except Exception as exc:
        print(json.dumps({"Status": "Failed", "Error": type(exc).__name__, "Detail": str(exc), "Promoted": False}))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
