# Проверка игровой capability

Production-контракты и ограничения описаны в [архитектуре](../architecture/game-ui.md).
Общие команды сборки и native workflow принадлежат [getting-started](../getting-started.md).

## Постоянные проверки

`GameGeometryTests` проверяет округление, весь frame, точки на краях и отказ неподдерживаемых
ориентаций/размеров. `GameUiRecognizerTests` использует неперсональные UI-crops на synthetic canvas
для каждого поддерживаемого контрольного разрешения. Эти fixtures доказывают mapping/scaling и ROI,
но не доказывают нативную раскладку UI другого разрешения.
Отдельные crops реальных Depot/Technology headers не должны распознаваться как Dock.
Selection fixtures имеют совпадающий Dock header и отдельный запрещающий Confirm; Core tests
проверяют запрет ввода и отказ postcondition при таком сочетании.

`GameUiActionRunnerTests` проверяет один tap, новый postcondition, stale/wrong target, отсутствие и
неоднозначность source, unsafe bounds, отказ mapping/ADB/capture, replay, изменение размера и отмену.
`GameInputViewportTests` защищает rotation, physical/logical frame, app bounds, display, obscured
окно, усечение и неизвестный формат. `GameUiAgentTests` использует production capture, matcher,
input adapter и общий lease через машинный envelope.

Native CTest `native_game_match` проверяет C ABI на сдвинутых контролируемых изображениях разных
размеров, конкурирующих кандидатах, низкой текстуре и ошибках ROI/budget. Ownership test считает live
frames/RGB bytes при повторных поисках на 4K. Managed negative probe отвергает DLL текущего ABI без
обязательного match export до создания frame.

## Реальные исходники и временные материалы

Полные screenshots и каталоги пользователя для тестов не требуются у другого разработчика.
В репозитории находятся только отобранные crops и обезличенные fixtures. Для локальной проверки
реальных изображений используй native boundary/production recognizer, сохраняя в системном temp
только SHA256, размеры, статусы и ограниченные метрики. Разовые скрипты, исходные captures, contact
sheets и отчёты приёмки также остаются в temp, без импорта внешнего каталога в Git.

Отдельно отмечай: synthetic fixtures; уменьшенные исходники; реальные native-resolution images;
live capture. Уменьшенный 4K screenshot не доказывает работу нативного 1080p UI. Ограниченные замеры
времени/памяти дополняют ownership tests, но не являются универсальным performance обещанием.

## Безопасная живая проверка

Через Agent CLI выполни read-only inventory, game observation, `game-ui-observe` и
`game-input-observe` для точной identity. При недоступном transport можно явно подготовить уже
запущенное окружение; причина должна быть доказана наблюдением. Не перезапускай здоровую игру
и не меняй разрешение ради проверки.

`game-dock-roundtrip` допустим на уже открытом главном экране. Он сохраняет четыре frame provenance,
две точки, match evidence и отдельные результаты открытия/возврата. Успех требует обоих переходов.
При первом отказе сохрани bounded envelope и останови проверку. Отсутствие доказанного postcondition
не является доказанным отсутствием эффекта. Покупки, сбор наград, изменение кораблей/настроек и
прочие игровые действия этой проверкой не разрешаются.
