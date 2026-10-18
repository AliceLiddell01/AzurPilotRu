# Native PNG-кадр: критичный контракт для агента

- `native/include/azurpilot_native_abi.h` — единственный владелец версии, раскладки, статусов и числовых лимитов C ABI. До создания кадра проверяй версию и доступность обязательных release/ROI exports в той же DLL через `LibraryImport`.
- Native DLL владеет декодированными пикселями RGB8 за непрозрачным дескриптором. В управляемом коде ресурс живёт только за `NativeFrameSafeHandle`; не раскрывай дескриптор или указатель на пиксели и не копируй полный декодированный кадр в C#.
- `GetInfo()` и `ScanRegion()` после завершённого `Dispose()` отказывают до вызова native ABI; проверка состояния не заменяет защиту `SafeHandle` при конкурентном освобождении. ROI принимает физические полуоткрытые границы и не выдаёт пиксельный буфер.
- `MatchTemplate()` использует тот же lifetime lock и SafeHandle; обязательный match export проверяется
  до создания frame. Игровые правила и ассеты принадлежат `game-ui.md`.
- Поддерживаемое подмножество PNG и политика обработки прозрачности — часть контракта. Не расширяй декодер молча и не считай каналы RGB выходом OpenCV по умолчанию: сохраняй проверку порядка каналов.
- Подробная политика декодирования, времени жизни и проверки: [`docs/architecture/native-frame-ownership.md`](../../docs/architecture/native-frame-ownership.md).
- Logical-to-physical mapping и named-region owner находятся в диагностическом слое: [`screenshot-diagnostics.md`](screenshot-diagnostics.md).
