using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Integration;

/// <summary>
/// Доступ к ЛИЧНЫМ задачам владельца из чата, привязанного к проекту.
///
/// Контекст проекта существует, чтобы модель не лезла в ЧУЖИЕ проекты. Личные задачи
/// пользователя чужими не являются — владение всё равно проверяет бэкенд по токену. Раньше же
/// проверка отбрасывала любую задачу без projectId: та читалась в одном чате, но tasks_update
/// по ней падал «недоступна в этом контексте», а scope=all обещал «все задачи пользователя
/// и личные» и отдавал один текущий проект. Снаружи это выглядело как отвалившийся инструмент.
///
/// Гоняем живой node против заглушки бэкенда: из C# этот гейт не виден.
/// </summary>
[Trait("Category", "Integration")]
public class McpTasksPersonalScopeTests
{
    private const string ProjectId = "проект-чата";
    private const string OtherProjectId = "чужой-проект";

    private static string? FindServerPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "mcp", "tasks-server", "index.js");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static int FreeTcpPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>
    /// Один tools/call в чате проекта <see cref="ProjectId"/>. Заглушка отвечает по пути запроса.
    /// Возвращает (текст модели, isError, пути запросов, дошедших до бэкенда).
    /// </summary>
    private static (string Text, bool IsError, List<string> Paths)? CallTool(
        string toolCallJson, Func<string, string> respondByPath)
    {
        var serverPath = FindServerPath();
        Skip.If(serverPath is null, "mcp/tasks-server/index.js не найден");

        var port = FreeTcpPort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        try { listener.Start(); }
        catch (HttpListenerException ex) { Skip.If(true, $"HttpListener недоступен: {ex.Message}"); }

        var paths = new List<string>();
        using var stop = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch (Exception) { return; }   // listener закрыт по выходу из теста

                var path = ctx.Request.Url?.PathAndQuery ?? "";
                lock (paths) paths.Add($"{ctx.Request.HttpMethod} {path}");
                var bytes = Encoding.UTF8.GetBytes(respondByPath(path));
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });

        var psi = new ProcessStartInfo
        {
            FileName = "node",
            ArgumentList = { serverPath! },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        psi.Environment["TASKS_API_URL"] = $"http://localhost:{port}";
        psi.Environment["TASKS_API_TOKEN"] = "test";
        psi.Environment["TASKS_SESSION_ID"] = "session-1";
        // Ключевое: чат ПРИВЯЗАН к проекту — именно тут раньше срабатывал запрет
        psi.Environment["TASKS_PROJECT_ID"] = ProjectId;

        Process? proc;
        try { proc = Process.Start(psi); }
        catch (Exception ex) { Skip.If(true, $"node недоступен: {ex.Message}"); return null; }
        Skip.If(proc is null, "не удалось запустить node");

        using (proc!)
        {
            proc.StandardInput.WriteLine(toolCallJson);
            proc.StandardInput.Flush();

            var line = proc.StandardOutput.ReadLine();
            proc.StandardInput.Close();
            if (!proc.WaitForExit(20_000)) proc.Kill(entireProcessTree: true);
            stop.Cancel();

            line.Should().NotBeNullOrWhiteSpace("сервер обязан ответить на tools/call");
            using var doc = JsonDocument.Parse(line!);
            var result = doc.RootElement.GetProperty("result");
            lock (paths)
                return (
                    result.GetProperty("content")[0].GetProperty("text").GetString() ?? "",
                    result.TryGetProperty("isError", out var e) && e.GetBoolean(),
                    [.. paths]);
        }
    }

    // Задача без projectId — личная задача владельца
    private static string PersonalTask(string id) =>
        $$"""{"id":"{{id}}","title":"личное дело","projectId":null,"status":"todo"}""";

    [SkippableFact]
    public void ЛичнуюЗадачу_МожноМенятьИзПроектногоЧата()
    {
        var call = CallTool(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"tasks_update","arguments":{"id":"t1","status":"done"}}}""",
            _ => PersonalTask("t1"));
        if (call is not { } r) return;   // скип отработал внутри

        r.IsError.Should().BeFalse($"личная задача владельца доступна из любого его чата: {r.Text}");
        r.Paths.Should().Contain(p => p.StartsWith("PUT "), "обновление обязано дойти до бэкенда");
    }

    [SkippableFact]
    public void ЗадачаЧужогоПроекта_ПоПрежнемуНедоступна()
    {
        var call = CallTool(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"tasks_update","arguments":{"id":"t2","status":"done"}}}""",
            _ => $$"""{"id":"t2","title":"чужое","projectId":"{{OtherProjectId}}","status":"todo"}""");
        if (call is not { } r) return;

        r.IsError.Should().BeTrue("контекст проекта по-прежнему закрывает ЧУЖИЕ проекты");
        r.Text.Should().Contain("другому проекту");
        r.Paths.Should().NotContain(p => p.StartsWith("PUT "), "до записи дело дойти не должно");
    }

    [SkippableFact]
    public void ScopeAll_ВозвращаетИЛичныеЗадачи()
    {
        var call = CallTool(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"tasks_list","arguments":{"scope":"all"}}}""",
            _ => $$"""
            [
              {"id":"t1","title":"личное дело","projectId":null,"status":"todo"},
              {"id":"t2","title":"своё","projectId":"{{ProjectId}}","status":"todo"},
              {"id":"t3","title":"чужое","projectId":"{{OtherProjectId}}","status":"todo"}
            ]
            """);
        if (call is not { } r) return;

        r.IsError.Should().BeFalse();
        r.Text.Should().Contain("t1", "scope=all обещает в описании и личные задачи тоже");
        r.Text.Should().Contain("t2");
        r.Text.Should().NotContain("t3", "чужой проект в выдачу не попадает");
    }
}
