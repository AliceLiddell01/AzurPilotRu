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
4. Для изменения поведения прочитай `.codex/context/verification.md` и относящиеся к подсистеме
   контракты.
5. Если нужны объяснение архитектуры, примеры, acceptance evidence или troubleshooting — переходи в `docs/`.
6. Не загружай весь `.codex/context/` или `docs/` «на всякий случай».

Код, schema, manifests, tests и runtime evidence имеют приоритет как фактические источники состояния.
Документация не должна подменять machine-readable source of truth.

## Маршрутизация процедур

Перед каждым соответствующим этапом сопоставь текущую задачу и её scope с этой таблицей и
`description` применимых project skills. При совпадении прочитай их `SKILL.md` до начала этапа,
следуй его контракту, а нужные references прочитай перед соответствующими шагами. Поле `whenToUse`
само по себе не гарантирует автоматическое применение skill. При изменении scope повтори
маршрутизацию и выбор agent-context для новой capability. Если нужный файл недоступен, обозначь
блокер и не подменяй его workflow.

| Задача | Владелец |
| --- | --- |
| Карта agent-context и владельцев | `.codex/context/INDEX.md` |
| Человеческая документация проекта | `docs/README.md` |
| Делегирование и координация субагентов | `.agents/skills/azurpilot-agent-coordination/SKILL.md` |
| Git/branch/commit/push/PR/Draft/merge lifecycle | `.agents/skills/azurpilot-git-workflow/SKILL.md` |
| Явно запрошенный CodeRabbit review cycle | `.agents/skills/azurpilot-coderabbit-review/SKILL.md` |
| Классификация и оформление новых правил/документации | `.agents/skills/azurpilot-documentation/SKILL.md` |

## Изменение постоянных знаний

Не добавляй новое знание автоматически в `AGENTS.md` или `.codex/context/`.
Сначала классифицируй его через `azurpilot-documentation`: глобальный invariant, agent-critical contract,
человеческая документация, повторяемая процедура либо machine-readable факт должны иметь разных владельцев.
Не дублируй одно правило между владельцами; в остальных местах ставь ссылку.

## Критерии и evidence

До реализации установи критерии результата, после неё сопоставь их с итоговым diff и фактически
выполненными проверками. Для изменений поведения применяй владельцев и границы доказательства из
`.codex/context/verification.md`; не выдавай непроверенное за проверенное. В итогах указывай реальные
проверки, ограничения, применённые skills, фактически задействованных субагентов и состояние Git/PR.
