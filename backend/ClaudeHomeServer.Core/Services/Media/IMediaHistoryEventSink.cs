using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.Media;

// Наблюдатель журнала истории медиа-генераций (ADR-030 §7): индекс в памяти получает новые
// события от писателя (ComfyRunHistory) без перечитывания файла. Необязательный: без
// наблюдателя журнал пишется, индекс просто не обновляется вживую (перечитает файл при старте).
public interface IMediaHistoryEventSink
{
    // Событие журнала (строка v:1). Idempotent по PromptId+Type: повторное событие того же
    // вида не дублирует запись.
    void Append(MediaHistoryEvent @event);
}

// Событие журнала (строка v:1): тип, время, происхождение и поля события. Params — белый
// список LocalRunParams как JsonObject (BCL, не вертикаль): индекс Main читает из него
// width/height/steps/seed/… для карточки. Card — снимок /ccs/stats (не нужен индексу, но
// несут строку для полноты).
public sealed record MediaHistoryEvent(
    string PromptId,
    string Type,
    DateTimeOffset Ts,
    LocalJobOrigin? Origin,
    string? Model,
    string? Prompt,
    JsonObject? Params,
    JsonObject? Card,
    string? Outcome,
    int? RunSeconds,
    string? Error);
