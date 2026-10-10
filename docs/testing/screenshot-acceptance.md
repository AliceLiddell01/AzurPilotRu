# Проверка screenshot capture и region scan

Screenshot acceptance дополняет управляемые tests двумя локальными проверками: read-only scan
ограниченной выборки пользовательских PNG и live capture с exact Android target. Оба режима используют
production decoder, geometry и scanner. Ни один режим не является проверкой UI-recognition.

## Локальные PNG samples

На Windows передай каталог явно:

```text
dotnet run --project tests/AzurPilot.AndroidAcceptance -c Release -- --screenshot-samples <каталог>
```

Перечисляются только файлы верхнего уровня, максимум 500. Выбор детерминирован: кандидаты группируются
по физическому размеру и основным IHDR-свойствам PNG; для каждой группы берутся до трёх файлов,
упорядоченных по размеру файла и имени. Сначала проверяются сигнатура/IHDR, затем выбранные файлы
читаются ограниченно и передаются production native decoder. Успех требует полного decode и успешных
`FullFrame` и `Center` scan. Неподдерживаемые PNG, некорректные заголовки и неподходящая геометрия
считаются отказами, а не пропускаются как подтверждённый sample.

Вывод содержит только обезличенные агрегаты: количества, размеры, выбранную/декодированную выборку и
фиксированные коды отказа. Имена файлов, абсолютные пути, текст исключений и изображения не выводятся и
не сохраняются. Входной каталог открывается только для чтения. При необходимости можно явно сохранить
отчёт в новый игнорируемый JSON-файл:

```text
dotnet run --project tests/AzurPilot.AndroidAcceptance -c Release -- --screenshot-samples <каталог> --report-artifact samples.json
```

## Явная подготовка ADB transport

Если read-only preflight сообщает `android_transport_absent`, сначала можно явно подготовить
transport для точного работающего MuMu instance, затем отдельной командой запустить screenshot live:

```text
dotnet run --project tests/AzurPilot.AndroidAcceptance -c Release -- --prepare-transport --instance mumu:<index>
dotnet run --project tests/AzurPilot.AndroidAcceptance -c Release -- --screenshot-live --instance mumu:<index> --captures 3
```

Режим `--prepare-transport` проверяет exact identity, состояние `Running`, bundled ADB и endpoint,
разрешённый для выбранного экземпляра. Для `Absent` он может выполнить ровно один target-local
`adb connect`, после чего независимо проверяет `get-state=device`, доступность shell и
`sys.boot_completed=1` в рамках существующего ограниченного readiness flow. Если transport уже
`Device`, повторный `connect` не выполняется. При `Offline`, `Unknown`, неизвестном endpoint или
недоказанном boot команда останавливается с причиной; глобальный ADB server, MuMu и игра не
перезапускаются и не изменяются.

`--screenshot-live` остаётся read-only и никогда не выполняет `connect`. При `Absent` он возвращает
`BLOCKED` с `android_transport_absent` и безопасной командой подготовки. `Offline`, `Unknown` и
недоказанная готовность Android получают отдельные причины. Это preflight-отказ до `screencap` и
native decode; его нельзя трактовать как ошибку PNG или decoder. Если подготовка не нужна, запускай
только screenshot-команду.

Аудит подготовки допускает только однократный `connect` к exact endpoint и ограниченные команды
наблюдения готовности. Screenshot-аудит разрешает exact `exec-out screencap -p`, но запрещает
`connect` и игровые mutations. Подготовительный режим не запускает приложение и не меняет его
состояние.

## Live MuMu capture

На уже подготовленной Windows-машине укажи конкретную identity; по умолчанию выполняются три
последовательных capture, максимум пять:

```text
dotnet run --project tests/AzurPilot.AndroidAcceptance -c Release -- --screenshot-live --instance mumu:<index>
```

Live acceptance read-only проверяет выбранный MuMu instance, bundled ADB, точный endpoint, состояние
Android и foreground Azur Lane Global/EN. Только после доказательства предусловий выполняется
`adb -s <endpoint> exec-out screencap -p`; результат проходит native decode, metadata validation,
`Center` geometry mapping и native RGB scan. В конце повторно наблюдаются host/Android/game states и
проверяется, что они не изменились. При `Background`, `Unknown`, неподготовленном Android или другом
недоказанном предусловии прогон завершается отказом; lifecycle-команды для подготовки не выполняются.

Опции `--captures 1..5` задают длину серии. `--report-artifact <name.json>` сохраняет только структурный
JSON aggregate в `artifacts/screenshot-diagnostics/`; новый файл создаётся без перезаписи. PNG не
сохраняется. Без этой опции отчёт остаётся в stdout.

Live report содержит build identity, наблюдённую identity/endpoint, начальные и конечные состояния,
число capture, размеры и RGB8 format frame, actual pixel bounds, ROI statistics и ограниченные коды
отказа. При сбое capture или scan отдельные поля `captureFailureCode` и `captureFailureReason` сохраняют
точный bounded отказ источника, даже если финальная проверка обнаружила изменение состояния устройства.
Это доказательство конкретной среды и запуска; оно не заменяет fixture tests или sample scan.

## Ограничения доказательства

Native и managed tests подтверждают байтовую границу, ABI, lifetime, geometry и statistics на
контролируемых fixtures. Sample scan подтверждает совместимость выбранных локальных PNG с текущим
decoder и regions. Только live acceptance подтверждает поддержку `exec-out screencap -p` и фактический
путь через exact MuMu/ADB target. Ни один из этих уровней не доказывает экранное состояние по цветам,
OCR, распознавание игровых ресурсов или управление игрой.
