using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ClaudeHomeServer.Services.AudioEditor.Jobs;

// Контракты исполнителя задач звука (ADR-021 §2, как ADR-017 §4 у картинок): котировка → запуск
// строго по quoteId → события в группу владельца. Отказ — результат с кодом и внятным текстом, а не
// исключение: ручке и тулсету нужно различать «котировка устарела», «слишком много задач» и
// «поставщик недоступен».

[JsonConverter(typeof(JsonStringEnumConverter<AudioEditInitiator>))]
public enum AudioEditInitiator { Human, Agent }

[JsonConverter(typeof(JsonStringEnumConverter<AudioEditJobStatus>))]
public enum AudioEditJobStatus { Queued, Running, Downloading, Completed, Failed, Cancelled }

public static class AudioEditErrorCodes
{
    public const string InvalidRequest = "invalid_request";
    public const string ProviderUnavailable = "provider_unavailable";
    public const string QuoteNotFound = "quote_not_found";
    public const string TooManyJobs = "too_many_jobs";
    public const string HeavyBusy = "heavy_busy";
}

public sealed record AudioEditCallResult<T>(T? Value, string? ErrorCode, string? Error)
{
    public static AudioEditCallResult<T> Ok(T value) => new(value, null, null);
    public static AudioEditCallResult<T> Fail(string code, string error) => new(default, code, error);
}

// Котировка. Mode — AudioModes.*; Operation, Provider, Model, Count, Fields не заданы — берутся по
// цепочке «настройки нити → префы режима → умолчание каталога» (AudioPrefsResolver). ThreadId вместе с
// SessionId — нить, чьи настройки идут первыми. VoiceKind — вид голоса запроса: по нему сверяется
// модель и подбирается сосед при отказе. Text и DurationSec — для цены за символы и секунды
public sealed record AudioQuoteRequest(
    string Mode,
    string? Operation = null,
    string? Provider = null,
    string? Model = null,
    int? Count = null,
    AudioVoiceKind? VoiceKind = null,
    string? SessionId = null,
    string? ThreadId = null,
    string? Text = null,
    int? DurationSec = null,
    JsonObject? Fields = null);

// Цена: Amount в единицах Unit (AudioPriceUnits.*), null — станет известна после запуска.
// Source — AudioEstimateSources.*, Eta — секунды на все варианты, QueueLength — очередь поставщика
public sealed record AudioPrice(double? Amount, string Unit, bool Approx, string Source, int? Eta, int? QueueLength);

public sealed record AudioQuoteDto(
    string QuoteId,
    string Mode,
    AudioOp Op,
    string Provider,
    string Model,
    int Count,
    AudioVoiceKind? VoiceKind,
    AudioPrice Price,
    string License,
    bool Heavy,
    DateTime ExpiresAt);

// Запуск по котировке: содержимое операции (текст, слова, входы). Params поверх полей из цепочки —
// частные параметры модели. BaseVersionId — версия нити, от которой запускают (null — текущая)
public sealed record AudioJobInput(
    string QuoteId,
    string? SessionId = null,
    string? ThreadId = null,
    string? BaseVersionId = null,
    AudioEditInitiator Initiator = AudioEditInitiator.Human,
    string? Text = null,
    string? Prompt = null,
    string? Lyrics = null,
    string? Language = null,
    int? DurationSec = null,
    double? StartSec = null,
    double? EndSec = null,
    JsonObject? Params = null,
    AudioBytes? Source = null,
    AudioBytes? Reference = null,
    IReadOnlyList<AudioBytes>? Clips = null,
    byte[]? VoiceModel = null,
    byte[]? VoiceIndex = null,
    long? Seed = null);

public sealed record AudioJobCreatedDto(string JobId);

// Файлы варианта — роли и пути от рабочей папки задачи
public sealed record AudioJobVariantDto(int Variant, IReadOnlyList<Threads.AudioVersionFile> Files);

public sealed record AudioJobDto(
    string JobId,
    string ScopeKey,
    AudioEditJobStatus Status,
    string Provider,
    string Model,
    AudioOp Op,
    int Count,
    IReadOnlyList<AudioJobVariantDto> Variants,
    AudioCost? Cost,
    AudioOutcome? Outcome,
    bool? Charged,
    string? Error,
    int? QueuePosition,
    int? EtaSeconds,
    DateTime CreatedAt,
    string? ChatSessionId,
    string? ThreadId,
    AudioEditInitiator Initiator,
    string License);
