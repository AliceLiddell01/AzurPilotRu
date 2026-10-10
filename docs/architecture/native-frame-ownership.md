# Native-owned PNG frame

## Граница и текущий scope

`AzurPilot.Windows` передаёт временный `ReadOnlySpan<byte>` через source-generated `LibraryImport` в существующую native boundary. DLL декодирует PNG из памяти, владеет итоговым RGB8 frame и возвращает opaque handle. Managed код получает только проверенные metadata; полный decoded pixel buffer не копируется в C#.

Эта capability принимает PNG bytes и не захватывает экран, не обращается к ADB/MuMu, не выполняет region scan или распознавание.

Единственный нормативный контракт C ABI, его version и числовые пределы определены в [`native/include/azurpilot_native_abi.h`](../../native/include/azurpilot_native_abi.h). ABI v2 сохраняет диагностические exports ABI v1 и меняет контракт только явно: handle создаётся decode export-ом, читается через metadata export и освобождается парным release export-ом DLL. Несовместимость проверяется до вызова frame API.

## Decode и PNG policy

Перед `cv::imdecode` native code проверяет PNG signature, границы и CRC chunks, единственный IHDR, размеры, порядок chunks, наличие IDAT/IEND и разрешённые параметры заголовка. Все расчёты для stride и RGB payload выполняются в widened integer arithmetic и сравниваются с лимитами ABI до decode. После decode повторно проверяются dimensions, type, contiguity, stride и длина payload.

| PNG input | Результат |
| --- | --- |
| 8-bit truecolor RGB без alpha | Decode через `cv::IMREAD_COLOR_RGB` в contiguous RGB8 |
| 8-bit RGBA, каждый alpha byte равен 255 | Decode в native BGRA и conversion в RGB8 |
| RGBA с любым прозрачным pixel | Отклонение как неподдерживаемый frame |
| Grayscale, palette/indexed, 16-bit, interlaced, `tRNS`, APNG или другой формат | Отклонение как неподдерживаемый или некорректный PNG согласно ABI status |

CRC, заголовок и chunk structure ограничивают вход до передачи OpenCV; сами сжатые image data проверяет OpenCV decoder. Отказ не создаёт handle. Caller buffer используется только синхронно во время вызова и не сохраняется frame-ом.

## Metadata и lifetime

`AzurPilotNativeFrameInfo` возвращает реальные `width`, `height`, `stride_bytes`, `byte_length` и `pixel_format`. RGB8 использует три байта на pixel и непрерывные строки; размеры не подменяются логическим разрешением экрана. На ошибке `out_info` обнуляется.

Native frame владеет `cv::Mat` RGB8. Public `NativeFrame` не предоставляет native handle или pixel pointer; `GetInfo()` передаёт typed `SafeHandle`, чтобы runtime удерживал ресурс до конца native вызова. `Dispose()` идемпотентно закрывает handle, а release export освобождает его аллокатором DLL. Произвольный stale или чужой native pointer не является восстанавливаемым входом; managed потребитель должен использовать `SafeHandle`.

Expected invalid, unsupported и oversized image statuses отображаются через `NativeBoundaryFailureMapper` в `native_frame_invalid` с ограниченными structured details. Allocation и неожиданные native failures остаются boundary/internal failures и не маскируются как ошибка изображения.

## Проверка

Из каталога `native/`:

```text
cmake --workflow --preset native-x64-debug
cmake --workflow --preset native-x64-release
cmake --workflow --preset native-x64-asan
```

Native smoke вызывает реальные exports из DLL через публичный header и PNG fixtures. Internal ownership test компилирует тот же decoder source с test-only counters и проверяет красный/синий RGB channel order, alpha policy, отказ на malformed/unsupported/oversized входах, а также возврат live-frame/RGB-byte counters к baseline. Managed `NativeInteropTests` загружает собранную DLL, проверяет metadata, expected failure mapping, GC, конкурентный `GetInfo`/`Dispose`, idempotent disposal и повторные create/release циклы. Негативные тесты отсутствующей и несовместимой DLL остаются отдельными managed tests.

ASan preset пишет только в отдельный `artifacts/native/asan` и копирует MSVC ASan runtime рядом с его тестовыми binary; обычный native runtime staging не меняется. OpenCV binary приходит из закреплённого пакета и собран без ASan instrumentation, поэтому проверки не покрывают его внутреннее исполнение полностью. MSVC ASan проверяет нарушения памяти в инструментированном коде, но не является leak detector. Scoped counters отдельно доказывают возврат числа живых frame owners и размера принадлежащего им RGB payload к baseline; они не утверждают отсутствие утечки во всех внутренних аллокациях OpenCV.
