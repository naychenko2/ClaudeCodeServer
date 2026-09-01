using System.Globalization;
using System.Net;

namespace ClaudeHomeServer.Telemetry;

/// <summary>
/// Классификация ошибок Dify-синхронизации для метрики <see cref="ServerMetrics.DifySyncErrors"/>.
/// Чистая функция: exception → строка-reason (401, 404, 429, конкретный HTTP-код, timeout, other).
/// Вынесена отдельно, т.к. KnowledgeService не мокается (методы не virtual), а логика
/// классификации — единственная нетривиальная часть catch-блока.
/// </summary>
public static class DifyErrorCategorizer
{
    public static string Categorize(Exception ex)
    {
        // TaskCanceledException наследует от OperationCanceledException — HttpClient
        // кидает именно его при таймауте
        if (ex is OperationCanceledException)
            return "timeout";

        if (ex is HttpRequestException hre)
        {
            // HttpRequestException может оборачивать таймаут (InnerException == TaskCanceledException)
            if (hre.InnerException is OperationCanceledException)
                return "timeout";

            return hre.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "401",
                HttpStatusCode.NotFound => "404",
                HttpStatusCode.TooManyRequests => "429",
                // С .NET 5+ EnsureSuccessStatusCode кладёт реальный код ответа в StatusCode
                // для ЛЮБОГО неуспешного статуса — не сваливать его в "other", иначе теряется
                // причина отказа (500/502/400…), которая уже есть в исключении.
                // null — сетевой обрыв без ответа сервера (DNS, refused, TLS) — это и есть "other".
                { } code => ((int)code).ToString(CultureInfo.InvariantCulture),
                null => "other",
            };
        }

        return "other";
    }
}
