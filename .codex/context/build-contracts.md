# Контракт версий и внешних зависимостей

Владелец: этот файл. Точные значения версий живут только в `eng/versions.json`; здесь описаны
правила, по которым эти значения выбираются, читаются, проверяются и обновляются. Карта проекта и
canonical entrypoints — [architecture.md](architecture.md), содержание проверок —
[verification.md](verification.md).

## Политика версий

- **latest-stable при adoption.** При первом добавлении любой внешней зависимости или toolchain
  reference берётся самая свежая стабильная версия, доступная на момент реализации. Цель — не
  начинать новый проект с накопленного migration debt.
- **stable = GA/release.** RC, Preview, alpha, beta, nightly, nightly-сборки и произвольный commit
  основной ветки стабильными не считаются и в фундаменте не используются. Если для действительно
  обязательной capability стабильного варианта нет, берётся последний стабильный контракт и
  ограничение фиксируется явно, а не подменяется preview молча.
- **exact pin после adoption.** Выбранная версия (а для скачиваемого артефакта — версия, URL и
  SHA256) фиксируется в `eng/versions.json`. Один commit проекта собирается с предсказуемым набором
  версий.
- **Никакого floating.** `latest`, `master`, `main`, плавающие теги и непроверяемые URL как build
  dependency запрещены. Новые stable релизы приходят через автоматический dependency-update
  workflow (PR), а не подтягиваются во время каждой сборки.
- **Major не игнорируются.** Major-обновления стабильных версий предлагаются так же, как minor и
  patch; предрелизные обновления по умолчанию не предлагаются.
- **Без искусственной обратной совместимости.** Старые framework/compiler версии не поддерживаются
  «для совместимости», если такой compatibility contract не существует.
- **Понижение запрещено.** Зависимость не понижается до старой stable-ветки только потому, что её
  проще получить через package manager. Если latest stable требует другого reproducible acquisition
  path — исправляется acquisition path.

## eng/versions.json — единственный источник версий

Файл `eng/versions.json` — repository-owned source of truth. Он не содержит абсолютных путей и
floating-значений.

| Ключ | Смысл |
| --- | --- |
| `dotnetSdk.version` | Точная версия .NET SDK; значение для `global.json` |
| `dotnetSdk.rollForward` | Политика roll-forward для `global.json` |
| `toolchain.msvcMinimumVersion` | Минимально допустимая версия MSVC toolset (major.minor) |
| `toolchain.msvcReferenceVersion` | Полная версия toolset, на которой фундамент реально проверен (диагностика) |
| `toolchain.cmakeMinimumVersion` | Минимально допустимая версия CMake для canonical build |
| `toolchain.cmakeLatestStable` | Latest stable CMake на момент adoption — для документации и Renovate |
| `toolchain.cmakeGenerator` | Канонический CMake generator |
| `toolchain.cmakeGeneratorAlternative` | Поддерживаемый альтернативный generator |
| `toolchain.ninjaMinimumVersion` | Минимально допустимая версия Ninja (только для альтернативного пути) |
| `opencv.version` | Точная версия OpenCV |
| `opencv.url` | Точный URL закреплённого релиза (без `latest`) |
| `opencv.sha256` | Контрольная сумма скачиваемого пакета |
| `opencv.archiveRoot` | Единственный корневой каталог внутри архива |
| `opencv.cmakeDir` | Каталог с `OpenCVConfig.cmake` относительно корня распаковки |
| `opencv.runtimeDir` | Каталог с runtime DLL OpenCV относительно корня распаковки |
| `nativeAbi.version` | Версия C ABI (зеркало значения из заголовка ABI) |
| `nativeAbi.capabilities` | Имена capability, присутствующих в этой версии ABI (без битовых значений) |

### Как файл читается

- Скрипты `eng/` читают файл от корня репозитория (`$PSScriptRoot`), а не от текущей рабочей
  директории, и передают значения дальше как параметры: cache variables CMake (`OpenCV_DIR`,
  требуемая версия ABI), свойства MSBuild (staging native runtime), версия .NET SDK для
  `global.json`/CI, generator.
- Те же значения запрещено хардкодить в скриптах, `CMakeLists.txt`, `CMakePresets.json`, workflow и
  документации: они читаются из источника или на него ссылаются.
- `global.json` — потребитель `dotnetSdk.version` и `dotnetSdk.rollForward`; собственных версий он
  не вводит.
- Значения `opencv.archiveRoot`, `opencv.cmakeDir`, `opencv.runtimeDir` заданы **относительно корня
  распаковки** `artifacts/opencv/<version>`. Для закреплённого OpenCV это означает:
  `artifacts/opencv/<version>/opencv/build/x64/vc16/lib/OpenCVConfig.cmake` (значение `OpenCV_DIR`
  для CMake) и `artifacts/opencv/<version>/opencv/build/x64/vc16/bin` (каталог runtime DLL).
  `archiveRoot` задаёт обязательный корневой каталог распаковки и используется как проверка
  целостности содержимого после распаковки.
- `opencv.sha256` сравнивается без учёта регистра; регистр записи в JSON не является частью контракта.
- `nativeAbi.version` — зеркало нормативного значения из `native/include/azurpilot_native_abi.h`.
  Владелец номера — заголовок; расхождение обязано валить verification. Битовые значения capability
  в JSON не дублируются: там только имена, а биты принадлежат заголовку.

### Значения, требующие пояснения

- **`dotnetSdk.rollForward = latestPatch`.** Feature band фиксирован значением `dotnetSdk.version`:
  допускается только патч-обновление внутри той же feature band, установленное на машине. Переход на
  другую feature band, на другой minor или major запрещён — это уже изменение пина через Renovate, а
  не молчаливый roll-forward. Точная версия из `version` остаётся предпочтительной; если установлен
  только более новый патч той же feature band, сборка не падает без причины.
- **`toolchain.cmakeMinimumVersion`.** Значение выбрано как минимальная версия линии, на которой
  canonical build обязан проходить (локальная верификация выполняется на этой же версии). До
  `cmakeLatestStable` минимум намеренно не поднят: latest stable зафиксирован отдельным полем для
  документации и Renovate, чтобы локальная проверка проходила на доступной версии. CMake,
  поставляемый внутри Visual Studio (`Common7/IDE/CommonExtensions/Microsoft/CMake`), отстаёт
  (4.3.1 на референсной среде) и **не удовлетворяет** минимуму: canonical build обязан использовать
  standalone CMake не ниже `cmakeMinimumVersion` и давать понятную ошибку, а не молча собирать
  VS-версией.
- **`toolchain.cmakeGenerator`.** Канонический generator — `Visual Studio 18 2026`: multi-config
  (Release/Debug без повторного configure), x64 через `-A x64`, не требует активации VS environment
  в shell и доступен и локально, и на CI-runner. `CMakePresets.json` обязан использовать ровно это
  значение; расхождение — дефект, который обязана ловить verification.
- **`toolchain.cmakeGeneratorAlternative` и `ninjaMinimumVersion`.** `Ninja Multi-Config` —
  поддерживаемый альтернативный путь (быстрый инкрементальный build, диагностика). Он требует
  активированного toolchain environment Visual Studio и Ninja не ниже `ninjaMinimumVersion`.
  Минимум Ninja применяется только на этом пути: отсутствие или старое значение Ninja не блокирует
  канонический путь, но диагностика обязана явно сообщить, что альтернативный путь недоступен.
- **`toolchain.msvcMinimumVersion`.** Задано как `major.minor` (точное значение — в
  `eng/versions.json`). Проверка toolchain сравнивает major.minor найденного toolset с этим
  значением; полная версия референсной сборки зафиксирована отдельно в `msvcReferenceVersion` для
  диагностики. Старший toolset требованию удовлетворяет, но закреплённым считается только после
  обновления пина.

## Референсная среда

Факты, которых нет в `eng/versions.json` и которые важны для диагностики:

- Visual Studio 2026 (18.10.x) — источник MSVC toolset; старшие продукты линии 18 поддерживаются.
- CMake, входящий в состав Visual Studio, отстаёт от требуемого минимума и не используется
  canonical build.
- Ninja, входящий в состав Visual Studio, удовлетворяет `ninjaMinimumVersion` и может быть
  использован как источник Ninja для альтернативного пути без установки сторонних пакетов.

## Почему OpenCV берётся официальным prebuilt-пакетом

- **Не vcpkg.** Стандартный registry отстаёт: на момент подготовки порт `opencv4` находится на
  4.14.0, то есть требует понижения до OpenCV 4.x. Политика проекта запрещает понижать зависимость
  ради удобства package manager. `vcpkg.json` не вводится ради его наличия.
- **Не source build.** Сборка OpenCV из исходников в CI даёт непропорциональную стоимость и второй
  build path для одной и той же зависимости, не добавляя воспроизводимости по сравнению с
  закреплённым официальным артефактом.
- **Официальный prebuilt Windows x64.** Закреплённый release-артефакт OpenCV 5.x — тот же самый
  файл локально и в CI, проверяемый по SHA256; содержит `OpenCVConfig.cmake` и target-based CMake
  package, линкуется с закреплённым MSVC toolset (проверено эмпирически, включая границу `std::string` на
  `cv::imwrite`/`cv::imread`), то есть удовлетворяет требованию «native target реально собран с
  выбранной стабильной линией OpenCV 5.x».

## Контракт получения native dependency

1. Скачать ровно `opencv.url` из `eng/versions.json`. Никаких плавающих URL и тегов.
2. Проверить SHA256 скачанного файла против `opencv.sha256`. Несовпадение — явная ошибка с обоими
   значениями в диагностике; сборка не продолжается и распаковка не выполняется.
3. Распаковать в `artifacts/opencv/<version>` и убедиться, что `archiveRoot` присутствует.
   Архив — самораспаковывающийся `.exe`; распаковка выполняется системным инструментом
   (например `tar.exe`/bsdtar) без установки сторонних пакетов.
4. Передать `OpenCV_DIR` = `<корень распаковки>/<cmakeDir>` в CMake как cache variable.
5. Операция идемпотентна: при совпадающем hash повторный запуск не перекачивает пакет.
6. CI кэширует скачанный пакет по ключу, привязанному к версии и SHA256 из `eng/versions.json`.
   Кэш — оптимизация транспорта и **не заменяет** проверку hash: кэшированный артефакт проходит ту
   же проверку, что и скачанный.

## Запрет committed third-party бинарников

Third-party бинарники (OpenCV DLL/LIB/заголовки, распакованные пакеты), build outputs и любые
сгенерированные артефакты в Git не коммитятся. Единственная граница для них — `artifacts/**` и
build directories, закрытые `.gitignore`. Коммит бинарника возможен только при доказанной
необходимости, зафиксированной явно, и по умолчанию не рассматривается.

## Staging OpenCV DLL

Runtime DLL OpenCV из `opencv.runtimeDir` и native DLL попадают в выход managed проекта и managed
теста автоматически через MSBuild `Content`/`Link` items. Источник задаёт `eng/build.ps1` через
свойство `AzurPilotNativeRuntimeDir` (см. [architecture.md](architecture.md)). Ручное копирование
DLL после сборки запрещено: оно расходится между локальной машиной и CI и не воспроизводится.

## Правило «один номер — один владелец»

Каждый номер версии имеет ровно одного владельца. Остальные места читают значение или ссылаются на
владельца.

| Номер / значение | Владелец |
| --- | --- |
| .NET SDK, MSVC, CMake, Ninja, OpenCV | `eng/versions.json` |
| Форма C ABI, биты `build_flags`/`capabilities`, коды возврата, нормативное значение версии ABI, имя native артефакта | `native/include/azurpilot_native_abi.h` |
| Зеркало версии ABI и список capability ABI (владелец номера — заголовок ABI) | `eng/versions.json` |
| `TargetFramework`, `RuntimeIdentifier`, общие compiler/analyzer свойства | `Directory.Build.props` |
| Версии NuGet-пакетов | `Directory.Packages.props` (и lock files) |
| Версии GitHub Actions | `.github/workflows/*.yml` |
| Канонические команды сборки/проверки | [architecture.md](architecture.md) |

Обновление версии не должно требовать правок одного и того же номера в нескольких несвязанных
местах: меняется владелец, потребители читают значение. Дублирование номера в README, workflow,
скриптах и документации запрещено; где значение нужно в тексте — используется ссылка на владельца.

Отдельно про версию ABI. `nativeAbi.version` в `eng/versions.json` — не второй владелец, а зеркало
нормативного значения из `native/include/azurpilot_native_abi.h`. Номер ABI описывает форму границы,
а не версию внешней зависимости, поэтому он не является dependency pin: его смена — согласованное
ручное действие (изменение формы контракта плюс синхронное обновление managed и native сторон), а не
результат автоматического обновления. Расхождение зеркала и заголовка обязано валить verification.

## Диагностика при отсутствующем или старом toolchain

- Сообщение обязано называть: что именно не найдено или устарело, найденную версию, требуемую
  версию и владельца значения (`eng/versions.json`), а также как исправить ситуацию.
- Молчаливый fallback запрещён: нельзя незаметно взять более старый toolset, VS-встроенный CMake,
  другую feature band .NET SDK, другую версию OpenCV или другой generator. Обнаруженное
  несоответствие — ошибка с ненулевым exit code.
- Отсутствие необязательного элемента (например Ninja для альтернативного пути) не блокирует
  канонический путь, но обязано быть явно сообщено в диагностике.
- Проверка версий детерминированная, без фиксированных `sleep` и без опоры на порядок операций.

## Требования к Renovate

Dependency-update automation обязана видеть все закреплённые version surfaces и не предлагать
предрелизы по умолчанию.

- Покрываются как минимум: .NET SDK (`global.json`), NuGet-пакеты (`Directory.Packages.props`,
  lock files), GitHub Actions (`.github/workflows/*.yml`), OpenCV pin (`eng/versions.json`:
  версия, URL и SHA256) и прочие versioned build tools из `eng/versions.json` (CMake, Ninja, MSVC).
- Для нестандартных пинов (JSON-поля `eng/versions.json`) используется custom manager с явными
  `managerFilePatterns` и `matchStrings`, либо явно обосновано, какой встроенный manager их
  покрывает; владелец каждого pin виден и проверяем.
- Major-обновления не игнорируются; предрелизные обновления по умолчанию не предлагаются
  (фильтры unstable/prerelease и `packageRules` по `matchUpdateTypes`).
- Exact pins сохраняются: автоматика не должна превращать точный pin в диапазон или плавающий тег.
- `nativeAbi.version` в `eng/versions.json` **не** является dependency pin и **не** обновляется
  автоматикой: владелец номера — `native/include/azurpilot_native_abi.h`, а смена версии ABI означает
  изменение формы границы и должна быть согласованным ручным действием (managed и native стороны
  меняются вместе). Renovate настраивается так, чтобы это поле было явно исключено (не покрывалось
  custom manager либо попадало под `ignore`), а причина исключения была видна в самой конфигурации,
  а не только в этом документе.
- Обновление pin приходит pull request'ом и не меняет поведение сборки в обход review; один PR не
  должен требовать правок одного и того же номера в нескольких владельцах.
- Механика исключения версии ABI зафиксирована в `renovate.json` и проверяема: поле
  `nativeAbi.version` детектируется отдельным custom manager (`azurpilot-native-abi-version`)
  только для того, чтобы исключение было явным правилом, а не отсутствием настройки, и
  выключается `packageRules` с `matchPackageNames` `azurpilot/native-abi` и `enabled: false`
  (datasource для этой зависимости не опрашивается). Причина исключения записана в описании самого
  правила, а не только в этом документе.
- Нижние границы toolchain (`cmakeMinimumVersion`, `ninjaMinimumVersion`, `msvcMinimumVersion`,
  `msvcReferenceVersion`) исключены из автообновления по той же схеме и с записанной причиной:
  граница — решение о поддерживаемой базе, а не dependency update. Автоматика обновляет пины того,
  что фундамент использует (.NET SDK, OpenCV, latest stable CMake), и не двигает минимумы.
- OpenCV pin автоматизирован частично и осознанно: версия и URL обновляются одной заменой (URL
  обязан содержать ту же версию), а `opencv.sha256` сохраняется и обновляется вручную в том же PR —
  хэш скачиваемого артефакта Renovate вычислить не может, а расхождение обязано валить сборку.
- Self-hosted запуск не вводится: используется hosted Renovate app, поэтому
  `.github/workflows/renovate.yml` не создаётся. Workflow появится только вместе с реальным
  требованием self-hosted запуска, а не «на всякий случай».
- Lock-файлы NuGet поддерживаются в актуальном состоянии `lockFileMaintenance` (обновление
  делегируется NuGet CLI); пины пакетов остаются точными, диапазоны не вводятся.
