using System.Text;
using System.Text.Json;

namespace AzurPilot.TuiHarness;

internal static class Calibration
{
    internal static int EmitPattern()
    {
        ConsoleProfile.EnableVirtualTerminalOutput();
        Console.OutputEncoding = Encoding.UTF8;
        const string esc = "\u001b[";
        StringBuilder frame = new(esc + "0m" + esc + "2J" + esc + "H" + esc + "?25lHARNESS COLOR CALIBRATION");
        for (int index = 0; index < 16; index++)
        {
            int sgr = index < 8 ? 40 + index : 100 + index - 8;
            _ = frame.Append(esc).Append(index + 2).Append(";1H").Append(esc).Append("0;").Append(sgr)
                .Append("m     ").Append(esc).Append("0m ANSI16 ").Append(index);
        }

        int[] indices = [16, 21, 196, 231, 232, 244, 255];
        for (int index = 0; index < indices.Length; index++)
        {
            _ = frame.Append(esc).Append(index + 18).Append(";1H").Append(esc).Append("0;48;5;").Append(indices[index])
                .Append("m     ").Append(esc).Append("0m ANSI256 ").Append(indices[index]);
        }

        _ = frame.Append(esc + "25;1H" + esc + "0;48;2;18;52;86m     " + esc + "0m RGB #123456")
            .Append(esc + "26;1H" + esc + "0;48;2;78;154;207m     " + esc + "0m Акцент тёмной темы #4E9ACF")
            .Append(esc + "27;1H" + esc + "0;48;2;224;108;117m     " + esc + "0m Отказ тёмной темы #E06C75")
            .Append(esc + "28;1H" + esc + "1;2;38;2;78;154;207mBoldDim" + esc + "0m")
            .Append(esc + "29;1H" + esc + "7;38;2;64;128;192m     " + esc + "0m Инверсия RGB")
            .Append(esc + "30;1HUnicode: Кириллица е́ 界" + esc + "0m");
        Console.Write(frame);
        _ = Console.ReadLine();
        return 0;
    }

    internal static async Task<HarnessResponse> RunAsync(HarnessRequest request)
    {
        HarnessResponse start = await Program.StartAsync(request with { Calibration = true, Columns = 120, Rows = 30 });
        if (start.Status != "PASS")
        {
            return start;
        }

        string id = start.Session!;
        try
        {
            HarnessResponse capture = await Program.SendAsync(new HarnessRequest
            {
                Command = "capture",
                Session = id,
                Name = "color-calibration",
                Contains = "HARNESS COLOR CALIBRATION"
            });
            if (capture.Status != "PASS")
            {
                return capture;
            }

            JsonElement paths = JsonSerializer.SerializeToElement(capture.Data, HarnessJson.Options);
            using JsonDocument cells = JsonDocument.Parse(await File.ReadAllTextAsync(paths.GetProperty("cells").GetString()!));
            JsonElement[] values = [.. cells.RootElement.GetProperty("cells").EnumerateArray()];
            List<object> assertions = [];
            void AssertBackground(int y, string mode, int value)
            {
                JsonElement cell = values.First(cell => cell.GetProperty("x").GetInt32() == 0 && cell.GetProperty("y").GetInt32() == y);
                JsonElement bg = cell.GetProperty("bg");
                bool pass = bg.GetProperty("mode").GetString() == mode && (mode == "default" || bg.GetProperty("value").GetInt32() == value);
                assertions.Add(new { y, expectedMode = mode, expectedValue = value, observed = bg, status = pass ? "PASS" : "FAIL" });
            }

            AssertBackground(0, "default", 0);
            for (int index = 0; index < 16; index++)
            {
                AssertBackground(index + 1, "palette", index);
            }

            int[] palette = [16, 21, 196, 231, 232, 244, 255];
            for (int index = 0; index < palette.Length; index++)
            {
                AssertBackground(index + 17, "palette", palette[index]);
            }

            AssertBackground(24, "rgb", 0x123456);
            AssertBackground(25, "rgb", 0x4e9acf);
            AssertBackground(26, "rgb", 0xe06c75);
            JsonElement intensity = values.First(cell => cell.GetProperty("x").GetInt32() == 0 && cell.GetProperty("y").GetInt32() == 27);
            JsonElement inverse = values.First(cell => cell.GetProperty("x").GetInt32() == 0 && cell.GetProperty("y").GetInt32() == 28);
            bool flags = intensity.GetProperty("bold").GetBoolean() && intensity.GetProperty("dim").GetBoolean()
                && inverse.GetProperty("inverse").GetBoolean() && !cells.RootElement.GetProperty("cursor").GetProperty("visible").GetBoolean();
            assertions.Add(new { name = "bold-dim-inverse-hidden-cursor", status = flags ? "PASS" : "FAIL" });
            bool unicode = values.Any(cell => cell.GetProperty("text").GetString() == "界" && cell.GetProperty("width").GetInt32() == 2)
                && values.Any(cell => cell.GetProperty("text").GetString() == "е́" && cell.GetProperty("width").GetInt32() == 1);
            assertions.Add(new { name = "unicode-combining-wide", status = unicode ? "PASS" : "FAIL" });
            using JsonDocument evidence = JsonDocument.Parse(await File.ReadAllTextAsync(paths.GetProperty("evidence").GetString()!));
            JsonElement assertionsJson = JsonSerializer.SerializeToElement(assertions, HarnessJson.Options);
            bool success = assertionsJson.EnumerateArray().All(assertion => assertion.GetProperty("status").GetString() == "PASS")
                && evidence.RootElement.GetProperty("colorComparison").GetProperty("comparisons").EnumerateArray()
                    .All(comparison => comparison.GetProperty("status").GetString() != "FAIL");
            string report = Path.ChangeExtension(paths.GetProperty("evidence").GetString()!, ".fidelity.json");
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
            {
                status = success ? "PASS" : "FAIL",
                assertions,
                capture.Data,
                scope = "Калибровка ConPTY → SGR → ячейки xterm → PNG. Отдельно от продуктового UI и физического Windows Terminal."
            }, HarnessJson.Options));
            return new(success ? "PASS" : "FAIL", id, "Проверка цветового тракта завершена.", new { report, capture = capture.Data });
        }
        finally
        {
            _ = await Program.SendAsync(new HarnessRequest { Command = "close", Session = id });
        }
    }
}
