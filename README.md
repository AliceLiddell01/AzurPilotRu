# AzurPilotRu

Персональная реализация проекта AzurPilot: Windows-only автоматизация на .NET/C# с отдельной native
частью на C++ и OpenCV, связанными узким versioned C ABI.

В репозитории собран фундамент: managed solution из трёх boundaries, native CMake boundary с OpenCV,
замороженный C ABI между ними, канонический build/verify workflow, Windows CI и dependency
automation. MuMu, ADB, работа с игрой, vision-пайплайн, OCR/ONNX и интерактивный CLI — будущие
capability: в текущий фундамент они не входят.

## Платформа

Windows, x64. Другие операционные системы не поддерживаются.

## Prerequisites

| Что нужно | Требование |
| --- | --- |
| Windows | x64 |
| .NET SDK | линия 10 (LTS); точная версия — `dotnetSdk.version` |
| Visual Studio 2026 | toolset MSVC x64 не ниже `toolchain.msvcMinimumVersion`, workload «Desktop development with C++» |
| CMake | standalone, не ниже `toolchain.cmakeMinimumVersion`; CMake из состава Visual Studio отстаёт и не принимается как подстановка |
| PowerShell | 7+ (`pwsh`) |

Значения выше — не второй владелец номеров, а ссылки на поля [eng/versions.json](eng/versions.json):
этот файл — единственный владелец закреплённых версий, и он же печатает их при сборке на шаге
проверки toolchain. Пошаговая установка и диагностика — [docs/getting-started.md](docs/getting-started.md).

## Канонические команды

```pwsh
pwsh ./eng/build.ps1 -Configuration Release
pwsh ./eng/verify.ps1
```

Первая собирает всё: проверку toolchain, получение закреплённых native dependencies, native сборку с
native CTest, managed сборку и interop тесты. Вторая — единая verification: тот же build плюс
analyzers, project-owned warnings, code-style, согласованность закреплённых версий и git-гигиена; при
реальном нарушении она возвращает ненулевой код.

Прямые `dotnet`/`cmake` команды пригодны для диагностики, но documented путь проходит только через
эти скрипты: CI вызывает ровно их, второй реализации build-логики в репозитории нет.

## Структура репозитория

- `AzurPilot.slnx` — managed solution: `src/AzurPilot.Core`, `src/AzurPilot.Windows`, `src/AzurPilot.App`.
- `native/` — CMake boundary: C++ с OpenCV, экспортирующая C ABI.
- `tests/` — тестовые проекты (interop тесты и проба для негативной проверки); это инструменты
  проверки, а не boundaries приложения.
- `eng/` — repository-owned orchestration: `eng/build.ps1`, `eng/verify.ps1`, `eng/versions.json` и helpers.
- `artifacts/` — единственная ignored boundary для outputs сборки и полученных зависимостей.

Карта проекта, границы и правило зависимостей — [.codex/context/architecture.md](.codex/context/architecture.md).

## Документация

- [AGENTS.md](AGENTS.md) — контракт репозитория и router: с чего начинать чтение.
- [.codex/context/INDEX.md](.codex/context/INDEX.md) — таблица владельцев: какой документ владеет каким правилом.
- [docs/getting-started.md](docs/getting-started.md) — prerequisites, сборка, тесты, диагностика.
- [.codex/context/build-contracts.md](.codex/context/build-contracts.md) — контракт версий и внешних зависимостей.
- [.codex/context/verification.md](.codex/context/verification.md) — что именно доказывает verification.

## Лицензия

AGPL-3.0 — см. [LICENSE](LICENSE).
