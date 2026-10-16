using Terminal.Gui.Input;

namespace AzurPilot.App.OperatorUi;

internal static class OperatorUiScrollMath
{
    private const int WheelLines = 3;

    internal static bool TryGetWheelDelta(MouseFlags flags, out int delta)
    {
        if (flags.HasFlag(MouseFlags.WheeledUp))
        {
            delta = -WheelLines;
            return true;
        }

        if (flags.HasFlag(MouseFlags.WheeledDown))
        {
            delta = WheelLines;
            return true;
        }

        delta = 0;
        return false;
    }

    internal static int ClampViewportY(int requestedY, int contentRows, int viewportRows) =>
        Math.Clamp(requestedY, 0, Math.Max(0, contentRows - viewportRows));
}
