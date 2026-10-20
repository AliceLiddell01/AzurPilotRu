# Приёмка транспортов снимков

Архитектура и контракты — [транспорты снимков](../architecture/screenshot-transports.md). Общая процедура
live-приёмки через Agent CLI — skill `azurpilot-live-testing`.

## Deterministic проверки (без MuMu)

- CTest `native_frame_sources`: все сырые форматы со stride и отражением, RGB565, BMZ1 (оба порядка заголовка,
  повреждённый/усечённый LZ4), JPEG, поворот кадра, NULL/invalid аргументы `nemu_ipc` без загрузки DLL эмулятора.
- CTest `native_video_decoder`: H.264 кодируется системным MFT и декодируется production-декодером; номера
  кадров, `NOT_READY`, геометрия дисплея. Без Media Foundation тест пропускается кодом 77.
- `tests/AzurPilot.Tests/Capture/`: статистика и выбор, интервалы и профили, строгая настройка `screenshot`,
  разбор аргументов Agent CLI, envelope worker-а, журнал helper-ов, shell-токены, наличие и SHA-256 закреплённых
  зависимостей в output, маршрутизатор `auto` (bootstrap, видимый откат, явный метод без подмены), сравнение
  кадров с проверкой порядка каналов.

CI не зависит от MuMu: `nemu_ipc`, helper-ы и scrcpy в нём проверяются только на уровне ABI и разбора протокола.

## Live-приёмка

Предусловия: exact identity, разрешение пользователя на подготовку transport, получение главного экрана,
измерения (временные helper-ы с очисткой), Dock roundtrip и финальный `game-stop`. Порядок:

1. Read-only preflight (`instances`, `mumu-observe`, `android-observe`, `game-observe`, `capture-methods`).
2. `android-prepare`, главный экран (`game-ui-ready-ensure` либо ручной возврат пользователя) и независимое
   `game-ui-ready-observe → Ready`.
3. Baseline 5 × `game-dock-roundtrip` до калибровки (`auto` → ADB).
4. `capture-benchmark` со всеми методами; отдельные `capture-probe` для диагностики отказов.
5. Повтор production-команд на `auto` и 5 × `game-dock-roundtrip` с выбранным методом.
6. `capture-benchmark --debug-overhead true --calibrate false` для ON/OFF Debug Mode.
7. `capture-intervals --scene normal`; затем остановка и запрос пользователю. Combat — только после его ручного
   входа в бой и явного подтверждения, отдельным вызовом с кодом.
8. Проверка устройства: нет процессов `app_process`/`ascreencap`, пустые `forward --list` и `reverse --list`,
   пустой журнал helper-ов.
9. `game-stop`, независимые `game-observe → Stopped`, `mumu-observe → Running`.

Кадры, UID и чаты остаются в `.codex/local/reports/` и системном TEMP и не попадают в PR.

## Зафиксированное evidence

MuMu 6.8.5.0, Android 15 (SDK 35, x86_64), 3840×2160 в landscape (физический буфер 2160×3840, `mRotation=1`).
Тестовое разрешение, отличное от 4K, не проверялось: смена разрешения эмулятора не входила в разрешённые
действия.

`capture-benchmark` (short, 25 снимков, общий срок 300 с; Main доказан до и после):

| Транспорт | Итог | Успешно | p50, мс | p95, мс | Новых кадров/с | Корректность |
| --- | --- | --- | --- | --- | --- | --- |
| `nemu_ipc` | PASS | 25/25 | 25,0 | 25,9 | 38,1 | доказана |
| `uiautomator2` | PASS | 25/25 | 183,8 | 193,1 | 5,4 | доказана |
| `DroidCast_raw` | PASS | 25/25 | 581,6 | 620,2 | 1,7 | доказана |
| `ADB_nc` | PARTIAL (срок) | 19/19 | 757,4 | 856,5 | 1,3 | доказана |
| `DroidCast` | PARTIAL (срок) | 12/12 | 2430,6 | 2513,7 | 0,4 | доказана |
| `ADB` | INSUFFICIENT (срок) | 4/4 | 2316 | 2326,9 | 0,4 | доказана |
| `aScreenCap`, `aScreenCap_nc` | UNSUPPORTED | — | — | — | — | `binary_cannot_link` |
| `scrcpy` | UNSUPPORTED | — | — | — | — | `encoder_rejected_resolution` 3840×2160 |

Выбор: `nemu_ipc` (`lowest_p95_end_to_end`), калибровка `auto` записана. На этом MuMu у scrcpy есть только
программный `OMX.google.h264.encoder`, который не кодирует 4K; понижение разрешения запрещено, чтобы не
сравнивать разные разрешения. Бинарник aScreenCap для SDK ≤ 28 не линкуется на Android 15
(`SurfaceComposerClient::getBuiltInDisplay`).

После калибровки `game-ui-observe`, `game-ui-ready-observe`, `screenshot-scan` и Dock используют `nemu_ipc`;
`game-ui-ready-observe` — 3,9 с против 11,1 с на ADB. `game-dock-roundtrip`: baseline ADB 5/5, 14,4–14,8 с;
`nemu_ipc` 5/5, 2,5–4,0 с (медиана 2,7 с). Debug Mode на `nemu_ipc`: p50 26,3 → 27,8 мс, p95 в пределах шума.

Интервалы `nemu_ipc`: normal устойчив с 33,3 мс (≈30 FPS), 25 мс и меньше — `Unstable`/`Unachievable`
(медиана вызова ≈24–26 мс). Combat (`UNVERIFIED_SCENE`, `USER_CONFIRMED`) устойчив от 50 до 1000 мс; 25 мс — на границе (Stable в одном
прогоне, Unstable с 25% пропусков в другом).
Профили: fast 33,3/75, balanced 75/300, low_load 150/500, current 150/1000 мс (normal/combat).

### Применение профиля

Deterministic: `AzurPilotConfigurationScreenshotIntervalsTests` (точечная правка для LF/CRLF, комментариев,
пустой и `~`-секции, отступа 4; BOM и backup; отказ при изменённом SHA-256 и при ошибке замены без временных
файлов; legacy JSON) и `CaptureProfileApplyTests` (профили из устойчивых точек, `current` как восстановление,
каждая причина отказа привязки, просроченная и старая сессии, незавершённые фазы, подделанные результаты,
одноразовый код, блокировка `apply.json`, независимая сверка записанного файла).

Live (MuMu выше, `nemu_ipc`, калибровка прежняя):

- старая сессия без привязки (`capture-intervals-live-2`) → `REJECTED session_binding_missing`, файл не изменён;
- новая сессия normal → `PASS`, устойчиво с 33,3 мс; preview `balanced` для пользовательского файла:
  `normalIntervalMs` 150 → 75, `combatIntervalMs` 1000 без изменений (`combat: not_measured`), SHA-256 файла не
  изменился;
- на копии конфигурации в `.codex/local`: неверный код → `confirmation_mismatch`, после него верный →
  `confirmation_used`; правка файла после preview → `configuration_changed_since_preview`; корректный apply →
  `APPLIED`, `reread_values_match`, добавлены только строки секции, комментарии сохранены, backup байт в байт
  совпадает с исходником; повтор кода → `confirmation_used`.

Влияние интервала на Dock (5 + 5 прогонов поочерёдно, `--config`): 150 мс — медиана 3,09 с (2,77–3,19);
75 мс — 2,86 с (2,66–3,01). Ожидание интервала в обоих случаях 0 мс: одно наблюдение постусловия (захват ≈30 мс
и сопоставление шаблонов ≈200 мс) длится ≈230 мс, дольше обоих интервалов, поэтому разница в пределах шума.
