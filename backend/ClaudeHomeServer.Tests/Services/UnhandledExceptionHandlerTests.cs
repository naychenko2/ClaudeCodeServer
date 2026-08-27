using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.Http;
using ClaudeHomeServer.Telemetry;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Тесты последнего рубежа пайплайна (<see cref="UnhandledExceptionHandler"/>).
///
/// Главный из них — <see cref="LogFields_PassPiiAllowlist"/>: структурный лог осмысленен
/// ровно настолько, насколько его имена проходят <see cref="PiiRules"/>. Дропнутое имя
/// не ломает ни сборку, ни тесты — оно тихо превращает боевую запись в «Необработанное
/// исключение на {Method} {Route}», то есть возвращает нас к тому, ради чего обработчик
/// и заводился.
/// </summary>
public class UnhandledExceptionHandlerTests
{
    private sealed record Entry(string Message, Dictionary<string, object?> State, Exception? Exception);

    private sealed class CapturingLogger(List<Entry> sink) : ILogger<UnhandledExceptionHandler>
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null!;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var fields = new Dictionary<string, object?>();
            if (state is IReadOnlyList<KeyValuePair<string, object?>> pairs)
                foreach (var p in pairs) fields[p.Key] = p.Value;
            sink.Add(new Entry(formatter(state, exception), fields, exception));
        }
    }

    private static (Entry Entry, bool Handled, HttpContext Context) Run(Exception exception)
    {
        var sink = new List<Entry>();
        var handler = new UnhandledExceptionHandler(new CapturingLogger(sink));
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Response.Body = new MemoryStream();

        var handled = handler.TryHandleAsync(context, exception, CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();

        return (sink.Should().ContainSingle().Subject, handled, context);
    }

    private static Exception Catch(Action act)
    {
        try { act(); return null!; }
        catch (Exception ex) { return ex; }
    }

    private static Exception Thrown()
    {
        try { ThrowHere(); return null!; }
        catch (Exception ex) { return ex; }
    }

    private static void ThrowHere() => throw new ArgumentException("Элемент с таким ключом уже добавлен");

    [Fact]
    public void LogFields_PassPiiAllowlist()
    {
        var (entry, _, _) = Run(Thrown());

        // Имена берём из САМОГО шаблона: переименовали параметр в коде — тест увидит
        var template = entry.State["{OriginalFormat}"]?.ToString();
        var names = Regex.Matches(template ?? "", @"\{([A-Za-z_][A-Za-z0-9_.]*)\}")
            .Select(m => m.Groups[1].Value).ToList();

        names.Should().NotBeEmpty();
        foreach (var name in names)
            PiiRules.Classify(name).Should().Be(PiiAction.Keep,
                $"поле {{{name}}} обработчика обязано доезжать до SigNoz — иначе разбор падения "
                + "снова упрётся в сообщение без подробностей");
    }

    [Fact]
    public void Callsite_PointsAtOwnFrame_NotLibraryThrower()
    {
        // Исключение бросает БИБЛИОТЕКА (Encoding.GetBytes), а виноват наш вызов.
        // Ровно этот перекос и делал TargetSite бесполезным: он назвал бы Encoding.
        var (entry, _, _) = Run(Catch(() => PiiRules.ComputeHash(null!)));

        var callsite = entry.State["Callsite"]?.ToString();
        callsite.Should().Contain($"{nameof(PiiRules)}.{nameof(PiiRules.ComputeHash)}",
            "нужен ближайший СВОЙ кадр — он называет виноватую строку");
        callsite.Should().NotContain("\\", "путь к файлу в лог не идёт — только «Тип.Метод:строка»");
        entry.Exception.Should().NotBeNull("в локальный лог исключение уходит целиком");
    }

    [Fact]
    public void Callsite_FallsBack_WhenNoOwnFrame()
    {
        // Кадров продукта в стеке нет вовсе (исключение целиком из чужого кода) —
        // тогда честный фолбэк на место броска, а не пустая строка.
        var (entry, _, _) = Run(Thrown());

        entry.State["Callsite"]?.ToString().Should().EndWith(nameof(ThrowHere));
        entry.State["ErrorType"].Should().Be(typeof(ArgumentException).FullName);
    }

    [Fact]
    public void Callsite_UnwrapsInnerException()
    {
        var (entry, _, _) = Run(new InvalidOperationException("обёртка",
            Catch(() => PiiRules.ComputeHash(null!))));

        entry.State["Callsite"]?.ToString().Should().Contain(nameof(PiiRules.ComputeHash),
            "внешний кадр показывает место перехвата, а причина — во внутреннем");
    }

    [Fact]
    public void Response_Is500WithTraceId()
    {
        var (_, handled, context) = Run(Thrown());

        handled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);

        context.Response.Body.Position = 0;
        var body = new StreamReader(context.Response.Body).ReadToEnd();
        body.Should().Contain("traceId");
        body.Should().NotContain("уже добавлен", "текст исключения наружу не отдаём");
    }
}
