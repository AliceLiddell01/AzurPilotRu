# Архитектура проекта: карта и границы — подробная документация

Этот документ сохраняет развёрнутое описание текущего устройства, rationale, примеры и evidence.
Короткий agent-critical contract находится в
[.codex/context/architecture.md](../../.codex/context/architecture.md).
Фактическое состояние production-кода, tests и manifests имеет приоритет над устаревшей prose-документацией.

## Область и платформа

AzurPilotRu — персональная реализация AzurPilot и текущий источник истины для своего кода, сборки и
инструментов репозитория: Windows-only, x64. Проект не кросс-платформенный: другие операционные
системы не поддерживаются, не эмулируются и не являются целью сборки. Managed часть — .NET/C# (SDK
задаётся `global.json`, `TargetFramework` и RID — владелец `Directory.Build.props`), native часть —
C++ с OpenCV.

`AliceLiddell01/Anki-decks` служит архитектурным ориентиром для инструментов репозитория и
инфраструктуры агентов. `AliceLiddell01/AzurPilot-private-Ru` служит ориентиром для поведения и
предметной области, но не шаблоном архитектуры. Ни один из этих репозиториев не заменяет AzurPilotRu
как текущий источник истины.

Продуктовая часть приложения во время выполнения отделена от инструментов репозитория. Команды
`cmake`, `dotnet`, CI и автоматизация обновления зависимостей обслуживают разработку репозитория, а не
являются возможностями продукта. Будущие продуктовые CLI, REPL и agent CLI не должны включать общие
операции инструментов репозитория: `build`, `repair` и `update`.

## Архитектурные границы

Проект состоит из трёх managed boundaries и одной отдельной native boundary. Дробление на
дополнительные проекты без реальной границы ответственности запрещено.

| Boundary | Путь | Ответственность |
| --- | --- | --- |
| `AzurPilot.Core` | `src/AzurPilot.Core` | Доменные и application-контракты, логика, независимая от Windows, включая MuMu-контракты и orchestration lifecycle, а также контракты Android и orchestration ADB readiness и lifecycle игры |
| `AzurPilot.Windows` | `src/AzurPilot.Windows` | Windows-specific adapters/infrastructure, включая managed сторону native interop, MuMu-adapter и Android-adapter: bundled ADB, разрешение точного endpoint и запуск команд ADB |
| `AzurPilot.App` | `src/AzurPilot.App` | Composition root и application host: сборка host-а, startup, structured logging, correlation, runtime-диагностика и интеграция MuMu- и Android-capability. Командной строки у приложения нет; presentation boundary (REPL, CLI) не реализована |
| native | `native/` | Отдельная CMake boundary: C++ + OpenCV shared library `AzurPilot.Native.dll` с узким C ABI |

Managed boundaries приложения — ровно три: `AzurPilot.Core`, `AzurPilot.Windows`, `AzurPilot.App`.
Плюс одна отдельная native boundary. Проекты в каталоге `tests/` — тестовые инструменты, а не
boundaries: они не входят в состав поставляемых артефактов и не расширяют список boundaries.

Managed solution — `AzurPilot.slnx` в корне репозитория.

## Тестовые проекты (не границы приложения)

Проекты в `tests/` существуют только для проверок и не поставляются:

- `tests/AzurPilot.Tests` — основной проект проверок: interop boundary, строгая конфигурация,
  application-level отказы, MuMu-capability, Android/ADB readiness вместе с lifecycle игры и поведение
  application host. Часть проверок выполняется на реальном процессе приложения, часть — в процессе
  самого теста; что именно доказывается, принадлежит [verification.md](../testing/verification.md).
- `tests/AzurPilot.NativeAbsenceProbe` — исполняемая проба, которая запускается тестом отдельным
  процессом и используется в двух режимах: негативная проверка загрузки native boundary (библиотеки
  нет или подложена fixture с несовместимым ABI) и application startup через composition root с явно
  переданным путём конфигурации. Во втором режиме проба сообщает полученный код выхода приложения и
  стабильный application failure code; её собственный код выхода описывает только корректность
  прогона, а не результат запуска.
- `tests/AzurPilot.MuMuAcceptance` — исполняемый инструмент реальной приёмки MuMu-capability на
  Windows-машине с установленной MuMuPlayer. Он собирает production-поверхность capability
  (`IMuMuHost` из `AzurPilot.Windows` и orchestration lifecycle из `AzurPilot.Core`) и выполняет
  lifecycle-матрицу с восстановлением начального состояния; аргументы разбирает только он сам, а в
  hosted CI он не запускается и в состав продуктовых артефактов не входит.
- `tests/AzurPilot.AndroidAcceptance` — исполняемый инструмент реальной приёмки Android-слоя: exact ADB
  endpoint выбранного экземпляра, готовность Android, пакет игры Global/EN и lifecycle игры с
  восстановлением начального состояния. Он собирает production-поверхность Android (`IAndroidHost`,
  `AndroidReadinessService`, `AzurLaneGameStateService` и `AzurLaneGameLifecycleService`) и требует явно
  выбранный exact instance; аргументы разбирает только он сам, а в hosted CI он не запускается и в состав
  продуктовых артефактов не входит.

Отдельный процесс нужен первым двум режимам по одной причине: загруженный native модуль остаётся
доступным до завершения процесса, поэтому отсутствие библиотеки в процессе теста невоспроизводимо.
Второй режим — режим самой пробы, а не пользовательская опция приложения: продукт аргументов запуска
не разбирает, поэтому startup с заданной конфигурацией и полностью управляемым каталогом прогона
вызывается кодом composition, а не командной строкой. Пользовательский
`%LOCALAPPDATA%\AzurPilot\config.json` при этом не читается.

Все проекты в `tests/` — инструменты проверки. Они не являются boundary приложения, не входят в число
трёх managed boundaries, не поставляются как продукт и не должны описываться как boundary в `README.md`,
`AGENTS.md` и `docs/**`. Инструменты реальной приёмки MuMu и Android — тоже тестовые инструменты, а не
продуктовая командная строка: пользовательских команд у приложения они не добавляют.

## Runtime-контракты приложения

Приложение уже имеет реальные runtime-контракты: строгую пользовательскую конфигурацию,
application-level модель отказов, structured logging с correlation identity,
runtime-диагностический snapshot и реальные Windows-возможности: MuMu-capability (обнаружение установки,
выбор Android-экземпляра и host-side lifecycle с доказуемым postcondition) и Android-слой (bundled ADB,
точный ADB endpoint выбранного экземпляра, готовность Android и lifecycle игры Azur Lane Global/EN с
доказуемым postcondition). Их правила имеют собственных владельцев —
[application-configuration.md](../reference/application-configuration.md),
[application-failures.md](../reference/application-failures.md), [runtime-diagnostics.md](../operations/runtime-diagnostics.md),
[mumu-lifecycle.md](mumu-lifecycle.md) и [android-game-lifecycle.md](android-game-lifecycle.md); здесь они
не повторяются. Пользовательских опций запуска и командной строки у приложения нет: startup вычисляет
runtime-путь конфигурации сам. Границы остаются прежними: `AzurPilot.Core` владеет контрактами
конфигурации, отказов, MuMu и Android, не зависящими от Windows, `AzurPilot.Windows` — проекцией ошибок
платформенной/native boundary, MuMu-adapter и Android-adapter, `AzurPilot.App` — composition и
диагностикой.

## Правило зависимостей

- Направление managed-зависимостей: `AzurPilot.App` → `AzurPilot.Windows` → `AzurPilot.Core`.
- `AzurPilot.Core` не зависит ни от одного другого проекта репозитория и не знает о Windows API.
- `AzurPilot.Windows` не зависит от `AzurPilot.App`. Обратные ссылки запрещены.
- Managed сторона не обращается к native коду напрямую: единственный канал — C ABI из
  `native/include/azurpilot_native_abi.h`, вызываемый через source-generated `LibraryImport`.
- Native boundary не знает о .NET и не зависит от managed кода; никаких managed-типов и
  managed runtime в native части.
- Native boundary не зависит от тестового и managed кода; зависимости native targets —
  только OpenCV и стандартная библиотека C++.
- Через границу не проходят `cv::Mat`, STL-типы, C++-исключения или произвольные pixel pointers.
  Единственное владение native memory передаётся typed opaque frame handle с парным release export-ом;
  форма границы описана в заголовке ABI и меняется только вместе с номером ABI. Подробности —
  [native-frame-ownership.md](native-frame-ownership.md).
- MuMu-capability не меняет направление зависимостей: доменные контракты и orchestration lifecycle
  живут в `AzurPilot.Core`, Windows-адаптер control surface — в `AzurPilot.Windows`, интеграция в
  host — в `AzurPilot.App`. Core не знает о Windows API и не заводит общий filesystem/process layer:
  доступ к реестру, файловой системе и запуску процессов приходит через объявленные им узкие границы.
- Android-capability не меняет направление зависимостей и не заводит второго слоя для той же
  ответственности: контракты Android и orchestration ADB readiness и lifecycle игры живут в
  `AzurPilot.Core`, Android-adapter (bundled ADB, разрешение endpoint, форма команд, разбор ответа) — в
  `AzurPilot.Windows`, интеграция в host — в `AzurPilot.App`. Второй device framework и второй process
  framework не вводятся: запуск процесса идёт через одну общую границу, а смысл её отказов принадлежит
  владельцу возможности, поэтому граница регистрируется по одному разу на владельца со своей проекцией,
  а не переписывается второй реализацией.

## Канонический путь сборки и тестирования

Native configure, build и CTest выполняются через workflow preset из каталога `native/`:

```text
cmake --workflow --preset native-x64-release
```

Для Debug используется preset `native-x64-debug`. Workflow configure читает OpenCV pin из
`native/opencv.json`, получает пакет при необходимости, проверяет SHA256 и layout, затем workflow
собирает native targets и запускает CTest. Имена generator и workflow presets принадлежат
`native/CMakePresets.json`; минимальная версия CMake и compiler/toolset floor —
`native/CMakeLists.txt`.

После native workflow из корня репозитория выполняется managed часть:

```text
dotnet restore AzurPilot.slnx --locked-mode
dotnet build AzurPilot.slnx --configuration Release --no-restore -warnaserror
dotnet test tests/AzurPilot.Tests/AzurPilot.Tests.csproj --configuration Release --no-restore --no-build
```

Версии .NET SDK и NuGet пакетов принадлежат `global.json`, `Directory.Packages.props` и lock-файлам.
CI напрямую выполняет приведённые команды CMake и .NET. Отдельной точки входа PowerShell и второй
реализации build logic в workflow нет.

## Структура репозитория

- `AzurPilot.slnx` — managed solution.
- `native/` — CMake boundary: `CMakeLists.txt`, `CMakePresets.json`, `opencv.json`, `cmake/`,
  `include/` (замороженный ABI), `src/` (реализация), `tests/` (native CTest).
- `global.json`, `Directory.Packages.props` и `**/packages.lock.json` — владельцы .NET SDK и графа
  NuGet-зависимостей; подробная карта владельцев находится в
  [.codex/context/build-contracts.md](../../.codex/context/build-contracts.md).
- `.github/workflows/ci.yml` — CI, который вызывает стандартные CMake/.NET команды.
- `src/` — managed проекты (три boundaries приложения), `tests/` — тестовые инструменты
  (не boundaries, см. выше).
- `artifacts/` — единственная ignored boundary для generated/build outputs и полученных
  native dependencies; source tree не засоряется, выходные каталоги не коммитятся.

## Конвенция staging native runtime

Native build помещает DLL native boundary и runtime DLL OpenCV в
`artifacts/native/runtime/<Configuration>`. `Directory.Build.targets` берёт их оттуда и добавляет в
выходные каталоги managed проектов через MSBuild `Content` items. Не требуется задавать свойство или
копировать DLL вручную.

MSBuild работает по принципу fail-closed и перед managed build проверяет, что в staging присутствуют
`AzurPilot.Native.dll` и runtime DLL OpenCV. Если отсутствует любая из них, сборка завершается
понятной ошибкой; native interop test не может дать ложный успех без production DLL.

## Запрет путей конкретной машины

В репозитории запрещены абсолютные пути конкретной машины: домашний каталог пользователя, буква
диска, путь к конкретной установке Visual Studio, OpenCV, Python или иного локального инструмента
(в том числе как значение по умолчанию и как пример в коде). CMake и MSBuild разрешают пути сборки
относительно манифестов репозитория и стандартных механизмов обнаружения toolchain; абсолютные пути
локальной машины не фиксируются в исходниках и конфигурации.

## Продуктовые ограничения текущего этапа

- `1280x720` не является фундаментальным разрешением архитектуры: в фундаменте и native API нет и
  не должно быть такой константы или предполагаемого размера кадра. Будущий screenshot сохраняется
  в нативном разрешении, а размеры кадра приходят как данные, а не как константа проекта.
- MuMu-capability реализована как реальная Windows-возможность: обнаружение установки MuMuPlayer,
  стабильная identity и выбор Android-экземпляра, host-side состояние экземпляра и безопасные
  start/stop/restart с доказуемым postcondition. Правила принадлежат
  [mumu-lifecycle.md](mumu-lifecycle.md); секция `mumu` конфигурации, MuMu-коды отказа, MuMu-секция
  диагностики и проверки существуют вместе с этой capability.
- Android-слой реализован как следующая реальная Windows-возможность: bundled ADB обнаруженной установки,
  точный ADB endpoint выбранного экземпляра, готовность Android и lifecycle игры Azur Lane Global/EN с
  доказуемым postcondition. Правила принадлежат
  [android-game-lifecycle.md](android-game-lifecycle.md); Android-коды отказа, секции Android и состояния
  игры в диагностике и проверки существуют вместе с этой capability, а продуктовые настройки для неё не
  вводятся: product identity и endpoint — runtime-данные, а не значения схемы.
- Native boundary поддерживает decode PNG bytes из памяти в native-owned RGB8 frame; это не добавляет
  screenshot capture, ADB/MuMu вызов, region scan или pixel recognition.
- Отсутствуют и не объявляются абстракциями «на будущее»: ввод (tap/swipe/keymap), screenshot capture и
  vision pipeline, OCR/ONNX/GPU inference, готовность UI игры, product CLI, REPL и agent CLI. Для них не
  создаются placeholder-документы, секции конфигурации, коды отказов и диагностические секции. Общие
  команды `build`, `repair` и `update` относятся к инструментам репозитория, а не к будущим product CLI,
  REPL и agent CLI.
