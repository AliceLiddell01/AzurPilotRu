# Приёмка операторского TUI

Headless tests проверяют production operator use cases и переходы ограниченного UI state. Они не
подменяют фактическую терминальную границу. Перед выпуском TUI отдельно проверяется на Windows x64 в
Windows Terminal с PowerShell 7, а live capability — только на явно выбранном MuMu instance.

## Интерактивная терминальная сессия

Собери Release по каноническому workflow из [getting-started.md](../getting-started.md), затем запусти
`AzurPilot.App.exe` в открытой Windows Terminal сессии без перенаправления stdin/stdout. Проверь:

- открывается fullscreen TUI; `/` и `F2` показывают палитру, стрелки перемещают выбор, `Tab` дополняет,
  `Enter` выполняет, `Esc` закрывает;
- `/help` показывает только доступные сейчас действия; startup status берётся из одного snapshot, и без
  явного выбора target никакое MuMu/Android/game действие не запускается;
- русские символы вводятся и вставляются в composer; история и log pane прокручиваются независимо от
  ввода; resize от широкого окна до 60×18 не теряет composer или UI state;
- `F3` открывает/закрывает отдельную log pane; во время логирования активный renderer не получает
  console output;
- confirmation mutation по умолчанию отменяет действие; `Esc` или выбор отмены не запускает service;
- отмена долгой операции и выход восстанавливают экран, курсор и terminal input state.

Если stdin или stdout перенаправлены, приложение должно завершить одноразовую диагностику, а не ждать
клавиши и не писать terminal control sequences. Проверяй оба потока раздельно.

## Live MuMu и screenshot

Перед live шагами запиши bounded preflight состояния среды. Выполняй действия из самого
`AzurPilot.App.exe`, не из acceptance executable и не из отдельной ADB-команды:

1. Открой `/instances` и выбери конкретный `MuMuInstanceId` из реально обнаруженного inventory. Не
   используй display name или автоматический выбор первого экземпляра.
2. Выполни read-only `/android` и `/game`. Сохрани доказанный результат или ограниченную причину
   неизвестного состояния.
3. Выполни `/screenshot` только когда существующая production readiness уже доказывает готовый target.
   Убедись, что один capture породил `FullFrame` и `Center` scan с фактическим размером, bounds и
   aggregate RGB statistics; не трактуй цветовую статистику как распознавание экрана.
4. Повтори read-only наблюдение host/Android/game состояния и сохрани bounded postflight рядом с тем же
   build identity и exact target.

Если MuMu отсутствует, instance не выбран, endpoint/readiness неизвестны или Android не готов, пометь
live часть как `BLOCKED` с кодом причины. Не запускай эмулятор, не подключай ADB и не меняй игру для
того, чтобы превратить отказ precondition в зелёный результат. Любую отдельную mutation можно включить
в acceptance только через production orchestration и при доказуемом восстановлении начального состояния.

Evidence отчёт ограничивается build identity, выбранным точным target, подтверждёнными фактами до/после,
кодами отказа и итогом ROI scan. Не сохраняй screenshot bytes, полный вывод внешних команд, полный список
процессов или machine-specific секреты. Acceptance executable остаётся полезным для собственных
контрактов, но его результат не доказывает работоспособность TUI.
