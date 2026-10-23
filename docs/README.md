# Документация AzurPilotRu

`docs/` — место для развёрнутого знания о проекте: архитектурных объяснений, обоснований, эксплуатации,
verification/acceptance, troubleshooting и других деталей, которые не нужно постоянно подмешивать в
контекст coding-agent.

## Входные точки

- `getting-started.md` — предварительные требования, сборка, запуск и базовая диагностика.
- `architecture/overview.md` — структура проекта, границы и текущее capability-состояние.
- `architecture/native-frame-ownership.md` — декодирование PNG в RGB8-кадр во владении DLL, ABI и время жизни.
- `architecture/mumu-lifecycle.md` — подробная архитектура интеграции MuMu и управления его жизненным циклом.
- `architecture/android-game-lifecycle.md` — готовность Android/ADB и жизненный цикл Azur Lane Global/EN.
- `architecture/screenshot-diagnostics.md` — точное получение PNG через Android, геометрия именованных областей и нативный анализ RGB.
- `architecture/game-ui.md` — игровая геометрия, проверенные ассеты, native matching и безопасный ввод.
- `architecture/operator-tui.md` — устройство интерактивной операторской поверхности AzurPilot.
- `reference/application-configuration.md` — схема и загрузка пользовательской конфигурации.
- `reference/application-failures.md` — модель отказов приложения и их проекция.
- `reference/agent-cli.md` — машинные команды, JSON-контракт, mutation coordination и границы evidence.
- `operations/runtime-diagnostics.md` — composition, logging, correlation и diagnostic snapshot.
- `testing/verification.md` — что доказывают CI, native/managed tests и real acceptance.
- `testing/android-acceptance.md` — подробности real/managed проверки Android/game lifecycle.
- `testing/screenshot-acceptance.md` — локальные образцы PNG и read-only live screenshot acceptance.
- `testing/game-ui.md` — fixture/native/managed проверки и безопасная живая проверка игрового перехода.
- `testing/operator-tui-acceptance.md` — приёмка самого TUI на Windows и live MuMu.
- `testing/tui-harness.md` — постоянный ConPTY/xterm.js CLI, воспроизводимые PNG, ячейки и границы точности воспроизведения.
- `testing/agent-infrastructure.md` — валидаторы Agent Skills/PR body, локальные команды и границы их доказательства.

## Чем `docs/` отличается от контекста агента

- `AGENTS.md` содержит только глобальные правила, нужные почти в любой задаче.
- `.codex/context/` содержит короткие контракты конкретных областей, критически важные для агента.
- `.agents/skills/` содержит повторяемые процедуры работы агента.
- `docs/` содержит объяснение системы для человека и детали, которые агент читает только по необходимости.
- Код, тесты, схемы и манифесты остаются фактическим source of truth для машиночитаемых значений.

Правила выбора владельца и оформления нового знания задаёт skill
`.agents/skills/azurpilot-documentation/`.
