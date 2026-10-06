using System.Reflection;
using AzurPilot.Core.MuMu;
using Xunit;

namespace AzurPilot.Tests.MuMu;

/// <summary>
/// Зафиксированная форма платформенно-независимого MuMu-контракта.
/// </summary>
/// <remarks>
/// Проверка идёт по публичной поверхности типов: имена, пространство имён, значения enum и сигнатуры
/// принадлежат контракту, против которого одновременно пишут платформенный adapter и acceptance.
/// Отдельно доказывается, что ни один тип контракта не зависит от Win32, реестра, процессов и
/// platform-specific путей установки.
/// </remarks>
public sealed class MuMuContractTests
{
    private static readonly Type[] Contracts =
    [
        typeof(MuMuInstanceId),
        typeof(MuMuInstance),
        typeof(MuMuInstallation),
        typeof(MuMuInstanceState),
        typeof(MuMuLifecycleState),
        typeof(MuMuLifecycleOperation),
        typeof(MuMuLifecycleMutation),
        typeof(MuMuLifecycleCommandOutcome),
        typeof(MuMuInstanceSelection),
        typeof(IMuMuHost),
        typeof(MuMuInstanceResolution),
        typeof(MuMuLifecycleOutcome),
        typeof(MuMuLifecycleService),
        typeof(MuMuLifecycleTimings),
        typeof(MuMuInstanceMutationGate),
    ];

    private static readonly string[] OperationNames = ["Start", "Stop", "Restart"];

    private static readonly string[] MutationNames = ["Start", "Stop"];

    private static readonly string[] OperationMethodNames =
    [
        nameof(MuMuLifecycleService.StartAsync),
        nameof(MuMuLifecycleService.StopAsync),
        nameof(MuMuLifecycleService.RestartAsync),
    ];

    private static readonly string[] TimingsPropertyNames =
    [
        nameof(MuMuLifecycleTimings.PollInterval),
        nameof(MuMuLifecycleTimings.StartDeadline),
        nameof(MuMuLifecycleTimings.StopDeadline),
        nameof(MuMuLifecycleTimings.RestartDeadline),
        nameof(MuMuLifecycleTimings.LaunchEffectWindow),
    ];

    [Fact(DisplayName = "MuMu-контракты живут в пространстве имён AzurPilot.Core.MuMu")]
    public void ContractsLiveInFrozenNamespace()
    {
        Assert.All(Contracts, contract => Assert.Equal("AzurPilot.Core.MuMu", contract.Namespace));
    }

    [Fact(DisplayName = "MuMu-контракты не зависят от Win32, реестра, процессов и путей установки")]
    public void ContractsDoNotDependOnPlatformSurface()
    {
        List<string> violations = [];
        foreach (Type contract in Contracts)
        {
            foreach (Type referenced in ReferencedTypes(contract))
            {
                string? referencedNamespace = referenced.Namespace;
                if (referencedNamespace is not null && IsPlatformNamespace(referencedNamespace))
                {
                    violations.Add($"{contract.Name} → {referenced.FullName}");
                }
            }
        }

        Assert.Empty(violations);
    }

    [Fact(DisplayName = "MuMuLifecycleState имеет зафиксированные значения")]
    public void LifecycleStateHasFrozenValues()
    {
        Assert.Equal(0, (int)MuMuLifecycleState.Unknown);
        Assert.Equal(1, (int)MuMuLifecycleState.Stopped);
        Assert.Equal(2, (int)MuMuLifecycleState.Running);
    }

    [Fact(DisplayName = "MuMuLifecycleOperation и MuMuLifecycleMutation различают операцию и примитив host-а")]
    public void OperationAndMutationAreSeparatePrimitives()
    {
        Assert.Equal(OperationNames, Enum.GetNames<MuMuLifecycleOperation>());
        Assert.Equal(MutationNames, Enum.GetNames<MuMuLifecycleMutation>());
        Assert.Equal(0, (int)MuMuLifecycleOperation.Start);
        Assert.Equal(1, (int)MuMuLifecycleOperation.Stop);
        Assert.Equal(2, (int)MuMuLifecycleOperation.Restart);
        Assert.Equal(0, (int)MuMuLifecycleMutation.Start);
        Assert.Equal(1, (int)MuMuLifecycleMutation.Stop);
    }

    [Fact(DisplayName = "MuMuInstanceId — value-тип с property Index, фабрикой FromIndex и канонической формой")]
    public void InstanceIdHasFrozenShape()
    {
        Type instanceId = typeof(MuMuInstanceId);

        Assert.True(instanceId.IsValueType);
        Assert.NotNull(instanceId.GetProperty(nameof(MuMuInstanceId.Index), typeof(string)));
        Assert.NotNull(instanceId.GetConstructor([typeof(string)]));
        Assert.NotNull(instanceId.GetMethod(nameof(MuMuInstanceId.FromIndex), [typeof(string)]));
        Assert.Equal("mumu:1", MuMuInstanceId.FromIndex("1").ToString());
    }

    [Fact(DisplayName = "Записи контракта имеют зафиксированный позиционный состав")]
    public void RecordsHaveFrozenPositionalShape()
    {
        Assert.True(typeof(MuMuInstance).IsSealed);
        Assert.NotNull(typeof(MuMuInstance).GetConstructor([typeof(MuMuInstanceId), typeof(string), typeof(string)]));

        Assert.True(typeof(MuMuInstallation).IsSealed);
        Assert.NotNull(typeof(MuMuInstallation).GetConstructor([typeof(string), typeof(string), typeof(string)]));

        Assert.True(typeof(MuMuInstanceState).IsSealed);
        Assert.NotNull(typeof(MuMuInstanceState).GetConstructor([typeof(MuMuLifecycleState), typeof(string)]));

        Assert.True(typeof(MuMuLifecycleCommandOutcome).IsSealed);
        Assert.NotNull(typeof(MuMuLifecycleCommandOutcome).GetConstructor([typeof(int), typeof(string)]));

        Assert.True(typeof(MuMuInstanceSelection).IsSealed);
        Assert.NotNull(typeof(MuMuInstanceSelection).GetConstructor([typeof(bool), typeof(MuMuInstanceId?)]));
        Assert.NotNull(typeof(MuMuInstanceSelection).GetMethod(nameof(MuMuInstanceSelection.Auto), Type.EmptyTypes));
        Assert.NotNull(typeof(MuMuInstanceSelection).GetMethod(
            nameof(MuMuInstanceSelection.Explicit),
            [typeof(MuMuInstanceId)]));

        Assert.True(typeof(MuMuLifecycleOutcome).IsSealed);
        Assert.NotNull(typeof(MuMuLifecycleOutcome).GetConstructor(
            [typeof(MuMuLifecycleOperation), typeof(MuMuLifecycleState), typeof(MuMuLifecycleState), typeof(string), typeof(TimeSpan)]));
        Assert.NotNull(typeof(MuMuLifecycleOutcome).GetProperty(nameof(MuMuLifecycleOutcome.InstanceId)));

        Assert.NotNull(typeof(MuMuInstanceResolution).GetMethod(
            nameof(MuMuInstanceResolution.Resolved),
            [typeof(MuMuInstance)]));
        Assert.NotNull(typeof(MuMuInstanceResolution).GetMethod(
            nameof(MuMuInstanceResolution.Failed),
            [typeof(AzurPilot.Core.Failures.ApplicationFailure)]));
    }

    [Fact(DisplayName = "IMuMuHost несёт host-примитив mutation, а не операцию сервиса")]
    public void HostSurfaceCarriesMutationPrimitive()
    {
        Assert.NotNull(typeof(IMuMuHost).GetMethod(nameof(IMuMuHost.DiscoverInstallation), Type.EmptyTypes));
        Assert.NotNull(typeof(IMuMuHost).GetMethod(
            nameof(IMuMuHost.EnumerateInstances),
            [typeof(MuMuInstallation)]));
        Assert.NotNull(typeof(IMuMuHost).GetMethod(
            nameof(IMuMuHost.ObserveInstanceState),
            [typeof(MuMuInstallation), typeof(MuMuInstanceId)]));

        MethodInfo request = Assert.IsAssignableFrom<MethodInfo>(
            typeof(IMuMuHost).GetMethod(
                nameof(IMuMuHost.RequestMutation),
                [typeof(MuMuInstallation), typeof(MuMuInstanceId), typeof(MuMuLifecycleMutation), typeof(CancellationToken)]));

        Assert.Null(typeof(IMuMuHost).GetMethod("RequestLifecycle"));
        Assert.Equal(
            typeof(AzurPilot.Core.Failures.ApplicationResult<MuMuLifecycleCommandOutcome>),
            request.ReturnType);
    }

    [Fact(DisplayName = "MuMuLifecycleService несёт Resolve и три lifecycle-операции")]
    public void ServiceExposesResolveAndOperations()
    {
        Assert.NotNull(typeof(MuMuLifecycleService).GetMethod(
            nameof(MuMuLifecycleService.Resolve),
            [typeof(MuMuInstallation), typeof(MuMuInstanceSelection)]));

        Type outcomeResult = typeof(AzurPilot.Core.Failures.ApplicationResult<MuMuLifecycleOutcome>);
        Assert.All(
            OperationMethodNames,
            name =>
            {
                MethodInfo method = Assert.IsAssignableFrom<MethodInfo>(typeof(MuMuLifecycleService).GetMethod(
                    name,
                    [typeof(MuMuInstallation), typeof(MuMuInstance), typeof(CancellationToken)]));
                Assert.Equal(typeof(Task<>).MakeGenericType(outcomeResult), method.ReturnType);
            });
    }

    [Fact(DisplayName = "MuMuLifecycleTimings — владелец интервала опроса и deadline каждой операции")]
    public void TimingsOwnPollIntervalAndDeadlines()
    {
        Assert.All(
            TimingsPropertyNames,
            name => Assert.NotNull(typeof(MuMuLifecycleTimings).GetProperty(name, typeof(TimeSpan))));

        Assert.NotNull(typeof(MuMuLifecycleTimings).GetMethod(
            nameof(MuMuLifecycleTimings.DeadlineFor),
            [typeof(MuMuLifecycleOperation)]));
        Assert.NotNull(typeof(MuMuLifecycleTimings).GetProperty(nameof(MuMuLifecycleTimings.Default)));
    }

    private static bool IsPlatformNamespace(string referencedNamespace)
        => referencedNamespace.StartsWith("Microsoft.Win32", StringComparison.Ordinal)
            || referencedNamespace.StartsWith("System.Diagnostics", StringComparison.Ordinal)
            || referencedNamespace.StartsWith("System.Runtime.InteropServices", StringComparison.Ordinal);

    private static IEnumerable<Type> ReferencedTypes(Type contract)
    {
        if (contract.BaseType is Type baseType)
        {
            yield return baseType;
        }

        foreach (Type implemented in contract.GetInterfaces())
        {
            yield return implemented;
        }

        const BindingFlags Declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static
            | BindingFlags.DeclaredOnly;

        foreach (PropertyInfo property in contract.GetProperties(Declared))
        {
            foreach (Type type in Expand(property.PropertyType))
            {
                yield return type;
            }
        }

        foreach (MethodInfo method in contract.GetMethods(Declared))
        {
            foreach (Type type in Expand(method.ReturnType))
            {
                yield return type;
            }

            foreach (ParameterInfo parameter in method.GetParameters())
            {
                foreach (Type type in Expand(parameter.ParameterType))
                {
                    yield return type;
                }
            }
        }
    }

    private static IEnumerable<Type> Expand(Type type)
    {
        yield return type;

        if (!type.IsGenericType)
        {
            yield break;
        }

        foreach (Type argument in type.GetGenericArguments())
        {
            foreach (Type expanded in Expand(argument))
            {
                yield return expanded;
            }
        }
    }
}
