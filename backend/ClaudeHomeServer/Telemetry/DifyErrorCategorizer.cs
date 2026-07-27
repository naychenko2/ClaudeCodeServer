using System.Net;

namespace ClaudeHomeServer.Telemetry;

/// <summary>
/// Классификация ошибок Dify-синхронизации для метрики <see cref="ServerMetrics.DifySyncErrors"/>.
/// Чистая функция: exception → строка-reason (401, 404, 429, timeout, other).
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
                _ => "other"
            };
        }

        return "other";
    }
}
