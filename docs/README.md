# Документация AzurPilotRu

`docs/` — место для развёрнутого знания о проекте: архитектурных объяснений, rationale, эксплуатации,
verification/acceptance, troubleshooting и других деталей, которые не нужно постоянно подмешивать в
контекст coding-agent.

## Входные точки

- `getting-started.md` — prerequisites, сборка, запуск и базовая диагностика.
- `architecture/overview.md` — структура проекта, boundaries и текущее capability-состояние.
- `architecture/native-frame-ownership.md` — PNG decode в native-owned RGB8 frame, ABI и lifetime.
- `architecture/mumu-lifecycle.md` — подробная архитектура MuMu integration/lifecycle.
- `architecture/android-game-lifecycle.md` — Android/ADB readiness и lifecycle Azur Lane Global/EN.
- `reference/application-configuration.md` — схема и загрузка пользовательской конфигурации.
- `reference/application-failures.md` — модель application failures и их проекция.
- `operations/runtime-diagnostics.md` — composition, logging, correlation и diagnostic snapshot.
- `testing/verification.md` — что доказывают CI, native/managed tests и real acceptance.
- `testing/android-acceptance.md` — подробности real/managed проверки Android/game lifecycle.

## Чем `docs/` отличается от agent context

- `AGENTS.md` содержит только глобальные правила, нужные почти в любой задаче.
- `.codex/context/` содержит короткие agent-critical contracts конкретных областей.
- `.agents/skills/` содержит повторяемые процедуры работы агента.
- `docs/` содержит объяснение системы для человека и детали, которые агент читает только по необходимости.
- Код, tests, schemas и manifests остаются фактическим source of truth для machine-readable значений.

Правила выбора владельца и оформления нового знания задаёт skill
`.agents/skills/azurpilot-documentation/`.
