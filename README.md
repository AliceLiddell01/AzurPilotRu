# AzurPilotRu

Персональная реализация проекта AzurPilot: Windows-only автоматизация на .NET/C# с отдельной native
частью на C++ и OpenCV, связанными узким versioned C ABI.

В репозитории собран фундамент: managed solution из трёх boundaries, native CMake boundary с OpenCV,
замороженный C ABI между ними, стандартный путь сборки и тестирования средствами CMake и .NET,
Windows CI и автоматизация обновления зависимостей. MuMu, ADB, работа с игрой, vision-пайплайн,
OCR/ONNX, продуктовые CLI/REPL и agent CLI — будущие возможности: в текущий фундамент они не входят.

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
`native-x64-debug`; подробности команд и проверок — [architecture.md](.codex/context/architecture.md)
и [verification.md](.codex/context/verification.md).

## Структура репозитория

- `AzurPilot.slnx` — managed solution: `src/AzurPilot.Core`, `src/AzurPilot.Windows`, `src/AzurPilot.App`.
- `native/` — CMake boundary: C++ с OpenCV и C ABI, workflow presets, manifest и configure/staging helpers.
- `tests/` — тестовые проекты (interop тесты и проба для негативной проверки); это инструменты
  проверки, а не boundaries приложения.
- `global.json`, `Directory.Packages.props` и `packages.lock.json` — манифесты зависимостей managed-проектов.
- `artifacts/` — единственная исключённая из Git область для build outputs и полученных зависимостей.

Карта проекта, границы и правило зависимостей — [.codex/context/architecture.md](.codex/context/architecture.md).

## Документация

- [AGENTS.md](AGENTS.md) — контракт репозитория и router: с чего начинать чтение.
- [.codex/context/INDEX.md](.codex/context/INDEX.md) — таблица владельцев: какой документ владеет каким правилом.
- [docs/getting-started.md](docs/getting-started.md) — требования, сборка, тесты, диагностика.
- [.codex/context/build-contracts.md](.codex/context/build-contracts.md) — контракт версий и внешних зависимостей.
- [.codex/context/verification.md](.codex/context/verification.md) — что именно доказывает verification.

## Лицензия

AGPL-3.0 — см. [LICENSE](LICENSE).
