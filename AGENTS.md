# AGENTS.md — контракт репозитория и маршрутизация

Короткий root-контракт для агентов и разработчиков. Это не энциклопедия: правила живут у своих
владельцев, а таблица владельцев — [.codex/context/INDEX.md](.codex/context/INDEX.md). Перед любой
работой откройте её и прочитайте документ, который владеет нужным правилом.

## Что это за репозиторий

AzurPilotRu — персональная реализация AzurPilot: Windows-only x64, managed часть на .NET/C#, тяжёлая
image/vision часть — отдельная native DLL на C++ с OpenCV, связь между ними — узкий versioned C ABI.
Репозиторные инструменты разработчика отделены от product runtime. Приложение уже имеет application
host, строгую пользовательскую конфигурацию, application-level отказы, structured logging с
correlation, runtime-диагностику и первую реальную Windows-возможность — MuMu-capability: обнаружение
установки MuMuPlayer, выбор Android-экземпляра и host-side start/stop/restart с доказуемым
postcondition. ADB и device readiness, lifecycle игры, ввод, vision-пайплайн, OCR/ONNX, product
CLI/REPL и agent CLI — будущие capability: в текущем приложении их нет.

## Минимальные инварианты (до чтения контекста)

- Windows-only, x64. Другие платформы не поддерживаются.
- Managed boundaries приложения — ровно три: `AzurPilot.Core`, `AzurPilot.Windows`, `AzurPilot.App`,
  плюс одна отдельная native boundary. Проекты в `tests/` — тестовые инструменты, не boundaries.
- Канонический Release-путь: из `native/` выполнить `cmake --workflow --preset native-x64-release`,
  затем из корня выполнить:
  `dotnet restore AzurPilot.slnx --locked-mode`,
  `dotnet build AzurPilot.slnx --configuration Release --no-restore -warnaserror` и
  `dotnet test tests/AzurPilot.Tests/AzurPilot.Tests.csproj --configuration Release --no-restore --no-build`.
  CI выполняет эти стандартные команды напрямую; отдельная точка входа PowerShell и вторая
  реализация логики сборки не вводятся. Локальная Debug-проверка использует preset
  `native-x64-debug`; в командах `dotnet build` и `dotnet test` замените `--configuration Release`
  на `--configuration Debug`.
- Каждый закреплённый version value принадлежит своему manifest из
  [.codex/context/INDEX.md](.codex/context/INDEX.md). Один номер — один владелец; не копируйте значения
  между владельцами.
- Machine-specific абсолютные пути (домашний каталог, буква диска, путь к конкретной установке
  Visual Studio/OpenCV/Python) в репозитории запрещены.
- Project-owned комментарии, диагностика и документация — на русском; идентификаторы, ключи
  конфигурации и общепринятые технические термины — латиницей.
- Одна самостоятельная публикуемая задача — одна рабочая ветка и один PR; напрямую в default
  branch разработка не ведётся. Git/GitHub lifecycle принадлежит
  [`.agents/skills/azurpilot-git-workflow/`](.agents/skills/azurpilot-git-workflow/): локальный
  commit не завершает публикацию, а merge выполняется только по отдельной текущей явной команде.
- CodeRabbit review запускается только по явному положительному запросу пользователя; его процедура
  принадлежит [`.agents/skills/azurpilot-coderabbit-review/`](.agents/skills/azurpilot-coderabbit-review/).
  Изменение skill или `.coderabbit.yaml` само по себе не разрешает запуск провайдера.

## Маршрутизация

| Вопрос | Владелец правила |
| --- | --- |
| Какой документ владеет каким правилом | [.codex/context/INDEX.md](.codex/context/INDEX.md) |
| Карта проекта, boundaries, правило зависимостей, entrypoints, staging, ограничения | [.codex/context/architecture.md](.codex/context/architecture.md) |
| Владельцы version pins, acquisition зависимостей, checksum, Renovate и диагностика toolchain | [.codex/context/build-contracts.md](.codex/context/build-contracts.md) и [.codex/context/INDEX.md](.codex/context/INDEX.md) |
| Схема конфигурации, runtime-путь файла конфигурации, правила загрузки, строгой валидации и нормализации legacy-входа, владелец defaults и отсутствие hot reload | [.codex/context/application-configuration.md](.codex/context/application-configuration.md) |
| Стабильные application-коды отказа, признак повторяемости, structured details, проекция отказов boundary и коды выхода процесса | [.codex/context/application-failures.md](.codex/context/application-failures.md) |
| Composition root и состав host-а, structured logging и граница `stdout`/`stderr`, correlation identity, состав диагностического snapshot | [.codex/context/runtime-diagnostics.md](.codex/context/runtime-diagnostics.md) |
| MuMu-capability: control surface, обнаружение установки, identity и выбор экземпляра, host-side состояние, start/stop/restart и postcondition | [.codex/context/mumu-lifecycle.md](.codex/context/mumu-lifecycle.md) |
| Что именно доказывают проверки, различие hosted CI и real Windows acceptance и как падает verification | [.codex/context/verification.md](.codex/context/verification.md) |
| Язык комментариев, диагностики и документации | [.codex/context/language.md](.codex/context/language.md) |
| Git/GitHub lifecycle: ветка, staging, commit, push, remote postcondition, PR, Draft/Ready, merge и cleanup | [`.agents/skills/azurpilot-git-workflow/`](.agents/skills/azurpilot-git-workflow/) |
| Явно запрошенный цикл CodeRabbit review: запуск CLI, triage findings, исправления, итерации и rate limit | [`.agents/skills/azurpilot-coderabbit-review/`](.agents/skills/azurpilot-coderabbit-review/) |
| Форма C ABI v1: структура, экспорты, коды возврата, семантика буферов | [native/include/azurpilot_native_abi.h](native/include/azurpilot_native_abi.h) |
| Канонические команды и рабочий путь разработки | [README.md](README.md), правила и источники — [.codex/context/architecture.md](.codex/context/architecture.md) |
| Установка prerequisites, сборка, тесты, типовые проблемы | [docs/getting-started.md](docs/getting-started.md) |
| Назначение проекта и канонические команды как входная точка | [README.md](README.md) |

## Изменение правил

- Правило меняется в документе-владельце; копия правила в другом файле запрещена.
- Если правило или номер получает нового владельца, он добавляется в
  [.codex/context/INDEX.md](.codex/context/INDEX.md).
- Файлы-заглушки под будущие подсистемы не создаются: документ появляется вместе с реальной
  capability.
