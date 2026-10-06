using System.Text;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AzurPilot.Tests.Configuration;

/// <summary>
/// Доказывает поведение загрузчика конфигурации: встроенные defaults при отсутствии файла, полное
/// чтение валидного файла, неизменяемость snapshot и явные отказы вместо исключений.
/// </summary>
[Trait("Category", "Configuration")]
public sealed class AzurPilotConfigurationLoaderTests
{
    private const string ValidWarningDocument =
        """{"schemaVersion":1,"diagnostics":{"minimumLevel":"Warning"}}""";

    [Fact(DisplayName = "Отсутствующий файл конфигурации даёт snapshot встроенных defaults")]
    public void MissingFileProducesBuiltInDefaultsSnapshot()
    {
        using TemporaryConfigurationDirectory directory = new();

        ApplicationResult<AzurPilotConfigurationSnapshot> result =
            AzurPilotConfigurationLoader.Load(directory.MissingConfigurationFilePath);

        Assert.True(result.IsSuccess, $"Отсутствие файла — валидный сценарий, получено {result}.");
        AzurPilotConfigurationSnapshot snapshot = result.Value!;
        Assert.Equal(AzurPilotConfigurationSource.BuiltInDefaults, snapshot.Source);
        Assert.True(snapshot.IsBuiltInDefaults);
        Assert.Null(snapshot.FilePath);

        // Значения приходят из единственного владельца defaults, а не из второй копии в загрузчике.
        Assert.Equal(AzurPilotConfigurationDefaults.Create(), snapshot.Configuration);
        Assert.Equal(AzurPilotConfiguration.CurrentSchemaVersion, snapshot.Configuration.SchemaVersion);
        Assert.Equal(LogLevel.Information, snapshot.Configuration.Diagnostics.MinimumLevel);

        // Загрузка не создаёт файл: отсутствие файла остаётся валидным сценарием и не имеет side effects.
        Assert.False(File.Exists(directory.MissingConfigurationFilePath));
    }

    [Fact(DisplayName = "Валидный файл schema v1 читается полностью и сохраняет путь в snapshot")]
    public void ValidFileIsLoadedAndPathIsPreserved()
    {
        using TemporaryConfigurationDirectory directory = new();
        string path = directory.WriteConfiguration(ValidWarningDocument);

        ApplicationResult<AzurPilotConfigurationSnapshot> result = AzurPilotConfigurationLoader.Load(path);

        Assert.True(result.IsSuccess, $"Валидный файл schema v1 обязан загружаться, получено {result}.");
        AzurPilotConfigurationSnapshot snapshot = result.Value!;
        Assert.Equal(AzurPilotConfigurationSource.File, snapshot.Source);
        Assert.False(snapshot.IsBuiltInDefaults);
        Assert.Equal(path, snapshot.FilePath);
        Assert.Equal(AzurPilotConfiguration.CurrentSchemaVersion, snapshot.Configuration.SchemaVersion);
        Assert.Equal(LogLevel.Warning, snapshot.Configuration.Diagnostics.MinimumLevel);
    }

    [Fact(DisplayName = "Изменение файла после загрузки не меняет уже полученный snapshot")]
    public void FileChangeAfterLoadDoesNotAffectSnapshot()
    {
        using TemporaryConfigurationDirectory directory = new();
        string path = directory.WriteConfiguration(ValidWarningDocument);
        AzurPilotConfigurationSnapshot snapshot = AzurPilotConfigurationLoader.Load(path).Value!;

        // Файл переписан другим значением: snapshot уже загружен и не перечитывается (hot reload нет).
        _ = directory.WriteConfiguration("""{"schemaVersion":1,"diagnostics":{"minimumLevel":"Critical"}}""");
        Assert.Equal(LogLevel.Warning, snapshot.Configuration.Diagnostics.MinimumLevel);

        // Удаление файла тоже не меняет загруженный snapshot и не переключает его на defaults.
        File.Delete(path);
        Assert.Equal(LogLevel.Warning, snapshot.Configuration.Diagnostics.MinimumLevel);
        Assert.Equal(AzurPilotConfigurationSource.File, snapshot.Source);
        Assert.Equal(path, snapshot.FilePath);
    }

    [Fact(DisplayName = "Существующий невалидный файл не подменяется defaults, а даёт configuration_invalid")]
    public void ExistingInvalidFileIsNeverReplacedByDefaults()
    {
        using TemporaryConfigurationDirectory directory = new();
        string path = directory.WriteConfiguration("""{"schemaVersion":1,"diagnostics":""");

        ApplicationResult<AzurPilotConfigurationSnapshot> result = AzurPilotConfigurationLoader.Load(path);

        ApplicationFailure failure = ConfigurationTestAssertions.AssertFailure(
            result,
            ApplicationFailure.ConfigurationInvalid,
            "синтаксически невалидный существующий файл");
        Assert.Equal(path, failure.Details!["config_path"]);
        Assert.False(failure.IsRetryable, "Отказ конфигурации не является retryable: файл должен исправить оператор.");
    }

    [Fact(DisplayName = "Пустой существующий файл отклоняется как configuration_invalid")]
    public void EmptyExistingFileIsRejected()
    {
        using TemporaryConfigurationDirectory directory = new();
        string path = directory.WriteConfiguration(string.Empty);

        ApplicationResult<AzurPilotConfigurationSnapshot> result = AzurPilotConfigurationLoader.Load(path);

        _ = ConfigurationTestAssertions.AssertFailure(result, ApplicationFailure.ConfigurationInvalid, "пустой файл");
    }

    [Fact(DisplayName = "Путь, указывающий на каталог, отклоняется как configuration_invalid")]
    public void DirectoryAtConfigurationPathIsRejected()
    {
        using TemporaryConfigurationDirectory directory = new();
        string path = directory.CreateDirectoryAtConfigurationPath();

        ApplicationResult<AzurPilotConfigurationSnapshot> result = AzurPilotConfigurationLoader.Load(path);

        _ = ConfigurationTestAssertions.AssertFailure(
            result,
            ApplicationFailure.ConfigurationInvalid,
            "каталог вместо файла");
    }

    [Fact(DisplayName = "Нечитаемый файл отклоняется как configuration_invalid, а не исключением")]
    public void UnreadableFileIsRejectedAsFailure()
    {
        using TemporaryConfigurationDirectory directory = new();
        string path = directory.WriteConfiguration(ValidWarningDocument);
        using FileStream locked = new(path, FileMode.Open, FileAccess.Read, FileShare.None);

        ApplicationResult<AzurPilotConfigurationSnapshot> result = AzurPilotConfigurationLoader.Load(path);

        ApplicationFailure failure = ConfigurationTestAssertions.AssertFailure(
            result,
            ApplicationFailure.ConfigurationInvalid,
            "файл занят другим процессом");
        Assert.Equal(path, failure.Details!["config_path"]);
    }

    [Fact(DisplayName = "UTF-8 BOM допускается: это кодировка, а не нарушение схемы")]
    public void ByteOrderMarkIsAccepted()
    {
        using TemporaryConfigurationDirectory directory = new();
        byte[] content =
        [
            .. Encoding.UTF8.GetPreamble(),
            .. new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(ValidWarningDocument),
        ];
        string path = directory.WriteConfigurationBytes(content);

        ApplicationResult<AzurPilotConfigurationSnapshot> result = AzurPilotConfigurationLoader.Load(path);

        Assert.True(result.IsSuccess, $"Файл в UTF-8 с BOM обязан читаться, получено {result}.");
        Assert.Equal(LogLevel.Warning, result.Value!.Configuration.Diagnostics.MinimumLevel);
    }

    [Fact(DisplayName = "Пустой путь и относительный путь — ошибка программирования, а не ожидаемый отказ")]
    public void NonAbsolutePathIsProgrammingError()
    {
        _ = Assert.Throws<ArgumentNullException>(() => AzurPilotConfigurationLoader.Load(null!));
        _ = Assert.Throws<ArgumentException>(() => AzurPilotConfigurationLoader.Load("   "));
        _ = Assert.Throws<ArgumentException>(
            () => AzurPilotConfigurationLoader.Load(Path.Combine("config", "config.json")));
    }
}
