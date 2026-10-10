# .codex/context — маршрутизация agent-critical контекста

`.codex/context/` не является энциклопедией проекта. Здесь хранится только минимальный постоянный контекст,
который помогает агенту не нарушить важный контракт при изменении соответствующей области.

Подробные объяснения, примеры, runtime/acceptance evidence и troubleshooting принадлежат `docs/`.
Процедуры работы агента принадлежат `.agents/skills/`. Фактические значения версий, схем, кодов и
конфигурации должны по возможности принадлежать коду или manifests.

## Правила чтения

- Сначала установи затронутую область по задаче и фактическому diff.
- Читай только строки маршрутизации, относящиеся к этой области, и указанные agent-context файлы.
- Не читай все документы каталога последовательно «для полноты».
- Если context ссылается на подробную документацию, открывай её только когда эти детали нужны задаче.
- При расхождении документации с кодом/tests/manifests сначала установи фактическое состояние, затем
  исправь устаревшую документацию.

## Маршрутизация

| Область | Agent-critical контекст | Подробная документация / source of truth |
| --- | --- | --- |
| Архитектурные boundaries и зависимости | `architecture.md` | `docs/architecture/`, production projects |
| Build/dependency contracts | `build-contracts.md` | manifests (`global.json`, `Directory.*`, `native/**`) |
| Пользовательская конфигурация | `application-configuration.md` | `docs/getting-started.md`, `src/AzurPilot.Core/Configuration/**` |
| Application failures | `application-failures.md` | `src/AzurPilot.Core/Failures/**`, `src/AzurPilot.App/AzurPilotExitCode.cs` |
| Native PNG frame и ownership | `native-frame.md` | `native/include/azurpilot_native_abi.h`, `docs/architecture/native-frame-ownership.md`, native/managed tests |
| Runtime logging и diagnostics | `runtime-diagnostics.md` | `src/AzurPilot.App/**`, `docs/getting-started.md` |
| MuMu lifecycle | `mumu-lifecycle.md` | production code + relevant tests/acceptance |
| Android/ADB и lifecycle Azur Lane Global/EN | `android-game-lifecycle.md` | `docs/architecture/android-game-lifecycle.md`, production code/tests |
| Verification semantics | `verification.md` | `docs/testing/`, tests, CI workflows |
| Язык project-owned текста | `language.md` | этот contract |
| Git/GitHub lifecycle | — | `.agents/skills/azurpilot-git-workflow/` |
| CodeRabbit review cycle | — | `.agents/skills/azurpilot-coderabbit-review/` |
| Размещение и оформление нового знания | — | `.agents/skills/azurpilot-documentation/` |

## Что запрещено складывать сюда

- историю разработки и причины, уже не нужные для принятия решений агентом;
- результаты конкретного локального acceptance-прогона, версии и наблюдения конкретной машины;
- tutorial, troubleshooting и инструкции пользователю;
- длинные примеры, полные stdout/stderr, логи и отчёты;
- полный каталог значений, если у него уже есть machine-readable owner;
- полный пересказ tests/CI;
- повторяемые workflow-процедуры, которым место в skill;
- факты «на будущее» о ещё не существующей capability.

Если новое утверждение не меняет решение агента при правке кода, скорее всего, ему место не в
`.codex/context/`.
