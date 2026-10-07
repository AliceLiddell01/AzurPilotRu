# Android/ADB и lifecycle Azur Lane Global/EN — agent-critical контракт

Этот файл содержит только ограничения, которые агент должен учитывать при изменении Android-слоя.
Развёрнутая архитектура, обоснования, примеры и наблюдения реальной среды находятся в
`../../docs/architecture/android-game-lifecycle.md`. Фактическая реализация и tests остаются источником
истины для текущего состояния.

## Граница capability

- Capability адресует только уже выбранный экземпляр MuMu и единственный поддерживаемый клиент
  Azur Lane Global/EN (`com.YoStarEN.AzurLane`).
- MuMu discovery/identity/host lifecycle принадлежат `mumu-lifecycle.md`; Android-слой не создаёт
  параллельного владельца этих решений.
- UI readiness, input, screenshot, vision, OCR/ONNX/GPU, APK install/uninstall, permissions и настройки
  эмулятора в эту capability не входят.
- Product identity, ADB endpoint и lifecycle timings не становятся пользовательскими настройками только
  ради удобства реализации.

## Identity и ADB endpoint

- Endpoint относится к точной identity выбранного экземпляра и разрешается из фактических сведений
  этого экземпляра.
- Запрещены: вычисление порта из `vmindex`, fallback на `127.0.0.1`, выбор первого устройства из
  `adb devices`, поиск «похожего» target-а.
- Неразрешённый или непригодный endpoint — явный отказ; неизвестное значение не подменяется default.
- Все операции наблюдения и mutation после разрешения endpoint адресуются тому же exact target.

## ADB executable и команды

- Используется bundled `adb.exe` обнаруженной установки MuMu. `PATH`, `ANDROID_SDK_ROOT`,
  пользовательский override и цепочка fallback-поиска не являются частью capability.
- Bundled ADB должен быть обнаружен и проверен до первой ADB-команды. Нарушение порядка со стороны
  вызывающего кода — programming error, а не состояние устройства.
- Подключение выполняется только как target-local `connect <endpoint>`.
- Все device commands используют `-s <endpoint>`.
- `adb kill-server`, глобальный reconnect и другие операции, влияющие на посторонние targets, запрещены.
- Одна логическая ADB-команда соответствует одному запуску процесса; shell-wrapper не вводится.
- Текущий Android host рассчитан на одну обнаруженную установку MuMu в процессе и кэширует найденный
  bundled ADB без ключа установки. Поддержку нескольких установок нельзя добавлять поверх этого кэша:
  сначала должна быть изменена ownership/cache boundary.
- Внешний stdout/stderr и evidence должны оставаться bounded; абсолютный путь bundled ADB и полный
  вывод команд не становятся application details/log payload.

## Readiness и read-only диагностика

Mutating readiness доказывает последовательно:

1. exact transport доступен;
2. shell отвечает на exact target;
3. `sys.boot_completed` подтверждает завершённую загрузку Android.

- Код выхода команды сам по себе не доказывает readiness.
- Recovery допускает только target-local действия этой capability.
- Startup/diagnostics работают read-only: они не выполняют `connect`, не запускают Android/MuMu и не
  мутируют игру.
- «Не удалось наблюдать» и «наблюдали отрицательный факт» — разные состояния и не сворачиваются друг
  в друга.

## Состояние игры

`Installed`, `ProcessRunning` и `Foreground` — независимые трёхзначные факты. `null` означает
«не доказано», а не `false`.

- Ошибка запроса пакета не равна отсутствующему пакету.
- Неизвестный список процессов не равен пустому списку.
- Нераспознанный foreground не равен «другое приложение».
- Итоговое состояние игры не выводится как доказанное, если необходимый факт неизвестен.
- Launcher component используется как адрес mutation запуска, но postcondition переднего плана
  подтверждается package identity, а не точным именем launcher activity.
- Неоднозначный или недоказанный launcher не выбирается догадкой.

## Lifecycle игры

- `start`, `stop`, `restart` завершаются успехом только после независимого наблюдения требуемого
  postcondition.
- Process exit code mutation является evidence, но не заменяет postcondition.
- Идемпотентный переход не выполняет лишнюю mutation, если требуемое состояние уже доказано.
- Mutation одной пары `endpoint + package` сериализуются; read-only наблюдение не требует mutation lock.
- Deadline и cancellation принадлежат одной операции и не продлеваются скрытыми retry.
- Fixed sleep не является доказательством состояния.
- Не добавляй recovery, который маскирует `Unknown` или воздействует на другой target/package.

## Отказы и ownership

- Значения application failure codes принадлежат `ApplicationFailure` в Core, а process exit mapping —
  `AzurPilotExitCode` в App. Этот файл не хранит второй каталог чисел или кодов.
- Платформенный adapter сообщает только факты своей boundary; Core orchestration не превращает
  ожидаемый host failure в `InternalError`.
- Различай failure команды, timeout операции, недоказанное состояние и доказанный negative fact.
- Новая форма failure/details должна иметь одного владельца; не синтезируй параллельные форматы ради
  удобства конкретного call path.

## Проверяемость

Managed tests должны защищать перечисленные контракты через external-boundary seams, а не snapshot
конкретной реализации. Hosted CI не доказывает поведение реальной MuMu/ADB/игры.
Real Windows acceptance — отдельное доказательство конкретной среды и не должен превращаться в
постоянный agent context. Подробности проверки находятся в `verification.md` и `docs/testing/`.
