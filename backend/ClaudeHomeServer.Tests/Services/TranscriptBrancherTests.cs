using ClaudeHomeServer.Services.Llm;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Резак ветвления: префикс [0, K) + fail-closed сопоставление (docs/research/chat-branching-2026-09.md §4).
// Синтетические .jsonl — с той же анатомией, что у живых транскриптов; пути — только temp.
public class TranscriptBrancherTests : IDisposable
{
    private const string Old = "858fdbf9-f11d-4466-86e3-0984552c07af";
    private const string New = "11111111-2222-4333-8444-555555555555";

    private readonly string _dir;
    public TranscriptBrancherTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "brancher_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // --- Синтез строк .jsonl ---
    private static string JsonStr(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    private static string UserStr(string uuid, string text) =>
        "{\"type\":\"user\",\"sessionId\":\"" + Old + "\",\"uuid\":\"" + uuid + "\",\"message\":{\"role\":\"user\",\"content\":" + JsonStr(text) + "}}";
    private static string AsstStr(string uuid, string text) =>
        "{\"type\":\"assistant\",\"sessionId\":\"" + Old + "\",\"uuid\":\"" + uuid + "\",\"message\":{\"role\":\"assistant\",\"content\":" + JsonStr(text) + "}}";
    private static string UserArr(string uuid, string blocks) =>
        "{\"type\":\"user\",\"sessionId\":\"" + Old + "\",\"uuid\":\"" + uuid + "\",\"message\":{\"role\":\"user\",\"content\":" + blocks + "}}";
    private static string UserMetaArr(string uuid, string blocks) =>
        "{\"type\":\"user\",\"isMeta\":true,\"sessionId\":\"" + Old + "\",\"uuid\":\"" + uuid + "\",\"message\":{\"role\":\"user\",\"content\":" + blocks + "}}";
    private static string AsstArr(string uuid, string blocks) =>
        "{\"type\":\"assistant\",\"sessionId\":\"" + Old + "\",\"uuid\":\"" + uuid + "\",\"message\":{\"role\":\"assistant\",\"content\":" + blocks + "}}";
    private static readonly string SysInit =
        "{\"type\":\"system\",\"subtype\":\"init\",\"sessionId\":\"" + Old + "\",\"uuid\":\"s0\"}";
    private const string ToolsUse =
        "[{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"Bash\",\"input\":{}},{\"type\":\"tool_use\",\"id\":\"t2\",\"name\":\"Read\",\"input\":{}}";
    private const string ToolsResult =
        "[{\"type\":\"tool_result\",\"tool_use_id\":\"t1\",\"content\":\"r1\"},{\"type\":\"tool_result\",\"tool_use_id\":\"t2\",\"content\":\"r2\"}";

    private string WriteFile(string name, params string[] lines)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, string.Join("\n", lines) + "\n");
        return p;
    }

    // Файл с недописанной последней строкой (без завершающего переноса)
    private string WriteTruncated(string name, string partialLine, params string[] completeLines)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, string.Join("\n", completeLines) + "\n" + partialLine);
        return p;
    }

    private static string Dst() => Path.Combine(Path.GetTempPath(), "brancher_dst_" + Guid.NewGuid().ToString("N") + ".jsonl");

    // Общий сценарный файл: три хода (A, B, C) с tools и isMeta-вставкой между B и C
    private string WriteScenario(out string a, out string b, out string c)
    {
        a = "АЛЬФА: разобрать схему ветвления чата и написать тест";
        b = "БЕТА: теперь собери префикс до этого сообщения, пожалуйста";
        c = "ГАММА: третий вопрос разговора, уже не нужен в ветке";
        return WriteFile("scenario.jsonl",
            SysInit,
            UserStr("u1", a),
            AsstArr("a1", "[{\"type\":\"text\",\"text\":\"ответ 1\"}," + ToolsUse + "]"),
            UserArr("u2", ToolsResult),
            AsstStr("a2", "ответ 2"),
            UserStr("u3", b),
            AsstStr("a3", "ответ 3"),
            UserMetaArr("u4", "[{\"type\":\"text\",\"text\":\"Continue from where you left off.\"}]"),
            UserStr("u5", c),
            AsstStr("a4", "ответ 4"));
    }

    [Fact]
    public void ГраницаВСередине_РежетПередСледующимПрмптом()
    {
        var src = WriteScenario(out var a, out var b, out _);
        var dst = Dst();

        var res = TranscriptBrancher.Branch(src, [a, b], New, dst);

        res.Ok.Should().BeTrue(res.Reason);
        res.CutLine.Should().Be(8); // до строки u5 (индекс 8), включительно — весь ход B
        var got = File.ReadAllLines(dst);
        got.Length.Should().Be(8);
        got.Where(l => !l.Contains("\"sessionId\":\"" + New + "\"")).Should().BeEmpty();
        got[7].Should().Contain("Continue from where you left off"); // хвост префикса = isMeta-вставка, не промпт
        File.ReadAllText(src).Should().Contain("\"sessionId\":\"" + Old + "\""); // источник не тронут
    }

    [Fact]
    public void ВетвлениеОтПоследнегоХода_КопируетВесьФайл()
    {
        var src = WriteScenario(out _, out _, out var c);
        var dst = Dst();

        var res = TranscriptBrancher.Branch(src, [c], New, dst);

        res.Ok.Should().BeTrue(res.Reason);
        res.CutLine.Should().Be(10); // всего файла
        var got = File.ReadAllLines(dst);
        got.Length.Should().Be(10);
        got[9].Should().Contain("ответ 4");
    }

    [Fact]
    public void ПараллельныеToolUse_ЗавершённыйХодПринятЦеликом()
    {
        var a = "АЛЬФА: разобрать схему ветвления чата и написать тест";
        var src = WriteFile("par.jsonl",
            SysInit,
            UserStr("u1", a),
            AsstArr("a1", ToolsUse + ",{\"type\":\"text\",\"text\":\"делаю\"}"),
            UserArr("u2", ToolsResult),
            AsstArr("a2", "[{\"type\":\"text\",\"text\":\"готово\"}]"));
        var dst = Dst();

        var res = TranscriptBrancher.Branch(src, [a], New, dst);

        res.Ok.Should().BeTrue(res.Reason);
        res.CutLine.Should().Be(5); // ход завершён и сбалансирован (t1/t2 парны) — весь файл
        var got = File.ReadAllLines(dst);
        got.Length.Should().Be(5);
        got[3].Should().Contain("\"t1\"").And.Contain("\"t2\""); // оба tool_result сохранены
    }

    [Fact]
    public void БитаяПоследняяСтрока_Отбрасывается()
    {
        var a = "АЛЬФА: разобрать схему ветвления чата и написать тест";
        var partial = "{\"type\":\"assistant\",\"sessionId\":\"" + Old + "\",\"uuid\":\"a9\",\"message\":{\"role\":\"assistant\",\"content\":\"обрыв";
        var src = WriteTruncated("trunc.jsonl", partial,
            SysInit,
            UserStr("u1", a),
            AsstArr("a1", ToolsUse + ",{\"type\":\"text\",\"text\":\"делаю\"}"),
            UserArr("u2", ToolsResult),
            AsstArr("a2", "[{\"type\":\"text\",\"text\":\"готово\"}]"));
        var dst = Dst();

        var res = TranscriptBrancher.Branch(src, [a], New, dst);

        res.Ok.Should().BeTrue(res.Reason);
        res.CutLine.Should().Be(5); // только пять полных строк
        var got = File.ReadAllLines(dst);
        got.Length.Should().Be(5);
        got.Should().NotContain(l => l.Contains("обрыв"));
    }

    [Fact]
    public void НесоответствующийЯкорь_Отказ()
    {
        var src = WriteFile("m1.jsonl",
            SysInit,
            UserStr("u1", "текст промта один с запасом символов"),
            AsstStr("a1", "ответ"));
        var dst = Dst();

        var res = TranscriptBrancher.Branch(src, ["совершенно другой текст, которого нет в файле"], New, dst);

        res.Ok.Should().BeFalse();
        res.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void НеполноеСопоставление_Отказ()
    {
        // первое (не якорное) сообщение не нашлось, якорь нашёлся — «часть сошлась» не считается
        var src = WriteFile("m2.jsonl",
            SysInit,
            UserStr("u1", "текст промта один с запасом символов"),
            AsstStr("a1", "ответ 1"),
            UserStr("u2", "текст промта два с запасом символов"),
            AsstStr("a2", "ответ 2"));
        var dst = Dst();

        var res = TranscriptBrancher.Branch(
            src,
            ["нет такого текста в транскрипте вообще", "текст промта два с запасом символов"],
            New, dst);

        res.Ok.Should().BeFalse();
        res.Reason.Should().Contain("неполное сопоставление");
    }

    [Fact]
    public void НемонотонноеСопоставление_Отказ()
    {
        // A (якорь) живёт в П3, B — в P2: первое найденное промпт-содержание
        // «задом наперед» по файлу → границы разъезжаются
        var src = WriteFile("m3.jsonl",
            SysInit,
            UserStr("u1", "нейтральный промпт нулевой без следов других"),
            AsstStr("a1", "ответ 1"),
            UserStr("u2", "нейтральный промпт нулевой без следов других бета-маркер"),
            AsstStr("a2", "ответ 2"),
            UserStr("u3", "нейтральный промпт нулевой без следов других зета-маркер"),
            AsstStr("a3", "ответ 3"));
        var dst = Dst();

        var res = TranscriptBrancher.Branch(
            src,
            ["зета-маркер", "бета-маркер"], // якорь зета-маркер (П3), сосед бета-маркер (П2)
            New, dst);

        res.Ok.Should().BeFalse();
        res.Reason.Should().Contain("монотон");
    }

    [Fact]
    public void КороткийЯкорьБезСоседа_Отказ()
    {
        // «ok» нашлось бы, но это единственный якорь и он слишком короткий:
        // без подтверждения предыдущим сообщением совпадение неоднозначно
        var src = WriteFile("m4.jsonl",
            SysInit,
            UserStr("u1", "ok, продолжай работу над схемой ветвления"),
            AsstStr("a1", "ответ"));
        var dst = Dst();

        var res = TranscriptBrancher.Branch(src, ["ok"], New, dst);

        res.Ok.Should().BeFalse();
        res.Reason.Should().Contain("коротк");
    }

    [Fact]
    public void КороткийЯкорьСПодтверждениемСоседа_Принят()
    {
        var a = "альфа: длинное первое сообщение с запасом символов для надёжности";
        var src = WriteFile("m5.jsonl",
            SysInit,
            UserStr("u1", a),
            AsstStr("a1", "ответ 1"),
            UserStr("u2", "ok, и ещё длинный контекст чтобы было отчего резать"),
            AsstStr("a2", "ответ 2"));
        var dst = Dst();

        var res = TranscriptBrancher.Branch(src, [a, "ok"], New, dst);

        res.Ok.Should().BeTrue(res.Reason); // короткий якорь подтверждён: его промпт — следующий за соседом
        res.CutLine.Should().Be(5);
    }
}
