import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

script = Path(__file__).resolve().parents[2] / "scripts" / "local-learning" / "runner.py"
spec = importlib.util.spec_from_file_location("local_learning_runner", script)
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)


class OfflineRunnerValidation(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="AutoAimmyRunnerChecks-")
        self.root = Path(self.temporary.name)
        self.manifest = {"Schema": 1, "AnnotationSource": "HumanReviewed", "SplitUnit": "RecordingSession", "ClassNames": ["Enemy"], "Train": [], "Validation": []}
        for split, key in (("train", "Train"), ("val", "Validation")):
            (self.root / "images" / split).mkdir(parents=True)
            (self.root / "labels" / split).mkdir(parents=True)
            for index in range(20):
                # This validates metadata and hashes only. No training/image decoding is claimed.
                image = f"synthetic hash fixture {split} {index}".encode()
                digest = hashlib.sha256(image).hexdigest()
                image_path = f"images/{split}/{digest}.jpg"
                (self.root / image_path).write_bytes(image)
                (self.root / "labels" / split / f"{digest}.txt").write_text("0 0.3 0.4 0.2 0.4\n")
                self.manifest[key].append({"CaptureId": digest, "SessionId": split, "ImagePath": image_path, "ImageSha256": digest,
                    "GroundTruth": [{"ClassId": 0, "X": .2, "Y": .2, "Width": .2, "Height": .4}]})
        self.save()

    def save(self):
        (self.root / "manifest.json").write_text(json.dumps(self.manifest))

    def tearDown(self):
        self.temporary.cleanup()

    def test_independent_validation_never_used_by_training_loop(self):
        runner.validate_dataset(self.root)
        descriptor = json.loads((self.root / "offline-dataset.yaml").read_text())
        self.assertEqual(descriptor["val"], "images/train")
        self.assertNotIn("download", descriptor)

    def test_proposals_cannot_train(self):
        self.manifest["AnnotationSource"] = "ModelPrediction"
        self.save()
        with self.assertRaisesRegex(ValueError, "VerifiedDatasetRequired"):
            runner.validate_dataset(self.root)

    def test_same_session_leakage_rejected(self):
        self.manifest["Validation"][0]["SessionId"] = "train"
        self.save()
        with self.assertRaisesRegex(ValueError, "SessionLeakage"):
            runner.validate_dataset(self.root)

    def test_changed_image_rejected(self):
        (self.root / self.manifest["Train"][0]["ImagePath"]).write_bytes(b"changed")
        with self.assertRaisesRegex(ValueError, "ImageHashMismatch"):
            runner.validate_dataset(self.root)

    def test_changed_yolo_labels_rejected(self):
        first = self.manifest["Train"][0]["CaptureId"]
        (self.root / "labels" / "train" / f"{first}.txt").write_text("0 0.5 0.5 0.2 0.2")
        with self.assertRaisesRegex(ValueError, "LabelsDoNotMatchReviewedManifest"):
            runner.validate_dataset(self.root)

    def test_path_escape_rejected(self):
        self.manifest["Train"][0]["ImagePath"] = "../escape.jpg"
        self.save()
        with self.assertRaisesRegex(ValueError, "DatasetPathEscapesRoot"):
            runner.validate_dataset(self.root)


if __name__ == "__main__":
    unittest.main()
