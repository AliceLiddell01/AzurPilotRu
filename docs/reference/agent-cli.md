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
AzurPilot.App.exe agent android-prepare --target mumu:1
AzurPilot.App.exe agent environment-prepare --target mumu:1 --timeout-ms 180000
AzurPilot.App.exe agent game-start --target mumu:1
AzurPilot.App.exe agent screenshot-scan --target mumu:1
AzurPilot.App.exe agent game-ui-observe --target mumu:1
AzurPilot.App.exe agent game-input-observe --target mumu:1
AzurPilot.App.exe agent game-dock-roundtrip --target mumu:1
AzurPilot.App.exe agent game-ui-ready-observe --target mumu:1
AzurPilot.App.exe agent game-ui-ready-ensure --target mumu:1 --timeout-ms 180000
AzurPilot.App.exe agent settings
```

Identity в примере условная: сначала выберите существующий exact target через inventory/resolve.
Игровые наблюдения и безопасный переход Dock требуют уже открытой игры; контракт и ограничения —
[game-ui](../architecture/game-ui.md). Обратимый переход сообщает отдельные результаты открытия
и возвращения; частичный эффект сохраняется при отказе и отмене.
Подготовка окружения сама обеспечивает MuMu и Android readiness; запуск игры включает эту подготовку.
Вызывающему агенту не требуется заранее выполнять цепочку низкоуровневых диагностических команд.
Остальные lifecycle и read-only операции перечисляются discovery.

`game-ui-ready-observe` возвращает классификацию и ограниченное evidence устойчивости без mutation.
Успешное read-only наблюдение состояния `Loading`, `Transition`, `AwaitingInput` или `Unknown` не
означает `Ready`. Потребитель обязан проверить `data.state`. Технический отказ или необходимость
ручного решения сохраняют failure. `game-ui-ready-ensure` включает существующую подготовку и запуск
игры, затем известный flow; успех требует именно `data.ui.state=Ready`. Обе операции повторно
проверяют exact identity/endpoint и foreground/viewport. Срок ensure задаёт только `--timeout-ms`:
при его истечении возвращается `AgentDeadlineExceeded`, а `data.lifecycle` и сведения о мутациях
сохраняются. `data.lifecycle` и `data.ui.phases` — фазы на одной монотонной шкале операции
(`enteredAtMs`, `exitedAtMs`, `elapsedMs`, `endReason`, `status=Observed|NotObserved`); `work` содержит
счётчики и длительности захвата, ADB-команды, распознавания, viewport, ввода и пауз. Список допустимых состояний/переходов,
стабильные признаки меню, независимые guards перекрытий и ограничения — [game-ui](../architecture/game-ui.md#готовность-главного-ui).

`android-prepare` подготавливает только transport выбранного target через существующий readiness owner.
Она не запускает MuMu или игру, не меняет настройки и не выполняет игровой ввод. Один target-local
connect допускается только при исходном `Absent`; `Device` проверяется без connect, `Offline`/`Unknown`
дают отказ. Readiness требует независимых наблюдений `Device`, shell и boot `1` в пределах общего
deadline. Физически недоступный endpoint может остаться неготовым после единственной попытки.
Процедура живого игрового preflight принадлежит [game-ui acceptance](../testing/game-ui.md#безопасная-живая-проверка).

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

У `android-prepare` `data.preparation` содержит доказанный успешный итог либо `null`, а `data.mutation`
сохраняет `initialTransport`, `mutationAttempted`, `changeProven` и `lastReadiness` даже при ожидаемом
отказе. `lastReadiness` — последние наблюдённые transport/shell/boot, а не обещание их неизменности;
ненаблюдавшийся boot остаётся `null`. При исключении эти же факты доступны в
`data.mutation.androidPreparation`. `failure` сообщает итог отдельно от эффекта: новый transport
может быть доказан до отказа boot. В `game-dock-roundtrip` успешная preparation хранится рядом с
`open`/`close`; ранний отказ несёт `androidMutation` и `gameUiMutation`. `effect=changed` из-за ADB
не означает доказанного перехода игры — проверяй оба `changeProven` игровых результатов.
`open.timing`/`close.timing` и `data.timing` дают время и работу бота по шагам; их смысл описан в
[игровом UI](../architecture/game-ui.md#фазовая-телеметрия).

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

При ненулевом exit code захвата `failure.details.transport_state` классифицирует доказанный
`absent` exact endpoint по существующему fail-closed parser либо сообщает `unknown`. Это evidence
данной команды, а не ретроспективная диагностика старых сбоев. Сырой stderr не публикуется;
дополнительных ADB-команд и скрытого connect диагностика не выполняет.

PNG-export этим интерфейсом не реализован. Encoded PNG и native RGB frame не передаются в
JSON/base64/stdout. Файловые артефакты — явно запрошенный debug-отчёт (ниже; discovery описывает его в
`artifacts`) и локальные отчёты команд измерения транспортов.

Native-компоненты (например, DLL эмулятора для `nemu_ipc`) могут писать диагностику в CRT stdout. Процесс
Agent CLI до запуска хоста перенаправляет fd 1 и `STD_OUTPUT_HANDLE` в NUL и пишет envelope в сохранённый
дубликат исходного stdout, поэтому stdout по-прежнему содержит ровно один JSON.

## Транспорты снимков

Выбор транспорта захвата прозрачен для остальных команд; архитектура, статусы и правила выбора —
[транспорты снимков](../architecture/screenshot-transports.md).

| Команда | Mutation | Назначение |
| --- | --- | --- |
| `capture-methods` | нет | Матрица девяти транспортов, настройка `screenshot`, калибровка `auto` для отпечатка target, проверка SHA-256 закреплённых зависимостей. Без захвата. |
| `capture-probe` | да | Worker одного транспорта (`--method`, `--samples`): эталон ADB, серия, освобождение. Сцену не проверяет; используется `capture-benchmark`. |
| `capture-benchmark` | да | Все или `--methods` транспорты на доказанном главном экране в отдельных worker-процессах, выбор, калибровка `auto` (`--calibrate`, по умолчанию `true`), опционально `--debug-overhead`. |
| `capture-intervals` | да | `--scene normal` на главном экране → `AWAITING_USER_COMBAT_CONFIRMATION` с кодом; `--scene combat --confirm <код>` той же `--session` после ручного входа пользователя в бой. |

Mutation-эффект измерений — временные helper-ы, forward/reverse и файлы в `/data/local/tmp/azurpilot-capture`;
helper-ы и порты освобождаются, поэтому `effect` = `mayHaveChanged` для не-ADB методов. Ввода в игру нет.
Отчёты пишутся в `.codex/local/reports/capture-benchmark/<run-id>/` и
`.codex/local/reports/capture-intervals/<session>/` текущего checkout; кадры в них не сохраняются. В envelope —
краткая сводка, полный набор измерений — в отчёте. Аргументы и типы публикует `discover`.

## Debug Mode

`--debug-report <session>` включает доказательную отладку одного вызова любой команды, кроме
`discover`. Без аргумента композиция, stdout и файловая система не меняются: декораторы и наблюдатели
регистрируются только в хосте этого вызова. Debug Mode не разрешает mutation — разрешение на
MuMu/игру/ввод по-прежнему даёт только выбор mutation-команды и действующая процедура приёмки.

```powershell
AzurPilot.App.exe agent game-ui-observe --target mumu:1 --debug-report pr-25-debug-mode
```

`<session>` — тема или PR (`pr-<n>-<slug>`): 1..64 строчных ASCII-букв, цифр и дефисов без дефиса по
краям и имён устройств Windows. Отчёт пишется в `.codex/local/reports/<session>/<run-id>/` ближайшего
checkout выше рабочего каталога (поиск `.git`); каталог игнорируется Git. Если checkout не найден,
каталог недоступен для записи или ведёт через reparse point, команда возвращает
`debug_report_unavailable` до запуска runtime и без эффектов. Путь отчёта сообщается одной JSON-строкой
stderr с `category=AzurPilot.App.Agent.Debug`, `debugReport`, `diagnostics` и `incompleteReasons`;
stdout остаётся тем же единственным envelope.

Каталог прогона (`<run-id>` = UTC-время, операция и префикс correlationId):

| Файл | Содержание |
| --- | --- |
| `manifest.json` | identity прогона, аргументы (значение `--config` скрыто), target, итог envelope, checkout/build, лимиты, счётчики и список кадров с `frameId`, `sha256`, состоянием и отметками; `diagnostics.status` = `complete`/`incomplete` с причинами; `files` — инвентарь остальных файлов с итоговыми `bytes` (сам `manifest.json` в него не входит, `filesExclude`) |
| `trace.jsonl` | события по фактическому порядку записи: `seq`, монотонное `tMs`, `utc`, `kind`, `source`, связи `frame`/`frameId`/`actionId`/`phaseId` |
| `performance.json` | время production-шагов (capture, ADB, decode, match, viewport, tap, wait, процессы) отдельно от диагностической работы (превью, trace, рендер и запись JPEG) |
| `summary.md` | краткий русский обзор, таблица кадров, русская легенда меток JPEG, невстреченные фазы (`NOT_TESTED`) и ограничения |
| `frames/*.jpg` | аннотированный JPEG для каждого успешного production capture |

Сессия хранит `runs.jsonl` — индекс прогонов; параллельные вызовы получают разные каталоги и
дописывают индекс под эксклюзивной блокировкой.

### Кадры

Превью создаётся native boundary из того же живого кадра непосредственно перед его освобождением:
уменьшение `INTER_AREA` до логического размера [GameGeometry](../../src/AzurPilot.Core/Game/GameGeometry.cs),
затем рисование на копии и кодирование JPEG без EXIF. Повторного захвата нет, исходный кадр не меняется,
полный RGB-буфер в managed не копируется. Отказ capture без изображения не порождает JPEG, а
учитывается в `captureFailures`. Кадр неподдерживаемой геометрии получает честный `skipped`.
Распознавание и ввод используют исходный кадр полного разрешения; JPEG — иллюстрация решения.

Кадр «запечатывается» с началом следующего capture или в конце прогона, поэтому ввод и postcondition,
принятые по кадру позже, попадают на его же JPEG. На кадре: ROI ассетов действия (серый штрих),
`Unique` (зелёная рамка), `Ambiguous` (оранжевый штрих со score/margin/mae), безопасная область ввода
(циан), отметки tap и подписи с состоянием, viewport и отсутствующими признаками (`no_box`). Подписи рисуются
только штрихом и не закрывают кадр заливкой. Рамки `Absent` не рисуются: лучший отвергнутый кандидат не
является наблюдённым объектом. Шрифт native boundary поддерживает только ASCII, поэтому на JPEG остаются
короткие технические метки (идентификаторы стадий и статусов), а их русское объяснение — в `summary.md`. Маскирование не выполняется,
поэтому отчёт не публикуется в PR, CI или сообщениях.

Стадии ввода различаются событиями: `tap_planned` (кандидат), `tap_permitted` (guards и возраст кадра),
`tap_attempted` (вызов адаптера), `tap_issued` (процесс ADB ввода запущен и вернул итог; фиксируется после
возврата процесса, а не до запуска), `tap_completed` (exit 0),
`tap_not_issued` (адаптер отверг до ADB), `tap_result_unproven` (эффект неизвестен). Ни одна стадия не
доказывает переход UI: это делает отдельный `postcondition_frame`. Стадии `tap_attempted`, `tap_issued` и
`tap_completed` несут `frame`, `frameId` и `actionId` разрешения (`GameTap`), по которому выполнен ввод;
`tap_issued` берёт их из области этого разрешения и расходует её один раз, а без области (или при повторном
запуске) записывает `correlation=none` без кадра и действия. Отказ запуска процесса `tap_issued` не порождает;
аргументы процесса, включая координаты, в эту запись не попадают.

### Лимиты и полнота

Точные значения публикует `manifest.settings`; их владелец —
[`AgentDebugRun`](../../src/AzurPilot.App/Agent/AgentDebugRun.cs) и
[`AgentDebugFrames`](../../src/AzurPilot.App/Agent/AgentDebugFrames.cs). Ограничены: число и объём
событий trace, число и общий объём JPEG одного прогона, ограниченная очередь фоновой записи, минимальное
свободное место, время ожидания записи при завершении. Объём и число файлов сессии — не жёсткий лимит, а
порог допуска (`sessionAdmissionBytes`, `sessionAdmissionFiles`): при запуске измеряется занятое место сессии;
если порог уже достигнут, JPEG отключаются (`frames_disabled:session_limit`), иначе бюджет JPEG прогона
(`frameByteBudget`) не превышает остаток порога, и недостающие кадры получают `skipped` с причиной
`session_limit` (`frames_missing`). Параллельные прогоны одной сессии делят остаток без резервирования, поэтому
суммарный объём сессии не гарантируется порогом; trace и итоговые файлы ограничены собственными пределами. Превышение не
останавливает команду и не меняет envelope: оно становится причиной `incomplete`
(`frames_missing`, `frames_disabled`, `trace_incomplete`, `late_annotation_events`,
`diagnostic_failures` и т. п.). Сбой собственного учёта debug изолирован от production-вызова и тоже
делает отчёт неполным.

Успешный envelope, `complete` и наличие JPEG не являются вердиктом приёмки: PASS/FAIL ставит
[live-testing](../../.agents/skills/azurpilot-live-testing/SKILL.md). Хранение ручное; отчёты удаляются
вместе с `.codex/local/`.

Managed tests запускают настоящий EXE для JSON/negative startup paths и отдельные процессы с
управляемым `IMuMuHost` для CLI↔CLI и взаимодействия общей границы сервиса TUI↔CLI. Screenshot-тест исполняет
настоящую native DLL с управляемым PNG. Это Windows/runtime evidence без физической mutation MuMu.
Debug Mode покрыт fixture-цепочкой Main→Dock→Main через production capture, matcher и ввод с настоящей
native DLL, а также процессным тестом EXE. Real MuMu lifecycle acceptance и визуальная проверка TUI
остаются отдельными доказательствами.
Визуальный [TUI Harness](../testing/tui-harness.md) не является частью Agent CLI.
