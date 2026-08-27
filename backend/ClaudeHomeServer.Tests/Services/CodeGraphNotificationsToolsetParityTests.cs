using System.Text.Json;
using ClaudeHomeServer.Services.Mcp.Http;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож парности графа кода и уведомлений (ADR-012, фаза 2 волна 3). У обоих серверов
/// состав ПОСТОЯННЫЙ (3 чтения и 4 инструмента), поэтому ось одна — но сверка идёт с
/// ЖИВЫМ stdio-сервером, а не с литералами: замороженный index.js остаётся веткой отката
/// по рубильнику Mcp:HttpTransport, и разошедшийся состав дал бы разное поведение модели
/// в зависимости от транспорта. Required-наборы — тоже по данным живого ответа, без
/// regex-скрейпинга JS (техдолг MCP-over-HTTP §6).
/// </summary>
public class CodeGraphNotificationsToolsetParityTests
{
    private record StdioTool(string Name, JsonElement Schema);

    // tools/list живого stdio-сервера: бэкенд не нужен, в сеть сервер не ходит.
    // null — node недоступен (тест пропускается).
    private static IReadOnlyList<StdioTool>? ListStdioTools(string serverDir,
        params (string Key, string Value)[] env)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "mcp", serverDir, "index.js");
            if (File.Exists(candidate)) break;
            dir = dir.Parent;
        }
        Skip.If(dir is null, $"mcp/{serverDir}/index.js не найден");

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "node",
            ArgumentList = { Path.Combine(dir!.FullName, "mcp", serverDir, "index.js") },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var (key, value) in env) psi.Environment[key] = value;

        System.Diagnostics.Process? proc;
        try { proc = System.Diagnostics.Process.Start(psi); }
        catch (Exception ex) { Skip.If(true, $"node недоступен: {ex.Message}"); return null; }
        Skip.If(proc is null, "не удалось запустить node");

        using (proc!)
        {
            proc.StandardInput.WriteLine("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
            proc.StandardInput.Flush();

            // Оба сервера первой строкой печатают нотификацию готовности («… ready»), и только
            // потом отвечают на запрос: читаем до строки С РЕЗУЛЬТАТОМ, а не первую попавшуюся
            string? line;
            JsonDocument? answer = null;
            while ((line = proc.StandardOutput.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parsed = JsonDocument.Parse(line);
                if (parsed.RootElement.TryGetProperty("result", out _)) { answer = parsed; break; }
            }
            proc.StandardInput.Close();
            if (!proc.WaitForExit(10_000)) proc.Kill(entireProcessTree: true);

            answer.Should().NotBeNull("сервер обязан ответить на tools/list");
            using var doc = answer!;
            return doc.RootElement.GetProperty("result").GetProperty("tools")
                .EnumerateArray()
                .Select(t => new StdioTool(
                    t.GetProperty("name").GetString()!,
                    t.GetProperty("inputSchema").Clone()))
                .ToList();
        }
    }

    [SkippableFact]
    public void ГрафКода_СоставСовпадаетСоStdioВеткой()
    {
        var stdio = ListStdioTools("codegraph-server",
            ("CODEGRAPH_API_URL", "http://127.0.0.1:1"),
            ("CODEGRAPH_API_TOKEN", "test"),
            ("CODEGRAPH_PROJECT_ID", "proj-1"));
        if (stdio is null) return;
        stdio.Select(t => t.Name).Should().BeEquivalentTo(CodeGraphToolset.AllTools.Select(t => t.Name),
            options => options.WithStrictOrdering(),
            "состав графа кода обязан совпадать со stdio-веткой отката");
    }

    [SkippableFact]
    public void Уведомления_СоставСовпадаетСоStdioВеткой()
    {
        var stdio = ListStdioTools("notifications-server",
            ("NOTIFICATIONS_API_URL", "http://127.0.0.1:1"),
            ("NOTIFICATIONS_API_TOKEN", "test"));
        if (stdio is null) return;
        stdio.Select(t => t.Name).Should().BeEquivalentTo(NotificationsToolset.AllTools.Select(t => t.Name),
            options => options.WithStrictOrdering(),
            "состав уведомлений обязан совпадать со stdio-веткой отката");
    }

    /// <summary>
    /// Required-наборы обеих веток совпадают: иначе аргументы валидируются по-разному.
    /// Сверка — по данным живого stdio-ответа.
    /// </summary>
    [SkippableTheory]
    [InlineData("codegraph-server", "codegraph",
        "CODEGRAPH_API_URL", "http://127.0.0.1:1", "CODEGRAPH_API_TOKEN", "test", "CODEGRAPH_PROJECT_ID", "proj-1")]
    [InlineData("notifications-server", "notifications",
        "NOTIFICATIONS_API_URL", "http://127.0.0.1:1", "NOTIFICATIONS_API_TOKEN", "test")]
    public void RequiredНаборы_СовпадаютПосимвольно(string serverDir, string kind,
        params string[] envPairs)
    {
        var env = envPairs.Chunk(2)
            .Select(p => (Key: p[0], Value: p[1])).ToArray();
        var stdio = ListStdioTools(serverDir, env);
        if (stdio is null) return;
        var tools = kind == "codegraph" ? CodeGraphToolset.AllTools : NotificationsToolset.AllTools;
        var byName = tools.ToDictionary(t => t.Name);

        foreach (var tool in stdio)
        {
            var csharp = byName.GetValueOrDefault(tool.Name);
            csharp.Should().NotBeNull($"инструмент {tool.Name} обязан быть в http-ветке");
            var stdioRequired = tool.Schema.TryGetProperty("required", out var required)
                ? required.EnumerateArray().Select(n => n.GetString()!).ToList()
                : [];
            var csharpRequired = csharp!.InputSchema["required"]?.AsArray()
                .Select(n => n!.GetValue<string>()).ToList() ?? [];
            stdioRequired.Should().BeEquivalentTo(csharpRequired,
                options => options.WithStrictOrdering(),
                $"required-набор {tool.Name} не должен расходиться между ветками");
        }
    }

    /// <summary>Хвосты маршрутов строятся одной формой — как у tasks/notes/wsp.</summary>
    [Fact]
    public void ХвостыМаршрутов_СтроятсяОднойФормой()
    {
        CodeGraphToolset.EndpointFor("http://localhost:5000", "sess-1")
            .Should().Be("http://localhost:5000/mcp/codegraph/sess-1");
        NotificationsToolset.EndpointFor("http://localhost:5000", "sess-1")
            .Should().Be("http://localhost:5000/mcp/notifications/sess-1");
    }
}
