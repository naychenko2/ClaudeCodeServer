using System.Diagnostics;
using ClaudeHomeServer.Filters;

namespace ClaudeHomeServer.Services.Mcp;

/// <summary>
/// Логирование вызовов продуктовых MCP-серверов к бэкенду. Запрос узнаётся по заголовку
/// <see cref="DenyOnDelegatedTurnAttribute.CallerHeader"/> (его шлёт общий api() каждого сервера);
/// имя инструмента — по <see cref="ToolHeader"/>. Обычные запросы фронта проходят мимо.
///
/// Успешные вызовы пишутся в Debug (в норме их много, шум в логе не нужен), отказы — в Warning
/// с кодом и длительностью: по ним видно и «инструмент отвалился», и «бэкенд лежал».
/// Агрегация — в <see cref="McpCallLog"/> для GET /api/mcp/calls.
/// </summary>
public static class McpCallLogMiddleware
{
    /// <summary>Имя вызванного инструмента; ставит MCP-сервер на каждый запрос к бэкенду.</summary>
    public const string ToolHeader = "X-Mcp-Tool";

    public static IApplicationBuilder UseMcpCallLog(this IApplicationBuilder app) =>
        app.UseWhen(
            ctx => ctx.Request.Headers.ContainsKey(DenyOnDelegatedTurnAttribute.CallerHeader),
            branch => branch.Use(Invoke));

    private static async Task Invoke(HttpContext ctx, RequestDelegate next)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await next(ctx);
        }
        finally
        {
            sw.Stop();
            // Инструмент не назвался (старая версия сервера в песочнице, чужой клиент с тем же
            // заголовком) — учитываем по пути, чтобы вызов не потерялся вовсе
            var tool = ctx.Request.Headers[ToolHeader].FirstOrDefault() is { Length: > 0 } t
                ? t : $"(без имени) {ctx.Request.Path}";
            var sessionId = ctx.Request.Headers[DenyOnDelegatedTurnAttribute.CallerHeader].FirstOrDefault();
            var status = ctx.Response.StatusCode;

            ctx.RequestServices.GetService<McpCallLog>()
                ?.Record(tool, sessionId, ctx.Request.Path, status, sw.ElapsedMilliseconds);

            // Ранний return здесь недопустим — это тело finally
            var log = ctx.RequestServices.GetService<ILogger<McpCallLog>>();
            if (log is not null && status >= 400)
                log.LogWarning("MCP {Tool} → {Status} за {Ms} мс (сессия {Session}, {Method} {Path})",
                    tool, status, sw.ElapsedMilliseconds, sessionId, ctx.Request.Method, ctx.Request.Path);
            else
                log?.LogDebug("MCP {Tool} → {Status} за {Ms} мс (сессия {Session})",
                    tool, status, sw.ElapsedMilliseconds, sessionId);
        }
    }
}
