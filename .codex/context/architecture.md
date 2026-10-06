# Архитектура проекта: карта и границы

Владелец: этот файл. Правила ниже имеют единственного владельца — здесь. Владельцы version pins и
acquisition зависимостей описаны в [build-contracts.md](build-contracts.md) и
[INDEX.md](INDEX.md), пользовательская конфигурация — в
[application-configuration.md](application-configuration.md), runtime-логирование и диагностика — в
[runtime-diagnostics.md](runtime-diagnostics.md), application-level отказы — в
[application-failures.md](application-failures.md), содержание проверок — [verification.md](verification.md),
форма C ABI — `native/include/azurpilot_native_abi.h`.

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
| `AzurPilot.Core` | `src/AzurPilot.Core` | Доменные и application-контракты, логика, независимая от Windows |
| `AzurPilot.Windows` | `src/AzurPilot.Windows` | Windows-specific adapters/infrastructure, включая managed сторону native interop |
| `AzurPilot.App` | `src/AzurPilot.App` | Composition root и application host: сборка host-а, startup, structured logging, correlation и runtime-диагностика. Командной строки у приложения нет; presentation boundary (REPL, CLI) не реализована |
| native | `native/` | Отдельная CMake boundary: C++ + OpenCV shared library `AzurPilot.Native.dll` с узким C ABI |

Managed boundaries приложения — ровно три: `AzurPilot.Core`, `AzurPilot.Windows`, `AzurPilot.App`.
Плюс одна отдельная native boundary. Проекты в каталоге `tests/` — тестовые инструменты, а не
boundaries: они не входят в состав поставляемых артефактов и не расширяют список boundaries.

Managed solution — `AzurPilot.slnx` в корне репозитория.

## Тестовые проекты (не границы приложения)

Проекты в `tests/` существуют только для проверок и не поставляются:

- `tests/AzurPilot.Tests` — основной проект проверок: interop boundary, строгая конфигурация,
  application-level отказы и поведение application host. Часть проверок выполняется на реальном
  процессе приложения, часть — в процессе самого теста; что именно доказывается, принадлежит
  [verification.md](verification.md).
- `tests/AzurPilot.NativeAbsenceProbe` — исполняемая проба, которая запускается тестом отдельным
  процессом и используется в двух режимах: негативная проверка загрузки native boundary (библиотеки
  нет или подложена fixture с несовместимым ABI) и application startup через composition root с явно
  переданным путём конфигурации. Во втором режиме проба сообщает полученный код выхода приложения и
  стабильный application failure code; её собственный код выхода описывает только корректность
  прогона, а не результат запуска.

Отдельный процесс нужен обоим режимам по одной причине: загруженный native модуль остаётся доступным
до завершения процесса, поэтому отсутствие библиотеки в процессе теста невоспроизводимо. Второй режим
— режим самой пробы, а не пользовательская опция приложения: продукт аргументов запуска не разбирает,
поэтому startup с заданной конфигурацией и полностью управляемым каталогом прогона вызывается кодом
composition, а не командной строкой. Пользовательский `%LOCALAPPDATA%\AzurPilot\config.json` при этом
не читается.

Оба проекта — инструменты проверки. Они не являются boundary приложения, не входят в число трёх
managed boundaries, не поставляются как продукт и не должны описываться как boundary в `README.md`,
`AGENTS.md` и `docs/**`.

## Runtime-контракты приложения

Приложение уже имеет реальные runtime-контракты: строгую пользовательскую конфигурацию схемы v1,
application-level модель отказов, structured logging с correlation identity и runtime-диагностический
snapshot. Их правила имеют собственных владельцев — [application-configuration.md](application-configuration.md),
[application-failures.md](application-failures.md) и [runtime-diagnostics.md](runtime-diagnostics.md);
здесь они не повторяются. Пользовательских опций запуска и командной строки у приложения нет:
startup вычисляет runtime-путь конфигурации сам. Границы остаются прежними: `AzurPilot.Core` владеет
контрактами конфигурации и отказов, не зависящими от Windows, `AzurPilot.Windows` — проекцией ошибок
платформенной/native boundary, `AzurPilot.App` — composition и диагностикой.

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
- Через границу не проходят `cv::Mat`, STL-типы, C++-исключения и владеющие указатели:
  форма границы описана в заголовке ABI и меняется только вместе с номером ABI.

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
  NuGet-зависимостей; подробная карта владельцев находится в [.codex/context/INDEX.md](INDEX.md).
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
- MuMu-first, ADB, lifecycle эмулятора и игры, ввод (tap/swipe), vision-пайплайн, OCR/ONNX/GPU
  inference, product CLI, REPL и agent CLI — будущие capability. Они не реализуются, не объявляются
  абстракциями «на будущее» и не имеют placeholder-документов; секции конфигурации, коды отказов и
  диагностические секции для них не создаются заранее. Общие команды `build`, `repair` и `update`
  относятся к инструментам репозитория, а не к будущим product CLI, REPL и agent CLI.
