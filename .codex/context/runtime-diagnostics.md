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
  отвергнутого файла применять нельзя;
- штатный lifetime-шум generic host поднят до предупреждений: порог повышается, а не скрывает сбои.

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
- identifier попадает в каждую запись structured log: и как свойство события, и в scope записи.

Не подключаются OpenTelemetry, exporter, collector, метрики и persistence telemetry: нужен только
локальный стандартный seam, к которому позже можно подключить exporter без изменения остального кода.

## Runtime diagnostic snapshot

Диагностическая операция — `AzurPilotDiagnosticService` (`src/AzurPilot.App/Diagnostics/`): она
используется текущим startup path, возвращает данные и ничего не печатает, поэтому будущее
`doctor`-представление сможет её переиспользовать, не меняя состав диагностики.

Snapshot — `AzurPilotDiagnosticReport` с тремя bounded секциями:

- application: identity сборки application host, .NET runtime и его identifier, описание
  операционной системы и её архитектура, архитектура процесса, идентификатор процесса, признак
  64-битного процесса;
- configuration: источник (встроенные defaults или файл), версия схемы, статус валидации, минимальный
  уровень логирования и путь файла. Полного дампа документа конфигурации в snapshot нет;
- native: доступность native boundary, совместимость ABI и обязательных capability, фактическая
  версия ABI, фактическая версия OpenCV, подтверждённые capability, факт реального исполнения кода
  OpenCV, строка сведений о сборке native библиотеки, причина совместимости и application-отказ, если
  границу использовать нельзя.

Недоступная или несовместимая native boundary — результат диагностики, а не исключение: ошибка
границы проецируется в application-отказ production-маппером, полученным из DI, и попадает в
native-секцию как данные ([application-failures.md](application-failures.md)).

## Startup и отказы

- Ожидаемый отказ (невалидная конфигурация, недоступная или несовместимая native boundary) завершает
  запуск явным предсказуемым ненулевым кодом выхода и не маскируется как здоровый запуск.
  Соответствие «код отказа → код выхода процесса» принадлежит
  [application-failures.md](application-failures.md).
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
- Логирование не является presentation-слоем; будущий REPL — отдельная presentation boundary.
- Диагностическая операция не является health-check автоматикой: она сообщает факты, а решение о
  продолжении запуска принимает startup.
