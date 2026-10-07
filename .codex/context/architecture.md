# Архитектура проекта — agent-critical контракт

Подробное описание структуры и текущих capability находится в `../../docs/architecture/overview.md`.
Этот файл фиксирует только границы, нарушение которых опасно при разработке.

## Платформа и boundaries

- Продукт поддерживает только Windows x64.
- Managed boundaries ровно три: `AzurPilot.Core`, `AzurPilot.Windows`, `AzurPilot.App`.
- Отдельная native boundary — C++/OpenCV через versioned C ABI.
- `tests/**` — инструменты проверки; они не являются product boundary и не поставляются как продукт.
- Не создавай новый project/layer только ради организационного удобства: нужна реальная граница
  ответственности.

## Направление зависимостей

`AzurPilot.App` → `AzurPilot.Windows` → `AzurPilot.Core`.

- Core не зависит от Windows API, App или Windows project.
- Windows не зависит от App.
- Managed/native взаимодействие идёт только через C ABI из `native/include/azurpilot_native_abi.h`.
- Через C ABI не проходят C++/STL ownership-типы и исключения.
- Новая capability расширяет существующего владельца, а не создаёт параллельный framework/source of truth.

Текущее распределение capability:

- Core — доменные/application contracts и orchestration, независимые от Windows;
- Windows — platform adapters, process/filesystem/registry/native/ADB/MuMu integration;
- App — composition root, startup, presentation output, logging и runtime diagnostics.

## Product и repository tooling

Build/CI/dependency automation обслуживают репозиторий и не являются product CLI.
Будущие product CLI/REPL/agent CLI не получают команды `build`, `repair`, `update` только потому, что
такие операции есть у developer tooling.

## Source of truth

- Версии SDK/packages/toolchain принадлежат manifests, перечисленным в `INDEX.md`.
- Форма C ABI принадлежит native header.
- Конфигурация, failures, diagnostics, MuMu и Android имеют собственные context owners.
- Канонические команды сборки/запуска для человека находятся в `docs/getting-started.md`.
- Не копируй machine-readable значения в новый prose-owner.

## Запреты

- Machine-specific абсолютные пути и значения конкретной машины не фиксируются в source/config.
- `1280x720` не является фундаментальным разрешением проекта; размеры будущего кадра — данные.
- Не создавай placeholder-abstractions/config/failures/docs под capability, которой ещё нет.
- Не смешивай test/acceptance CLI с будущим product CLI.

## Текущее capability-состояние

Реализованы:

- MuMu discovery/identity/host lifecycle;
- Android/ADB exact-target readiness;
- lifecycle Azur Lane Global/EN.

Не реализованы и не должны имитироваться placeholder-кодом: input, screenshot/vision pipeline,
OCR/ONNX/GPU, UI readiness игры, product CLI/REPL/agent CLI.

Подробности конкретной capability читай у её context owner и в `docs/architecture/`.
