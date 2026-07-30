using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Integration;

/// <summary>
/// Регресс прода: на запись ASP.NET отвечает <c>Ok()</c> без объекта — 200 с ПУСТЫМ телом
/// (FilesController.WriteContent/MkDir/Rename/Create). <c>res.json()</c> в MCP-серверах кидал
/// на нём «Unexpected end of JSON input», и удавшаяся запись возвращалась модели как ошибка —
/// та повторяла запись второй раз. Обработан был только 204, а 200-без-тела — нет.
///
/// Гоняем настоящий node-процесс против заглушки бэкенда: из C# этот путь не виден.
/// </summary>
[Trait("Category", "Integration")]
public class McpServerEmptyBodyTests
{
    // Путь к серверу: поднимаемся от bin тестов до корня репозитория
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

    [SkippableFact]
    public void ПустоеТелоУспешногоОтвета_ЗаписьНеСчитаетсяОшибкой()
    {
        var serverPath = FindServerPath("workspace-server");
        Skip.If(serverPath is null, "mcp/workspace-server/index.js не найден");

        using var listener = StartHttpListenerWithRetry(out var port);

        // Заглушка бэкенда отвечает ровно как FilesController.MkDir: 200 и ни байта тела
        _ = Task.Run(async () =>
        {
            try
            {
                var ctx = await listener.GetContextAsync();
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentLength64 = 0;
                ctx.Response.Close();
            }
            catch (Exception) { /* listener закрыт по выходу из теста — штатно */ }
        });

        var psi = new ProcessStartInfo
        {
            FileName = "node",
            ArgumentList = { serverPath! },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // node читает и пишет UTF-8; без явной кодировки .NET возьмёт системную
            // (на русской Windows — CP866) и кириллица в ответе станет кракозябрами
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        psi.Environment["WORKSPACE_API_URL"] = $"http://localhost:{port}";
        psi.Environment["WORKSPACE_API_TOKEN"] = "test";
        psi.Environment["WORKSPACE_SECTIONS"] = "files";
        psi.Environment["WORKSPACE_WRITE"] = "1";

        Process? proc;
        try { proc = Process.Start(psi); }
        catch (Exception ex) { Skip.If(true, $"node недоступен: {ex.Message}"); return; }
        Skip.If(proc is null, "не удалось запустить node");

        using (proc!)
        {
            proc.StandardInput.WriteLine(
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"files_mkdir","arguments":{"projectId":"p1","path":"новая-папка"}}}""");
            proc.StandardInput.Flush();

            var line = proc.StandardOutput.ReadLine();
            proc.StandardInput.Close();
            if (!proc.WaitForExit(10_000)) proc.Kill(entireProcessTree: true);

            line.Should().NotBeNullOrWhiteSpace("сервер обязан ответить на tools/call");
            using var doc = JsonDocument.Parse(line!);
            var result = doc.RootElement.GetProperty("result");

            var isError = result.TryGetProperty("isError", out var e) && e.GetBoolean();
            var text = result.GetProperty("content")[0].GetProperty("text").GetString() ?? "";

            isError.Should().BeFalse($"пустое тело успешного ответа — не ошибка, а сервер вернул: {text}");
            text.Should().Contain("создана");
        }
    }
}
