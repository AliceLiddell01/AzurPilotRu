# Operator TUI — agent-critical контракт

Подробное устройство и клавиатура описаны в `../../docs/architecture/operator-tui.md`.

- `AzurPilot.App.OperatorUi` владеет Terminal.Gui renderer, навигацией и presentation state;
  предметные решения, адресация и postconditions остаются в `AzurPilot.App.OperatorServices` и
  production capabilities ниже.
- Один `AzurPilotHost`/DI container живёт всю интерактивную сессию. Первый экран использует его
  startup diagnostic snapshot; отдельный startup pipeline не создаётся.
- Instance выбирается явно из фактического inventory. UI передаёт только `MuMuInstanceId`; resolver
  связывает identity и Android endpoint. Запрещены default-first и пользовательский endpoint.
- Read-only observation не подготавливает ADB и не запускает ресурсы. Каждая MuMu/game lifecycle
  mutation и Android transport preparation требует отдельного подтверждения и проходит через текущую
  orchestration, gates и postcondition.
- Screenshot action использует production capture и scanner для `FullFrame` и `Center`; native frame
  остаётся внутри одного disposable capture и не попадает в UI state.
- Один UI event loop изменяет presentation state. История и log provider bounded; background logging
  не пишет в console. Отмена прекращает ожидание/команду, но не запускает compensating MuMu/game stop.
- Нет TTY — это одноразовый diagnostic path. Не добавляй пустые страницы и UI для ещё не существующих
  игровых задач или настроек.
