# Критерии по затронутым boundaries

| Область | Что проследить в реализации и её потребителях |
| --- | --- |
| Managed архитектура | App → Windows → Core; composition и orchestration у текущих владельцев; публичные API/CLI/JSON/config и обратная совместимость |
| Native/interop | C ABI и P/Invoke, размеры/структуры/экспорты, marshalling, ownership native memory, lifetime/dispose, освобождение при исключении, отсутствие C++ exceptions через ABI |
| Процессы и ресурсы | Принадлежность дочерних процессов и handle, завершение/shutdown, утечки, Windows paths/quoting, отмена и bounded timeouts без потери partial effects |
| Concurrency | Process-local gates и interprocess lease exact identity через весь сценарий; отсутствие повторного захвата во вложенных шагах, раннего освобождения и опасных повторов |
| Agent CLI | Один JSON envelope в stdout после shutdown; логи в stderr; schema, exit/status/failure/effect/data и nullable facts согласованы; отказ не теряет requestedTarget/target и известный эффект |
| MuMu/ADB | Exact identity/endpoint и повторная проверка; никаких default-first, вычисленных портов, global ADB-server mutation и fallback на другой target; read-only без connect/launch; transport/shell/boot как разные факты |
| Игровой UI | Provenance ассетов, распознавание конкретного target, freshness, ROI/viewport/rotation/coordinates, unique match и guards до tap; новый независимый кадр postcondition; Unknown и отмена не означают NoEffect |
| Отказы и безопасность | Входные данные, shell/path injection, secrets, bounded diagnostics, ошибочный success, partial effects и отсутствие слепого rollback/recovery |
| Тесты | Проверяют наблюдаемый контракт и отрицательные сценарии; реальные native/managed boundaries там, где это заявлено; doubles не выдаются за real-device evidence |

Для каждого существенного требования найди реализацию и проверку либо назови пробел. Не требуй
универсальности за пределами поддерживаемого Windows x64/MuMu/Global EN. Дополнительная реализация
оправдана correctness, безопасностью, тестируемостью и завершением реальной границы, а не вкусом ревьюера.

Проверь необоснованный hardcode: машинные абсолютные пути, авторские каталоги, номера PR/этапов,
fixtures и assertions под текущий снимок, случайные counts/order. Стандарты, утверждённые constants
и предметные значения допустимы; для finding докажи, почему literal должен иметь другого владельца.

Ищи копирование production-логики в тест, заглушки и фиктивные assertions, дубли, мёртвые/legacy ветви,
неиспользуемые зависимости и placeholder-abstractions. Не требуй косметической переделки несвязанного кода.
Project-owned комментарии, диагностику и документацию сверяй с
[языковой политикой](../../../../.codex/context/language.md); API и общепринятые термины сохраняются.
