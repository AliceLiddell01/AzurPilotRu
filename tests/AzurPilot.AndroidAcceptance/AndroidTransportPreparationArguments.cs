using AzurPilot.Core.Configuration;
using AzurPilot.Core.MuMu;

namespace AzurPilot.AndroidAcceptance;

/// <summary>Аргументы явной подготовки ADB transport в тестовом acceptance-инструменте.</summary>
/// <param name="InstanceId">Явно выбранная identity MuMu.</param>
internal sealed record AndroidTransportPreparationOptions(MuMuInstanceId InstanceId)
{
    internal string InstanceValue => InstanceId.ToString();
}

/// <summary>Результат разбора режима подготовки transport.</summary>
/// <param name="Options">Проверенные опции либо <see langword="null"/> при отказе.</param>
/// <param name="Error">Причина отказа разбора.</param>
internal sealed record AndroidTransportPreparationArguments(
    AndroidTransportPreparationOptions? Options,
    string Error)
{
    internal static AndroidTransportPreparationArguments Accepted(AndroidTransportPreparationOptions options)
        => new(options, string.Empty);

    internal static AndroidTransportPreparationArguments Rejected(string error)
        => new(null, error);
}

/// <summary>Fail-closed parser отдельного явного режима подготовки ADB transport.</summary>
internal static class AndroidTransportPreparationArgumentParser
{
    internal const string Mode = "--prepare-transport";
    internal const string InstanceOption = "--instance";

    internal const string Usage =
        "Использование: AzurPilot.AndroidAcceptance " + Mode + " " + InstanceOption + " mumu:<index>.";

    internal static bool IsMode(IReadOnlyList<string> args)
        => args.Count > 0 && string.Equals(args[0], Mode, StringComparison.Ordinal);

    internal static AndroidTransportPreparationArguments Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (!IsMode(args))
        {
            return AndroidTransportPreparationArguments.Rejected("Режим подготовки transport не задан.");
        }

        string? instanceText = null;
        for (int index = 1; index < args.Count; index++)
        {
            string argument = args[index];
            if (!string.Equals(argument, InstanceOption, StringComparison.Ordinal))
            {
                return AndroidTransportPreparationArguments.Rejected(
                    $"Неизвестный аргумент «{argument}».");
            }

            if (instanceText is not null)
            {
                return AndroidTransportPreparationArguments.Rejected("Опция --instance задана повторно.");
            }

            if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                return AndroidTransportPreparationArguments.Rejected(
                    "Опция --instance требует явную identity mumu:<index>.");
            }

            instanceText = args[++index];
        }

        if (instanceText is null)
        {
            return AndroidTransportPreparationArguments.Rejected(
                "Подготовка требует явную identity --instance mumu:<index>.");
        }

        if (MuMuInstanceValue.IsAuto(instanceText) || !MuMuInstanceValue.IsValid(instanceText))
        {
            return AndroidTransportPreparationArguments.Rejected(
                "--instance должен содержать явную identity mumu:<index> без ведущих нулей.");
        }

        string providerIndex = instanceText[MuMuInstanceValue.ProviderPrefix.Length..];
        return AndroidTransportPreparationArguments.Accepted(
            new AndroidTransportPreparationOptions(MuMuInstanceId.FromIndex(providerIndex)));
    }
}
