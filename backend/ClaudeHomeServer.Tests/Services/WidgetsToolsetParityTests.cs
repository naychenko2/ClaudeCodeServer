using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.Mcp.Http;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож парности контракта widget_show (находка консилиума по 3b764c58). Схема, лимиты и
/// тексты продублированы в WidgetsToolset.cs (http-ветка) и mcp/widgets-server/index.js
/// (stdio-ветка отката по рубильнику Mcp:HttpTransport) — обе живые, и правка лимита только
/// в C# прошла бы зелёные тесты, молча разойдясь со второй веткой. Источник контракта —
/// WidgetsToolset.cs (index.js заморожен, см. его шапку); этот тест ловит расхождение пары.
/// </summary>
public class WidgetsToolsetParityTests
{
    private static readonly WidgetsToolset Toolset = new();
    private static readonly McpToolSchema Tool = Toolset.Tools.Single();

    private static string JsPath
    {
        get
        {
            // Корень репозитория от каталога сборки: bin/Debug/net10.0 → вверх до .git
            // (в worktree .git — файл-указатель, поэтому ищем и файл, и папку)
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null
                   && !Directory.Exists(Path.Combine(dir.FullName, ".git"))
                   && !File.Exists(Path.Combine(dir.FullName, ".git")))
                dir = dir.Parent;
            var path = dir is null
                ? null
                : Path.Combine(dir.FullName, "mcp", "widgets-server", "index.js");
            if (path is null || !File.Exists(path))
                throw new InvalidOperationException("index.js stdio-ветки не найден — сторож парности не может работать");
            return path;
        }
    }

    private static readonly Lazy<string> Js = new(() => File.ReadAllText(JsPath));

    // Регион инструмента в JS: от литерала имени до inputSchema (описание — конкатенация литералов)
    private static string DescriptionBlock()
    {
        var start = Js.Value.IndexOf("'widget_show'", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "инструмент обязан быть описан в stdio-ветке");
        var end = Js.Value.IndexOf("inputSchema:", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);
        return Js.Value[start..end];
    }

    // Регион схемы: от properties до конца блока TOOLS (ключи свойств — строки «name: {»)
    private static string PropertiesBlock()
    {
        var start = Js.Value.IndexOf("properties:", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0);
        var end = Js.Value.IndexOf("function json", StringComparison.Ordinal);
        return Js.Value[start..end];
    }

    [Fact]
    public void ЛимитыСовпадают()
    {
        JsConst("MAX_HTML").Should().Be(WidgetsToolset.MaxHtml, "лимит html обязан ехать парой");
        JsConst("MAX_TITLE").Should().Be(WidgetsToolset.MaxTitle);
        JsConst("MIN_HEIGHT").Should().Be(WidgetsToolset.MinHeight);
        JsConst("MAX_HEIGHT").Should().Be(WidgetsToolset.MaxHeight);
    }

    [Fact]
    public void ИмяИОписаниеИнструментаСовпадаютПосимвольно()
    {
        var literals = Regex.Matches(DescriptionBlock(), "'([^']+)'")
            .Select(m => m.Groups[1].Value).ToList();
        literals.Should().NotBeEmpty();
        literals[0].Should().Be(Tool.Name, "первым литералом в блоке — имя инструмента");

        // Описание в JS собрано конкатенацией литералов: склейка обязана совпасть с C#-строкой
        // целиком — это текст для модели, расхождение меняет поведение веток по-разному
        string.Concat(literals.Skip(1)).Should().Be(Tool.Description);
    }

    [Fact]
    public void СхемаСовпадает()
    {
        var required = Regex.Match(Js.Value, @"required:\s*\[([^\]]*)\]");
        required.Success.Should().BeTrue();
        var jsRequired = Regex.Matches(required.Groups[1].Value, "'([^']+)'")
            .Select(m => m.Groups[1].Value).ToList();

        var schema = Tool.InputSchema;
        var csharpRequired = schema["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        jsRequired.Should().BeEquivalentTo(csharpRequired);

        // Свойства — строки «name: {» в регионе properties: набор и границы height не расходятся
        var jsProperties = Regex.Matches(PropertiesBlock(), @"^ +(\w+): \{", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToList();
        var csharpProperties = schema["properties"]!.AsObject().Select(p => p.Key).ToList();
        jsProperties.Should().BeEquivalentTo(csharpProperties, "набор аргументов не смеет расходиться");

        var height = schema["properties"]!["height"]!.AsObject();
        height["minimum"]!.GetValue<int>().Should().Be(WidgetsToolset.MinHeight);
        height["maximum"]!.GetValue<int>().Should().Be(WidgetsToolset.MaxHeight);
    }

    /// <summary>
    /// Тексты ответов: пустой-html отказ — посимвольно (JS-литерал против поведения C#),
    /// хвосты остальных сообщений — наличием в обеих ветках.
    /// </summary>
    [Fact]
    public async Task ТекстыОтветовСовпадают()
    {
        var empty = await Toolset.CallAsync("widget_show", new JsonObject { ["html"] = "  " },
            new McpToolCallContext("owner-parity", null), CancellationToken.None);
        empty.IsError.Should().BeTrue();
        Js.Value.Should().Contain(empty.Text, "текст отказа «html пустой» обязан совпадать посимвольно");

        foreach (var tail in new[]
                 {
                     "упрости разметку", "сократи данные", // отказ «слишком большой»
                     "показан пользователю в ленте чата", "НЕ дублируй его содержимое текстом", // успех
                 })
            Js.Value.Should().Contain(tail);
    }

    // Числовая константа JS: `N`, `N * M` (64 * 1024) или `N / M` (MAX_HTML / 1024)
    private static int JsConst(string name)
    {
        var match = Regex.Match(Js.Value, $@"const {name}\s*=\s*([^;]+);");
        match.Success.Should().BeTrue($"константа {name} обязана быть в stdio-ветке");
        var expr = match.Groups[1].Value.Trim();
        var binary = Regex.Match(expr, @"^(\d+)\s*([*/])\s*(\d+)$");
        if (!binary.Success)
            return int.Parse(expr);
        var a = int.Parse(binary.Groups[1].Value);
        var b = int.Parse(binary.Groups[3].Value);
        return binary.Groups[2].Value == "*" ? a * b : a / b;
    }
}
