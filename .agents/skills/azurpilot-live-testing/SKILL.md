---
name: azurpilot-live-testing
description: >-
  Реальная смок- и live-приёмка AzurPilotRu через production AzurPilot.App.exe agent:
  discovery, runtime diagnostics, MuMu/Android/game lifecycle, screenshot, recognition и
  разрешённый безопасный input. Применяй к просьбам «проверь реальное управление Android
  через agent CLI», runtime-приёмке и воспроизведению в фактической среде. Live-приёмка PR
  выполняется с Debug Mode (`--debug-report`). Mutation требует отдельной текущей авторизации
  exact identity и операции. Визуальный TUI проверяет tui-harness.
---

# Приёмка через production Agent CLI

Исполняй настоящий `AzurPilot.App.exe agent ...`. Skill владеет процедурой приёмки, а не
`AgentCommands`, `AgentRequest`, `AgentJson`, `AzurPilotOperatorService`,
`WindowsInstanceMutationLease`, native ABI или AndroidAcceptance. Каталог команд и mutation flags
получай из `agent discover`, затем сверяй с кодом и [Agent CLI](../../../docs/reference/agent-cli.md).
Не закрепляй сегодняшний список команд как второй вечный каталог.

1. Установи checkout HEAD/dirty diff, исходную сборку, команды её получения, фактические пути
   EXE/DLL и хеши. При сомнительном происхождении пересобери штатно по
   [getting-started](../../../docs/getting-started.md). Текст HEAD рядом со старым EXE не доказывает
   проверку текущего кода. Выполняй команды в текущей Windows x64/PowerShell среде через pipes,
   с раздельными raw stdout/stderr; TTY для Agent CLI не нужен.
2. До запуска составь для **каждой существенно изменённой runtime capability** обязательный сценарий:
   production path, входные условия, expected result из контракта, независимое свидетельство исполнения,
   negative/failure path, допустимый откат и проверку конечного состояния. Читай
   [процедуру](references/procedure.md) и только применимые [поверхности](references/surfaces.md).
3. Выполни read-only preflight, установи exact identity через inventory/resolve. Не угадывай `mumu:<index>`,
   endpoint или первый instance; во всех последующих target-вызовах передавай подтверждённую identity.
   Read-only отказ не даёт права запускать preparation или выбирать другой target.
4. Mutation выполняй лишь после отдельной текущей авторизации пользователя: exact identity,
   операция/границы и разрешённое восстановление. После preflight **перед mutation повторно подтверди
   с пользователем** актуальность этого разрешения для конкретного действия. При его отсутствии
   не запускай mutation, укажи NOT_RUN/BLOCKED. TUI confirmation/modes и узкое Harness live-разрешение
   не авторизуют прямую Agent CLI-приёмку. Не обходи lease, timeouts или guards ради зелёного результата.
5. Live-приёмка PR вызывает **каждую** команду с `--debug-report pr-<n>-<slug>` (до номера PR —
   тематическая сессия), кроме контрольных прогонов без флага для замера накладных расходов
   и проверки отсутствия отчёта, и проверяет полноту отчёта по [процедуре](references/procedure.md#debug-отчёт-pr-приёмки).
   Повседневные и диагностические вызовы вне PR-приёмки идут без флага. Debug Mode не расширяет
   разрешения на mutation и не заменяет независимые postconditions.
6. Автоматизируй реальные вызовы, ограничения времени, проверку JSON/exit/status/effect,
   независимые postconditions и разрешённый cleanup. Помощник только управляет, наблюдает и сравнивает:
   fake JSON emitter, mock MuMu/ADB, собственная orchestration и запись ожидаемого state запрещены
   как live evidence. Doubles допустимы отдельно в unit/contract tests.
7. При первом отказе останови сценарий, сохрани bounded evidence и проверяй фактическое состояние.
   Отмена/deadline не означают rollback; `Unknown`, nullable facts и `mayHaveChanged` не равны `none`.
   Cleanup разрешён лишь для своего доказанного эффекта, на том же target и в согласованных границах.
   Не делай слепой повтор tap или compensating stop. Не исключай упавший сценарий из плана.
   Для игровых capabilities соблюдай раздел «Игровые capabilities» [процедуры](references/procedure.md):
   один production-триггер, телеметрия из envelope и итог «игра Stopped, MuMu Running».
8. Сверь до/после и дай отдельный `PASS / FAIL / PARTIAL / NOT_RUN / BLOCKED` каждому обязательному
   сценарию по [процедуре](references/procedure.md), отделив deterministic tests, CI и реальные
   Windows/MuMu/Android свидетельства. Общий PASS допустим только при PASS всех обязательных сценариев.

## Артефакты и другие владельцы

Разовые помощники разработки/приёмки могут лежать в `.codex/local/scripts/<task>/`, отчёты —
в `.codex/local/reports/<task>/` (туда же пишет Debug Mode): до записи проверь `git check-ignore` и `git ls-files`, после —
`git status`. Соблюдай более узкие ограничения владельцев: игровые исходные изображения и их временные
производные остаются в системном TEMP по [game-ui acceptance](../../../docs/testing/game-ui.md).
Не повреждай runtime-конфиги, ассеты, снимки и чужие артефакты; удаляй только свои файлы/ресурсы.
Разовые скрипты, журналы и пользовательские данные в PR не попадают.

Визуальные цвета, ввод, навигация/resize, ConPTY/xterm.js и physical Windows Terminal принадлежат
[tui-harness](../azurpilot-tui-harness/SKILL.md). Backend-only изменение его не требует. Если затронуты
обе поверхности, нужны две отдельные группы evidence. Прямая Agent CLI-приёмка не заменяет TUI.
Штатные MuMuAcceptance/AndroidAcceptance, managed/interop и CTest сохраняют своих владельцев и
не заменяются вторым framework. Независимое read-only ревью использует свой системный TEMP по
[code-review](../azurpilot-code-review/SKILL.md); публикация — [git-workflow](../azurpilot-git-workflow/SKILL.md).
