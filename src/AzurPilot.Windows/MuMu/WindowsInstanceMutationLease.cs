using AzurPilot.Core.Failures;
using AzurPilot.Core.MuMu;
using AzurPilot.Core.Configuration;

namespace AzurPilot.Windows.MuMu;

/// <summary>Межпроцессная аренда exact identity в пределах Windows-пользователя, включая разные сессии.</summary>
/// <remarks>
/// Открытый handle с FileShare.None удерживается через всю orchestration. Он не привязан к потоку,
/// закрывается при падении процесса и не наследуется дочерними процессами. Пустой файл не удаляется:
/// удаление создало бы гонку между освобождением и повторным открытием. Вложенные сценарии вызывают
/// private core методы, а не приобретают аренду повторно. Разные identity используют разные файлы.
/// </remarks>
public static class WindowsInstanceMutationLease
{
    /// <summary>Пытается немедленно занять target; очередь и скрытые повторы отсутствуют.</summary>
    public static ApplicationResult<FileStream> TryAcquire(MuMuInstanceId instanceId)
    {
        if (!MuMuInstanceValue.IsValid(instanceId.ToString()))
        {
            return ApplicationResult<FileStream>.Failure(new ApplicationFailure
            {
                Code = ApplicationFailure.InvalidArgument,
                Message = "Межпроцессная аренда требует каноническую MuMu identity.",
            });
        }

        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!Path.IsPathFullyQualified(localData))
        {
            return Unavailable();
        }

        string directory = Path.Combine(localData, "AzurPilot", "mutation-locks");
        try
        {
            _ = Directory.CreateDirectory(directory);
            return ApplicationResult<FileStream>.Success(new FileStream(
                Path.Combine(directory, instanceId.Index + ".lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33)
        {
            return ApplicationResult<FileStream>.Failure(new ApplicationFailure
            {
                Code = ApplicationFailure.TargetBusy,
                Message = "Другой процесс выполняет mutation выбранного экземпляра.",
                IsRetryable = true,
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Unavailable();
        }
    }

    private static ApplicationResult<FileStream> Unavailable() => ApplicationResult<FileStream>.Failure(new ApplicationFailure
    {
        Code = ApplicationFailure.MutationCoordinationUnavailable,
        Message = "Межпроцессная защита недоступна. Изменения не выполнялись.",
    });
}
