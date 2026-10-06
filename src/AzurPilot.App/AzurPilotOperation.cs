using System.Diagnostics;

namespace AzurPilot.App;

/// <summary>
/// Минимальная runtime correlation model приложения: одна логическая операция представлена
/// <see cref="Activity"/>, а все её шаги делят один trace identifier.
/// </summary>
/// <remarks>
/// <para>
/// Используются только стандартные <see cref="ActivitySource"/> и <see cref="Activity"/>: собственного
/// telemetry framework, exporter, collector и persistence нет. Локальный слушатель источника нужен
/// потому, что без слушателя <see cref="ActivitySource.StartActivity(string, ActivityKind)"/> возвращает
/// <see langword="null"/> и correlation identity исчезла бы; этот же слушатель — seam, к которому позже
/// можно подключить exporter, не меняя остальной код.
/// </para>
/// <para>
/// Correlation identifier операции — её trace identifier: он одинаков для startup, загрузки
/// конфигурации и native-диагностики одного запуска и попадает в structured diagnostics.
/// </para>
/// </remarks>
public sealed class AzurPilotOperation : IDisposable
{
    /// <summary>Имя <see cref="ActivitySource"/> приложения.</summary>
    public const string SourceName = "AzurPilot.App";

    /// <summary>Имя операции запуска application host.</summary>
    public const string StartupActivityName = "azurpilot.startup";

    /// <summary>Имя шага загрузки пользовательской конфигурации.</summary>
    public const string ConfigurationActivityName = "azurpilot.configuration.load";

    /// <summary>Имя шага сбора native-диагностики.</summary>
    public const string NativeDiagnosticsActivityName = "azurpilot.native.diagnostics";

    private static readonly ActivitySource OperationSource = new(
        SourceName,
        typeof(AzurPilotOperation).Assembly.GetName().Version?.ToString());

    private readonly Activity _activity;

    static AzurPilotOperation()
    {
        // Слушатель регистрируется один раз на процесс и живёт столько же: без него StartActivity
        // вернул бы null. Ссылку хранит сам ActivitySource, отдельного поля не требуется.
        _ = ListenToOperationSource();
    }

    private AzurPilotOperation(Activity activity)
    {
        _activity = activity;
        CorrelationId = activity.TraceId.ToHexString();
    }

    /// <summary>Correlation identifier операции.</summary>
    /// <value>Trace identifier операции в hex-форме: 32 символа.</value>
    public string CorrelationId { get; }

    /// <summary>Начинает операцию запуска приложения.</summary>
    /// <returns>Операция, завершаемая через <see cref="Dispose"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// Источник операции не имеет слушателя: correlation identity недоступна, и запускать приложение
    /// с неполной диагностикой нельзя.
    /// </exception>
    public static AzurPilotOperation Start()
    {
        Activity? activity = OperationSource.StartActivity(StartupActivityName, ActivityKind.Internal);
        if (activity is null)
        {
            throw new InvalidOperationException(
                $"Источник «{SourceName}» не имеет слушателя: correlation identifier операции недоступен.");
        }

        return new AzurPilotOperation(activity);
    }

    /// <summary>Начинает шаг внутри операции.</summary>
    /// <param name="name">Имя шага.</param>
    /// <returns>Шаг операции, завершаемый через <see cref="Dispose"/>; вложен в текущую операцию.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> пуст или состоит из пробелов.</exception>
    public Activity? StartStep(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // Родитель задаётся явно контекстом операции, а не неявным Activity.Current: шаг принадлежит
        // именно этой операции, поэтому её correlation identifier он разделяет гарантированно.
        return OperationSource.StartActivity(name, ActivityKind.Internal, _activity.Context);
    }

    /// <summary>Завершает операцию.</summary>
    public void Dispose() => _activity.Dispose();

    /// <summary>Регистрирует локальный слушатель источника операции.</summary>
    /// <returns>Слушатель, живущий столько же, сколько процесс.</returns>
    private static ActivityListener ListenToOperationSource()
    {
        ActivityListener listener = new()
        {
            ShouldListenTo = static source => string.Equals(source.Name, SourceName, StringComparison.Ordinal),
            Sample = SampleOperation,
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    /// <summary>Сэмплирует операцию приложения: локальная диагностика всегда получает полные данные.</summary>
    /// <param name="options">Параметры создания операции.</param>
    /// <returns>Решение сэмплирования с полными данными и признаком записи.</returns>
    private static ActivitySamplingResult SampleOperation(ref ActivityCreationOptions<ActivityContext> options)
        => ActivitySamplingResult.AllDataAndRecorded;
}
