using AzurPilot.Core.Android;
using AzurPilot.Core.Android.Orchestration;
using AzurPilot.Core.Failures;

namespace AzurPilot.Tests.Android;

/// <summary>
/// Сценарий устройства за подменяемой host-границей Android.
/// </summary>
/// <remarks>
/// <para>
/// Тип описывает только ответы границы: он не реализует ни одного решения orchestration, не выводит
/// состояние игры и не доказывает postcondition. Проверки вызывают настоящие
/// <see cref="AzurLaneGameStateService"/> и <see cref="AzurLaneGameLifecycleService"/>, а этот тип лишь
/// сообщает, что «устройство» ответило на примитив границы, и применяет эффект mutation так, как его
/// применило бы устройство.
/// </para>
/// <para>
/// Состояние устройства намеренно выражается ровно теми значениями контракта, которые различают
/// «доказано» и «не доказано»: присутствие пакета, наблюдение процессов и наблюдение переднего плана.
/// </para>
/// </remarks>
internal sealed class TestAndroidDevice
{
    private readonly TestAndroidHost _host;

    internal TestAndroidDevice(TestAndroidHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        _host = host;
        SetStopped();

        _host.PackageHandler = (_, _) => ApplicationResult<AndroidPackagePresence>.Success(Presence);
        _host.LauncherHandler = (_, _) => ApplicationResult<AndroidLauncherResolution>.Success(Launcher);
        _host.ProcessesHandler = (_, _) =>
        {
            ProcessesObserved?.Invoke();
            return ApplicationResult<AndroidProcessObservation>.Success(Processes);
        };
        _host.ForegroundHandler = _ => ApplicationResult<AndroidForegroundObservation>.Success(Foreground);
        _host.MutationHandler = (request, _) => ApplyMutation(request);
    }

    /// <summary>Наблюдённое присутствие пакета игры.</summary>
    internal AndroidPackagePresence Presence { get; set; }

    /// <summary>Наблюдение процессов пакета игры.</summary>
    internal AndroidProcessObservation Processes { get; set; } = AndroidTestContext.AbsentProcesses();

    /// <summary>Наблюдение переднего плана.</summary>
    internal AndroidForegroundObservation Foreground { get; set; } = AndroidTestContext.OtherForeground();

    /// <summary>Разрешение launcher-компонента пакета игры.</summary>
    internal AndroidLauncherResolution Launcher { get; set; } = AndroidTestContext.ResolvedLauncher();

    /// <summary>Код выхода команды запуска игры.</summary>
    internal int StartExitCode { get; set; }

    /// <summary>Код выхода команды принудительной остановки игры.</summary>
    internal int ForceStopExitCode { get; set; }

    /// <summary>Достигает ли запуск переднего плана игры: иначе устройство остаётся в прежнем состоянии.</summary>
    internal bool StartReachesForeground { get; set; } = true;

    /// <summary>Достигает ли принудительная остановка доказанного отсутствия процессов.</summary>
    internal bool ForceStopReachesStopped { get; set; } = true;

    /// <summary>Наблюдаемое событие перед ответом на запрос процессов.</summary>
    internal Action? ProcessesObserved { get; set; }

    /// <summary>Наблюдаемое событие после применения эффекта mutation.</summary>
    internal Action<AndroidMutationRequest>? MutationApplied { get; set; }

    /// <summary>Игра установлена, её процессов нет, игра не на переднем плане.</summary>
    internal void SetStopped()
    {
        Presence = AndroidPackagePresence.Installed;
        Processes = AndroidTestContext.AbsentProcesses();
        Foreground = AndroidTestContext.OtherForeground();
    }

    /// <summary>Игра установлена, её процесс наблюдается, но игра не на переднем плане.</summary>
    /// <param name="processIds">Наблюдённые процессы пакета игры.</param>
    internal void SetBackground(params int[] processIds)
    {
        int[] running = processIds.Length == 0 ? [4242] : processIds;

        Presence = AndroidPackagePresence.Installed;
        Processes = AndroidTestContext.RunningProcesses(running);
        Foreground = AndroidTestContext.OtherForeground();
    }

    /// <summary>Игра установлена, её процесс наблюдается и игра на переднем плане.</summary>
    /// <param name="activity">Наблюдённая activity пакета игры на переднем плане.</param>
    internal void SetForeground(string activity = "com.manjuu.azurlane.MainActivity")
    {
        Presence = AndroidPackagePresence.Installed;
        Processes = AndroidTestContext.RunningProcesses(4242);
        Foreground = AndroidTestContext.GameForeground(activity);
    }

    /// <summary>Устройство ответило, что пакета игры нет.</summary>
    internal void SetNotInstalled()
    {
        Presence = AndroidPackagePresence.Absent;
        Processes = AndroidTestContext.AbsentProcesses();
        Foreground = AndroidTestContext.OtherForeground();
    }

    /// <summary>Присутствие пакета игры не доказано: запрос не дал распознанного ответа.</summary>
    internal void SetPackageQueryFailed()
    {
        Presence = AndroidPackagePresence.QueryFailed;
        Processes = AndroidTestContext.AbsentProcesses();
        Foreground = AndroidTestContext.OtherForeground();
    }

    /// <summary>Наличие и отсутствие процессов пакета игры не доказаны.</summary>
    internal void SetUnprovenProcesses()
    {
        Presence = AndroidPackagePresence.Installed;
        Processes = AndroidTestContext.UnprovenProcesses();
        Foreground = AndroidTestContext.OtherForeground();
    }

    /// <summary>Передний план не доказан: форма ответа устройства не распознана.</summary>
    internal void SetUnknownForeground()
    {
        Presence = AndroidPackagePresence.Installed;
        Processes = AndroidTestContext.RunningProcesses(4242);
        Foreground = AndroidTestContext.UnknownForeground();
    }

    private ApplicationResult<AndroidCommandOutcome> ApplyMutation(AndroidMutationRequest request)
    {
        switch (request.Mutation)
        {
            case AndroidGameMutation.Start:
                if (StartReachesForeground)
                {
                    SetForeground();
                }

                MutationApplied?.Invoke(request);
                return AndroidTestContext.Command(StartExitCode);
            case AndroidGameMutation.ForceStop:
                if (ForceStopReachesStopped)
                {
                    SetStopped();
                }

                MutationApplied?.Invoke(request);
                return AndroidTestContext.Command(ForceStopExitCode);
            default:
                throw new InvalidOperationException(
                    $"Mutation игры {request.Mutation} не поддерживается сценарием устройства.");
        }
    }
}
