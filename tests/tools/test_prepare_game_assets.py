# /// script
# requires-python = ">=3.12"
# dependencies = ["pillow==13.0.0"]
# ///
"""Проверки целостности генератора на искусственных исходниках без данных аккаунта."""
import hashlib
import importlib.util
import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from PIL import Image

sys.dont_write_bytecode = True
REPOSITORY = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("prepare_game_assets", REPOSITORY / "tools/assets/prepare_game_assets.py")
generator = importlib.util.module_from_spec(spec)
spec.loader.exec_module(generator)


class PrepareGameAssetsTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        (self.root / "assets/game").mkdir(parents=True)
        (self.root / "tests/fixtures/game").mkdir(parents=True)
        self.catalog_path = self.root / "assets/game/catalog.json"
        self.provenance_path = self.root / "tests/fixtures/game/provenance.json"
        self.sources = []
        recipes = []
        for index in range(3):
            source = self.root / f"source-{index}.png"
            Image.new("RGB", (1280, 720), (20 + index * 40, 80, 120)).save(source)
            self.sources.append(source)
            recipes.append(dict(sourceSha256=hashlib.sha256(source.read_bytes()).hexdigest(),
                                sourceWidth=1280, sourceHeight=720,
                                crop=dict(left=2, top=0, right=24, bottom=16)))
        self.catalog = dict(assets=[dict(id="dock.header", variants=[dict(
            recipes[0], file="dock.header.png", sha256="0" * 64)])])
        self.provenance = dict(depot=recipes[1], technology=recipes[2])
        self.write_recipes()
        (self.root / "tests/fixtures/game/states.json").write_text(json.dumps(
            {"main": [], "dock": ["dock.header"], "selection": ["dock.header"]}), encoding="utf-8")
        # Старый комплект намеренно отличается от результата генерации.
        for relative in ["assets/game/dock.header.png", "tests/fixtures/game/dock-720.png",
                         "tests/fixtures/game/depot-720.png", "tests/fixtures/game/technology-720.png"]:
            (self.root / relative).write_bytes(b"previous artifact")

    def write_recipes(self):
        self.catalog_path.write_text(json.dumps(self.catalog), encoding="utf-8")
        self.provenance_path.write_text(json.dumps(self.provenance), encoding="utf-8")

    def snapshot(self):
        return {path.relative_to(self.root).as_posix(): path.read_bytes()
                for directory in ("assets/game", "tests/fixtures/game")
                for path in (self.root / directory).rglob("*") if path.is_file()}

    def assert_rejected_unchanged(self, sources, message):
        before = self.snapshot()
        with self.assertRaisesRegex(ValueError, message):
            generator.prepare(sources, self.root)
        self.assertEqual(before, self.snapshot(), "Отказ не должен изменять ни один артефакт")
        self.assertEqual([], list(self.root.glob(".game-assets-*")))

    def test_missing_negative_sources_preserve_entire_set(self):
        for index in (1, 2):
            with self.subTest(negative=index):
                self.assert_rejected_unchanged([p for i, p in enumerate(self.sources) if i != index], "SHA256")

    def test_missing_states_composition_preserves_entire_set(self):
        (self.root / "tests/fixtures/game/states.json").unlink()
        self.assert_rejected_unchanged(self.sources, "states.json")

    def test_data_driven_states_reject_unknown_asset_without_publication(self):
        states = self.root / "tests/fixtures/game/states.json"
        states.write_text(json.dumps({"known": ["missing.asset"]}), encoding="utf-8")
        self.assert_rejected_unchanged(self.sources, "состав")

    def test_data_driven_states_generate_only_selected_patches(self):
        states = self.root / "tests/fixtures/game/states.json"
        states.write_text(json.dumps({"positive": ["dock.header"], "negative": []}), encoding="utf-8")
        generator.prepare(self.sources, self.root)
        with Image.open(self.root / "tests/fixtures/game/positive-720.png") as positive, Image.open(
                self.root / "tests/fixtures/game/negative-720.png") as negative:
            self.assertEqual((20, 80, 120), positive.getpixel((3, 3)))
            self.assertEqual((37, 46, 58), negative.getpixel((3, 3)))

    def test_wrong_negative_hash_preserves_entire_set(self):
        Image.new("RGB", (1280, 720), (1, 2, 3)).save(self.sources[2])
        self.assert_rejected_unchanged(self.sources, "SHA256")

    def test_wrong_negative_dimensions_preserve_entire_set(self):
        self.provenance["technology"]["sourceWidth"] = 1920
        self.provenance["technology"]["sourceHeight"] = 1080
        self.write_recipes()
        self.assert_rejected_unchanged(self.sources, "Размер")

    def test_corrupt_negative_source_preserves_entire_set(self):
        self.sources[2].write_bytes(b"not a PNG")
        self.assert_rejected_unchanged(self.sources, "корректным PNG")

    def test_missing_source_file_preserves_entire_set(self):
        self.sources[2].unlink()
        self.assert_rejected_unchanged(self.sources, "отсутствует")

    def test_wrong_source_format_preserves_entire_set(self):
        Image.new("RGB", (1280, 720)).save(self.sources[2], format="JPEG")
        self.assert_rejected_unchanged(self.sources, "PNG RGB/RGBA")

    def test_invalid_recipes_preserve_entire_set(self):
        for case, invalid in (("missing", None), ("hash", dict(self.provenance["technology"], sourceSha256="invalid")),
                              ("bounds", dict(self.provenance["technology"], crop=dict(left=2, top=0, right=1281, bottom=16)))):
            with self.subTest(recipe_case=case):
                self.provenance["technology"] = invalid
                self.write_recipes()
                self.assert_rejected_unchanged(self.sources, "рецепт")

    def test_incomplete_catalog_preserves_entire_set(self):
        complete = json.dumps(self.catalog)
        for case in ("file", "id", "variants"):
            with self.subTest(missing_field=case):
                self.catalog = json.loads(complete)
                asset = self.catalog["assets"][0]
                del (asset["variants"][0] if case == "file" else asset)[case]
                self.write_recipes()
                self.assert_rejected_unchanged(self.sources, "Неполный каталог")

    def test_preparation_failure_preserves_entire_set(self):
        before = self.snapshot()
        save = generator.png_bytes
        count = 0

        def fail_during_fixture(image):
            nonlocal count
            count += 1
            if count == 4:
                raise OSError("Проверочный отказ подготовки")
            return save(image)

        with patch.object(generator, "png_bytes", side_effect=fail_during_fixture):
            with self.assertRaisesRegex(OSError, "отказ подготовки"):
                generator.prepare(self.sources, self.root)
        self.assertEqual(before, self.snapshot())

    def test_publication_failure_restores_existing_and_removes_new_files(self):
        before = self.snapshot()
        replace = generator.os.replace
        count = 0

        def fail_during_publication(source, target):
            nonlocal count
            count += 1
            if count == 6:
                raise OSError("Проверочный отказ публикации")
            return replace(source, target)

        with patch.object(generator.os, "replace", side_effect=fail_during_publication):
            with self.assertRaisesRegex(OSError, "отказ публикации"):
                generator.prepare(self.sources, self.root)
        self.assertGreater(count, 6, "Должно выполняться восстановление прежних файлов")
        self.assertEqual(before, self.snapshot())
        self.assertEqual([], list(self.root.glob(".game-assets-*")))

    def test_staging_failure_preserves_entire_set(self):
        before = self.snapshot()
        copyfile = generator.shutil.copyfile
        count = 0

        def fail_during_backup(source, target):
            nonlocal count
            count += 1
            if count == 2:
                raise OSError("Проверочный отказ резервного копирования")
            return copyfile(source, target)

        with patch.object(generator.shutil, "copyfile", side_effect=fail_during_backup):
            with self.assertRaisesRegex(OSError, "отказ резервного копирования"):
                generator.prepare(self.sources, self.root)
        self.assertEqual(before, self.snapshot())
        self.assertEqual([], list(self.root.glob(".game-assets-*")))

    def test_failed_rollback_retains_recovery_copies(self):
        before = self.snapshot()
        replace = generator.os.replace
        count = 0

        def fail_publication_and_one_restore(source, target):
            nonlocal count
            count += 1
            if count in (6, 7):
                raise OSError("Проверочная блокировка файла")
            return replace(source, target)

        with patch.object(generator.os, "replace", side_effect=fail_publication_and_one_restore):
            with self.assertRaisesRegex(RuntimeError, "резервные копии"):
                generator.prepare(self.sources, self.root)
        recovery, = self.root.glob(".game-assets-*")
        self.assertEqual(before["tests/fixtures/game/dock-720.png"], (recovery / "old-3").read_bytes())
        self.assertEqual(before["assets/game/catalog.json"], self.catalog_path.read_bytes())

    def test_physical_crops_round_outward_at_1920x1080(self):
        crop = dict(left=3, top=1, right=23, bottom=17)
        recipes = [self.catalog["assets"][0]["variants"][0], self.provenance["depot"],
                   self.provenance["technology"]]
        for index, recipe in enumerate(recipes):
            Image.new("RGB", (1920, 1080), (20 + index * 40, 80, 120)).save(self.sources[index])
            recipe.update(sourceSha256=hashlib.sha256(self.sources[index].read_bytes()).hexdigest(),
                          sourceWidth=1920, sourceHeight=1080, crop=dict(crop))
        self.write_recipes()

        generator.prepare(self.sources, self.root)

        with Image.open(self.root / "assets/game/dock.header.png") as generated_crop:
            self.assertEqual((31, 25), generated_crop.size)
        with Image.open(self.root / "tests/fixtures/game/technology-1080.png") as negative:
            self.assertEqual((1920, 1080), negative.size)
            self.assertEqual((100, 80, 120), negative.getpixel((4, 1)))
            self.assertEqual((100, 80, 120), negative.getpixel((34, 25)))
            self.assertEqual((37, 46, 58), negative.getpixel((3, 1)))
            self.assertEqual((37, 46, 58), negative.getpixel((35, 25)))
            self.assertEqual((37, 46, 58), negative.getpixel((34, 26)))

    def test_complete_sources_produce_consistent_idempotent_set(self):
        sources_before = [path.read_bytes() for path in self.sources]
        generator.prepare(self.sources, self.root)
        first = self.snapshot()
        catalog = json.loads(self.catalog_path.read_text(encoding="utf-8"))
        variant = catalog["assets"][0]["variants"][0]
        crop_path = self.root / "assets/game/dock.header.png"
        self.assertEqual(variant["sha256"], hashlib.sha256(crop_path.read_bytes()).hexdigest())
        with Image.open(crop_path) as crop:
            self.assertEqual((22, 16), crop.size)
            self.assertEqual((20, 80, 120), crop.getpixel((0, 0)))
        for height in (720, 1080, 1440, 2160):
            for state in ("main", "dock", "selection"):
                with Image.open(self.root / f"tests/fixtures/game/{state}-{height}.png") as image:
                    self.assertEqual((height * 16 // 9, height), image.size)
                    self.assertEqual((37, 46, 58), image.getpixel((1000, 600)))
                    self.assertEqual((20, 80, 120) if state != "main" else (37, 46, 58), image.getpixel((12, 8)))
        for name, color in (("depot", (60, 80, 120)), ("technology", (100, 80, 120))):
            with Image.open(self.root / f"tests/fixtures/game/{name}-720.png") as image:
                self.assertEqual((1280, 720), image.size)
                self.assertEqual(color, image.getpixel((3, 2)))
                self.assertEqual((37, 46, 58), image.getpixel((100, 100)))
        generator.prepare(list(reversed(self.sources)), self.root)
        self.assertEqual(first, self.snapshot(), "Повторный запуск должен быть байтово воспроизводимым")
        self.assertEqual(sources_before, [path.read_bytes() for path in self.sources])

    def test_state_background_changes_without_changing_ui_crops(self):
        states = {"light": {"assets": ["dock.header"], "background": [220, 210, 200]},
                  "dark": {"assets": ["dock.header"], "background": [8, 12, 20]}}
        (self.root / "tests/fixtures/game/states.json").write_text(json.dumps(states), encoding="utf-8")
        generator.prepare(self.sources, self.root)
        for state, background in (("light", (220, 210, 200)), ("dark", (8, 12, 20))):
            with Image.open(self.root / f"tests/fixtures/game/{state}-720.png") as image:
                self.assertEqual(background, image.getpixel((1000, 600)))
                self.assertEqual((20, 80, 120), image.getpixel((12, 8)))

    def test_invalid_state_background_never_publishes_partial_output(self):
        for background in ([0, 0], [0, 0, 256], [0, 0, -1], [0, True, 0], "red"):
            with self.subTest(background=background):
                path = self.root / "tests/fixtures/game/states.json"
                path.write_text(json.dumps({"bad": {"assets": [], "background": background}}), encoding="utf-8")
                before = self.snapshot()
                with self.assertRaisesRegex(ValueError, "состав или фон"):
                    generator.prepare(self.sources, self.root)
                self.assertEqual(before, self.snapshot())


if __name__ == "__main__":
    unittest.main(verbosity=2)
