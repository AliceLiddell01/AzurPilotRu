using AzurPilot.Core.Failures;

namespace AzurPilot.Windows.MuMu;

/// <summary>
/// Обнаружение установки MuMu по минимальному достаточному набору источников.
/// </summary>
/// <remarks>
/// <para>
/// Источники: uninstall-записи реестра, install metadata продукта и фактическое состояние каталога
/// установки. Корень установки считается кандидатом, только если он подтверждён хотя бы одним из двух
/// первых источников и в нём есть точка входа control surface поддерживаемой раскладки.
/// </para>
/// <para>
/// Несколько разрешённых установок дают статус <see cref="MuMuInstallationDiscoveryStatus.Ambiguous"/>:
/// доказуемого правила выбора между установками нет, поэтому случайный выбор запрещён. Отклонённые
/// кандидаты сохраняются с причиной, чтобы реальная установка не исчезла из диагностики.
/// </para>
/// <para>
/// Ожидаемые исходы обнаружения возвращаются значением. Отказом операции сообщается только
/// невозможность выполнить обнаружение: исключения реестра и файловой системы переводятся в
/// application-level отказ и наружу как machine contract не протекают.
/// </para>
/// </remarks>
public sealed class MuMuInstallationDiscovery
{
    private readonly IMuMuInstallationRegistrySource _registrySource;
    private readonly IMuMuInstallMetadataSource _metadataSource;
    private readonly IMuMuFileSystemProbe _fileSystemProbe;

    /// <summary>Создаёт обнаружение установок поверх трёх узких границ.</summary>
    /// <param name="registrySource">Источник uninstall-записей реестра.</param>
    /// <param name="metadataSource">Источник документов install metadata.</param>
    /// <param name="fileSystemProbe">Граница файловой системы.</param>
    /// <exception cref="ArgumentNullException">Один из аргументов равен <see langword="null"/>.</exception>
    public MuMuInstallationDiscovery(
        IMuMuInstallationRegistrySource registrySource,
        IMuMuInstallMetadataSource metadataSource,
        IMuMuFileSystemProbe fileSystemProbe)
    {
        ArgumentNullException.ThrowIfNull(registrySource);
        ArgumentNullException.ThrowIfNull(metadataSource);
        ArgumentNullException.ThrowIfNull(fileSystemProbe);

        _registrySource = registrySource;
        _metadataSource = metadataSource;
        _fileSystemProbe = fileSystemProbe;
    }

    /// <summary>Определяет, относится ли uninstall-запись к поддерживаемому семейству MuMu.</summary>
    /// <param name="candidate">Запись uninstall-раздела.</param>
    /// <returns><see langword="true"/>, если запись принадлежит семейству MuMu.</returns>
    /// <remarks>
    /// Признак семейства — издатель и отображаемое имя, сообщённые uninstall-записью. Номер версии в
    /// признак не входит: версия — evidence, а не разрешение на поддержку.
    /// </remarks>
    public static bool IsMuMuUninstallEntry(MuMuRegistryCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        return string.Equals(candidate.Publisher, MuMuProductFamily.UninstallPublisher, StringComparison.OrdinalIgnoreCase)
            && candidate.DisplayName.Contains(
                MuMuProductFamily.UninstallDisplayNameFragment,
                StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Выполняет обнаружение установок MuMu.</summary>
    /// <returns>Структурированный результат обнаружения либо application-level отказ.</returns>
    public ApplicationResult<MuMuInstallationDiscoveryResult> Discover()
    {
        IReadOnlyList<MuMuRegistryCandidate> registryCandidates;

        try
        {
            registryCandidates = _registrySource.ReadCandidates();
        }
        catch (Exception exception)
        {
            return ApplicationResult<MuMuInstallationDiscoveryResult>.Failure(
                MuMuPlatformFailureMapper.ForPlatformException(exception, MuMuFailureReasons.RegistryAccessFailed));
        }

        IReadOnlyList<MuMuInstallMetadataDocument> metadataDocuments;

        try
        {
            metadataDocuments = _metadataSource.ReadDocuments();
        }
        catch (Exception exception)
        {
            return ApplicationResult<MuMuInstallationDiscoveryResult>.Failure(
                MuMuPlatformFailureMapper.ForPlatformException(exception, MuMuFailureReasons.FileSystemAccessFailed));
        }

        List<MuMuInstallMetadata> parsedMetadata = [];
        List<MuMuRejectedInstallMetadataDocument> rejectedMetadata = [];

        foreach (MuMuInstallMetadataDocument document in metadataDocuments)
        {
            MuMuInstallMetadataParseResult parsed = MuMuInstallMetadataParser.Parse(document.FilePath, document.Content);

            if (parsed.Metadata is MuMuInstallMetadata metadata)
            {
                parsedMetadata.Add(metadata);
            }
            else
            {
                rejectedMetadata.Add(new MuMuRejectedInstallMetadataDocument
                {
                    FilePath = document.FilePath,
                    Reason = parsed.InvalidReason ?? MuMuInstallMetadataParser.InvalidJsonReason,
                });
            }
        }

        List<MuMuRegistryCandidate> muMuRegistryCandidates =
            [.. registryCandidates.Where(IsMuMuUninstallEntry)];

        List<string> candidateRoots = [];
        List<MuMuRejectedInstallationCandidate> rejectedCandidates = [];

        foreach (MuMuRegistryCandidate candidate in muMuRegistryCandidates)
        {
            AddCandidateRoot(candidateRoots, rejectedCandidates, candidate.InstallLocation);
        }

        foreach (MuMuInstallMetadata metadata in parsedMetadata)
        {
            AddCandidateRoot(candidateRoots, rejectedCandidates, metadata.InstallDirectory);
        }

        List<MuMuDiscoveredInstallation> installations = [];

        foreach (string installRoot in candidateRoots)
        {
            MuMuDiscoveredInstallation? installation = ResolveInstallation(
                installRoot,
                parsedMetadata,
                muMuRegistryCandidates,
                rejectedCandidates);

            if (installation is not null)
            {
                installations.Add(installation);
            }
        }

        return ApplicationResult<MuMuInstallationDiscoveryResult>.Success(new MuMuInstallationDiscoveryResult
        {
            Status = installations.Count switch
            {
                0 => MuMuInstallationDiscoveryStatus.NotFound,
                1 => MuMuInstallationDiscoveryStatus.Single,
                _ => MuMuInstallationDiscoveryStatus.Ambiguous,
            },
            Installations = installations,
            RejectedCandidates = rejectedCandidates,
            RejectedMetadataDocuments = rejectedMetadata,
        });
    }

    private static void AddCandidateRoot(
        List<string> candidateRoots,
        List<MuMuRejectedInstallationCandidate> rejectedCandidates,
        string? reportedRoot)
    {
        if (string.IsNullOrWhiteSpace(reportedRoot))
        {
            return;
        }

        if (!TryNormalizePath(reportedRoot, out string normalized))
        {
            // Корень, который не является абсолютным путём, не превращается в догадку о каталоге:
            // он остаётся в диагностике как отклонённый кандидат.
            rejectedCandidates.Add(Reject(reportedRoot, MuMuInstallationRejectionReasons.InstallRootNotAbsolute));
            return;
        }

        if (!candidateRoots.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            candidateRoots.Add(normalized);
        }
    }

    private MuMuDiscoveredInstallation? ResolveInstallation(
        string installRoot,
        IReadOnlyList<MuMuInstallMetadata> parsedMetadata,
        IReadOnlyList<MuMuRegistryCandidate> muMuRegistryCandidates,
        List<MuMuRejectedInstallationCandidate> rejectedCandidates)
    {
        if (!_fileSystemProbe.DirectoryExists(installRoot))
        {
            rejectedCandidates.Add(Reject(installRoot, MuMuInstallationRejectionReasons.InstallRootMissing));
            return null;
        }

        string controlExecutablePath = MuMuInstallationLayout.GetControlExecutablePath(installRoot);

        if (!_fileSystemProbe.FileExists(controlExecutablePath))
        {
            rejectedCandidates.Add(Reject(installRoot, MuMuInstallationRejectionReasons.ControlExecutableMissing));
            return null;
        }

        List<MuMuInstallMetadata> matchingMetadata =
            [.. parsedMetadata.Where(metadata => PathsEqual(metadata.InstallDirectory, installRoot))];

        if (matchingMetadata.Count > 1)
        {
            rejectedCandidates.Add(Reject(installRoot, MuMuInstallationRejectionReasons.InstallMetadataAmbiguous));
            return null;
        }

        MuMuInstallMetadata? metadata = matchingMetadata.Count == 1 ? matchingMetadata[0] : null;

        MuMuRegistryCandidate? registryCandidate = muMuRegistryCandidates.FirstOrDefault(
            candidate => PathsEqual(candidate.InstallLocation, installRoot));

        string? version = metadata?.Version;

        if (string.IsNullOrWhiteSpace(version))
        {
            version = registryCandidate?.DisplayVersion;
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            rejectedCandidates.Add(Reject(installRoot, MuMuInstallationRejectionReasons.VersionUnavailable));
            return null;
        }

        return new MuMuDiscoveredInstallation
        {
            InstallRoot = installRoot,
            ControlSurface = new MuMuControlSurface { ExecutablePath = controlExecutablePath },
            Version = version,
            VersionSource = string.IsNullOrWhiteSpace(metadata?.Version)
                ? MuMuVersionSource.UninstallRegistry
                : MuMuVersionSource.InstallMetadata,
            ProductName = metadata?.ProductName,
            ProductId = metadata?.ProductId,
            InstallMetadataConfigVersion = metadata?.ConfigVersion,
            InstallMetadataFilePath = metadata?.FilePath,
            UninstallRegistryKeyPath = registryCandidate?.RegistryKeyPath,
            AndroidEngines = metadata?.AndroidEngines ?? [],
        };
    }

    private static MuMuRejectedInstallationCandidate Reject(string installRoot, string reason)
        => new() { InstallRoot = installRoot, Reason = reason };

    private static bool PathsEqual(string? left, string? right)
        => left is not null
            && right is not null
            && TryNormalizePath(left, out string normalizedLeft)
            && TryNormalizePath(right, out string normalizedRight)
            && string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);

    private static bool TryNormalizePath(string path, out string normalized)
    {
        normalized = string.Empty;

        if (!Path.IsPathFullyQualified(path))
        {
            return false;
        }

        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }

        return true;
    }
}

/// <summary>
/// Признаки поддерживаемого семейства продуктов MuMu.
/// </summary>
/// <remarks>
/// Семейство определяется издателем и отображаемым именем установки, а не номером версии: конкретная
/// версия продукта не является условием поддержки.
/// </remarks>
public static class MuMuProductFamily
{
    /// <summary>Издатель, которым подписаны uninstall-записи семейства.</summary>
    public const string UninstallPublisher = "Netease";

    /// <summary>Фрагмент отображаемого имени, присутствующий в uninstall-записях семейства.</summary>
    public const string UninstallDisplayNameFragment = "MuMuPlayer";
}
