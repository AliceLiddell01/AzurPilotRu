# Проверка Android/ADB и lifecycle Azur Lane Global/EN

Этот документ подробно описывает, какие свойства Android-слоя проверяются managed tests и что дополнительно
доказывает real Windows acceptance. Короткие agent-critical правила проверки находятся в
`../../.codex/context/verification.md`; фактический набор tests остаётся источником текущего состояния.

## Hosted/managed verification

Managed tests прогоняют production orchestration через управляемые external-boundary seams. Они не требуют
установленной MuMuPlayer, реального ADB или игры.

Проверки должны доказывать как минимум:

- exact ADB endpoint разрешается из сведений выбранного экземпляра, а не вычисляется и не подменяется;
- device-команды target-explicit и адресуют exact endpoint;
- глобальные операции над ADB server не используются;
- readiness различает transport, shell и завершение загрузки Android;
- read-only диагностика не выполняет `connect` и другую mutation;
- неизвестное наблюдение не превращается в отрицательный факт;
- package presence, process state и foreground независимы;
- launcher component не выбирается догадкой;
- `start`, `stop`, `restart` подтверждают postcondition наблюдением, а не exit code mutation;
- deadline/cancellation и serialization mutation сохраняют контракт;
- внешние diagnostics/evidence bounded и не утаскивают machine-specific пути или полный вывод.

Эти tests доказывают корректность production-кода относительно управляемых границ. Они не доказывают
поведение конкретной установки MuMu, реального ADB или конкретной версии Android.

## Real Windows acceptance

Инструмент: `tests/AzurPilot.AndroidAcceptance`.

Запуск выполняется на Windows с установленной MuMuPlayer и Azur Lane Global/EN. Требуется точный instance:

~~~text
dotnet run --project tests/AzurPilot.AndroidAcceptance -c Release -- --instance mumu:<index>
~~~

До запуска managed acceptance должен существовать native runtime соответствующей конфигурации согласно
`../getting-started.md`.

Acceptance использует production Android surface и проверяет реальную цепочку:

1. выбранный MuMu instance существует и находится в допустимом host-side состоянии;
2. bundled ADB обнаружен;
3. exact endpoint разрешён из сведений этого instance;
4. transport, shell и `sys.boot_completed` доказывают готовность Android;
5. пакет Global/EN наблюдается на том же target;
6. начальное состояние игры фиксируется до mutation;
7. `stop`, `start`, `restart` проверяются через независимое наблюдение итогового состояния;
8. после прогона восстанавливается исходное состояние игры.

Для `restart` недостаточно увидеть «игра снова запущена»: acceptance дополнительно должен иметь evidence,
что операция действительно дала новый runtime-процесс/набор процессов, если текущая реализация использует
это как доказательство перезапуска.

## Безопасность acceptance

Инструмент не должен расширять продуктовую capability ради теста. В частности, acceptance не получает
разрешение:

- выполнять `adb kill-server` или глобальный reconnect;
- работать с другим target или package;
- устанавливать/удалять APK;
- очищать данные/кэш;
- менять permissions или настройки эмулятора;
- выполнять input, screenshot, vision/OCR;
- оставлять изменённое состояние игры после штатного завершения.

Если restoration не доказан, прогон не считается чистым успешным evidence.

## Что считать evidence

Результат real acceptance относится к конкретной машине, установке и моменту времени.

В долговременную документацию полезно переносить:

- устойчивый обнаруженный контракт;
- новый подтверждённый edge case;
- ограничение provider/API, которое влияет на архитектуру;
- безопасный способ доказать postcondition.

Не нужно переносить в `.codex/context/` конкретные версии MuMu/ADB/Android, timestamps, полный stdout/stderr
или текст единичного отчёта. Такой материал остаётся PR/acceptance artifact либо используется как основание
для краткого устойчивого invariant.
