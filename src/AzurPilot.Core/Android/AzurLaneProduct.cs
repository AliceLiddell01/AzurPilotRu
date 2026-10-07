namespace AzurPilot.Core.Android;

/// <summary>
/// Product identity игры Azur Lane Global/EN.
/// </summary>
/// <remarks>
/// <para>
/// Это единственный владелец product identity: и отображаемое имя, и идентификатор пакета заданы здесь
/// ровно один раз. Второй перечень значений — в коде, конфигурации или документации — не заводится.
/// </para>
/// <para>
/// Значения не являются user-configurable: регионы, каналы, списки известных пакетов и автоопределение
/// JP/CN/TW не поддерживаются. Другой регион — другая capability со своим контрактом, а не значение
/// настройки этого типа.
/// </para>
/// <para>
/// <see cref="DisplayName"/> — человекочитаемое имя продукта для оператора, <see cref="Package"/> —
/// идентификатор пакета, которым адресуется игра на устройстве. Значения совпадают с Global/EN-версией
/// игры и не выводятся из окружения: подстановка «похожего» пакета запрещена.
/// </para>
/// </remarks>
public static class AzurLaneProduct
{
    /// <summary>Отображаемое имя продукта: <c>Azur Lane Global/EN</c>.</summary>
    public const string DisplayName = "Azur Lane Global/EN";

    /// <summary>Идентификатор пакета Global/EN-версии игры: <c>com.YoStarEN.AzurLane</c>.</summary>
    public const string Package = "com.YoStarEN.AzurLane";
}
