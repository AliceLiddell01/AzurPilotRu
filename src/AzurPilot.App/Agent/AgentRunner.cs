using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Windows;
using HostRunner = System.Func<System.Func<AzurPilot.App.OperatorServices.AzurPilotOperatorService, System.Threading.CancellationToken,
    System.Threading.Tasks.Task<AzurPilot.App.Agent.AgentPayload>>, System.Threading.CancellationToken,
    System.Threading.Tasks.Task<AzurPilot.App.Agent.AgentPayload>>;

namespace AzurPilot.App.Agent;

/// <summary>Envelope вызова и, только при явном Debug Mode, итог записи отчёта.</summary>
internal sealed record AgentRunResult(AgentEnvelope Envelope, AgentDebugCompletion? Debug);

internal static class AgentRunner
{
    // Делегат подменяет границу сборки хоста для детерминированных тестов; приложение не принимает его через CLI/env.
    internal static async Task<AgentEnvelope> RunAsync(string[] args, HostRunner? runHost = null,
        CancellationToken cancellationToken = default)
    {
        return (await RunWithDebugAsync(args, runHost is null ? null : _ => runHost, null, cancellationToken).ConfigureAwait(false)).Envelope;
    }

    // Тестовая граница получает debug-прогон до сборки хоста; workingDirectory заменяет только поиск корня checkout.
    internal static async Task<AgentRunResult> RunWithDebugAsync(string[] args, Func<AgentDebugRun?, HostRunner>? hostFactory,
        string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        string correlationId = Guid.NewGuid().ToString("N");
        string? operation = null;
        string? requestedTarget = null;
        AgentContext? context = null;
        AgentDebugRun? debug = null;
        AgentPayload payload;
        using CancellationTokenSource deadline = new();
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            ApplicationResult<AgentRequest> parsed = AgentRequest.Parse(args);
            if (parsed.IsFailure)
            {
                return new(AgentJson.Envelope(correlationId, null, null, new(null, parsed.FailureInfo)), null);
            }

            AgentRequest request = parsed.Value!;
            operation = request.Command.Name;
            requestedTarget = request.Target;
            deadline.CancelAfter(request.TimeoutMilliseconds);
            using AzurPilotOperation activity = AzurPilotOperation.Start("azurpilot.agent." + operation);
            correlationId = activity.CorrelationId;
            if (request.DebugSession is string session)
            {
                // Отчёт создаётся только по явному аргументу и до runtime: отказ записи не порождает эффектов.
                ApplicationResult<AgentDebugRun> created = AgentDebugRun.Create(session, correlationId, operation, requestedTarget,
                    ManifestArguments(args), request.TimeoutMilliseconds, TimeProvider.System, workingDirectory);
                if (created.IsFailure)
                {
                    return new(AgentJson.Envelope(correlationId, operation, null, new(null, created.FailureInfo), requestedTarget), null);
                }

                debug = created.Value!;
            }

            linked.Token.ThrowIfCancellationRequested();
            if (operation == "discover")
            {
                payload = await request.Command.Handler(new AgentContext(null), linked.Token).ConfigureAwait(false);
            }
            else
            {
                AgentDebugRun? observed = debug;
                HostRunner runHost = hostFactory?.Invoke(observed)
                    ?? ((run, token) => AzurPilotHost.RunAgentAsync(run, request.ConfigurationPath, token, observed));
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
                            observed?.Trace.Record("agent", "target_rejected", new { selection, failure = AgentJson.Failure(resolved.FailureInfo) });
                            return new AgentPayload(null, resolved.FailureInfo);
                        }

                        context.Target = resolved.Value;
                        if (observed is not null)
                        {
                            observed.ResolvedTarget = resolved.Value.ToString();
                            observed.Trace.Record("agent", "target_resolved", new { selection, target = observed.ResolvedTarget });
                        }
                    }

                    token.ThrowIfCancellationRequested();
                    observed?.Trace.Record("agent", "handler_started", new { command = request.Command.Name, mutation = request.Command.Mutation });
                    AgentPayload completed = await request.Command.Handler(context, token).ConfigureAwait(false);
                    context.Completed = completed;
                    observed?.Trace.Record("agent", "handler_completed", new { effect = completed.Effect, failure = AgentJson.Failure(completed.Failure) });
                    return completed;
                }, linked.Token).ConfigureAwait(false);
            }

            // Синхронные внешние probes имеют собственный bounded timeout. Если общий deadline наступил
            // во время такого probe, его результат сохраняется, но запрос не объявляется своевременным.
            if (linked.IsCancellationRequested)
            {
                debug?.Trace.Record("agent", !cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested
                    ? "deadline_exceeded" : "cancelled", new { timeoutMs = request.TimeoutMilliseconds });
            }

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
            debug?.Trace.Record("agent", "interrupted", new { exception = exception.GetType().Name, failure = AgentJson.Failure(failure) });
            payload = context?.Interrupted(failure) ?? new(null, failure);
        }

        AgentEnvelope envelope = AgentJson.Envelope(correlationId, operation, context?.Target?.ToString(), payload, requestedTarget);
        if (debug is null)
        {
            return new(envelope, null);
        }

        using (debug)
        {
            return new(envelope, await CompleteDebugAsync(debug, envelope).ConfigureAwait(false));
        }
    }

    // Запись отчёта завершается после итогового envelope и не может его изменить.
    private static async Task<AgentDebugCompletion> CompleteDebugAsync(AgentDebugRun debug, AgentEnvelope envelope)
    {
        try
        {
            return await debug.CompleteAsync(envelope).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(debug.RelativeRunDirectory, "incomplete", ["completion_failed:" + exception.GetType().Name]);
        }
    }

    // В manifest не попадают локальные пути: значение --config скрывается.
    private static IReadOnlyList<string> ManifestArguments(string[] args)
    {
        return [.. args.Skip(1).Select((value, index) => index > 0 && args[index] == "--config" ? "[путь скрыт]" : AgentJson.Safe(value))];
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
            AgentRunResult result = await RunWithDebugAsync(args, null, null, cancellation.Token).ConfigureAwait(false);
            AgentEnvelope envelope = result.Envelope;
            if (result.Debug is AgentDebugCompletion debug)
            {
                // Stdout остаётся единственным JSON envelope; путь отчёта сообщается структурированной строкой stderr.
                Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    category = "AzurPilot.App.Agent.Debug",
                    debugReport = debug.RelativeRunDirectory,
                    diagnostics = debug.Status,
                    incompleteReasons = debug.IncompleteReasons,
                }));
            }

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
