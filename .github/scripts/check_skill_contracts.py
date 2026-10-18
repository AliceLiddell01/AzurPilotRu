"""Проверка метаданных Agent Skills и Markdown-ссылок внутри checkout."""

import argparse
import os
import re
import sys
import unicodedata
from pathlib import Path
from urllib.parse import unquote, urlsplit

from markdown_it import MarkdownIt
from ruamel.yaml import YAML, YAMLError

FIELDS = {"name", "description", "license", "compatibility", "metadata", "allowed-tools"}
NAME = re.compile(r"[a-z0-9]+(?:-[a-z0-9]+)*\Z")
MARKDOWN = MarkdownIt("commonmark")
# Только разбор, без рендера: опасные схемы URL должны попасть в проверку, а не исчезнуть из tokens.
MARKDOWN.validateLink = lambda target: True
# Ссылки ведут к постоянным владельцам, а не к локальным данным или Git metadata.
OWNER_ROOTS = (".agents/skills", ".codex/context", ".github", "docs", "src", "tests", "tools", "native", "assets")
ROOT_FILES = {"AGENTS.md", "README.md", "Directory.Build.props", "Directory.Build.targets",
              "Directory.Packages.props", "global.json", "AzurPilot.slnx", ".editorconfig"}


def owner_path(path: Path, root: Path) -> bool:
    relative = path.relative_to(root)
    if any(part in {".git", "bin", "obj", "__pycache__"} for part in relative.parts):
        return False
    return relative.as_posix() in ROOT_FILES or any(
        relative.is_relative_to(Path(owner)) for owner in OWNER_ROOTS
    )


def exact_case(path: Path, root: Path) -> bool:
    """Проверяет регистр каждого компонента пути по записям каталогов checkout."""
    try:
        current = root
        for part in path.relative_to(root).parts:
            if part not in os.listdir(current):
                return False
            current /= part
    except (OSError, ValueError):
        return False
    return True


def anchors(source: str) -> set[str]:
    """Anchors заголовков Markdown по правилам GitHub; HTML id не поддерживаются."""
    result = set()
    counts = {}
    tokens = MARKDOWN.parse(source)
    for index, token in enumerate(tokens):
        if token.type != "heading_open":
            continue
        children = tokens[index + 1].children or []
        text = "".join(child.content for child in children if child.type in {"text", "code_inline"})
        slug = "".join(c for c in text.lower() if c in "_- " or unicodedata.category(c)[0] in "LN").replace(" ", "-")
        count = counts.get(slug, 0)
        counts[slug] = count + 1
        result.add(slug if count == 0 else f"{slug}-{count}")
    return result


def validate_links(path: Path, root: Path, source: str | None = None) -> list[str]:
    errors = []
    root = root.resolve(strict=True)
    source = path.read_text(encoding="utf-8") if source is None else source
    for block in MARKDOWN.parse(source):
        for token in block.children or []:
            target = token.attrGet("href") if token.type == "link_open" else token.attrGet("src") if token.type == "image" else None
            if target is None:
                continue
            try:
                url = urlsplit(target)
                if url.scheme in {"http", "https"}:
                    if not url.hostname:
                        raise ValueError("URL не содержит host")
                    continue
                if url.scheme == "mailto" and url.path:
                    continue
                if url.scheme or url.netloc or url.query:
                    raise ValueError("недопустимая схема, сетевой путь или query")
                value = unquote(url.path)
                if "\\" in value or ":" in value or value.startswith("/"):
                    raise ValueError("ожидается относительный путь")
                lexical = Path(os.path.abspath(path.parent / value)) if value else path.absolute()
                resolved = lexical.resolve(strict=True)
                if not lexical.is_relative_to(root) or not resolved.is_relative_to(root):
                    raise ValueError("выход за checkout, включая symlink/junction")
                if not exact_case(lexical, root):
                    raise ValueError("регистр пути не совпадает с записью каталога checkout")
                if not owner_path(lexical, root) or not owner_path(resolved, root) or not resolved.is_file():
                    raise ValueError("ссылка не на файл постоянного владельца")
                if url.fragment and resolved.suffix.lower() == ".md":
                    if unquote(url.fragment) not in anchors(resolved.read_text(encoding="utf-8")):
                        raise ValueError("несуществующий Markdown anchor")
            except (OSError, ValueError, RuntimeError) as error:
                errors.append(f"{path}: неверная ссылка {target!r}: {error}")
    return errors


def validate_skill(path: Path, root: Path) -> list[str]:
    try:
        root = root.resolve(strict=True)
        resolved = path.resolve(strict=True)
        if not resolved.is_relative_to(root) or not owner_path(resolved, root):
            return [f"{path}: SKILL.md выходит за разрешённую границу checkout"]
        source = path.read_text(encoding="utf-8")
        lines = source.splitlines()
        if not lines or lines[0] != "---" or "---" not in lines[1:]:
            return [f"{path}: отсутствует YAML frontmatter с границами ---"]
        end = lines.index("---", 1)
        yaml = YAML(typ="safe", pure=True)
        yaml.allow_duplicate_keys = False
        yaml.max_depth = 20
        header = yaml.load("\n".join(lines[1:end]))
        if not isinstance(header, dict) or not all(isinstance(k, str) for k in header):
            return [f"{path}: frontmatter должен быть mapping со строковыми ключами"]
        errors = []
        for key in header.keys() - FIELDS:
            errors.append(f"{path}: недопустимое поле {key}; активация принадлежит description")
        name = header.get("name")
        if not isinstance(name, str) or len(name) > 64 or not NAME.fullmatch(name) or name != path.parent.name:
            errors.append(f"{path}: name должен совпадать с каталогом и соответствовать спецификации")
        for key, limit in (("description", 1024), ("compatibility", 500)):
            if key == "compatibility" and key not in header:
                continue
            value = header.get(key)
            if not isinstance(value, str) or not value.strip() or len(value) > limit:
                errors.append(f"{path}: {key} должен содержать 1–{limit} символов")
        for key in ("license", "allowed-tools"):
            if key in header and (not isinstance(header[key], str) or not header[key].strip()):
                errors.append(f"{path}: {key} должен быть непустой строкой")
        if "metadata" in header:
            metadata = header["metadata"]
            if not isinstance(metadata, dict) or not all(isinstance(k, str) and isinstance(v, str) for k, v in metadata.items()):
                errors.append(f"{path}: metadata должен быть mapping строк в строки")
        if not "\n".join(lines[end + 1:]).strip():
            errors.append(f"{path}: отсутствует Markdown-инструкция")
        return errors + validate_links(path, root, "\n".join(lines[end + 1:]))
    except (OSError, UnicodeError, ValueError, RuntimeError, YAMLError) as error:
        return [f"{path}: ошибка чтения/YAML: {error}"]


def validate_tree(root: Path) -> list[str]:
    root = root.resolve(strict=True)
    base = root / ".agents/skills"
    directories = sorted(path for path in base.iterdir() if path.is_dir()) if base.is_dir() else []
    if not directories:
        return ["Отсутствуют каталоги .agents/skills/"]
    errors = []
    for directory in directories:
        skill = directory / "SKILL.md"
        if not skill.is_file():
            errors.append(f"{directory}: отсутствует SKILL.md")
            continue
        errors.extend(validate_skill(skill, root))
        if not directory.resolve().is_relative_to(root):
            continue
        for reference in sorted(directory.rglob("*.md")):
            if reference == skill:
                continue
            try:
                resolved = reference.resolve(strict=True)
                if not resolved.is_relative_to(root) or not owner_path(resolved, root):
                    raise ValueError("reference выходит за разрешённую границу checkout")
                errors.extend(validate_links(reference, root))
            except (OSError, UnicodeError, ValueError, RuntimeError) as error:
                errors.append(f"{reference}: {error}")
    return errors


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    args = parser.parse_args()
    try:
        errors = validate_tree(args.root)
    except (OSError, ValueError, RuntimeError) as error:
        print(f"Не удалось проверить checkout: {error}", file=sys.stderr)
        return 2
    for error in errors:
        print(error, file=sys.stderr)
    if not errors:
        print("Метаданные и ссылки Agent Skills: PASSED")
    return int(bool(errors))


if __name__ == "__main__":
    sys.exit(main())
