using AzurPilot.App;
using AzurPilot.App.OperatorServices;
using AzurPilot.App.OperatorUi;
using AzurPilot.Core.MuMu;
using Microsoft.Extensions.Logging;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Xunit;

namespace AzurPilot.Tests.OperatorUi;

[Trait("Category", "Operator")]
public sealed class OperatorUiSessionTests
{
    [Fact(DisplayName = "Service operation требует explicit target и сохраняет его до завершения")]
    public void OperationGateRequiresAndRetainsExactTarget()
    {
        OperatorUiSession session = new();
        MuMuInstanceId selected = MuMuInstanceId.FromIndex("7");
        MuMuInstanceId other = MuMuInstanceId.FromIndex("3");

        Assert.False(session.TryBeginOperation(OperatorUiAction.AndroidObserve, out MuMuInstanceId? missing));
        Assert.Null(missing);
        Assert.True(session.SelectInstance(selected));
        Assert.True(session.TryBeginOperation(OperatorUiAction.AndroidObserve, out MuMuInstanceId? target));
        Assert.Equal(selected, target);
        Assert.False(session.SelectInstance(other));
        Assert.False(session.TryBeginOperation(OperatorUiAction.Instances, out _));

        session.CompleteOperation();

        Assert.True(session.SelectInstance(other));
        Assert.True(session.TryBeginOperation(OperatorUiAction.AndroidObserve, out target));
        Assert.Equal(other, target);
    }

    [Fact(DisplayName = "Подтверждение mutation связано с action и exact target и потребляется один раз")]
    public void MutationConfirmationIsBoundToActionAndTargetAndConsumedOnce()
    {
        OperatorUiSession session = new();
        MuMuInstanceId selected = MuMuInstanceId.FromIndex("7");
        Assert.True(session.SelectInstance(selected));
        Assert.False(session.TryBeginOperation(OperatorUiAction.MuMuStart, out _));
        Assert.True(session.RequestConfirmation(OperatorUiAction.MuMuStart));
        Assert.Equal(new OperatorUiConfirmation(OperatorUiAction.MuMuStart, selected), session.PendingConfirmation);
        Assert.True(session.ConfirmMutation());
        Assert.False(session.TryBeginOperation(OperatorUiAction.MuMuStop, out _));
        Assert.True(session.TryBeginOperation(OperatorUiAction.MuMuStart, out MuMuInstanceId? target));
        Assert.Equal(selected, target);
        Assert.Null(session.PendingConfirmation);
        Assert.False(session.TryBeginOperation(OperatorUiAction.MuMuStart, out _));

        session.CompleteOperation();
        Assert.True(session.RequestConfirmation(OperatorUiAction.GameStop));
        Assert.True(session.SelectInstance(MuMuInstanceId.FromIndex("3")));
        Assert.Null(session.PendingConfirmation);
        Assert.False(session.ConfirmMutation());
        Assert.False(session.TryBeginOperation(OperatorUiAction.GameStop, out _));
    }

    [Fact(DisplayName = "Подготовка MuMu и Android доступна только после подтверждения exact target")]
    public void EnvironmentPreparationRequiresConfirmationForExactTarget()
    {
        OperatorUiCommand command = OperatorUiCommand.For(OperatorUiAction.EnvironmentPrepare);
        Assert.Equal("/environment-start", command.Command);
        Assert.Contains("Запустить окружение", command.Title);
        Assert.True(command.RequiresConfirmation);
        Assert.True(command.RequiresInstance);
        Assert.True(command.IsOperation);

        OperatorUiSession session = new();
        MuMuInstanceId selected = MuMuInstanceId.FromIndex("7");
        Assert.True(session.SelectInstance(selected));
        Assert.False(session.TryBeginOperation(OperatorUiAction.EnvironmentPrepare, out _));
        Assert.True(session.RequestConfirmation(OperatorUiAction.EnvironmentPrepare));
        Assert.Equal(new OperatorUiConfirmation(OperatorUiAction.EnvironmentPrepare, selected), session.PendingConfirmation);
        Assert.True(session.ConfirmMutation());
        Assert.True(session.TryBeginOperation(OperatorUiAction.EnvironmentPrepare, out MuMuInstanceId? target));
        Assert.Equal(selected, target);
        Assert.Null(session.PendingConfirmation);
        session.CompleteOperation();
    }

    [Fact(DisplayName = "История сохраняет draft, а сообщения ограничены и очищают terminal controls")]
    public void HistoryPreservesDraftAndMessagesAreBoundedAndSanitized()
    {
        OperatorUiSession session = new();
        session.RememberCommand("/help");
        session.RememberCommand("/instances");

        Assert.Equal("/instances", session.NavigateHistory(-1, "draft text"));
        Assert.Equal("/help", session.NavigateHistory(-1, "/instances"));
        Assert.Equal("/instances", session.NavigateHistory(1, "/help"));
        Assert.Equal("draft text", session.NavigateHistory(1, "/instances"));

        for (int index = 0; index < 130; index++)
        {
            session.AddMessage($"title-{index}", index == 129 ? "русский\u001b[31m\tтекст" : $"message-{index}",
                index == 129 ? OperatorUiMessageKind.Error : OperatorUiMessageKind.Information,
                index == 129 ? "код\u001b[31m: отказ" : null);
        }

        IReadOnlyList<OperatorUiMessage> messages = session.SnapshotMessages();
        Assert.Equal(128, messages.Count);
        Assert.Equal("title-2", messages[0].Title);
        Assert.Equal("русский[31mтекст", messages[^1].Text);
        Assert.Equal(OperatorUiMessageKind.Error, messages[^1].Kind);
        Assert.Equal("код[31m: отказ", messages[^1].Details);
        Assert.DoesNotContain('\u001b', string.Join('\n', messages.Select(static message => message.Text)));
        Assert.DoesNotContain('\u001b', string.Join('\n', messages.Select(static message => message.Details)));

        session.AddMessage("bounded", "кратко", details: new string('т', 9000));
        Assert.Equal(8192, session.SnapshotMessages()[^1].Details!.Length);
        Assert.Contains(OperatorUiCommand.All, command => command.Action == OperatorUiAction.Details
            && command.Command == "/details");
    }

    [Theory(DisplayName = "Wheel переводится в независимый scroll delta истории и логов")]
    [InlineData(MouseFlags.WheeledUp, -3)]
    [InlineData(MouseFlags.WheeledDown, 3)]
    public void MouseWheelProducesThreeRowDelta(MouseFlags flags, int expectedDelta)
    {
        Assert.True(OperatorUiScrollMath.TryGetWheelDelta(flags, out int delta));
        Assert.Equal(expectedDelta, delta);
    }

    [Theory(DisplayName = "Нев-wheel мышь не меняет scroll position")]
    [InlineData(MouseFlags.None)]
    [InlineData(MouseFlags.LeftButtonClicked)]
    public void NonWheelMouseHasNoScrollDelta(MouseFlags flags)
    {
        Assert.False(OperatorUiScrollMath.TryGetWheelDelta(flags, out int delta));
        Assert.Equal(0, delta);
    }

    [Theory(DisplayName = "Scroll position ограничен содержимым pane")]
    [InlineData(-10, 100, 20, 0)]
    [InlineData(200, 100, 20, 80)]
    [InlineData(10, 5, 20, 0)]
    public void ScrollPositionIsClamped(int requestedY, int contentRows, int viewportRows, int expectedY)
    {
        Assert.Equal(expectedY, OperatorUiScrollMath.ClampViewportY(requestedY, contentRows, viewportRows));
    }

    [Fact(DisplayName = "Wheel маршрутизируется к видимой панели истории под указателем")]
    public void MouseWheelRoutesToVisibleScrollablePaneUnderPointer()
    {
        using View root = new();
        View history = new();
        View historyLabel = new();
        View palette = new();
        _ = root.Add(history);
        _ = root.Add(palette);
        _ = history.Add(historyLabel);

        Assert.Same(history, AzurPilotOperatorUi.FindScrollablePane(historyLabel, history));
        Assert.Null(AzurPilotOperatorUi.FindScrollablePane(palette, history));

        history.Visible = false;
        Assert.Null(AzurPilotOperatorUi.FindScrollablePane(historyLabel, history));
    }

    [Theory(DisplayName = "Footer помещает приоритетные клавиши и помощь в доступную ширину")]
    [InlineData(118)]
    [InlineData(98)]
    [InlineData(78)]
    [InlineData(58)]
    public void FooterFitsViewportAndKeepsImportantActions(int width)
    {
        string footer = AzurPilotOperatorUi.BuildFooterText(width);
        int cellWidth = footer.EnumerateRunes().Sum(static rune => Math.Max(0, rune.GetColumns()));

        Assert.InRange(cellWidth, 1, width);
        Assert.Contains("F4 отмена", footer);
        Assert.Contains("Ctrl+C выход", footer);
        Assert.Contains("F2", footer);
    }

    [Fact(DisplayName = "Log buffer ограничен, однострочен и связывает запись с activity")]
    public void LogBufferIsBoundedSanitizedAndCorrelated()
    {
        using AzurPilotOperatorLogBuffer buffer = new();
        using ILoggerFactory loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(buffer));
        ILogger logger = loggerFactory.CreateLogger("operator.test");
        Action<ILogger, int, Exception?> logCorrelated = LoggerMessage.Define<int>(
            LogLevel.Information,
            new EventId(1, "Correlated"),
            "русский\r\nтекст {Index}");
        Action<ILogger, int, Exception?> logEntry = LoggerMessage.Define<int>(
            LogLevel.Information,
            new EventId(2, "Entry"),
            "entry {Index}");

        string correlationId;
        using (AzurPilotOperation operation = AzurPilotOperation.Start("azurpilot.operator.test"))
        {
            correlationId = operation.CorrelationId;
            logCorrelated(logger, 0, null);
        }

        AzurPilotOperatorLogEntry correlatedEntry = Assert.Single(buffer.Snapshot());

        for (int index = 1; index < 300; index++)
        {
            logEntry(logger, index, null);
        }

        IReadOnlyList<AzurPilotOperatorLogEntry> entries = buffer.Snapshot();
        Assert.Equal(256, entries.Count);
        Assert.Equal(45, entries[0].Sequence);
        Assert.Equal(300, entries[^1].Sequence);
        Assert.Equal(correlationId, correlatedEntry.TraceId);
        Assert.NotNull(correlatedEntry.SpanId);
        Assert.DoesNotContain('\r', entries[0].Message);
        Assert.DoesNotContain('\n', entries[0].Message);
        Assert.Equal(entries.Count, buffer.SnapshotLines().Count);
    }

    [Fact(DisplayName = "Краткая строка журнала отделяет источник от подробных полей")]
    public void LogSummaryKeepsReadableFieldsAndDetailsKeepCorrelation()
    {
        AzurPilotOperatorLogEntry entry = new(
            Sequence: 7,
            TimestampUtc: new DateTimeOffset(2026, 10, 11, 12, 34, 56, TimeSpan.Zero),
            Level: LogLevel.Warning,
            Category: "AzurPilot.Core.Android.Orchestration.AndroidReadinessService",
            EventId: 3002,
            EventName: "EndpointResolutionFailed",
            TraceId: "0123456789abcdef",
            SpanId: "abcdef0123456789",
            Message: "ADB endpoint не разрешён для mumu:1",
            Properties: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["instance_id"] = "mumu:1",
                ["reason"] = "host_missing",
            });

        string header = OperatorUiLogPresentation.FormatSummaryHeader(entry);
        string details = string.Join('\n', OperatorUiLogPresentation.FormatTechnicalDetails(entry));

        Assert.Contains("[ВНИМАНИЕ]", header);
        Assert.Contains("Orchestration.AndroidReadinessService", header);
        Assert.DoesNotContain("0123456789abcdef", header);
        Assert.Contains("3002 EndpointResolutionFailed", details);
        Assert.Contains("0123456789abcdef", details);
        Assert.Contains("instance_id: mumu:1", details);
        Assert.Contains("reason: host_missing", details);
    }
}
