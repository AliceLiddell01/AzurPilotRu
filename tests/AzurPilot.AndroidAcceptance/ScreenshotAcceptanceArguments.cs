using System.Globalization;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.MuMu;

namespace AzurPilot.AndroidAcceptance;

internal abstract record ScreenshotAcceptanceOptions(string? ArtifactName);

internal sealed record ScreenshotLiveOptions(
    MuMuInstanceId InstanceId,
    int CaptureCount,
    string? OutputName)
    : ScreenshotAcceptanceOptions(OutputName)
{
    internal string InstanceValue => InstanceId.ToString();
}

internal sealed record ScreenshotSamplesOptions(string DirectoryPath, string? OutputName)
    : ScreenshotAcceptanceOptions(OutputName);

internal sealed record ScreenshotAcceptanceArguments(
    ScreenshotAcceptanceOptions? Options,
    string Error)
{
    internal bool IsAccepted => Options is not null;

    internal static ScreenshotAcceptanceArguments Accepted(ScreenshotAcceptanceOptions options)
        => new(options, string.Empty);

    internal static ScreenshotAcceptanceArguments Rejected(string error)
        => new(null, error);
}

internal static class ScreenshotAcceptanceArgumentParser
{
    internal const string LiveMode = "--screenshot-live";
    internal const string SamplesMode = "--screenshot-samples";
    internal const string InstanceOption = "--instance";
    internal const string CaptureCountOption = "--captures";
    internal const string ArtifactOption = "--report-artifact";
    internal const int DefaultCaptureCount = 3;
    internal const int MaximumCaptureCount = 5;

    internal const string Usage =
        "Использование: AzurPilot.AndroidAcceptance " + LiveMode + " --instance mumu:<index> "
        + "[--captures 1..5] [--report-artifact <имя.json>] | AzurPilot.AndroidAcceptance "
        + SamplesMode + " <каталог> [--report-artifact <имя.json>].";

    internal static bool IsScreenshotMode(IReadOnlyList<string> args)
        => args.Count > 0
            && (string.Equals(args[0], LiveMode, StringComparison.Ordinal)
                || string.Equals(args[0], SamplesMode, StringComparison.Ordinal));

    internal static ScreenshotAcceptanceArguments Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0)
        {
            return ScreenshotAcceptanceArguments.Rejected("Режим screenshot acceptance не задан.");
        }

        if (string.Equals(args[0], LiveMode, StringComparison.Ordinal))
        {
            return ParseLive(args);
        }

        if (string.Equals(args[0], SamplesMode, StringComparison.Ordinal))
        {
            return ParseSamples(args);
        }

        return ScreenshotAcceptanceArguments.Rejected("Неизвестный режим screenshot acceptance.");
    }

    private static ScreenshotAcceptanceArguments ParseLive(IReadOnlyList<string> args)
    {
        string? instanceText = null;
        string? artifactName = null;
        int captureCount = DefaultCaptureCount;
        bool captureCountSet = false;

        for (int index = 1; index < args.Count; index++)
        {
            string argument = args[index];
            if (!TryReadValue(args, ref index, argument, out string? value, out string? error))
            {
                return ScreenshotAcceptanceArguments.Rejected(error!);
            }

            switch (argument)
            {
                case InstanceOption when instanceText is null:
                    instanceText = value;
                    break;
                case InstanceOption:
                    return ScreenshotAcceptanceArguments.Rejected("Опция --instance задана повторно.");
                case CaptureCountOption:
                    if (captureCountSet)
                    {
                        return ScreenshotAcceptanceArguments.Rejected("Опция --captures задана повторно.");
                    }

                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out captureCount)
                        || captureCount is < 1 or > MaximumCaptureCount)
                    {
                        return ScreenshotAcceptanceArguments.Rejected("--captures должен быть от 1 до 5.");
                    }

                    captureCountSet = true;
                    break;
                case ArtifactOption when artifactName is null:
                    if (!IsArtifactName(value!))
                    {
                        return ScreenshotAcceptanceArguments.Rejected(
                            "--report-artifact принимает только новое имя файла *.json без каталога.");
                    }

                    artifactName = value;
                    break;
                case ArtifactOption:
                    return ScreenshotAcceptanceArguments.Rejected("Опция --report-artifact задана повторно.");
                default:
                    return ScreenshotAcceptanceArguments.Rejected($"Неизвестный аргумент «{argument}».");
            }
        }

        if (instanceText is null)
        {
            return ScreenshotAcceptanceArguments.Rejected("Live capture требует явный --instance mumu:<index>.");
        }

        if (MuMuInstanceValue.IsAuto(instanceText) || !MuMuInstanceValue.IsValid(instanceText))
        {
            return ScreenshotAcceptanceArguments.Rejected(
                "--instance должен содержать явную identity mumu:<index> без ведущих нулей.");
        }

        string providerIndex = instanceText[MuMuInstanceValue.ProviderPrefix.Length..];
        return ScreenshotAcceptanceArguments.Accepted(
            new ScreenshotLiveOptions(MuMuInstanceId.FromIndex(providerIndex), captureCount, artifactName));
    }

    private static ScreenshotAcceptanceArguments ParseSamples(IReadOnlyList<string> args)
    {
        string? directoryPath = null;
        string? artifactName = null;

        for (int index = 1; index < args.Count; index++)
        {
            string argument = args[index];
            if (string.Equals(argument, ArtifactOption, StringComparison.Ordinal))
            {
                if (artifactName is not null)
                {
                    return ScreenshotAcceptanceArguments.Rejected("Опция --report-artifact задана повторно.");
                }

                if (!TryReadValue(args, ref index, argument, out string? value, out string? error))
                {
                    return ScreenshotAcceptanceArguments.Rejected(error!);
                }

                if (!IsArtifactName(value!))
                {
                    return ScreenshotAcceptanceArguments.Rejected(
                        "--report-artifact принимает только новое имя файла *.json без каталога.");
                }

                artifactName = value;
                continue;
            }

            if (argument.StartsWith("--", StringComparison.Ordinal) || directoryPath is not null)
            {
                return ScreenshotAcceptanceArguments.Rejected($"Неизвестный аргумент «{argument}».");
            }

            directoryPath = argument;
        }

        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return ScreenshotAcceptanceArguments.Rejected("Режим sample scan требует один путь каталога.");
        }

        return ScreenshotAcceptanceArguments.Accepted(new ScreenshotSamplesOptions(directoryPath, artifactName));
    }

    private static bool TryReadValue(
        IReadOnlyList<string> args,
        ref int index,
        string option,
        out string? value,
        out string? error)
    {
        if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = null;
            error = $"Опция {option} требует значение.";
            return false;
        }

        value = args[++index];
        error = null;
        return true;
    }

    private static bool IsArtifactName(string name)
        => !string.IsNullOrWhiteSpace(name)
            && string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal)
            && string.Equals(Path.GetExtension(name), ".json", StringComparison.OrdinalIgnoreCase)
            && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
}
