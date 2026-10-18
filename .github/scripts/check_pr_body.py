"""Проверка структуры PR по azurpilot-git-workflow/references/pr-body.md."""

import argparse
import json
import re
import sys
from pathlib import Path

from markdown_it import MarkdownIt

REQUIRED = ("Цель", "Область изменений", "Реализация", "Проверки", "CI", "Готовность", "Ограничения")
REVIEW = "CodeRabbit review и обработка замечаний"
TABLE = ("Итерация / reviewed HEAD", "Уровень", "Путь", "Влияние", "Решение", "Результат")
# Публичные GitHub identity, проверяемые совместно с type и branch policy.
BOTS = {"renovate[bot]": (29139614, "renovate/"), "dependabot[bot]": (49699333, "dependabot/")}
MARKDOWN = MarkdownIt("commonmark").enable("table")
PLACEHOLDER = re.compile(r"(?:todo|tbd|fixme|заполнить|уточнить|описание|текст|[-—.…]+|<[^>]+>)[.!…]*", re.I)


def inline_text(token) -> str:
    return "".join(child.content if child.type in {"text", "code_inline"} else " " if child.type in {"softbreak", "hardbreak"} else ""
                   for child in token.children or [])


def meaningful(value: str) -> bool:
    text = re.sub(r"^(?:[-*+]\s+)?(?:\[[ xX]\]\s+)?", "", value.strip()).strip("`*_ ")
    return bool(text) and not PLACEHOLDER.fullmatch(text)


def sections(body: str) -> list[tuple[str, list]]:
    """Только реальные корневые h2; примеры и HTML comments остаются токенами кода/HTML."""
    result = []
    active = None
    tokens = MARKDOWN.parse(body)
    index = 0
    while index < len(tokens):
        token = tokens[index]
        if token.type == "heading_open" and token.tag == "h2" and token.level == 0:
            active = []
            result.append((inline_text(tokens[index + 1]).strip(), active))
            index += 3
            continue
        if active is not None:
            active.append(token)
        index += 1
    return result


def row_width(line: str) -> int:
    """GFM делит ячейки по неэкранированному pipe, в том числе внутри backticks."""
    parts = []
    current = ""
    escaped = False
    for char in line.strip():
        if char == "|" and not escaped:
            parts.append(current)
            current = ""
        else:
            current += char
        escaped = char == "\\" and not escaped
    parts.append(current)
    if not parts[0].strip():
        parts.pop(0)
    if parts and not parts[-1].strip():
        parts.pop()
    return len(parts)


def review_errors(tokens: list, body: str) -> list[str]:
    tables = []
    current = None
    row = None
    stray = False
    malformed_width = False
    lines = body.splitlines()
    for token in tokens:
        if token.type == "table_open":
            current = []
            tables.append(current)
            start, end = token.map
            malformed_width |= any(row_width(line) != 6 for line in lines[start:end])
        elif token.type == "table_close":
            current = None
        elif current is not None and token.type == "tr_open":
            row = []
            current.append(row)
        elif current is not None and token.type == "inline" and row is not None:
            row.append(inline_text(token).strip())
        elif token.type == "inline" and "|" in token.content:
            stray = True
    if len(tables) != 1 or not tables[0] or tuple(tables[0][0]) != TABLE:
        return ["CodeRabbit: нужна одна накопительная таблица установленной формы"]
    errors = []
    if malformed_width:
        errors.append("CodeRabbit: каждая строка должна иметь ровно шесть ячеек")
    if stray:
        errors.append("CodeRabbit: строка находится вне таблицы; уберите пустые разделители строк")
    if len(tables[0]) < 2:
        errors.append("CodeRabbit: отсутствуют строки завершённой итерации")
    for number, cells in enumerate(tables[0][1:], 1):
        if len(cells) != 6:
            errors.append(f"CodeRabbit: строка {number} должна иметь шесть полей")
            continue
        if not re.fullmatch(r"[1-9][0-9]*\s*·\s*[0-9a-f]{7,40}", cells[0]):
            errors.append(f"CodeRabbit: строка {number} не содержит номер и reviewed HEAD")
        clean = cells[4] == "clean"
        for index, cell in enumerate(cells[1:], 1):
            if clean and index in {1, 2} and cell == "—":
                continue
            if not meaningful(cell):
                errors.append(f"CodeRabbit: пустое поле или заглушка в строке {number}, колонке {index + 1}")
        if cells[4] not in {"confirmed", "partially confirmed", "stale/repeated", "false positive", "clean"}:
            errors.append(f"CodeRabbit: неизвестное решение в строке {number}")
        if clean and cells[1:3] != ["—", "—"]:
            errors.append(f"CodeRabbit: clean-строка {number} требует — в уровне и пути")
    return errors


def validate(body: str) -> list[str]:
    blocks = sections(body)
    names = [name for name, _ in blocks]
    errors = []
    for name in REQUIRED:
        count = names.count(name)
        if count != 1:
            errors.append(f"Обязательный раздел ## {name}: ожидается один, найдено {count}")
    if all(name in names for name in REQUIRED):
        positions = [names.index(name) for name in REQUIRED]
        if positions != sorted(positions):
            errors.append("Нарушен порядок обязательных разделов")
    for name, tokens in blocks:
        if name in REQUIRED:
            contents = [inline_text(t) for t in tokens if t.type == "inline"]
            if not any(meaningful(content) for content in contents):
                errors.append(f"Раздел ## {name} пуст или содержит заглушку")
        if name.startswith("CodeRabbit review") and name != REVIEW:
            errors.append(f"Неверное название раздела CodeRabbit: {name}")
        if name == REVIEW:
            errors.extend(review_errors(tokens, body))
    if names.count(REVIEW) > 1:
        errors.append("Раздел CodeRabbit повторяется")
    return errors


def event_pr(payload: object) -> dict:
    if not isinstance(payload, dict) or not isinstance(payload.get("pull_request"), dict):
        raise ValueError("Нет объекта pull_request")
    pr = payload["pull_request"]
    for key in ("user", "head", "base"):
        if not isinstance(pr.get(key), dict):
            raise ValueError(f"Нет объекта pull_request.{key}")
    if not isinstance(pr.get("body"), (str, type(None))):
        raise ValueError("pull_request.body должен быть строкой или null")
    user = pr["user"]
    if not isinstance(user.get("login"), str) or not isinstance(user.get("type"), str) or type(user.get("id")) is not int:
        raise ValueError("Повреждена identity автора PR")
    if not isinstance(pr["head"].get("ref"), str) or not pr["head"]["ref"]:
        raise ValueError("Нет head.ref")
    return pr


def is_automation_pr(payload: object) -> bool:
    pr = event_pr(payload)
    user = pr["user"]
    identity = BOTS.get(user["login"])
    if identity is None or user["type"] != "Bot" or user["id"] != identity[0]:
        return False
    prefix = identity[1]
    branch = pr["head"]["ref"]
    # Только собственные dependency-ветки репозитория, не fork с похожим именем.
    head_repo, base_repo = pr["head"].get("repo"), pr["base"].get("repo")
    same_repo = (isinstance(head_repo, dict) and isinstance(base_repo, dict)
                 and type(head_repo.get("id")) is int and type(base_repo.get("id")) is int
                 and head_repo["id"] > 0 and head_repo["id"] == base_repo["id"])
    return same_repo and branch.startswith(prefix) and len(branch) > len(prefix)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--body-file", type=Path, help="Подготовленный локальный Markdown")
    source.add_argument("--event-path", type=Path, help="GitHub pull_request event JSON")
    args = parser.parse_args()
    try:
        if args.body_file is not None:
            body = args.body_file.read_text(encoding="utf-8")
        else:
            payload = json.loads(args.event_path.read_text(encoding="utf-8"))
            pr = event_pr(payload)
            if is_automation_pr(payload):
                print("Проверенная dependency bot identity и branch policy: исключение для автоматического PR")
                return 0
            body = pr.get("body") or ""
        errors = validate(body)
    except (OSError, UnicodeError, ValueError) as error:
        print(f"Не удалось проверить PR body: {error}", file=sys.stderr)
        return 2
    for error in errors:
        print(error, file=sys.stderr)
    if not errors:
        print("Структура PR body: PASSED")
    return int(bool(errors))


if __name__ == "__main__":
    sys.exit(main())
