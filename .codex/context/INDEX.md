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
| Карта репозитория, состав boundaries (ровно три managed + одна native), статус проектов `tests/` как тестовых инструментов, а не boundaries, правило зависимостей, канонические команды сборки и тестирования и рабочий путь разработки, конвенция staging native runtime, запрет machine-specific путей, Windows-only/x64, отсутствие `1280x720` как фундаментального разрешения, статус реализованных MuMu-capability и Android/ADB readiness вместе с lifecycle игры, статус отсутствующих ввода, vision/OCR/ONNX и product CLI/REPL/agent CLI | [architecture.md](architecture.md) |
| Схема пользовательской конфигурации v2, нормализация legacy-входа v1 в памяти, runtime-путь файла конфигурации, правила загрузки и строгой валидации, владелец встроенных значений по умолчанию и отсутствие hot reload | [application-configuration.md](application-configuration.md) |
| Application-level модель отказов: стабильные коды, включая коды Android readiness и lifecycle игры, признак повторяемости, состав structured details, проекция отказов платформенной/native boundary и соответствие «код отказа → код выхода процесса» | [application-failures.md](application-failures.md) |
| Composition root и состав application host, structured logging и граница `stdout`/`stderr`, correlation/operation identity, состав runtime-диагностического snapshot, включая секции Android и состояния игры | [runtime-diagnostics.md](runtime-diagnostics.md) |
| MuMu-capability: поддерживаемое семейство и форма control surface, version/capability discovery, обнаружение установки, стабильная identity и выбор Android-экземпляра, host-side состояние и его evidence, семантика start/stop/restart и postcondition, границы времени, ownership mutation | [mumu-lifecycle.md](mumu-lifecycle.md) |
| Android/ADB и lifecycle игры Azur Lane Global/EN: связь выбранного экземпляра MuMu с точным ADB endpoint, граница ADB executable и форма команд, наблюдение transport и контракт готовности Android, product identity Global/EN, наблюдение пакета/процесса/переднего плана, семантика start/stop/restart игры и postcondition, разрешение launcher-компонента, границы времени и ownership mutation игры, граница read-only диагностики и mutating readiness | [android-game-lifecycle.md](android-game-lifecycle.md) |
| Политика version pins, чтение владельцев manifests, acquisition и SHA256-верификация OpenCV, запрет committed third-party бинарников, диагностика toolchain и требования к Renovate | [build-contracts.md](build-contracts.md) |
| Что именно доказывают стандартные CI build/test commands, требования к native CTest и managed interop тестам, негативные проверки, проверки конфигурации, отказов, диагностики, MuMu lifecycle и Android/ADB readiness вместе с lifecycle игры через подменяемые external-boundary seams, различие доказательств hosted CI, real Windows acceptance MuMu и real Windows acceptance Android, границы проверки | [verification.md](verification.md) |
| Языковая политика project-owned комментариев, диагностики и документации | [language.md](language.md) |

## Владельцы вне `.codex/context`

| Правило или факт | Владелец |
| --- | --- |
| Глобальный контракт репозитория и router: минимальные инварианты и ссылка на эту таблицу | `AGENTS.md` |
| Назначение проекта, поддерживаемая платформа, краткая входная точка и наблюдаемое поведение запуска | `README.md` |
| Пошаговые инструкции по установке, сборке, тестам, запуску приложения и диагностике | `docs/getting-started.md` |
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
| Git/GitHub lifecycle текущей задачи: branch, staging, commit, push, проверка remote SHA, PR body, Draft/Ready, merge, конфликты и cleanup | `.agents/skills/azurpilot-git-workflow/SKILL.md` |
| Явно запрошенный CodeRabbit review cycle: CLI discovery, invocation, completion, triage findings, итерации, clean marker commit, rate limit и CodeRabbit-specific данные PR | `.agents/skills/azurpilot-coderabbit-review/SKILL.md` |
| Repository configuration CodeRabbit: language/profile, path instructions, tools, auto-review, knowledge base и provider settings | `.coderabbit.yaml` |
