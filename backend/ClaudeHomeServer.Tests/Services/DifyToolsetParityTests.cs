using System.Text.Json;
using ClaudeHomeServer.Services.Mcp.Http;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож парности dify (ADR-012, фаза 2 волна 4). Сверка идёт с ЖИВЫМ stdio-сервером
/// mcp-dify/dist/index.js (TypeScript со сборкой tsc): он заморожен как ветка отката по
/// рубильнику Mcp:HttpTransport, и разошедшийся состав или required-наборы давали бы разное
/// поведение модели в зависимости от транспорта. dist и node_modules не живут в git —
/// без них тесты живой сверки скипаются (сборка: cd mcp-dify; npm install; npm run build),
/// структурные проверки работают всегда.
/// </summary>
public class DifyToolsetParityTests
{
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
               && !Directory.Exists(Path.Combine(dir.FullName, ".git"))
               && !File.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir?.FullName;
    }

    private record StdioTool(string Name, JsonElement Schema);

    // tools/list живого stdio-сервера: env-режим задаёт состав (DIFY_SEARCH_ONLY), в сеть
    // сервер не ходит. null — node или dist недоступны (тест скипается).
    private static IReadOnlyList<StdioTool>? ListStdioTools(params (string Key, string Value)[] env)
    {
        var root = FindRepoRoot();
        var serverPath = root is null ? null : Path.Combine(root, "mcp-dify", "dist", "index.js");
        Skip.If(serverPath is null || !File.Exists(serverPath),
            "mcp-dify/dist/index.js не найден (сборка: cd mcp-dify; npm install; npm run build)");

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "node",
            ArgumentList = { serverPath! },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["DIFY_API_URL"] = "http://127.0.0.1:1";
        psi.Environment["DIFY_API_KEY"] = "test";
        foreach (var (key, value) in env) psi.Environment[key] = value;

        System.Diagnostics.Process? proc;
        try { proc = System.Diagnostics.Process.Start(psi); }
        catch (Exception ex) { Skip.If(true, $"node недоступен: {ex.Message}"); return null; }
        Skip.If(proc is null, "не удалось запустить node");

        using (proc!)
        {
            // SDK-сервер отвечает на tools/list после initialize — пишем оба запроса и читаем
            // строку с result.tools (мимо ответов на initialize и уведомлений)
            proc.StandardInput.WriteLine("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""");
            proc.StandardInput.WriteLine("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            proc.StandardInput.WriteLine("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
            proc.StandardInput.Flush();

            string? line;
            JsonDocument? answer = null;
            while ((line = proc.StandardOutput.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonDocument parsed;
                try { parsed = JsonDocument.Parse(line); }
                catch (JsonException) { continue; }
                if (parsed.RootElement.TryGetProperty("id", out var id)
                    && id.GetInt32() == 2
                    && parsed.RootElement.TryGetProperty("result", out var result)
                    && result.TryGetProperty("tools", out _))
                {
                    answer = parsed;
                    break;
                }
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
    public void ПолныйСостав_СовпадаетСоStdioВеткой()
    {
        var stdio = ListStdioTools();
        if (stdio is null) return;
        stdio.Select(t => t.Name).Should().BeEquivalentTo(DifyToolset.AllTools.Select(t => t.Name),
            options => options.WithStrictOrdering(),
            "полный состав dify обязан совпадать со stdio-веткой отката (12 инструментов)");
    }

    [SkippableFact]
    public void SearchOnlyСостав_СовпадаетСоStdioВеткой()
    {
        // DIFY_SEARCH_ONLY=true + датасет по умолчанию — режим чата проекта с базой:
        // у stdio состав сужается до 4 инструментов, у http — тот же SearchOnlyTools
        var stdio = ListStdioTools(("DIFY_DEFAULT_DATASET_ID", "ds-1"), ("DIFY_SEARCH_ONLY", "true"));
        if (stdio is null) return;
        stdio.Select(t => t.Name).Should().BeEquivalentTo(DifyToolset.SearchOnlyTools.Select(t => t.Name),
            options => options.WithStrictOrdering(),
            "search-only состав (проект со своей базой) обязан совпадать со stdio-веткой");
    }

    /// <summary>
    /// Required-наборы обеих веток совпадают: иначе аргументы валидируются по-разному.
    /// Сверяются ЖИВЫЕ схемы stdio-ответа с C# InputSchema (без regex-скрейпинга TS-кода —
    /// урок техдолга MCP-over-HTTP §6 о хрупкости сторожей по форматированию).
    /// </summary>
    [SkippableFact]
    public void RequiredНаборы_СовпадаютПосимвольно()
    {
        var stdio = ListStdioTools();
        if (stdio is null) return;
        var byName = DifyToolset.AllTools.ToDictionary(t => t.Name, t => t);
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

    /// <summary>Search-only ядро — подмножество полного состава, в порядке stdio-ветки.</summary>
    [Fact]
    public void SearchOnlyЯдро_ПодмножествоПолногоСостава()
    {
        DifyToolset.SearchOnlyTools.Select(t => t.Name).Should().BeSubsetOf(DifyToolset.AllTools.Select(t => t.Name));
        DifyToolset.SearchOnlyTools.Should().HaveCount(4, "поиск + три чтения — как DIFY_SEARCH_ONLY=true stdio-ветки");
        DifyToolset.AllTools.Should().HaveCount(12, "полный состав dify — 12 инструментов stdio-ветки");
    }

    /// <summary>Хвост маршрута строится одной формой — как у tasks/notes/wsp/codegraph.</summary>
    [Fact]
    public void ХвостМаршрута_СтроитсяОднойФормой() =>
        DifyToolset.EndpointFor("http://localhost:5000", "sess-1")
            .Should().Be("http://localhost:5000/mcp/dify/sess-1");
}
