namespace AzurPilot.Core.Game;

/// <summary>Логическая полуоткрытая область игрового UI в reference space.</summary>
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

/// <summary>Точка Android input в проверенной системе физических координат.</summary>
public readonly record struct GameInputPoint(int X, int Y);

/// <summary>Геометрия поддерживаемого landscape 16:9; не утверждает совпадение с Android viewport.</summary>
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
            throw new ArgumentOutOfRangeException(nameof(width), "Требуется landscape 16:9 в проверенном диапазоне 720p–4K.");
        }

        Width = width;
        Height = height;
    }

    /// <summary>Округляет границы наружу; не выполняет clipping.</summary>
    public GamePixelRect Map(GameRect rect)
    {
        Validate(rect);
        return new(ScaleFloor(rect.Left, Width, LogicalWidth), ScaleFloor(rect.Top, Height, LogicalHeight),
            ScaleCeiling(rect.Right, Width, LogicalWidth), ScaleCeiling(rect.Bottom, Height, LogicalHeight));
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
