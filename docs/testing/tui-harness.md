# Постоянный визуальный TUI Harness

`tools/AzurPilot.TuiHarness` запускает настоящий `AzurPilot.App.exe` в ConPTY и показывает его
VT-поток через xterm.js в Chromium. Это инструмент разработки на C#/.NET, отдельный от продуктовых
границ приложения. JavaScript отвечает за терминальный рендерер; управление ConPTY и временем жизни
процессов остаётся в C#. Node.js, HTTP-сервер, CDN и пользовательский профиль браузера не нужны.

## Подготовка

Нужны Windows x64, SDK из `global.json`, PowerShell 7 и `tar.exe`. Подготовка native runtime и сборка
приложения описаны в [getting-started](../getting-started.md).

```powershell
pwsh -NoProfile -File tools/AzurPilot.TuiHarness/prepare.ps1
dotnet restore AzurPilot.slnx --locked-mode
dotnet build AzurPilot.slnx -c Release --no-restore -warnaserror
$harness = './tools/AzurPilot.TuiHarness/bin/Release/net10.0-windows/win-x64/AzurPilot.TuiHarness.exe'
$app = './src/AzurPilot.App/bin/Release/net10.0-windows/win-x64/AzurPilot.App.exe'
& $harness run --exe $app --scenario tools/AzurPilot.TuiHarness/scenarios/startup.json
```

Версии Playwright и ConPTY принадлежат `Directory.Packages.props`, граф NuGet — lock-файлам.
Версии, URL и SHA-512 xterm.js и Unicode addon находятся в `renderer.lock.json`. `prepare.ps1`
проверяет хеш каждого архива, включая кэш, и распаковывает пакеты в `artifacts/tui-harness/vendor`.
Загрузка сначала записывается во временный файл; повреждённый архив не используется повторно.
`-AssetsOnly` подготавливает только эти файлы для CI; `-SkipBrowser` пропускает установку Chromium.
Его revision закреплён Playwright; каталог по умолчанию — `artifacts/tui-harness/browsers`, явный
`PLAYWRIGHT_BROWSERS_PATH` поддерживается. Полученные бинарники и профили сессий не коммитятся.
ConPTY предоставляется пакетом Microsoft.Windows.Console.ConPTY с собственными `conpty.dll` и
OpenConsole; версия записывается в свидетельства сессии.

## Управление сессией

```powershell
$session = (& $harness start --exe $app --mode diagnostic | ConvertFrom-Json).session
& $harness text --session $session --value 'Черновик Кириллица е́ 界'
& $harness key --session $session --value F2
& $harness capture --session $session --contains 'Настройки' --name palette
& $harness key --session $session --value Escape
& $harness resize --session $session --columns 80 --rows 25
& $harness capture --session $session --name resized
& $harness close --session $session
```

Каждая сессия имеет отдельного владельца, канал Named Pipe и каталог. `--session` для `start`
позволяет заранее выбрать новый ID и сохранить адрес владельца до получения stdout; повторное
использование каталога с `start.json` отклоняется. ID проверяется по грамматике `HarnessRequest`.
Неизвестная, закрытая или истёкшая сессия возвращает `BLOCKED`.

CLI печатает один JSON в stdout через обычные pipes. Коды выхода: `PASS=0`, `FAIL=1`, `BLOCKED=2`,
`NOT_TESTED=3`. `key` принимает имена Playwright: `Enter`, `Escape`, `ArrowUp`, `Tab`, `Shift+Tab`,
`F2`, `Control+c`, `Control+End`. `text` передаётся через вставку xterm с текущим bracketed-paste
mode и далее в ConPTY. Методы интерфейса приложения напрямую не вызываются.

Мышь адресуется в ячейках: `mouse --value click|move|wheel-up|wheel-down --x COLUMN --y ROW`.
Отсутствие передачи событий мыши или ошибка доставки дают `BLOCKED`, неверные координаты — `FAIL`.
Доставленный клик без наблюдаемого эффекта имеет `NOT_TESTED` и отдельную машинную причину
`click_effect_not_observed`. Это отсутствие доказательства поддержки навигации, а не доказанная
неисправность доставки. Изменение ячеек, курсора или выделения само по себе ещё не доказывает нужный
эффект: проверяющий сценарий отдельно подтверждает открытие настроек. Он принимает и будущую
работающую навигацию. Движение без кнопки требует режима `any`. Прокрутка колёсиком проверяется
самостоятельно и остаётся обязательной.

## Дедлайны и владение процессами

Точные бюджеты принадлежат `HarnessTimeouts` и `HarnessRequest`; CLI `help` отдаёт внешние лимиты
для `verify.ps1`. Подключение начального `status` получает бюджет создания сессии, остальные
подключения — `TimeoutMs`. После подключения отсчитывается отдельный срок ответа: `TimeoutMs`
с ограниченным запасом IPC. Увеличенный бюджет `start` не расширяет ожидание остальных команд.

До первого ответа `status` владелец наблюдает запускающий CLI и дедлайн готовности. Обрыв клиента
или истечение срока завершают только принадлежащий Job Object. Ошибка `StartAsync` также завершает
сохранённый дескриптор владельца с ограниченным ожиданием. `verify.ps1` заранее записывает ID
начатых сессий, в том числе при потере stdout. Проверка `abandoned-startup-cleanup` запускает настоящий
EXE, обрывает клиента до подтверждения готовности и проверяет выход владельца и приложения при
сохранении постороннего контрольного процесса. Она требует Chromium и выполняется в runtime-прогоне;
unit suite не требует установки браузера. Поздняя готовность до/после дедлайна отдельно проверяется
управляемыми часами без длительного ожидания в CI.

Сессия ограничена общим сроком и периодом бездействия; точные пределы размеров, ввода и захватов
проверяет протокол. Хвост VT ограничен, общий объём и усечение записываются отдельно. Переполнение
очереди не останавливает чтение канала, но блокирует дальнейшее доказательство кадра.

## Изоляция и live

По умолчанию safe-сессия запрещает внешние изменения состояния и отдельное окно журнала через общий
operator service до межпроцессной аренды. Production App получает `--isolated-ui <absolute-root>`
с существующим отдельным YAML. Нет файла — нет перехода к пользовательскому пути или встроенным
настройкам. Вход процесса, host и сохранение используют `WindowsConfigurationIsolation`:
Windows разрешает окончательные пути, включая junction/symlink и эквивалентные представления;
совпадение с пользовательским runtime-каталогом и ссылки на пользовательский файл отклоняются.
Файловая идентичность дополнительно проверяет hard links. Пользовательские пути вычисляет только
`AzurPilotConfigurationPath`. Без TTY изолированный запуск отклоняется.

Проверка пути выполняется перед загрузкой и снова перед сохранением. Это защита от ошибочного
выбора или переадресации пути, а не файловая песочница против конкурентной подмены ссылок другим
процессом между проверкой и записью. Отдельные безопасные абсолютные каталоги вне `artifacts`
разрешены. Приложение не создаёт резервный путь в пользовательском каталоге при отказе изоляции.

В default safe дочерний процесс назначается приостановленным в Job Object с `ActiveProcessLimit=1`:
запуск внешних MuMu/ADB блокируется. Недоступное наблюдение на экране остаётся неизвестным.
`--providers read-only` разрешает процессы настоящего inventory, сохраняя запрет изменений состояния.
Это позволяет проверить реальное подтверждение и отказ даже после выбора «Подтвердить».

```powershell
& $harness run --exe $app --providers read-only --scenario tools/AzurPilot.TuiHarness/scenarios/safe-confirmation.json
pwsh -NoProfile -File tools/AzurPilot.TuiHarness/verify.ps1 -App $app -ReadOnlyProviders
```

`safe-confirmation` требует установленного MuMu и однозначного inventory; отсутствие этих фактов
блокирует сценарий. Проверка настроек сравнивает хеши пользовательских YAML/JSON и резервных копий
до/после, требует сохранение и резервную копию только в отдельном каталоге. Отображённый путь
сравнивается точно: переносы терминала допустимы, значимые пробелы сохраняются. UI переносит строку
пути посимвольно, не удаляя и не объединяя пробелы.

Единственный live-сценарий Harness — подготовка окружения и восстановление **исходно Stopped**
точного экземпляра:

```powershell
# Только для экземпляра, явно разрешённого пользователем в текущей задаче.
& $harness live-environment --exe $app --instance mumu:<index> --authorize prepare-and-restore
```

Harness сохраняет предварительное наблюдение через read-only Agent CLI. App независимо получает
наблюдение через свой production provider; сам `--live-environment-target` полномочий не даёт.
Прямой запуск App подчиняется той же узкой политике. `Running`, `Unknown`, ошибки и отсутствующий
экземпляр не дают разрешения. Перед подготовкой состояние проверяется снова **под арендой**:
переход в `Running`/`Unknown` после начального наблюдения запрещает изменения. Подготовка допускается
один раз; только после её начала разрешён восстановительный `Stop` того же экземпляра. После
успешной остановки разрешение погашается. `Start`, `Restart`, игровые операции, прямой Android
prepare и другой экземпляр запрещены. Обычный запуск и namespace `agent` сохраняют свои контракты.

Флаг задаёт намерение оператора; это не секрет и не граница защиты от кода, уже исполняемого с
правами пользователя. Подтверждение действия проходит через TUI. Сценарий сохраняет предварительное
наблюдение, PNG подтверждений, готовность, повторное наблюдение и `restored`. Postflight выполняется
также при ошибке UI-остановки. Восстановление не объявляется при недоказанном состоянии.

Аренда сериализует операции AzurPilot, но не внешние средства управления MuMu. Между наблюдением
и действием нет абсолютной атомарности; live-проверка требует исключённого внешнего управления.
В live внешний MuMu не включается в kill-on-close Job приложения: авария браузера/владельца не
должна принудительно останавливать его. Owner и App используют отдельные Jobs с управляемым
breakaway; Chromium, драйвер и conhost остаются под владельцем. После аварии target может остаться
запущенным, восстановление считается неподтверждённым. Новый запуск с `Running` не получает право
его остановить; восстановление требует отдельно разрешённой операции с новым наблюдением.

Эта политика ограничивает Harness. Прямая приёмка через production Agent CLI по явному поручению
остаётся отдельной границей с точным target, наблюдениями и восстановлением; см.
[Agent CLI](../reference/agent-cli.md) и [Android acceptance](android-acceptance.md).

## Захват и проверки

Один кадр содержит `*.png`, `*.cells.json`, `*.vt.txt`, `*.evidence.json` с общим префиксом.
PNG снимается Playwright с настоящего xterm. Ячейки включают координаты, ширину Unicode, fg/bg,
атрибуты, курсор, буфер, прокрутку, выделение и режим мыши. Свидетельства содержат SHA-256 EXE **и
DLL**, PID, версию, аргументы, профиль, версии рендерера, последовательности вывода и хеш PNG.
Изменение бинарника требует новой сессии.

Git-корень ищется от расположения Harness и проверяется по корню checkout и отслеживаемым файлам
solution/App/Harness. Рабочий каталог вызывающей оболочки и её переменные `GIT_*` не выбирают
репозиторий. Вне подтверждённого checkout Git provenance имеет `NOT_TESTED`, commit отсутствует.
Даже подтверждённый текущий Git HEAD описывает исходники и **не доказывает**, из какого HEAD собран
EXE. Идентичность бинарников остаётся отдельным свидетельством.

Для захвата дожидаются пустых очередей, обратных вызовов `terminal.write`, тихого интервала и двух
кадров анимации. Ячейки до/после PNG и номер последовательности ConPTY должны совпасть. Один
`onWriteParsed` этого не доказывает. Истечение срока даёт `BLOCKED`; стабильный кадр без требуемого
текста даёт `FAIL`. Это ограниченное ожидание согласованного кадра, не блокировка цикла UI.

JSON-сценарии проверяют `contains`, `absent`, размеры и историю. Строка операции ищется по
`Операция:`, история — между заголовком «Сообщения и результаты» и разделителем ввода; номера строк
не закреплены. Координата колеса выводится из границ истории предыдущего снимка. Изменение
часов/статуса не считается прокруткой. Wheel требует направленного
перекрытия строк; полный PageUp проверяется также по порядку уникальных введённых маркеров. Xterm
`viewportY` должен оставаться прежним. `Control+End` обязан вернуть прежний конец истории.
Сценарии: `startup.json` — палитра/Esc, Unicode и размеры с восстановлением; `history.json` —
wheel/PageUp/конец; `settings.json` — точное сообщение сохранения; `safe-confirmation.json` —
отмена по умолчанию и отказ изменения состояния. Снимки не перезаписывают предыдущие состояния.

## Цвет и граница Windows Terminal

Профиль Campbell использует ANSI-палитру Microsoft, непрозрачный фон и Consolas 12pt (16 CSS px при
DPR 1). Светлый профиль меняет fg/bg и курсор при той же палитре. `--profile` принимает отдельный
JSON; настройки Windows Terminal пользователя не читаются. До App короткий процесс в той же
ConPTY задаёт настоящую палитру и цвета консоли через Win32. Его PID записывается отдельно.
Цвета по умолчанию должны присутствовать в ANSI 16, иначе `BLOCKED`. App включает VT перед выбором
драйвера и восстанавливает режим при выходе. При отсутствии OSC-ответа фон читается из Win32;
поздний ответ обновляет палитру в цикле UI, сохраняя черновик.

Владелец задаёт `TERM`/`COLORTERM` и удаляет у себя `NO_COLOR`, `FORCE_COLOR`, чужую `WT_SESSION`.
Пользовательское окружение не меняется. `FontFace local(...)` обязан загрузить выбранный шрифт;
резервный шрифт для отдельных глифов, shaping и совпадение ширин с Windows Terminal не доказаны.
Метрики измеряются по `.xterm-screen`, размер Chromium вычисляется из ячеек. Resize применяется к
ConPTY и xterm. `minimumContrastRatio=1`, bold не меняет ANSI index, профиль Chromium — sRGB.

```powershell
& $harness fidelity
& $harness fidelity --profile tools/AzurPilot.TuiHarness/profiles/campbell-light.json
```

Калибровка — отдельный помеченный VT-образец, не продуктовый UI. Проверяются default/ANSI 16/256/RGB,
bold/dim/inverse, скрытый курсор и Unicode widths. Кадры App отдельно измеряют акценты. SGR показывает
наблюдённые коды, ячейки — распознанные режимы, PNG — RGB однотонного фона и центрального пикселя
текста. Полностью сглаженный глиф даёт `NOT_TESTED` для сравнения текста; расхождение фона — `FAIL`.

Это не пиксельная идентичность Windows Terminal: Chromium DOM и DirectWrite могут различать
сглаживание, толщину шрифта, выбор глифов, округление ячеек и курсор. Acrylic/Mica, прозрачность,
лигатуры и произвольный DPI не эмулируются. Выделение браузера не равно выделению в приложении.

### Необязательная физическая проверка Windows Terminal

Существующий внешний маршрут использует [Microsoft WinApp CLI](https://learn.microsoft.com/en-us/windows/apps/dev-tools/winapp-cli/)
(public preview, не зависимость продукта). Сверьте установленную версию и `--help`. Запустите тот
же EXE/DLL в отдельном окне Windows Terminal с `--mode diagnostic --isolated-ui <absolute-root>`
и отдельным существующим YAML. Сохраните хеши, версии Terminal/WinApp, HWND, заголовок, DPI, шрифт,
цветовую схему, строки/столбцы и размер PNG в локальных артефактах. Запустите App в одном окне, а
инспекцию — во втором, пока TUI работает. В первом окне PowerShell задайте уникальный заголовок:

```powershell
$Host.UI.RawUI.WindowTitle = 'AzurPilot physical smoke'
& $app --mode diagnostic --isolated-ui $isolatedRoot
```

В отдельном окне PowerShell из корня checkout найдите это окно и сохраните инспекцию и снимок:

```powershell
$winapp = (Get-Command winapp.exe).Source
New-Item -ItemType Directory -Force -Path 'artifacts/tui-harness/physical' | Out-Null
$windows = & $winapp ui list-windows -a Terminal --json | ConvertFrom-Json
$target = @($windows | Where-Object title -eq 'AzurPilot physical smoke')
if ($target.Count -ne 1) { throw 'Не найдено единственное целевое окно Windows Terminal.' }
$hwnd = [int]$target[0].hwnd
& $winapp ui inspect -w $hwnd --json
& $winapp ui screenshot -w $hwnd --output 'artifacts/tui-harness/physical/startup.png' --json
```

Выберите окно по уникальному заголовку. UIA показывает элементы Terminal, но не ячейки TUI; PNG
оценивается отдельно. WGC может снять перекрытое окно; это не доказывает захват свёрнутого окна или
работу на заблокированном рабочем столе. `--capture-screen` снимает видимую область и выводит окно
вперёд. Синтетический ввод требует доступного интерактивного рабочего стола и не считается
физическим вводом. Наличие `wt.exe`/`winapp.exe` не доказывает успех. Отдельно укажите проверку
Unicode, wheel и resize; сравнивайте одинаковые состояния с xterm без требования пиксельного
равенства. Без GUI/HWND/WGC физический этап — `BLOCKED`, браузерный снимок его не заменяет.
Подробности: [WinApp CLI UI automation](https://github.com/microsoft/winappCli/blob/main/docs/ui-automation.md).

## Визуальный осмотр агентом

Процедура принадлежит [azurpilot-tui-harness](../../.agents/skills/azurpilot-tui-harness/SKILL.md).
После визуальной правки нужен новый снимок и сравнение с исходным состоянием. PNG необходимо
открыть инструментом просмотра; отчёт называет видимый признак, статус и путь. Наличие файла или
текст ячеек не означает осмотр. Отдельный маршрут DSH не входит в приёмку Harness: без его запуска
он имеет `NOT_TESTED` и не блокирует уже выполненный осмотр в Codex.

## Первичные технические контракты

- [Microsoft: создание и resize ConPTY](https://learn.microsoft.com/en-us/windows/console/creating-a-pseudoconsole-session).
- [Windows Terminal appearance](https://learn.microsoft.com/en-us/windows/terminal/customize-settings/profile-appearance)
  и [color schemes](https://learn.microsoft.com/en-us/windows/terminal/customize-settings/color-schemes).
- [xterm.js API](https://xtermjs.org/docs/api/terminal/classes/terminal/),
  [ITheme](https://xtermjs.org/docs/api/terminal/interfaces/itheme/),
  [IBufferCell](https://xtermjs.org/docs/api/terminal/interfaces/ibuffercell/).
- [Playwright .NET screenshots](https://playwright.dev/dotnet/docs/screenshots).
