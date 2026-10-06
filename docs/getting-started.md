# Начало работы

Практическая входная точка: что установить, как собрать проект и запустить проверки. Правила
проектной архитектуры и владельцы manifests перечислены в
[.codex/context/INDEX.md](../.codex/context/INDEX.md).

## 1. Требования

- **Windows x64** — единственная поддерживаемая платформа
  ([architecture.md](../.codex/context/architecture.md)).
- **Git** и **.NET SDK** — доступны из `PATH`. Выбранный .NET SDK задаёт [global.json](../global.json).
- **Visual Studio 2026** с workload «Desktop development with C++» и x64 MSVC toolset. Минимум
  compiler/toolset и CMake указан в [native/CMakeLists.txt](../native/CMakeLists.txt).
- **CMake** не ниже указанного в `native/CMakeLists.txt`; команда `cmake` должна разрешаться через
  `PATH`.
- Доступ к сети требуется при первом получении OpenCV. Ручная установка не нужна: CMake configure
  скачивает пакет из [native/opencv.json](../native/opencv.json).

Версии NuGet и принципы обновления описаны в
[build-contracts.md](../.codex/context/build-contracts.md). Точный перечень владельцев находится в
[INDEX.md](../.codex/context/INDEX.md).

## 2. Сборка и тесты

Для Release выполните native workflow из каталога `native/`, затем managed команды из корня
репозитория:

```text
# Из native/
cmake --workflow --preset native-x64-release

# Из корня репозитория
dotnet restore AzurPilot.slnx --locked-mode
dotnet build AzurPilot.slnx --configuration Release --no-restore -warnaserror
dotnet test tests/AzurPilot.Tests/AzurPilot.Tests.csproj --configuration Release --no-restore --no-build
```

Для локальной Debug-проверки используйте `cmake --workflow --preset native-x64-debug`, затем замените
`Release` на `Debug` в managed-командах. CI вызывает стандартные команды Release напрямую из
соответствующих каталогов.

## 3. Что происходит при сборке

1. CMake configure читает OpenCV pin, загружает архив при необходимости, проверяет SHA256 и layout.
2. CMake build собирает DLL и smoke test; затем CTest запускает native ABI smoke test.
3. Native build stages production DLL и runtime DLL OpenCV в
   `artifacts/native/runtime/<Configuration>`.
4. `dotnet restore --locked-mode` восстанавливает закреплённый lock-файлами граф NuGet.
5. `dotnet build` собирает solution и передаёт warnings как ошибки.
6. `dotnet test` запускает interop и repository contract tests без повторной сборки.

MSBuild берёт runtime из `artifacts/native/runtime/<Configuration>` и добавляет DLL в managed outputs.
Если там нет `AzurPilot.Native.dll` или runtime DLL OpenCV, MSBuild останавливает managed build с
ошибкой. Подробнее о каноническом пути и границах — [architecture.md](../.codex/context/architecture.md)
и [verification.md](../.codex/context/verification.md).

## 4. OpenCV

Acquisition выполняется автоматически при CMake configure. Manifest
[native/opencv.json](../native/opencv.json) владеет версией, URL, SHA256 и относительными путями
внутри архива.

- Новый и кэшированный архив проверяется по SHA256 до распаковки.
- Архив распаковывается в `artifacts/opencv/<version>`; configure проверяет `OpenCVConfig.cmake`
  и runtime layout.
- CI может кэшировать архив в `artifacts/downloads`, но configure всё равно проверяет его hash.
- При несовпадении SHA256 или layout configure завершается ошибкой; сборка не продолжается.

Правила обновления и ответственность за проверку нового hash описаны в
[build-contracts.md](../.codex/context/build-contracts.md).

## 5. Артефакты

Генерируемые файлы находятся в `artifacts/` и стандартных managed `bin`/`obj`; их не коммитят.

| Путь | Содержимое |
| --- | --- |
| `artifacts/downloads` | Загруженный архив OpenCV |
| `artifacts/opencv/<version>` | Распакованный OpenCV |
| `artifacts/native/cmake` | CMake configure/build и CTest metadata |
| `artifacts/native/bin/<Configuration>` | Native DLL, PDB и smoke test |
| `artifacts/native/runtime/<Configuration>` | DLL native runtime для managed outputs |
| `artifacts/native/abi-mismatch/<Configuration>` | Изолированная DLL для негативного ABI теста |
| `src/**/bin`, `tests/**/bin` | Managed assemblies и тестовые outputs |

## 6. Запуск приложения

`AzurPilot.App` — application host: после сборки Release он запускается без параметров, сам собирает
зависимости, загружает конфигурацию и выполняет runtime-диагностику. Командной строки и
пользовательских опций запуска у приложения нет.

Путь файла конфигурации вычисляет его владелец `AzurPilotConfigurationPath`, а каталог приложение не
создаёт. Если файла нет, запуск идёт на встроенной конфигурации по умолчанию. Существующий невалидный
файл не подменяется defaults: запуск завершается явным отказом и ненулевым кодом выхода.

Путь файла и правила схемы принадлежат
[application-configuration.md](../.codex/context/application-configuration.md), коды отказа и коды
выхода процесса — [application-failures.md](../.codex/context/application-failures.md).

Что видно при запуске:

- `stdout` — человекочитаемый итог: identity сборки, runtime и процесса, источник и версия схемы
  конфигурации, фактические сведения native boundary (версия ABI, версия OpenCV, capability),
  строка MuMu-секции (обнаружение установки, выбранный экземпляр и его наблюдённое состояние) и итог
  запуска. Startup MuMu не запускает и не останавливает;
- `stderr` — structured runtime logs в JSON-формате; каждая запись несёт correlation identifier
  операции. Логи в `stdout` не попадают, поэтому вывод остаётся presentation surface.

Проверить это поведение целиком, включая границу `stdout`/`stderr`, можно командой из раздела 2:
`dotnet test` запускает реальный процесс приложения вместе с его диагностикой.

## 7. Проверки

- **Native CTest** запускает project-owned код через C ABI, проверяет версию ABI и OpenCV, факт
  выполнения OpenCV, capabilities, повторный вызов и границы буферов.
- **Managed interop tests** загружают реальную native DLL через source-generated `LibraryImport`.
  Негативные тесты проверяют явный отказ без production DLL и при несовместимом ABI.
- **Application-проверки** доказывают строгую конфигурацию, проекцию отказов native boundary, состав
  application host, correlation identity, содержимое диагностики и границу `stdout`/`stderr`.
- **MuMu-проверки** прогоняют production-код capability через управляемые внешние границы и доказывают
  orchestration lifecycle без установленной MuMu; поведение реальной установки доказывает отдельная
  локальная приёмка, которая требует установленной MuMuPlayer и в hosted CI не запускается:
  `dotnet run --project tests/AzurPilot.MuMuAcceptance -c Release -- --instance mumu:<index>`.
  Приёмка собирается вместе с `AzurPilot.Windows`, поэтому до её запуска нужен native runtime этой
  конфигурации: из каталога `native` выполните CMake workflow preset `native-x64-release`. Что именно
  доказывает каждая проверка — [verification.md](../.codex/context/verification.md).
- **Repository contract tests** проверяют отсутствие machine-specific абсолютных путей,
  фундаментального hardcode `1280x720`, Git-visible binaries и build outputs.

Не запускайте managed interop tests без native workflow соответствующей конфигурации: MSBuild
намеренно завершится ошибкой, если native runtime не собран.

## 8. Диагностика

| Симптом | Что проверить |
| --- | --- |
| Не найден MSVC x64 toolset или compiler ниже минимума | Установите Visual Studio 2026 с workload «Desktop development with C++» и проверьте требования в `native/CMakeLists.txt` |
| CMake не найден или ниже минимума | Добавьте подходящий CMake в `PATH`; минимум принадлежит `native/CMakeLists.txt` |
| Не найден требуемый .NET SDK | Установите SDK согласно `global.json` |
| Locked restore завершился ошибкой | Сверьте `Directory.Packages.props` и lock-файлы; изменение графа должно обновлять их согласованно |
| SHA256 OpenCV не совпал | Не распаковывайте архив; проверьте URL/hash в `native/opencv.json` и удалите повреждённый архив из локального `artifacts/downloads` перед повторным configure |
| Managed build не нашёл native runtime DLL | Запустите CMake workflow preset той же конфигурации из `native/` |
| Приложение завершилось ненулевым кодом выхода | Прочитайте код отказа в итоге на `stdout` и в structured logs на `stderr`; значения кодов принадлежат [application-failures.md](../.codex/context/application-failures.md), правила схемы — [application-configuration.md](../.codex/context/application-configuration.md) |
| `dotnet test` завершился с кодом 5 из-за неизвестной опции | Проект использует `Microsoft.Testing.Platform`; не передавайте неподдерживаемые runner options, например `--nologo` |
| MuMu-секция сообщает, что установка не обнаружена или экземпляр не выбран | Это диагностический результат, а не отказ запуска: startup MuMu не запускает и не останавливает, а правила принадлежат [mumu-lifecycle.md](../.codex/context/mumu-lifecycle.md) |

## 9. Куда смотреть дальше

- [README.md](../README.md) — назначение проекта, build/test-путь и поведение при запуске.
- [AGENTS.md](../AGENTS.md) — корневой контракт и router для агентов.
- [.codex/context/INDEX.md](../.codex/context/INDEX.md) — владельцы правил и manifests.
- [architecture.md](../.codex/context/architecture.md) — boundaries, source of truth, references, build/runtime boundaries.
- [build-contracts.md](../.codex/context/build-contracts.md) — version ownership, OpenCV acquisition и Renovate.
- [application-configuration.md](../.codex/context/application-configuration.md) — схема конфигурации, путь файла и правила загрузки.
- [application-failures.md](../.codex/context/application-failures.md) — коды отказа и коды выхода процесса.
- [runtime-diagnostics.md](../.codex/context/runtime-diagnostics.md) — composition, логирование, correlation и диагностика.
- [mumu-lifecycle.md](../.codex/context/mumu-lifecycle.md) — MuMu-capability: установка, экземпляр и lifecycle.
- [verification.md](../.codex/context/verification.md) — что доказывают проверки.
