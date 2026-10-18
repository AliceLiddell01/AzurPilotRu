# Проверка агентской инфраструктуры

Процедуры агента находятся в `.agents/skills/`, маршруты — в [AGENTS.md](../../AGENTS.md).
Детерминированные проверки защищают формат и ссылки, но не доказывают исполнение skill моделью.
Контракты product runtime и границы evidence остаются у [verification](verification.md).

## Локальный запуск в Windows

Используется нативный Python через существующий uv. Зависимости только repository tooling закреплены
в [requirements.txt](../../.github/scripts/requirements.txt); приложение не получает Python dependency.
Команды из корня checkout:

```powershell
uv run --python 3.15 --with-requirements .github/scripts/requirements.txt python -B .github/scripts/check_skill_contracts.py --root .
uv run --python 3.15 --with-requirements .github/scripts/requirements.txt python -B -m unittest discover -s .github/scripts -p 'test_check_*.py' -v
uv run --python 3.15 --with-requirements .github/scripts/requirements.txt python -B .github/scripts/check_pr_body.py --body-file .codex/local/pr/<task>/body.md
```

`-B` предотвращает запись `__pycache__` в checkout. Тесты используют изолированный системный TEMP.
`unittest` выполняется последовательно; CLI subprocess и Windows junction проверяются непосредственно.
Symlink-тест при отсутствии соответствующего права Windows явно пропускается; junction-проверка
остаётся обязательной на Windows. Пропуск не выдаётся за успешную symlink-проверку.

## Agent Skills

[check_skill_contracts.py](../../.github/scripts/check_skill_contracts.py) использует безопасный YAML 1.2
parser ruamel.yaml с запретом duplicate keys и ограничением глубины. После разбора проверяются типы,
обязательные/допустимые поля и длины по [Agent Skills specification](https://agentskills.io/specification).
`whenToUse` не является стандартным полем: условия активации принадлежат `description` и маршрутизации.

Markdown-it разбирает inline/reference links и изображения в SKILL.md и его Markdown references.
Примеры внутри кода и HTML comments не считаются ссылками. URL http/https/mailto отделяются от локальных
путей; доступность интернета/удалённой страницы эта deterministic проверка не обещает.
Локальная ссылка может выходить из skill root к подтверждённым владельцам checkout, включая `docs/`.
Разрешённые области задаёт сам checker; `.git`, локальные данные, generated output и неожиданные файлы
не являются допустимыми владельцами. Проверяются существование файла, percent-decoding,
фактический путь через symlink/junction и Markdown heading anchor (включая повторные заголовки).
HTML `id`, HTML-ссылки и произвольные пути внутри inline code не входят в поддерживаемый формат ссылок.

## PR body и CI

[pr-body.md](../../.agents/skills/azurpilot-git-workflow/references/pr-body.md) владеет структурой,
[check_pr_body.py](../../.github/scripts/check_pr_body.py) — её автоматической проверкой.
Разбираются реальные корневые h2, порядок, дубли, пустые разделы/очевидные заглушки и условная таблица
CodeRabbit. Markdown examples, fenced code, HTML comments и blockquotes не подменяют разделы.
Это структурная проверка: она не устанавливает достоверность заявленных тестов или reviewed HEAD.

Dependency PR освобождается только при совместной проверке публичных login/id/type GitHub-бота,
соответствующего непустого branch prefix и равенства head/base repository identity. Имя ветки человека
исключения не даёт. Неизвестная automation проходит обычный контракт. Event без корректного объекта
PR/identity/body отклоняется; текст читается из JSON-файла и не исполняется shell/eval.

[CI](../../.github/workflows/ci.yml) запускает метаданные/ссылки и regression tests на Windows для PR,
push в main и ручного запуска, сохраняя отдельную native/managed verification.
[PR-body workflow](../../.github/workflows/pr-body.yml) проверяет event JSON также при `edited`, поэтому
исправление одного body запускает проверку. Permissions — только `contents: read`, без secrets и
`pull_request_target`; checkout не сохраняет credentials. Тесты валидаторов не дублируются в PR-body job.
Required check настраивается через branch protection/ruleset отдельно; наличие workflow само по себе
не делает его обязательным для merge.
