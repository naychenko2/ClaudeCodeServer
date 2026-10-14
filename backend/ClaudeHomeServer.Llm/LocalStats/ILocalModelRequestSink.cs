using ClaudeHomeServer.Services.Llm.Gateway;

namespace ClaudeHomeServer.Services.Llm;

// Приёмник метрик локальной модели: шлюз LLM (LlmGatewayEndpoints) отводит ему копию
// SSE-потока каждого запроса хода — моменты «запрос начался», «пришёл первый токен» и
// итог RequestUsage по завершении. Реализацию (счётчик хода) делает Main в шаге 19;
// без зарегистрированной реализации шлюз не меняет поведения (GetService → null).
//
// requestId — уникальный идентификатор HTTP-запроса (выдаёт шлюз, Guid "N"): параллельные
// запросы одного хода (сабагенты, боковые запросы CLI) имеют разный requestId, даже если
// turnId у них один (turnId — один на процесс CLI). turnId — для привязки к сессии
// (ResolveSession → sessionId/ownerId).
public interface ILocalModelRequestSink
{
    // Запрос начался: заголовки ответа получены.
    void OnRequestStarted(string requestId, string turnId);

    // Пришёл первый токен (первая content_block_delta).
    void OnFirstToken(string requestId, string turnId);

    // Запрос завершился: итог RequestUsage (токены, TTFT, скорость, кэш).
    void OnRequestCompleted(string requestId, string turnId, RequestUsage usage);

    // M5: приращение выходных токенов запроса во время стриминга (≈1 на content_block_delta).
    // cumulativeTokens — накопленный счётчик токенов ЭТОГО запроса (не сессии).
    // Счётчик хода суммирует по всем запросам сессии и считает скользящую скорость.
    void OnTokenCount(string requestId, string turnId, int cumulativeTokens);

    // Подсказочные заголовки CLI (GatewayHintHeaders, ADR-029): класс запроса, тип агента,
    // компакция — бесплатная атрибуция для разреза «основной ход / субагенты / компакция».
    // Default-реализация пуста: приёмник, которому подсказки не нужны, не переопределяет.
    // Вызывается шлюзом сразу после OnRequestStarted, только когда наблюдатель создан.
    void OnRequestHints(string requestId, string turnId, GatewayHintHeaders hints) { }
}
