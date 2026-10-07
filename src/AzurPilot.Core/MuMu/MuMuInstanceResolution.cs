using AzurPilot.Core.Failures;

namespace AzurPilot.Core.MuMu;

/// <summary>
/// Результат разрешения выбранного Android-экземпляра MuMu: либо выбранный экземпляр, либо ожидаемый
/// отказ выбора.
/// </summary>
/// <remarks>
/// <para>
/// Экземпляры создаются фабриками <see cref="Resolved"/> и <see cref="Failed"/>: разрешение либо даёт
/// экземпляр, либо объясняет отказ значением, а не исключением.
/// </para>
/// <para>
/// <see cref="IsResolved"/> и <see cref="IsFailed"/> — взаимодополняющие признаки: отказ всегда несёт
/// <see cref="Failure"/>, успешное разрешение — <see cref="Instance"/>.
/// </para>
/// </remarks>
/// <param name="Instance">Выбранный экземпляр; для отказа — отсутствует.</param>
/// <param name="Failure">Ожидаемый отказ выбора; для успешного разрешения — отсутствует.</param>
public sealed record MuMuInstanceResolution(MuMuInstance? Instance, ApplicationFailure? Failure)
{
    /// <summary>Создаёт успешное разрешение выбранного экземпляра.</summary>
    /// <param name="instance">Выбранный экземпляр MuMu.</param>
    /// <returns>Разрешение с выбранным экземпляром.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="instance"/> равен <see langword="null"/>.</exception>
    public static MuMuInstanceResolution Resolved(MuMuInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return new MuMuInstanceResolution(instance, null);
    }

    /// <summary>Создаёт разрешение, завершившееся ожидаемым отказом.</summary>
    /// <param name="failure">Ожидаемый отказ выбора экземпляра.</param>
    /// <returns>Разрешение с отказом.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="failure"/> равен <see langword="null"/>.</exception>
    public static MuMuInstanceResolution Failed(ApplicationFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new MuMuInstanceResolution(null, failure);
    }

    /// <summary>Признак успешного разрешения выбранного экземпляра.</summary>
    public bool IsResolved => Instance is not null;

    /// <summary>Признак того, что разрешение завершилось ожидаемым отказом.</summary>
    public bool IsFailed => Instance is null;
}
