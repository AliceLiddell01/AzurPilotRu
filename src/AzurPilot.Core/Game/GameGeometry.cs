namespace AzurPilot.Core.Game;

/// <summary>Полуоткрытая область игрового UI в логической системе координат.</summary>
public readonly record struct GameRect(int Left, int Top, int Right, int Bottom);

/// <summary>Полуоткрытая область физических пикселей кадра.</summary>
public readonly record struct GamePixelRect(int Left, int Top, int Right, int Bottom)
{
    /// <summary>Проверяет принадлежность физической точки области.</summary>
    public bool Contains(GameInputPoint point)
    {
        return point.X >= Left && point.X < Right && point.Y >= Top && point.Y < Bottom;
    }
}

/// <summary>Точка ввода Android в проверенной системе физических координат.</summary>
public readonly record struct GameInputPoint(int X, int Y);

/// <summary>Геометрия поддерживаемой альбомной ориентации 16:9; не доказывает совпадение с областью игры Android.</summary>
public sealed class GameGeometry
{
    /// <summary>Ширина логического пространства UI.</summary>
    public const int LogicalWidth = 1280;
    /// <summary>Высота логического пространства UI.</summary>
    public const int LogicalHeight = 720;
    /// <summary>Физическая ширина.</summary>
    public int Width { get; }
    /// <summary>Физическая высота.</summary>
    public int Height { get; }

    /// <summary>Создаёт геометрию только для доказанного диапазона 720p–4K.</summary>
    public GameGeometry(int width, int height)
    {
        if (width < LogicalWidth || height < LogicalHeight || width > 3840 || height > 2160
            || (long)width * LogicalHeight != (long)height * LogicalWidth)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Требуется альбомная ориентация 16:9 в проверенном диапазоне 720p–4K.");
        }

        Width = width;
        Height = height;
    }

    /// <summary>Округляет границы наружу; не обрезает область по границам кадра.</summary>
    public GamePixelRect Map(GameRect rect)
    {
        Validate(rect);
        return new(ScaleFloor(rect.Left, Width, LogicalWidth), ScaleFloor(rect.Top, Height, LogicalHeight),
            ScaleCeiling(rect.Right, Width, LogicalWidth), ScaleCeiling(rect.Bottom, Height, LogicalHeight));
    }

    /// <summary>Округляет границы внутрь: результат никогда не выходит за логическую область.</summary>
    public GamePixelRect MapInside(GameRect rect)
    {
        Validate(rect);
        return new(ScaleCeiling(rect.Left, Width, LogicalWidth), ScaleCeiling(rect.Top, Height, LogicalHeight),
            ScaleFloor(rect.Right, Width, LogicalWidth), ScaleFloor(rect.Bottom, Height, LogicalHeight));
    }

    /// <summary>
    /// Обратное отображение физической полуоткрытой области кадра в логическое пространство: границы
    /// округляются наружу, область вне кадра отвергается, а не обрезается. Используется для показа уже
    /// принятых решений и не участвует в распознавании или вводе.
    /// </summary>
    public GameRect ToLogical(GamePixelRect rect)
    {
        if (rect.Left < 0 || rect.Top < 0 || rect.Right > Width || rect.Bottom > Height
            || rect.Left >= rect.Right || rect.Top >= rect.Bottom)
        {
            throw new ArgumentOutOfRangeException(nameof(rect));
        }

        return new(ScaleFloor(rect.Left, LogicalWidth, Width), ScaleFloor(rect.Top, LogicalHeight, Height),
            ScaleCeiling(rect.Right, LogicalWidth, Width), ScaleCeiling(rect.Bottom, LogicalHeight, Height));
    }

    /// <summary>Логический пиксель, содержащий физическую точку кадра.</summary>
    public GameInputPoint ToLogical(GameInputPoint point)
    {
        return point.X < 0 || point.Y < 0 || point.X >= Width || point.Y >= Height
            ? throw new ArgumentOutOfRangeException(nameof(point))
            : new(ScaleFloor(point.X, LogicalWidth, Width), ScaleFloor(point.Y, LogicalHeight, Height));
    }

    /// <summary>Пересекаются ли две полуоткрытые логические области.</summary>
    public static bool Intersects(GameRect first, GameRect second)
    {
        return first.Left < second.Right && second.Left < first.Right && first.Top < second.Bottom && second.Top < first.Bottom;
    }

    /// <summary>Выбирает внутреннюю центральную точку разрешённой области.</summary>
    public GameInputPoint Center(GamePixelRect rect)
    {
        return rect.Left < 0 || rect.Top < 0 || rect.Right > Width || rect.Bottom > Height
            || rect.Left >= rect.Right || rect.Top >= rect.Bottom
            ? throw new ArgumentOutOfRangeException(nameof(rect))
            : new(rect.Left + ((rect.Right - rect.Left - 1) / 2), rect.Top + ((rect.Bottom - rect.Top - 1) / 2));
    }

    /// <summary>Проверяет логические полуоткрытые границы.</summary>
    public static void Validate(GameRect rect)
    {
        if (rect.Left < 0 || rect.Top < 0 || rect.Right > LogicalWidth || rect.Bottom > LogicalHeight
            || rect.Left >= rect.Right || rect.Top >= rect.Bottom)
        {
            throw new ArgumentOutOfRangeException(nameof(rect));
        }
    }

    private static int ScaleFloor(int value, int physical, int logical)
    {
        return checked((int)((long)value * physical / logical));
    }

    private static int ScaleCeiling(int value, int physical, int logical)
    {
        return checked((int)((((long)value * physical) + logical - 1) / logical));
    }
}
