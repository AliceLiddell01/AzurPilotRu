using AzurPilot.App;
using AzurPilot.App.OperatorServices;
using AzurPilot.App.OperatorUi;
using AzurPilot.Core.MuMu;
using Microsoft.Extensions.Logging;
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
            session.AddMessage($"title-{index}", index == 129 ? "русский\u001b[31m\tтекст" : $"message-{index}");
        }

        IReadOnlyList<OperatorUiMessage> messages = session.SnapshotMessages();
        Assert.Equal(128, messages.Count);
        Assert.Equal("title-2", messages[0].Title);
        Assert.Equal("русский[31mтекст", messages[^1].Text);
        Assert.DoesNotContain('\u001b', string.Join('\n', messages.Select(static message => message.Text)));
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
}
