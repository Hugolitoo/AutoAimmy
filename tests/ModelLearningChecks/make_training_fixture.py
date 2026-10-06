"""Synthetic integration fixture only: not player data or a real-game quality benchmark."""
from pathlib import Path
import hashlib
import json
import random
import sys
from PIL import Image, ImageDraw

root = Path(sys.argv[1]); root.mkdir(parents=True, exist_ok=False)
manifest = {"Schema": 1, "Id": "synthetic-training-integration", "AnnotationSource": "HumanReviewed",
            "Purpose": "SyntheticIntegrationTest", "SplitUnit": "RecordingSession", "ClassNames": ["Enemy"], "Train": [], "Validation": []}
rng = random.Random(14)
for split, key in (("train", "Train"), ("val", "Validation")):
    (root / "images" / split).mkdir(parents=True); (root / "labels" / split).mkdir(parents=True)
    for index in range(20):
        image = Image.new("RGB", (320, 320), (rng.randrange(40, 80), rng.randrange(40, 80), rng.randrange(40, 80)))
        draw = ImageDraw.Draw(image)
        x, y, width, height = rng.randrange(60, 180), rng.randrange(30, 90), 40, 120
        draw.ellipse((x + 8, y, x + 32, y + 24), fill=(190, 150, 120))
        draw.rectangle((x, y + 25, x + width, y + 80), fill=(40, 90, 130))
        draw.rectangle((x, y + 80, x + 15, y + height), fill=(90, 90, 100))
        draw.rectangle((x + 25, y + 80, x + width, y + height), fill=(90, 90, 100))
        temporary = root / f"image-{split}-{index}.jpg"; image.save(temporary)
        digest = hashlib.sha256(temporary.read_bytes()).hexdigest()
        relative = f"images/{split}/{digest}.jpg"; temporary.rename(root / relative)
        box = {"ClassId": 0, "X": x / 320, "Y": y / 320, "Width": width / 320, "Height": height / 320}
        (root / "labels" / split / f"{digest}.txt").write_text(f"0 {(x+width/2)/320} {(y+height/2)/320} {width/320} {height/320}\n")
        manifest[key].append({"CaptureId": digest, "SessionId": f"synthetic-{split}", "ImagePath": relative, "ImageSha256": digest, "GroundTruth": [box]})
(root / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
