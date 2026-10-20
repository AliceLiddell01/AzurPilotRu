# Agent CLI — agent-critical контракт

- Явный namespace `agent` маршрутизируется до legacy parser и TTY; все машинные отказы сохраняют один
  JSON. Единственные владельцы каталога, parser и envelope — `src/AzurPilot.App/Agent/`.
- CLI вызывает общий `AzurPilotOperatorService`; endpoint, readiness, lifecycle и screenshot
  orchestration не переносятся в presentation. TUI permissions/confirmation не авторизуют CLI.
- Межпроцессная аренда exact identity принадлежит operator boundary и покрывает целый сценарий обеих
  поверхностей. Вложенные core методы работают под уже занятой арендой; повторный захват запрещён.
- Не выводи `none`/rollback из отмены и не теряй известный частичный эффект, nullable facts или `Unknown`.
  Deadline кооперативный: незавершённая mutation не бросается с освобождением аренды.
- Discovery не загружает config/runtime; runtime-команды не открывают log viewer и пишут presentation
  только одним JSON в raw stdout после shutdown. Логи остаются в stderr. Process stdout (fd 1 и
  `STD_OUTPUT_HANDLE`) изолирован в NUL до runtime; envelope пишется только в сохранённый поток.
- `--debug-report` только наблюдает: не меняет stdout/envelope/effect, не разрешает mutation, не
  делает повторных capture или команд. Сбой учёта debug изолируется (`AgentDebugRun.Guard`) и делает
  отчёт `incomplete`; отказ создания отчёта — до runtime. Без флага debug-композиция не регистрируется.
- Контракт, ограничения координации, Debug Mode и evidence: [Agent CLI](../../docs/reference/agent-cli.md).
