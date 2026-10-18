# Игровая геометрия, распознавание и безопасный tap

Capability работает с уже открытым Azur Lane Global/EN на exact MuMu identity. Она не определяет
готовность всех экранов игры и не реализует логин или игровой прогресс. Через
[Agent CLI](../reference/agent-cli.md) доступны наблюдение известных ассетов, read-only проверка
input mapping и обратимый сценарий открытия Dock с подтверждённым возвратом.

## Владельцы

- [Core/Game](../../src/AzurPilot.Core/Game/) содержит типы координат, provenance, однократное
  разрешение `GameTap` и общий `GameUiActionRunner`.
- [Windows/Game](../../src/AzurPilot.Windows/Game/) связывает существующий screenshot host с
  native recognizer. `NativeFrame` остаётся единственным владельцем полного RGB8-кадра.
- [Каталог](../../assets/game/catalog.json) владеет смысловыми ID, crop provenance, вариантами,
  режимами сопоставления, ROI, порогами, безопасными областями и сроками действий.
- [C ABI](../../native/include/azurpilot_native_abi.h) владеет формой match result и бюджетами.
- `AzurPilotOperatorService` разрешает exact endpoint и держит существующую межпроцессную аренду
  на протяжении полного сценария. Вложенные шаги аренду повторно не захватывают.

## Координаты и native поиск

`GameGeometry` отделён от диагностических `ScreenshotDiagnosticRegions`. Logical reference space
описывает UI; физическое разрешение приходит из metadata native frame. Поддерживается точный
landscape 16:9 от 720p до 4K. Границы ROI округляются наружу, безопасной области — внутрь.
Clipping, автоматическое вращение и догадки о letterboxing отсутствуют. Поддержка 8K не заявлена:
нынешние бюджеты декодера и проверенный диапазон геометрии для неё недостаточны.

OpenCV читает ROI через заголовок `cv::Mat` без копирования полного кадра. Масштабируется маленький
шаблон, а не screenshot. Каталог кеширует только encoded crops; временные native шаблоны и score map
освобождаются в одном синхронном вызове. Декодер проверяет отдельный template budget до выделения RGB.
Текущие плоские кнопки используют яркость, заголовок Dock — RGB. Для этих непрозрачных crops маска
не нужна. Добавление нового ассета требует проверки его собственных положительных и отрицательных
образцов; совместимость произвольных UI-версий не предполагается.

`Unique` требует достаточных correlation, pixel error и отрыва от второго независимого кандидата.
`Absent`, `Ambiguous`, `GeometryRejected` и `TechnicalFailure` сохраняются отдельно. Score не является
вероятностью. Второй максимум ищется вне окрестности первого размером в половину шаблона по каждой
оси; соседние положения одного и того же элемента не считаются разными кнопками.
Официальная семантика метода: [OpenCV matchTemplate](https://docs.opencv.org/4.x/df/dfb/group__imgproc__object.html).

## Ввод и доказательство эффекта

Перед tap свежий source должен быть `Unique`, а независимый postcondition — `Absent`.
Все исключающие признаки из action должны быть доказанно `Absent` до ввода и после него. В текущем
переходе кнопка подтверждения выбора кораблей исключает selection mode: общий Dock header сам по
себе не доказывает обычный Dock. Эта кнопка не имеет safe tap и никогда не нажимается. Проверяются
target, физические размеры, внутренняя безопасная область, monotonic возраст от начала capture,
foreground Global/EN и полноэкранное окно Android. `dumpsys input` должен доказать одинаковые
logical/physical frames и device size для display 0; rotation согласуется с конфигурацией окна.
Неизвестный формат, конфликтующие viewports, inset/app bounds, obscured окно или compat scaling
отклоняются. Shell input получает координаты текущего logical display; поворот touch hardware
повторно не применяется. Основание —
[AOSP InputShellCommand](https://github.com/aosp-mirror/platform_frameworks_base/blob/master/services/core/java/com/android/server/input/InputShellCommand.java).

Только Core создаёт `GameTap`; Windows adapter повторно проверяет срок и атомарно расходует разрешение.
Один tap адресуется тем же bundled ADB и exact endpoint, с явным display 0. Raw XY через CLI отсутствует.
Произвольный swipe пока не публикуется: у текущего проверенного сценария нет такого действия.

После tap новый capture должен начаться после завершения команды, принадлежать тому же target и
разрешению и независимо подтвердить новый признак с исчезновением source. Проверка ограничена
дедлайном и не повторяет tap. `Unknown`, отказ ADB и отмена сохраняют возможный частичный эффект.
Успех ADB не подтверждает переход. При отмене или отказе автоматический возврат не выполняется;
успешное открытие и подтверждённое возвращение отражаются отдельно.

Аренда координирует AzurPilot, но не посторонние ADB tools и действия пользователя. Проверка viewport
уменьшает окно гонки; аппаратной атомарности между наблюдением и вводом Android не предоставляет.

## Сопоставление с оригиналом

Предметный ориентир — `AzurPilot-private-Ru`, ветка `personal/stable`, а не её `main`.

| Механизм оригинала | Решение текущей capability |
| --- | --- |
| `module/device/screenshot.py`: ориентация, screenshot interval, время жизни изображения | Сохранены проверка размеров/ориентации и свежесть. Захват остаётся existing Windows host → native frame; глобального уменьшения до 720p нет. |
| `module/base/button.py`: отдельные `area`/`button`, ROI offset, luma и проверка цвета | Search ROI и safe tap принадлежат данным; найденное смещение переносит безопасную область. Для разных crops выбраны luma/RGB и независимая pixel-error проверка. |
| `module/base/template.py`, `utils.py`: scaled template и варианты | Масштабируется только template; варианты хранятся в каталоге. Общего снижения порога на другом разрешении нет. |
| `module/base/resource.py`: кеширование и освобождение ресурсов | Маленький bounded кеш encoded templates и RAII временных native объектов; полного managed RGB owner нет. |
| `module/device/control.py`, `method/adb.py`: button → координата → Android input | Safe center однократного разрешения идёт через existing bundled ADB/exact endpoint; случайные координаты и старый device stack не переносятся. |
| `module/ui/ui.py::ui_click`: appear → click → check | Один tap и отдельные subsequent observations под общим дедлайном. Цикл повторных кликов/автоматического восстановления не переносится. |

Проверки и воспроизведение ассетов: [game-ui verification](../testing/game-ui.md).
