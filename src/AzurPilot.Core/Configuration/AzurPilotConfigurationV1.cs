using System.Text.Json.Serialization;

namespace AzurPilot.Core.Configuration;

/// <summary>
/// Legacy-контракт схемы v1: документ с <c>schemaVersion = 1</c> и единственной секцией диагностики.
/// </summary>
/// <remarks>
/// <para>
/// Число 1 — версия этой legacy-схемы, поэтому оно принадлежит этому типу; версия, которую понимает
/// текущая сборка, принадлежит <see cref="AzurPilotConfiguration.CurrentSchemaVersion"/>. Второй копии
/// ни одного из этих чисел не заводится.
/// </para>
/// <para>
/// Контракт существует только для чтения legacy-входа: он не сериализуется обратно, файл v1 не
/// переписывается и не создаётся, migration/repair/update-команды не вводятся. Строгость к v1 не
/// ослабляется: секция <c>mumu</c> для этого контракта — неизвестное свойство, поэтому документ
/// v1 с ней невалиден.
/// </para>
/// </remarks>
internal sealed record AzurPilotConfigurationV1
{
    /// <summary>Версия legacy-схемы, которой соответствует этот контракт.</summary>
    internal const int LegacySchemaVersion = 1;

    /// <summary>Версия схемы конфигурации, объявленная в legacy-документе.</summary>
    [JsonPropertyName("schemaVersion")]
    public required int SchemaVersion { get; init; }

    /// <summary>Настройки диагностики приложения.</summary>
    [JsonPropertyName("diagnostics")]
    public required DiagnosticsConfiguration Diagnostics { get; init; }

    /// <summary>Нормализует legacy-документ v1 к эффективной схеме v2 в памяти.</summary>
    /// <returns>Конфигурация схемы v2: прочитанная диагностика и встроенное значение новой секции.</returns>
    /// <remarks>
    /// Нормализация выполняется только в памяти и не касается файла. Значения, которых в схеме v1 не
    /// было, берутся у единственного владельца встроенных defaults, поэтому второй копии значения
    /// <c>mumu.instance = "auto"</c> не появляется.
    /// </remarks>
    internal AzurPilotConfiguration ToCurrentSchema()
        => AzurPilotConfigurationDefaults.Create() with { Diagnostics = Diagnostics };
}
