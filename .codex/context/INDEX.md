# .codex/context — таблица владельцев правил

Назначение файла — маршрутизация: какой документ владеет каким правилом. Содержательных правил
здесь нет, чтобы не появился второй владелец.

## Правило владения

- Каждое правило и каждый закреплённый номер имеет ровно одного владельца из таблицы ниже.
- Документ, не являющийся владельцем, ссылается на владельца, а не повторяет правило.
- Значение, которое используется в коде, конфигурации или CI, имеет одного владельца-источника;
  остальные места читают его из источника, а не хранят копию.
- Документ появляется вместе с реальной capability: файлов-заглушек под будущие подсистемы нет.
- Изменение правила выполняется в документе-владельце; правка копии в другом месте запрещена.

## Таблица владельцев

| Правило или факт | Владелец |
| --- | --- |
| Форма C ABI v1: структура `AzurPilotNativeInfo`, экспорты, коды возврата, семантика `required_size`, биты `build_flags`/`capabilities`, нормативное значение версии ABI, имя native артефакта, запрет C++-типов и исключений на границе | [native/include/azurpilot_native_abi.h](../../native/include/azurpilot_native_abi.h) |
| Карта репозитория, состав boundaries (ровно три managed + одна native), статус проектов `tests/` как тестовых инструментов, а не boundaries, правило зависимостей, канонические команды сборки и тестирования и рабочий путь разработки, конвенция staging native runtime, запрет machine-specific путей, Windows-only/x64, отсутствие `1280x720` как фундаментального разрешения, статус MuMu/ADB/game automation как будущих capability | [architecture.md](architecture.md) |
| Политика version pins, чтение владельцев manifests, acquisition и SHA256-верификация OpenCV, запрет committed third-party бинарников, диагностика toolchain и требования к Renovate | [build-contracts.md](build-contracts.md) |
| Что именно доказывают стандартные CI build/test commands, требования к native CTest и managed interop тестам, негативные проверки и границы проверки | [verification.md](verification.md) |
| Языковая политика project-owned комментариев, диагностики и документации | [language.md](language.md) |

## Владельцы вне `.codex/context`

| Правило или факт | Владелец |
| --- | --- |
| Глобальный контракт репозитория и router: минимальные инварианты и ссылка на эту таблицу | `AGENTS.md` |
| Назначение проекта, поддерживаемая платформа и краткая входная точка | `README.md` |
| Пошаговые инструкции по установке, сборке, тестам и диагностике | `docs/getting-started.md` |
| Версии GitHub Actions, состав CI-шагов и кэширование | `.github/workflows/**` |
| Расписание и правила dependency-update automation | `renovate.json` |
| Общие managed compiler/analyzer/build свойства и `TargetFramework`/RID | `Directory.Build.props` |
| Проверка и подключение native runtime к managed outputs | `Directory.Build.targets` |
| Версии NuGet-пакетов | `Directory.Packages.props` |
| Разрешённые графы NuGet-зависимостей | `**/packages.lock.json` |
| Native targets, CMake minimum и compiler/toolset floor | `native/CMakeLists.txt` |
| Native generator и workflow presets | `native/CMakePresets.json` |
| OpenCV version pin, URL, SHA256 и layout | `native/opencv.json` |
| .NET SDK version и roll-forward policy | `global.json` |
| Версии C# code-style правил | `.editorconfig` |
