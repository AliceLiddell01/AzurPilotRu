# Приёмка операторского TUI

Headless tests проверяют production operator use cases и переходы ограниченного UI state. Они не
подменяют фактическую терминальную границу. Перед выпуском TUI отдельно проверяется на Windows x64 в
Windows Terminal с PowerShell 7, а live capability — только на явно выбранном MuMu instance.
Захват в Windows ConPTY через xterm.js полезен для проверки input, cell layout и resize, но не доказывает
рисование или resize в физическом Windows Terminal.

## Интерактивная терминальная сессия

Собери Release по каноническому workflow из [getting-started.md](../getting-started.md), затем запусти
`AzurPilot.App.exe` в открытой Windows Terminal сессии без перенаправления stdin/stdout. Проверь:

- открывается fullscreen TUI; `/` и `F2` показывают палитру, стрелки перемещают выбор, `Tab` дополняет,
  `Enter` выполняет, `Esc` закрывает;
- `/help` показывает только доступные сейчас действия; startup status берётся из одного snapshot, и без
  явного выбора target никакое MuMu/Android/game действие не запускается;
- русские символы вводятся и вставляются в composer; история и полноширинная нижняя log pane
  прокручиваются независимо от ввода; колесо мыши меняет только pane под курсором;
- `F3` открывает/закрывает журнал; `PgUp`/`PgDn` прокручивают историю, `Alt+PgUp`/`Alt+PgDn` — журнал;
  во время логирования активный renderer не получает console output;
- `/environment-prepare` показывает план для выбранного exact target и требует подтверждения: стартует
  MuMu только из доказанного `Stopped`, пропускает перезапуск при `Running`, использует endpoint того же
  target и проверяет Android transport/shell/boot readiness; игра не запускается. `Unknown` и ошибки
  preflight fail closed, а после уже выполненного старта MuMu может остаться запущенным при отказе или
  отмене;
- confirmation mutation по умолчанию выделяет «Отмена»: `Enter`, `Esc` или выбор отмены не запускают
  service; подтвердить можно только отдельным явным выбором;
- проверь footer и resize одной сессией 120×30 → 100×30 → 80×25 → 60×18 → 59×17 → 120×30: `F4`,
  `Ctrl+C` и `F2` остаются видимы на всех поддерживаемых размерах, ниже 60×18 отображается предупреждение,
  возврат восстанавливает выбранный target и панели;
- отмена долгой операции и выход восстанавливают экран, курсор и terminal input state.

Если stdin или stdout перенаправлены, приложение должно завершить одноразовую диагностику, а не ждать
клавиши и не писать terminal control sequences. Проверяй оба потока раздельно. При успехе итог stdout
должен говорить только о запуске host/начальной диагностике и явно не подтверждать готовность MuMu,
Android или Azur Lane; structured JSON не должен попадать в stdout.

## Live MuMu и screenshot

Перед live шагами запиши bounded preflight состояния среды. Выполняй действия из самого
`AzurPilot.App.exe`, не из acceptance executable и не из отдельной ADB-команды:

1. Открой `/instances` и выбери конкретный `MuMuInstanceId` из реально обнаруженного inventory. Не
   используй display name или автоматический выбор первого экземпляра.
2. Выполни read-only `/android` и `/game`. Сохрани наблюдённые факты либо ограниченную причину, по
   которой состояние не удалось доказать. Эти команды не подключают ADB, не запускают MuMu и не меняют
   игру.
3. Различай пассивное наблюдение и подтверждённую подготовку:
   - Если проверяется только `/android` или `/game`, недоступность endpoint/readiness относится к этому
     read-only результату. Не запускай MuMu и не подключай ADB лишь для получения положительного ответа.
   - `/environment-prepare` — отдельная подтверждаемая операция. Она повторно разрешает exact target и
     наблюдает host-side состояние внутри production use case. При доказанном `Stopped` команда может
     запустить этот MuMu; при `Running` пропускает запуск. `Stopped` не является ошибкой предусловия.
     Read-only `android_endpoint_unavailable` до запуска не запрещает этот сценарий: endpoint и Android
     readiness разрешаются после host-side шага.
   - Если exact identity не разрешена, host-side состояние `Unknown` или discovery/наблюдение MuMu
     завершается реальным отказом, use case должен fail closed до последующих lifecycle/Android действий.
     Если отказ возникает уже после запуска MuMu на этапе endpoint/readiness, зафиксируй его фактическую
     stage и failure code; не представляй его как отказ предусловия до запуска. Частичное изменение
     состояния сохраняется без compensating stop.
4. После подготовки независимо повтори `/instances`, `/android` и `/game`. Успех запуска приложения или
   MuMu сам по себе не доказывает готовность Android: positive readiness требует exact endpoint,
   transport `Device`, доступный shell и `boot_completed=1`.
5. Выполни production `/screenshot` только после доказанной текущей Android readiness. Убедись, что один
   capture породил `FullFrame` и `Center` scan с фактическим размером, bounds и aggregate RGB statistics;
   не трактуй цветовую статистику как распознавание экрана. Не запускай Azur Lane только ради захвата.

Если exact instance отсутствует или identity/host-side состояние не доказаны, сохраняй конкретную причину
и не продолжай mutation через другой target или fallback. Если Android endpoint/readiness отсутствовали
до подтверждённого `/environment-prepare`, это само по себе не является `BLOCKED` для подготовки.
Выполняй её только через production orchestration, с отдельным явным подтверждением и точным target.
После теста восстанови исходное известное host-side состояние через production lifecycle-команду и
независимо подтверди postcondition через `/instances`.

Если понадобится автоматически запускать Azur Lane после подготовки, см. отдельный контракт и причины
в [описании операторского TUI](../architecture/operator-tui.md#follow-up-настраиваемый-запуск-игры).
Текущая приёмка `/environment-prepare` заканчивается доказанной Android readiness и не запускает игру.

Evidence отчёт ограничивается build identity, выбранным точным target, подтверждёнными фактами до/после,
кодами отказа и итогом ROI scan. Не сохраняй screenshot bytes, полный вывод внешних команд, полный список
процессов или machine-specific секреты. Acceptance executable остаётся полезным для собственных
контрактов, но его результат не доказывает работоспособность TUI.
