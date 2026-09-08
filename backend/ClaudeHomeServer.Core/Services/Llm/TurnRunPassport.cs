using System.Text.Json.Serialization;

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
    DateTime RecordedAt)
{
    /// <summary>Ход не состоялся — человек увидел ошибку вместо ответа.</summary>
    [JsonIgnore]
    public bool Failed => Outcome is "failed" or "egress_down" or "crashed";
}

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
