# Runtime: composition, логирование, correlation и диагностика

Владелец: этот файл. Здесь описаны composition root приложения, structured logging и граница
`stdout`/`stderr`, correlation/operation identity и состав runtime-диагностического snapshot. Правила
пользовательской конфигурации принадлежат
[application-configuration.md](application-configuration.md), application-level модель отказов —
[application-failures.md](application-failures.md), содержание проверок — [verification.md](verification.md),
карта проекта и границы — [architecture.md](architecture.md).

## Composition root и состав host-а

Application host собирается в `AzurPilotHost` (`src/AzurPilot.App/`): это единственное место, где
строится host и выполняется startup. Точка входа только вызывает startup и печатает человекочитаемый
итог, поэтому runtime-логика в `Program.cs` не живёт. Командной строки у приложения нет: startup
вычисляет runtime-путь конфигурации сам через его владельца
([application-configuration.md](application-configuration.md)).

Host строится на `Host.CreateEmptyApplicationBuilder(...)` с отключёнными defaults: зависимости,
configuration providers и logging providers добавляются осознанно, а не появляются скрытым
стандартным набором. `Host.CreateApplicationBuilder` с неявным набором providers не используется.

Отклоняются как конфигурация приложения и не подключаются:

- `appsettings.json` и environment-specific `appsettings.*`;
- user secrets;
- environment variables;
- Debug/EventSource/EventLog providers без конкретной причины.

Пользовательская конфигурация — один строгий JSON snapshot, поэтому ни один configuration provider
не регистрируется: загруженный snapshot передаётся в DI как готовый объект
([application-configuration.md](application-configuration.md)). В DI попадают только реально
используемые runtime services этого этапа: snapshot конфигурации, диагностическая операция и проекция
ошибок платформенной/native boundary в application-отказ
([application-failures.md](application-failures.md)). Service или interface не вводится «на будущее».

Реальная MuMu-capability подключена к тому же composition root: production host-side поверхность
`IMuMuHost` приходит из платформенной boundary `AzurPilot.Windows`, а orchestration lifecycle, координация
mutation, часы и числа времени — из Core. Все они — singleton-ы одного host-а: второй экземпляр
координации mutation нарушил бы process-local гарантию «одновременных mutation одного экземпляра нет», а
второй источник времени — контракт deadline. MuMu-операции резолвятся из DI и не создаются вручную, а
optional-зависимостей с молчаливыми значениями у orchestration нет. Правила семейства MuMu, identity
экземпляра и lifecycle принадлежат [mumu-lifecycle.md](mumu-lifecycle.md).

## Structured logging

Логирование использует стандартный `Microsoft.Extensions.Logging`; сторонний logging framework не
подключается. Правила:

- единственный logging provider — встроенный console provider с действующим JSON formatter;
- `stdout` — поверхность обычного application/presentation output и будущего machine-output;
  structured runtime logs туда не попадают;
- весь runtime log направляется в `stderr`, поэтому техническая диагностика отделена от
  пользовательского вывода;
- устойчивые project-owned события оформлены source-generated `[LoggerMessage]` со статическим
  сообщением и structured properties; тексты событий собираются не из строк на месте;
- минимальный уровень логирования принадлежит загруженному snapshot конфигурации. Если конфигурация
  отклонена, snapshot не существует: тогда действует встроенный уровень, потому что настройку
  отвергнутого файла применять нельзя. Встроенный уровень читается у его владельца
  `AzurPilotConfigurationDefaults`, а не повторяется литералом в application host;
- штатный lifetime-шум generic host поднят до предупреждений: порог повышается, а не скрывает сбои;
- MuMu-события идут тем же стеком и тем же correlation: host-side адаптер сообщает итог обнаружения
  установки, Core orchestration — выбор экземпляра, запрошенную lifecycle-операцию, доказанный terminal
  postcondition и отказ, application host — bounded итог MuMu-диагностики. Набор событий bounded: каждый
  poll и каждая mutation не являются событиями уровня `Information` и остаются диагностическими записями
  уровня `Debug`; полный stdout/stderr control utility в логи не попадает.

Логирование не превращается в UI, а вывод проектируется под будущего machine-consumer: в события не
попадают полный документ конфигурации, секреты, токены и произвольные большие payload. Сложный
универсальный redaction framework на этом этапе не вводится.

## Correlation / operation identity

Одна логическая операция запуска представлена `AzurPilotOperation` (`src/AzurPilot.App/`) и построена
только на стандартных `System.Diagnostics.ActivitySource` и `Activity`:

- correlation identifier операции — её trace identifier в hex-форме; он одинаков для startup,
  загрузки конфигурации и native-диагностики одного запуска;
- шаги операции вложены в операцию явным контекстом, а не неявным `Activity.Current`, поэтому шаг
  разделяет identifier операции гарантированно;
- локальный слушатель источника регистрируется на процесс: без слушателя `StartActivity` вернул бы
  `null` и correlation identity исчезла бы. Отсутствие слушателя считается неполной диагностикой и
  приводит к явному отказу запуска, а не к запуску без correlation;
- identifier попадает в каждую запись structured log: события application host несут его и как свойство
  события, и как scope записи, а события остальных project-owned владельцев — тем же scope, поэтому один
  identifier связывает события одной операции независимо от владельца события.

Не подключаются OpenTelemetry, exporter, collector, метрики и persistence telemetry: нужен только
локальный стандартный seam, к которому позже можно подключить exporter без изменения остального кода.

## Runtime diagnostic snapshot

Диагностическая операция — `AzurPilotDiagnosticService` (`src/AzurPilot.App/Diagnostics/`): она
используется текущим startup path, возвращает данные и ничего не печатает, поэтому будущее
`doctor`-представление сможет её переиспользовать, не меняя состав диагностики.

Snapshot — `AzurPilotDiagnosticReport` с четырьмя bounded секциями:

- application: identity сборки application host, .NET runtime и его identifier, описание
  операционной системы и её архитектура, архитектура процесса, идентификатор процесса, признак
  64-битного процесса;
- configuration: источник (встроенные defaults или файл), версия схемы источника и версия эффективной
  схемы, признак нормализации legacy-входа, статус валидации, минимальный уровень логирования и путь
  файла. Обе версии схемы нужны, чтобы нормализация v1 → v2 не выглядела молчаливой подменой
  конфигурации; полного дампа документа конфигурации в snapshot нет;
- native: доступность native boundary, совместимость ABI и обязательных capability, фактическая
  версия ABI, фактическая версия OpenCV, подтверждённые capability, факт реального исполнения кода
  OpenCV, строка сведений о сборке native библиотеки, причина совместимости и application-отказ, если
  границу использовать нельзя;
- mumu: обнаружена ли установка MuMuPlayer и её версия, bounded статус control surface, разрешённый
  конфигурацией экземпляр, каноническая identity и отображаемое имя выбранного экземпляра, наблюдённое
  host-side состояние и bounded evidence последнего наблюдения, а также application-отказ, если
  диагностика остановилась. Полного дампа реестра, списка процессов, полного stdout/stderr control
  utility, полного документа конфигурации и machine-specific путей установки в секции нет: внешние
  текстовые значения приводятся к bounded однострочной форме их владельцем, а не печатаются как есть.

Недоступная или несовместимая native boundary — результат диагностики, а не исключение: ошибка
границы проецируется в application-отказ production-маппером, полученным из DI, и попадает в
native-секцию как данные ([application-failures.md](application-failures.md)).

MuMu-секция собирается чтением host-side поверхности: обнаружение установки, разрешение выбранного
экземпляра и наблюдение его состояния. Разрешение выбора не повторяет семантику выбора у себя — она
принадлежит orchestration Core. Диагностика MuMu не запрашивает mutation ни на одном пути, поэтому
startup не может незаметно запустить или остановить эмулятор; отказ любого шага остаётся данными секции
([mumu-lifecycle.md](mumu-lifecycle.md)).

## Startup и отказы

- Ожидаемый отказ (невалидная конфигурация, недоступная или несовместимая native boundary) завершает
  запуск явным предсказуемым ненулевым кодом выхода и не маскируется как здоровый запуск.
  Соответствие «код отказа → код выхода процесса» принадлежит
  [application-failures.md](application-failures.md).
- MuMu-отказ не является отказом запуска: отсутствие MuMu, неподдерживаемая control surface,
  остановленный экземпляр и неразрешённый выбор экземпляра — нормальные диагностические результаты,
  которые отображаются в snapshot и в человекочитаемом итоге, но не отклоняют запуск и не «исправляются»
  запуском эмулятора. Startup остаётся read-only по отношению к MuMu.
- Нормальный startup выдаёт короткий человекочитаемый итог в `stdout`; технические structured logs
  идут отдельно в `stderr`. Человекочитаемый итог не является контрактом отказа.
- Ожидаемый отказ возвращается значением; исключением сообщается только ошибка программирования или
  непредвиденная ошибка startup, которая проецируется в application-отказ для единообразия исхода.
- Командной строки и справки у приложения нет: пользовательских опций запуска не вводится. Явно
  переданный путь файла конфигурации существует только как код-параметр composition — seam для
  проверок, а не продуктовая опция.

## Границы capability

- Не вводятся: `doctor` command как CLI surface, REPL, agent CLI, командная строка приложения,
  `config reload`, hot reload, файловые и rolling logs, OpenTelemetry exporter/backend, web/site/API,
  база данных, scheduler.
- Диагностика и startup не выполняют MuMu lifecycle: запуск, остановка и перезапуск экземпляра —
  отдельные операции, которые запрашивает presentation-слой, а не startup path.
- Логирование не является presentation-слоем; будущий REPL — отдельная presentation boundary.
- Диагностическая операция не является health-check автоматикой: она сообщает факты, а решение о
  продолжении запуска принимает startup.
