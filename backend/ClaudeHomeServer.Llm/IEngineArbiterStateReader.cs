namespace ClaudeHomeServer.Services.Llm;

// Читатель состояния GPU-арбитра для именованного движка (GET /state): контракт
// LocalEngineStateClient (Б3a). Его consumers: дорожка статистики (Б3b — статус
// «уступила» при молчащем /metrics) и блок «Дежурство» (Б3c).
// null — у движка нет `StateUrl` (арбитра нет) или сам движок неизвестен.
// Исключение — только явная отмена вызывающего (`ct`); всё остальное — снимок с reachable.
public interface IEngineArbiterStateReader
{
    Task<EngineArbiterSnapshot?> GetAsync(string engineName, CancellationToken ct = default);
}
