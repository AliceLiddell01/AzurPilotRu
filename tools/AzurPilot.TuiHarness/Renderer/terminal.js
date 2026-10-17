/* Настоящий xterm.js: ни интерпретатора VT, ни копии продуктовый UI здесь нет. */
window.harnessInitialize = async (profile, columns, rows) => {
    const font = new FontFace('HarnessVerifiedFont', `local("${profile.fontFamily}")`);
    await font.load(); // document.fonts.check сам по себе допускает незаметный fallback.
    document.fonts.add(font);
    document.body.style.cssText = `margin:0;background:${profile.theme.background};overflow:hidden`;
    const terminal = new Terminal({
        cols: columns, rows, theme: profile.theme,
        fontFamily: 'HarnessVerifiedFont', fontSize: profile.fontSize,
        fontWeight: profile.fontWeight, fontWeightBold: profile.fontWeightBold,
        lineHeight: profile.lineHeight, letterSpacing: profile.letterSpacing,
        cursorStyle: profile.cursorStyle, cursorBlink: false,
        minimumContrastRatio: 1, drawBoldTextInBrightColors: false,
        scrollback: 2000, allowProposedApi: true,
        rescaleOverlappingGlyphs: false
    });
    terminal.loadAddon(new Unicode11Addon.Unicode11Addon());
    terminal.unicode.activeVersion = '11';
    const element = document.createElement('div');
    element.id = 'terminal';
    element.style.overflow = 'hidden';
    document.body.append(element);
    terminal.open(element);
    terminal.focus();
    terminal.onData(data => window.harnessInput(data));
    let writes = 0;
    let parsed = 0;
    let cursorVisible = true;
    terminal.onWriteParsed(() => parsed++);
    for (const [final, visible] of [['h', true], ['l', false]]) {
        terminal.parser.registerCsiHandler({ prefix: '?', final }, params => {
            if (params.includes(25)) cursorVisible = visible;
            return false;
        });
    }
    const frame = () => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
    const geometry = () => {
        const box = document.querySelector('.xterm-screen').getBoundingClientRect();
        return { width: box.width, height: box.height, cellWidth: box.width / terminal.cols,
            cellHeight: box.height / terminal.rows, x: box.x, y: box.y, dpr: devicePixelRatio };
    };
    const color = (cell, foreground) => ({
        mode: (foreground ? cell.isFgRGB() : cell.isBgRGB()) ? 'rgb' :
            (foreground ? cell.isFgPalette() : cell.isBgPalette()) ? 'palette' : 'default',
        value: foreground ? cell.getFgColor() : cell.getBgColor()
    });
    const snapshot = () => {
        const buffer = terminal.buffer.active;
        const lines = [], cells = [];
        for (let y = 0; y < terminal.rows; y++) {
            const line = buffer.getLine(buffer.viewportY + y);
            lines.push(line?.translateToString(false, 0, terminal.cols) ?? '');
            for (let x = 0; x < terminal.cols; x++) {
                const cell = line?.getCell(x);
                if (!cell) continue;
                cells.push({ x, y, text: cell.getChars(), width: cell.getWidth(),
                    fg: color(cell, true), bg: color(cell, false),
                    bold: !!cell.isBold(), dim: !!cell.isDim(), inverse: !!cell.isInverse(),
                    italic: !!cell.isItalic(), underline: !!cell.isUnderline(),
                    blink: !!cell.isBlink(), invisible: !!cell.isInvisible(),
                    strike: !!cell.isStrikethrough(), overline: !!cell.isOverline() });
            }
        }
        return { columns: terminal.cols, rows: terminal.rows, lines, cells, writes, parsed,
            buffer: buffer.type, viewportY: buffer.viewportY, baseY: buffer.baseY,
            cursor: { x: buffer.cursorX, y: buffer.cursorY, visible: cursorVisible },
            modes: { mouseTrackingMode: terminal.modes.mouseTrackingMode,
                applicationCursorKeysMode: terminal.modes.applicationCursorKeysMode,
                bracketedPasteMode: terminal.modes.bracketedPasteMode },
            selection: terminal.getSelectionPosition() ?? null,
            geometry: geometry(), profile, effectiveTheme: terminal.options.theme,
            renderer: { type: document.querySelector('.xterm-rows') ? 'DOM' : 'unknown',
                canvasCount: document.querySelectorAll('canvas').length }, unicode: terminal.unicode.activeVersion,
            fontAvailable: font.status === 'loaded',
            glyphFallback: 'NOT_TESTED: отдельные глифы могут использовать резервный системный шрифт' };
    };
    window.harness = {
        write: async data => { await new Promise(resolve => terminal.write(data, resolve)); writes++; },
        frame, snapshot, geometry,
        resize: async size => {
            terminal.resize(size.columns, size.rows);
            await frame();
            element.style.width = geometry().width + 'px';
            return geometry();
        },
        text: value => terminal.paste(value),
        select: selection => terminal.select(selection.x, selection.y, selection.length),
        pngSamples: async ({ base64, points }) => {
            const img = new Image();
            img.src = 'data:image/png;base64,' + base64;
            await img.decode();
            const canvas = document.createElement('canvas');
            canvas.width = img.width; canvas.height = img.height;
            const ctx = canvas.getContext('2d', { willReadFrequently: true });
            ctx.drawImage(img, 0, 0);
            return points.map(point => {
                let rgb = Array.from(ctx.getImageData(point.pixelX, point.pixelY, 1, 1).data).slice(0, 3);
                if (point.kind === 'foreground') {
                    let best = Infinity;
                    const expected = [point.expected >> 16, (point.expected >> 8) & 255, point.expected & 255];
                    const data = ctx.getImageData(point.left, point.top, point.width, point.height).data;
                    for (let index = 0; index < data.length; index += 4) {
                        const delta = Math.max(...expected.map((value, channel) => Math.abs(value - data[index + channel])));
                        if (delta < best) { best = delta; rgb = Array.from(data.slice(index, index + 3)); }
                    }
                }
                return { ...point, rgb };
            });
        }
    };
    await frame();
    return geometry();
};
