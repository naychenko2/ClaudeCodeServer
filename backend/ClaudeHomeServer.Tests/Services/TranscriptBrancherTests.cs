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
        res.AnchorTurnExcluded.Should().BeFalse(); // завершённый ход — отступа не было
        var got = File.ReadAllLines(dst);
        got.Length.Should().Be(5);
        got[3].Should().Contain("\"t1\"").And.Contain("\"t2\""); // оба tool_result сохранены
    }

    // Непарный tool_use в последнем ходе: граница отступает к началу хода (CutLine = промпт)
    // И признак AnchorTurnExcluded — истинный: якорный ход в ветку не попал.
    // В ветку попадают завершённые ходы (с парными tool_use/tool_result), поэтому
    // LastTurnIsBalanced на них — true, а отступ срабатывает только на оборванном.
    [Fact]
    public void НепарныйToolUse_ОтступГраницы_ПризнакИстинный()
    {
        var a = "альфа: первое сообщение разговора с запасом символов для якоря";
        var src = WriteFile("unbal2.jsonl",
            SysInit,
            // завершённый ход 1: text + tool_use t1 + tool_result t1
            UserStr("u1", a),
            AsstArr("a1", "[{\"type\":\"text\",\"text\":\"дело\"},{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"Bash\",\"input\":{}}"),
            UserArr("u1r", "[{\"type\":\"tool_result\",\"tool_use_id\":\"t1\",\"content\":\"ok\"}]"),
            AsstStr("a2", "готово"),
            // оборванный ход 2: tool_use t9 без tool_result
            UserStr("u2", "второй: продолжай и запусти ещё один инструмент"),
            AsstArr("a3", "[{\"type\":\"text\",\"text\":\"начал\"},{\"type\":\"tool_use\",\"id\":\"t9\",\"name\":\"Bash\",\"input\":{}}]"));
        var dst = Dst();

        var res = TranscriptBrancher.Branch(src, [a, "второй: продолжай и запусти ещё один инструмент"], New, dst);

        res.Ok.Should().BeTrue(res.Reason);
        res.AnchorTurnExcluded.Should().BeTrue();
        res.CutLine.Should().Be(5); // начало оборванного хода = промпт u2 (индекс 5)
        var got = File.ReadAllLines(dst);
        got.Should().HaveCount(5); // до u2, оборванный ход B не в ветке
        got[3].Should().Contain("\"t1\""); // завершённый ход 1 сохранён целиком
    }

    // Замечание 1 ревью: sessionId меняется ТОЛЬКО на верхнем уровне записи.
    // Вложенное \"sessionId\":\"…\" (экранированное, внутри tool_result) прежняя регулярка
    // портила; Utf8JsonReader-путь берёт байтовые границы значения верхнеуровневого свойства.
    [Fact]
    public void ВложенныйSessionIdToolResult_НеТронут_ВерхнийЗаменён()
    {
        var a = "альфа: разбор схемы ветвления с чтением чужого транскрипта";
        var b = "бета: запусти инструмент и доложи результат";
        var c = "гамма: ещё один вопрос разговора, не нужен в ветке";
        // tool_result, внутри которого — ЧУЖОЙ транскрипт: экранированное "sessionId":"other-id"
        // (в строке .jsonl это \\\\"sessionId\\\\\":" — вложение во вложенный JSON)
        const string foreignTranscript =
            "{\"type\":\"user\",\"sessionId\":\"22222222-3333-4444-5555-666666666666\",\"uuid\":\"x1\",\"message\":{\"role\":\"user\",\"content\":\"другая сессия\"}}";
        var toolResultBlocks =
            "[{\"type\":\"tool_result\",\"tool_use_id\":\"t9\",\"content\":" + JsonStr(foreignTranscript) + "}";
        var src = WriteFile("nested.jsonl",
            SysInit,
            UserStr("u1", a),
            AsstStr("a1", "ответ 1"),
            UserStr("u2", b),
            AsstArr("a2", "[{\"type\":\"tool_use\",\"id\":\"t9\",\"name\":\"Bash\",\"input\":{}}]"),
            UserArr("u3", toolResultBlocks + "]"),
            AsstStr("a3", "ответ 2"),
            UserStr("u4", c));
        var dst = Dst();

        var res = TranscriptBrancher.Branch(src, [a, b], New, dst);

        res.Ok.Should().BeTrue(res.Reason);
        res.CutLine.Should().Be(7); // до u4=c (индекс 7) — ход B (u2..a3) целиком в ветке
        var got = File.ReadAllLines(dst);
        got.Should().HaveCount(7);
        // верхнеуровневый sessionId во ВСЕХ записях → новый
        got.Should().OnlyContain(l => l.Contains("\"sessionId\":\"" + New + "\""));
        // вложенное (экранированное) sessionId чужого транскрипта НЕ тронуто
        got[5].Should().Contain(JsonStr(foreignTranscript));
    }

    // Кириллица в content: байт в байт, кроме самого sessionId (пересериализация JsonNode
    // переэкранировала бы не-ASCII, а WritePrefix — точечная замена).
    [Fact]
    public void КириллицаВContent_БайтВБайт_КромеSessionId()
    {
        const string cyr = "Разбор схемы: привет, мир, приветствие от пользователя.";
        var src = WriteFile("cyr.jsonl",
            UserStr("u1", cyr),
            AsstStr("a1", cyr));
        var dst = Dst();

        var res = TranscriptBrancher.Branch(src, [cyr], New, dst);

        res.Ok.Should().BeTrue(res.Reason);
        var got = File.ReadAllLines(dst);
        // исходная строка, где заменено только значение sessionId
        var expected = UserStr("u1", cyr).Replace("\"sessionId\":\"" + Old + "\"",
            "\"sessionId\":\"" + New + "\"");
        got[0].Should().Be(expected);
        expected = AsstStr("a1", cyr).Replace("\"sessionId\":\"" + Old + "\"",
            "\"sessionId\":\"" + New + "\"");
        got[1].Should().Be(expected);
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

    // --- Точный якорь по uuid (шаг 5, §4 «Точный якорь») ---

    // Главный смысл шага: при точном якоре текстовое сопоставление НЕ запускается вовсе.
    // Доказательство — заведомо негодные тексты («да»): текстовый путь на них отказывает
    // (см. КороткийЯкорьБезПодтверждения_Отказ), а с uuid граница находится ровно та же,
    // что в ГраницаВСередине_РежетПередСледующимПрмптом.
    [Fact]
    public void ТочныйЯкорь_ГраницаПоUuid_ТекстНеСпрашивается()
    {
        var src = WriteScenario(out _, out _, out _);
        var dst = Dst();

        var res = TranscriptBrancher.Branch(src, ["да"], New, dst, anchorUuid: "a3");

        res.Ok.Should().BeTrue(res.Reason);
        res.CutLine.Should().Be(8); // до строки u5 — весь ход B, как и у текстового пути
        File.ReadAllLines(dst).Should().HaveCount(8);
    }

    // Ветвление от ПОСЛЕДНЕГО хода: следующего промпта нет — префикс до конца файла.
    [Fact]
    public void ТочныйЯкорь_ПоследнийХод_РежетДоКонцаФайла()
    {
        var src = WriteScenario(out _, out _, out _);
        var dst = Dst();

        var res = TranscriptBrancher.Branch(src, ["да"], New, dst, anchorUuid: "a4");

        res.Ok.Should().BeTrue(res.Reason);
        res.CutLine.Should().Be(10);
    }

    // Якорь есть, но в ЭТОМ файле его нет (короткая копия транскрипта, чужой файл) —
    // не отказ, а честный откат на прежний текстовый путь.
    [Fact]
    public void ТочныйЯкорь_НеНайден_ПадаетНаТекстовыйПуть()
    {
        var src = WriteScenario(out var a, out var b, out _);
        var dst = Dst();

        var res = TranscriptBrancher.Branch(src, [a, b], New, dst, anchorUuid: "нет-такого-uuid");

        res.Ok.Should().BeTrue(res.Reason);
        res.CutLine.Should().Be(8);
    }

    // Игла ищется с ведущей кавычкой: "parentUuid"/"leafUuid" соседних записей не должны
    // сойти за собственный uuid записи — иначе граница уехала бы на ход назад.
    [Fact]
    public void ТочныйЯкорь_НеПутаетParentUuid()
    {
        var a = "альфа: первое сообщение разговора с запасом символов для якоря";
        var b = "бета: второе сообщение разговора с запасом символов для якоря";
        var src = WriteFile("uuid-parent.jsonl",
            SysInit,
            UserStr("u1", a),
            // ссылается на a2 ВПЕРЁД: наивный поиск подстроки "uuid":"a2" встал бы здесь
            "{\"type\":\"assistant\",\"parentUuid\":\"a2\",\"leafUuid\":\"a2\",\"uuid\":\"a1\",\"message\":{\"role\":\"assistant\",\"content\":\"ответ 1\"}}",
            UserStr("u2", b),
            AsstStr("a2", "ответ 2"));
        var dst = Dst();

        var res = TranscriptBrancher.Branch(src, ["да"], New, dst, anchorUuid: "a2");

        res.Ok.Should().BeTrue(res.Reason);
        res.CutLine.Should().Be(5); // весь файл, а не обрез по первому ходу
    }

    // Хвостовая проверка считается по ХОДУ якоря (от его промпта), а не от записи-якоря:
    // непарный tool_use внутри хода обязан отправить границу к началу этого хода.
    [Fact]
    public void ТочныйЯкорь_НепарныйToolUse_ОтступаетКНачалуХода()
    {
        var a = "альфа: первое сообщение разговора с запасом символов для якоря";
        var b = "бета: второе сообщение разговора с запасом символов для якоря";
        var src = WriteFile("uuid-unbalanced.jsonl",
            SysInit,
            UserStr("u1", a),
            AsstStr("a1", "ответ 1"),
            UserStr("u2", b),
            AsstArr("a2", "[{\"type\":\"text\",\"text\":\"начал\"},{\"type\":\"tool_use\",\"id\":\"t9\",\"name\":\"Bash\",\"input\":{}}]"),
            AsstStr("a3", "ход оборван: результата инструмента нет"));
        var dst = Dst();

        var res = TranscriptBrancher.Branch(src, ["да"], New, dst, anchorUuid: "a3");

        res.Ok.Should().BeTrue(res.Reason);
        res.CutLine.Should().Be(3); // до промпта u2 — оборванный ход B в ветку не попал
    }

    // Недописанная последняя строка отброшена вместе с якорем — тогда его считаем
    // отсутствующим и идём текстовым путём, а не режем по несуществующей строке.
    [Fact]
    public void ТочныйЯкорь_НаНедописаннойСтроке_ПадаетНаТекстовыйПуть()
    {
        var a = "альфа: первое сообщение разговора с запасом символов для якоря";
        var src = WriteTruncated("uuid-truncated.jsonl",
            "{\"type\":\"assistant\",\"uuid\":\"a9\",\"message\":{\"role\":\"assis",
            SysInit,
            UserStr("u1", a),
            AsstStr("a1", "ответ 1"));
        var dst = Dst();

        var res = TranscriptBrancher.Branch(src, [a], New, dst, anchorUuid: "a9");

        res.Ok.Should().BeTrue(res.Reason);
        res.CutLine.Should().Be(3);
    }
}
