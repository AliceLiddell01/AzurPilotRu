namespace AzurPilot.Core.Android;

/// <summary>
/// Наблюдение процессов пакета на точном endpoint-е.
/// </summary>
/// <remarks>
/// <para>
/// Наблюдение отвечает на вопрос «есть ли процесс этого пакета», а не «что сейчас на экране»: наличие
/// процесса не означает foreground. Поэтому наличие процесса и наблюдение foreground — разные факты и
/// разные типы.
/// </para>
/// <para>
/// <see cref="ProcessIds"/> содержит только идентификаторы процессов запрошенного пакета и остаётся
/// <see langword="null"/>, если запрос не дал распознанного ответа: «не удалось спросить» не выдаётся за
/// «процессов нет». Пустой список означает доказанное отсутствие процессов.
/// </para>
/// <para>
/// <see cref="ProcessCount"/> согласован с <see cref="ProcessIds"/>: при <see langword="null"/> он равен
/// нулю, иначе — длине списка. Полный список процессов устройства сюда не попадает.
/// </para>
/// <para>
/// <see cref="Evidence"/> — bounded сведения о наблюдении: полный вывод ADB в них не попадает.
/// </para>
/// </remarks>
/// <param name="ProcessCount">Число наблюдённых процессов пакета.</param>
/// <param name="ProcessIds">
/// Идентификаторы процессов пакета либо <see langword="null"/>, если наблюдение не доказано.
/// </param>
/// <param name="Evidence">Bounded evidence наблюдения, пригодный для диагностики.</param>
public sealed record AndroidProcessObservation(
    int ProcessCount,
    IReadOnlyList<int>? ProcessIds,
    string Evidence);
