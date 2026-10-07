# Verification — agent-critical контракт

Подробное описание проверок находится в `../../docs/testing/verification.md` и
`../../docs/testing/android-acceptance.md`. Фактические tests и CI workflows остаются источником
текущего набора проверок.

## Общий принцип

Verification должна доказывать supported behavior и границы, а не конкретный implementation snapshot.

- Не ослабляй assertion ради прохождения нового кода.
- Не дублируй production logic в test настолько, чтобы обе стороны могли ошибиться одинаково.
- Negative/error paths проверяются там, где их нарушение меняет correctness.
- Repository-contract tests защищают глобальные запреты вроде machine-specific paths.
- Канонические пользовательские build/test команды документируются в `docs/getting-started.md`, а не
  копируются во все context-файлы.

## Native/managed boundaries

- Native CTest обязан исполнять project-owned native code через C ABI; пустая/фиктивная проверка не
  считается доказательством.
- Managed interop tests используют production native boundary и отдельно проверяют негативные сценарии
  отсутствия/несовместимости runtime.
- Hosted CI подтверждает reproducible build и управляемые tests, но не симулирует real device/provider.

## MuMu

Managed tests проверяют orchestration через управляемые external seams: identity, fail-closed parsing,
state transitions, postcondition, timeout/cancellation и отсутствие опасных fallback.

Real MuMu acceptance — отдельное локальное evidence конкретной Windows/MuMu environment и не заменяется
моками в CI.

## Android/ADB и game lifecycle

Managed tests должны защищать:

- exact endpoint и target-explicit команды;
- отсутствие global ADB-server mutation;
- readiness transport/shell/boot;
- read-only diagnostics;
- tri-state game facts и fail-closed state derivation;
- launcher resolution без догадок;
- lifecycle postcondition, idempotency, timeout/cancellation и mutation serialization;
- bounded diagnostics/details.

Hosted CI не доказывает реальную MuMu/ADB/Android/game среду.
`AzurPilot.AndroidAcceptance` проверяет её отдельно на exact instance, восстанавливает исходное состояние
и не получает разрешение на операции вне product capability. Подробности — в
`../../docs/testing/android-acceptance.md`.

## Граница доказательства

Всегда явно различай:

- что доказано deterministic tests;
- что доказано hosted CI;
- что наблюдалось real acceptance;
- что вообще не входит в текущую capability.

Не превращай единичное runtime observation в универсальный invariant без дополнительного основания.
