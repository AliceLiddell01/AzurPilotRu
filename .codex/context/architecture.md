# Архитектура проекта: карта и границы

Владелец: этот файл. Правила ниже имеют единственного владельца — здесь. Контракт версий и
acquisition зависимостей — [build-contracts.md](build-contracts.md), содержание проверок —
[verification.md](verification.md), форма C ABI — `native/include/azurpilot_native_abi.h`.

## Область и платформа

AzurPilotRu — персональная реализация AzurPilot: Windows-only, x64. Проект не кросс-платформенный:
другие операционные системы не поддерживаются, не эмулируются и не являются целью сборки. Managed
часть — .NET/C# (актуальный stable SDK из `eng/versions.json`, конкретные `TargetFramework`/RID —
владелец `Directory.Build.props`), native часть — C++ с OpenCV.

## Границы (boundaries)

Проект состоит из трёх managed boundaries и одной отдельной native boundary. Дробление на
дополнительные проекты без реальной границы ответственности запрещено.

| Boundary | Путь | Ответственность |
| --- | --- | --- |
| `AzurPilot.Core` | `src/AzurPilot.Core` | Доменные и application-контракты, логика, независимая от Windows |
| `AzurPilot.Windows` | `src/AzurPilot.Windows` | Windows-specific adapters/infrastructure, включая managed сторону native interop |
| `AzurPilot.App` | `src/AzurPilot.App` | Composition root и будущая host/presentation boundary |
| native | `native/` | Отдельная CMake boundary: C++ + OpenCV shared library `AzurPilot.Native.dll` с узким C ABI |

Managed boundaries приложения — ровно три: `AzurPilot.Core`, `AzurPilot.Windows`, `AzurPilot.App`.
Плюс одна отдельная native boundary. Проекты в каталоге `tests/` — тестовые инструменты, а не
boundaries: они не входят в состав поставляемых артефактов и не расширяют список boundaries.

Managed solution — `AzurPilot.slnx` в корне репозитория.

## Тестовые инструменты (не boundaries)

Проекты в `tests/` существуют только для проверок и не поставляются:

- `tests/AzurPilot.Tests` — интеграционные тесты, доказывающие interop boundary.
- `tests/AzurPilot.NativeAbsenceProbe` — исполняемая проба для негативной проверки: запускается
  тестом из каталога, где native библиотеки заведомо нет, и доказывает, что production-код interop
  сообщает об этом исключением. Отдельный процесс нужен потому, что загруженный модуль остаётся
  доступным до завершения процесса, и отсутствие файла в том же процессе невоспроизводимо.

Оба проекта — инструменты проверки. Они не являются boundary приложения, не входят в число трёх
managed boundaries, не поставляются как продукт и не должны описываться как boundary в `README.md`,
`AGENTS.md` и `docs/**`.

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

## Канонические entrypoints

Канонический путь сборки и проверки — repository-owned PowerShell, а не отдельные команды:

- `pwsh ./eng/build.ps1 -Configuration Release` — полный воспроизводимый путь: проверка toolchain →
  получение закреплённых native dependencies → CMake configure/build native части → native CTest →
  restore/build managed solution → staging native runtime в managed output → managed tests,
  доказывающие interop boundary.
- `pwsh ./eng/verify.ps1` — единый verification entrypoint; ненулевой exit code при любом нарушении
  build/test/analyzer/format контракта.

Прямые `dotnet`/`cmake` команды допустимы для локальной диагностики, но documented canonical path
проходит только через эти скрипты. CI вызывает те же скрипты и не содержит второй реализации
build logic: любая логика сборки живёт в `eng/`, а не в workflow.

## Структура репозитория

- `AzurPilot.slnx` — managed solution.
- `eng/` — repository-owned orchestration: `build.ps1`, `verify.ps1`, общие helpers и
  `eng/versions.json` (единственный источник версий).
- `native/` — CMake boundary: `CMakeLists.txt`, `CMakePresets.json`, `include/` (замороженный ABI),
  `src/` (реализация), `tests/` (native CTest).
- `src/` — managed проекты (три boundaries приложения), `tests/` — тестовые инструменты
  (не boundaries, см. выше).
- `artifacts/` — единственная ignored boundary для generated/build outputs и полученных
  native dependencies; source tree не засоряется, выходные каталоги не коммитятся.

## Конвенция staging native runtime

Native DLL и её runtime-зависимости (OpenCV DLL) попадают в выход managed проекта и managed теста
автоматически — через MSBuild `Content`/`Link` items, без ручного копирования после сборки.

- Источник задаётся свойством `AzurPilotNativeRuntimeDir`, которое выставляет `eng/build.ps1`
  (значение вычисляется из `eng/versions.json` и фактического расположения артефактов сборки).
- Путь в проектном файле не хардкодится; при отсутствии свойства сборка managed части не падает.
- Отсутствие native DLL в output не должно давать ложный успех: native-положительный тест обязан
  явно упасть с понятным сообщением.

## Запрет machine-specific путей

В репозитории запрещены абсолютные пути конкретной машины: домашний каталог пользователя, буква
диска, путь к конкретной установке Visual Studio, OpenCV, Python или иного локального инструмента
(в том числе как значение по умолчанию и как пример в коде). Всё
разрешается от корня репозитория (`$PSScriptRoot`, не текущая рабочая директория) и через
стандартные механизмы обнаружения toolchain (например `vswhere`, `PATH`, cache variables CMake).
Скрипты обязаны работать из любого текущего каталога.

## Продуктовые ограничения фундамента

- `1280x720` не является фундаментальным разрешением архитектуры: в foundation и native API нет и
  не должно быть такой константы или предполагаемого размера кадра. Будущий screenshot сохраняется
  в нативном разрешении, а размеры кадра приходят как данные, а не как константа проекта.
- MuMu-first, ADB, lifecycle эмулятора и игры, ввод (tap/swipe), vision-пайплайн, OCR/ONNX/GPU
  inference, конфигурация приложения, интерактивный REPL и agent CLI — будущие capability.
  В текущем фундаменте они не реализуются, не объявляются абстракциями «на будущее» и не имеют
  placeholder-документов.
