# /// script
# requires-python = ">=3.12"
# dependencies = ["pillow==13.0.0"]
# ///
"""Воспроизводит только утверждённые crops; полные исходники не копируются в репозиторий."""
import argparse
import hashlib
import io
import json
import os
import re
import shutil
import tempfile
from pathlib import Path

from PIL import Image, UnidentifiedImageError


def bounds(rect):
    return tuple(rect[key] for key in ("left", "top", "right", "bottom"))


def validate_recipe(recipe):
    """Отвергает неполные рецепты до подготовки выходных файлов."""
    try:
        source_hash = recipe["sourceSha256"]
        width, height = recipe["sourceWidth"], recipe["sourceHeight"]
        left, top, right, bottom = bounds(recipe["crop"])
        if (not isinstance(source_hash, str) or not re.fullmatch(r"[0-9a-f]{64}", source_hash)
                or any(type(v) is not int for v in (width, height, left, top, right, bottom))
                or not (1280 <= width <= 3840 and 720 <= height <= 2160)
                or width * 720 != height * 1280
                or not (0 <= left < right <= 1280 and 0 <= top < bottom <= 720)):
            raise ValueError
    except (KeyError, TypeError, ValueError) as error:
        raise ValueError("Неполный или недопустимый рецепт происхождения ассета") from error


def png_bytes(image):
    image.info.clear()
    output = io.BytesIO()
    image.save(output, format="PNG", optimize=False, compress_level=9)
    return output.getvalue()


def publish(outputs, root):
    """Публикует готовый комплект; при исключении восстанавливает прежние файлы."""
    staging = Path(tempfile.mkdtemp(prefix=".game-assets-", dir=root))
    replaced = []
    cleanup = True
    try:
        # Все новые файлы и резервные копии готовятся на том же томе до первой замены.
        backups = {}
        for index, (target, data) in enumerate(outputs.items()):
            (staging / f"new-{index}").write_bytes(data)
            backup = staging / f"old-{index}"
            if target.exists():
                shutil.copyfile(target, backup)
                backups[target] = backup
            else:
                backups[target] = None
        try:
            for index, target in enumerate(outputs):
                os.replace(staging / f"new-{index}", target)
                replaced.append(target)
        except BaseException:
            failures = []
            for target in reversed(replaced):
                try:
                    if backups[target] is None:
                        target.unlink()
                    else:
                        os.replace(backups[target], target)
                except OSError as error:
                    failures.append(error)
            if failures:
                cleanup = False
                raise RuntimeError(f"Не удалось восстановить комплект; резервные копии: {staging}") from failures[0]
            raise
    finally:
        if cleanup:
            shutil.rmtree(staging)


def prepare(source_paths: list[Path], root: Path):
    catalog_path = root / "assets/game/catalog.json"
    catalog = json.loads(catalog_path.read_text(encoding="utf-8"))
    negative_recipes = json.loads((root / "tests/fixtures/game/provenance.json").read_text(encoding="utf-8"))
    try:
        recipes = [variant for asset in catalog["assets"] for variant in asset["variants"]]
        recipes.extend(negative_recipes.values())
        filenames = [variant["file"] for asset in catalog["assets"] for variant in asset["variants"]]
        if any(not isinstance(asset["id"], str) for asset in catalog["assets"]):
            raise ValueError("Недопустимый идентификатор ассета")
    except (KeyError, TypeError, AttributeError) as error:
        raise ValueError("Неполный каталог ассетов или отрицательных образцов") from error
    if not catalog["assets"] or any(not asset["variants"] for asset in catalog["assets"]):
        raise ValueError("Каталог должен содержать ассеты с вариантами")
    output_names = set()
    for recipe in recipes:
        validate_recipe(recipe)
    for name in filenames:
        if (not isinstance(name, str) or not re.fullmatch(r"[a-zA-Z0-9_.-]+\.png", name)
                or name in output_names):
            raise ValueError("Недопустимое или повторное имя выходного PNG")
        output_names.add(name)
    if any(not re.fullmatch(r"[a-zA-Z0-9_-]+", name) for name in negative_recipes):
        raise ValueError("Недопустимое имя отрицательного образца")
    sources = {}
    for path in source_paths:
        try:
            data = path.read_bytes()
            source_hash = hashlib.sha256(data).hexdigest()
            with Image.open(io.BytesIO(data)) as image:
                if image.format != "PNG" or image.mode not in ("RGB", "RGBA"):
                    raise ValueError("Требуется исходник PNG RGB/RGBA")
                expected = [recipe for recipe in recipes if recipe["sourceSha256"] == source_hash]
                if any(image.size != (recipe["sourceWidth"], recipe["sourceHeight"]) for recipe in expected):
                    raise ValueError(f"Размер исходника не совпадает с происхождением: {source_hash}")
                if expected:
                    sources[source_hash] = image.convert("RGB")
                else:
                    image.load()
        except (OSError, UnidentifiedImageError, Image.DecompressionBombError) as error:
            raise ValueError("Исходник отсутствует или не является корректным PNG") from error
    missing = sorted({recipe["sourceSha256"] for recipe in recipes} - sources.keys())
    if missing:
        raise ValueError("Не переданы обязательные исходники с SHA256: " + ", ".join(missing))

    outputs = {}
    crops = {}
    for asset in catalog["assets"]:
        for variant in asset["variants"]:
            source = sources[variant["sourceSha256"]]
            logical = bounds(variant["crop"])
            physical = tuple(v * source.width // 1280 for v in logical)
            crop = source.crop(physical)
            output = root / "assets/game" / variant["file"]
            crops[variant["file"]] = crop
            outputs[output] = png_bytes(crop)
            variant["sha256"] = hashlib.sha256(outputs[output]).hexdigest()
    outputs[catalog_path] = (json.dumps(catalog, ensure_ascii=False, indent=2) + "\n").encode("utf-8")

    # Искусственные образцы содержат только неперсональные UI-crops на однотонном фоне.
    # Это доказательство масштабирования/ROI, а не работы нативного UI в другом разрешении.
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
                    patch = crops[variant["file"]].resize((box[2] - box[0], box[3] - box[1]), Image.Resampling.BOX)
                    canvas.paste(patch, box[:2])
            outputs[root / "tests/fixtures/game" / f"{state}-{height}.png"] = png_bytes(canvas)

    for name, recipe in negative_recipes.items():
        source = sources[recipe["sourceSha256"]]
        left, top, right, bottom = bounds(recipe["crop"])
        box = (left * source.width // 1280, top * source.height // 720,
               right * source.width // 1280, bottom * source.height // 720)
        canvas = Image.new("RGB", source.size, (37, 46, 58))
        canvas.paste(source.crop(box), box[:2])
        output = root / "tests/fixtures/game" / f"{name}-{source.height}.png"
        if output in outputs:
            raise ValueError("Повторный путь выходного образца")
        outputs[output] = png_bytes(canvas)
    publish(outputs, root)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, action="append", required=True)
    args = parser.parse_args()
    try:
        prepare(args.source, Path(__file__).resolve().parents[2])
    except (ValueError, OSError, RuntimeError) as error:
        parser.exit(1, f"Подготовка ассетов не выполнена: {error}\n")
