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
    // Переводы строк обязаны уезжать в \n: без этого многострочный текст (тик /loop,
    // recall-префикс) разорвал бы строку .jsonl и запись перестала бы быть записью
    private static string JsonStr(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"")
        .Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
    private static string UserStr(string uuid, string text) =>
        "{\"type\":\"user\",\"sessionId\":\"" + Old + "\",\"uuid\":\"" + uuid + "\",\"message\":{\"role\":\"user\",\"content\":" + JsonStr(text) + "}}";
    // Промпт со временем записи — как пишет живой CLI (поле timestamp, ISO-8601)
    private static string UserStrAt(string uuid, string text, string timestamp) =>
        "{\"type\":\"user\",\"sessionId\":\"" + Old + "\",\"uuid\":\"" + uuid + "\",\"timestamp\":"
        + JsonStr(timestamp) + ",\"message\":{\"role\":\"user\",\"content\":" + JsonStr(text) + "}}";
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
    public void ПропущенноеЗвеноЦепочки_ЯкорьВсёРавноНайден()
    {
        // Сообщение истории, которого нет в транскрипте (ход не доехал в этот файл) — штатное
        // расхождение, а не повод отказать: задача f18e784c, чат прода 955e0ba2, где четыре
        // таких звена подряд делали невозможным ветвление от ЛЮБОГО более позднего места.
        var anchor = "текст промта два, длинный, с надёжным запасом значимых символов";
        var src = WriteFile("m2.jsonl",
            SysInit,
            UserStr("u1", "текст промта один с запасом символов"),
            AsstStr("a1", "ответ 1"),
            UserStr("u2", anchor),
            AsstStr("a2", "ответ 2"));
        var dst = Dst();

        var res = TranscriptBrancher.Branch(
            src,
            ["нет такого текста в транскрипте вообще", anchor],
            New, dst);

        res.Ok.Should().BeTrue(res.Reason);
        res.CutLine.Should().Be(5);
    }

    [Fact]
    public void ЦепочкаЗадаётНижнююГраницуПоиска_ЯкорьРаньшеСоседаНеБерётся()
    {
        // Без времени цепочка задаёт нижнюю границу: якорь ищется ПОСЛЕ промпта соседа.
        // Здесь «бета-маркер» живёт в П2, а сосед «зета-маркер» — в П3, то есть якорь
        // раньше соседа: назад резак не смотрит и отказывает, а не режет наугад.
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
            ["зета-маркер", "бета-маркер"], // якорь бета-маркер (П2), сосед зета-маркер (П3)
            New, dst);

        res.Ok.Should().BeFalse();
        res.Reason.Should().Contain("не найден");
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

    // --- include=beforePrompt: граница на самом якорном промпте ---

    // Блокер финального ревью: без include резак всегда резал по концу хода, и у
    // beforePrompt в памяти ветки оставались и якорный вопрос, и прежний ответ на него —
    // ровно то, от чего пользователь уходит, нажимая «Ветвление» под своим сообщением.
    [Fact]
    public void BeforePrompt_ГраницаНаЯкорномПромпте_ХодЯкоряНеВходит()
    {
        var src = WriteScenario(out var a, out var b, out _);
        var dst = Dst();

        var res = TranscriptBrancher.Branch(src, [a, b], New, dst,
            include: TranscriptBrancher.BranchInclude.BeforePrompt);

        res.Ok.Should().BeTrue(res.Reason);
        res.CutLine.Should().Be(5); // до строки u3=b (индекс 5): сам якорный промпт не входит
        res.AnchorTurnExcluded.Should().BeFalse("отступа не было — это штатная граница beforePrompt");
        var got = File.ReadAllLines(dst);
        got.Should().HaveCount(5);
        got.Should().NotContain(l => l.Contains(b));
        got.Should().NotContain(l => l.Contains("ответ 3"));
    }

    // Тот же якорь с точным uuid: граница считается от промпта ЕГО хода, а не от записи-якоря
    [Fact]
    public void BeforePrompt_ТочныйЯкорь_ГраницаНаПромптеЕгоХода()
    {
        var src = WriteScenario(out _, out var b, out _);
        var dst = Dst();

        var res = TranscriptBrancher.Branch(src, ["да"], New, dst, anchorUuid: "a3",
            include: TranscriptBrancher.BranchInclude.BeforePrompt);

        res.Ok.Should().BeTrue(res.Reason);
        res.CutLine.Should().Be(5);
        File.ReadAllLines(dst).Should().NotContain(l => l.Contains(b));
    }

    // Якорь — первый промпт разговора: до него нет ни одного хода, транскрипт без ходов
    // отдавать CLI нельзя — честный отказ вместо молча пустой памяти.
    [Fact]
    public void BeforePrompt_ЯкорьПервыйПромпт_Отказ()
    {
        var src = WriteScenario(out var a, out _, out _);
        var dst = Dst();

        var res = TranscriptBrancher.Branch(src, [a], New, dst,
            include: TranscriptBrancher.BranchInclude.BeforePrompt);

        res.Ok.Should().BeFalse();
        res.Reason.Should().Contain("ветвить нечего");
        File.Exists(dst).Should().BeFalse();
    }

    // Хвостовая проверка у beforePrompt считается по ПРЕДЫДУЩЕМУ ходу (якорный в ветку и
    // так не входит): его непарный tool_use отправляет границу к началу этого хода.
    [Fact]
    public void BeforePrompt_НепарныйToolUseПредыдущегоХода_ОтступИПризнак()
    {
        var a = "альфа: первое сообщение разговора с запасом символов для якоря";
        var b = "бета: второе сообщение разговора с запасом символов для якоря";
        var c = "гамма: третье сообщение разговора с запасом символов для якоря";
        var src = WriteFile("before-unbalanced.jsonl",
            SysInit,
            UserStr("u1", a),
            AsstStr("a1", "ответ 1"),
            // ход B оборван: tool_use t9 без результата
            UserStr("u2", b),
            AsstArr("a2", "[{\"type\":\"text\",\"text\":\"начал\"},{\"type\":\"tool_use\",\"id\":\"t9\",\"name\":\"Bash\",\"input\":{}}]"),
            UserStr("u3", c),
            AsstStr("a3", "ответ 3"));
        var dst = Dst();

        var res = TranscriptBrancher.Branch(src, [a, b, c], New, dst,
            include: TranscriptBrancher.BranchInclude.BeforePrompt);

        res.Ok.Should().BeTrue(res.Reason);
        res.AnchorTurnExcluded.Should().BeTrue();
        res.CutLine.Should().Be(3); // отступ к началу оборванного хода B (строка u2)
        File.ReadAllLines(dst).Should().HaveCount(3);
    }

    // --- Класс «насыщенный персона-чат» (задача f18e784c) ---
    //
    // Прежние тесты этого файла — чистые разговоры на 2–3 промптах, и все были зелёными,
    // когда на проде ветвление работало в 20 местах из 66. Дыру закрывает корпусная фикстура
    // с анатомией живого чата 955e0ba2 СРАЗУ: повторяющиеся дословно тики /loop, повторяющийся
    // шаблон доклада персоны-исполнителя, повтор короткого «продолжай», recall-префикс перед
    // текстом хода и сообщения истории, чьих промптов в транскрипте нет вовсе.
    // Утверждение — покрытие с точными числами: ровно 10 верных границ, ровно 2 отказа,
    // ни одного неверного среза.

    private const string LoopTick =
        "[СИСТЕМНАЯ ДИРЕКТИВА — ТИК ОЖИДАНИЯ ЦИКЛА 1/20]\nТы в фазе ожидания по своему маркеру. "
        + "Проверь, не завершился ли делегированный шаг, и продолжай ждать.";
    private const string ExecutorReport =
        "Персона-исполнитель Код-ревьюер (Глеб) завершила делегированную тобой задачу и прислала отчёт. "
        + "Ознакомься с результатом и реши, что делать дальше.";
    private const string RecallPrefix =
        "## Память по теме\n- ветвление чата обсуждали ранее\n\n---\n\n";

    // Время истории (Unix-мс) и транскрипта (ISO) — лаг 3 с, как у живой пары
    private static long HistMs(int step) =>
        DateTimeOffset.Parse("2026-09-18T18:00:00Z").AddSeconds(step * 600).ToUnixTimeMilliseconds();
    private static string TrMs(int step) =>
        DateTimeOffset.Parse("2026-09-18T18:00:00Z").AddSeconds(step * 600 + 3).ToUniversalTime()
            .ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    private string WriteSaturatedChat()
    {
        var alpha = "альфа: давай добавим ветвление чата, чтобы можно было уйти в сторону с любого шага";
        var omega = "омега: последнее длинное сообщение разговора, уже после мержа изменений";
        return WriteFile("saturated.jsonl",
            SysInit,
            UserStrAt("u0", RecallPrefix + alpha, TrMs(0)),   // 1  — recall клеится ПРЕФИКСОМ
            AsstStr("a0", "ответ 0"),                          // 2
            UserStrAt("u1", ExecutorReport, TrMs(1)),          // 3
            AsstStr("a1", "ответ 1"),                          // 4
            UserStrAt("u2", LoopTick, TrMs(2)),                // 5  — тик
            AsstStr("a2", "ответ 2"),                          // 6
            UserStrAt("u3", LoopTick, TrMs(3)),                // 7  — тик, текст тот же
            AsstStr("a3", "ответ 3"),                          // 8
            // сообщения истории №4 «Делай» и №5 «продолжай» в транскрипт не попали
            UserStrAt("u6", "продолжай", TrMs(6)),             // 9
            AsstStr("a6", "ответ 6"),                          // 10
            UserStrAt("u7", ExecutorReport, TrMs(7)),          // 11 — доклад, текст тот же
            AsstStr("a7", "ответ 7"),                          // 12
            UserStrAt("u8", LoopTick, TrMs(8)),                // 13 — тик, текст тот же
            AsstStr("a8", "ответ 8"),                          // 14
            UserStrAt("u9", "продолжай", TrMs(9)),             // 15 — повтор короткого текста
            AsstStr("a9", "ответ 9"),                          // 16
            UserStrAt("u10", "мержим", TrMs(10)),              // 17
            AsstStr("a10", "ответ 10"),                        // 18
            UserStrAt("u11", omega, TrMs(11)),                 // 19
            AsstStr("a11", "ответ 11"));                       // 20
    }

    // Сообщения истории: текст, время, ожидаемая граница (null — обязан быть отказ)
    public static TheoryData<int, string, int, int?> SaturatedHistory() => new()
    {
        { 0,  "альфа: давай добавим ветвление чата, чтобы можно было уйти в сторону с любого шага", 0, 3 },
        { 1,  ExecutorReport, 1, 5 },
        { 2,  LoopTick, 2, 7 },
        { 3,  LoopTick, 3, 9 },   // третий дословный тик — НЕ первый: граница 9, а не 7
        { 4,  "Делай", 4, null },       // промпта нет в транскрипте, двойников по тексту нет
        { 5,  "продолжай", 5, null },   // промпта нет, а двойники есть — их отсекает окно лага
        { 6,  "продолжай", 6, 11 },
        { 7,  ExecutorReport, 7, 13 },
        { 8,  LoopTick, 8, 15 },
        { 9,  "продолжай", 9, 17 },     // повтор короткого текста — граница 17, а не 11
        { 10, "мержим", 10, 19 },
        { 11, "омега: последнее длинное сообщение разговора, уже после мержа изменений", 11, 21 },
    };

    [Theory]
    [MemberData(nameof(SaturatedHistory))]
    public void НасыщенныйПерсонаЧат_ГраницаПоВремени(int index, string text, int step, int? expectedCut)
    {
        var src = WriteSaturatedChat();
        var dst = Dst();

        var res = TranscriptBrancher.Branch(src, [text], New, dst,
            anchorTimestampMs: HistMs(step));

        if (expectedCut is null)
        {
            res.Ok.Should().BeFalse($"сообщение №{index} в транскрипт не попало — резать наугад нельзя");
            res.Reason.Should().NotBeNullOrWhiteSpace();
        }
        else
        {
            res.Ok.Should().BeTrue(res.Reason);
            res.CutLine.Should().Be(expectedCut, $"сообщение №{index}");
        }
    }

    // Итог по фикстуре одним утверждением: покрытие ровно 10 из 12, и ни одного неверного среза
    [Fact]
    public void НасыщенныйПерсонаЧат_Покрытие10Из12()
    {
        var src = WriteSaturatedChat();
        var ok = 0;
        var wrong = 0;
        foreach (var row in SaturatedHistory())
        {
            var (index, text, step, expected) = ((int)row[0], (string)row[1], (int)row[2], (int?)row[3]);
            var res = TranscriptBrancher.Branch(src, [text], New, Dst(), anchorTimestampMs: HistMs(step));
            if (!res.Ok) continue;
            ok++;
            if (res.CutLine != expected) wrong++;
        }

        ok.Should().Be(10);
        wrong.Should().Be(0, "неверная граница хуже отказа: ветка ушла бы не от того шага");
    }

    // Точный якорь из РАЗОШЕДШЕЙСЯ копии: у чата, мигрировавшего между провайдерами, хвост
    // хода снимается по одной копии транскрипта, а резак берёт самую длинную. В живом 955e0ba2
    // uuid хвоста ходов от 21.09 оказался последней записью трёх коротких копий от 18.09, и в
    // длинной копии тот же uuid лежит в середине — ветка молча резалась позапрошлым днём.
    // Противоречие по времени снимает доверие к uuid, работу продолжает текстовый путь.
    [Fact]
    public void ТочныйЯкорь_ЗаписьСтарееСообщения_ПадаетНаТекстовыйПуть()
    {
        var anchor = "мержим, выкладывай на бой и проверим живьём";
        var src = WriteFile("stale-uuid.jsonl",
            SysInit,                                                              // 0
            UserStrAt("u1", "старый разговор позапрошлого дня с запасом символов",
                "2026-09-18T19:00:00.000Z"),                                      // 1
            // хвост старого хода — именно на него показывает протухший uuid
            "{\"type\":\"assistant\",\"sessionId\":\"" + Old + "\",\"uuid\":\"tail-old\","
                + "\"timestamp\":\"2026-09-18T19:57:53.411Z\",\"message\":{\"role\":\"assistant\","
                + "\"content\":\"ответ 1\"}}",                                     // 2
            UserStrAt("u2", anchor, "2026-09-21T16:00:03.000Z"),                  // 3
            AsstStr("a2", "ответ 2"));                                             // 4
        var dst = Dst();

        var res = TranscriptBrancher.Branch(src, [anchor], New, dst, anchorUuid: "tail-old",
            anchorTimestampMs: DateTimeOffset.Parse("2026-09-21T16:00:00Z").ToUnixTimeMilliseconds());

        res.Ok.Should().BeTrue(res.Reason);
        res.CutLine.Should().Be(5, "граница по тексту и времени — конец файла, а не строка протухшего uuid");
    }

    // Чат без времени (95 чатов прода из 400 не имеют Timestamp ни у одного сообщения) —
    // работает цепочка, и она обязана искать якорь ПОСЛЕ промпта предыдущего сообщения.
    // Прежний поиск с начала файла схлопывал повтор на первое вхождение: ровно так отказывал
    // живой c2480bce, где «пересобери прод» звучало дважды.
    [Fact]
    public void БезВремени_ПовторТекста_ЦепочкаБерётВтороеВхождение()
    {
        var start = "старт: длинное первое сообщение разговора с запасом значимых символов";
        var repeat = "пересобери прод, пожалуйста, целиком и выложи его на бой";
        var src = WriteFile("no-time-repeat.jsonl",
            SysInit,
            UserStr("u1", start),                                   // 1
            AsstStr("a1", "ответ 1"),                               // 2
            UserStr("u2", repeat),                                  // 3 — первое вхождение
            AsstStr("a2", "ответ 2"),                               // 4
            UserStr("u3", ExecutorReport),                          // 5 — служебный промпт между ними
            AsstStr("a3", "ответ 3"),                               // 6
            UserStr("u4", repeat),                                  // 7 — повтор, он и есть якорь
            AsstStr("a4", "ответ 4"));                              // 8
        var dst = Dst();

        var res = TranscriptBrancher.Branch(src, [start, repeat, repeat], New, dst);

        res.Ok.Should().BeTrue(res.Reason);
        res.CutLine.Should().Be(9); // ход ВТОРОГО вхождения (до конца файла), а не первого
    }

    // Два дословно одинаковых промпта ВНУТРИ окна лага — не выдумка: в живом 955e0ba2 тики
    // /loop местами идут подряд с интервалом в секунды (промпты на строках 276 и 283).
    // Среди кандидатов окна берётся БЛИЖАЙШИЙ по времени, иначе ветка уехала бы на ход вперёд.
    [Fact]
    public void ДваОдинаковыхПромптаВОкне_БерётсяБлижайшийПоВремени()
    {
        var src = WriteFile("close-twins.jsonl",
            SysInit,
            UserStrAt("u1", LoopTick, "2026-09-18T18:00:03.000Z"),  // 1 — лаг 3 с
            AsstStr("a1", "ответ 1"),                                // 2
            UserStrAt("u2", LoopTick, "2026-09-18T18:01:03.000Z"),  // 3 — лаг 63 с, тоже в окне
            AsstStr("a2", "ответ 2"),                                // 4
            UserStrAt("u3", "мержим", "2026-09-18T18:02:03.000Z"),  // 5
            AsstStr("a3", "ответ 3"));                               // 6
        var dst = Dst();

        var res = TranscriptBrancher.Branch(src, [LoopTick], New, dst,
            anchorTimestampMs: DateTimeOffset.Parse("2026-09-18T18:00:00Z").ToUnixTimeMilliseconds());

        res.Ok.Should().BeTrue(res.Reason);
        res.CutLine.Should().Be(3); // ход ПЕРВОГО тика, а не второго (тот дал бы 5)
    }

    // Короткий якорь сверяется по границам слова: «делай» не считается найденным внутри
    // «сделай» — иначе у сообщения, чьего промпта в файле нет, единственным кандидатом
    // оказался бы чужой промпт с подстрокой, и срез ушёл бы не туда молча.
    [Fact]
    public void КороткийЯкорь_ПодстрокаВнутриСлова_НеСчитаетсяСовпадением()
    {
        var src = WriteFile("word-bound.jsonl",
            SysInit,
            UserStrAt("u1", "сделай уже наконец", "2026-09-18T18:00:03.000Z"),
            AsstStr("a1", "ответ 1"));
        var dst = Dst();

        var res = TranscriptBrancher.Branch(src, ["делай"], New, dst,
            anchorTimestampMs: DateTimeOffset.Parse("2026-09-18T18:00:00Z").ToUnixTimeMilliseconds());

        res.Ok.Should().BeFalse();
        res.Reason.Should().Contain("не найден");
    }

    // Часовой пояс: история — Unix-мс UTC, транскрипт — ISO со смещением. Разбор без приведения
    // к UTC сдвинул бы все лаги на часы и отказал бы ВЕЗДЕ (при зелёном CI, где TZ=UTC).
    [Fact]
    public void ВремяСоСмещением_РазбираетсяВUtc()
    {
        var anchor = "продолжай";
        var src = WriteFile("tz.jsonl",
            SysInit,
            // 21:00:04 +03:00 == 18:00:04Z — верный кандидат, лаг 4 с
            UserStrAt("u1", anchor, "2026-09-18T21:00:04.000+03:00"),
            AsstStr("a1", "ответ 1"),
            // тот же текст 40 минутами позже — вне окна лага
            UserStrAt("u2", anchor, "2026-09-18T21:40:00.000+03:00"),
            AsstStr("a2", "ответ 2"));
        var dst = Dst();

        var res = TranscriptBrancher.Branch(src, [anchor], New, dst,
            anchorTimestampMs: DateTimeOffset.Parse("2026-09-18T18:00:00Z").ToUnixTimeMilliseconds());

        res.Ok.Should().BeTrue(res.Reason);
        res.CutLine.Should().Be(3); // граница перед вторым промптом, а не в конце файла
    }
}
