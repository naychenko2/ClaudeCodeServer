using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Integration;

/// <summary>
/// Инструмент context_list (материалы контекста чата, A4): гоняем живой node-сервер против
/// заглушки бэкенда — из C# не видно ни маршрута запроса, ни того, что тул делает с записью
/// «не найден» (её нельзя отдавать модели адресом: она пойдёт читать и получит отказ).
/// </summary>
[Trait("Category", "Integration")]
public class WorkspaceContextListTests
{
    // Латиница не для красоты: id уезжает в HTTP-заголовок X-Caller-Session-Id, а не-ASCII
    // значение undici отправить не может (в проде это GUID)
    private const string SessionId = "session-1";
    private const string ProjectId = "project-1";

    private static string? FindServerPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "mcp", "workspace-server", "index.js");
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

    // TOCTOU между пробой порта и bind'ом — ретраим с новым портом (паттерн
    // McpTasksPersonalScopeTests); пять отказов подряд — среда без HttpListener.
    private static HttpListener StartHttpListenerWithRetry(out int port)
    {
        for (var attempt = 1; ; attempt++)
        {
            var candidate = FreeTcpPort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{candidate}/");
            try
            {
                listener.Start();
                port = candidate;
                return listener;
            }
            catch (HttpListenerException ex)
            {
                listener.Close();
                if (attempt >= 5) Skip.If(true, $"HttpListener недоступен: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Один вызов context_list. Возвращает (текст модели, isError, пути запросов,
    /// значения заголовка X-Caller-Session-Id).
    /// </summary>
    private static (string Text, bool IsError, List<string> Paths, List<string?> CallerHeaders)? CallContextList(
        string responseJson, string chatContextEnv = "1")
    {
        var serverPath = FindServerPath();
        Skip.If(serverPath is null, "mcp/workspace-server/index.js не найден");

        using var listener = StartHttpListenerWithRetry(out var port);

        var paths = new List<string>();
        var callerHeaders = new List<string?>();
        using var stop = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch (Exception) { return; }   // listener закрыт по выходу из теста

                lock (paths)
                {
                    paths.Add($"{ctx.Request.HttpMethod} {ctx.Request.Url?.PathAndQuery}");
                    callerHeaders.Add(ctx.Request.Headers["X-Caller-Session-Id"]);
                }
                var bytes = Encoding.UTF8.GetBytes(responseJson);
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
        psi.Environment["WORKSPACE_API_URL"] = $"http://localhost:{port}";
        psi.Environment["WORKSPACE_API_TOKEN"] = "test";
        psi.Environment["WORKSPACE_PROJECT_ID"] = ProjectId;
        psi.Environment["WORKSPACE_SELF_SESSION_ID"] = SessionId;
        psi.Environment["WORKSPACE_SECTIONS"] = "projects,files,knowledge,search";
        psi.Environment["WORKSPACE_CHAT_CONTEXT"] = chatContextEnv;

        Process? proc;
        try { proc = Process.Start(psi); }
        catch (Exception ex) { Skip.If(true, $"node недоступен: {ex.Message}"); return null; }
        Skip.If(proc is null, "не удалось запустить node");

        using (proc!)
        {
            proc.StandardInput.WriteLine(
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"context_list","arguments":{}}}""");
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
                    [.. paths],
                    [.. callerHeaders]);
        }
    }

    [SkippableFact]
    public void ContextList_ОтдаётСоставИСпрашиваетСессиюЗаголовком()
    {
        var call = CallContextList($$"""
            {"sessionId":"{{SessionId}}","projectId":"{{ProjectId}}","entries":[
              {"type":"file","id":"docs/readme.md","title":"README","missing":false},
              {"type":"task","id":"t1","title":null,"missing":false},
              {"type":"url","id":"https://example.com","title":null,"missing":false}]}
            """);
        if (call is not { } r) return;   // скип отработал внутри

        r.IsError.Should().BeFalse(r.Text);
        r.Paths.Should().ContainSingle().Which.Should().Be("GET /api/mcp/session-context",
            "сессия не параметр — её берёт бэкенд из заголовка");
        r.CallerHeaders.Should().AllBe(SessionId, "тул адресован СВОЕМУ чату");

        r.Text.Should().Contain(ProjectId, "projectId сессии нужен модели для files_read/tasks_get");
        r.Text.Should().Contain("docs/readme.md").And.Contain("README");
        // Подсказка «чем раскрыть» — по типу записи
        r.Text.Should().Contain("files_read").And.Contain("tasks_get").And.Contain("WebFetch");
    }

    [SkippableFact]
    public void ContextList_БитаяЗапись_УходитПредупреждениемАНеАдресом()
    {
        var call = CallContextList($$"""
            {"sessionId":"{{SessionId}}","projectId":"{{ProjectId}}","entries":[
              {"type":"file","id":"docs/gone.md","title":null,"missing":true},
              {"type":"file","id":"docs/live.md","title":null,"missing":false}]}
            """);
        if (call is not { } r) return;

        r.IsError.Should().BeFalse(r.Text);
        using var doc = JsonDocument.Parse(r.Text);
        var entries = doc.RootElement.GetProperty("entries");
        entries.GetArrayLength().Should().Be(1, "битый адрес в состав не отдаётся");
        entries[0].GetProperty("id").GetString().Should().Be("docs/live.md");
        var warnings = doc.RootElement.GetProperty("warnings");
        warnings.GetArrayLength().Should().Be(1);
        warnings[0].GetString().Should().Contain("не найден").And.Contain("docs/gone.md",
            "молчать об отвалившемся материале нельзя — человек добавлял его осознанно");
    }

    [SkippableFact]
    public void ContextList_БезФлагаВладельца_ВызовОтклоняетсяБезПоходаНаБэкенд()
    {
        // Defense-in-depth: инструмента нет в tools/list, но и прямой вызов не исполняется
        var call = CallContextList("""{"projectId":null,"entries":[]}""", chatContextEnv: "0");
        if (call is not { } r) return;

        r.IsError.Should().BeTrue("фича выключена у владельца");
        r.Paths.Should().BeEmpty("до бэкенда дело доходить не должно");
    }
}
