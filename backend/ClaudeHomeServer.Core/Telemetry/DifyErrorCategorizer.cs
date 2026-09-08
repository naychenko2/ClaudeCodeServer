using System.Globalization;
using System.Net;

namespace ClaudeHomeServer.Core.Telemetry;

/// <summary>
/// Классификация ошибок Dify-синхронизации для метрики DifySyncErrors.
/// Чистая функция: exception → строка-reason (401, 404, 429, конкретный HTTP-код, timeout, other).
/// Перенесён из ClaudeHomeServer.Telemetry.DifyErrorCategorizer (Main).
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
                { } code => ((int)code).ToString(CultureInfo.InvariantCulture),
                null => "other",
            };
        }

        return "other";
    }
}
