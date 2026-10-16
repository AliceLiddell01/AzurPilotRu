# Постоянный визуальный TUI Harness

`tools/AzurPilot.TuiHarness` — developer tool на C#/.NET для настоящего production EXE.
Он не входит в product boundaries и не поставляется пользователю. Runtime не требует отдельной установки
Node.js, HTTP-сервера или CDN. Собственный JavaScript утилиты — только xterm.js renderer в Chromium;
Playwright .NET использует свой поставляемый driver. Владельцем production ConPTY остаётся C#.

## Подготовка и запуск

Нужны Windows x64, SDK из `global.json`, PowerShell 7 и `tar.exe`. Production App собирается по
[getting-started](../getting-started.md); native runtime должен соответствовать конфигурации сборки.

```powershell
pwsh -NoProfile -File tools/AzurPilot.TuiHarness/prepare.ps1
dotnet restore AzurPilot.slnx --locked-mode
dotnet build AzurPilot.slnx -c Release --no-restore -warnaserror
$harness = './tools/AzurPilot.TuiHarness/bin/Release/net10.0-windows/win-x64/AzurPilot.TuiHarness.exe'
$app = './src/AzurPilot.App/bin/Release/net10.0-windows/win-x64/AzurPilot.App.exe'
& $harness run --exe $app --scenario tools/AzurPilot.TuiHarness/scenarios/startup.json
```

Версия Playwright принадлежит `Directory.Packages.props`, NuGet graph — lock-файлам. Версии,
URL и SHA-512 xterm.js/Unicode addon принадлежат `renderer.lock.json`. Подготовка проверяет hash
каждого архива, включая кэшированный, и распаковывает его в `artifacts/tui-harness/vendor`.
`-AssetsOnly` используется в CI до managed build. Chromium устанавливается Playwright-скриптом в
`artifacts/tui-harness/browsers`; его revision закреплён выбранным Playwright. После подготовки
все runtime-ассеты локальны. `-SkipBrowser` пропускает установку уже подготовленного браузера.
Явный `PLAYWRIGHT_BROWSERS_PATH` поддерживается; браузеры и профили не входят в Git.
ConPTY предоставляется закреплённым стабильным пакетом Microsoft.Windows.Console.ConPTY:
собственный interop управляет его `conpty.dll` и OpenConsole. Версия backend включается в identity;
поведение не зависит от случайной версии системного conhost.

## Одна сессия, несколько CLI-вызовов

```powershell
$session = (& $harness start --exe $app --mode diagnostic | ConvertFrom-Json).session
& $harness text --session $session --value 'Черновик Кириллица е́ 界'
& $harness key --session $session --value F2
& $harness capture --session $session --name palette --contains Настройки
& $harness key --session $session --value Escape
& $harness resize --session $session --columns 80 --rows 25
& $harness capture --session $session --name narrow --contains Черновик
& $harness close --session $session
```

`start` возвращает случайный session ID, PID владельца и каталог. Отдельный скрытый процесс
владеет ConPTY, production child и Playwright до `close`, истечения lifetime или аварии child.
IPC — локальный Named Pipe с `CurrentUserOnly`; TCP listener отсутствует. Один владелец последовательно
обрабатывает операции одной сессии. Независимые сессии имеют разные процессы, каналы и каталоги.
Неизвестный/закрытый ID возвращает `BLOCKED`. При повторном использовании ID сверяется адресат.

Обычные команды печатают один JSON в pipes stdout, без VT. Exit codes: `PASS=0`, `FAIL=1`,
`BLOCKED=2`, `NOT_TESTED=3`. `key` использует имена Playwright (`Enter`, `Escape`, `ArrowUp`,
`Tab`, `Shift+Tab`, `F2`, `Control+c`, `Control+End`). `text` передаётся через xterm paste с
актуальным bracketed-paste mode, затем в ConPTY; прямого вызова методов production UI нет.
`mouse --value click|move|wheel-up|wheel-down --x COLUMN --y ROW` использует координаты ячеек.
Если приложение не включило mouse reporting, операция возвращает `BLOCKED`.
Для click без наблюдаемого изменения cells/cursor/selection также возвращается `BLOCKED`: текущая
production палитра обрабатывает wheel, но не реализует click navigation. Это ограничение App,
а не разрешение вызывать UI-методы напрямую. Движение без кнопки требует режима reporting `any`.
`PASS` ввода подтверждает доставку и завершение очереди, а эффект доказывают assertions.

Операции и IPC ограничены дедлайнами и бюджетами в `HarnessRequest`. Lifetime ограничен 15 минутами,
idle timeout — двумя минутами, capture budget — 64 кадрами на сессию. VT tail ограничен 64 KiB
символов UTF-16; число принятых байтов, отброшенных символов и усечение указаны отдельно. При переполнении очереди чтение pipe
продолжается, но дальнейшее доказательство кадра блокируется. Session outputs находятся в `artifacts/`.

## Safe UI и live

Safe UI используется по умолчанию. `--isolated-ui` production App выбирает отдельный YAML и
отключает viewer и все внешние mutation в общем operator service **до** межпроцессной аренды.
Default startup приложения и `agent` namespace сохраняют прежнюю семантику. Изолированный запуск
требует существующий YAML; отсутствие файла не подставляет пользовательский каталог сохранения.
Harness создаёт его через production configuration store до запуска EXE. Изолированный запуск
без TTY отклоняется, поэтому diagnostic fallback не может стать успешным UI evidence.

Дополнительно default safe child назначается приостановленным в Job Object с `ActiveProcessLimit=1`:
MuMu/ADB даже не могут быть запущены дочерними процессами. При таком режиме inventory недоступен,
и экран честно показывает неизвестное состояние. Для настоящего read-only inventory используется
`--providers read-only`: Job разрешает процессы provider, но тот же production mutation guard
остаётся закрытым. Этот режим позволяет открыть настоящее подтверждение и проверить отказ даже
при намеренном выборе «Подтвердить». Он не мокает MuMu и не разрешает Android connect/start.

```powershell
& $harness run --exe $app --providers read-only --scenario tools/AzurPilot.TuiHarness/scenarios/safe-confirmation.json
pwsh -NoProfile -File tools/AzurPilot.TuiHarness/verify.ps1 -App $app -ReadOnlyProviders
```

`safe-confirmation` требует установленного MuMu и однозначного production inventory. При отсутствии
этих фактов сценарий блокируется, а не рисует подтверждение. `verify.ps1` сравнивает hashes
пользовательских YAML/JSON/backups до и после настроек; сохранение и backup должны появиться только
в runtime-каталоге сессии. Настройки следующего запуска не включают viewer текущего safe host.

Единственный live-сценарий — подготовка окружения и возвращение **исходно Stopped** exact target:

```powershell
# Выполнять только для явно разрешённого пользователем instance.
& $harness live-environment --exe $app --instance mumu:<index> --authorize prepare-and-restore
```

Без обоих явных аргументов запуск отклоняется. Read-only Agent CLI собирает preflight; `Unknown`,
`Running`, ambiguous и ошибки не присваивают ownership. Само действие и подтверждение проходят
через production TUI. Product policy разрешает только environment preparation и MuMu stop для
указанного exact target; game mutations, другой target и прямой Android prepare остаются закрытыми.
Сохранение выбора относится к отдельному YAML. Сценарий сохраняет preflight, PNG подтверждений,
готовность, postflight и признак `restored`. Он не объявляет восстановление при ошибке.

В live-mode external MuMu не включается в kill-on-close Job приложения: он не должен принудительно
уничтожаться при аварии browser/owner. Owner и App находятся в отдельных Jobs с управляемым
breakaway; Chromium/driver/conhost остаются под владельцем. При аварийной потере владельца target
может остаться запущенным, а восстановление не подтверждено. Повторение начинается с exact-target
preflight. Координация production mutation не покрывает посторонние MuMu/ADB-клиенты; live smoke
проводится при исключённом внешнем управлении выбранным target. Произвольные live-сценарии отсутствуют.

## Capture и assertions

Кадр — `*.png`, `*.cells.json`, `*.vt.txt`, `*.evidence.json` с общим префиксом. PNG снимается
Playwright с реального xterm renderer. Cells включают координаты, Unicode width, fg/bg mode/value,
flags, cursor, alternate/normal buffer, viewport, selection и mouse mode. Evidence содержит
EXE **и DLL** SHA-256 (apphost сам по себе недостаточен), PID, version, аргументы, исходный commit/dirty
state, профиль, renderer versions, parsed/output sequence и hash PNG. Замена бинарника требует
новой сессии. Dirty build не объявляется committed build только по Git SHA.

Для согласования дренируются очереди ввода и вывода, ожидаются callbacks `terminal.write`, quiet
interval и два animation frames. Cells до/после PNG должны совпасть; ConPTY sequence не должен
измениться. Один `onWriteParsed` не считается дренированием всех writes. Истёкший deadline даёт
`BLOCKED`. Это bounded quiescence-протокол, а не блокировка внутреннего event loop приложения:
непредсказуемо меняющийся экран может не дать кадр.
Если стабильный кадр получен, но `contains` не выполнен до deadline, результат — `FAIL` assertion.

JSON-сценарии используют настоящие cells для `contains`, `absent`, размера и изменения history.
Wheel assertion сравнивает history-строки приложения и требует неизменного xterm `viewportY`.
`startup.json` проверяет полный Unicode draft, палитру/Esc и размеры с обратным восстановлением;
`history.json` — wheel/PgUp; `settings.json` — отдельное сохранение; `safe-confirmation.json` —
default cancel и отказ подтверждённой mutation. Имена captures уникальны; ошибки не затирают baseline.

## Цвет, геометрия и граница Windows Terminal

Профиль Campbell использует опубликованные Microsoft ANSI colors, непрозрачный фон и Consolas 12pt
(16 CSS px при DPR 1). Контрастный light-профиль меняет default fg/bg и cursor при той же ANSI palette.
Можно передать собственный JSON-профиль через `--profile`; пользовательский `settings.json` не читается.
До production child короткий bootstrap в той же ConPTY задаёт через Windows API реальную ANSI
палитру и default fg/bg console buffer. Его PID отделён от production PID; он не рисует UI.
Default fg/bg должны совпадать с одной из ANSI 16 записей, иначе запуск `BLOCKED`: ограничения
Win32 console attributes не маскируются другой темой. App включает VT output на время TUI и
восстанавливает режим после shutdown. При отсутствующем OSC-ответе цвет фона берётся из фактической
Win32 color table; поздний OSC-ответ обновляет палитру в UI loop без потери черновика.
Owner задаёт `TERM`/`COLORTERM` и удаляет у себя `NO_COLOR`, `FORCE_COLOR`, чужую `WT_SESSION`:
окружение неинтерактивного агента не является профилем терминала. Пользовательская среда не меняется.
FontFace с `local(...)` обязан загрузиться; незаметный fallback всего font face блокирует запуск.
Fallback отдельных glyphs, shaping и совпадение ширин с Windows Terminal этим не доказываются.

Cell metrics измеряются по `.xterm-screen`, viewport рассчитывается из них. Resize применяется и
к ConPTY, и к xterm. `minimumContrastRatio=1`, без скрытой коррекции контраста; bold не меняет ANSI
index автоматически. Профиль, эффективная тема, метрики, DPR, cursor, Unicode provider и фактически
обнаруженный renderer записаны в cells. Цветовой профиль Chromium принудительно sRGB.

```powershell
& $harness fidelity
& $harness fidelity --profile tools/AzurPilot.TuiHarness/profiles/campbell-light.json
```

Калибровка использует отдельный, явно помеченный VT-pattern через ConPTY; это **не production UI**.
Она проверяет default/ANSI 16/256/RGB, bold/dim/inverse, скрытый cursor и Unicode widths. Production
captures отдельно измеряют реальные акценты. `sgr` evidence показывает наблюдавшиеся SGR; cells
показывают распознанные modes; PNG samples численно сопоставляют однотонный фон и ближайший
foreground core pixel. Если glyph полностью сглажен, сравнение foreground отмечается `NOT_TESTED`,
а не потерей truecolor. Расхождение фона — `FAIL` в цветовых comparisons.

Это не пиксельная идентичность Windows Terminal: Chromium DOM и DirectWrite могут различать
антиалиасинг, font weight, glyph fallback, cell rounding, cursor и Unicode widths. Acrylic/Mica,
прозрачность, ligatures и произвольный DPI не эмулируются. Текстовое browser selection не равно
выделению в приложении; в evidence фиксируется фактическое состояние, а не обещание полного parity.

Для physical smoke откройте отдельный Windows Terminal с известным непрозрачным Campbell-профилем,
Consolas 12pt, padding 0 и теми же rows/columns. Запустите **тот же** EXE/DLL с другим изолированным
root, откройте то же меню, зафиксируйте binary hashes, DPI/font/profile/размер и сравните с PNG.
До запуска скопируйте канонический YAML в отдельный root; без него изолированный запуск отклоняется.
Не меняйте пользовательский default profile. Отдельно отметьте Unicode, wheel и resize ниже порога.
Если захват/доступ к physical окну недоступен, результат — `BLOCKED` с этой зависимостью; browser
captures его не заменяют. Установленный `wt.exe` сам по себе не подтверждает physical render.

## Цикл агента и vision

Процедура визуальной разработки принадлежит
[`azurpilot-tui-harness`](../../.agents/skills/azurpilot-tui-harness/SKILL.md). После каждого визуального
изменения нужен новый checkpoint, затем общий regression. Существование PNG не означает осмотр.
В Codex PNG открывается инструментом просмотра локальных изображений; оценка должна называть
видимый признак (например, взаимное положение символов и строк), статус и путь к осмотренному PNG.

Для DSH нужен доказанный путь передачи PNG конкретной модели/профилю/provider. `dsh headless --help`
проверяет CLI, но не vision. Если профиль принимает только текст и не установлена работа image tool,
помечайте vision `BLOCKED`; не называйте чтение cells анализом изображения. При запрете пользователя
на дополнительных агентов не запускайте отдельного DSH-агента ради проверки. Работающий Codex-путь
остаётся пригодным независимо от этого ограничения.

## Первичные технические контракты

- [Microsoft: lifecycle и resize ConPTY](https://learn.microsoft.com/en-us/windows/console/creating-a-pseudoconsole-session).
- [Microsoft: Windows Terminal appearance](https://learn.microsoft.com/en-us/windows/terminal/customize-settings/profile-appearance)
  и [color schemes](https://learn.microsoft.com/en-us/windows/terminal/customize-settings/color-schemes).
- [xterm.js API](https://xtermjs.org/docs/api/terminal/classes/terminal/),
  [ITheme](https://xtermjs.org/docs/api/terminal/interfaces/itheme/) и
  [IBufferCell](https://xtermjs.org/docs/api/terminal/interfaces/ibuffercell/).
- [Playwright .NET screenshots](https://playwright.dev/dotnet/docs/screenshots).
