# Документация AzurPilotRu

`docs/` — место для развёрнутого знания о проекте: архитектурных объяснений, rationale, эксплуатации,
verification/acceptance, troubleshooting и других деталей, которые не нужно постоянно подмешивать в
контекст coding-agent.

## Входные точки

- `getting-started.md` — prerequisites, сборка, запуск и базовая диагностика.
- `architecture/overview.md` — структура проекта, boundaries и текущее capability-состояние.
- `architecture/native-frame-ownership.md` — декодирование PNG в RGB8-кадр во владении DLL, ABI и время жизни.
- `architecture/mumu-lifecycle.md` — подробная архитектура MuMu integration/lifecycle.
- `architecture/android-game-lifecycle.md` — Android/ADB readiness и lifecycle Azur Lane Global/EN.
- `architecture/screenshot-diagnostics.md` — точный Android PNG capture, geometry named regions и native RGB scan.
- `architecture/game-ui.md` — игровая геометрия, проверенные ассеты, native matching и безопасный ввод.
- `architecture/operator-tui.md` — устройство интерактивной операторской поверхности AzurPilot.
- `reference/application-configuration.md` — схема и загрузка пользовательской конфигурации.
- `reference/application-failures.md` — модель application failures и их проекция.
- `reference/agent-cli.md` — машинные команды, JSON-контракт, mutation coordination и границы evidence.
- `operations/runtime-diagnostics.md` — composition, logging, correlation и diagnostic snapshot.
- `testing/verification.md` — что доказывают CI, native/managed tests и real acceptance.
- `testing/android-acceptance.md` — подробности real/managed проверки Android/game lifecycle.
- `testing/screenshot-acceptance.md` — локальные PNG samples и read-only live screenshot acceptance.
- `testing/game-ui.md` — fixture/native/managed проверки и безопасная живая проверка игрового перехода.
- `testing/operator-tui-acceptance.md` — приёмка самого TUI на Windows и live MuMu.
- `testing/tui-harness.md` — постоянный ConPTY/xterm.js CLI, воспроизводимые PNG, ячейки и границы точности воспроизведения.
- `testing/agent-infrastructure.md` — валидаторы Agent Skills/PR body, локальные команды и границы их доказательства.

## Чем `docs/` отличается от agent context

- `AGENTS.md` содержит только глобальные правила, нужные почти в любой задаче.
- `.codex/context/` содержит короткие agent-critical contracts конкретных областей.
- `.agents/skills/` содержит повторяемые процедуры работы агента.
- `docs/` содержит объяснение системы для человека и детали, которые агент читает только по необходимости.
- Код, tests, schemas и manifests остаются фактическим source of truth для machine-readable значений.

Правила выбора владельца и оформления нового знания задаёт skill
`.agents/skills/azurpilot-documentation/`.
