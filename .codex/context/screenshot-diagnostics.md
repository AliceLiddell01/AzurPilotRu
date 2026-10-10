# Screenshot diagnostics — agent-critical контракт

- Захват использует exact MuMu identity/Android endpoint из существующего выбора и тот же bundled ADB/
  `WindowsProcessRunner`; stdout остаётся байтами. Не добавляй device discovery или lifecycle mutation.
- Успешный capture требует полного bounded PNG, успешного decode и проверенных metadata. Encoded PNG не
  попадает в diagnostics/logs и сам по себе не доказывает состояние игрового UI.
- `1280×720` — логическое reference space только `ScreenshotDiagnosticRegions`; физические размеры
  берутся из `NativeFrameInfo`. Mapping принимает только точный landscape 16:9, использует half-open
  bounds, округляет наружу и не делает clipping или letterbox detection.
- Region statistics выполняются существующим `NativeFrame`/`SafeHandle` и native ABI. Не копируй RGB
  frame в managed memory и не вводи второй frame owner.
- Startup diagnostics не захватывают screenshot. Локальные samples читаются только на верхнем уровне;
  ограничение перечисления задаёт `ScreenshotSampleRunner`. Evidence не содержит пользовательских путей,
  имён и изображений.
- Подробности архитектуры и локального acceptance: [`docs/architecture/screenshot-diagnostics.md`](../../docs/architecture/screenshot-diagnostics.md), [`docs/testing/screenshot-acceptance.md`](../../docs/testing/screenshot-acceptance.md).
