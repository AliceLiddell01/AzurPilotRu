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
| Точные версии: .NET SDK, MSVC, CMake (минимум, latest stable, generator), Ninja, OpenCV (версия, URL, SHA256, пути внутри пакета), а также зеркало версии ABI и список capability ABI (владелец номера ABI — заголовок) | [eng/versions.json](../../eng/versions.json) |
| Карта репозитория, состав boundaries (ровно три managed + одна native), статус проектов `tests/` как тестовых инструментов, а не boundaries, правило зависимостей, канонические entrypoints, конвенция staging native runtime, запрет machine-specific путей, Windows-only/x64, отсутствие `1280x720` как фундаментального разрешения, статус MuMu/ADB/game automation как будущих capability | [architecture.md](architecture.md) |
| Политика версий (latest-stable + exact pin), чтение `eng/versions.json`, выбор и проверка acquisition для OpenCV, checksum-верификация, запрет committed third-party бинарников, правило «один номер — один владелец», staging OpenCV DLL, диагностика toolchain, требования к Renovate | [build-contracts.md](build-contracts.md) |
| Что именно доказывает verification, требования к native CTest и managed interop тесту, негативные проверки, границы verification, контракт ненулевого exit code `pwsh ./eng/verify.ps1` | [verification.md](verification.md) |
| Языковая политика project-owned комментариев, диагностики и документации | [language.md](language.md) |

## Владельцы вне `.codex/context`

| Правило или факт | Владелец |
| --- | --- |
| Глобальный контракт репозитория и router: минимальные инварианты и ссылка на эту таблицу | `AGENTS.md` |
| Назначение проекта, поддерживаемая платформа, prerequisites и canonical commands как входная точка | `README.md`, `docs/**` |
| Версии GitHub Actions, состав CI-шагов и кэширование | `.github/workflows/**` |
| Расписание и правила dependency-update automation | `renovate.json` |
| Общие managed compiler/analyzer/build свойства и `TargetFramework`/RID | `Directory.Build.props` |
| Версии NuGet-пакетов | `Directory.Packages.props` |
| Версии C# code-style правил | `.editorconfig` |
