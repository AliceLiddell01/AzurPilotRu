using AzurPilot.Core.Configuration;
using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using Xunit;

namespace AzurPilot.Tests.MuMu;

/// <summary>
/// Доказательства семантики разрешения выбранного экземпляра.
/// </summary>
/// <remarks>
/// Автоматический выбор разрешает только ровно один экземпляр, явный выбор ищет по stable identity, а
/// отображаемое имя identity не является.
/// </remarks>
public sealed class MuMuInstanceResolutionTests
{
    [Fact(DisplayName = "auto с нулём экземпляров даёт mumu_instance_not_found")]
    public void AutoWithNoInstancesFailsWithNotFound()
    {
        MuMuLifecycleTestContext context = new();
        context.Host.InstancesResult = ApplicationResult<IReadOnlyList<MuMuInstance>>.Success([]);

        MuMuInstanceResolution resolution = context.Service.Resolve(
            MuMuLifecycleTestContext.Installation,
            MuMuInstanceSelection.Auto());

        Assert.True(resolution.IsFailed);
        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.Instance);

        ApplicationFailure failure = resolution.Failure!;
        Assert.Equal(ApplicationFailure.MuMuInstanceNotFound, failure.Code);

        // Режим выбора в details — токен владельца синтаксиса значения конфигурации.
        Assert.Equal(MuMuInstanceValue.AutoValue, failure.Details!["selection"]);
        Assert.Equal("0", failure.Details!["instance_count"]);
    }

    [Fact(DisplayName = "auto с ровно одним экземпляром выбирает его")]
    public void AutoWithSingleInstanceResolvesIt()
    {
        MuMuLifecycleTestContext context = new();
        MuMuInstance only = MuMuLifecycleTestContext.Instance("1", "Единственный экземпляр");
        context.Host.InstancesResult = ApplicationResult<IReadOnlyList<MuMuInstance>>.Success([only]);

        MuMuInstanceResolution resolution = context.Service.Resolve(
            MuMuLifecycleTestContext.Installation,
            MuMuInstanceSelection.Auto());

        Assert.True(resolution.IsResolved);
        Assert.Equal(only, resolution.Instance);
        Assert.Null(resolution.Failure);
    }

    [Fact(DisplayName = "auto с двумя и более экземплярами даёт mumu_instance_ambiguous и не выбирает ни первый, ни vmindex 0")]
    public void AutoWithSeveralInstancesIsAmbiguous()
    {
        MuMuLifecycleTestContext context = new();
        MuMuInstance first = MuMuLifecycleTestContext.Instance("0", "Первый по перечислению");
        MuMuInstance second = MuMuLifecycleTestContext.Instance("1", "Второй по перечислению");
        context.Host.InstancesResult = ApplicationResult<IReadOnlyList<MuMuInstance>>.Success([first, second]);

        MuMuInstanceResolution resolution = context.Service.Resolve(
            MuMuLifecycleTestContext.Installation,
            MuMuInstanceSelection.Auto());

        Assert.True(resolution.IsFailed);
        Assert.Null(resolution.Instance);

        ApplicationFailure failure = resolution.Failure!;
        Assert.Equal(ApplicationFailure.MuMuInstanceAmbiguous, failure.Code);
        Assert.Equal(MuMuInstanceValue.AutoValue, failure.Details!["selection"]);
        Assert.Equal("2", failure.Details!["instance_count"]);
        Assert.Equal("mumu:0,mumu:1", failure.Details!["candidate_ids"]);
    }

    [Fact(DisplayName = "explicit находит экземпляр по stable identity, а не по позиции")]
    public void ExplicitResolvesByIdentity()
    {
        MuMuLifecycleTestContext context = new();
        context.Host.InstancesResult = ApplicationResult<IReadOnlyList<MuMuInstance>>.Success(
        [
            MuMuLifecycleTestContext.Instance("1"),
            MuMuLifecycleTestContext.Instance("2"),
            MuMuLifecycleTestContext.Instance("3"),
        ]);

        MuMuInstanceResolution resolution = context.Service.Resolve(
            MuMuLifecycleTestContext.Installation,
            MuMuInstanceSelection.Explicit(MuMuInstanceId.FromIndex("2")));

        Assert.True(resolution.IsResolved);
        Assert.Equal(MuMuInstanceId.FromIndex("2"), resolution.Instance!.Id);
    }

    [Fact(DisplayName = "explicit с ненайденной identity даёт mumu_instance_not_found")]
    public void ExplicitWithUnknownIdentityFailsWithNotFound()
    {
        MuMuLifecycleTestContext context = new();
        context.Host.InstancesResult = ApplicationResult<IReadOnlyList<MuMuInstance>>.Success(
        [
            MuMuLifecycleTestContext.Instance("1"),
            MuMuLifecycleTestContext.Instance("2"),
        ]);

        MuMuInstanceResolution resolution = context.Service.Resolve(
            MuMuLifecycleTestContext.Installation,
            MuMuInstanceSelection.Explicit(MuMuInstanceId.FromIndex("7")));

        Assert.True(resolution.IsFailed);
        Assert.Null(resolution.Instance);

        ApplicationFailure failure = resolution.Failure!;
        Assert.Equal(ApplicationFailure.MuMuInstanceNotFound, failure.Code);
        Assert.Equal("explicit", failure.Details!["selection"]);
        Assert.Equal("mumu:7", failure.Details!["instance_id"]);
        Assert.Equal("2", failure.Details!["instance_count"]);
    }

    [Fact(DisplayName = "display name не является identity и не участвует в выборе")]
    public void DisplayNameIsNotIdentity()
    {
        MuMuLifecycleTestContext context = new();
        MuMuInstance instance = MuMuLifecycleTestContext.Instance("5", "1");
        context.Host.InstancesResult = ApplicationResult<IReadOnlyList<MuMuInstance>>.Success([instance]);

        MuMuInstanceResolution byDisplayName = context.Service.Resolve(
            MuMuLifecycleTestContext.Installation,
            MuMuInstanceSelection.Explicit(MuMuInstanceId.FromIndex("1")));

        Assert.True(byDisplayName.IsFailed);
        Assert.Equal(ApplicationFailure.MuMuInstanceNotFound, byDisplayName.Failure!.Code);

        MuMuInstanceResolution byIdentity = context.Service.Resolve(
            MuMuLifecycleTestContext.Installation,
            MuMuInstanceSelection.Explicit(MuMuInstanceId.FromIndex("5")));

        Assert.Equal(instance, byIdentity.Instance);
    }

    [Fact(DisplayName = "Ожидаемый отказ перечисления экземпляров пробрасывается без изменений")]
    public void EnumerationFailureIsPropagatedUnchanged()
    {
        MuMuLifecycleTestContext context = new();
        ApplicationFailure enumerationFailure = new()
        {
            Code = ApplicationFailure.MuMuInstallationNotFound,
            Message = "Установка MuMuPlayer не обнаружена.",
        };
        context.Host.InstancesResult = ApplicationResult<IReadOnlyList<MuMuInstance>>.Failure(enumerationFailure);

        MuMuInstanceResolution resolution = context.Service.Resolve(
            MuMuLifecycleTestContext.Installation,
            MuMuInstanceSelection.Auto());

        Assert.True(resolution.IsFailed);
        Assert.Same(enumerationFailure, resolution.Failure);
    }

    [Fact(DisplayName = "Явный выбор без identity — ошибка программирования, а не ожидаемый отказ")]
    public void MalformedSelectionIsRejected()
    {
        MuMuLifecycleTestContext context = new();

        _ = Assert.Throws<ArgumentException>(() => context.Service.Resolve(
            MuMuLifecycleTestContext.Installation,
            new MuMuInstanceSelection(false, null)));
    }

    [Fact(DisplayName = "Отсутствующие аргументы разрешения — ошибка программирования")]
    public void MissingArgumentsAreRejected()
    {
        MuMuLifecycleTestContext context = new();

        _ = Assert.Throws<ArgumentNullException>(() => context.Service.Resolve(
            null!,
            MuMuInstanceSelection.Auto()));
        _ = Assert.Throws<ArgumentNullException>(() => context.Service.Resolve(
            MuMuLifecycleTestContext.Installation,
            null!));
    }
}
