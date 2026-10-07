# MuMu lifecycle — agent-critical контракт

Подробная архитектура и runtime observations находятся в `../../docs/architecture/mumu-lifecycle.md`.
Этот файл оставляет только правила, которые легко нарушить при изменении capability.

## Граница

- MuMu owner отвечает за обнаружение установки, control surface, stable instance identity,
  host-side state и start/stop/restart emulator instance.
- Android readiness/game lifecycle принадлежат `android-game-lifecycle.md`.
- Test acceptance tooling не является product CLI.

## Installation и control surface

- Поддерживаемая установка должна быть доказана через определённые production discovery sources;
  первая «похожая» директория или executable не выбираются эвристикой.
- Неоднозначность установки остаётся неоднозначностью, а не превращается в «not found» или выбор первой.
- Ответ control surface разбирается fail-closed; truncated/непонятный output не считается валидным.
- Внешний вывод bounded и не переносит machine-specific пути в durable diagnostics/details.

## Instance identity и выбор

- Stable identity экземпляра — provider identity `mumu:<vmindex>`, а не display name.
- Явный выбор адресует exact identity.
- `auto` допустим только когда выбор однозначен; при нескольких кандидатах не выбирай первый.
- Не используй ADB endpoint/port как замену MuMu identity.

## State и mutation

- Host-side state выводится только из доказанного ответа control surface.
- Exit code mutation — evidence, но не postcondition.
- `start`, `stop`, `restart` успешны только после наблюдения требуемого terminal state.
- Lifecycle mutation одного instance сериализуются.
- Adapter выполняет ровно запрошенную primitive mutation; orchestration владеет ожиданием/retry policy.

Для `Stopped → Running` разрешён единственный повтор `launch` только если первое действие не дало
наблюдаемого эффекта и instance всё ещё доказанно `Stopped`. Если состояние уже изменилось/стало
неизвестным, повторный blind `launch` запрещён.

## Time/cancellation/recovery

- Команды и операции bounded по времени; fixed sleep не является доказательством состояния.
- Cancellation не должна превращаться в другой failure.
- Не force-kill процессы MuMu и не управляй чужими process trees ради recovery.
- Android/ADB readiness не используется для «лечения» недоказанного host-side state MuMu.
- Real acceptance восстанавливает исходное состояние; конкретные результаты прогона не являются
  постоянным agent context.

Failure codes/details принадлежат `application-failures.md`; этот файл не хранит второй каталог.
