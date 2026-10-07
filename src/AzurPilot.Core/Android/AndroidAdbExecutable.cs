namespace AzurPilot.Core.Android;

/// <summary>
/// Обнаруженный исполняемый файл ADB, которым адресуется Android-устройство.
/// </summary>
/// <remarks>
/// <para>
/// Тип описывает обнаруженный executable как данные: путь и bounded evidence версии. Core не знает, где
/// именно лежит bundled ADB установки MuMuPlayer, не проверяет существование пути и не подставляет
/// значений по умолчанию — это ответственность реализации <see cref="IAndroidHost"/>.
/// </para>
/// <para>
/// <see cref="Path"/> — machine-specific данные конкретной машины: он не попадает ни в structured
/// details отказов, ни в логи. Для диагностики достаточно <see cref="VersionEvidence"/>, то есть
/// ограниченной версии, которую сообщил сам executable.
/// </para>
/// <para>
/// Версия не является allowlist-ом: она сообщается как evidence и не разрешает и не запрещает работу с
/// executable.
/// </para>
/// </remarks>
/// <param name="Path">Путь обнаруженного исполняемого файла ADB.</param>
/// <param name="VersionEvidence">Bounded evidence версии ADB, пригодный для диагностики.</param>
public sealed record AndroidAdbExecutable(string Path, string VersionEvidence);
