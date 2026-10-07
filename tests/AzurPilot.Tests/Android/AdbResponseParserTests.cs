using AzurPilot.Core.Android;
using AzurPilot.Windows;
using AzurPilot.Windows.Android;
using AzurPilot.Windows.Processes;
using Xunit;

namespace AzurPilot.Tests.Android;

/// <summary>
/// Доказательства fail-closed разбора вывода ADB: доказанное значение, недоказанное наблюдение и
/// доказанное отсутствие — разные результаты.
/// </summary>
/// <remarks>
/// <para>
/// Проверки идут через настоящий <see cref="AdbResponseParser"/> и настоящие контрактные типы, поэтому
/// доказывается observable contract разбора, а не отдельная реализация: недостижимый target никогда не
/// выдаётся за отсутствие пакета, пустой список процессов не подставляется вместо недоказанного, а
/// нераспознанная форма ответа даёт <c>Unknown</c>, а не догадку.
/// </para>
/// <para>
/// Payload-ы соответствуют документированным формам ответа ADB; синтетические варианты отличаются от них
/// ровно одним признаком формы и помечены в комментариях.
/// </para>
/// </remarks>
[Trait("Category", "Android")]
public sealed class AdbResponseParserTests
{
    private static readonly AndroidEndpoint Endpoint = new("127.0.0.1", 16416);

    private static readonly AndroidPackageId Package = new(AzurLaneProduct.Package);

    private static readonly string AdbExecutablePath =
        AndroidInstallationLayout.GetAdbExecutablePath(AndroidTestPaths.Create("install"));

    // --- Наблюдение transport ---

    [Fact(DisplayName = "Состояние transport берётся только из распознанной формы ответа")]
    public void TransportStateComesOnlyFromRecognizedForm()
    {
        Assert.Equal(
            AndroidTransportState.Device,
            AdbResponseParser.ParseTransport(Outcome(0, "device\n"), Endpoint).State);
        Assert.Equal(
            AndroidTransportState.Offline,
            AdbResponseParser.ParseTransport(Outcome(0, "offline\n"), Endpoint).State);

        // Синтетика: распознанной формой является ровно device и offline.
        Assert.Equal(
            AndroidTransportState.Unknown,
            AdbResponseParser.ParseTransport(Outcome(0, "unknown\n"), Endpoint).State);
        Assert.Equal(
            AndroidTransportState.Unknown,
            AdbResponseParser.ParseTransport(Outcome(0, string.Empty), Endpoint).State);
    }

    [Fact(DisplayName = "Отсутствие target-а доказывается только документированной формой ответа ADB")]
    public void AbsentTransportRequiresDocumentedForm()
    {
        AndroidTransportObservation documented = AdbResponseParser.ParseTransport(
            Outcome(1, string.Empty, $"error: device '{Endpoint}' not found"),
            Endpoint);

        Assert.Equal(AndroidTransportState.Absent, documented.State);

        // Синтетика: сообщение про другое устройство и общее «нет устройств» доказательством
        // отсутствия запрошенного target-а не являются.
        Assert.Equal(
            AndroidTransportState.Unknown,
            AdbResponseParser.ParseTransport(
                Outcome(1, string.Empty, "error: device '127.0.0.2:16416' not found"),
                Endpoint).State);
        Assert.Equal(
            AndroidTransportState.Unknown,
            AdbResponseParser.ParseTransport(
                Outcome(1, string.Empty, "error: no devices/emulators found"),
                Endpoint).State);
    }

    [Fact(DisplayName = "Усечённый вывод transport полноценным ответом не считается")]
    public void TruncatedTransportOutputIsNotAProvenObservation()
        => Assert.Equal(
            AndroidTransportState.Unknown,
            AdbResponseParser.ParseTransport(
                Outcome(0, "device\n", truncated: true),
                Endpoint).State);

    // --- Присутствие пакета ---

    [Fact(DisplayName = "Установка пакета доказывается строкой package:")]
    public void InstalledPackageIsProvenByPackagePath()
        => Assert.Equal(
            AndroidPackagePresence.Installed,
            AdbResponseParser.ParsePackagePresence(
                Outcome(0, "package:/data/app/~~abc==/com.YoStarEN.AzurLane/base.apk\n"),
                Endpoint));

    [Fact(DisplayName = "Отсутствие пакета доказывается пустым ответом с ненулевым кодом выхода")]
    public void AbsentPackageIsProvenByEmptyAnswer()
        => Assert.Equal(
            AndroidPackagePresence.Absent,
            AdbResponseParser.ParsePackagePresence(Outcome(1, string.Empty), Endpoint));

    [Fact(DisplayName = "«Не удалось спросить» даёт QueryFailed, а не доказанное отсутствие")]
    public void UnprovenPackageQueryIsNotAbsence()
    {
        AndroidPackagePresence unreachableTarget = AdbResponseParser.ParsePackagePresence(
            Outcome(1, string.Empty, $"error: device '{Endpoint}' not found"),
            Endpoint);
        AndroidPackagePresence failedQuery = AdbResponseParser.ParsePackagePresence(
            Outcome(1, "Error: java.lang.IllegalArgumentException\n"),
            Endpoint);
        AndroidPackagePresence unrecognizedSuccess = AdbResponseParser.ParsePackagePresence(
            Outcome(0, string.Empty),
            Endpoint);
        AndroidPackagePresence truncated = AdbResponseParser.ParsePackagePresence(
            Outcome(0, "package:/data/app/base.apk\n", truncated: true),
            Endpoint);

        Assert.Equal(AndroidPackagePresence.QueryFailed, unreachableTarget);
        Assert.Equal(AndroidPackagePresence.QueryFailed, failedQuery);
        Assert.Equal(AndroidPackagePresence.QueryFailed, unrecognizedSuccess);
        Assert.Equal(AndroidPackagePresence.QueryFailed, truncated);

        // Три случая различимы: «не доказано» — не «отсутствует».
        Assert.NotEqual(AndroidPackagePresence.Absent, unreachableTarget);
        Assert.NotEqual(AndroidPackagePresence.Absent, failedQuery);
    }

    [Fact(DisplayName = "Сообщение об ошибке в stderr не выдаётся за доказанное отсутствие пакета")]
    public void PackageErrorOnStandardErrorIsNotAbsence()
    {
        // Пустой stdout с ненулевым кодом выхода доказывает отсутствие пакета только тогда, когда stderr
        // тоже пуст: «error: device offline» — это «не удалось спросить», а не «пакета нет».
        AndroidPackagePresence offline = AdbResponseParser.ParsePackagePresence(
            Outcome(1, string.Empty, "error: device offline"),
            Endpoint);

        Assert.Equal(AndroidPackagePresence.QueryFailed, offline);
        Assert.NotEqual(AndroidPackagePresence.Absent, offline);
    }

    // --- Разрешение launcher-компонента ---

    [Fact(DisplayName = "Launcher разрешается ровно одним компонентом запрошенного пакета")]
    public void LauncherResolvesWithSingleComponent()
    {
        AndroidLauncherResolution resolution = AdbResponseParser.ParseLauncherResolution(
            Outcome(0, AzurLaneProduct.Package + "/com.manjuu.azurlane.PrePermissionActivity\n"),
            Endpoint,
            Package);

        Assert.Equal(AndroidLauncherResolutionStatus.Resolved, resolution.Status);
        Assert.Equal(1, resolution.MatchingComponentCount);
        Assert.NotNull(resolution.Component);
        Assert.Equal(Package, resolution.Component!.Package);
        Assert.Equal(
            AzurLaneProduct.Package + "/com.manjuu.azurlane.PrePermissionActivity",
            resolution.Component.Flattened);
    }

    [Fact(DisplayName = "Отсутствие launcher-компонента доказывается пустым ответом и сообщением провайдера")]
    public void MissingLauncherIsProvenByEmptyAnswer()
    {
        AndroidLauncherResolution empty = AdbResponseParser.ParseLauncherResolution(
            Outcome(0, string.Empty),
            Endpoint,
            Package);
        AndroidLauncherResolution noActivities = AdbResponseParser.ParseLauncherResolution(
            Outcome(0, "No activities found\n"),
            Endpoint,
            Package);

        Assert.Equal(AndroidLauncherResolutionStatus.Missing, empty.Status);
        Assert.Equal(AndroidLauncherResolutionStatus.Missing, noActivities.Status);
        Assert.Null(empty.Component);
        Assert.Equal(0, empty.MatchingComponentCount);
    }

    [Fact(DisplayName = "Неоднозначность launcher-а не разрешается выбором первого компонента")]
    public void AmbiguousLauncherIsNotResolved()
    {
        AndroidLauncherResolution resolution = AdbResponseParser.ParseLauncherResolution(
            Outcome(
                0,
                AzurLaneProduct.Package + "/com.manjuu.azurlane.FirstActivity\n"
                    + AzurLaneProduct.Package + "/com.manjuu.azurlane.SecondActivity\n"),
            Endpoint,
            Package);

        Assert.Equal(AndroidLauncherResolutionStatus.Ambiguous, resolution.Status);
        Assert.Null(resolution.Component);
        Assert.Equal(2, resolution.MatchingComponentCount);
    }

    [Fact(DisplayName = "Недоказанное разрешение launcher-а отличается от отсутствия компонента")]
    public void UnprovenLauncherIsNotMissing()
    {
        AndroidLauncherResolution failedExit = AdbResponseParser.ParseLauncherResolution(
            Outcome(1, string.Empty),
            Endpoint,
            Package);
        AndroidLauncherResolution otherPackage = AdbResponseParser.ParseLauncherResolution(
            Outcome(0, "com.other.app/com.other.app.MainActivity\n"),
            Endpoint,
            Package);
        AndroidLauncherResolution unrecognizedLine = AdbResponseParser.ParseLauncherResolution(
            Outcome(0, "not-a-component\n"),
            Endpoint,
            Package);
        AndroidLauncherResolution truncated = AdbResponseParser.ParseLauncherResolution(
            Outcome(0, AzurLaneProduct.Package + "/com.manjuu.azurlane.FirstActivity\n", truncated: true),
            Endpoint,
            Package);

        Assert.Equal(AndroidLauncherResolutionStatus.QueryFailed, failedExit.Status);
        Assert.Equal(AndroidLauncherResolutionStatus.QueryFailed, otherPackage.Status);
        Assert.Equal(AndroidLauncherResolutionStatus.QueryFailed, unrecognizedLine.Status);
        Assert.Equal(AndroidLauncherResolutionStatus.QueryFailed, truncated.Status);

        Assert.NotEqual(AndroidLauncherResolutionStatus.Missing, failedExit.Status);
        Assert.Null(otherPackage.Component);
    }

    // --- Процессы пакета ---

    [Fact(DisplayName = "Процессы наблюдаются только по точному пакету и его подпроцессам")]
    public void ProcessesAreMatchedByExactPackage()
    {
        AndroidProcessObservation single = AdbResponseParser.ParseProcessObservation(
            Outcome(0, "PID NAME\n4242 com.YoStarEN.AzurLane\n5151 com.other.app\n"),
            Package);

        int[] expectedSingle = [4242];
        int[] expectedMultiple = [4242, 4243];

        Assert.Equal(1, single.ProcessCount);
        Assert.Equal(expectedSingle, single.ProcessIds);

        AndroidProcessObservation multiple = AdbResponseParser.ParseProcessObservation(
            Outcome(0, "PID NAME\n4242 com.YoStarEN.AzurLane\n4243 com.YoStarEN.AzurLane:remote\n"),
            Package);

        Assert.Equal(2, multiple.ProcessCount);
        Assert.Equal(expectedMultiple, multiple.ProcessIds);
    }

    [Fact(DisplayName = "Доказанное отсутствие процессов — пустой список, а не null")]
    public void ProvenAbsenceOfProcessesIsEmptyList()
    {
        AndroidProcessObservation observation = AdbResponseParser.ParseProcessObservation(
            Outcome(0, "PID NAME\n5151 com.other.app\n"),
            Package);

        Assert.Equal(0, observation.ProcessCount);
        Assert.NotNull(observation.ProcessIds);
        Assert.Empty(observation.ProcessIds!);
    }

    [Fact(DisplayName = "Недоказанное наблюдение процессов даёт null, а не пустой список")]
    public void UnprovenProcessObservationIsNull()
    {
        AndroidProcessObservation failedExit = AdbResponseParser.ParseProcessObservation(
            Outcome(1, string.Empty),
            Package);
        AndroidProcessObservation emptyOutput = AdbResponseParser.ParseProcessObservation(
            Outcome(0, string.Empty),
            Package);
        AndroidProcessObservation unrecognizedLine = AdbResponseParser.ParseProcessObservation(
            Outcome(0, "PID NAME\nnot-a-pid com.YoStarEN.AzurLane\n"),
            Package);
        AndroidProcessObservation truncated = AdbResponseParser.ParseProcessObservation(
            Outcome(0, "PID NAME\n4242 com.YoStarEN.AzurLane\n", truncated: true),
            Package);

        Assert.Null(failedExit.ProcessIds);
        Assert.Null(emptyOutput.ProcessIds);
        Assert.Null(unrecognizedLine.ProcessIds);
        Assert.Null(truncated.ProcessIds);

        // «Не удалось спросить» не равно «процессов нет»: пустой список доказан только ответом.
        int[] provenEmpty = [];

        Assert.NotEqual(provenEmpty, failedExit.ProcessIds);
    }

    // --- Передний план ---

    [Fact(DisplayName = "Передний план сравнивается по exact package, а не по компоненту")]
    public void ForegroundComparesExactPackage()
    {
        // Синтетика по реальному наблюдению: после запуска launcher-компонента на переднем плане
        // оказывается другая activity того же пакета.
        AndroidForegroundObservation otherActivity = AdbResponseParser.ParseForeground(
            Outcome(
                0,
                "  mCurrentFocus=Window{1 u0 "
                    + AzurLaneProduct.Package
                    + "/com.manjuu.azurlane.MainActivity}\n"),
            AzurLaneProduct.Package);

        Assert.Equal(AndroidForegroundStatus.Foreground, otherActivity.Status);
        Assert.Equal(AzurLaneProduct.Package, otherActivity.Component!.Package.ToString());
        Assert.NotEqual(
            AzurLaneProduct.Package + "/com.manjuu.azurlane.PrePermissionActivity",
            otherActivity.Component.Flattened);
    }

    [Fact(DisplayName = "Чужой пакет на переднем плане даёт Other с наблюдённым компонентом")]
    public void OtherForegroundReportsObservedComponent()
    {
        AndroidForegroundObservation observation = AdbResponseParser.ParseForeground(
            Outcome(0, "  mCurrentFocus=Window{1 u0 app.lawnchair/app.lawnchair.LawnchairLauncher}\n"),
            AzurLaneProduct.Package);

        Assert.Equal(AndroidForegroundStatus.Other, observation.Status);
        Assert.Equal("app.lawnchair", observation.Component!.Package.ToString());
    }

    [Fact(DisplayName = "Нераспознанная форма ответа даёт Unknown без компонента, а не догадку")]
    public void UnrecognizedForegroundIsUnknown()
    {
        AndroidForegroundObservation noMarker = AdbResponseParser.ParseForeground(
            Outcome(0, "Display: mDisplayId=0\n"),
            AzurLaneProduct.Package);
        AndroidForegroundObservation nullFocus = AdbResponseParser.ParseForeground(
            Outcome(0, "  mCurrentFocus=null\n"),
            AzurLaneProduct.Package);
        AndroidForegroundObservation failedExit = AdbResponseParser.ParseForeground(
            Outcome(1, "  mCurrentFocus=Window{1 u0 app.lawnchair/app.lawnchair.LawnchairLauncher}\n"),
            AzurLaneProduct.Package);
        AndroidForegroundObservation truncated = AdbResponseParser.ParseForeground(
            Outcome(
                0,
                "  mCurrentFocus=Window{1 u0 " + AzurLaneProduct.Package + "/com.manjuu.azurlane.MainActivity}\n",
                truncated: true),
            AzurLaneProduct.Package);

        Assert.Equal(AndroidForegroundStatus.Unknown, noMarker.Status);
        Assert.Equal(AndroidForegroundStatus.Unknown, nullFocus.Status);
        Assert.Equal(AndroidForegroundStatus.Unknown, failedExit.Status);
        Assert.Equal(AndroidForegroundStatus.Unknown, truncated.Status);
        Assert.Null(nullFocus.Component);
        Assert.Null(failedExit.Component);
    }

    [Fact(DisplayName = "Резервный маркер mFocusedApp наблюдается так же, как mCurrentFocus")]
    public void FocusedAppMarkerIsObserved()
    {
        AndroidForegroundObservation observation = AdbResponseParser.ParseForeground(
            Outcome(
                0,
                "  mFocusedApp=AppWindowToken{1 u0 "
                    + AzurLaneProduct.Package
                    + "/com.manjuu.azurlane.MainActivity}\n"),
            AzurLaneProduct.Package);

        Assert.Equal(AndroidForegroundStatus.Foreground, observation.Status);
    }

    [Fact(DisplayName = "Значение соседнего поля не приписывается маркеру переднего плана")]
    public void FocusMarkerDoesNotBorrowValueFromAnotherLine()
    {
        // mCurrentFocus=null означает недоказанный передний план. Компонент из следующего поля того же
        // дампа не относится к маркеру переднего плана и не должен выдавать игру за находящуюся на нём.
        AndroidForegroundObservation observation = AdbResponseParser.ParseForeground(
            Outcome(
                0,
                "  mCurrentFocus=null\n  mTopFullscreenOpaqueWindowState=Window{1 u0 "
                    + AzurLaneProduct.Package
                    + "/com.manjuu.azurlane.MainActivity}\n"),
            AzurLaneProduct.Package);

        Assert.Equal(AndroidForegroundStatus.Unknown, observation.Status);
        Assert.Null(observation.Component);
    }

    [Fact(DisplayName = "Нераспознанный mCurrentFocus не подменяется резервным mFocusedApp")]
    public void UnrecognizedPrimaryMarkerIsNotReplacedByFallbackMarker()
    {
        // mCurrentFocus присутствует, но его значение не распознано, а резервный маркер называет пакет игры.
        // Значение резервного маркера не выдаётся за доказанный передний план: ответ с нераспознанным
        // первичным маркером означает «не доказано», а не «игра на переднем плане».
        AndroidForegroundObservation observation = AdbResponseParser.ParseForeground(
            Outcome(
                0,
                "  mCurrentFocus=null\n  mFocusedApp=AppWindowToken{1 u0 "
                    + AzurLaneProduct.Package
                    + "/com.manjuu.azurlane.MainActivity}\n"),
            AzurLaneProduct.Package);

        Assert.Equal(AndroidForegroundStatus.Unknown, observation.Status);
        Assert.Null(observation.Component);
    }

    // --- Свойства Android ---

    [Fact(DisplayName = "Целочисленное свойство читается только из распознанного ответа")]
    public void IntegerPropertyRequiresRecognizedAnswer()
    {
        Assert.Equal(1, AdbResponseParser.ParsePropertyInteger(Outcome(0, "1\n")));
        Assert.Equal(0, AdbResponseParser.ParsePropertyInteger(Outcome(0, "0")));

        // Отсутствие наблюдённого значения — не ноль и не «не готово».
        Assert.Null(AdbResponseParser.ParsePropertyInteger(Outcome(0, "not-a-number\n")));
        Assert.Null(AdbResponseParser.ParsePropertyInteger(Outcome(1, "1\n")));
        Assert.Null(AdbResponseParser.ParsePropertyInteger(Outcome(0, "1\n", truncated: true)));
    }

    [Fact(DisplayName = "Текстовое свойство читается только из распознанного ответа")]
    public void TextPropertyRequiresRecognizedAnswer()
    {
        Assert.Equal("15.0", AdbResponseParser.ParsePropertyText(Outcome(0, "15.0\n")));

        Assert.Null(AdbResponseParser.ParsePropertyText(Outcome(1, "15.0\n")));
        Assert.Null(AdbResponseParser.ParsePropertyText(Outcome(0, "   \n")));
        Assert.Null(AdbResponseParser.ParsePropertyText(Outcome(0, "15.0\n", truncated: true)));
    }

    [Fact(DisplayName = "Evidence версии ADB не переносит путь установки в диагностику")]
    public void AdbVersionEvidenceExcludesInstallPath()
    {
        string output = "Android Debug Bridge version 1.0.41\n"
            + "Version 36.0.0-13206524\n"
            + "Installed as " + AdbExecutablePath + "\n";

        string evidence = Assert.IsType<string>(
            AdbResponseParser.ParseAdbVersionEvidence(Outcome(0, output)));

        Assert.Contains("Android Debug Bridge version 1.0.41", evidence, StringComparison.Ordinal);
        Assert.Contains("Version 36.0.0-13206524", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("Installed as", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain(AdbExecutablePath, evidence, StringComparison.Ordinal);
        AssertBounded(evidence);
    }

    [Fact(DisplayName = "Нераспознанная форма ответа version не подтверждает executable")]
    public void UnrecognizedVersionResponseIsNotEvidence()
    {
        Assert.Null(AdbResponseParser.ParseAdbVersionEvidence(Outcome(0, "some other tool output\n")));
        Assert.Null(AdbResponseParser.ParseAdbVersionEvidence(Outcome(1, "Android Debug Bridge version 1.0.41\n")));
        Assert.Null(
            AdbResponseParser.ParseAdbVersionEvidence(
                Outcome(0, "Android Debug Bridge version 1.0.41\n", truncated: true)));
    }

    // --- Результат однократного вызова ---

    [Fact(DisplayName = "Результат вызова сохраняет код выхода и ограничивает вывод")]
    public void CommandOutcomeKeepsExitCodeAndBoundsOutput()
    {
        AndroidCommandOutcome outcome = AdbResponseParser.ToCommandOutcome(
            Outcome(7, new string('x', BoundedDiagnosticText.MaxLength * 4), "error text"));

        Assert.Equal(7, outcome.ExitCode);
        AssertBounded(outcome.BoundedOutput);

        AndroidCommandOutcome multiline = AdbResponseParser.ToCommandOutcome(Outcome(0, "line one\nline two\n"));

        Assert.DoesNotContain("\n", multiline.BoundedOutput, StringComparison.Ordinal);
    }

    private static void AssertBounded(string evidence)
    {
        Assert.False(string.IsNullOrEmpty(evidence));
        Assert.True(
            evidence.Length <= BoundedDiagnosticText.MaxLength,
            $"Evidence длиной {evidence.Length} превышает bounded предел.");
        Assert.DoesNotContain("\n", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", evidence, StringComparison.Ordinal);
    }

    private static WindowsProcessOutcome Outcome(
        int exitCode,
        string standardOutput,
        string standardError = "",
        bool truncated = false)
        => new()
        {
            ExitCode = exitCode,
            StandardOutput = standardOutput,
            StandardError = standardError,
            Duration = TimeSpan.FromMilliseconds(11),
            StandardOutputTruncated = truncated,
            StandardErrorTruncated = false,
        };
}
