# Agent CLI

`AzurPilot.App.exe agent discover` возвращает JSON-описание реально поддерживаемых операций.
Точный каталог, аргументы, их типы и признак mutation принадлежат
[`AgentCommands`](../../src/AzurPilot.App/Agent/AgentCommands.cs) и
[`AgentRequest`](../../src/AzurPilot.App/Agent/AgentContract.cs). Общий реестр
[`AgentArgumentTypes`](../../src/AzurPilot.App/Agent/AgentArgumentTypes.cs) публикует базовый JSON-тип,
ограничение длины, числовые границы, грамматику identity и требования к пути; parser использует его же
проверки. Discovery не загружает конфигурацию,
не строит host, не обращается к MuMu, ADB или native DLL. Добавление команды в каталог одновременно
регистрирует parser, discovery и обработчик.

## Запуск

```powershell
AzurPilot.App.exe agent discover
AzurPilot.App.exe agent status
AzurPilot.App.exe agent diagnostics
AzurPilot.App.exe agent instances
AzurPilot.App.exe agent resolve --target auto
AzurPilot.App.exe agent android-observe --target mumu:1
AzurPilot.App.exe agent environment-prepare --target mumu:1 --timeout-ms 180000
AzurPilot.App.exe agent game-start --target mumu:1
AzurPilot.App.exe agent screenshot-scan --target mumu:1
AzurPilot.App.exe agent game-ui-observe --target mumu:1
AzurPilot.App.exe agent game-input-observe --target mumu:1
AzurPilot.App.exe agent game-dock-roundtrip --target mumu:1
AzurPilot.App.exe agent settings
```

Identity в примере условная: сначала выберите существующий exact target через inventory/resolve.
Игровые наблюдения и безопасный переход Dock требуют уже открытой игры; контракт и ограничения —
[game-ui](../architecture/game-ui.md). Операция roundtrip сообщает отдельные результаты открытия
и возвращения; частичный эффект сохраняется при отказе и отмене.
Подготовка окружения сама обеспечивает MuMu и Android readiness; запуск игры включает эту подготовку.
Вызывающему агенту не требуется заранее выполнять цепочку низкоуровневых диагностических команд.
Остальные lifecycle и read-only операции перечисляются discovery.

`--target` применим только к операциям над экземпляром. При его отсутствии используется `mumu.instance`
эффективного snapshot. `auto` означает единственного кандидата; при неоднозначности изменения запрещены.
Явный target действует только в одном вызове и не сохраняется в конфигурацию. Endpoint пользователь
не задаёт: его разрешает существующий production resolver для той же identity с повторной проверкой.

Необязательный `--config` принимает абсолютный путь к строгой пользовательской конфигурации. Без него
сохраняется runtime-выбор YAML/legacy JSON. Это выбор источника snapshot одного процесса, а не новый
слой настроек. Discovery этот аргумент отклоняет. Неизвестные, повторные и неприменимые аргументы
отклоняются целиком. `settings` читает только реализованные безопасные параметры; записи настроек через
Agent CLI пока нет.

## JSON и ошибки

Namespace `agent` в первой позиции выбирается до legacy parser и проверки терминала. Машинный путь не открывает
TUI и отдельный log viewer, в том числе при включённом viewer в YAML. Без namespace сохраняются прежние
TUI, `--mode`, log viewer и однократная диагностика перенаправленных потоков.

Stdout содержит один UTF-8 JSON-документ без BOM, баннера, progress, markup и бинарных данных.
Он записывается после завершения host/dispose. Диагностические логи идут отдельно в stderr.
Отсутствующая native boundary мешает screenshot/диагностике native, но не запрещает чтение settings
или inventory. Общая конфигурационная ошибка отклоняет runtime-команду до обращения к устройству.

Пример успешного разрешения identity:

```json
{"schemaVersion":1,"correlationId":"0123456789abcdef0123456789abcdef","operation":"resolve","target":"mumu:1","status":"success","exitCode":0,"effect":"none","failure":null,"data":{"instanceId":"mumu:1"},"requestedTarget":"mumu:1"}
```

Пример отказа разбора:

```json
{"schemaVersion":1,"correlationId":"0123456789abcdef0123456789abcdef","operation":null,"target":null,"status":"failure","exitCode":29,"effect":"none","failure":{"code":"invalid_argument","isRetryable":false,"message":"Операция отклонена: invalid_argument","details":null},"data":null,"requestedTarget":null}
```

Envelope и лимит ответа версионируются владельцем
[`AgentJson`](../../src/AzurPilot.App/Agent/AgentContract.cs). Поля верхнего уровня сохраняют типы:
`operation`, `requestedTarget`, `target` — строка либо null; `target` означает разрешённую identity,
а `requestedTarget` — явный запрос клиента. `data` — ограниченные факты конкретной операции либо null.
Состояния `Unknown` и nullable факты остаются недоказанными; отрицательным результатом они не являются.
Изменение wire-контракта требует решения о версии envelope.

`status` различает `success`, `failure`, `cancelled`, `busy`. Клиент использует `failure.code`,
`failure.isRetryable` и `exitCode`, а не текст сообщения. Строковые коды имеют единственного владельца
[`ApplicationFailure`](../../src/AzurPilot.Core/Failures/ApplicationFailure.cs), числовая проекция —
[`AzurPilotExitCode`](../../src/AzurPilot.App/AzurPilotExitCode.cs). Ожидаемые MuMu/ADB/native отказы
сохраняют существующие коды. Повторяемость не означает, что операция была автоматически повторена.
Сообщения исключений и локальные пути не становятся машинным payload; details ограничены.

## Mutation и конкурентный доступ

Отдельная mutation-команда — явное намерение клиента. Stdin-промптов, GUI-подтверждений и зависимости
от TUI mode нет. Read-only команды не выполняют скрытые launch/connect/game mutation.

[`WindowsInstanceMutationLease`](../../src/AzurPilot.Windows/MuMu/WindowsInstanceMutationLease.cs)
использует открытый файл с `FileShare.None` в каталоге AzurPilot пользователя. Общий operator service
удерживает handle через всю последовательность revalidation → observation → mutation → postcondition
и предусмотренный postflight. Это одна защита для TUI и Agent CLI; process-local gates Core остаются
дополнительной внутренней координацией. Вложенная preparation/game orchestration не захватывает аренду
повторно. Разные exact identity не сериализуются глобально. При конфликте возвращается немедленный
`busy`, очереди нет; при недоступной защите mutation запрещена.

Handle не привязан к managed-потоку, закрывается при завершении/падении процесса и не наследуется
дочерними tools. Пустые lock-файлы сохраняются: удаление после закрытия создало бы гонку повторного
открытия. Они не содержат пользовательских данных. Гарантия действует между обновлёнными процессами
AzurPilot одного Windows-пользователя, включая разные login-сессии. Другие Windows accounts, старые
версии приложения, acceptance tools с прямым вызовом Core и внешние MuMu/ADB клиенты этой арендой не
координируются. Read-only наблюдения могут видеть переходное состояние конкурентной mutation.

`effect` различает:

- `none` — изменений этим вызовом не было, включая известный отказ до mutation и доказанный no-op;
- `changed` — наблюдением доказан хотя бы один переход; это не обещание успеха всей операции;
- `mayHaveChanged` — mutation могла состояться, но её эффект/конечное состояние не доказаны.

Сведения о попытке mutation записываются до вызова внешней границы, а доказанное изменение — после
независимого наблюдения postcondition. Диагностическая стадия не определяет `effect`: отказ до mutation
с готовым окружением сохраняет `none`, исключение после попытки — `mayHaveChanged`, а доказанное
частичное изменение подготовки или остановки при restart — `changed`.

После частичного отказа `data` сохраняет preparation stage, начальное состояние, результат запуска
MuMu и доступное postflight-наблюдение. Ошибка shutdown сохраняет уже завершённый результат в
`data.completed`. Успешный lifecycle несёт наблюдённый `finalState`; код возврата внешнего tool не
заменяет postcondition. Отмена или deadline не означают rollback. Слепой компенсирующей остановки нет.

## Ограничения времени и объёма

Общий `--timeout-ms` ограничен parser-ом; значения и default публикуются discovery. Отмена Ctrl+C
кооперативная. Синхронные внешние probes завершаются по собственным production timeouts: общий deadline
проверяется между шагами, поэтому возможен bounded хвост текущего probe, postflight и shutdown.
Host shutdown ограничен отдельно. Процесс не бросает незавершённую mutation ради преждевременного
ответа и освобождения аренды. Принудительное завершение процесса не гарантирует JSON или откат.

Inventory ограничен количеством наблюдений; превышение лимита ответа возвращает структурированный
отказ до единственной записи stdout, сохраняя `effect`. Длительный сервер, scheduler и jobs отсутствуют.

## Screenshot и evidence

`screenshot-scan` использует один production capture и тот же native ROI scanner. JSON содержит
реальные frame dimensions/format, время capture, named ROI, half-open physical pixel bounds и RGB
statistics. Логическое reference space не является физическим размером кадра. Статистика не означает
OCR, распознавание игрового экрана или UI readiness.

PNG-export и файловые артефакты этим интерфейсом не реализованы; discovery возвращает пустой набор
artifact capabilities. Encoded PNG и native RGB frame не передаются в JSON/base64/stdout. Удалять
артефакты после вызова не требуется: screenshot-файлы не создаются.

Managed tests запускают настоящий EXE для JSON/negative startup paths и отдельные процессы с
управляемым `IMuMuHost` для CLI↔CLI и взаимодействия общей границы сервиса TUI↔CLI. Screenshot-тест исполняет
настоящую native DLL с управляемым PNG. Это Windows/runtime evidence без физической mutation MuMu.
Real MuMu lifecycle acceptance и визуальная проверка TUI остаются отдельными доказательствами.
Будущий визуальный TUI Harness не является частью Agent CLI.
