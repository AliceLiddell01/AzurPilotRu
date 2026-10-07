using AzurPilot.App;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows.MuMu;

namespace AzurPilot.Tests.Host;

/// <summary>
/// Категории structured log, принадлежащие проекту.
/// </summary>
/// <remarks>
/// <para>
/// Правило, которое защищают проверки границы <c>stdout</c>/<c>stderr</c>: в <c>stderr</c> нет чужих
/// категорий логирования, а весь structured runtime log идёт от владельцев проекта. Категории не
/// выписываются литералами: категория события — полное имя типа-владельца логгера, поэтому набор
/// строится из самих типов.
/// </para>
/// <para>
/// MuMu-владельцы входят в набор потому, что capability реализована реальными сервисами host-а: discovery
/// сообщает Windows-адаптер, выбор экземпляра — Core orchestration, а диагностический итог — application
/// host. Чужие (framework/host) категории этим не разрешаются.
/// </para>
/// </remarks>
internal static class ProjectOwnedLogCategories
{
    /// <summary>Категория application host: <c>AzurPilot.App</c>.</summary>
    private static readonly string ApplicationHost = typeof(AzurPilotHost).Namespace!;

    /// <summary>Категория orchestration MuMu в Core.</summary>
    private static readonly string MuMuLifecycle = typeof(MuMuLifecycleService).FullName!;

    /// <summary>Категория host-side поверхности MuMu в платформенной boundary.</summary>
    private static readonly string MuMuHost = typeof(MuMuWindowsHost).FullName!;

    /// <summary>Допустимые категории project-owned событий.</summary>
    internal static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        ApplicationHost,
        MuMuLifecycle,
        MuMuHost,
    };

    /// <summary>Проверяет, принадлежит ли запись владельцу проекта.</summary>
    /// <param name="record">Запись structured runtime log.</param>
    /// <returns><see langword="true"/>, если категория записи принадлежит проекту.</returns>
    internal static bool Contains(StructuredLogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return All.Contains(record.Category);
    }
}
