using Microsoft.Extensions.Http.Logging;

namespace ClaudeHomeServer.Telemetry;

/// <summary>
/// Тихий логгер HTTP-запросов OTLP-экспортёров.
///
/// Экспортёры OTLP берут HttpClient из <c>IHttpClientFactory</c> под именами
/// <c>OtlpTraceExporter</c> / <c>OtlpMetricExporter</c> / <c>OtlpLogExporter</c>, а дефолтное
/// логирование <c>Microsoft.Extensions.Http</c> пишет КАЖДЫЙ неудачный запрос как
/// <b>Error</b> с полным стектрейсом <see cref="HttpRequestException"/>. На машине разработчика
/// коллектор поднят не всегда, а экспорт идёт по расписанию (трейсы и логи — раз в 5 секунд,
/// метрики — раз в минуту): консоль забивается красными портянками про «конечный компьютер
/// отверг запрос на подключение», в которых тонут настоящие ошибки приложения.
///
/// Недоступный коллектор — не ошибка приложения: продукт работает, теряется только телеметрия.
/// Поэтому уровень <b>Warning</b>, без стектрейса, и не чаще раза в <see cref="ReportInterval"/> —
/// сообщение остаётся сигналом, а не фоном. Логгер — синглтон на все три клиента, так что
/// интервал общий: один сбой коллектора = одна строка, а не три.
///
/// Восстановление экспорта сбрасывает троттлинг: следующий сбой будет виден сразу, а не через
/// пять минут тишины.
/// </summary>
internal sealed class OtlpExportHttpLogger : IHttpClientLogger
{
    /// <summary>Не чаще одной жалобы за этот интервал (на все экспортёры сразу).</summary>
    internal static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(5);

    private readonly ILogger _log;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();
    private DateTimeOffset? _lastReport;

    public OtlpExportHttpLogger(ILoggerFactory factory, Func<DateTimeOffset>? now = null)
    {
        // Категория своя, а не System.Net.Http.HttpClient.*: по ней видно, что речь о телеметрии,
        // и её можно приглушить отдельно от остального HTTP-логирования.
        _log = factory.CreateLogger("ClaudeHomeServer.Telemetry.OtlpExport");
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public object? LogRequestStart(HttpRequestMessage request) => null;

    public void LogRequestStop(
        object? context, HttpRequestMessage request, HttpResponseMessage response, TimeSpan elapsed)
    {
        if (response.IsSuccessStatusCode)
        {
            // Экспорт снова проходит — снимаем троттлинг.
            lock (_gate) _lastReport = null;
            return;
        }

        // Коллектор жив, но отказал (например 503 при перезапуске SigNoz) — тот же класс проблемы.
        if (ShouldReport())
            _log.LogWarning(
                "Телеметрия не уходит: OTLP-коллектор {Endpoint} ответил {Status}",
                Endpoint(request), (int)response.StatusCode);
    }

    public void LogRequestFailed(
        object? context, HttpRequestMessage request, HttpResponseMessage? response,
        Exception exception, TimeSpan elapsed)
    {
        if (!ShouldReport()) return;

        // Только сообщение первопричины: стектрейс здесь всегда один и тот же — путь через
        // HttpConnectionPool, диагностической ценности в нём нет.
        _log.LogWarning(
            "Телеметрия не уходит: OTLP-коллектор {Endpoint} недоступен ({Reason})",
            Endpoint(request), exception.GetBaseException().Message);
    }

    private static string Endpoint(HttpRequestMessage request) =>
        request.RequestUri?.GetLeftPart(UriPartial.Authority) ?? "(неизвестен)";

    private bool ShouldReport()
    {
        var now = _now();
        lock (_gate)
        {
            if (_lastReport is { } last && now - last < ReportInterval) return false;
            _lastReport = now;
            return true;
        }
    }
}
