using AzurPilot.App.OperatorServices;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows;

namespace AzurPilot.App.Agent;

internal static class AgentRunner
{
    // Делегат подменяет границу сборки хоста для детерминированных тестов; приложение не принимает его через CLI/env.
    internal static async Task<AgentEnvelope> RunAsync(string[] args,
        Func<Func<AzurPilotOperatorService, CancellationToken, Task<AgentPayload>>, CancellationToken, Task<AgentPayload>>? runHost = null, CancellationToken cancellationToken = default)
    {
        string correlationId = Guid.NewGuid().ToString("N");
        string? operation = null;
        string? requestedTarget = null;
        AgentContext? context = null;
        AgentPayload payload;
        using CancellationTokenSource deadline = new();
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            ApplicationResult<AgentRequest> parsed = AgentRequest.Parse(args);
            if (parsed.IsFailure)
            {
                return AgentJson.Envelope(correlationId, null, null, new(null, parsed.FailureInfo));
            }

            AgentRequest request = parsed.Value!;
            operation = request.Command.Name;
            requestedTarget = request.Target;
            deadline.CancelAfter(request.TimeoutMilliseconds);
            using AzurPilotOperation activity = AzurPilotOperation.Start("azurpilot.agent." + operation);
            correlationId = activity.CorrelationId;
            linked.Token.ThrowIfCancellationRequested();
            if (operation == "discover")
            {
                payload = await request.Command.Handler(new AgentContext(null), linked.Token).ConfigureAwait(false);
            }
            else
            {
                runHost ??= (run, token) => AzurPilotHost.RunAgentAsync(run, request.ConfigurationPath, token);
                payload = await runHost(async (service, token) =>
                {
                    context = new AgentContext(service);
                    token.ThrowIfCancellationRequested();
                    if (request.Command.Target)
                    {
                        string selection = request.Target ?? service.Configuration.Configuration.MuMu.Instance;
                        MuMuInstanceSelection targetSelection = MuMuInstanceValue.IsAuto(selection)
                            ? MuMuInstanceSelection.Auto()
                            : MuMuInstanceSelection.Explicit(MuMuInstanceId.FromIndex(selection[MuMuInstanceValue.ProviderPrefix.Length..]));
                        ApplicationResult<MuMuInstanceId> resolved = service.ResolveTarget(targetSelection);
                        if (resolved.IsFailure)
                        {
                            return new AgentPayload(null, resolved.FailureInfo);
                        }

                        context.Target = resolved.Value;
                    }

                    token.ThrowIfCancellationRequested();
                    AgentPayload completed = await request.Command.Handler(context, token).ConfigureAwait(false);
                    context.Completed = completed;
                    return completed;
                }, linked.Token).ConfigureAwait(false);
            }

            // Синхронные внешние probes имеют собственный bounded timeout. Если общий deadline наступил
            // во время такого probe, его результат сохраняется, но запрос не объявляется своевременным.
            if (linked.IsCancellationRequested && payload.Failure?.Code is null or ApplicationFailure.OperationCancelled)
            {
                payload = payload with
                {
                    Failure = CancellationFailure(deadline, cancellationToken) with { Details = payload.Failure?.Details },
                };
            }
        }
        catch (Exception exception)
        {
            ApplicationFailure failure = exception is OperationCanceledException && linked.IsCancellationRequested
                ? CancellationFailure(deadline, cancellationToken) : NativeBoundaryFailureMapper.Map(exception);
            payload = context?.Interrupted(failure) ?? new(null, failure);
        }

        return AgentJson.Envelope(correlationId, operation, context?.Target?.ToString(), payload, requestedTarget);
    }

    private static ApplicationFailure CancellationFailure(CancellationTokenSource deadline, CancellationToken client)
    {
        return new()
        {
            Code = !client.IsCancellationRequested && deadline.IsCancellationRequested
            ? ApplicationFailure.AgentDeadlineExceeded : ApplicationFailure.OperationCancelled,
            Message = "Ожидание завершено. Отмена не означает откат изменений.",
        };
    }

    internal static async Task<int> RunProcessAsync(string[] args)
    {
        // Сохраняем поток stdout до подавления стороннего консольного вывода. Записываем ответ один раз,
        // после остановки и освобождения ресурсов хоста. При сборке хоста структурированные логи направляются в stderr.
        using Stream output = Console.OpenStandardOutput();
        Console.SetOut(TextWriter.Null);
        using CancellationTokenSource cancellation = new();
        void handler(object? _, ConsoleCancelEventArgs e) { e.Cancel = true; cancellation.Cancel(); }

        Console.CancelKeyPress += handler;
        try
        {
            AgentEnvelope envelope = await RunAsync(args, cancellationToken: cancellation.Token).ConfigureAwait(false);
            byte[] json;
            try
            {
                json = AgentJson.Serialize(ref envelope);
            }
            catch (Exception exception)
            {
                envelope = AgentJson.Envelope(envelope.CorrelationId, envelope.Operation, envelope.Target,
                    new(null, NativeBoundaryFailureMapper.Map(exception), envelope.Effect), envelope.RequestedTarget);
                json = AgentJson.Serialize(ref envelope);
            }

            await output.WriteAsync(json, CancellationToken.None).ConfigureAwait(false);
            await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            return envelope.ExitCode;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }
}
