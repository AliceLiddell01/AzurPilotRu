# Игровой UI — agent-critical контракт

- Геометрия игры отделена от screenshot diagnostics. Кадр остаётся native-owned; масштабировать
  весь RGB8 к логическому reference size запрещено.
- Ассетные ROI, safe tap, пороги, варианты и provenance принадлежат `assets/game/catalog.json`.
- Заголовок страницы сам по себе не доказывает режим. Исключающие признаки action должны быть
  доказанно Absent до ввода и после него; неизвестный guard блокирует действие.
- Tap разрешает Core только после unique match, свежести и exact Android viewport proof; `GameTap`
  одноразовый. Общая operator lease покрывает весь сценарий, без повторного захвата во вложенных шагах.
- После tap требуется отдельный новый frame с независимым postcondition. Unknown/отмена не означают
  NoEffect; слепого повторного tap и автоматического recovery нет.
- Архитектура и rationale: [game-ui](../../docs/architecture/game-ui.md); границы evidence:
  [verification](../../docs/testing/game-ui.md). Login, OCR и игровой прогресс сюда не входят.
