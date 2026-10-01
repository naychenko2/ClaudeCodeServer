namespace ClaudeHomeServer.Services.AudioEditor;

// Котировка драйвера (ADR-021 §2, как ADR-017 §4 у картинок): сумма в единицах поставщика, время и
// очередь. Драйвер без этого шва котируется по PriceHint модели
public interface IAudioQuoter
{
    // Бросает AudioEngineUnavailableException, если поставщик сейчас недоступен или не работает
    // в области запроса: котировка тогда отвечает отказом с причиной, а не цифрой
    Task<AudioEstimate> EstimateAsync(AudioModelInfo model, AudioRequest request, CancellationToken ct);

    // Ожидаемая длительность для процентов на фронте; null — неизвестно
    int? ExpectedSeconds(AudioModelInfo model, AudioRequest request);
}

public sealed class AudioEngineUnavailableException(string message) : Exception(message);

// Amount null — цена станет известна после запуска. Source: provider | catalog | unknown
public sealed record AudioEstimate(
    double? Amount,
    string Unit,
    bool Approx,
    string Source,
    int? EtaSeconds = null,
    int? QueueLength = null);

public static class AudioEstimateSources
{
    public const string Provider = "provider";
    public const string Catalog = "catalog";
    public const string Unknown = "unknown";
}
