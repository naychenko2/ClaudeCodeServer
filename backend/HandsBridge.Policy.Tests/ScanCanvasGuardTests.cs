using System.Reflection;
using System.Text.Json;
using ClaudeHomeServer.HandsBridge.Policy;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace HandsBridge.Policy.Tests;

/// <summary>
/// Сторож <c>scan_canvas</c> (C1 «руки без UIA»): (1) каждый ввод идёт через ГЕЙТ
/// (запрещённое окно/терминал — отказ ДО первого действия); (2) первое действие зажигает плашку
/// <c>HandsGate.Acted</c>; (3) запись — только через <c>HandsProjectFiles</c> (никаких прямых
/// <c>File.*</c>/<c>Directory.*</c>/<c>Stream</c>-записей в инструменте); (4) инструмент в «списке
/// ровно этих инструментов» (<c>HandsTools.All</c> + мост).
/// </summary>
public class ScanCanvasGuardTests
{
    private const string ToolType = "Sbroenne.WindowsMcp.Tools.ScanCanvasTool";
    private const string ToolName = "scan_canvas";

    private static string BridgeDir => FindBridgeDir();

    private static string Read(string relative) =>
        System.IO.File.ReadAllText(System.IO.Path.Combine(BridgeDir, relative));

    /// <summary>Тело метода инструмента (от сигнатуры <c>ExecuteAsync</c> до закрытия скобок).</summary>
    private static string ToolBody()
    {
        var source = Read("Tools/ScanCanvasTool.cs");
        var start = source.IndexOf("public static async Task<CallToolResult> ExecuteAsync", StringComparison.Ordinal);
        Assert.True(start >= 0, "нет метода ExecuteAsync у scan_canvas");

        var open = source.IndexOf('{', source.IndexOf("CancellationToken cancellationToken)", start, StringComparison.Ordinal));
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
                depth++;
            else if (source[i] == '}' && --depth == 0)
                return source[open..(i + 1)];
        }
        throw new System.InvalidOperationException("тело scan_canvas не закрыто");
    }

    private static string FindBridgeDir()
    {
        for (var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "backend", "HandsBridge");
            if (System.IO.File.Exists(System.IO.Path.Combine(candidate, "HandsBridge.csproj")))
                return candidate;
        }
        throw new System.InvalidOperationException("не найден backend/HandsBridge");
    }

    private static CompilationUnitSyntax ParseTree(string source) =>
        CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();

    /// <summary>
    /// Атрибут <c>McpServerTool</c> независимо от квалификации: <c>[McpServerTool(…)]</c> и
    /// <c>[ModelContextProtocol.Server.McpServerTool(…)]</c> — имя берём как последний идентификатор.
    /// </summary>
    private static bool IsMcpServerToolAttribute(AttributeSyntax attr)
    {
        var lastId = attr.Name.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().LastOrDefault();
        return lastId?.Identifier.Text == "McpServerTool";
    }

    // ---------- 1. Гейт — ДО первого действия ----------

    /// <summary>
    /// Гейт <c>CheckKeyboard</c> стоит ПЕРЕД первым <c>await</c> (первое действие — await):
    /// запрещённое окно/терминал — отказ до первого действия.
    /// </summary>
    [Fact]
    public void Gate_check_stands_before_first_action()
    {
        var body = ToolBody();
        var gateAt = body.IndexOf("HandsGate.Policy.CheckKeyboard(", StringComparison.Ordinal);
        var firstAwait = body.IndexOf("await ", StringComparison.Ordinal);
        Assert.True(gateAt >= 0, "нет вызова гейта");
        Assert.True(firstAwait < 0 || gateAt < firstAwait, "гейт после первого действия (await)");
    }

    /// <summary>
    /// Отказ гейта возвращается (return HandsGate.Deny) ПЕРЕД первым await — ни одно действие не
    /// отправлено.
    /// </summary>
    [Fact]
    public void Gate_denial_returned_before_first_action()
    {
        var body = ToolBody();
        var gateAt = body.IndexOf("HandsGate.Policy.CheckKeyboard(", StringComparison.Ordinal);
        var denyAt = body.IndexOf("return HandsGate.Deny(HandsTools.", StringComparison.Ordinal);
        var firstAwait = body.IndexOf("await ", StringComparison.Ordinal);
        Assert.True(denyAt > gateAt, "отказ гейта не возвращается");
        Assert.True(firstAwait < 0 || denyAt < firstAwait, "действия раньше отказа");
    }

    // ---------- 2. Плашка (действие) — после гейта, до первого действия ----------

    /// <summary>
    /// Первое действие зажигает плашку: <c>HandsGate.Acted(HandsTools.ScanCanvas, ...)</c> стоит
    /// после отказа гейта и до первого <c>await</c>.
    /// </summary>
    [Fact]
    public void First_action_reports_activity_after_gate_before_action()
    {
        var body = ToolBody();
        var actedAt = body.IndexOf("HandsGate.Acted(HandsTools.ScanCanvas", StringComparison.Ordinal);
        var denyAt = body.IndexOf("return HandsGate.Deny(HandsTools.", StringComparison.Ordinal);
        var firstAwait = body.IndexOf("await ", StringComparison.Ordinal);
        Assert.True(actedAt >= 0, "нет HandsGate.Acted(HandsTools.ScanCanvas, ...)");
        Assert.True(actedAt > denyAt, "сигнал раньше отказа");
        Assert.True(firstAwait < 0 || actedAt < firstAwait, "сигнал после действия");
    }

    // ---------- 3. Запись — только через HandsProjectFiles (никаких прямых File.*) ----------

    /// <summary>
    /// Прямая запись в файл — это «свой файл» мимо границы: в инструменте нет
    /// <c>File.*</c>/<c>Directory.*</c>/<c>StreamWriter</c>/<c>FileStream</c>-записи (все — через
    /// <c>HandsProjectFiles</c>). Мутация «прямая запись File.*» → этот сторож красит.
    /// </summary>
    [Fact]
    public void No_direct_file_writes_in_the_tool()
    {
        var source = Read("Tools/ScanCanvasTool.cs");
        // Запись идёт только через HandsProjectFiles (WriteNew).
        Assert.Contains("d.Files.WriteNew", source);
        // Прямых записей на диск в инструменте нет (никаких File./Directory./StreamWriter/FileStream).
        Assert.DoesNotContain("File.", source);
        Assert.DoesNotContain("Directory.", source);
        Assert.DoesNotContain("StreamWriter", source);
        Assert.DoesNotContain("FileStream", source);
    }

    // ---------- 4. «Ровно эти инструменты»: scan_canvas в мосту и в HandsTools.All ----------

    /// <summary>
    /// <c>scan_canvas</c> не публикуется в первой выкладке: атрибут <c>[McpServerTool(Name = "scan_canvas")</c>
    /// снят (модель инструмент не видит), но имя зарезервировано в <c>HandsTools.All</c>.
    /// Мутация «вернуть публикацию» (вставить атрибут) → красный.
    /// </summary>
    [Fact]
    public void Scan_canvas_not_published_in_the_first_release()
    {
        // Roslyn, а не литерал: вариант форматирования Name="scan_canvas" (без пробела) не обходит.
        var root = ParseTree(Read("Tools/ScanCanvasTool.cs"));
        var names = new List<string>();
        foreach (var attr in root.DescendantNodes().OfType<AttributeSyntax>())
        {
            if (!IsMcpServerToolAttribute(attr)) continue;
            var nameArg = attr.ArgumentList?.Arguments
                .FirstOrDefault(a => a.NameEquals?.Name?.Identifier.Text == "Name");
            if (nameArg?.Expression is LiteralExpressionSyntax lit)
            {
                var text = lit.Token.Text;
                if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
                    names.Add(text[1..^1]);
            }
        }
        Assert.DoesNotContain("scan_canvas", names);

        var policySource = System.IO.File.ReadAllText(System.IO.Path.Combine(BridgeDir, "..", "HandsBridge.Policy", "HandsPolicy.cs"));
        Assert.Contains("\"scan_canvas\"", policySource);
    }

    // ---------- 5. Runtime (Linux): запретное окно → отказ ДО первого действия ----------

    /// <summary>
    /// На Linux (NoWindows) любое windowHandle — «процесс с неизвестным образом» → гейт отказывает
    /// ДО первого действия (и до первого WinAPI-сбора). Ответ — чистый отказ <c>hands_policy</c>,
    /// а не краш на сборке.
    /// </summary>
    [Fact]
    public async Task Forbidden_window_refused_before_first_action()
    {
        var (isError, text) = await HandsBridgeGateRuntimeTests.Call(ToolType, new()
        {
            ["windowHandle"] = "65552",
            ["saveTo"] = "scheme.png",
        });

        Assert.True(isError, text);
        using var json = JsonDocument.Parse(text);
        Assert.Equal("hands_policy", json.RootElement.GetProperty("error").GetString());
        Assert.Equal("scan_canvas", json.RootElement.GetProperty("tool").GetString());
        Assert.Contains("never click or type", json.RootElement.GetProperty("message").GetString());
    }

    /// <summary>
    /// Без windowHandle — отказ до гейта (форма, как у scan_document): «requires windowHandle».
    /// </summary>
    [Fact]
    public async Task Without_window_handle_refused_before_gate()
    {
        var (isError, text) = await HandsBridgeGateRuntimeTests.Call(ToolType, new()
        {
            ["saveTo"] = "scheme.png",
        });

        Assert.True(isError, text);
        Assert.Contains("requires windowHandle", text);
    }
}
