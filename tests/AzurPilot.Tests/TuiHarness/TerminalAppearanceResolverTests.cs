using System.Text.Json.Nodes;
using AzurPilot.TuiHarness.Physical;
using Xunit;

namespace AzurPilot.Tests.TuiHarness;

public sealed class TerminalAppearanceResolverTests
{
    private const string DefaultProfileId = "{11111111-1111-1111-1111-111111111111}";
    private const string PowerShellProfileId = "{22222222-2222-2222-2222-222222222222}";

    [Fact]
    public void Resolve_CurrentUsesTheExactWtProfileAndEffectiveDarkSchemeWithItsOverrides()
    {
        JsonObject settings = Settings();
        JsonObject selected = (JsonObject)settings["profiles"]!["list"]![1]!;
        selected["colorScheme"] = new JsonObject { ["light"] = "PowerShell Light", ["dark"] = "User Dark" };
        selected["background"] = "#1e1b18";
        selected["foreground"] = "#e5d5b5";
        selected["opacity"] = 70;
        selected["useAcrylic"] = true;
        settings["schemes"]!.AsArray().Add(ColorScheme("User Dark", "#050505", "#f0f0f0"));
        settings["schemes"]!.AsArray().Add(ColorScheme("PowerShell Light", "#fafafa", "#111111"));

        TerminalAppearance result = TerminalAppearanceResolver.Resolve(settings.ToJsonString(), "current",
            PowerShellProfileId, "dark", "settings.json", "settings-hash");

        Assert.Equal("PowerShell", result.ProfileName);
        Assert.Equal(PowerShellProfileId, result.ProfileId);
        Assert.Equal("User Dark", result.SchemeName);
        Assert.Equal("#1E1B18", result.Theme!["background"]);
        Assert.Equal("#E5D5B5", result.Theme["foreground"]);
        Assert.Equal(70, result.OpacityPercent);
        Assert.True(result.UseAcrylic);
        Assert.False(result.OpaqueBackgroundConfirmed);
        Assert.True(result.ComparisonReady);
    }

    [Fact]
    public void Resolve_ProfileThemeBranchFallsBackToTerminalDefaultsForMissingBranch()
    {
        JsonObject settings = Settings();
        JsonObject defaults = (JsonObject)settings["profiles"]!["defaults"]!;
        defaults["colorScheme"] = new JsonObject { ["light"] = "Default Light", ["dark"] = "Default Dark" };
        JsonObject selected = (JsonObject)settings["profiles"]!["list"]![1]!;
        selected["colorScheme"] = new JsonObject { ["light"] = "PowerShell Light" };
        settings["schemes"]!.AsArray().Add(ColorScheme("Default Dark", "#202020", "#f0f0f0"));
        settings["schemes"]!.AsArray().Add(ColorScheme("Default Light", "#ffffff", "#111111"));
        settings["schemes"]!.AsArray().Add(ColorScheme("PowerShell Light", "#eeeeee", "#111111"));

        TerminalAppearance result = TerminalAppearanceResolver.Resolve(settings.ToJsonString(), "current",
            PowerShellProfileId, "dark");

        Assert.Equal("Default Dark", result.SchemeName);
        Assert.Equal("#202020", result.Theme!["background"]);
        Assert.True(result.ComparisonReady);
    }

    [Fact]
    public void Resolve_UnknownSystemThemeDoesNotGuessBetweenProfileLightAndDarkSchemes()
    {
        JsonObject settings = Settings();
        JsonObject selected = (JsonObject)settings["profiles"]!["list"]![1]!;
        selected["colorScheme"] = new JsonObject { ["light"] = "PowerShell Light", ["dark"] = "User Dark" };
        settings["schemes"]!.AsArray().Add(ColorScheme("User Dark", "#050505", "#f0f0f0"));
        settings["schemes"]!.AsArray().Add(ColorScheme("PowerShell Light", "#fafafa", "#111111"));

        TerminalAppearance result = TerminalAppearanceResolver.Resolve(settings.ToJsonString(), "current",
            PowerShellProfileId, systemApplicationTheme: null);

        Assert.Null(result.SchemeName);
        Assert.Null(result.Theme);
        Assert.False(result.ComparisonReady);
        Assert.Contains("light/dark", result.Limitation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_CustomTerminalThemeSelectsItsApplicationThemeAndProfileDefaultsRemainDistinct()
    {
        JsonObject settings = Settings();
        settings["theme"] = "User UI Theme";
        settings["themes"] = new JsonArray(new JsonObject
        {
            ["name"] = "User UI Theme",
            ["window"] = new JsonObject { ["applicationTheme"] = "light" },
        });
        _ = settings["profiles"]!["defaults"]!.AsObject().Remove("background");
        JsonObject selected = (JsonObject)settings["profiles"]!["list"]![1]!;
        selected["colorScheme"] = new JsonObject { ["light"] = "PowerShell Light", ["dark"] = "User Dark" };
        settings["schemes"]!.AsArray().Add(ColorScheme("User Dark", "#050505", "#f0f0f0"));
        settings["schemes"]!.AsArray().Add(ColorScheme("PowerShell Light", "#fafafa", "#111111"));

        TerminalAppearance result = TerminalAppearanceResolver.Resolve(settings.ToJsonString(), "current",
            PowerShellProfileId, systemApplicationTheme: "dark");

        Assert.Equal("light", result.ApplicationTheme);
        Assert.Equal("PowerShell Light", result.SchemeName);
        Assert.Equal("#FAFAFA", result.Theme!["background"]);
    }

    [Fact]
    public void Resolve_DoesNotUseRootDefaultProfileWhenWtProfileIdNamesAnotherProfile()
    {
        JsonObject settings = Settings();
        JsonObject selected = (JsonObject)settings["profiles"]!["list"]![1]!;
        selected["colorScheme"] = "PowerShell Custom";
        settings["schemes"]!.AsArray().Add(ColorScheme("PowerShell Custom", "#24201c", "#e0d0b0"));

        TerminalAppearance result = TerminalAppearanceResolver.Resolve(settings.ToJsonString(), "current",
            PowerShellProfileId, "dark");

        Assert.Equal("PowerShell Custom", result.SchemeName);
        Assert.Equal("#202020", result.Theme!["background"]);
        Assert.NotEqual("Default Profile", result.ProfileName);
    }

    [Fact]
    public void Resolve_UnmatchedWtProfileDoesNotBorrowPaletteOrAppearanceFromDefaults()
    {
        TerminalAppearance result = TerminalAppearanceResolver.Resolve(Settings().ToJsonString(), "current",
            "{33333333-3333-3333-3333-333333333333}", "dark");

        Assert.Null(result.ProfileName);
        Assert.Null(result.SchemeName);
        Assert.Null(result.Theme);
        Assert.Null(result.UseAcrylic);
        Assert.Null(result.OpacityPercent);
        Assert.Null(result.BackgroundImage);
        Assert.Null(result.UnfocusedAppearance);
        Assert.False(result.OpaqueBackgroundConfirmed);
        Assert.False(result.ComparisonReady);
        Assert.Contains("WT_PROFILE_ID", result.Limitation, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveSettingsPath_UsesThePackageOfTheActualWindowsTerminalExecutable()
    {
        string root = Path.Combine(Path.GetTempPath(), "TerminalAppearanceResolverTests", Guid.NewGuid().ToString("N"));
        string local = Path.Combine(root, "LocalAppData");
        string previewSettings = Path.Combine(local, "Packages", "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe",
            "LocalState", "settings.json");
        string stableSettings = Path.Combine(local, "Packages", "Microsoft.WindowsTerminal_8wekyb3d8bbwe",
            "LocalState", "settings.json");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(previewSettings)!);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(stableSettings)!);
        File.WriteAllText(previewSettings, "{}");
        File.WriteAllText(stableSettings, "{}");
        string executable = Path.Combine(root, "WindowsApps", "Microsoft.WindowsTerminalPreview_1.0_x64__8wekyb3d8bbwe",
            "WindowsTerminal.exe");

        try
        {
            Assert.Equal(previewSettings, TerminalAppearanceResolver.ResolveSettingsPath(executable, local));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveSettingsPath_DoesNotGuessAWindowsAppsPackageFromAnotherInstalledSettingsFile()
    {
        string local = Path.Combine(Path.GetTempPath(), "TerminalAppearanceResolverTests", Guid.NewGuid().ToString("N"));
        string stableSettings = Path.Combine(local, "Packages", "Microsoft.WindowsTerminal_8wekyb3d8bbwe",
            "LocalState", "settings.json");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(stableSettings)!);
        File.WriteAllText(stableSettings, "{}");
        string executable = Path.Combine(local, "WindowsApps", "Unknown.Terminal_1.0_x64__publisher", "WindowsTerminal.exe");

        try
        {
            Assert.Null(TerminalAppearanceResolver.ResolveSettingsPath(executable, local));
        }
        finally
        {
            Directory.Delete(local, recursive: true);
        }
    }

    private static JsonObject Settings()
    {
        return new JsonObject
        {
            ["defaultProfile"] = DefaultProfileId,
            ["profiles"] = new JsonObject
            {
                ["defaults"] = new JsonObject
                {
                    ["colorScheme"] = "Factory",
                    ["background"] = "#202020",
                    ["opacity"] = 100,
                    ["font"] = new JsonObject { ["face"] = "Cascadia Mono", ["size"] = 14 },
                },
                ["list"] = new JsonArray
                {
                    new JsonObject { ["guid"] = DefaultProfileId, ["name"] = "Default Profile", ["colorScheme"] = "Factory" },
                    new JsonObject { ["guid"] = PowerShellProfileId, ["name"] = "PowerShell", ["colorScheme"] = "Factory" },
                },
            },
            ["schemes"] = new JsonArray(ColorScheme("Factory", "#0c0c0c", "#cccccc")),
        };
    }

    private static JsonObject ColorScheme(string name, string background, string foreground)
    {
        JsonObject scheme = new()
        {
            ["name"] = name,
            ["background"] = background,
            ["foreground"] = foreground,
        };
        string[] keys = ["black", "red", "green", "yellow", "blue", "purple", "cyan", "white",
            "brightBlack", "brightRed", "brightGreen", "brightYellow", "brightBlue", "brightPurple", "brightCyan", "brightWhite"];
        for (int index = 0; index < keys.Length; index++)
        {
            scheme[keys[index]] = $"#{index + 1:X6}";
        }

        return scheme;
    }
}
