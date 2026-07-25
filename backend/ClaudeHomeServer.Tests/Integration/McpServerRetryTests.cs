using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Integration;

/// <summary>
/// Устойчивость MCP-серверов к сбоям бэкенда. В истории прода 18 карточек «HTTP 503» с пустым
/// телом пришлись на 4 сессии — это моменты, когда бэкенда просто не было на адресе (рестарт,
/// деплой). Ретраев не было ни одного, поэтому секундная недоступность превращалась в серию
/// красных карточек, а модель бросала начатое.
///
/// Проверяем на живом node-процессе против заглушки бэкенда:
/// 1. чтение (GET) переживает 503 прозрачно для модели;
/// 2. мутация (POST) НЕ повторяется автоматически — повтор задвоил бы задачу;
/// 3. текст ошибки несёт класс: «временный сбой» ≠ «отказ» ≠ «занято».
/// </summary>
public class McpServerRetryTests
{
    private static string? FindServerPath(string serverDir)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "mcp", serverDir, "index.js");
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
    /// Гоняет один tools/call против заглушки, отвечающей по сценарию.
    /// Возвращает (текст ответа модели, isError, сколько запросов дошло до бэкенда).
    /// </summary>
    private static (string Text, bool IsError, int Requests)? CallTool(
        string toolCallJson, Func<int, (int Status, string Body)> respond)
    {
        var serverPath = FindServerPath("tasks-server");
        Skip.If(serverPath is null, "mcp/tasks-server/index.js не найден");

        var port = FreeTcpPort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        try { listener.Start(); }
        catch (HttpListenerException ex) { Skip.If(true, $"HttpListener недоступен: {ex.Message}"); }

        var requests = 0;
        using var stop = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch (Exception) { return; }   // listener закрыт по выходу из теста

                var (status, body) = respond(Interlocked.Increment(ref requests));
                var bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.StatusCode = status;
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
            return (
                result.GetProperty("content")[0].GetProperty("text").GetString() ?? "",
                result.TryGetProperty("isError", out var e) && e.GetBoolean(),
                Volatile.Read(ref requests));
        }
    }

    [SkippableFact]
    public void Чтение_ПереживаетДваПадения503_БезОшибкиДляМодели()
    {
        var call = CallTool(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"tasks_list","arguments":{}}}""",
            n => n < 3 ? (503, "") : (200, "[]"));
        if (call is not { } r) return;   // скип отработал внутри

        r.IsError.Should().BeFalse($"секундная недоступность бэкенда не должна доходить до модели: {r.Text}");
        r.Requests.Should().Be(3, "две неудачные попытки и одна успешная");
    }

    [SkippableFact]
    public void Мутация_НеПовторяетсяАвтоматически_ИначеЗадвоитЗапись()
    {
        var call = CallTool(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"tasks_create","arguments":{"title":"x"}}}""",
            _ => (503, ""));
        if (call is not { } r) return;

        r.Requests.Should().Be(1, "POST повторять нельзя — задача создалась бы дважды");
        r.IsError.Should().BeTrue();
        r.Text.Should().Contain("Временный сбой", "модель должна понять, что запрет тут ни при чём");
        r.Text.Should().Contain("повтори", "и что попытку имеет смысл повторить");
    }

    [SkippableTheory]
    // Отказ по правилу — повторять бессмысленно; занято — повторить позже
    [InlineData(403, "нельзя", "Отказ", "бессмысленно")]
    [InlineData(409, "busy", "занято", "Повтори позже")]
    public void ОтказБэкенда_ПодписанКлассом(int status, string body, string expectedClass, string expectedAdvice)
    {
        var call = CallTool(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"tasks_create","arguments":{"title":"x"}}}""",
            _ => (status, $$"""{"error":"{{body}}"}"""));
        if (call is not { } r) return;

        r.IsError.Should().BeTrue();
        r.Text.Should().Contain(expectedClass).And.Contain(expectedAdvice);
        r.Text.Should().Contain(body, "текст сервера должен доходить до модели целиком");
    }
}
