# Проверка: что именно доказывается

Владелец: этот файл. Здесь описаны свойства фундамента и текущих application-контрактов, которые
доказывают native CTest и managed тесты, а также результаты, которые считаются ложными. Канонический
build/test-путь принадлежит [architecture.md](architecture.md), владельцы toolchain и dependency
manifests перечислены в [build-contracts.md](build-contracts.md) и [INDEX.md](INDEX.md), форма C ABI —
`native/include/azurpilot_native_abi.h`, правила конфигурации, отказов и диагностики —
[application-configuration.md](application-configuration.md),
[application-failures.md](application-failures.md) и
[runtime-diagnostics.md](runtime-diagnostics.md).

## Команды CI

CI выполняет стандартные команды CMake и .NET, а не отдельный orchestration entrypoint. Из каталога
`native/` запускается Release workflow; следующие команды выполняются из корня репозитория:

```text
cmake --workflow --preset native-x64-release
dotnet restore AzurPilot.slnx --locked-mode
dotnet build AzurPilot.slnx --configuration Release --no-restore -warnaserror
dotnet test tests/AzurPilot.Tests/AzurPilot.Tests.csproj --configuration Release --no-restore --no-build
```

CMake workflow включает configure, build и CTest. Restore работает в locked mode и завершается
ошибкой, если manifests не соответствуют lock-файлам. Managed test запускается после build без
повторной сборки. Для локальной Debug-проверки используется CMake workflow preset
`native-x64-debug` и те же managed-команды с `--configuration Debug`.

## Что доказывает native CTest

- CMake configure находит закреплённый OpenCV из `native/opencv.json`; configure получает пакет и
  проверяет SHA256 и ожидаемый layout, в том числе для кэшированного архива.
- Workflow собирает native library с OpenCV и исполняет `native_abi_smoke`. Пустой набор CTest
  считается ошибкой.
- Smoke test вызывает native code через C ABI и проверяет версию и раскладку ABI, версию OpenCV из
  manifest, фактическое выполнение OpenCV-кода, capability `core` и `imgcodecs`, стабильность
  повторного вызова и обработку малого буфера без частичной записи.

Тест-заглушка без вызова project-owned native code доказательством не считается.

## Что доказывают managed тесты

- Тест загружает собранную `AzurPilot.Native.dll` через source-generated `LibraryImport` и получает
  реальные данные C ABI; мок не заменяет native boundary.
- Проверяются версия ABI и OpenCV, размер managed-структуры, capability и факт выполнения OpenCV.
- Негативные тесты запускают probe-процесс без production native DLL и с изолированной ABI mismatch
  fixture. Отсутствие DLL и несовместимый ABI должны приводить к ожидаемому явному отказу.
- Repository contract tests проверяют machine-specific absolute paths и hardcode
  `1280x720` в исходниках/конфигурации, а также отсутствие Git-visible binaries и build outputs.
  Документация исключена из этих source checks.

MSBuild берёт native runtime из `artifacts/native/runtime/<Configuration>`. До managed build он
завершается ошибкой, если staging не содержит `AzurPilot.Native.dll` или runtime DLL OpenCV; поэтому
положительный interop test не может пройти без native runtime.

## Что доказывают проверки application-контрактов

Правила конфигурации, отказов и диагностики принадлежат
[application-configuration.md](application-configuration.md),
[application-failures.md](application-failures.md) и
[runtime-diagnostics.md](runtime-diagnostics.md); здесь описано только то, что доказывается.

Строгая конфигурация:

- отсутствие файла даёт snapshot встроенных defaults, значения берутся у владельца defaults, а файл
  при загрузке не создаётся;
- валидный файл схемы v1 читается полностью, а путь файла сохраняется в snapshot;
- документ, не соответствующий схеме, отклоняется: синтаксически невалидный и пустой документ,
  документ без JSON-объекта в корне, неизвестное свойство, включая секции будущих capability,
  duplicate property, неверный регистр имени свойства и значения, нарушение required/nullable
  contract, trailing comma, комментарий, числовая форма уровня логирования, неверный тип
  `schemaVersion`;
- неподдерживаемая версия схемы отклоняется отдельным стабильным отказом и распознаётся даже тогда,
  когда документ содержит секции будущей схемы;
- существующий, но нечитаемый файл и путь, указывающий на каталог, отклоняются, а не подменяются
  defaults;
- UTF-8 BOM допускается и не ослабляет строгость;
- относительный или пустой путь остаётся ошибкой программирования, а не ожидаемым отказом;
- изменение и удаление файла после загрузки не меняет уже полученный snapshot.

Composition и отказы:

- построенный host не подключает нежелательные configuration providers и logging providers:
  пользовательская конфигурация приходит одним строгим JSON snapshot, а действующий formatter —
  JSON console;
- в DI попадает тот же экземпляр snapshot, который вернул загрузчик, и минимальный уровень
  логирования из конфигурации действительно применяется к логированию;
- runtime services host-а — snapshot конфигурации, диагностическая операция и проекция ошибок
  платформенной/native boundary — резолвятся из контейнера, а не создаются вручную;
- проекция отказов проверяется на реальных типах production-исключений, включая сохранение кода
  возврата native стороны, ограниченность details и отсутствие утечки текста неожиданного исключения;
- недоступная native DLL и несовместимый ABI дают ожидаемые application-коды отказа, а не общее
  сообщение об ошибке.

Диагностика и граница вывода:

- здоровый диагностический snapshot содержит реальные evidence native boundary, а не заглушку:
  версию ABI, версию OpenCV, capability, факт исполнения кода OpenCV и строку сведений о сборке;
- недоступная и несовместимая native boundary проверяются в изолированной копии каталога запуска,
  где библиотека удалена или подменена fixture с несовместимым ABI, поэтому проверяется реальный
  failure mode, а не мок;
- проверки выполняются на реальном процессе приложения, а не только на исходном тексте composition;
- structured runtime logs уходят в `stderr`, а `stdout` остаётся человекочитаемым итогом: в `stdout`
  нет ни одной записи structured log;
- один correlation identifier связывает события startup, конфигурации и native-диагностики и
  присутствует в каждой записи операции, включая её scopes, а не только в тексте одного сообщения;
- диагностика не печатает полный документ конфигурации ни в `stdout`, ни в `stderr`, а сообщает
  bounded сведения о ней;
- проверки конфигурации используют явно переданный путь, поэтому результат не зависит от
  пользовательского `%LOCALAPPDATA%\AzurPilot\config.json`.

## Предупреждения и анализаторы

Managed build запускается с `-warnaserror`; общие compiler/analyzer и code-style настройки принадлежат
`Directory.Build.props`. Native targets собираются с `/W4` и `/WX`. Реальный build CI тем самым
проверяет warnings на ошибку вместе с компиляцией; отдельный неиспользуемый formatter entrypoint не
является частью CI-контракта.

## Границы проверки

Проверка доказывает только текущее состояние: native CMake build, OpenCV acquisition и линковку, C ABI,
managed interop, строгую конфигурацию, application-level отказы, диагностику и границу вывода
application host, тесты repository contracts и закреплённый .NET/native toolchain.

Она не доказывает и не должна имитировать работу MuMu, ADB, lifecycle игры, реальный screenshot,
корректность будущих vision algorithms, OCR/ONNX/GPU inference и real-device acceptance. Эти
capabilities отсутствуют в текущем приложении; фиктивные шаги для них в CI запрещены.
