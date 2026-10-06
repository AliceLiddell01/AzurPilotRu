# Начало работы

Практическая входная точка: что установить, как собрать проект, как запускать тесты и что делать при
типовых проблемах. Правила проекта живут в [.codex/context](../.codex/context/INDEX.md) — этот
документ ссылается на них, а не пересказывает их.

## 1. Prerequisites

- **Windows x64.** Единственная поддерживаемая платформа
  ([architecture.md](../.codex/context/architecture.md)).
- **.NET SDK** линии 10 (LTS). Точная версия закреплена в `dotnetSdk.version`, политика обновления —
  `dotnetSdk.rollForward`, и ту же версию требует [global.json](../global.json).
- **Visual Studio 2026** с workload «Desktop development with C++» (компонент
  `Microsoft.VisualStudio.Component.VC.Tools.x86.x64`). Сборка находит установку через `vswhere` и
  требует toolset MSVC не ниже `toolchain.msvcMinimumVersion`.
- **Standalone CMake** не ниже `toolchain.cmakeMinimumVersion`. CMake, входящий в состав Visual
  Studio, отстаёт от минимума и намеренно не принимается как подстановка.
- **PowerShell 7+** (`pwsh`) — язык канонических скриптов.
- **Ninja** — необязательно: нужен только альтернативному пути сборки
  (`toolchain.cmakeGeneratorAlternative`) и не влияет на канонический путь.

Все значения версий — ссылки на поля [eng/versions.json](../eng/versions.json): это единственный
владелец закреплённых номеров. Посмотреть текущий закреплённый набор можно прямо в этом файле или в
журнале сборки: шаг проверки toolchain печатает найденные и требуемые версии. Правила выбора и
обновления версий — [build-contracts.md](../.codex/context/build-contracts.md).

## 2. Быстрый старт

Из корня репозитория (команды работают и из любого другого текущего каталога):

```pwsh
pwsh ./eng/build.ps1 -Configuration Release
pwsh ./eng/verify.ps1
```

`eng/build.ps1` собирает проект целиком и запускает тесты; `eng/verify.ps1` — единая проверка: тот же
канонический build плюс analyzers, project-owned warnings, code-style, согласованность закреплённых
версий и git-гигиена. Ненулевой код возврата означает реальное нарушение контракта.

## 3. Что происходит при сборке

`eng/build.ps1` проходит шесть шагов:

1. **Проверка toolchain** — dotnet, standalone CMake, MSVC через `vswhere`, Ninja (для
   альтернативного пути). При отсутствии или слишком старой версии — понятная ошибка без
   молчаливого fallback.
2. **Получение закреплённых native dependencies** — пакет OpenCV скачивается по точному URL из пина
   и проверяется по SHA256.
3. **Сборка native части и native CTest** — CMake configure/build x64 и запуск project-owned native
   теста.
4. **Staging native runtime** — native библиотека и runtime OpenCV попадают в staging-каталог,
   который далее передаётся managed сборке свойством `AzurPilotNativeRuntimeDir`.
5. **Managed restore и сборка solution** — `AzurPilot.slnx` со staging native runtime в выходе.
6. **Managed тесты** — interop тесты реально загружают собранную native библиотеку.

## 4. Native dependency (OpenCV)

Шаг 2 выполняется автоматически. Отдельно его можно запустить так:

```pwsh
pwsh ./eng/Get-NativeDependencies.ps1
```

- Скачивается ровно тот артефакт, который закреплён в `opencv.url`; SHA256 обязателен и проверяется
  и для скачанного, и для закэшированного файла.
- Пакет распаковывается в `artifacts/opencv/<version>`; повторный запуск при совпадающем hash не
  перекачивает пакет.
- Ручная установка OpenCV не требуется: по умолчанию `OpenCV_DIR` вычисляется из пина. Явное
  переопределение возможно только параметром `-OpenCvDir` (например для локальной диагностики) и
  никогда не подставляется молча.
- При расхождении SHA256 сборка не продолжается. Правила выбора и обновления зависимости —
  [build-contracts.md](../.codex/context/build-contracts.md).

## 5. Артефакты

Всё генерируемое живёт внутри единственной ignored boundary `artifacts/` и стандартных managed
`bin`/`obj` — в Git это не коммитится.

| Путь | Содержимое |
| --- | --- |
| `artifacts/downloads` | скачанный пакет OpenCV (кэшируется в CI по версии и SHA256) |
| `artifacts/opencv/<version>` | распакованный закреплённый OpenCV |
| `artifacts/native/cmake` | каталог сборки native части; журналы CTest — в `artifacts/native/cmake/Testing/Temporary` |
| `artifacts/native/bin/<Configuration>` | native библиотека, PDB и native smoke test |
| `artifacts/native/runtime/<Configuration>` | staging native runtime для managed выхода |
| `src/**/bin`, `tests/**/bin` | managed выходы проектов и тестов |

## 6. Тесты

Канонический путь запускает всё сам; ниже — что именно проверяется и как запустить то же самое
вручную для диагностики.

- **Native (CTest, шаг 3)** — реально исполняет project-owned native код с OpenCV: версия ABI,
  исполнение OpenCV-кода, capabilities, обработка малого буфера.
- **Managed (шаг 6)** — interop тесты реально загружают собранную native библиотеку через
  source-generated `LibraryImport` и получают данные от C ABI.
- **Негативная проверка** — отдельная проба запускается тестом из каталога, где native библиотеки
  заведомо нет, и подтверждает, что production-код interop сообщает об этом исключением. Это
  тестовый инструмент, а не boundary приложения
  ([architecture.md](../.codex/context/architecture.md)).

Диагностический запуск managed тестов:

```pwsh
dotnet test --project ./tests/AzurPilot.Tests/AzurPilot.Tests.csproj -c Release `
  -p:AzurPilotNativeRuntimeDir="$PWD/artifacts/native/runtime/Release"
```

Два условия, без которых команда не сработает:

- Свойство `AzurPilotNativeRuntimeDir` нужно указывать явно. Канонический `eng/build.ps1` передаёт его
  сам, но между отдельными вызовами `dotnet` оно не сохраняется: без него native библиотека не попадает
  в выход теста, и положительные interop тесты падают с сообщением, что библиотека не загружена. Это
  ожидаемое поведение (см. раздел 8), а не признак поломки.
- Путь должен быть абсолютным. `$PWD` в примере раскрывается в корень репозитория. Относительный путь
  здесь не работает: свойство разрешается в контексте каталога проекта, поэтому путь вида
  `./artifacts/...` будет искаться не там, где лежит staging, и тесты упадут так же, как без свойства.

Важно: .NET 10 SDK вместе с xunit.v3 работает в режиме `Microsoft.Testing.Platform` — opt-in уже
включён в [global.json](../global.json). В этом режиме неизвестные опции передаются самому приложению,
поэтому не добавляйте `--nologo`: запуск упадёт с кодом 5. Что именно обязана доказывать проверка и
как она должна падать — [verification.md](../.codex/context/verification.md).

## 7. Параметры скриптов

| Скрипт | Параметры |
| --- | --- |
| `eng/build.ps1` | `-Configuration` (`Debug`/`Release`, по умолчанию `Release`), `-SkipTests`, `-SkipNative`, `-Clean` |
| `eng/verify.ps1` | `-Configuration`, `-SkipBuild` (отладка самих проверок, не verification), `-CheckDotNetFormat` (жёсткий гейт форматирования; по умолчанию частью контракта не является) |
| `eng/Invoke-NativeBuild.ps1` | `-Configuration` (обязателен), `-OpenCvDir` (переопределение каталога `OpenCVConfig.cmake` для локальной диагностики), `-Clean`, `-SkipTests` |
| `eng/Get-NativeDependencies.ps1` | `-Force` (перекачать закреплённый пакет даже при наличии локальной копии) |

`-SkipNative` без собранного native runtime останавливает сборку, если тесты не отключены: ложный
зелёный результат для interop boundary запрещён.

## 8. Диагностика типовых проблем

| Симптом | Причина и что делать |
| --- | --- |
| `Не найден vswhere.exe` или «не найдена ни одна установка Visual Studio с toolset MSVC x64» | установите Visual Studio 2026 с workload «Desktop development with C++» |
| Toolset найден, но его версия ниже закреплённой | обновите Visual Studio; значение и владелец — `toolchain.msvcMinimumVersion` |
| CMake не найден или слишком старый | поставьте standalone CMake не ниже `toolchain.cmakeMinimumVersion`; CMake из состава Visual Studio не принимается намеренно |
| .NET SDK не соответствует пину (другая feature band или старше) | установите feature band из `dotnetSdk.version`; roll-forward допускается только внутри той же feature band |
| SHA256 скачанного OpenCV не совпал | файл повреждён или подменён: удалите `artifacts/downloads` и повторите; сборка намеренно не продолжается |
| Native библиотека не найдена, managed тесты падают | это ожидаемое поведение: без native части положительный interop тест обязан падать. Соберите проект канонической командой |
| Ninja отсутствует или старее минимума | канонический путь не блокируется: Ninja нужен только альтернативному генератору, сборка сообщает об этом отдельно |
| `dotnet test` падает с кодом 5 или сообщением о неизвестной опции | режим `Microsoft.Testing.Platform`: уберите неподдерживаемые опции (например `--nologo`) |
| Нужен чистый прогон | `pwsh ./eng/build.ps1 -Configuration Release -Clean` очищает build outputs; закреплённый пакет OpenCV при этом сохраняется |

## 9. Куда смотреть дальше

- [README.md](../README.md) — назначение проекта, платформа, канонические команды.
- [AGENTS.md](../AGENTS.md) — контракт репозитория и router для агентов.
- [.codex/context/INDEX.md](../.codex/context/INDEX.md) — таблица владельцев правил.
- [architecture.md](../.codex/context/architecture.md) — карта проекта, boundaries, правило зависимостей.
- [build-contracts.md](../.codex/context/build-contracts.md) — контракт версий и зависимостей.
- [verification.md](../.codex/context/verification.md) — что доказывает verification.
