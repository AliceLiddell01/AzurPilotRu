"""Позитивные и отрицательные проверки метаданных и границ ссылок."""

import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

from check_skill_contracts import validate_links, validate_skill, validate_tree


class SkillContractTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.folder = self.root / ".agents/skills/sample-review"
        self.folder.mkdir(parents=True)
        self.path = self.folder / "SKILL.md"

    def write(self, header="name: sample-review\ndescription: Проверяет предметную операцию.", body="# Процедура\nВыполни проверку."):
        self.path.write_text(f"---\n{header}\n---\n{body}\n", encoding="utf-8")

    def errors(self):
        return validate_tree(self.root)

    def test_valid_yaml_forms_and_optional_fields(self):
        for value in ('"Описание: проверка."', "'Проверка ''кавычек''.'", ">-\n  Проверка\n  операции.", "|+\n  Проверка.", ">2-\n  Проверка."):
            with self.subTest(value=value):
                self.write(f"name: sample-review\ndescription: {value}\nmetadata: {{author: агент, version: '1'}}\nlicense: MIT\ncompatibility: Windows x64\nallowed-tools: Read")
                self.assertEqual(self.errors(), [])

    def test_missing_manifest_and_empty_tree(self):
        self.assertIn("SKILL.md", " ".join(self.errors()))
        self.folder.rmdir()
        self.assertIn("Отсутствуют", " ".join(self.errors()))

    def test_name_grammar_and_directory_identity(self):
        for value in ("wrong-name", "UPPER", "a--b", "-name", "name-", "a" * 65, "123", "[sample-review]"):
            with self.subTest(value=value):
                self.write(f"name: {value}\ndescription: Описание.")
                self.assertIn("name", " ".join(self.errors()))

    def test_description_empty_type_and_limit(self):
        for value in ('""', '"   "', "null", "true", "[описание]", "{a: b}", "x" * 1025, ">-", "|-"):
            with self.subTest(value=value):
                self.write(f"name: sample-review\ndescription: {value}")
                self.assertIn("description", " ".join(self.errors()))
        self.write("name: sample-review\ndescription: " + "я" * 1024)
        self.assertEqual(self.errors(), [])

    def test_name_and_compatibility_last_valid_boundary(self):
        long_name = "a" * 64
        renamed = self.folder.with_name(long_name)
        self.folder.rename(renamed)
        self.folder = renamed
        self.path = renamed / "SKILL.md"
        self.write(f"name: {long_name}\ndescription: Описание.\ncompatibility: " + "я" * 500)
        self.assertEqual(self.errors(), [])

    def test_required_fields_and_scalar_mapping_types(self):
        for header in ("name: sample-review", "description: Описание.", "1: value", "null",
                       "name: sample-review\ndescription: Описание.\nmetadata: {1: value}"):
            with self.subTest(header=header):
                self.write(header)
                self.assertTrue(self.errors())

    def test_cli_exit_codes(self):
        command = [sys.executable, "-B", str(Path(__file__).with_name("check_skill_contracts.py")), "--root"]
        self.write()
        valid = subprocess.run([*command, str(self.root)], capture_output=True, encoding="utf-8", errors="replace")
        self.assertEqual(valid.returncode, 0, valid.stderr)
        self.write("name: wrong\ndescription: Описание.")
        invalid = subprocess.run([*command, str(self.root)], capture_output=True, encoding="utf-8", errors="replace")
        self.assertEqual(invalid.returncode, 1, invalid.stderr)
        self.assertIn("name", invalid.stderr)
        absent = subprocess.run([*command, str(self.root / "absent")], capture_output=True, encoding="utf-8", errors="replace")
        self.assertEqual(absent.returncode, 2, absent.stderr)
        self.assertNotIn("Traceback", absent.stderr)

    def test_yaml_syntax_duplicate_unknown_and_types(self):
        for extra in ('description: "незакрыто', "description: [незакрыто", "description: {a: [b}",
                      'description: "a" "b"', 'description: "bad\\q"', "description: >wrong\n  Текст",
                      "description: первая: вторая", "description: !unknown x", "description: Описание.\nname: duplicate",
                      "description: Описание.\nwhenToUse: всегда", "description: Описание.\nmetadata: {a: [b]}",
                      "description: Описание.\nmetadata: {a: 1}", "description: Описание.\nmetadata: []",
                      "description: Описание.\ncompatibility: " + "x" * 501,
                      "description: Описание.\nlicense: 1", "description: Описание.\nallowed-tools: []"):
            with self.subTest(extra=extra):
                self.write("name: sample-review\n" + extra)
                self.assertTrue(self.errors(), extra)

    def test_excessive_yaml_depth_is_rejected(self):
        self.write("name: sample-review\ndescription: Описание.\nmetadata: " + "[" * 30 + "x" + "]" * 30)
        self.assertIn("YAML", " ".join(self.errors()))

    def test_frontmatter_required_and_body_required(self):
        for text in ("# Без frontmatter", "---\nname: sample-review", "---\n[]\n---\nТекст.",
                     "---\nname: sample-review\ndescription: Описание.\n---\n"):
            with self.subTest(text=text):
                self.path.write_text(text, encoding="utf-8")
                self.assertTrue(self.errors())

    def test_local_sibling_and_external_skill_docs_links(self):
        # Не зависит от сегодняшнего имени Harness: любой skill может ссылаться на docs.
        docs = self.root / "docs/testing"
        docs.mkdir(parents=True)
        (docs / "surface.md").write_text("# Проверка ввода\n\n# Проверка ввода\n", encoding="utf-8")
        references = self.folder / "references"
        references.mkdir()
        (references / "details.md").write_text("[Назад](../SKILL.md#процедура)", encoding="utf-8")
        sibling = self.root / ".agents/skills/other-skill"
        sibling.mkdir()
        (sibling / "SKILL.md").write_text("---\nname: other-skill\ndescription: Другая операция.\n---\n# Другой skill\n", encoding="utf-8")
        self.write(body="[Детали](references/details.md)\n[Другой](../other-skill/SKILL.md)\n"
                   "[Приёмка](../../../docs/testing/surface.md#проверка-ввода-1)\n"
                   "[URL](https://example.org/path#x)\n[Почта](mailto:example@example.org)\n"
                   "# Процедура\n[Здесь](#процедура)")
        self.assertEqual(self.errors(), [])

    def test_broken_reference_in_reference_file_is_checked(self):
        self.write()
        references = self.folder / "references"
        references.mkdir()
        (references / "details.md").write_text("[Нет](missing.md)", encoding="utf-8")
        self.assertIn("missing.md", " ".join(self.errors()))

    @unittest.skipUnless(os.name == "nt", "Проверка регистра путей Windows")
    def test_link_with_wrong_case_is_rejected(self):
        docs = self.root / "docs/testing"
        docs.mkdir(parents=True)
        (docs / "verification.md").write_text("# Проверка", encoding="utf-8")
        self.write(body="[Проверка](../../../Docs/testing/verification.md)")
        self.assertIn("регистр пути", " ".join(self.errors()))

    def test_missing_internal_external_and_anchor_links(self):
        for link in ("references/missing.md", "../../../docs/missing.md", "#нет-такого-заголовка",
                     "../../../../outside.md", "file:///C:/secret", "//server/share", "/absolute.md",
                     "../../../.git/config", "../../../.codex/local/private.md", "../../../.env", "https:missing"):
            with self.subTest(link=link):
                self.write(body=f"[Ссылка]({link})")
                self.assertTrue(self.errors(), link)

    def test_reference_style_image_and_escaped_link_destinations(self):
        self.write(body="[Ссылка][owner]\n\n[owner]: ../../../docs/missing.md\n![Изображение](references/missing.png)")
        self.assertEqual(len(self.errors()), 2)
        self.write(body="[Ссылка](../../../%2e%2e/secret.md)")
        self.assertTrue(self.errors())

    def test_code_and_comment_examples_are_not_links(self):
        self.write(body="```md\n[Нет](missing.md)\n```\n<!-- [Нет](missing.md) -->\n`[Нет](missing.md)`")
        self.assertEqual(self.errors(), [])

    def test_symlink_escape_and_manifest_escape(self):
        outside = tempfile.TemporaryDirectory()
        self.addCleanup(outside.cleanup)
        target = Path(outside.name) / "outside.md"
        target.write_text("Внешний файл.", encoding="utf-8")
        link = self.folder / "outside.md"
        try:
            link.symlink_to(target)
        except OSError as error:
            self.skipTest(f"Windows не разрешает symlink: {error}")
        self.write(body="[Ссылка](outside.md)")
        self.assertIn("checkout", " ".join(self.errors()))
        self.path.unlink()
        self.path.symlink_to(target)
        self.assertIn("checkout", " ".join(validate_skill(self.path, self.root)))

    @unittest.skipUnless(os.name == "nt", "Проверка Windows junction")
    def test_windows_junction_escape(self):
        outside = tempfile.TemporaryDirectory()
        self.addCleanup(outside.cleanup)
        (Path(outside.name) / "outside.md").write_text("Внешний файл.", encoding="utf-8")
        link = self.folder / "linked"
        # Создание junction через Windows API оболочки; удаление только самой ссылки.
        environment = dict(os.environ, AZURPILOT_TEST_LINK=str(link), AZURPILOT_TEST_TARGET=outside.name)
        result = subprocess.run(["pwsh", "-NoProfile", "-Command",
                                 "New-Item -ItemType Junction -Path $env:AZURPILOT_TEST_LINK -Target $env:AZURPILOT_TEST_TARGET | Out-Null"],
                                env=environment, capture_output=True, encoding="utf-8", errors="replace")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.addCleanup(link.rmdir)
        self.write(body="[Ссылка](linked/outside.md)")
        self.assertIn("checkout", " ".join(self.errors()))

    def test_repo_owner_boundary_even_for_existing_file(self):
        private = self.root / ".codex/local/private.md"
        private.parent.mkdir(parents=True)
        private.write_text("Закрытые данные.", encoding="utf-8")
        self.write(body="[Ссылка](../../../.codex/local/private.md)")
        self.assertIn("владельца", " ".join(self.errors()))


if __name__ == "__main__":
    unittest.main()
