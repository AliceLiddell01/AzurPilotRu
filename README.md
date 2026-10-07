# AzurPilotRu

Персональная реализация проекта AzurPilot: Windows-only автоматизация на .NET/C# с отдельной native
частью на C++ и OpenCV, связанными узким versioned C ABI.

В репозитории собраны managed solution из трёх boundaries, native CMake boundary с OpenCV и
замороженный C ABI между ними, стандартный путь сборки и тестирования средствами CMake и .NET,
Windows CI и автоматизация обновления зависимостей. Приложение уже является application host:
строгая пользовательская конфигурация, application-level отказы, structured logging в `stderr` с
correlation identity, runtime-диагностика и две реальные Windows-возможности: MuMu (обнаружение
установки MuMuPlayer, выбор Android-экземпляра и host-side запуск, остановка и перезапуск с доказуемым
postcondition) и Android-слой (bundled ADB обнаруженной установки, точный ADB endpoint выбранного
экземпляра, готовность Android и lifecycle игры Azur Lane Global/EN с доказуемым postcondition). Ввод,
vision-пайплайн, OCR/ONNX, готовность UI игры, продуктовые CLI/REPL и agent CLI — будущие возможности: в
текущее приложение они не входят.

## Платформа

Windows, x64. Другие операционные системы не поддерживаются.

## Требования

| Что нужно | Требование |
| --- | --- |
| Windows | x64 |
| .NET SDK | Версия и политика выбора из [`global.json`](global.json) |
| Visual Studio | MSVC x64 compiler/toolset не ниже требований [`native/CMakeLists.txt`](native/CMakeLists.txt), workload «Desktop development with C++» |
| CMake | Не ниже требования [`native/CMakeLists.txt`](native/CMakeLists.txt); команда `cmake` должна быть доступна в `PATH` |
| Git | Доступен в `PATH` для тестов контрактов репозитория |

Пошаговая установка и диагностика — [docs/getting-started.md](docs/getting-started.md). Владельцы
закреплённых версий и внешних зависимостей перечислены в [.codex/context/INDEX.md](.codex/context/INDEX.md)
и описаны в [.codex/context/build-contracts.md](.codex/context/build-contracts.md).

## Канонические команды

```text
# Из native/
cmake --workflow --preset native-x64-release

# Из корня репозитория
dotnet restore AzurPilot.slnx --locked-mode
dotnet build AzurPilot.slnx --configuration Release --no-restore -warnaserror
dotnet test tests/AzurPilot.Tests/AzurPilot.Tests.csproj --configuration Release --no-restore --no-build
```

Native workflow выполняет configure (получение и проверку SHA256 OpenCV), build и CTest. Managed
команды восстанавливают NuGet-граф в locked mode, собирают solution с предупреждениями как ошибками
и запускают interop-тесты и тесты контрактов репозитория.

CI использует эти стандартные команды напрямую. Для native Debug предусмотрен workflow preset
`native-x64-debug`; подробности команд и проверок — [architecture.md](docs/architecture/overview.md)
и [verification.md](docs/testing/verification.md).

## Приложение при запуске

`AzurPilot.App` запускается как application host: собирает зависимости, загружает конфигурацию,
выполняет runtime-диагностику и печатает короткий человекочитаемый итог.

- Пользовательских опций запуска и командной строки нет: путь файла конфигурации startup вычисляет
  сам через его владельца, а каталог конфигурации приложение не создаёт.
- Отсутствие файла конфигурации — валидный сценарий запуска на встроенных defaults; существующий
  невалидный файл не подменяется defaults и завершает запуск явным отказом с ненулевым кодом выхода.
- В `stdout` уходит человекочитаемый итог, в `stderr` — structured runtime logs в JSON-формате с
  correlation identifier операции.
- Диагностика включает bounded MuMu-секцию: обнаружение установки, выбранный экземпляр и его
  наблюдённое состояние. Запуск приложения экземпляр MuMu не запускает и не останавливает.
- Диагностика включает bounded Android-секцию (доступность bundled ADB, точный endpoint выбранного
  экземпляра, состояние ADB transport, доступность shell, завершение загрузки Android и версия Android) и
  секцию состояния игры (product identity Global/EN, установлен ли пакет, запущен ли процесс, находится
  ли игра на переднем плане и выведенное из этих фактов состояние). Диагностика только читает: запуск
  приложения не подключает ADB, не запускает и не останавливает игру и эмулятор.

Путь конфигурации, правила схемы и коды отказа принадлежат
[application-configuration.md](docs/reference/application-configuration.md) и
[application-failures.md](docs/reference/application-failures.md), состав логирования и диагностики —
[runtime-diagnostics.md](docs/operations/runtime-diagnostics.md), правила MuMu-capability —
[mumu-lifecycle.md](docs/architecture/mumu-lifecycle.md), правила Android-слоя и lifecycle игры —
[android-game-lifecycle.md](docs/architecture/android-game-lifecycle.md), проверяемые свойства —
[verification.md](docs/testing/verification.md) и тесты `tests/AzurPilot.Tests/`.

## Структура репозитория

- `AzurPilot.slnx` — managed solution: `src/AzurPilot.Core`, `src/AzurPilot.Windows`, `src/AzurPilot.App`.
- `native/` — CMake boundary: C++ с OpenCV и C ABI, workflow presets, manifest и configure/staging helpers.
- `tests/` — тестовые проекты: interop и application-проверки, проба для негативной проверки native
  boundary и инструменты реальной приёмки MuMu и Android-слоя. Это инструменты проверки, а не boundaries
  приложения.
- `global.json`, `Directory.Packages.props` и `packages.lock.json` — манифесты зависимостей managed-проектов.
- `artifacts/` — единственная исключённая из Git область для build outputs и полученных зависимостей.

Карта проекта, границы и правило зависимостей — [.codex/context/architecture.md](docs/architecture/overview.md).

## Документация

- [docs/README.md](docs/README.md) — карта развёрнутой документации проекта.
- [docs/getting-started.md](docs/getting-started.md) — prerequisites, сборка, запуск и диагностика.
- [docs/architecture/android-game-lifecycle.md](docs/architecture/android-game-lifecycle.md) — подробная архитектура Android/ADB и lifecycle игры.
- [AGENTS.md](AGENTS.md) — минимальный глобальный контракт для coding-agent.
- [.codex/context/INDEX.md](.codex/context/INDEX.md) — маршрутизация короткого agent-critical контекста.

## Лицензия

AGPL-3.0 — см. [LICENSE](LICENSE).
