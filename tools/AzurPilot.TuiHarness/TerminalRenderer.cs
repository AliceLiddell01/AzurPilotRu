using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;

namespace AzurPilot.TuiHarness;

internal sealed class TerminalRenderer : IAsyncDisposable
{
    private readonly IPlaywright _playwright;
    private readonly IBrowser _browser;
    private readonly IPage _page;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly ConPty _pty;
    private readonly Task _pump;
    private readonly StringBuilder _vt = new();
    private long _rendered;
    private long _vtBytes;
    private long _discardedVtCharacters;
    private string? _fault;
    private string? _mouseBefore;
    private bool _closing;
    internal string? DiagnosticDirectory { get; set; }
    internal bool IsAlive => _browser.IsConnected && _fault is null && !_closing;

    private TerminalRenderer(IPlaywright playwright, IBrowser browser, IPage page, ConPty pty)
    {
        _playwright = playwright;
        _browser = browser;
        _page = page;
        _pty = pty;
        _pump = PumpAsync();
    }

    internal static async Task<TerminalRenderer> CreateAsync(ConPty pty, JsonElement profile, int columns, int rows)
    {
        IPlaywright playwright = await Playwright.CreateAsync();
        IBrowser? browser = null;
        try
        {
            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = ["--force-color-profile=srgb"],
                Timeout = 20000,
            });
            IPage page = await browser.NewPageAsync(new BrowserNewPageOptions
            {
                DeviceScaleFactor = 1,
                ViewportSize = new ViewportSize { Width = 2000, Height = 1200 },
                ColorScheme = ColorScheme.Dark,
                ReducedMotion = ReducedMotion.Reduce,
            });
            page.SetDefaultTimeout(10000);
            // Никаких CDN, HTTP endpoint и доступа к пользовательскому браузерному профилю.
            _ = await page.RouteAsync("**/*", route => route.AbortAsync());
            await page.SetContentAsync("<!doctype html><html><head><meta charset='utf-8'></head><body></body></html>");
            string renderer = Path.Combine(AppContext.BaseDirectory, "Renderer");
            _ = await page.AddStyleTagAsync(new PageAddStyleTagOptions { Path = Path.Combine(renderer, "vendor", "xterm.css") });
            foreach (string script in new[] { "vendor/xterm.js", "vendor/addon-unicode11.js", "terminal.js" })
            {
                _ = await page.AddScriptTagAsync(new PageAddScriptTagOptions { Path = Path.Combine(renderer, script) });
            }

            _ = await page.ExposeFunctionAsync("harnessInput", (string data) => pty.Send(data));
            JsonElement geometry = await page.EvaluateAsync<JsonElement>(
                "async json => { const p = JSON.parse(json); return await harnessInitialize(p.profile, p.columns, p.rows); }",
                JsonSerializer.Serialize(new { profile, columns, rows }, HarnessJson.Options));
            await SetViewportAsync(page, geometry);
            return new TerminalRenderer(playwright, browser, page, pty);
        }
        catch
        {
            if (browser is not null)
            {
                await browser.CloseAsync();
            }

            playwright.Dispose();
            throw;
        }
    }

    private async Task PumpAsync()
    {
        Decoder decoder = Encoding.UTF8.GetDecoder();
        try
        {
            await foreach ((long sequence, byte[] bytes) in _pty.Output.Reader.ReadAllAsync())
            {
                char[] characters = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
                int count = decoder.GetChars(bytes, characters, flush: false);
                string value = new(characters, 0, count);
                await _gate.WaitAsync();
                try
                {
                    _vtBytes += bytes.Length;
                    _ = _vt.Append(value);
                    if (_vt.Length > 65536)
                    {
                        int discarded = _vt.Length - 65536;
                        _discardedVtCharacters += discarded;
                        _ = _vt.Remove(0, discarded);
                    }

                    _ = await _page.EvaluateAsync("async data => await harness.write(data)", value);
                    _ = Interlocked.Exchange(ref _rendered, sequence);
                }
                finally
                {
                    _ = _gate.Release();
                }
            }
        }
        catch (Exception exception)
        {
            if (!_closing)
            {
                _fault = exception.Message;
            }
        }
    }

    internal async Task InputAsync(HarnessRequest request)
    {
        await _gate.WaitAsync();
        try
        {
            switch (request.Command)
            {
                case "text":
                    _ = await _page.EvaluateAsync("value => harness.text(value)", request.Value ?? "");
                    break;
                case "key":
                    await _page.Keyboard.PressAsync(request.Value!);
                    break;
                case "resize":
                    HarnessRequest.CheckSize(request.Columns, request.Rows);
                    _pty.Resize(request.Columns, request.Rows);
                    JsonElement geometry = await _page.EvaluateAsync<JsonElement>(
                        "async size => await harness.resize(size)", new { columns = request.Columns, rows = request.Rows });
                    await SetViewportAsync(_page, geometry);
                    break;
                case "mouse":
                    JsonElement snapshot = await SnapshotAsync();
                    _mouseBefore = MouseState(snapshot);
                    string protocol = snapshot.GetProperty("modes").GetProperty("mouseTrackingMode").GetString()!;
                    if (protocol == "none")
                    {
                        throw new HarnessBlockedException("Приложение не включило mouse reporting.");
                    }

                    if (request.Value == "move" && protocol != "any")
                    {
                        throw new HarnessBlockedException("Приложение не включило reporting движения без нажатой кнопки.");
                    }

                    if (request.X < 0 || request.Y < 0 || request.X >= snapshot.GetProperty("columns").GetInt32()
                        || request.Y >= snapshot.GetProperty("rows").GetInt32())
                    {
                        throw new ArgumentException("Координаты мыши за пределами терминала.");
                    }

                    JsonElement metrics = snapshot.GetProperty("geometry");
                    await _page.Mouse.MoveAsync((float)((request.X + 0.5) * metrics.GetProperty("cellWidth").GetDouble()),
                        (float)((request.Y + 0.5) * metrics.GetProperty("cellHeight").GetDouble()));
                    switch (request.Value)
                    {
                        case "click":
                            await _page.Mouse.ClickAsync(
                            (float)((request.X + 0.5) * metrics.GetProperty("cellWidth").GetDouble()),
                            (float)((request.Y + 0.5) * metrics.GetProperty("cellHeight").GetDouble())); break;
                        case "wheel-up": await _page.Mouse.WheelAsync(0, -120); break;
                        case "wheel-down": await _page.Mouse.WheelAsync(0, 120); break;
                        case "move": break;
                        default: throw new ArgumentException("Мышь: click, move, wheel-up или wheel-down.");
                    }

                    break;
                default: throw new ArgumentException("Неизвестная операция ввода.");
            }
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    internal async Task<JsonElement> WaitAsync(string? contains, int timeoutMs, bool requireOperationIdle = false)
    {
        Stopwatch deadline = Stopwatch.StartNew();
        bool stableSeen = false;
        while (deadline.ElapsedMilliseconds < timeoutMs)
        {
            ThrowIfUnavailable();
            await _gate.WaitAsync();
            try
            {
                if (_pty.PendingInput == 0 && _pty.Sequence == Interlocked.Read(ref _rendered) && _pty.OutputIdle.TotalMilliseconds >= 200)
                {
                    _ = await _page.EvaluateAsync("async () => await harness.frame()");
                    JsonElement state = await SnapshotAsync();
                    stableSeen = true;
                    string operation = state.GetProperty("lines")[1].GetString()!.Trim();
                    bool idle = IsOperationIdle(operation);
                    if ((!requireOperationIdle || idle) && _pty.Sequence == Interlocked.Read(ref _rendered)
                        && (string.IsNullOrEmpty(contains) || VisibleText(state).Contains(contains, StringComparison.Ordinal)))
                    {
                        return state;
                    }
                }
            }
            finally
            {
                _ = _gate.Release();
            }

            await Task.Delay(30);
        }

        if (stableSeen && contains is not null && !requireOperationIdle)
        {
            throw new HarnessAssertionException($"Assertion по видимым cells не выполнен за {timeoutMs} ms: {contains}.");
        }

        throw new HarnessBlockedException($"Не достигнуто согласованное состояние за {timeoutMs} ms; ожидание: {contains ?? "quiescence"}.");
    }

    internal static bool IsOperationIdle(string operation) => operation is
        "Операция: нет" or "Операция: завершена" or "Операция: отказ" or "Операция: отменена" or "Операция: ошибка";

    internal bool MouseEffectObserved(JsonElement snapshot)
    {
        return _mouseBefore != MouseState(snapshot);
    }

    private static string MouseState(JsonElement snapshot)
    {
        return VisibleText(snapshot) + snapshot.GetProperty("cursor").GetRawText() + snapshot.GetProperty("selection").GetRawText();
    }

    internal async Task<object> CaptureAsync(string directory, string name, object identity, string? contains, int timeoutMs)
    {
        Stopwatch deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < timeoutMs)
        {
            _ = await WaitAsync(contains, Math.Max(100, timeoutMs - (int)deadline.ElapsedMilliseconds));
            await _gate.WaitAsync();
            try
            {
                long sequence = _pty.Sequence;
                if (sequence != Interlocked.Read(ref _rendered))
                {
                    continue;
                }

                JsonElement before = await SnapshotAsync();
                byte[] png = await _page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Type = ScreenshotType.Png,
                    Animations = ScreenshotAnimations.Disabled,
                    Timeout = 5000
                });
                JsonElement after = await SnapshotAsync();
                if (_pty.Sequence != sequence || before.GetRawText() != after.GetRawText())
                {
                    continue;
                }

                string prefix = Path.Combine(directory, name);
                await File.WriteAllBytesAsync(prefix + ".png", png);
                await File.WriteAllTextAsync(prefix + ".cells.json", after.GetRawText());
                await File.WriteAllTextAsync(prefix + ".vt.txt", _vt.ToString());
                object[] points = ColorSamples.Points(after);
                string samplesJson = await _page.EvaluateAsync<string>(
                    "async json => JSON.stringify(await harness.pngSamples(JSON.parse(json)))",
                    JsonSerializer.Serialize(new { base64 = Convert.ToBase64String(png), points }, HarnessJson.Options));
                using JsonDocument samples = JsonDocument.Parse(samplesJson);
                object colors = ColorSamples.Compare(samples.RootElement);
                object evidence = new
                {
                    status = "PASS",
                    identity,
                    sequence,
                    parsedSequence = Interlocked.Read(ref _rendered),
                    synchronization = "Очередь VT дренирована; write callbacks завершены; два animation frames; cells до/после PNG совпали; ConPTY sequence неизменен",
                    png = prefix + ".png",
                    cells = prefix + ".cells.json",
                    vt = prefix + ".vt.txt",
                    vtObservedBytes = _vtBytes,
                    vtTailCharacters = _vt.Length,
                    sgr = VtDiagnostics.Summarize(_vt.ToString()),
                    vtTruncated = _discardedVtCharacters > 0,
                    vtDiscardedCharacters = _discardedVtCharacters,
                    pngSha256 = Convert.ToHexString(SHA256.HashData(png)),
                    chromium = _browser.Version,
                    playwright = typeof(Playwright).Assembly.GetName().Version?.ToString(),
                    rendererDependencies = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "renderer.lock.json"))).RootElement,
                    colorComparison = colors,
                };
                await File.WriteAllTextAsync(prefix + ".evidence.json", JsonSerializer.Serialize(evidence, HarnessJson.Options));
                return new { png = prefix + ".png", cells = prefix + ".cells.json", evidence = prefix + ".evidence.json", sequence };
            }
            finally
            {
                _ = _gate.Release();
            }
        }

        throw new HarnessBlockedException("VT менялся во время PNG: согласованный capture не получен.");
    }

    private async Task<JsonElement> SnapshotAsync()
    {
        using JsonDocument document = JsonDocument.Parse(await _page.EvaluateAsync<string>("() => JSON.stringify(harness.snapshot())"));
        return document.RootElement.Clone();
    }

    internal async Task SaveDiagnosticsAsync(string directory)
    {
        await _gate.WaitAsync();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "final.vt.txt"), _vt.ToString());
            await File.WriteAllTextAsync(Path.Combine(directory, "final.cells.json"), (await SnapshotAsync()).GetRawText());
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    internal static string VisibleText(JsonElement snapshot)
    {
        return string.Join('\n', snapshot.GetProperty("lines").EnumerateArray().Select(line => line.GetString()));
    }

    private void ThrowIfUnavailable()
    {
        if (_pty.Fault is not null || _fault is not null)
        {
            throw new HarnessBlockedException(_pty.Fault ?? _fault!);
        }

        if (!_pty.Alive)
        {
            throw new HarnessBlockedException($"Production EXE завершился (0x{_pty.ExitCode:X8}); интерактивный capture недоступен.");
        }
    }

    private static Task SetViewportAsync(IPage page, JsonElement geometry)
    {
        return page.SetViewportSizeAsync(
        (int)Math.Ceiling(geometry.GetProperty("width").GetDouble()),
        (int)Math.Ceiling(geometry.GetProperty("height").GetDouble()));
    }

    public async ValueTask DisposeAsync()
    {
        _closing = true;
        try
        {
            await _pty.DisposeAsync();
            await _pump.WaitAsync(TimeSpan.FromSeconds(10));
            if (DiagnosticDirectory is string directory)
            {
                await SaveDiagnosticsAsync(directory);
            }
        }
        finally
        {
            try
            {
                await _browser.CloseAsync();
            }
            finally
            {
                _playwright.Dispose();
                _gate.Dispose();
            }
        }
    }
}

internal static class ColorSamples
{
    private static readonly string[] Ansi = ["black", "red", "green", "yellow", "blue", "magenta", "cyan", "white",
        "brightBlack", "brightRed", "brightGreen", "brightYellow", "brightBlue", "brightMagenta", "brightCyan", "brightWhite"];

    internal static object[] Points(JsonElement snapshot)
    {
        JsonElement geometry = snapshot.GetProperty("geometry");
        JsonElement theme = snapshot.GetProperty("effectiveTheme");
        object[] backgrounds = [.. snapshot.GetProperty("cells").EnumerateArray()
            .Where(cell => string.IsNullOrWhiteSpace(cell.GetProperty("text").GetString())
                && !cell.GetProperty("dim").GetBoolean())
            .GroupBy(cell => cell.GetProperty("bg").GetRawText() + cell.GetProperty("inverse").GetRawText()).Take(32)
            .Select(group => group.First())
            .Select(cell => (object)new
            {
                x = cell.GetProperty("x").GetInt32(),
                kind = "background",
                y = cell.GetProperty("y").GetInt32(),
                bg = cell.GetProperty("bg"),
                expected = cell.GetProperty("inverse").GetBoolean() ? Resolve(cell.GetProperty("fg"), theme, "foreground")
                    : Resolve(cell.GetProperty("bg"), theme, "background"),
                pixelX = (int)((cell.GetProperty("x").GetInt32() + 0.5) * geometry.GetProperty("cellWidth").GetDouble()),
                pixelY = (int)((cell.GetProperty("y").GetInt32() + 0.5) * geometry.GetProperty("cellHeight").GetDouble()),
            })];
        object[] foregrounds = [.. snapshot.GetProperty("cells").EnumerateArray()
            .Where(cell => !string.IsNullOrWhiteSpace(cell.GetProperty("text").GetString())
                && !cell.GetProperty("dim").GetBoolean() && !cell.GetProperty("inverse").GetBoolean())
            .GroupBy(cell => cell.GetProperty("fg").GetRawText()).Take(16).Select(group => group.First())
            .Select(cell => (object)new
            {
                kind = "foreground", x = cell.GetProperty("x").GetInt32(), y = cell.GetProperty("y").GetInt32(),
                fg = cell.GetProperty("fg"), expected = Resolve(cell.GetProperty("fg"), theme, "foreground"),
                left = (int)(cell.GetProperty("x").GetInt32() * geometry.GetProperty("cellWidth").GetDouble()),
                top = (int)(cell.GetProperty("y").GetInt32() * geometry.GetProperty("cellHeight").GetDouble()),
                width = (int)geometry.GetProperty("cellWidth").GetDouble(), height = (int)geometry.GetProperty("cellHeight").GetDouble(),
                pixelX = 0, pixelY = 0,
            })];
        return [.. backgrounds, .. foregrounds];
    }

    internal static int Resolve(JsonElement color, JsonElement theme, string defaultName)
    {
        int value = color.GetProperty("value").GetInt32();
        return color.GetProperty("mode").GetString() switch
        {
            "rgb" => value,
            "palette" when value < 16 => Convert.ToInt32(theme.GetProperty(Ansi[value]).GetString()![1..], 16),
            "palette" when value < 232 => (Cube((value - 16) / 36) << 16) | (Cube((value - 16) / 6 % 6) << 8) | Cube((value - 16) % 6),
            "palette" => (8 + ((value - 232) * 10)) * 0x010101,
            _ => Convert.ToInt32(theme.GetProperty(defaultName).GetString()![1..], 16),
        };
    }

    private static int Cube(int value)
    {
        return value == 0 ? 0 : 55 + (value * 40);
    }

    internal static object Compare(JsonElement samples)
    {
        object[] comparisons = [.. samples.EnumerateArray().Select(sample =>
        {
            int expected = sample.GetProperty("expected").GetInt32();
            int[] rgb = [.. sample.GetProperty("rgb").EnumerateArray().Select(value => value.GetInt32())];
            int delta = Math.Max(Math.Abs(rgb[0] - (expected >> 16)),
                Math.Max(Math.Abs(rgb[1] - ((expected >> 8) & 255)), Math.Abs(rgb[2] - (expected & 255))));
            bool glyph = sample.GetProperty("kind").GetString() == "foreground";
            return (object)new { status = delta == 0 ? "PASS" : glyph ? "NOT_TESTED" : "FAIL", sample, maxChannelDelta = delta,
                reason = glyph && delta > 0 ? "В выбранном glyph нет непрозрачного core pixel; antialiasing не равен потере RGB." : null };
        })];
        return new { scope = "Точный RGB однотонных background; ближайший core pixel foreground с явной границей antialiasing", comparisons };
    }
}
