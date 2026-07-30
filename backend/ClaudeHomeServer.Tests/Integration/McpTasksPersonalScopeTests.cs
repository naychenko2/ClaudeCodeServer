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

    // Между пробой свободного порта и bind'ом — TOCTOU-окно: параллельный тест или
    // процесс может занять порт, и тест флакал на HttpListenerException. Ретрай
    // с новым портом; пять отказов подряд — среда без HttpListener, пропуск как раньше.
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
    /// Один tools/call в чате проекта <see cref="ProjectId"/>. Заглушка отвечает по пути запроса.
    /// Возвращает (текст модели, isError, пути запросов, тела запросов).
    /// Тела нужны, чтобы проверять, ЧТО tasks_create отправил в POST (sourceSessionId и др.).
    /// </summary>
    private static (string Text, bool IsError, List<string> Paths, List<string> Bodies)? CallTool(
        string toolCallJson, Func<string, string> respondByPath, Action<ProcessStartInfo>? configureEnv = null)
    {
        var serverPath = FindServerPath();
        Skip.If(serverPath is null, "mcp/tasks-server/index.js не найден");

        using var listener = StartHttpListenerWithRetry(out var port);

        var paths = new List<string>();
        var bodies = new List<string>();
        using var stop = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch (Exception) { return; }   // listener закрыт по выходу из теста

                var path = ctx.Request.Url?.PathAndQuery ?? "";
                // Тело нужно для проверки, что tasks_create отправил в POST (sourceSessionId и т.д.)
                string bodyText = "";
                try { using var reader = new StreamReader(ctx.Request.InputStream); bodyText = await reader.ReadToEndAsync(); }
                catch { /* запрос без тела — пустая строка */ }
                lock (paths) paths.Add($"{ctx.Request.HttpMethod} {path}");
                lock (bodies) bodies.Add(bodyText);
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
        configureEnv?.Invoke(psi);

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
                    [.. paths],
                    [.. bodies]);
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

    // ─── Происхождение задачи: sourceSessionId не зависит от наличия персоны ────
    // Баг: задачи из чата БЕЗ персоны уходили без SourceSessionId → чат-исполнитель не
    // подвязывался дочерним. Чат-источник — факт «задача рождена в этом чате», он не
    // привязан к тому, кто вёл ход.

    private static string CreatedTask() =>
        """{"id":"t-new","title":"новая задача","projectId":"проект-чата","status":"todo"}""";

    [SkippableFact]
    public void tasks_create_БезПерсоны_ШлётSourceSessionId()
    {
        // Env не задаёт TASKS_SELF_PERSONA_ID (ход без персоны), но TASKS_SESSION_ID известен.
        // Раньше sourceSessionId не отправлялся — чат-исполнитель всплывал в корень.
        var call = CallTool(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"tasks_create","arguments":{"title":"новая задача"}}}""",
            _ => CreatedTask());
        if (call is not { } r) return;

        r.IsError.Should().BeFalse($"задача должна создаться: {r.Text}");
        r.Paths.Should().Contain(p => p.StartsWith("POST "), "создание обязано дойти до бэкенда");
        r.Bodies.Should().Contain(b => b.Contains("\"sourceSessionId\":\"session-1\""),
            "чат-источник шлём всегда, когда известен TASKS_SESSION_ID, — даже без персоны");
        r.Bodies.Should().NotContain(b => b.Contains("createdByPersonaId"),
            "без персоны-постановщика поле не отправляем");
    }

    [SkippableFact]
    public void tasks_create_СПерсоной_ШлётИПостановщикаИИсточник()
    {
        // Регресс: поведение задач от персон не изменилось — createdByPersonaId + sourceSessionId.
        var call = CallTool(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"tasks_create","arguments":{"title":"новая задача"}}}""",
            _ => CreatedTask(),
            configureEnv: psi => psi.Environment["TASKS_SELF_PERSONA_ID"] = "prs-1");
        if (call is not { } r) return;

        r.IsError.Should().BeFalse();
        r.Bodies.Should().Contain(b => b.Contains("\"createdByPersonaId\":\"prs-1\""));
        r.Bodies.Should().Contain(b => b.Contains("\"sourceSessionId\":\"session-1\""));
    }
}
