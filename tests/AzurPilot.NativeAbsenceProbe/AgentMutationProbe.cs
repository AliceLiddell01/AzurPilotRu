using AzurPilot.App;
using AzurPilot.App.Agent;
using AzurPilot.App.OperatorServices;
using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace AzurPilot.NativeAbsenceProbe;

// Управляется только внешний IMuMuHost. CLI dispatch, TUI service boundary, gates и orchestration — production.
internal static class AgentMutationProbe
{
    internal static async Task<int> RunAsync(string role, string index, string directory, string completion)
    {
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(25));
        var configuration = AzurPilotConfigurationLoader.Load(Path.Combine(directory, "absent.yaml"));
        HostApplicationBuilder builder = AzurPilotHost.CreateBuilder(configuration);
        _ = builder.Services.RemoveAll<IMuMuHost>();
        _ = builder.Services.AddSingleton<IMuMuHost>(new ControlledMuMuHost(index, directory, completion, cancellation));
        using IHost host = builder.Build();
        await host.StartAsync(cancellation.Token).ConfigureAwait(false);
        AgentEnvelope envelope;
        try
        {
            AzurPilotOperatorService service = host.Services.GetRequiredService<AzurPilotOperatorService>();
            if (role == "cli")
            {
                envelope = await AgentRunner.RunAsync(["agent", "mumu-start", "--target", "mumu:" + index],
                    (run, token) => run(service, token), cancellation.Token).ConfigureAwait(false);
            }
            else
            {
                ApplicationResult<MuMuLifecycleOutcome> result = await service.RunMuMuLifecycleAsync(
                    MuMuInstanceId.FromIndex(index), MuMuLifecycleOperation.Start, cancellation.Token).ConfigureAwait(false);
                envelope = AgentJson.Envelope("probe", "mumu-start", "mumu:" + index,
                    result.IsSuccess ? new(result.Value, Effect: "changed") : new(null, result.FailureInfo));
            }
        }
        finally
        {
            await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        byte[] bytes = AgentJson.Serialize(ref envelope);
        using Stream output = Console.OpenStandardOutput();
        await output.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false);
        return envelope.ExitCode;
    }

    private sealed class ControlledMuMuHost(string index, string directory, string completion,
        CancellationTokenSource cancellation) : IMuMuHost
    {
        private MuMuLifecycleState _state = MuMuLifecycleState.Stopped;
        public ApplicationResult<MuMuInstallation> DiscoverInstallation()
            => ApplicationResult<MuMuInstallation>.Success(new("probe", directory, "controlled-external-seam"));
        public ApplicationResult<IReadOnlyList<MuMuInstance>> EnumerateInstances(MuMuInstallation installation)
            => ApplicationResult<IReadOnlyList<MuMuInstance>>.Success([new(MuMuInstanceId.FromIndex(index), "probe", "probe")]);
        public ApplicationResult<MuMuInstanceState> ObserveInstanceState(MuMuInstallation installation, MuMuInstanceId instance)
        {
            if (completion == "observe" && _state == MuMuLifecycleState.Stopped
                || completion == "postcondition" && _state == MuMuLifecycleState.Running)
            {
                WaitForRelease(instance, cancellation.Token);
            }

            return ApplicationResult<MuMuInstanceState>.Success(new(_state, "controlled-observation"));
        }
        public ApplicationResult<MuMuLifecycleCommandOutcome> RequestMutation(MuMuInstallation installation,
            MuMuInstanceId instance, MuMuLifecycleMutation mutation, CancellationToken cancellationToken)
        {
            if (completion is not ("observe" or "postcondition"))
            {
                WaitForRelease(instance, cancellationToken);
            }

            if (completion == "cancel")
            {
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            _state = MuMuLifecycleState.Running;
            return ApplicationResult<MuMuLifecycleCommandOutcome>.Success(new(0, "controlled-mutation"));
        }

        private void WaitForRelease(MuMuInstanceId instance, CancellationToken cancellationToken)
        {
            File.WriteAllText(Path.Combine(directory, "entered"), instance.ToString());
            while (!File.Exists(Path.Combine(directory, "release")))
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = cancellationToken.WaitHandle.WaitOne(10);
            }
        }
    }
}
