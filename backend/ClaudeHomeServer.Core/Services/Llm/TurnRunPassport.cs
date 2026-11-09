using System.Text.Json.Serialization;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Llm;

/// <summary>
/// Паспорт одного хода глазами оркестратора фолбэка: чем кончился, сколько попыток стоил и на
/// какой паре «модель × провайдер» встал.
/// </summary>
/// <param name="SessionId">чат, в котором шёл ход.</param>
/// <param name="Outcome">
/// исход: success | failed | egress_down | interrupted | cancelled | crashed.
/// Отдельный egress_down — не педантизм: он отвечает на вопрос «виноват вендор или наш канал»,
/// а именно этот вопрос при разборе суток занимает больше всего времени.
/// </param>
/// <param name="Attempts">сколько попыток доставки сделано (1 — ход прошёл с первого раза).</param>
/// <param name="Substitutions">из них смен ТИПА поставщика (шаг цепочки/сторонний провайдер).</param>
/// <param name="EgressRetries">повторов той же пары из-за лежащего выхода в сеть.</param>
/// <param name="Chain">цепочка хода — модели пресета в порядке фолбэка.</param>
/// <param name="LastErrorClass">wire-имя класса последней ошибки (rate_limit, unreachable…).</param>
/// <param name="LastError">сырой текст последней ошибки, усечённый — для глаз, не для разбора.</param>
/// <param name="CacheMissReason">
/// причина промаха prompt cache первого вызова хода (<c>diagnostics.cache_miss_reason</c>
/// из result последней попытки): <c>None</c> — префикс прочитан без промаха; null — сигнала
/// кэша не было вовсе (сторонний провайдер или ход без кэша). Метрика
/// <c>ccs.prompt_cache.turns</c> считает ходы, а сколько токенов переписано — только здесь:
/// токены в OTel не едут (C4).
/// </param>
/// <param name="CacheMissedTokens">сколько входных токенов ушло мимо кэша (<c>cache_missed_input_tokens</c>).</param>
///
/// Этап 5, шаг 2: record переехал в Core (разрыв цикла Llm ⇄ Turn через шину событий хода):
/// `TurnCompleted` в Services/Turn/TurnEvents.cs держит ссылку на тип, чтобы подписчик
/// `TurnRunLog` заменил прямой finally-вызов в `FallbackLlmSessionAdapter` единственной
/// публикацией на шине (ADR-013 §1.1). Реализация record-а и связанных тип-сводок
// (`TurnRunSummary`/`TurnOutcomeStat`/`TurnErrorClassStat`) — здесь же; класс
// `TurnRunLog` со сбором/персистом остаётся в Main.
/// </summary>
public sealed record TurnRunPassport(
    string SessionId,
    DateTime StartedAt,
    DateTime EndedAt,
    int DurationSeconds,
    string Outcome,
    string? StartModel,
    string? StartProvider,
    string? FinalModel,
    string? FinalProvider,
    int Attempts,
    int Substitutions,
    int EgressRetries,
    IReadOnlyList<string> Chain,
    string? LastErrorClass,
    string? LastError,
    int ContextTokens,
    DateTime RecordedAt,
    PromptCacheMissReason? CacheMissReason = null,
    int? CacheMissedTokens = null)
{
    /// <summary>Ход не состоялся — человек увидел ошибку вместо ответа.</summary>
    [JsonIgnore]
    public bool Failed => Outcome is "failed" or "egress_down" or "crashed";

    /// <summary>
    /// Факт внутренних повторов CLI (событие system/api_retry) за ход: сколько раз
    /// headless-сессия сама переждала 429/529 внутри процесса, прежде чем отдать ход нам.
    /// С 2.1.246 CLI дотягивает оборванный ответ и может переждать 429/529 минутами:
    /// <see cref="Attempts"/>/<see cref="EgressRetries"/> — это НАШИ повторы (цепочка),
    /// а эти поля — сколько CLI молчал сам. 0/null — повторов не было (CLI не дотягивал).
    /// </summary>
    public int CliRetries { get; init; }

    /// <summary>Суммарное время ожидания внутренних повторов CLI (мс, сумма retry_delay_ms).</summary>
    public long CliRetryWaitMs { get; init; }

    /// <summary>Последний HTTP-статус внутренних повторов CLI (529/429); null — не было.</summary>
    public string? CliRetryLastStatus { get; init; }
}

/// <summary>
/// Факт внутренних повторов CLI (system/api_retry) за ход: сколько раз headless-сессия
/// сама переждала 429/529 внутри процесса, суммарное время ожидания (мс) и последний
/// HTTP-статус. Читается адаптером для паспорта хода (TurnRunPassport.CliRetries/
/// CliRetryWaitMs/CliRetryLastStatus). 0/null — повторов не было.
/// </summary>
public record CliRetryStats(int Count, long WaitMs, string? LastStatus);

/// <summary>
/// Сводка по паспортам ходов: сколько ходов и с какой причиной встали.
/// Тип живёт рядом с `TurnRunPassport`, потому что поля ссылаются на исходный record
/// (`ByOutcome`/`ByErrorClass` — `IReadOnlyList<TurnOutcomeStat>/<TurnErrorClassStat>`).
/// Все три типа живут в Core как единая DTO-сводка.
/// </summary>
public record TurnRunSummary(
    int Turns,
    int Failed,
    IReadOnlyList<TurnOutcomeStat> ByOutcome,
    IReadOnlyList<TurnErrorClassStat> ByErrorClass);

public record TurnOutcomeStat(string Outcome, int Turns);

public record TurnErrorClassStat(string ErrorClass, int Turns);
