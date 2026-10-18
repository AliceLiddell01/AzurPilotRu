"""Проверки содержательной структуры PR, таблицы ревью и event boundary."""

import copy
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

from check_pr_body import is_automation_pr, validate

HEADINGS = ("Цель", "Область изменений", "Реализация", "Проверки", "CI", "Готовность", "Ограничения")
CONTENTS = (
    "Разделить независимое ревью и приёмку Agent CLI, автоматизировать проверку контрактов.",
    "Изменены project skills и repository tooling; production boundaries сохранены.",
    "Добавлены YAML/Markdown validators с отрицательными сценариями и Windows CI.",
    "PASSED: deterministic проверки валидаторов и внутрипроектных ссылок.",
    "Проверки текущего HEAD ожидают запуска GitHub Actions.",
    "Реализация подготовлена; PR остаётся Draft, merge не выполнялся.",
    "Real-device mutation не выполнялась без отдельного разрешения exact target.",
)
TABLE = ("\n\n## CodeRabbit review и обработка замечаний\n"
         "| Итерация / reviewed HEAD | Уровень | Путь | Влияние | Решение | Результат |\n"
         "| --- | --- | --- | --- | --- | --- |\n")
REVIEWED_HEAD = "abcdef0123456789abcdef0123456789abcdef01"
ROW = f"| 1 · `{REVIEWED_HEAD}` | major | src/file.cs | Потеря partial effects | confirmed | Исправлено в `bcdef01` |"


def valid_body():
    return "\n\n".join(f"## {heading}\n\n{content}" for heading, content in zip(HEADINGS, CONTENTS))


def event(login="renovate[bot]", identity=29139614, branch="renovate/dotnet-monorepo"):
    return {"action": "edited", "pull_request": {"body": None,
            "user": {"login": login, "id": identity, "type": "Bot"},
            "head": {"ref": branch, "repo": {"id": 123}}, "base": {"repo": {"id": 123}}}}


class PrBodyContractTests(unittest.TestCase):
    def test_realistic_azurpilot_body_and_atx_trailing_hashes(self):
        self.assertEqual(validate(valid_body()), [])
        self.assertEqual(validate(valid_body().replace("## Цель", "## Цель ###")), [])

    def test_missing_duplicate_reordered_and_wrong_level(self):
        for body in (valid_body().replace("## CI", "### CI"), valid_body() + "\n\n## Цель\nПовтор.",
                     valid_body().replace("## Цель", "## CI").replace("## CI\n\nПроверки", "## Цель\n\nПроверки")):
            with self.subTest(body=body):
                self.assertTrue(validate(body))
        parts = valid_body().split("\n\n## ")
        self.assertTrue(validate("\n\n## ".join([parts[0], parts[2], parts[1], *parts[3:]])))

    def test_empty_placeholder_and_comment_only_sections(self):
        for replacement in ("", "TODO", "**TBD**", "...", "<!-- Скрытая информация -->", "```text\nПример.\n```", "<описание>", "- TODO\n- TBD"):
            with self.subTest(replacement=replacement):
                self.assertTrue(validate(valid_body().replace(CONTENTS[4], replacement)))

    def test_fence_markers_lengths_and_info_strings(self):
        for opening, closing in (("```md", "```"), ("~~~markdown", "~~~~"), ("````md\n```", "````"),
                                 ("```text\n```python", "```"), ("~~~text\n```", "~~~")):
            with self.subTest(opening=opening):
                errors = validate(f"{opening}\n{valid_body()}\n{closing}")
                self.assertEqual(len(errors), 7)
        self.assertEqual(validate("````md\n## Пример\n```\n````\n" + valid_body()), [])

    def test_html_comments_fences_and_inline_comment_interaction(self):
        for body in (f"<!--\n{valid_body()}\n-->", f"<!--\n```md\n{valid_body()}\n```\n-->",
                     "<!-- Незакрытый комментарий\n" + valid_body()):
            with self.subTest(body=body):
                self.assertEqual(len(validate(body)), 7)
        self.assertEqual(validate("```text\n<!--\n```\n" + valid_body()), [])
        self.assertEqual(validate(valid_body().replace(CONTENTS[4], "<!-- Комментарий -->\nРеальный CI ожидается.")), [])

    def test_blockquote_and_indented_code_headings_do_not_count(self):
        self.assertEqual(len(validate("\n".join("> " + line for line in valid_body().splitlines()))), 7)
        self.assertEqual(len(validate("\n".join("    " + line for line in valid_body().splitlines()))), 7)

    def test_optional_review_valid_finding_and_clean_rows(self):
        self.assertEqual(validate(valid_body() + TABLE + ROW), [])
        clean = "| 1 · abcdef0 | — | — | Ноль findings | clean | Marker commit `bcdef01` опубликован |"
        self.assertEqual(validate(valid_body() + TABLE + clean), [])
        self.assertEqual(validate(valid_body()), [])

    def test_review_wrong_name_missing_table_sha_and_duplicate(self):
        for suffix in (TABLE.replace("обработка замечаний", "disposition") + ROW, TABLE,
                       TABLE + ROW.replace(f"1 · `{REVIEWED_HEAD}`", "1"),
                       TABLE + ROW + TABLE + ROW, TABLE.replace("Результат", "Исправление") + ROW,
                       "\n\n## CodeRabbit review и обработка замечаний\n```md\n" + TABLE + ROW + "\n```"):
            with self.subTest(suffix=suffix):
                self.assertTrue(validate(valid_body() + suffix))

    def test_every_review_row_and_cell_are_validated(self):
        cells = ROW.strip("|").split("|")
        for index in range(6):
            for placeholder in ("", "TODO", "—"):
                with self.subTest(index=index, placeholder=placeholder):
                    broken = cells.copy()
                    broken[index] = placeholder
                    self.assertTrue(validate(valid_body() + TABLE + ROW + "\n|" + "|".join(broken) + "|"))
        self.assertTrue(validate(valid_body() + TABLE + ROW + "\n\n" + ROW))

    def test_review_rejects_extra_or_missing_cells_but_allows_escaped_pipe(self):
        self.assertTrue(validate(valid_body() + TABLE + ROW + " лишнее |"))
        self.assertTrue(validate(valid_body() + TABLE + ROW.rsplit("|", 2)[0] + "|"))
        self.assertEqual(validate(valid_body() + TABLE + ROW.replace("partial effects", "partial \\| effects")), [])

    def test_bot_exception_requires_exact_identity_branch_and_repo(self):
        self.assertTrue(is_automation_pr(event()))
        self.assertTrue(is_automation_pr(event("dependabot[bot]", 49699333, "dependabot/nuget/library")))
        variants = []
        for field, value in (("login", "human"), ("type", "User"), ("id", 1)):
            payload = event()
            payload["pull_request"]["user"][field] = value
            variants.append(payload)
        for branch in ("feat/dependency", "renovate/", "Renovate/dotnet"):
            variants.append(event(branch=branch))
        foreign = event()
        foreign["pull_request"]["head"]["repo"]["id"] = 999
        variants.append(foreign)
        for payload in variants:
            with self.subTest(payload=payload):
                self.assertFalse(is_automation_pr(payload))

    def run_cli(self, option, content):
        with tempfile.TemporaryDirectory() as tmp:
            source = Path(tmp) / "input"
            source.write_text(content, encoding="utf-8")
            return subprocess.run([sys.executable, "-B", str(Path(__file__).with_name("check_pr_body.py")), option, str(source)],
                                  capture_output=True, encoding="utf-8", errors="replace")

    def test_cli_body_file_and_real_event_paths(self):
        self.assertEqual(self.run_cli("--body-file", valid_body()).returncode, 0)
        payload = event("human", 1, "infra/skills")
        payload["pull_request"]["user"]["type"] = "User"
        payload["pull_request"]["body"] = valid_body()
        self.assertEqual(self.run_cli("--event-path", json.dumps(payload)).returncode, 0)
        self.assertEqual(self.run_cli("--event-path", json.dumps(event())).returncode, 0)
        forged = event("human", 1)
        self.assertEqual(self.run_cli("--event-path", json.dumps(forged)).returncode, 1)

    def test_cli_malformed_event_fails_without_traceback(self):
        malformed = ["{", "[]", "null", "{}", '{"pull_request": []}']
        for field, value in (("user", []), ("head", None), ("body", {})):
            payload = copy.deepcopy(event())
            payload["pull_request"][field] = value
            malformed.append(json.dumps(payload))
        for content in malformed:
            with self.subTest(content=content):
                result = self.run_cli("--event-path", content)
                self.assertEqual(result.returncode, 2, result.stderr)
                self.assertNotIn("Traceback", result.stderr)


if __name__ == "__main__":
    unittest.main()
