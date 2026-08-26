using ClaudeHomeServer.Filters;
using ClaudeHomeServer.Services.Mcp;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Журнал MCP-вызовов и НЕОБРАБОТАННЫЕ исключения (блокер 4 консилиума, проверка «настоящего
/// HTTP 500»). Исключение из эндпоинта разворачивается сквозь finally журнала раньше, чем хост
/// успевает выставить код ответа: без пометки сбой записывался бы дефолтным статусом 200 —
/// «успехом». Здесь это проверяется на голом пайплайне middleware, без тестового хоста:
/// диспетчер /mcp после hardened-правок свои исключения уже не выпускает, а журнал слушает
/// и маршруты /api/* всех остальных MCP-серверов.
/// </summary>
public class McpCallLogMiddlewareTests
{
    private static (IServiceProvider Services, McpCallLog Log) Build()
    {
        var log = new McpCallLog();
        var services = new ServiceCollection().AddSingleton(log).BuildServiceProvider();
        return (services, log);
    }

    private static DefaultHttpContext Context(IServiceProvider services, string path, int statusToSet)
    {
        var ctx = new DefaultHttpContext { RequestServices = services };
        ctx.Request.Headers[DenyOnDelegatedTurnAttribute.CallerHeader] = "sess-journal";
        ctx.Request.Headers[McpCallLogMiddleware.ToolHeader] = "tasks_list";
        ctx.Request.Path = path;
        ctx.Response.StatusCode = statusToSet;
        return ctx;
    }

    [Fact]
    public async Task НеобработанноеИсключение_ЗаписываетсяКак500_АНеУспех()
    {
        var (services, log) = Build();
        var app = new ApplicationBuilder(services);
        app.UseMcpCallLog();
        app.Run(_ => throw new InvalidOperationException("взрыв эндпоинта"));
        var pipeline = app.Build();
        var ctx = Context(services, "/api/projects/x/tasks", StatusCodes.Status200OK);

        // Исключение обязано пройти НАСКВОЗЬ (журнал его не глотает), но до этого пометиться
        await pipeline.Invoking(p => p(ctx)).Should().ThrowAsync<InvalidOperationException>();

        log.RecentFailures().Should().ContainSingle(f =>
            f.StatusCode == 500 && f.Tool == "tasks_list" && f.SessionId == "sess-journal",
            "исключение, разворачивающееся из эндпоинта, — это сбой, а не успех 200");
    }

    [Fact]
    public async Task ОбычныйОтказ_ПишетсяСвоимКодом()
    {
        var (services, log) = Build();
        var app = new ApplicationBuilder(services);
        app.UseMcpCallLog();
        app.Run(ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        });

        await app.Build()(Context(services, "/api/projects/x/tasks", StatusCodes.Status404NotFound));

        log.RecentFailures().Should().ContainSingle(f => f.StatusCode == 404);
    }

    [Fact]
    public async Task ОтменаКлиента_НеПишетсяКак500()
    {
        var (services, log) = Build();
        var app = new ApplicationBuilder(services);
        app.UseMcpCallLog();
        app.Run(ctx =>
        {
            ctx.RequestAborted = new CancellationToken(canceled: true);
            throw new OperationCanceledException(ctx.RequestAborted);
        });

        var ctx = Context(services, "/api/projects/x/tasks", StatusCodes.Status200OK);
        await app.Build().Invoking(p => p(ctx)).Should().ThrowAsync<OperationCanceledException>();

        log.RecentFailures().Should().BeEmpty("клиент ушёл — это не сбой инструмента");
    }

    /// <summary>
    /// Форму ключа проверяет сам McpCallLog.Record (владелец словаря), а не вызывающий путь:
    /// заголовок X-Mcp-Tool — внешний ввод без лимита длины, лимит заголовков Kestrel
    /// допускает десятки КБ, и GET /api/mcp/calls не должен раздуваться чужими строками.
    /// Составные ключи MCP-over-HTTP («widgets/initialize») обязаны выжить.
    /// </summary>
    [Fact]
    public void Record_ПроверяетФормуКлюча_НоНеТрогаетСоставныеИменаМетодов()
    {
        var log = new McpCallLog();

        // Составное имя «сервер/метод» из NameCallForLog — легитимный ключ таблицы
        var served = log.Record("widgets/initialize", "s1", "/mcp/widgets", 200, 1);
        served.Should().Be("widgets/initialize");

        // 30-КБ имя с CRLF из чужого заголовка — общая строка переполнения
        var evil = log.Record(new string('z', 30_000) + "\r\nX-Injected: 1", "s2", "/p", 200, 1);
        evil.Should().Be(McpCallLog.Overflow, "негодное по форме не становится ключом словаря");

        var tools = log.Stats().Select(s => s.Tool).ToList();
        tools.Should().Contain("widgets/initialize");
        tools.Should().NotContain(t => t.Length > 64);
    }
}
