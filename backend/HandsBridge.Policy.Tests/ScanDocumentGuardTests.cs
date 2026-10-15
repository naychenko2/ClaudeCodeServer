using System.Reflection;
using System.Text.Json;
using ClaudeHomeServer.HandsBridge.Policy;
using Xunit;

namespace HandsBridge.Policy.Tests;

/// <summary>
/// Сторож <c>scan_document</c> (A5 «руки без UIA»): (1) каждый ввод идёт через ГЕЙП КЛАВИАТУРЫ
/// (запрещённое окно/терминал — отказ ДО первого снимка); (2) первое действие зажигает плашку
/// <c>HandsGate.Acted</c>; (3) запись — только через <c>HandsProjectFiles</c> (никаких прямых
/// <c>File.*</c>/<c>Directory.*</c>/<c>Stream</c> в инструменте); (4) инструмент появился в
/// «списке ровно этих инструментов» (медиана <c>notYetInBridge</c> — см. фикс в
/// <see cref="HandsBridgeSourceGuardTests"/>).
/// </summary>
public class ScanDocumentGuardTests
{
    private const string ToolType = "Sbroenne.WindowsMcp.Tools.ScanDocumentTool";
    private const string ToolName = "scan_document";

    private static string BridgeDir => FindBridgeDir();

    private static string Read(string relative) =>
        System.IO.File.ReadAllText(System.IO.Path.Combine(BridgeDir, relative));

    /// <summary>Тело метода инструмента (от <c>[McpServerTool(Name="scan_document")</c> до закрытия скобок).</summary>
    private static string ToolBody()
    {
        var source = Read("Tools/ScanDocumentTool.cs");
        var start = source.IndexOf($"[McpServerTool(Name = \"{ToolName}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "нет инструмента scan_document");

        var open = source.IndexOf('{', source.IndexOf("CancellationToken cancellationToken)", start, StringComparison.Ordinal));
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
                depth++;
            else if (source[i] == '}' && --depth == 0)
                return source[open..(i + 1)];
        }
        throw new System.InvalidOperationException("тело scan_document не закрыто");
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

    // ---------- 1. Гейт клавиатуры — ДО первого снимка ----------

    /// <summary>
    /// Гейт <c>CheckKeyboard</c> стоит ПЕРЕД первым <c>await</c> (первый снимок — await):
    /// запрещённое окно/терминал — отказ до первого снимка.
    /// </summary>
    [Fact]
    public void Gate_keyboard_check_stands_before_first_capture()
    {
        var body = ToolBody();
        var gateAt = body.IndexOf("HandsGate.Policy.CheckKeyboard(", StringComparison.Ordinal);
        var firstAwait = body.IndexOf("await ", StringComparison.Ordinal);
        Assert.True(gateAt >= 0, "нет вызова гейта клавиатуры");
        Assert.True(firstAwait < 0 || gateAt < firstAwait, "гейт после первого снимка (await)");
    }

    /// <summary>
    /// Отказ гейта возвращается (return HandsGate.Deny) ПЕРЕД первым await — ни один кадр не снят.
    /// </summary>
    [Fact]
    public void Gate_denial_returned_before_first_capture()
    {
        var body = ToolBody();
        var gateAt = body.IndexOf("HandsGate.Policy.CheckKeyboard(", StringComparison.Ordinal);
        var denyAt = body.IndexOf("return HandsGate.Deny(HandsTools.", StringComparison.Ordinal);
        var firstAwait = body.IndexOf("await ", StringComparison.Ordinal);
        Assert.True(denyAt > gateAt, "отказ гейта не возвращается");
        Assert.True(firstAwait < 0 || denyAt < firstAwait, "снимка раньше отказа");
    }

    // ---------- 2. Плашка (действие) — после гейта, до первого действия ----------

    /// <summary>
    /// Первое действие (снимок/клавиша) зажигает плашку: <c>HandsGate.Acted(HandsTools.ScanDocument, ...)</c>
    /// стоит после отказа гейта и до первого <c>await</c>.
    /// </summary>
    [Fact]
    public void First_action_reports_activity_after_gate_before_action()
    {
        var body = ToolBody();
        var actedAt = body.IndexOf("HandsGate.Acted(HandsTools.ScanDocument", StringComparison.Ordinal);
        var denyAt = body.IndexOf("return HandsGate.Deny(HandsTools.", StringComparison.Ordinal);
        var firstAwait = body.IndexOf("await ", StringComparison.Ordinal);
        Assert.True(actedAt >= 0, "нет HandsGate.Acted(HandsTools.ScanDocument, ...)");
        Assert.True(actedAt > denyAt, "сигнал раньше отказа");
        Assert.True(firstAwait < 0 || actedAt < firstAwait, "сигнал после действия");
    }

    // ---------- 3. Запись — только через HandsProjectFiles (никаких прямых File.*) ----------

    /// <summary>
    /// Прямая запись в файл — это «свой файл» мимо границы: в инструменте нет
    /// <c>File.*</c>/<c>Directory.*</c>/<c>Stream</c>-записи (все — через <c>HandsProjectFiles</c>).
    /// Мутация «прямая запись File.*» → этот сторож красит.
    /// </summary>
    [Fact]
    public void No_direct_file_writes_in_the_tool()
    {
        var source = Read("Tools/ScanDocumentTool.cs");
        // Запись идёт только через HandsProjectFiles (WriteNew/AppendOwn).
        Assert.Contains("d.Files.WriteNew", source);
        Assert.Contains("d.Files.AppendOwn", source);
        // Прямых записей на диск в инструменте нет (никаких File./Directory./StreamWriter/Stream-записей).
        Assert.DoesNotContain("File.", source);
        Assert.DoesNotContain("Directory.", source);
        Assert.DoesNotContain("StreamWriter", source);
        Assert.DoesNotContain("FileStream", source);
    }

    // ---------- 4. «Ровно эти инструменты»: scan_document теперь в мосту (фикс notYetInBridge) ----------

    /// <summary>
    /// <c>scan_document</c> теперь в списке инструментов моста: в <c>notYetInBridge</c>
    /// (фикс в <see cref="HandsBridgeSourceGuardTests"/>) его больше нет, а в списке <c>All</c> — есть.
    /// </summary>
    [Fact]
    public void Scan_document_now_in_the_bridge_tool_list()
    {
        // В мосту появился [McpServerTool(Name = "scan_document" ...)]
        var source = Read("Tools/ScanDocumentTool.cs");
        Assert.Contains("[McpServerTool(Name = \"scan_document\"", source);

        // ... и он числится в перечне инструментов (HandsTools.All содержит "scan_document").
        var policySource = System.IO.File.ReadAllText(System.IO.Path.Combine(BridgeDir, "..", "HandsBridge.Policy", "HandsPolicy.cs"));
        Assert.Contains("\"scan_document\"", policySource);
    }

    // ---------- 5. Схема: keepPages убран (A5.1) ----------

    /// <summary>
    /// keepPages убран из схемы (A5.1): политика <c>CheckOutputPath</c> отказывает <c>.png</c> для
    /// tool-key <c>scan_document</c>, а результат <c>WriteNew</c> отбрасывался — параметр
    /// «не поддерживается». Метод инструмента (и его схема) не содержит <c>keepPages</c>.
    /// Мутация «вернуть keepPages в параметры» красит тест.
    /// </summary>
    [Fact]
    public void Schema_does_not_contain_keepPages()
    {
        var type = HandsBridgeGateRuntimeTests.Bridge.Value.GetType(ToolType, throwOnError: true)!;
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.GetCustomAttributes().Any(a => a.GetType().Name == "McpServerToolAttribute"));
        var paramNames = method.GetParameters().Select(p => p.Name!).ToList();
        Assert.DoesNotContain("keepPages", paramNames);
    }

    // ---------- 6. Runtime (Linux): запретное окно → отказ ДО первого снимка ----------

    /// <summary>
    /// На Linux (NoWindows) любое windowHandle — «процесс с неизвестным образом» → гейт отказывает
    /// ДО первого снимка (и до первого WinAPI-сбора). Ответ — чистый отказ <c>hands_policy</c>,
    /// а не краш на сборке.
    /// </summary>
    [Fact]
    public async Task Forbidden_window_refused_before_first_capture()
    {
        var (isError, text) = await HandsBridgeGateRuntimeTests.Call(ToolType, new()
        {
            ["windowHandle"] = "65552",
            ["saveTo"] = "doc.md",
        });

        Assert.True(isError, text);
        using var json = System.Text.Json.JsonDocument.Parse(text);
        Assert.Equal("hands_policy", json.RootElement.GetProperty("error").GetString());
        Assert.Equal("scan_document", json.RootElement.GetProperty("tool").GetString());
        // «never click or type” — формулировка гейта для окна с неизвестным/запрещённым процессом.
        Assert.Contains("never click or type", json.RootElement.GetProperty("message").GetString());
    }

    /// <summary>
    /// Без windowHandle — отказ до гейта (форма, как у keyboard_control): «requires windowHandle”.
    /// </summary>
    [Fact]
    public async Task Without_window_handle_refused_before_gate()
    {
        var (isError, text) = await HandsBridgeGateRuntimeTests.Call(ToolType, new()
        {
            ["saveTo"] = "doc.md",
        });

        Assert.True(isError, text);
        Assert.Contains("requires windowHandle", text);
    }
}
