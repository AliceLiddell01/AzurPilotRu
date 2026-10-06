# Проверка: что именно доказывается

Владелец: этот файл. Здесь описаны свойства фундамента, которые доказывают native CTest и managed
тесты, а также результаты, которые считаются ложными. Канонический build/test-путь принадлежит
[architecture.md](architecture.md), владельцы toolchain и dependency manifests перечислены в
[build-contracts.md](build-contracts.md) и [INDEX.md](INDEX.md), форма C ABI —
`native/include/azurpilot_native_abi.h`.

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

## Предупреждения и анализаторы

Managed build запускается с `-warnaserror`; общие compiler/analyzer и code-style настройки принадлежат
`Directory.Build.props`. Native targets собираются с `/W4` и `/WX`. Реальный build CI тем самым
проверяет warnings на ошибку вместе с компиляцией; отдельный неиспользуемый formatter entrypoint не
является частью CI-контракта.

## Границы проверки

Проверка доказывает только фундамент: native CMake build, OpenCV acquisition и линковку, C ABI,
managed interop, тесты repository contracts и закреплённый .NET/native toolchain.

Она не доказывает и не должна имитировать работу MuMu, ADB, lifecycle игры, реальный screenshot,
корректность будущих vision algorithms, OCR/ONNX/GPU inference и real-device acceptance. Эти
capabilities отсутствуют в текущем фундаменте; фиктивные шаги для них в CI запрещены.
