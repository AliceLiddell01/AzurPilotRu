using AzurPilot.Core.Failures;

namespace AzurPilot.Windows.MuMu;

/// <summary>
/// Доказательство того, что установленная форма control surface поддерживается адаптером.
/// </summary>
/// <remarks>
/// <para>
/// Поддержка определяется фактическими возможностями, а не номером версии: версия сообщается как
/// evidence, но разрешением на поддержку не является. Allowlist версий вида «поддерживается только
/// конкретный номер» здесь не заводится.
/// </para>
/// <para>
/// Форма считается поддерживаемой, когда распознан ответ и на версию, и на перечисление экземпляров.
/// Команды изменения состояния отдельным флагом не подтверждаются: их форма проверяется тем же
/// контрактом ответа в момент вызова, и нераспознанный ответ даёт отказ, а не «поддержано».
/// </para>
/// </remarks>
public sealed record MuMuCapabilityReport
{
    /// <summary>Признак того, что форма control surface подтверждена целиком.</summary>
    public required bool IsControlSurfaceSupported { get; init; }

    /// <summary>Версия, которую сообщил провайдер, или <see langword="null"/>, если она не получена.</summary>
    public string? ReportedVersion { get; init; }

    /// <summary>Признак того, что ответ на подкоманду версии распознан.</summary>
    public required bool IsVersionSurfaceRecognized { get; init; }

    /// <summary>Признак того, что ответ на перечисление экземпляров распознан.</summary>
    public required bool IsInstanceEnumerationRecognized { get; init; }

    /// <summary>Число экземпляров в распознанном перечислении или <see langword="null"/>.</summary>
    public int? EnumeratedInstanceCount { get; init; }

    /// <summary>Отказ, из-за которого форма не подтверждена, или <see langword="null"/>.</summary>
    public ApplicationFailure? UnsupportedReason { get; init; }
}

/// <summary>
/// Проверяет, что установленная форма control surface действительно поддерживается адаптером.
/// </summary>
/// <remarks>
/// <para>
/// Проверка выполняется только чтением: запрашиваются версия и перечисление экземпляров. Команды
/// изменения состояния не вызываются, поэтому capability discovery не может изменить состояние ни
/// одного экземпляра.
/// </para>
/// <para>
/// Отмена операции не превращается в «не поддерживается»: она возвращается как отказ операции, чтобы
/// вызывающая сторона не приняла отменённую проверку за отрицательный результат capability.
/// </para>
/// </remarks>
public sealed class MuMuControlSurfaceProbe
{
    private readonly MuMuManagerClient _client;

    /// <summary>Создаёт проверку формы control surface поверх клиента.</summary>
    /// <param name="client">Клиент control surface.</param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> равен <see langword="null"/>.</exception>
    public MuMuControlSurfaceProbe(MuMuManagerClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <summary>Проверяет форму control surface чтением версии и перечисления экземпляров.</summary>
    /// <param name="cancellationToken">Запрос отмены операции.</param>
    /// <returns>Отчёт о поддержке либо отказ операции (например, отмена).</returns>
    public async Task<ApplicationResult<MuMuCapabilityReport>> ProbeAsync(
        CancellationToken cancellationToken = default)
    {
        ApplicationResult<string> version = await _client.GetVersionAsync(cancellationToken).ConfigureAwait(false);

        if (version.IsFailure && version.FailureInfo is ApplicationFailure versionFailure)
        {
            if (versionFailure.Code == ApplicationFailure.OperationCancelled)
            {
                return ApplicationResult<MuMuCapabilityReport>.Failure(versionFailure);
            }

            return ApplicationResult<MuMuCapabilityReport>.Success(new MuMuCapabilityReport
            {
                IsControlSurfaceSupported = false,
                IsVersionSurfaceRecognized = false,
                IsInstanceEnumerationRecognized = false,
                UnsupportedReason = versionFailure,
            });
        }

        ApplicationResult<MuMuInstanceEnumeration> enumeration =
            await _client.EnumerateInstancesAsync(cancellationToken).ConfigureAwait(false);

        if (enumeration.IsFailure && enumeration.FailureInfo is ApplicationFailure enumerationFailure)
        {
            if (enumerationFailure.Code == ApplicationFailure.OperationCancelled)
            {
                return ApplicationResult<MuMuCapabilityReport>.Failure(enumerationFailure);
            }

            return ApplicationResult<MuMuCapabilityReport>.Success(new MuMuCapabilityReport
            {
                IsControlSurfaceSupported = false,
                ReportedVersion = version.Value!,
                IsVersionSurfaceRecognized = true,
                IsInstanceEnumerationRecognized = false,
                UnsupportedReason = enumerationFailure,
            });
        }

        return ApplicationResult<MuMuCapabilityReport>.Success(new MuMuCapabilityReport
        {
            IsControlSurfaceSupported = true,
            ReportedVersion = version.Value!,
            IsVersionSurfaceRecognized = true,
            IsInstanceEnumerationRecognized = true,
            EnumeratedInstanceCount = enumeration.Value!.Instances.Count,
        });
    }
}
