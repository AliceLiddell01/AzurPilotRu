namespace AzurPilot.Core.Configuration;

/// <summary>Результат успешной миграции legacy JSON в канонический YAML.</summary>
/// <remarks>
/// <para>
/// Кроме провалидированного snapshot канонического документа результат несёт путь резервной копии
/// legacy-файла: операторский интерфейс обязан показать его пользователю, и вычислять имя копии
/// самостоятельно он не должен — владелец схемы имени остаётся один.
/// </para>
/// <para>
/// Резервная копия — это перемещённый legacy-файл: после успешной миграции по исходному пути
/// <c>config.json</c> файла нет, а действующим источником остаётся ровно один канонический YAML.
/// Поэтому runtime-политика «оба файла существуют» после миграции не срабатывает.
/// </para>
/// </remarks>
public sealed record AzurPilotConfigurationMigrationResult
{
    /// <summary>Snapshot канонического YAML, подтверждённый повторным чтением загрузчиком.</summary>
    public required AzurPilotConfigurationSnapshot Snapshot { get; init; }

    /// <summary>Полный путь резервной копии, в которую перенесён legacy JSON-файл.</summary>
    public required string BackupFilePath { get; init; }
}
