# Operator TUI — agent-critical контракт

Подробное устройство, режимы и клавиатура описаны в `../../docs/architecture/operator-tui.md`.
Визуальные изменения проверяй через [azurpilot-tui-harness](../../.agents/skills/azurpilot-tui-harness/SKILL.md).

- `AzurPilot.App.OperatorUi` владеет Terminal.Gui renderer, навигацией, режимами доступа и presentation
  state; предметные решения, адресация и postconditions остаются в `AzurPilot.App.OperatorServices` и
  production capabilities ниже.
- Единственный владелец каталога действий — `OperatorUiCommand.All`: каждый пункт несёт режим
  доступности, категорию и human-readable название. Второй список командных строк не заводится, а
  правило доступа применяется и к палитре, и к прямому вводу.
- Режим (`normal`/`manual`/`diagnostic`) — presentation-правило, а не оформление. Явный `--mode`
  приоритетнее `ui.mode`; неверное значение — отказ запуска, а не подстановка обычного режима.
- Встроенной области журнала нет ни в одном режиме: геометрия не резервирует высоту под лог. Runtime
  logging принадлежит application host; отдельный viewer — read-only consumer в своём процессе.
- Один `AzurPilotHost`/DI container живёт всю интерактивную сессию. Первый экран использует его
  startup diagnostic snapshot; отдельный startup pipeline не создаётся.
- Instance выбирается явно либо разрешается production-резолвером из `mumu.instance` без mutation.
  UI передаёт только `MuMuInstanceId`; resolver связывает identity и Android endpoint. Запрещены
  default-first и пользовательский endpoint. Неоднозначный или недоказанный target не мутирует.
- Read-only observation не подготавливает ADB и не запускает ресурсы. Пользовательский запрос mutation
  требует default-cancel подтверждения; когда выбранный target отличается от сохранённого, оператор
  может подтвердить mutation с сохранением target или без него. Зависимые этапы подготовки MuMu/Android
  не требуют новых подтверждений. Все этапы проходят через текущие orchestration, gates и postconditions.
- Screenshot action использует production capture и scanner для `FullFrame` и `Center`; native frame
  остаётся внутри одного disposable capture и не попадает в UI state.
- Один UI event loop изменяет presentation state. История и log provider bounded; background logging
  не пишет в console. Отмена прекращает ожидание/команду, но не запускает compensating MuMu/game stop.
- Нет TTY — это одноразовый diagnostic path без UI и без запуска отдельной лог-консоли. Не добавляй
  пустые страницы и UI для ещё не существующих игровых задач или настроек.
