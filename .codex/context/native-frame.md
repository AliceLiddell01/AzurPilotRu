# Native PNG frame: agent-critical contract

- `native/include/azurpilot_native_abi.h` — единственный владелец версии, layout, статусов и числовых лимитов C ABI. Согласованно проверяй обе стороны до вызова frame API.
- Native DLL владеет декодированными RGB8 pixels за opaque handle. В managed коде ресурс живёт только за `NativeFrameSafeHandle`; не раскрывай handle/pixel pointer и не копируй полный decoded frame в C#.
- Поддерживаемое PNG-подмножество и alpha policy — часть контракта. Не расширяй decoder молча и не считай каналами RGB выход OpenCV по умолчанию: сохраняй проверку порядка каналов.
- Подробная политика decode, lifetime и verification: [`docs/architecture/native-frame-ownership.md`](../../docs/architecture/native-frame-ownership.md).
