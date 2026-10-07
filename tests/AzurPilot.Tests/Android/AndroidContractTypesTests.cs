using System.Reflection;
using AzurPilot.Core.Android;
using AzurPilot.Core.Android.Orchestration;
using Xunit;

namespace AzurPilot.Tests.Android;

/// <summary>
/// Доказательства контрактных типов Android: точный ADB endpoint, идентификатор пакета и единственный
/// владелец product identity игры.
/// </summary>
/// <remarks>
/// Проверки относятся к самим значениям контракта: ни установленной MuMu, ни ADB, ни установленной игры
/// для них не требуется. Ошибки программирования (пустой host, порт вне диапазона, пустой пакет)
/// проверяются как исключения, а не как ожидаемые отказы: значение приходит из уже проверенной установки
/// и выбранной identity.
/// </remarks>
[Trait("Category", "Android")]
public sealed class AndroidContractTypesTests
{
    [Fact(DisplayName = "Пустой host ADB endpoint — ошибка программирования")]
    public void EmptyHostIsProgrammingError()
    {
        _ = Assert.Throws<ArgumentException>(() => new AndroidEndpoint(string.Empty, 5555));
        _ = Assert.Throws<ArgumentException>(() => new AndroidEndpoint("   ", 5555));
        _ = Assert.Throws<ArgumentNullException>(() => new AndroidEndpoint(null!, 5555));
    }

    [Fact(DisplayName = "Host с двоеточием или пробелом делает каноническую форму неоднозначной")]
    public void HostWithColonOrWhitespaceIsProgrammingError()
    {
        _ = Assert.Throws<ArgumentException>(() => new AndroidEndpoint("127.0.0.1:5555", 5555));
        _ = Assert.Throws<ArgumentException>(() => new AndroidEndpoint("127.0.0.1 5555", 5555));
    }

    [Fact(DisplayName = "Порт вне диапазона 1..65535 — ошибка программирования")]
    public void PortOutsideRangeIsProgrammingError()
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => new AndroidEndpoint("127.0.0.1", AndroidEndpoint.MinPort - 1));
        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => new AndroidEndpoint("127.0.0.1", AndroidEndpoint.MaxPort + 1));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new AndroidEndpoint("127.0.0.1", -1));
    }

    [Fact(DisplayName = "Границы диапазона порта допустимы")]
    public void PortBoundariesAreValid()
    {
        Assert.Equal(AndroidEndpoint.MinPort, new AndroidEndpoint("127.0.0.1", AndroidEndpoint.MinPort).Port);
        Assert.Equal(AndroidEndpoint.MaxPort, new AndroidEndpoint("127.0.0.1", AndroidEndpoint.MaxPort).Port);
    }

    [Fact(DisplayName = "Каноническая форма endpoint-а — host:port")]
    public void CanonicalFormIsHostAndPort()
    {
        AndroidEndpoint endpoint = AndroidEndpoint.FromHostPort("127.0.0.1", 16416);

        Assert.Equal("127.0.0.1:16416", endpoint.ToString());
        Assert.Equal("127.0.0.1", endpoint.Host);
        Assert.Equal(16416, endpoint.Port);

        // Host нормализуется: пробелы по краям не создают второй формы того же target-а.
        Assert.Equal(endpoint, AndroidEndpoint.FromHostPort(" 127.0.0.1 ", 16416));
    }

    [Fact(DisplayName = "Endpoint сравнивается по значению host и порта")]
    public void EndpointIsComparedByValue()
    {
        AndroidEndpoint endpoint = new("127.0.0.1", 16416);

        Assert.Equal(new AndroidEndpoint("127.0.0.1", 16416), endpoint);
        Assert.NotEqual(new AndroidEndpoint("127.0.0.1", 16417), endpoint);
        Assert.NotEqual(new AndroidEndpoint("127.0.0.2", 16416), endpoint);

        // Значение по умолчанию не является валидным endpoint-ом: host не задан, порт нулевой.
        Assert.Null(default(AndroidEndpoint).Host);
        Assert.Equal(0, default(AndroidEndpoint).Port);
        Assert.NotEqual(default, endpoint);
    }

    [Fact(DisplayName = "Идентификатор пакета проверяется на пригодность и сравнивается по значению")]
    public void PackageIdValidatesValue()
    {
        AndroidPackageId package = AndroidPackageId.FromValue(AzurLaneProduct.Package);

        Assert.Equal(AzurLaneProduct.Package, package.Value);
        Assert.Equal(AzurLaneProduct.Package, package.ToString());
        Assert.Equal(new AndroidPackageId(AzurLaneProduct.Package), package);

        _ = Assert.Throws<ArgumentNullException>(() => new AndroidPackageId(null!));
        _ = Assert.Throws<ArgumentException>(() => new AndroidPackageId(string.Empty));
        _ = Assert.Throws<ArgumentException>(() => new AndroidPackageId("   "));
        _ = Assert.Throws<ArgumentException>(() => new AndroidPackageId("com.YoStarEN AzurLane"));

        // Значение по умолчанию не является пригодным идентификатором пакета.
        Assert.Null(default(AndroidPackageId).Value);
    }

    [Fact(DisplayName = "AzurLaneProduct — единственный владелец identity Global/EN")]
    public void ProductIdentityIsOwnedOnce()
    {
        Assert.Equal("Azur Lane Global/EN", AzurLaneProduct.DisplayName);
        Assert.Equal("com.YoStarEN.AzurLane", AzurLaneProduct.Package);

        // Наблюдение состояния игры адресует ровно тот пакет, которым владеет product identity:
        // второго перечня значений нет.
        Assert.Equal(AzurLaneProduct.Package, AzurLaneGameStateService.Package.ToString());
    }

    [Fact(DisplayName = "Product identity не имеет региональных вариантов и user-configurable значений")]
    public void ProductIdentityHasNoRegionalVariants()
    {
        Type product = typeof(AzurLaneProduct);

        Assert.True(product.IsAbstract && product.IsSealed, "AzurLaneProduct обязан быть статическим типом.");

        MemberInfo[] declared =
            [.. product.GetMembers(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)];
        string[] memberNames = [.. declared.Select(member => member.Name).Order(StringComparer.Ordinal)];

        // Ровно два значения identity: display name и пакет Global/EN. Ни регионов, ни каналов, ни
        // списков известных пакетов, ни изменяемых свойств у владельца identity нет.
        Assert.Equal(["DisplayName", "Package"], memberNames);
        Assert.All(
            declared,
            member => Assert.True(
                member is FieldInfo { IsLiteral: true },
                $"Владелец product identity не должен объявлять член «{member.Name}»."));
    }
}
