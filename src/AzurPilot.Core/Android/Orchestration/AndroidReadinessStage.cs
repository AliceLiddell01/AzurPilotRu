namespace AzurPilot.Core.Android.Orchestration;

/// <summary>Фактически начатый этап подготовки Android, без искусственного процента готовности.</summary>
public enum AndroidReadinessStage
{
    /// <summary>Наблюдение и при необходимости подключение точного transport.</summary>
    Transport,
    /// <summary>Ожидание доступного shell и завершённой загрузки Android.</summary>
    Boot,
}
