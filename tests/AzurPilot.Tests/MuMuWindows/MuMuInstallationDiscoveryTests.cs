using AzurPilot.Core.Failures;
using AzurPilot.Windows.MuMu;
using Xunit;

namespace AzurPilot.Tests.MuMuWindows;

/// <summary>
/// Доказательства обнаружения установки: ни одной, ровно одна, несколько, а также поведение при
/// отклонённых кандидатах и при ошибках платформенных источников.
/// </summary>
/// <remarks>
/// Проверки идут через подменяемые границы реестра, install metadata и файловой системы: ни
/// установленная MuMu, ни реальные ключи реестра не участвуют. Абсолютные пути собираются в runtime из
/// временного каталога, поэтому machine-specific констант в проверках нет.
/// </remarks>
[Trait("Category", "MuMuWindows")]
public sealed class MuMuInstallationDiscoveryTests
{
    [Fact(DisplayName = "Установка не обнаружена: это ожидаемый результат, а не отказ операции")]
    public void MissingInstallationIsExpectedOutcome()
    {
        DiscoveryFixture fixture = new();

        ApplicationResult<MuMuInstallationDiscoveryResult> result = fixture.Discovery.Discover();

        Assert.True(result.IsSuccess);
        Assert.Equal(MuMuInstallationDiscoveryStatus.NotFound, result.Value!.Status);
        Assert.Empty(result.Value!.Installations);
        Assert.Empty(result.Value!.RejectedCandidates);
    }

    [Fact(DisplayName = "Одна установка разрешается вместе с версией, движками и control surface")]
    public void SingleInstallationIsResolved()
    {
        DiscoveryFixture fixture = new();

        string installRoot = fixture.AddInstallation("single", registryVersion: null);

        ApplicationResult<MuMuInstallationDiscoveryResult> result = fixture.Discovery.Discover();

        Assert.True(result.IsSuccess);
        Assert.Equal(MuMuInstallationDiscoveryStatus.Single, result.Value!.Status);

        MuMuDiscoveredInstallation installation = Assert.Single(result.Value!.Installations);

        Assert.Equal(installRoot, installation.InstallRoot);
        Assert.Equal("6.8.0.0", installation.Version);
        Assert.Equal(MuMuVersionSource.InstallMetadata, installation.VersionSource);
        Assert.Equal("MuMu6.0", installation.ProductId);
        Assert.Equal("MuMuPlayerGlobal", installation.ProductName);
        Assert.Equal("2", installation.InstallMetadataConfigVersion);
        Assert.Equal(
            MuMuTestInstallation.ControlExecutablePath(installRoot),
            installation.ControlSurface.ExecutablePath);
        Assert.Equal(2, installation.AndroidEngines.Count);
        Assert.Equal("12.0", installation.AndroidEngines[0].AndroidVersion);
        Assert.Equal("15.0", installation.AndroidEngines[1].AndroidVersion);
        Assert.NotNull(installation.InstallMetadataFilePath);
        Assert.NotNull(installation.UninstallRegistryKeyPath);
        Assert.Empty(result.Value!.RejectedCandidates);
    }

    [Fact(DisplayName = "Версия берётся из uninstall-записи реестра, если install metadata недоступна")]
    public void VersionFallsBackToRegistry()
    {
        DiscoveryFixture fixture = new();

        string installRoot = fixture.AddInstallation(
            "registry-only",
            registryVersion: "6.8.0.0",
            withMetadata: false);

        ApplicationResult<MuMuInstallationDiscoveryResult> result = fixture.Discovery.Discover();

        MuMuDiscoveredInstallation installation = Assert.Single(result.Value!.Installations);

        Assert.Equal(installRoot, installation.InstallRoot);
        Assert.Equal("6.8.0.0", installation.Version);
        Assert.Equal(MuMuVersionSource.UninstallRegistry, installation.VersionSource);
        Assert.Empty(installation.AndroidEngines);
        Assert.Null(installation.InstallMetadataFilePath);
    }

    [Fact(DisplayName = "Несколько установок дают ambiguity вместо случайного выбора")]
    public void MultipleInstallationsAreAmbiguous()
    {
        DiscoveryFixture fixture = new();

        _ = fixture.AddInstallation("first");
        _ = fixture.AddInstallation("second");

        ApplicationResult<MuMuInstallationDiscoveryResult> result = fixture.Discovery.Discover();

        Assert.True(result.IsSuccess);
        Assert.Equal(MuMuInstallationDiscoveryStatus.Ambiguous, result.Value!.Status);
        Assert.Equal(2, result.Value!.Installations.Count);
    }

    [Fact(DisplayName = "Повторный корень установки не превращается во вторую установку")]
    public void DuplicateRootIsDeduplicated()
    {
        DiscoveryFixture fixture = new();

        string installRoot = fixture.AddInstallation("duplicate");

        // Та же установка видна в двух представлениях реестра: ключи разные, корень один.
        fixture.Registry.Add(new MuMuRegistryCandidate
        {
            RegistryKeyPath = "LocalMachine\\Registry32\\Uninstall\\MuMuPlayerGlobal",
            DisplayName = "MuMuPlayer",
            DisplayVersion = "6.8.0.0",
            InstallLocation = installRoot,
            Publisher = "Netease",
        });

        ApplicationResult<MuMuInstallationDiscoveryResult> result = fixture.Discovery.Discover();

        Assert.Equal(MuMuInstallationDiscoveryStatus.Single, result.Value!.Status);
        _ = Assert.Single(result.Value!.Installations);
    }

    [Theory(DisplayName = "Записи чужого семейства не считаются установкой MuMu")]
    [InlineData("Other Publisher", "MuMuPlayer", "6.8.0.0")]
    [InlineData("Netease", "Другой продукт", "6.8.0.0")]
    [InlineData("Netease", "Android Device", "1.0.0.0")]
    public void ForeignUninstallEntriesAreIgnored(string publisher, string displayName, string version)
    {
        DiscoveryFixture fixture = new();

        fixture.Registry.Add(new MuMuRegistryCandidate
        {
            RegistryKeyPath = "LocalMachine\\Registry64\\Uninstall\\Foreign",
            DisplayName = displayName,
            DisplayVersion = version,
            InstallLocation = MuMuWindowsTestPaths.Create("foreign"),
            Publisher = publisher,
        });

        ApplicationResult<MuMuInstallationDiscoveryResult> result = fixture.Discovery.Discover();

        Assert.Equal(MuMuInstallationDiscoveryStatus.NotFound, result.Value!.Status);
        Assert.Empty(result.Value!.RejectedCandidates);
    }

    [Fact(DisplayName = "Корень без control executable отклоняется с причиной")]
    public void InstallRootWithoutControlExecutableIsRejected()
    {
        DiscoveryFixture fixture = new();

        string installRoot = fixture.AddInstallation("without-control", withControlExecutable: false);

        ApplicationResult<MuMuInstallationDiscoveryResult> result = fixture.Discovery.Discover();

        Assert.Equal(MuMuInstallationDiscoveryStatus.NotFound, result.Value!.Status);

        MuMuRejectedInstallationCandidate rejected = Assert.Single(result.Value!.RejectedCandidates);

        Assert.Equal(installRoot, rejected.InstallRoot);
        Assert.Equal(MuMuInstallationRejectionReasons.ControlExecutableMissing, rejected.Reason);
    }

    [Fact(DisplayName = "Отсутствующий каталог установки отклоняется с причиной")]
    public void MissingInstallRootIsRejected()
    {
        DiscoveryFixture fixture = new();

        string installRoot = MuMuWindowsTestPaths.Create("missing-root");

        fixture.Registry.Add(MuMuCandidate(installRoot, "6.8.0.0"));

        ApplicationResult<MuMuInstallationDiscoveryResult> result = fixture.Discovery.Discover();

        MuMuRejectedInstallationCandidate rejected = Assert.Single(result.Value!.RejectedCandidates);

        Assert.Equal(installRoot, rejected.InstallRoot);
        Assert.Equal(MuMuInstallationRejectionReasons.InstallRootMissing, rejected.Reason);
    }

    [Fact(DisplayName = "Неабсолютный корень установки отклоняется, а не достраивается")]
    public void RelativeInstallRootIsRejected()
    {
        DiscoveryFixture fixture = new();

        fixture.Registry.Add(MuMuCandidate("relative-root", "6.8.0.0"));

        ApplicationResult<MuMuInstallationDiscoveryResult> result = fixture.Discovery.Discover();

        MuMuRejectedInstallationCandidate rejected = Assert.Single(result.Value!.RejectedCandidates);

        Assert.Equal("relative-root", rejected.InstallRoot);
        Assert.Equal(MuMuInstallationRejectionReasons.InstallRootNotAbsolute, rejected.Reason);
    }

    [Fact(DisplayName = "Установка без версии ни в metadata, ни в реестре отклоняется")]
    public void InstallationWithoutVersionIsRejected()
    {
        DiscoveryFixture fixture = new();

        string installRoot = fixture.AddInstallation(
            "without-version",
            registryVersion: null,
            withMetadata: false);

        ApplicationResult<MuMuInstallationDiscoveryResult> result = fixture.Discovery.Discover();

        MuMuRejectedInstallationCandidate rejected = Assert.Single(result.Value!.RejectedCandidates);

        Assert.Equal(installRoot, rejected.InstallRoot);
        Assert.Equal(MuMuInstallationRejectionReasons.VersionUnavailable, rejected.Reason);
    }

    [Fact(DisplayName = "Два документа install metadata на один корень дают ambiguity, а не выбор")]
    public void AmbiguousInstallMetadataIsRejected()
    {
        DiscoveryFixture fixture = new();

        string installRoot = MuMuWindowsTestPaths.Create("ambiguous-metadata");

        fixture.Registry.Add(MuMuCandidate(installRoot, "6.8.0.0"));
        fixture.FileSystem.AddDirectory(installRoot);
        fixture.FileSystem.AddFile(
            MuMuTestInstallation.ControlExecutablePath(installRoot),
            string.Empty);

        fixture.Metadata.Add(MuMuWindowsTestPaths.Create("meta-a", "install_config.json"), MuMuTestInstallation.InstallMetadata(installRoot));
        fixture.Metadata.Add(MuMuWindowsTestPaths.Create("meta-b", "install_config.json"), MuMuTestInstallation.InstallMetadata(installRoot));

        ApplicationResult<MuMuInstallationDiscoveryResult> result = fixture.Discovery.Discover();

        MuMuRejectedInstallationCandidate rejected = Assert.Single(result.Value!.RejectedCandidates);

        Assert.Equal(MuMuInstallationRejectionReasons.InstallMetadataAmbiguous, rejected.Reason);
    }

    [Fact(DisplayName = "Неразобранный документ install metadata сообщается отдельно")]
    public void InvalidInstallMetadataDocumentIsReported()
    {
        DiscoveryFixture fixture = new();

        string metadataPath = MuMuWindowsTestPaths.Create("broken", "install_config.json");
        fixture.Metadata.Add(metadataPath, "{ \"config_version\": \"2\", ");

        ApplicationResult<MuMuInstallationDiscoveryResult> result = fixture.Discovery.Discover();

        Assert.Equal(MuMuInstallationDiscoveryStatus.NotFound, result.Value!.Status);

        MuMuRejectedInstallMetadataDocument rejected = Assert.Single(result.Value!.RejectedMetadataDocuments);

        Assert.Equal(metadataPath, rejected.FilePath);
        Assert.Equal(MuMuInstallMetadataParser.InvalidJsonReason, rejected.Reason);
    }

    [Fact(DisplayName = "Документ install metadata без каталога установки не разбирается")]
    public void InstallMetadataWithoutInstallDirectoryIsInvalid()
    {
        DiscoveryFixture fixture = new();

        fixture.Metadata.Add(
            MuMuWindowsTestPaths.Create("no-install-dir", "install_config.json"),
            "{ \"config_version\": \"2\", \"product\": { \"version\": \"6.8.0.0\" } }");

        ApplicationResult<MuMuInstallationDiscoveryResult> result = fixture.Discovery.Discover();

        Assert.Equal(
            MuMuInstallMetadataParser.MissingInstallDirectoryReason,
            _ = Assert.Single(result.Value!.RejectedMetadataDocuments).Reason);
    }

    [Fact(DisplayName = "Ошибка чтения реестра проецируется в application-level отказ")]
    public void RegistryFailureBecomesApplicationFailure()
    {
        DiscoveryFixture fixture = new();
        fixture.Registry.Failure = new UnauthorizedAccessException("Доступ к разделу реестра запрещён.");

        ApplicationResult<MuMuInstallationDiscoveryResult> result = fixture.Discovery.Discover();

        Assert.True(result.IsFailure);

        ApplicationFailure failure = result.FailureInfo!;

        Assert.Equal(ApplicationFailure.InternalError, failure.Code);
        Assert.Equal(MuMuFailureReasons.RegistryAccessFailed, failure.Details![MuMuFailureDetailKeys.Reason]);
        Assert.Equal(
            nameof(UnauthorizedAccessException),
            failure.Details![MuMuFailureDetailKeys.ExceptionType]);
    }

    [Fact(DisplayName = "Ошибка чтения файловой системы проецируется в application-level отказ")]
    public void FileSystemFailureBecomesApplicationFailure()
    {
        DiscoveryFixture fixture = new();
        fixture.Metadata.Failure = new IOException("Каталог данных приложений недоступен.");

        ApplicationResult<MuMuInstallationDiscoveryResult> result = fixture.Discovery.Discover();

        Assert.True(result.IsFailure);

        ApplicationFailure failure = result.FailureInfo!;

        Assert.Equal(ApplicationFailure.InternalError, failure.Code);
        Assert.Equal(MuMuFailureReasons.FileSystemAccessFailed, failure.Details![MuMuFailureDetailKeys.Reason]);
    }

    [Fact(DisplayName = "Признак семейства определяется издателем и отображаемым именем, а не версией")]
    public void FamilyFilterDoesNotDependOnVersion()
    {
        // Версия в признак семейства не входит: она evidence, а не разрешение на поддержку.
        Assert.True(MuMuInstallationDiscovery.IsMuMuUninstallEntry(MuMuCandidate("any", "99.0.0.0")));
        Assert.True(MuMuInstallationDiscovery.IsMuMuUninstallEntry(MuMuCandidate("any", null)));

        Assert.False(MuMuInstallationDiscovery.IsMuMuUninstallEntry(new MuMuRegistryCandidate
        {
            RegistryKeyPath = "key",
            DisplayName = "Android Device",
            Publisher = "Netease",
            DisplayVersion = "6.8.0.0",
        }));

        Assert.False(MuMuInstallationDiscovery.IsMuMuUninstallEntry(new MuMuRegistryCandidate
        {
            RegistryKeyPath = "key",
            DisplayName = "MuMuPlayer",
            Publisher = "Other Publisher",
            DisplayVersion = "6.8.0.0",
        }));
    }

    private static MuMuRegistryCandidate MuMuCandidate(string installRoot, string? version)
        => MuMuTestInstallation.RegistryCandidate(installRoot, version);

    /// <summary>Собранный набор подменяемых границ обнаружения.</summary>
    private sealed class DiscoveryFixture
    {
        internal DiscoveryFixture()
        {
            Discovery = new MuMuInstallationDiscovery(Registry, Metadata, FileSystem);
        }

        internal FakeMuMuInstallationRegistrySource Registry { get; } = new();

        internal FakeMuMuInstallMetadataSource Metadata { get; } = new();

        internal FakeMuMuFileSystemProbe FileSystem { get; } = new();

        internal MuMuInstallationDiscovery Discovery { get; }

        internal string AddInstallation(
            string leaf,
            string? registryVersion = "6.8.0.0",
            bool withMetadata = true,
            bool withControlExecutable = true)
        {
            return MuMuTestInstallation.Add(
                Registry,
                Metadata,
                FileSystem,
                leaf,
                registryVersion,
                withMetadata,
                withControlExecutable);
        }
    }
}
