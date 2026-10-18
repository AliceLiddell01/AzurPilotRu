# Выбор поверхности Agent CLI

Это маршрутизация к действующим владельцам, не второй каталог CLI. Сначала прочитай production
`AgentCommands`/`AgentRequest` и вывод `agent discover` проверяемой сборки.

| Область | Требуемое свидетельство и владелец |
| --- | --- |
| Discovery/parser/envelope | Настоящий EXE: discovery без host/config/runtime и отказ неверного запроса; один JSON, schema/exit/effect. [Agent CLI](../../../../docs/reference/agent-cli.md) |
| Runtime diagnostics, settings, inventory/resolve | Текущий snapshot и exact identity без записи настроек, запуска процессов среды или connect. Ошибка config не маскируется пустыми defaults |
| MuMu lifecycle | Exact instance, начальное состояние, независимый Running/Stopped postflight; при restart доказательство нового lifecycle. [MuMu context](../../../../.codex/context/mumu-lifecycle.md) |
| Android readiness/lifecycle | Exact endpoint, transport/shell/boot отдельно; game package/process/foreground tri-state. [Android acceptance](../../../../docs/testing/android-acceptance.md) и [Android context](../../../../.codex/context/android-game-lifecycle.md) |
| Screenshot/native scanner | Настоящий capture и native scanner: dimensions/format, capture time, FullFrame/Center ROI bounds и RGB statistics. Это не распознавание UI; PNG export через Agent CLI отсутствует. [Screenshot acceptance](../../../../docs/testing/screenshot-acceptance.md) |
| Recognition/viewport/input | Известные ассеты и provenance, отдельное viewport/rotation/coordinates proof, свежесть и guards; результат действия подтверждается новым независимым кадром. [Game UI acceptance](../../../../docs/testing/game-ui.md) |

## Transport recovery

Текущий `android-prepare` подготавливает только transport: не запускает MuMu/игру и не выполняет input.
Один target-local connect допускается при исходном `Absent`; `Device` не требует connect,
`Offline`/`Unknown` отклоняются. Успех connect не равен readiness: нужны наблюдения Device, shell и boot.
После подготовки повтори read-only наблюдение. Не добавляй recovery в помощник и не расширяй
неуспешную preparation до `environment-prepare` ради запуска среды. Названия и точные поля заново
сверяй с [Agent CLI](../../../../docs/reference/agent-cli.md).

Для game UI readiness transport не отменяет свежие guards и viewport перед каждым tap. Ограниченная
приёмка разрешённого Dock roundtrip требует отдельных `open.changeProven` и `close.changeProven`;
общий `effect=changed` из-за ADB этого не доказывает. При первом отказе сохрани envelope и остановись.
Покупки, награды, изменения кораблей/настроек и неразрешённые переходы не входят в этот сценарий.
Он требует отдельной текущей авторизации ввода, даже если preparation уже разрешена.

Вызовы MuMuAcceptance/AndroidAcceptance напрямую проверяют свои production boundaries, но не
подменяют исполнение через Agent CLI. TUI ConPTY/WinApp и узкая подготовка исходно Stopped instance
остаются у [tui-harness](../../azurpilot-tui-harness/SKILL.md); backend-only проверка их не требует.
