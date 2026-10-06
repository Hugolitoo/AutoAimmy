"""Recover supported YOLOv8 weights from numeric ONNX tensors, without loading a supplied pickle.

Every convolution and learned parameter must match a built-in architecture, and
two real forward passes must agree with the original ONNX before saving weights.
Unsupported exports are rejected rather than replaced with a different model.
"""
from pathlib import Path
import ast
import hashlib
import json


def recover(source: Path, output: Path) -> dict:
    import numpy as np
    import onnx
    from onnx import numpy_helper
    import onnxruntime as ort
    import torch
    from ultralytics.nn.tasks import DetectionModel
    from ultralytics.nn.modules import Conv

    if not source.is_file() or source.stat().st_size > 256 * 1024 * 1024 or output.exists():
        raise ValueError("UnsupportedSourceOrExistingRecoveryDirectory")
    graph = onnx.load(str(source), load_external_data=False)
    if any(x.data_location == onnx.TensorProto.EXTERNAL for x in graph.graph.initializer):
        raise ValueError("ExternalWeightsUnsupported")
    metadata = {x.key: x.value for x in graph.metadata_props}
    if metadata.get("task") != "detect" or len(metadata.get("names", "")) > 10000:
        raise ValueError("YOLOv8DetectionMetadataRequired")
    names = ast.literal_eval(metadata["names"])
    if not isinstance(names, dict) or list(sorted(names)) != list(range(len(names))) or not 0 < len(names) < 100:
        raise ValueError("InvalidClassMetadata")
    if any(not isinstance(x, str) or len(x) > 100 for x in names.values()):
        raise ValueError("InvalidClassNames")
    weights = {x.name: numpy_helper.to_array(x).copy() for x in graph.graph.initializer if x.name.endswith((".weight", ".bias"))}
    if not weights or sum(x.size for x in weights.values()) > 50000000 or any(not np.isfinite(x).all() for x in weights.values()):
        raise ValueError("InvalidNumericWeights")
    convolution_names = {n.input[1] for n in graph.graph.node if n.op_type == "Conv"}
    if not convolution_names.issubset(weights):
        raise ValueError("UnnamedOrUnsupportedConvolutions")
    torch.set_num_threads(2)
    model = None
    for size in ("n", "s", "m", "l", "x"):
        candidate = DetectionModel(f"yolov8{size}.yaml", nc=len(names), verbose=False)
        modules = dict(candidate.named_modules())
        convolutions = {name + ".weight": module for name, module in modules.items() if isinstance(module, torch.nn.Conv2d)}
        if set(convolutions) != convolution_names or any(tuple(weights[name].shape) != tuple(layer.weight.shape) for name, layer in convolutions.items()):
            continue
        with torch.no_grad():
            for name, layer in convolutions.items():
                layer.weight.copy_(torch.from_numpy(weights[name]))
                prefix = name[:-7]
                bias = weights.get(prefix + ".bias")
                parent_name = prefix.rsplit(".", 1)[0]
                parent = modules.get(parent_name)
                if isinstance(parent, Conv) and parent.conv is layer:
                    # Undo the fusion with an identity BN whose bias carries the fused bias.
                    parent.bn.running_mean.zero_(); parent.bn.running_var.fill_(1)
                    parent.bn.weight.fill_((1 + parent.bn.eps) ** .5)
                    parent.bn.bias.copy_(torch.from_numpy(bias) if bias is not None else torch.zeros_like(parent.bn.bias))
                    parent.bn.num_batches_tracked.zero_()
                elif layer.bias is not None:
                    if bias is None or tuple(bias.shape) != tuple(layer.bias.shape):
                        raise ValueError("MissingConvolutionBias")
                    layer.bias.copy_(torch.from_numpy(bias))
                elif bias is not None:
                    raise ValueError("UnexpectedConvolutionBias")
        model = candidate.eval()
        break
    if model is None:
        raise ValueError("UnsupportedYOLOArchitecture")
    model.names = names
    options = ort.SessionOptions(); options.intra_op_num_threads = 2
    session = ort.InferenceSession(str(source), sess_options=options, providers=["CPUExecutionProvider"])
    shape = session.get_inputs()[0].shape
    if len(shape) != 4 or shape[:2] != [1, 3] or shape[2:] != [640, 640]:
        raise ValueError("UnsupportedInputShape")
    worst_boxes = worst_scores = 0.0
    random = np.random.default_rng(17)
    for pixels in (np.zeros(shape, dtype=np.float32), random.random(shape, dtype=np.float32)):
        baseline = session.run(None, {session.get_inputs()[0].name: pixels})[0]
        with torch.no_grad():
            restored = model(torch.from_numpy(pixels))[0].numpy()
        if baseline.shape != restored.shape or baseline.shape[1] != 4 + len(names):
            raise ValueError("RestoredOutputMismatch")
        worst_boxes = max(worst_boxes, float(np.max(np.abs(baseline[:, :4] - restored[:, :4]))))
        worst_scores = max(worst_scores, float(np.max(np.abs(baseline[:, 4:] - restored[:, 4:]))))
    if worst_boxes > .05 or worst_scores > .0002:
        raise ValueError(f"RecoveryNotEquivalent:boxes={worst_boxes},scores={worst_scores}")
    output.mkdir(parents=True, exist_ok=False)
    checkpoint = output / "recovered.pt"
    torch.save({"model": model, "ema": None, "optimizer": None, "epoch": -1,
                "train_args": {"task": "detect", "imgsz": 640}, "version": "8.3.40"}, checkpoint)
    summary = {"Status": "RecoveredEquivalentWeights", "WeightsPath": str(checkpoint.resolve()),
               "WeightsSha256": hashlib.sha256(checkpoint.read_bytes()).hexdigest(),
               "SourceOnnxSha256": hashlib.sha256(source.read_bytes()).hexdigest(),
               "MaximumBoxDifference": worst_boxes, "MaximumScoreDifference": worst_scores}
    (output / "recovery.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    return summary
