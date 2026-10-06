# Контракт версий и внешних зависимостей

Владелец: этот файл. Здесь описаны правила выбора и обновления зависимостей; сами значения находятся
в manifests-владельцах из [INDEX.md](INDEX.md). Карта проекта и канонический build/test-путь —
[architecture.md](architecture.md), содержание проверок — [verification.md](verification.md), форма
C ABI — `native/include/azurpilot_native_abi.h`.

## Источники значений

У каждого значения один источник. Обновляйте его там, а потребителей и ссылки синхронизируйте без
копирования пина в другие manifests или документацию.

| Значение | Владелец |
| --- | --- |
| Версия .NET SDK и политика выбора установленного SDK | `global.json` |
| Версии прямых NuGet-пакетов | `Directory.Packages.props` |
| Разрешённый граф NuGet-пакетов | `packages.lock.json` каждого проекта |
| Минимум CMake и минимальный MSVC compiler/toolset floor | `native/CMakeLists.txt` |
| Native generator, конфигурации и workflow presets | `native/CMakePresets.json` |
| Версия OpenCV, URL, SHA256 и пути внутри архива | `native/opencv.json` |
| Общие .NET build/analyzer свойства и `TargetFramework`/RID | `Directory.Build.props` |
| Форма и нормативная версия C ABI, exports и семантика границы | `native/include/azurpilot_native_abi.h` |
| Версии GitHub Actions и состав CI-шагов | `.github/workflows/ci.yml` |

Таблица маршрутизации всех правил находится в [INDEX.md](INDEX.md). Документы не дублируют значения
версий: для точного значения открывайте его manifest-владелец.

## Политика обновлений

- Используйте стабильные GA-релизы; Preview, alpha, beta, nightly и плавающие версии не являются
  допустимыми build dependencies.
- После принятия зависимости обновляйте значение в её owner manifest. Locked NuGet restore обязан
  использовать сохранённый граф из `packages.lock.json`; расхождение графа должно останавливать
  restore, а не переписывать lock file незаметно.
- Не понижайте зависимость до старой стабильной линии только ради удобства package manager.
- Не фиксируйте machine-specific абсолютные пути или полученные third-party бинарники в Git.
- Нижние границы native toolchain — решение о поддерживаемой базе и принадлежат
  `native/CMakeLists.txt`. Их изменение рассматривается отдельно от обновления version pin.

## Получение OpenCV

`native/opencv.json` — единственный владелец OpenCV version pin, download URL, SHA256 и layout. Во
время CMake configure код из `native/cmake/OpenCvDependency.cmake`:

1. скачивает закреплённый пакет при отсутствии локального архива;
2. проверяет SHA256 и для нового, и для кэшированного архива;
3. распаковывает пакет в `artifacts/opencv/<version>`, проверяет `archiveRoot`, наличие
   `OpenCVConfig.cmake` по `cmakeDir` и runtime-каталог по `runtimeDir`;
4. завершает configure с ошибкой при неверном hash или layout.

Пути `archiveRoot`, `cmakeDir` и `runtimeDir` задаются относительно каталога распаковки. CI может
кэшировать архив в `artifacts/downloads`, но кэш не заменяет SHA256-проверку. CMake preset запускает
тот же configure и локально, и в CI. Runtime staging принадлежит
[architecture.md](architecture.md).

## Dependency automation

`renovate.json` использует реальные manifests проекта: NuGet manager отслеживает `global.json` и
`Directory.Packages.props`, lock-file maintenance обслуживает `packages.lock.json`, GitHub Actions
manager читает workflow, а custom regex manager отслеживает `native/opencv.json`. Стабильные major,
minor и patch updates рассматриваются; предрелизы отфильтрованы.

Для OpenCV Renovate обновляет версию и URL согласованно. Он не вычисляет SHA256: новый hash нужно
проверить по опубликованному артефакту и обновить в том же изменении. Обновление pin не должно
обходить CI и review.
