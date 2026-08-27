using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Mcp.Http;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож парности контракта widget_show (находка консилиума по 3b764c58). Схема, лимиты и
/// тексты продублированы в WidgetsToolset.cs (http-ветка) и mcp/widgets-server/index.js
/// (stdio-ветка отката по рубильнику Mcp:HttpTransport) — обе живые, и правка лимита только
/// в C# прошла бы зелёные тесты, молча разойдясь со второй веткой. Источник контракта —
/// WidgetsToolset.cs (index.js заморожен, см. его шапку); этот тест ловит расхождение пары.
///
/// Сверка идёт ПО ДАННЫМ живого stdio-сервера (tools/list и tools/call через node), а не
/// regex-скрейпингом форматирования index.js — урок техдолга MCP-over-HTTP §6: сторож,
/// якорящийся на литералы, кавычки и отступы JS, падает не по существу при переформатировании.
/// </summary>
public class WidgetsToolsetParityTests
{
    private static readonly WidgetsToolset Toolset = new();
    private static readonly McpToolSchema Tool = Toolset.Tools.Single();

    // Ответ живого stdio-сервера: result по id запроса. Сервер — чистый readline JSON-RPC,
    // отвечает без initialize; env не нужен (инструмент в сеть не ходит). null → скип.
    private static Dictionary<int, JsonElement>? AskStdio(params string[] requests)
    {
        // Корень репозитория от каталога сборки: bin/Debug/net10.0 → вверх до .git
        // (в worktree .git — файл-указатель, поэтому ищем и файл, и папку)
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
               && !Directory.Exists(Path.Combine(dir.FullName, ".git"))
               && !File.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        var serverPath = dir is null ? null : Path.Combine(dir.FullName, "mcp", "widgets-server", "index.js");
        Skip.If(serverPath is null || !File.Exists(serverPath),
            "mcp/widgets-server/index.js не найден — сторожу парности не с чем сверять");

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "node",
            ArgumentList = { serverPath! },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // node пишет stdout в UTF-8, а .NET по умолчанию читает в консольной кодировке
            // ОС — кириллические описания превращались в кракозябры и ломали посимвольную
            // сверку (на Linux тесты проходят и без этого, CI там)
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        System.Diagnostics.Process? proc;
        try { proc = System.Diagnostics.Process.Start(psi); }
        catch (Exception ex) { Skip.If(true, $"node недоступен: {ex.Message}"); return null; }
        Skip.If(proc is null, "не удалось запустить node");

        using (proc!)
        {
            foreach (var request in requests) proc.StandardInput.WriteLine(request);
            proc.StandardInput.Flush();
            // EOF: сервер допишет ответы и выйдет сам — читаем всё, что накопил пайп
            proc.StandardInput.Close();

            var answers = new Dictionary<int, JsonElement>();
            string? line;
            while ((line = proc.StandardOutput.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonDocument parsed;
                try { parsed = JsonDocument.Parse(line); }
                catch (JsonException) { continue; }
                if (parsed.RootElement.TryGetProperty("id", out var id)
                    && id.TryGetInt32(out var n)
                    && parsed.RootElement.TryGetProperty("result", out var result))
                    answers[n] = result.Clone();
            }
            if (!proc.WaitForExit(10_000)) proc.Kill(entireProcessTree: true);
            return answers;
        }
    }

    // JSON-RPC tools/call виджета с произвольными аргументами
    private static string Call(int id, object arguments) => JsonSerializer.Serialize(new
    {
        jsonrpc = "2.0",
        id,
        method = "tools/call",
        @params = new { name = "widget_show", arguments },
    });

    // Схема инструмента из живого tools/list
    private static JsonElement StdioSchema(Dictionary<int, JsonElement> answers)
    {
        var tool = answers[1].GetProperty("tools").EnumerateArray().Single();
        tool.GetProperty("name").GetString().Should().Be(Tool.Name, "имя инструмента не смеет разъехаться");
        return tool.GetProperty("inputSchema");
    }

    [SkippableFact]
    public void ИмяИОписаниеСовпадаютПосимвольно()
    {
        var answers = AskStdio("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        if (answers is null) return;
        var tool = answers[1].GetProperty("tools").EnumerateArray().Single();

        // Описание — текст для модели: расхождение меняет поведение веток по-разному
        tool.GetProperty("description").GetString().Should().Be(Tool.Description,
            "описание инструмента обязано совпадать со stdio-веткой посимвольно");
    }

    [SkippableFact]
    public void Схема_RequiredСвойстваИГраницыВысотыСовпадают()
    {
        var answers = AskStdio("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        if (answers is null) return;
        var schema = StdioSchema(answers);

        var stdioRequired = schema.TryGetProperty("required", out var required)
            ? required.EnumerateArray().Select(n => n.GetString()!).ToList() : [];
        var csharpRequired = Tool.InputSchema["required"]!.AsArray()
            .Select(n => n!.GetValue<string>()).ToList();
        stdioRequired.Should().BeEquivalentTo(csharpRequired,
            options => options.WithStrictOrdering(),
            "required-набор не должен расходиться между ветками");

        var stdioProperties = schema.GetProperty("properties").EnumerateObject()
            .Select(p => p.Name).ToList();
        var csharpProperties = Tool.InputSchema["properties"]!.AsObject().Select(p => p.Key).ToList();
        stdioProperties.Should().BeEquivalentTo(csharpProperties,
            "набор аргументов не смеет расходиться");

        // Границы height — это константы MinHeight/MaxHeight тулсета: сверка по данным
        // живой схемы, а не по JS-литералам
        var height = schema.GetProperty("properties").GetProperty("height");
        height.GetProperty("minimum").GetInt32().Should().Be(WidgetsToolset.MinHeight);
        height.GetProperty("maximum").GetInt32().Should().Be(WidgetsToolset.MaxHeight);
    }

    /// <summary>
    /// Тексты ответов обеих веток — посимвольно: модель видит именно их, расхождение
    /// меняет поведение веток по-разному. Лимиты проверяются поведением: too-big html
    /// через границу MaxHtml и длинный title через MaxTitle (обрезка видна в тексте).
    /// </summary>
    [SkippableFact]
    public async Task ТекстыОтветовСовпадаютПосимвольно()
    {
        var tooBig = new string('x', WidgetsToolset.MaxHtml + 1);
        var longTitle = new string('З', WidgetsToolset.MaxTitle + 10);
        var answers = AskStdio(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
            Call(2, new { html = "  " }),
            Call(3, new { html = tooBig }),
            Call(4, new { html = "<b>ок</b>", title = longTitle, height = 400 }));
        if (answers is null) return;
        var context = new McpToolCallContext("owner-parity", null);

        var empty = await Toolset.CallAsync("widget_show", new JsonObject { ["html"] = "  " },
            context, CancellationToken.None);
        AssertSameCall(answers[2], empty);

        var big = await Toolset.CallAsync("widget_show",
            new JsonObject { ["html"] = tooBig }, context, CancellationToken.None);
        AssertSameCall(answers[3], big);

        var ok = await Toolset.CallAsync("widget_show", new JsonObject
        {
            ["html"] = "<b>ок</b>",
            ["title"] = longTitle,
            ["height"] = 400,
        }, context, CancellationToken.None);
        AssertSameCall(answers[4], ok);
    }

    // Сверка текстового content-ответа stdio (tools/call) с результатом http-ветки
    private static void AssertSameCall(JsonElement stdioResult, McpToolCallResult csharp)
    {
        var text = stdioResult.GetProperty("content")[0].GetProperty("text").GetString();
        text.Should().Be(csharp.Text, "текст ответа обязан совпадать посимвольно");
        var stdioIsError = stdioResult.TryGetProperty("isError", out var isError) && isError.GetBoolean();
        stdioIsError.Should().Be(csharp.IsError,
            "признак ошибки обязан совпадать — от него зависит реакция модели");
    }
}
