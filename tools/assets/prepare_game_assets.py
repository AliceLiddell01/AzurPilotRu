# /// script
# requires-python = ">=3.12"
# dependencies = ["pillow==13.0.0"]
# ///
"""Воспроизводит только утверждённые crops; полные исходники не копируются в репозиторий."""
import argparse
import hashlib
import json
from pathlib import Path

from PIL import Image


def bounds(rect):
    return tuple(rect[key] for key in ("left", "top", "right", "bottom"))


def prepare(source_paths: list[Path], root: Path):
    catalog_path = root / "assets/game/catalog.json"
    catalog = json.loads(catalog_path.read_text(encoding="utf-8"))
    sources = {}
    for path in source_paths:
        source_hash = hashlib.sha256(path.read_bytes()).hexdigest()
        with Image.open(path) as image:
            sources[source_hash] = image.convert("RGB")
    for asset in catalog["assets"]:
        for variant in asset["variants"]:
            source = sources[variant["sourceSha256"]]
            if source.size != (variant["sourceWidth"], variant["sourceHeight"]):
                raise ValueError("Размер source не совпадает с provenance")
            logical = bounds(variant["crop"])
            physical = tuple(v * source.width // 1280 for v in logical)
            crop = source.crop(physical)
            crop.info.clear()
            output = root / "assets/game" / variant["file"]
            crop.save(output, optimize=False, compress_level=9)
            variant["sha256"] = hashlib.sha256(output.read_bytes()).hexdigest()
    catalog_path.write_text(json.dumps(catalog, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    # Synthetic fixtures содержат только неперсональные UI-crops на однотонном canvas.
    # Это доказательство scaling/ROI, а не работы нативного UI в другом разрешении.
    for width, height in ((1280, 720), (1920, 1080), (2560, 1440), (3840, 2160)):
        for state in ("main", "dock", "selection"):
            canvas = Image.new("RGB", (width, height), (37, 46, 58))
            for asset in catalog["assets"]:
                included = (state == "main" and asset["id"].startswith("main.")) or (
                    state in ("dock", "selection") and asset["id"] == "dock.header") or (
                    state == "selection" and asset["id"] == "dock.selection-confirm")
                if included:
                    variant = asset["variants"][0]
                    left, top, right, bottom = bounds(variant["crop"])
                    box = (left * width // 1280, top * height // 720,
                           (right * width + 1279) // 1280, (bottom * height + 719) // 720)
                    with Image.open(root / "assets/game" / variant["file"]) as crop:
                        patch = crop.resize((box[2] - box[0], box[3] - box[1]), Image.Resampling.BOX)
                        canvas.paste(patch, box[:2])
            canvas.save(root / "tests/fixtures/game" / f"{state}-{height}.png", compress_level=9)

    negative_recipes = json.loads((root / "tests/fixtures/game/provenance.json").read_text(encoding="utf-8"))
    for name, recipe in negative_recipes.items():
        source = sources[recipe["sourceSha256"]]
        if source.size != (recipe["sourceWidth"], recipe["sourceHeight"]):
            raise ValueError("Размер negative source не совпадает с provenance")
        left, top, right, bottom = bounds(recipe["crop"])
        box = (left * source.width // 1280, top * source.height // 720,
               right * source.width // 1280, bottom * source.height // 720)
        canvas = Image.new("RGB", source.size, (37, 46, 58))
        canvas.paste(source.crop(box), box[:2])
        canvas.save(root / "tests/fixtures/game" / f"{name}-{source.height}.png", compress_level=9)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, action="append", required=True)
    args = parser.parse_args()
    prepare(args.source, Path(__file__).resolve().parents[2])
