# Runtime diagnostics — agent-critical контракт

Подробное описание composition/logging/snapshot находится в
`../../docs/operations/runtime-diagnostics.md`.

## Composition

- Единственный composition root — `AzurPilotHost`; `Program.cs` не становится владельцем runtime-логики.
- Пользовательская конфигурация загружается строгим loader-ом и передаётся как готовый snapshot.
- DI содержит только реально используемые services; optional placeholders «на будущее» не вводятся.
- Coordination/time owners, от которых зависит lifecycle correctness, не дублируются вторыми singleton-ами.

## Logging

- Project-owned runtime logging использует `Microsoft.Extensions.Logging`.
- Structured logs идут в `stderr`; обычный human/machine presentation output — в `stdout`.
- Устойчивые события имеют стабильные structured properties; произвольные большие внешние payload не
  логируются.
- Секреты, полный config, machine-specific paths и полный stdout/stderr внешних tools в logs не попадают.
- Одна logical operation сохраняет общий correlation identity через вложенные шаги.

## Diagnostic snapshot

Snapshot bounded и описывает фактически наблюдённое состояние application/config/native/MuMu/Android/game.
Он не является дампом внутренних объектов.

- Недоступность capability может быть диагностическим результатом, а не startup failure.
- «Не наблюдалось» не превращается в доказанное `false`.
- MuMu/Android/game diagnostics read-only: startup не запускает/останавливает emulator/game и не
  выполняет ADB `connect`.
- Ошибка одного диагностического шага сохраняет честный bounded result, а не синтезирует успех.

## Startup

- Configuration/native failures, делающие основной runtime некорректным, завершают startup через
  application failure/exit mapping.
- Отсутствующая/неготовая MuMu/Android/game среда сама по себе остаётся диагностическим состоянием,
  пока конкретная product operation не требует readiness.
- CLI/REPL/`doctor` как product surface этим diagnostic service автоматически не создаются.

Состав конкретных capability-facts принадлежит их owners; здесь не дублируй полные списки полей.
