# Классификация постоянного знания

## Decision tree

Используй первое подходящее правило сверху вниз.

| Вопрос | Владелец |
| --- | --- |
| Значение уже может быть однозначно прочитано из кода/schema/manifest? | Machine-readable source; prose только объясняет роль и ссылается |
| Правило нужно почти в любой инженерной задаче репозитория? | `AGENTS.md` |
| Нарушение правила типично приводит к неправильному коду именно в этой subsystem? | `.codex/context/<owner>.md` |
| Информация объясняет архитектуру, rationale, usage, troubleshooting или evidence? | `docs/**` |
| Информация описывает повторяемую процедуру агента? | `.agents/skills/**` |
| Это только история конкретного PR/прогона без долговременной ценности? | PR/issue/artifact, не durable context |

## Примеры классификации

### Точный номер версии

Плохо: продублировать `.NET SDK 10.x` в `AGENTS.md`, context и docs.

Хорошо: `global.json` владеет числом; docs объясняет, что SDK pin читается оттуда; context обычно не
хранит само число.

### ADB target identity

Утверждение «device commands обязаны адресовать exact endpoint выбранного MuMu instance и не выбирать
первое устройство» меняет решение агента при реализации и защищает чужие targets.

Это agent-critical contract → `.codex/context/android-game-lifecycle.md`.

Почему endpoint именно так устроен, наблюдавшиеся особенности MuMu/ADB, примеры ответов и acceptance
evidence → `docs/architecture/android-game-lifecycle.md`.

### Результат real acceptance

`MuMu 6.x + Android 15 на машине X прошли start/restart/stop` не является вечным agent contract.

Место: PR body, ignored artifact или `docs/` только если это действительно долговременный reference.
В `.codex/context/` переносится лишь устойчивый invariant, доказанный этим наблюдением.

### Git publication flow

Последовательность `status → stage → commit → push → verify remote → PR` — повторяемая процедура.

Место: `azurpilot-git-workflow`, а не `AGENTS.md` и не `.codex/context/`.

### Failure code

Точное строковое значение и числовой exit mapping принадлежат production-коду.

Context может закрепить семантический invariant: неизвестное состояние не маскируется `InternalError`,
ожидаемый failure остаётся типизированным и детали bounded. Полную таблицу значений туда не копируй.

## Проверка необходимости context

Перед добавлением абзаца в `.codex/context/` закончи фразу:

> «Если coding-agent не увидит это утверждение при изменении этой subsystem, он с заметной вероятностью…»

Если невозможно назвать конкретную неправильную реализацию или нарушение контракта, перенеси текст в
`docs/` либо не добавляй вовсе.
