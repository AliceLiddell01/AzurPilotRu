# Диагностика screenshot и областей изображения

Эта capability связывает exact MuMu instance и его Android endpoint с бинарным PNG, существующим
native-owned RGB8 frame и статистикой явно названной области. Она не определяет содержимое экрана и
не является игровым UI-recognition.

## Владельцы и поток данных

`AndroidWindowsHost` реализует `IAndroidScreenshotHost` поверх того же bundled ADB и общего
`WindowsProcessRunner`, которые используются остальным Android-слоем. Capture принимает уже
разрешённые `MuMuInstanceId` и `AndroidEndpoint`: команда строится ровно как
`adb -s <endpoint> exec-out screencap -p`. Здесь нет shell-wrapper, глобального выбора устройства,
неявного `connect` или lifecycle mutation. Вызывающая сторона отвечает за связь identity и endpoint.

Stdout читается бинарно через stream границы процесса; stderr остаётся ограниченным текстом. Размер
encoded PNG ограничен нормативным лимитом native ABI, который managed сторона читает из той же DLL.
Результат процесса считается пригодным только при полном выводе и успешном exit code. Затем существующий
`AzurPilotNativeBridge` декодирует PNG в RGB8 frame во владении DLL и проверяет его metadata. Encoded
байты используются временно и не сохраняются в `CapturedAndroidScreenshot`.

`CapturedAndroidScreenshot` владеет frame и несёт exact identity/endpoint, UTC-момент завершения
успешной команды и decode, monotonic длительность команды, число PNG-байтов, exit code и frame metadata.
Потребитель освобождает capture через `Dispose()`. ROI scan выполняется через существующий `NativeFrame`
и его `SafeHandle`; полная матрица пикселей в managed память не копируется.

## Геометрия и named regions

`ScreenshotDiagnosticRegions` владеет небольшим набором областей этого диагностического consumer-а.
Его reference space `1280×720` задаёт только логические координаты именованных областей; физические
`width` и `height` берутся из metadata декодированного native frame. Это не разрешение MuMu, default
размер capture или глобальное ограничение проекта.

Прямоугольники полуоткрытые: левые и верхние границы включены, правые и нижние исключены. Левые и
верхние края отображаются округлением вниз, правые и нижние — вверх. Пустые, перевёрнутые и выходящие за
reference space области отклоняются без clipping. Поддерживается только landscape frame с точным
соотношением сторон `16:9`; иная ориентация или соотношение сторон отклоняются. Mapping относится ко
всему декодированному растру. Letterboxing внутри подходящего растра не обнаруживается и не является
доказанной причиной отказа: автоматическое обнаружение viewport и анализ содержимого не выполняются.

`ScreenshotRegionScanner` возвращает provenance источника, фактический размер/формат кадра, logical
bounds, вычисленные физические pixel bounds и native RGB statistics. Native ABI использует zero-copy
`cv::Mat` view одного ROI и возвращает число пикселей, средние и population standard deviation каналов
RGB. Ошибка анализа превращается в ограниченный application failure. Результат scan не утверждает, что
на кадре открыт конкретный игровой экран или что UI игры готов.

Сейчас именованные области — `FullFrame` и `Center`. Новые значения добавляются только при наличии
реального диагностического потребителя; пользовательского registry координат нет.

## Сохранение

Capture и scan сами не записывают изображение. Acceptance-инструмент может по явному
`--report-artifact <name.json>` создать новый JSON aggregate в игнорируемом
`artifacts/screenshot-diagnostics/`. Имя ограничено одним именем файла, существующий файл не
перезаписывается. PNG, исходные sample-файлы, их имена и пользовательские каталоги в артефакт не входят.
Startup diagnostics остаются read-only и не запускают screenshot capture.

Подробный порядок локальной и live-проверки: [screenshot acceptance](../testing/screenshot-acceptance.md).
