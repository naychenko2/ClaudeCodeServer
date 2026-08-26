using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;

namespace ClaudeHomeServer.Services.Http;

/// <summary>
/// Последний рубеж пайплайна: исключение, которое не поймал никто по дороге.
///
/// Зачем понадобился. Без него необработанное исключение ловил сам Kestrel и писал
/// собственный лог «Connection id …, Request id …: An unhandled exception was thrown by the
/// application» — сообщение, из которого не видно НИ маршрута, ни точки падения. В SigNoz
/// это усугублялось санитайзером логов: <see cref="Telemetry.PiiSanitizingLogProcessor"/>
/// осознанно обнуляет <c>LogRecord.Exception</c> (в стектрейсе — абсолютные пути сборки,
/// в тексте — URL с параметрами), оставляя один <c>exception.type</c>. Разбор боевого
/// падения упирался в «System.ArgumentException где-то в приложении».
///
/// Что делает. Логирует то же событие СТРУКТУРНО, именами, которые проходят allowlist
/// <see cref="Telemetry.PiiRules"/> — маршрут (шаблон, без значений параметров), метод,
/// тип исключения, текст и точка броска. Стектрейс по-прежнему уезжает только в локальный
/// лог (исключение передаётся логгеру вторым аргументом), в OTLP его срежет санитайзер —
/// это прежнее решение, и здесь оно не пересматривается.
///
/// Точка броска (<c>Callsite</c>) — компромисс вместо стектрейса: <c>MethodBase</c> даёт
/// «тип.метод» без единого пути к файлу, то есть безопасен по тем же меркам, по которым
/// стектрейс небезопасен, а для поиска места падения этого обычно достаточно.
///
/// Ответ клиенту — ProblemDetails 500 без деталей: текст исключения наружу не отдаём
/// (в нём бывают внутренние пути и параметры), клиенту хватает <c>traceId</c>, по которому
/// падение находится в телеметрии.
/// </summary>
public sealed class UnhandledExceptionHandler(ILogger<UnhandledExceptionHandler> log) : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        // Шаблон маршрута («api/projects/{projectId}/services»), а не фактический путь:
        // в пути лежат идентификаторы, шаблон же — константа из кода, PII в нём нет.
        var route = context.GetEndpoint() is Microsoft.AspNetCore.Routing.RouteEndpoint endpoint
            ? endpoint.RoutePattern.RawText
            : null;

        log.LogError(exception,
            "Необработанное исключение на {Method} {Route}: {ErrorType} в {Callsite} — {Error}",
            context.Request.Method,
            route ?? "(маршрут не сопоставлен)",
            exception.GetType().FullName,
            Callsite(exception),
            exception.Message);

        // Ответ уже пошёл клиенту (стриминг, прокси дев-сервера) — переписать статус нельзя;
        // отдаём исключение дальше Kestrel'у, он оборвёт соединение. Лог выше уже написан,
        // то есть ради этого случая мы ничего не теряем.
        if (context.Response.HasStarted) return ValueTask.FromResult(false);

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        return new ValueTask<bool>(WriteProblemAsync(context, ct));
    }

    private static async Task<bool> WriteProblemAsync(HttpContext context, CancellationToken ct)
    {
        await context.Response.WriteAsJsonAsync(new
        {
            type = "https://tools.ietf.org/html/rfc9110#section-15.6.1",
            title = "Внутренняя ошибка сервера",
            status = StatusCodes.Status500InternalServerError,
            traceId = context.TraceIdentifier,
        }, ct);
        return true;
    }

    /// <summary>
    /// Место падения в НАШЕМ коде: «Тип.Метод:строка» первого кадра из сборки продукта.
    ///
    /// Почему не <c>TargetSite</c> (первая версия этого метода): исключение обычно бросает
    /// не наш код, а библиотечный. У «An item with the same key has already been added»
    /// TargetSite — это <c>Dictionary.TryInsert</c>, то есть ответ «упало в словаре», ради
    /// которого разбор и не стоило затевать. Нужен ближайший к нему СВОЙ кадр — он и
    /// называет виноватую строку.
    ///
    /// Номер строки берём, а путь к файлу — нет: путь и есть то, из-за чего санитайзер
    /// вырезает стектрейс целиком (абсолютные пути сборочной машины). «Тип.Метод:строка»
    /// однозначно указывает место и в логе выглядит одной короткой строкой.
    ///
    /// Кадры бывают недоступны (исключение без стектрейса, релизная инлайн-оптимизация) —
    /// тогда честно откатываемся к TargetSite и, если и его нет, к типу исключения.
    /// </summary>
    private static string Callsite(Exception exception)
    {
        // У обёрнутых исключений полезен внутренний кадр: внешний почти всегда указывает
        // на место перехвата, а не на причину.
        var current = exception;
        while (current.InnerException is not null) current = current.InnerException;

        var own = typeof(UnhandledExceptionHandler).Assembly;
        foreach (var frame in new StackTrace(current, fNeedFileInfo: true).GetFrames())
        {
            var method = frame.GetMethod();
            if (method?.DeclaringType?.Assembly != own) continue;
            var line = frame.GetFileLineNumber();
            return line > 0
                ? $"{method.DeclaringType.FullName}.{method.Name}:{line}"
                : $"{method.DeclaringType.FullName}.{method.Name}";
        }

        var target = current.TargetSite;
        return target?.DeclaringType is null
            ? target?.Name ?? current.GetType().Name
            : $"{target.DeclaringType.FullName}.{target.Name}";
    }
}
