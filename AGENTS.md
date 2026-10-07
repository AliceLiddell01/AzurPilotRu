# AGENTS.md — минимальный контракт репозитория

AzurPilotRu — Windows-only x64 приложение: managed часть на .NET/C#, отдельная native boundary на C++/OpenCV.
Этот файл намеренно короткий: он содержит только правила, которые должны быть видимы почти в любой задаче.
Подробная документация находится в `docs/`, task-specific агентские ограничения — в `.codex/context/`,
повторяемые процедуры — в `.agents/skills/`.

## Глобальные инварианты

- Managed boundaries приложения — `AzurPilot.Core`, `AzurPilot.Windows`, `AzurPilot.App`; native boundary
  отдельна. Проекты `tests/**` — инструменты проверки, а не product boundaries.
- Направление managed-зависимостей: `AzurPilot.App` → `AzurPilot.Windows` → `AzurPilot.Core`.
- Machine-specific абсолютные пути и значения конкретной рабочей машины в committed source/config
  запрещены.
- Project-owned комментарии, диагностика и документация пишутся по-русски; точные идентификаторы,
  API/CLI names и общепринятые технические термины сохраняются.
- Не создавай placeholder-abstractions, конфигурацию, коды отказов или документы под capability,
  которой в продукте ещё нет.
- Одна самостоятельная публикуемая задача — одна рабочая ветка и один PR. Merge выполняется только по
  отдельной текущей явной команде пользователя.
- CodeRabbit запускается только по явному положительному запросу пользователя.

## Как получать контекст

1. Определи затронутую capability по фактическому коду/diff.
2. Открой `.codex/context/INDEX.md`.
3. Прочитай только указанные там agent-context документы, относящиеся к этой задаче.
4. Если нужны объяснение архитектуры, примеры, acceptance evidence или troubleshooting — переходи в `docs/`.
5. Не загружай весь `.codex/context/` или `docs/` «на всякий случай».

Код, schema, manifests, tests и runtime evidence имеют приоритет как фактические источники состояния.
Документация не должна подменять machine-readable source of truth.

## Маршрутизация процедур

| Задача | Владелец |
| --- | --- |
| Карта agent-context и владельцев | `.codex/context/INDEX.md` |
| Человеческая документация проекта | `docs/README.md` |
| Git/branch/commit/push/PR/Draft/merge lifecycle | `.agents/skills/azurpilot-git-workflow/` |
| Явно запрошенный CodeRabbit review cycle | `.agents/skills/azurpilot-coderabbit-review/` |
| Классификация и оформление новых правил/документации | `.agents/skills/azurpilot-documentation/` |

## Изменение постоянных знаний

Не добавляй новое знание автоматически в `AGENTS.md` или `.codex/context/`.
Сначала классифицируй его через `azurpilot-documentation`: глобальный invariant, agent-critical contract,
человеческая документация, повторяемая процедура либо machine-readable факт должны иметь разных владельцев.
Не дублируй одно правило между владельцами; в остальных местах ставь ссылку.
