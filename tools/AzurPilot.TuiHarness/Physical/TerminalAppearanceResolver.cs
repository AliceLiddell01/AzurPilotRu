using System.Globalization;
using System.Text.Json;

namespace AzurPilot.TuiHarness.Physical;

internal sealed record TerminalAppearance(
    string RequestedScheme,
    string? SchemeName,
    string? ProfileId,
    string? ProfileName,
    string? ApplicationTheme,
    string? FontFamily,
    int? FontSize,
    Dictionary<string, string>? Theme,
    string? SettingsPath,
    string? SettingsHash,
    string? PaletteSource,
    bool OpaqueBackgroundConfirmed,
    bool ComparisonReady,
    bool? UseAcrylic,
    double? OpacityPercent,
    bool? BackgroundImage,
    bool? UnfocusedAppearance,
    string? Limitation,
    object Evidence)
{
    internal bool PaletteStateConfirmed { get; init; }
    internal string? AdjustIndistinguishableColors { get; init; }

    internal bool CanCompareForeground(string colorMode)
    {
        return PaletteStateConfirmed
        && (AdjustIndistinguishableColors == "never"
            || (AdjustIndistinguishableColors == "indexed" && colorMode == "rgb"));
    }

    internal static TerminalAppearance Unknown(string requestedScheme, string reason, string? settingsPath = null,
        string? profileId = null)
    {
        return new(requestedScheme, null, profileId, null, null, null, null, null, settingsPath, null,
            null, false, false, false, null, false, false, reason,
            new
            {
                status = "NOT_TESTED",
                requestedScheme,
                settingsPath,
                selectedProfileId = profileId,
                reason,
                settingsWereReadOnly = true,
            });
    }
}

/// <summary>Разрешает цвета того профиля Windows Terminal, который сообщил именно тестируемый процесс.</summary>
internal static class TerminalAppearanceResolver
{
    private static readonly string[] PaletteKeys =
    [
        "foreground", "background", "black", "red", "green", "yellow", "blue", "magenta", "cyan", "white",
        "brightBlack", "brightRed", "brightGreen", "brightYellow", "brightBlue", "brightMagenta", "brightCyan", "brightWhite",
    ];
    private static readonly string[] SchemeBranches = ["light", "dark"];

    internal static TerminalAppearance Resolve(string settingsJson, string requestedScheme, string? selectedProfileId,
        string? systemApplicationTheme, string? settingsPath = null, string? settingsHash = null,
        string? terminalDefaultsJson = null, string? terminalDefaultsPath = null, string? terminalDefaultsHash = null,
        string? terminalDefaultsReadError = null, bool? focused = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedScheme);
        using JsonDocument document = JsonDocument.Parse(settingsJson, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = 32,
        });

        JsonElement root = document.RootElement;
        JsonElement profiles = Property(root, "profiles");
        JsonElement defaults = Property(profiles, "defaults");
        JsonElement profileList = Property(profiles, "list");
        JsonElement selected = FindProfile(profileList, selectedProfileId);
        string? profileName = Text(selected, "name");
        string? fontFamily = NestedText(selected, defaults, "font", "face");
        int? fontSize = NestedInt(selected, defaults, "font", "size");
        JsonElement unfocusedDefaults = Property(defaults, "unfocusedAppearance");
        JsonElement unfocusedProfile = Property(selected, "unfocusedAppearance");
        JsonElement unfocused = GetInherited(selected, defaults, "unfocusedAppearance");
        bool hasUnfocused = unfocused.ValueKind == JsonValueKind.Object;
        if (focused == false && hasUnfocused && selected.ValueKind == JsonValueKind.Object)
        {
            // Объект профиля наследует его обычное оформление; объект defaults — обычное оформление defaults.
            selected = unfocusedProfile.ValueKind != JsonValueKind.Undefined
                ? MergeAppearance(defaults, selected, unfocusedProfile)
                : MergeAppearance(defaults, unfocusedDefaults);
        }

        string? appTheme = ResolveApplicationTheme(root, systemApplicationTheme);

        JsonElement profileSchemeValue = Property(selected, "colorScheme");
        string? schemeName = requestedScheme != "current" ? requestedScheme
            : profileSchemeValue.ValueKind == JsonValueKind.Object && appTheme is null
                ? SelectSchemeName(profileSchemeValue, appTheme)
                : SelectSchemeName(profileSchemeValue, appTheme) ?? SelectSchemeName(Property(defaults, "colorScheme"), appTheme);
        JsonElement scheme = FindScheme(Property(root, "schemes"), schemeName);
        Dictionary<string, string>? palette = ReadPalette(scheme);
        bool paletteReadFromTerminalDefaults = false;
        if (palette is null && scheme.ValueKind != JsonValueKind.Object)
        {
            palette = ReadBuiltInPalette(terminalDefaultsJson, schemeName);
            paletteReadFromTerminalDefaults = palette is not null;
        }

        string? limitation = null;
        if (selected.ValueKind != JsonValueKind.Object)
        {
            palette = null;
            limitation = "WT_PROFILE_ID не разрешился в профиль из settings.json; профиль и его переопределения неизвестны.";
        }
        else if (schemeName is null)
        {
            palette = null;
            limitation = "Нельзя определить активную light/dark ветвь текущей цветовой схемы.";
        }
        else if (palette is null)
        {
            limitation = $"Схема '{schemeName}' отсутствует в точном settings.json или не подтверждена defaults.json выбранного Windows Terminal.";
            if (terminalDefaultsReadError is not null)
            {
                limitation += " defaults.json невозможно прочитать: " + terminalDefaultsReadError;
            }
        }

        if (palette is not null)
        {
            if (!ApplyAppearanceOverrides(palette, MergeAppearance(defaults, selected), out string? error))
            {
                palette = null;
                limitation = error;
            }
        }

        bool? useAcrylic = selected.ValueKind == JsonValueKind.Object
            ? GetInherited(selected, defaults, "useAcrylic").ValueKind == JsonValueKind.Undefined
                ? false : InheritedBoolean(selected, defaults, "useAcrylic") : null;
        double? opacity = selected.ValueKind == JsonValueKind.Object ? InheritedOpacity(selected, defaults) : null;
        bool? backgroundImage = selected.ValueKind == JsonValueKind.Object
            ? IsNonEmpty(InheritedText(selected, defaults, "backgroundImage")) : null;
        bool? unfocusedAppearance = selected.ValueKind == JsonValueKind.Object ? hasUnfocused : null;
        bool paletteStateConfirmed = selected.ValueKind == JsonValueKind.Object && (focused is not null || !hasUnfocused);
        string? adjustment = InheritedText(selected, defaults, "adjustIndistinguishableColors");
        bool renderingEffects = HasEffect(GetInherited(selected, defaults, "experimental.retroTerminalEffect"))
            || IsNonEmpty(InheritedText(selected, defaults, "experimental.pixelShaderPath"));
        bool opaque = palette is not null && useAcrylic == false && opacity == 100
            && backgroundImage == false && paletteStateConfirmed && !renderingEffects;
        bool comparisonReady = palette is not null && selected.ValueKind == JsonValueKind.Object && schemeName is not null;
        if (limitation is null && !opaque)
        {
            limitation = "Ожидаемый RGB не подтверждён: неизвестен фокус либо действуют прозрачность, изображение фона или эффекты рендеринга.";
        }

        string? paletteSource = comparisonReady
            ? paletteReadFromTerminalDefaults
                ? $"Точный defaults.json Windows Terminal ({terminalDefaultsPath}, SHA-256 {terminalDefaultsHash}): схема {schemeName}; переопределения профиля и общих настроек из settings.json"
                : $"Точный settings.json: профиль {profileName} ({selectedProfileId}), схема {schemeName} с переопределениями профиля и общих настроек"
            : null;
        return new TerminalAppearance(requestedScheme, comparisonReady ? schemeName : null,
            selectedProfileId, profileName, appTheme,
            fontFamily, fontSize, palette, settingsPath, settingsHash, paletteSource, opaque, comparisonReady,
            useAcrylic, opacity, backgroundImage, unfocusedAppearance, limitation,
            new
            {
                status = comparisonReady ? "PASS" : "NOT_TESTED",
                requestedScheme,
                settingsPath,
                settingsSha256 = settingsHash,
                terminalDefaultsPath,
                terminalDefaultsSha256 = terminalDefaultsHash,
                terminalDefaultsReadError,
                selectedProfileId,
                profileName,
                rootDefaultProfileId = Text(root, "defaultProfile"),
                profileColorScheme = DescribeSchemeValue(Property(selected, "colorScheme")),
                defaultsColorScheme = DescribeSchemeValue(Property(defaults, "colorScheme")),
                resolvedSchemeName = comparisonReady ? schemeName : "UNKNOWN",
                settingsCandidateSchemeName = schemeName,
                applicationTheme = appTheme ?? "UNKNOWN",
                fontFamily,
                fontSize,
                useAcrylic,
                opacityPercent = opacity,
                backgroundImage,
                unfocusedAppearance,
                focused,
                paletteStateConfirmed,
                adjustIndistinguishableColors = adjustment ?? "UNKNOWN",
                opaqueBackgroundConfirmed = opaque,
                palette,
                paletteSource,
                limitation,
                settingsWereReadOnly = true,
            })
        {
            PaletteStateConfirmed = paletteStateConfirmed && !renderingEffects,
            AdjustIndistinguishableColors = adjustment,
        };
    }

    internal static string? ResolveSettingsPath(string executablePath, string localApplicationData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationData);
        string fullExecutablePath = Path.GetFullPath(executablePath);
        string? packageDirectory = Path.GetDirectoryName(fullExecutablePath);
        if (packageDirectory is null)
        {
            return null;
        }

        if (File.Exists(Path.Combine(packageDirectory, ".portable")))
        {
            string portableSettings = Path.Combine(packageDirectory, "settings", "settings.json");
            return File.Exists(portableSettings) ? portableSettings : null;
        }

        string packageFolder = Path.GetFileName(packageDirectory);
        bool inWindowsApps = fullExecutablePath.Contains(
            $"{Path.DirectorySeparatorChar}WindowsApps{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
        if (inWindowsApps)
        {
            string? packageName = packageFolder.StartsWith("Microsoft.WindowsTerminal_", StringComparison.OrdinalIgnoreCase)
                ? "Microsoft.WindowsTerminal"
                : packageFolder.StartsWith("Microsoft.WindowsTerminalPreview_", StringComparison.OrdinalIgnoreCase)
                    ? "Microsoft.WindowsTerminalPreview"
                    : packageFolder.StartsWith("Microsoft.WindowsTerminalDev_", StringComparison.OrdinalIgnoreCase)
                        ? "Microsoft.WindowsTerminalDev" : null;
            if (packageName is null)
            {
                return null;
            }

            string settings = Path.Combine(localApplicationData, "Packages", packageName + "_8wekyb3d8bbwe",
                "LocalState", "settings.json");
            return File.Exists(settings) ? settings : null;
        }

        string unpackaged = Path.Combine(localApplicationData, "Microsoft", "Windows Terminal", "settings.json");
        return File.Exists(unpackaged) ? unpackaged : null;
    }

    internal static string? ResolveDefaultsPath(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        string? executableDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath));
        return executableDirectory is null ? null : Path.Combine(executableDirectory, "defaults.json");
    }

    private static Dictionary<string, string>? ReadBuiltInPalette(string? defaultsJson, string? schemeName)
    {
        if (string.IsNullOrWhiteSpace(defaultsJson) || schemeName is null)
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(defaultsJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 32,
            });
            return ReadPalette(FindScheme(Property(document.RootElement, "schemes"), schemeName));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement FindProfile(JsonElement profileList, string? profileId)
    {
        if (profileId is null || profileList.ValueKind != JsonValueKind.Array)
        {
            return default;
        }

        string normalized = NormalizeGuid(profileId);
        JsonElement[] matches = [.. profileList.EnumerateArray().Where(profile =>
            NormalizeGuid(Text(profile, "guid") ?? string.Empty) == normalized)];
        return matches.Length == 1 ? matches[0] : default;
    }

    private static string? ResolveApplicationTheme(JsonElement root, string? systemTheme)
    {
        JsonElement value = Property(root, "theme");
        if (value.ValueKind == JsonValueKind.Object)
        {
            string? branch = NormalizeTheme(systemTheme);
            if (branch is null)
            {
                return null;
            }

            value = Property(value, branch);
            if (value.ValueKind != JsonValueKind.String)
            {
                return null;
            }
        }

        string? configured = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (value.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.String))
        {
            return null;
        }

        if (configured is null or "system")
        {
            return NormalizeTheme(systemTheme);
        }

        string? direct = NormalizeTheme(configured);
        if (direct is not null)
        {
            return direct;
        }

        JsonElement themes = Property(root, "themes");
        if (themes.ValueKind == JsonValueKind.Array)
        {
            JsonElement[] matches = [.. themes.EnumerateArray().Where(item =>
                string.Equals(Text(item, "name"), configured, StringComparison.Ordinal))];
            if (matches.Length != 1)
            {
                return null;
            }

            JsonElement applicationTheme = Property(Property(matches[0], "window"), "applicationTheme");
            if (applicationTheme.ValueKind == JsonValueKind.Undefined)
            {
                return "dark";
            }

            string? name = applicationTheme.ValueKind == JsonValueKind.String ? applicationTheme.GetString() : null;
            return name == "system" ? NormalizeTheme(systemTheme) : NormalizeTheme(name);
        }

        return null;
    }

    private static string? SelectSchemeName(JsonElement configured, string? applicationTheme)
    {
        if (configured.ValueKind == JsonValueKind.String)
        {
            return configured.GetString();
        }

        if (configured.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? light = Text(configured, "light");
        string? dark = Text(configured, "dark");
        if (applicationTheme == "light")
        {
            return light;
        }

        if (applicationTheme == "dark")
        {
            return dark;
        }

        return string.Equals(light, dark, StringComparison.Ordinal) ? light : null;
    }

    private static JsonElement FindScheme(JsonElement schemes, string? name)
    {
        if (name is null || schemes.ValueKind != JsonValueKind.Array)
        {
            return default;
        }

        return schemes.EnumerateArray().FirstOrDefault(scheme =>
            string.Equals(Text(scheme, "name"), name, StringComparison.Ordinal));
    }

    private static Dictionary<string, string>? ReadPalette(JsonElement scheme)
    {
        if (scheme.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        Dictionary<string, string> result = new(StringComparer.Ordinal);
        foreach (string key in PaletteKeys)
        {
            string source = key switch { "magenta" => "purple", "brightMagenta" => "brightPurple", _ => key };
            string? value = Text(scheme, source) ?? (source == key ? null : Text(scheme, key));
            string? normalized = NormalizeRgb(value);
            if (normalized is null)
            {
                return null;
            }

            result.Add(key, normalized);
        }

        string? cursor = NormalizeRgb(Text(scheme, "cursorColor"));
        string? selection = NormalizeRgb(Text(scheme, "selectionBackground"));
        if (cursor is not null)
        {
            result["cursor"] = cursor;
        }

        if (selection is not null)
        {
            result["selectionBackground"] = selection;
        }

        return result;
    }

    private static bool ApplyAppearanceOverrides(Dictionary<string, string> palette, JsonElement source, out string? error)
    {
        error = null;
        foreach (string key in new[] { "foreground", "background", "cursorColor", "selectionBackground" })
        {
            string? value = Text(source, key);
            if (value is null)
            {
                continue;
            }

            string? normalized = NormalizeRgb(value);
            if (normalized is null)
            {
                error = $"Переопределение '{key}' не является однозначным RGB-цветом.";
                return false;
            }

            string target = key == "cursorColor" ? "cursor" : key;
            palette[target] = normalized;
        }

        return true;
    }

    private static string? NormalizeRgb(string? value)
    {
        if (value is null || value.Length is not (4 or 7) || value[0] != '#'
            || !uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
        {
            return null;
        }

        if (value.Length == 7)
        {
            return value.ToUpperInvariant();
        }

        return "#" + char.ToUpperInvariant(value[1]) + char.ToUpperInvariant(value[1])
            + char.ToUpperInvariant(value[2]) + char.ToUpperInvariant(value[2])
            + char.ToUpperInvariant(value[3]) + char.ToUpperInvariant(value[3]);
    }

    private static string? NormalizeTheme(string? theme) => theme?.ToLowerInvariant() switch
    {
        "light" => "light",
        "dark" => "dark",
        _ => null,
    };

    private static string NormalizeGuid(string value) => value.Trim().Trim('{', '}').ToLowerInvariant();

    private static string? DescribeSchemeValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return value.ValueKind == JsonValueKind.Object
            ? string.Join(",", SchemeBranches.Where(key => Text(value, key) is not null)
                .Select(key => $"{key}={Text(value, key)}"))
            : null;
    }

    private static string? NestedText(JsonElement profile, JsonElement defaults, string parent, string child)
    {
        JsonElement profileValue = Property(Property(profile, parent), child);
        if (profileValue.ValueKind == JsonValueKind.String)
        {
            return profileValue.GetString();
        }

        JsonElement defaultValue = Property(Property(defaults, parent), child);
        return defaultValue.ValueKind == JsonValueKind.String ? defaultValue.GetString() : null;
    }

    private static int? NestedInt(JsonElement profile, JsonElement defaults, string parent, string child)
    {
        JsonElement profileValue = Property(Property(profile, parent), child);
        if (profileValue.ValueKind == JsonValueKind.Number && profileValue.TryGetInt32(out int value))
        {
            return value;
        }

        JsonElement defaultValue = Property(Property(defaults, parent), child);
        return defaultValue.ValueKind == JsonValueKind.Number && defaultValue.TryGetInt32(out value) ? value : null;
    }

    private static bool? InheritedBoolean(JsonElement profile, JsonElement defaults, string property)
    {
        JsonElement value = GetInherited(profile, defaults, property);
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    }

    private static string? InheritedText(JsonElement profile, JsonElement defaults, string property)
    {
        JsonElement value = GetInherited(profile, defaults, property);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static double? InheritedOpacity(JsonElement profile, JsonElement defaults)
    {
        JsonElement opacity = GetInherited(profile, defaults, "opacity");
        if (opacity.ValueKind != JsonValueKind.Undefined)
        {
            return opacity.ValueKind == JsonValueKind.Number && opacity.TryGetDouble(out double percent)
                && percent is >= 0 and <= 100 ? percent : null;
        }

        JsonElement legacy = GetInherited(profile, defaults, "acrylicOpacity");
        if (legacy.ValueKind == JsonValueKind.Undefined)
        {
            return 100;
        }

        return legacy.ValueKind == JsonValueKind.Number && legacy.TryGetDouble(out double fraction) && fraction is >= 0 and <= 1
            ? fraction * 100 : null;
    }

    private static bool HasEffect(JsonElement appearance)
    {
        if (appearance.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        return appearance.ValueKind == JsonValueKind.Object && appearance.EnumerateObject().Any();
    }

    private static bool IsNonEmpty(string? value) => !string.IsNullOrWhiteSpace(value);

    private static JsonElement MergeAppearance(params JsonElement[] sources)
    {
        Dictionary<string, JsonElement> properties = new(StringComparer.Ordinal);
        foreach (JsonElement source in sources.Where(item => item.ValueKind == JsonValueKind.Object))
        {
            foreach (JsonProperty property in source.EnumerateObject())
            {
                if (property.Name == "colorScheme" && property.Value.ValueKind == JsonValueKind.Object
                    && properties.TryGetValue(property.Name, out JsonElement inherited))
                {
                    JsonElement branches = inherited.ValueKind == JsonValueKind.String
                        ? JsonSerializer.SerializeToElement(new { light = inherited.GetString(), dark = inherited.GetString() })
                        : inherited;
                    properties[property.Name] = MergeAppearance(branches, property.Value);
                }
                else
                {
                    properties[property.Name] = property.Value;
                }
            }
        }

        return JsonSerializer.SerializeToElement(properties);
    }

    private static JsonElement GetInherited(JsonElement profile, JsonElement defaults, string property)
    {
        JsonElement profileValue = Property(profile, property);
        return profileValue.ValueKind != JsonValueKind.Undefined ? profileValue : Property(defaults, property);
    }

    private static JsonElement Property(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) ? value : default;

    private static string? Text(JsonElement element, string name)
    {
        JsonElement value = Property(element, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
