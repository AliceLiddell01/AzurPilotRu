# Application failures — agent-critical контракт

Подробное объяснение модели находится в `../../docs/reference/application-failures.md`.
Точные строковые коды принадлежат `ApplicationFailure` в Core, а process exit mapping —
`AzurPilotExitCode` в App.

## Модель

- Ожидаемый отказ возвращается типизированным значением `ApplicationResult`/`ApplicationResult<T>`.
- Исключения используются для programming errors и действительно неожиданных сбоев, а не как
  альтернативный путь ожидаемого failure.
- Не создавай второй каталог failure codes в документации, adapter-е или presentation layer.
- Новый код появляется только вместе с реальным поддерживаемым состоянием/отказом capability.
- Уже опубликованные stable codes/exit mappings не перенумеровываются без отдельной migration причины.

## Retry semantics

`IsRetryable` — часть контракта конкретного failure, а не автоматическая retry policy.
Не добавляй retry/backoff/circuit breaker в модель отказов только потому, что код помечен retryable.

## Structured details

- Details bounded, machine-readable и минимальны.
- Не включай полный config, stdout/stderr, process lists, stack traces, секреты и абсолютные
  machine-specific пути.
- Человекочитаемый `Message` не является machine contract и может меняться без смены stable code.
- Каждая boundary формирует только факты, которыми действительно владеет.

## Boundary projection

- Platform/native adapter проецирует собственные ожидаемые ошибки в application failure без логики
  presentation layer.
- Core orchestration не подменяет ожидаемый host failure `InternalError`.
- Не смешивай «команда вернула ненулевой код», «операция не доказала postcondition»,
  «истёк deadline» и «состояние неизвестно».
- Для MuMu/Android success определяется наблюдаемым postcondition, а не process exit code mutation.

Конкретные условия MuMu и Android принадлежат соответственно `mumu-lifecycle.md` и
`android-game-lifecycle.md`.
