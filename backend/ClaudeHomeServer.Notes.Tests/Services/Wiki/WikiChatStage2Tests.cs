using ClaudeHomeServer.Models;
using ClaudeHomeServer.Notes.Controllers;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Ступень 2 отбора серых чатов (N1a, ADR-035 решение 12а). Модель — подменённый
// ICheapTextRunner; стор — настоящий, на диске. Критерии карточки:
// c1 — все семь путей отказа: stage2Zone не пишется, показанная зона gray;
// c2 — вход модели ≤ 6 000 токенов (символы/4) И помещается в NumCtx профиля Small
//      (сравнение с ProfileDefaults[Small], а не с константой);
// c3 — гонка UpdateStage2 / UpdateScoring / Upsert сохраняет три набора полей;
// c4 — skip с баллом 40, исполнение задач и «важный» с отстающей stage1Zone: 0 вызовов модели.
public class WikiChatStage2Tests : IDisposable
{
    private const string User = "u1";
    private const string ChatId = "chat-1";

    private readonly string _dir;
    private readonly string _root;
    private readonly IConfiguration _config;
    private readonly FakeSessions _sessions = new();
    private readonly FakeLlm _llm = new();

    public WikiChatStage2Tests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_stage2_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        _root = WikiPaths.Root(_dir, User);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ─── 1. Вердикт пишется: зона, причина, число реплик человека ──────────

    [Theory]
    [InlineData(true, "triage")]
    [InlineData(false, "skipped")]
    public async Task Вердикт_ЕстьЗнание_triage_НетЗнания_skipped_СоСчётчикомРепликЧеловека(bool has, string zone)
    {
        SeedGrayChat();
        _llm.Answer = $$"""{"hasKnowledge":{{(has ? "true" : "false")}},"about":"сборка на Linux","reason":"есть решение"}""";

        var res = await Judge();

        res.Outcome.Should().Be(WikiChatStage2.Outcome.Judged, res.Detail);
        res.Zone.Should().Be(zone);
        var e = StoredEntry();
        e.Stage2Zone.Should().Be(zone);
        e.Stage2Reason.Should().Contain("сборка на Linux").And.Contain("есть решение");
        e.Stage2HistoryCount.Should().Be(2, "две реплики человека в истории");
        e.Stage1Zone.Should().Be(WikiChatScorer.ZoneGray, "чужие поля скорера вердикт не трогает");
        e.Score.Should().Be(9);
        WikiChatsController.ShownZone(e).Should().Be(zone, "ступень 2 > ступень 1");
        _llm.Calls.Should().ContainSingle().Which.ActionKey.Should().Be(LocalActionCatalog.WikiTriage);
    }

    [Fact]
    public async Task Ответ_ВОбёрткеИзПрозы_Разбирается()
    {
        SeedGrayChat();
        _llm.Answer = "Вот мой ответ: {\"hasKnowledge\": true, \"about\": \"a\", \"reason\": \"r\"} — спасибо.";

        (await Judge()).Outcome.Should().Be(WikiChatStage2.Outcome.Judged);
    }

    [Fact]
    public async Task ИсторияВыросла_ЗаВремяВызова_ВердиктПишетсяСоСчётчикомДоВызова()
    {
        SeedGrayChat();
        _llm.Answer = """{"hasKnowledge":true,"about":"a","reason":"r"}""";
        _llm.OnCall = () => _sessions.History[ChatId].AddRange(
            [new StoredUserMessage("ещё реплика 1"), new StoredUserMessage("ещё реплика 2"), new StoredUserMessage("ещё реплика 3")]);

        var res = await Judge();

        res.Outcome.Should().Be(WikiChatStage2.Outcome.Judged);
        StoredEntry().Stage2HistoryCount.Should().Be(2,
            "счётчик замерен до вызова: ночь увидит +3 реплики и переспросит");
    }

    // ─── 2. c1: семь путей отказа → stage2* не пишутся, зона gray ──────────

    public static TheoryData<string> ПутиОтказа => new()
    {
        "невалидный_json", "json_без_hasKnowledge", "исключение_модели", "пустой_ответ",
        "нет_маршрута", "отмена_ct", "стор_invalid_при_записи",
    };

    [Theory]
    [MemberData(nameof(ПутиОтказа))]
    public async Task Отказ_Stage2НеПишется_ЗонаОстаётсяGray_ИсключенияНаружуНет(string path)
    {
        SeedGrayChat();
        var cts = new CancellationTokenSource();
        switch (path)
        {
            case "невалидный_json": _llm.Answer = "это вообще не json"; break;
            case "json_без_hasKnowledge": _llm.Answer = """{"about":"x","reason":"y"}"""; break;
            case "исключение_модели": _llm.Throw = new InvalidOperationException("модель упала"); break;
            case "пустой_ответ": _llm.Answer = "   "; break;
            case "нет_маршрута": _llm.Route = false; _llm.Answer = """{"hasKnowledge":true}"""; break;
            case "отмена_ct": cts.Cancel(); _llm.Answer = """{"hasKnowledge":true}"""; break;
            case "стор_invalid_при_записи":
                _llm.Answer = """{"hasKnowledge":true,"about":"a","reason":"r"}""";
                // Файл портится ПОСЛЕ чтения стора и ДО записи вердикта — ровно во время вызова модели.
                _llm.OnCall = () => File.WriteAllText(TriageFile(), "{ не массив");
                break;
        }

        var res = await Judge(cts.Token);

        res.Outcome.Should().Be(WikiChatStage2.Outcome.Refused, $"{path}: {res.Detail}");
        res.Zone.Should().BeNull();
        if (path == "стор_invalid_при_записи")
        {
            File.ReadAllText(TriageFile()).Should().Be("{ не массив", "битый файл не перезаписывается");
            return;
        }
        var e = StoredEntry();
        e.Stage2Zone.Should().BeNull();
        e.Stage2Reason.Should().BeNull();
        e.Stage2HistoryCount.Should().BeNull();
        WikiChatsController.ShownZone(e).Should().Be(WikiChatScorer.ZoneGray);
    }

    // ─── 3. c4: живая пометка сильнее отстающей stage1Zone — модель не зовётся ─────────

    [Fact]
    public async Task Skip_СБаллом40_ИИсполнениеЗадач_МодельНеЗовётся()
    {
        // Запись в сторе — «серая, балл 40»: отстающая stage1Zone; живая пометка перекрывает её.
        SeedGrayChat("s-skip", score: 40, wikiMark: WikiMarks.Skip);
        SeedGrayChat("s-task", score: 40, taskExecution: true);

        var skip = await Judge(sessionId: "s-skip");
        var task = await Judge(sessionId: "s-task");

        skip.Outcome.Should().Be(WikiChatStage2.Outcome.NotApplicable);
        task.Outcome.Should().Be(WikiChatStage2.Outcome.NotApplicable);
        _llm.Calls.Should().BeEmpty("skip и исполнение задач не доходят до модели");
        // Мутация «убрать проверку WikiMark/TaskExecution в JudgeCoreAsync»: чат со stage1Zone=gray
        // доходит до модели, Calls не пуст — тест красный.
    }

    [Fact]
    public async Task ВажныйПослеОтставшегоПересчёта_МодельНеЗовётся()
    {
        // Человек поставил «важный» после последнего пересчёта: в сторе ещё stage1Zone=gray, и без
        // живой проверки ступень 2 позвала бы модель — её «skipped» перекрыл бы показ.
        SeedGrayChat("s-important", score: 9, wikiMark: WikiMarks.Important);

        var res = await Judge(sessionId: "s-important");

        res.Outcome.Should().Be(WikiChatStage2.Outcome.NotApplicable, res.Detail);
        _llm.Calls.Should().BeEmpty("живая пометка «важный»: ступень 2 не зовёт модель");
        StoredEntry("s-important").Stage2Zone.Should().BeNull("вердикт не пишется, чат остаётся серым");
        // Мутация «убрать проверку WikiMarks.Important в JudgeCoreAsync»: чат со stage1Zone=gray
        // доходит до модели (пустой ответ → Refused), Calls не пуст — тест красный.
    }

    [Theory]
    [InlineData(WikiChatScorer.ZoneTriage)]
    [InlineData(WikiChatScorer.ZoneSkipped)]
    [InlineData(WikiChatScorer.ZoneExcluded)]
    public async Task НеСерыйЧат_МодельНеЗовётся(string stage1Zone)
    {
        SeedGrayChat(stage1Zone: stage1Zone);

        (await Judge()).Outcome.Should().Be(WikiChatStage2.Outcome.NotApplicable);

        _llm.Calls.Should().BeEmpty("ступень 2 — только для stage1Zone=gray: important/закреплённый/«Итог» это triage");
    }

    [Fact]
    public async Task ЧужойИНесуществующийЧат_МодельНеЗовётся()
    {
        SeedGrayChat();
        _sessions.All.Single().OwnerId = "u2";

        (await Judge()).Outcome.Should().Be(WikiChatStage2.Outcome.NotApplicable);
        (await Judge(sessionId: "нет-такого")).Outcome.Should().Be(WikiChatStage2.Outcome.NotApplicable);

        _llm.Calls.Should().BeEmpty();
    }

    // ─── 4. c2: размер входа ───────────────────────────────────────────────

    [Fact]
    public async Task Чат100кСимволов_ВходМоделиВТокенахИВNumCtxПрофиляSmall()
    {
        SeedGrayChat(history: BigHistory(100_000));
        _llm.Answer = """{"hasKnowledge":true,"about":"a","reason":"r"}""";

        var res = await Judge();

        res.Outcome.Should().Be(WikiChatStage2.Outcome.Judged, res.Detail);
        var prompt = _llm.Calls.Single().Prompt;
        (prompt.Length / 4).Should().BeLessThanOrEqualTo(6_000, "потолок входа — 6k токенов по оценке символы/4");
        // Контекст профиля — из каталога, а не константой: вход + системная часть (она уже в prompt) +
        // ответ влезают в NumCtx. Оценка строже карточной (символы/3, как у WikiChatSlicer), чтобы
        // кириллица в реальном токенизаторе не вылезла из окна.
        var small = LocalActionCatalog.ProfileDefaults[CheapProfile.Small];
        ((prompt.Length / WikiChatSlicer.CharsPerTokenEstimate) + small.NumPredict)
            .Should().BeLessThanOrEqualTo(small.NumCtx, "вход + системный промпт + ответ ≤ NumCtx профиля Small");
        LocalActionCatalog.Find(LocalActionCatalog.WikiTriage)!.Profile.Should().Be(CheapProfile.Small,
            "тест меряет по профилю места wiki-triage; сменится профиль — пересмотреть потолок");
        prompt.Should().Contain("HEAD-МАРКЕР").And.Contain("TAIL-МАРКЕР",
            "начало и конец чата в срезе остаются");
    }

    [Fact]
    public void Срез_КороткийЧатЦеликом_ГраницаВходаHalfOpen()
    {
        WikiChatSlicer.Line[] OneLine(int len) => [new(1, true, new string('а', len))];
        // "Пользователь:\n" + текст + "\n\n" → после Trim остаётся 14 + len.
        var fits = OneLine(WikiChatStage2.MaxInputChars - 14);
        WikiChatStage2.Slice(fits).Length.Should().Be(WikiChatStage2.MaxInputChars, "ровно на потолке — целиком");

        var over = OneLine(WikiChatStage2.MaxInputChars - 14 + 1);
        var cut = WikiChatStage2.Slice(over);
        cut.Length.Should().BeLessThanOrEqualTo(WikiChatStage2.MaxInputChars);
        cut.Should().Contain("[…]", "на символ больше потолка — срез");
    }

    [Fact]
    public void Срез_ОкнаВокругСигналов_НеБольшеЧетырёх_ПустойЧатПустойСрез()
    {
        var lines = new List<WikiChatSlicer.Line> { new(1, true, new string('а', 3_000)) };
        for (var i = 0; i < 10; i++)
            lines.Add(new(i + 2, true, new string('б', 1_500) + " решили: вариант" + i + " " + new string('в', 1_500)));
        lines.Add(new(20, false, new string('г', 3_000)));

        var cut = WikiChatStage2.Slice(lines);

        cut.Length.Should().BeLessThanOrEqualTo(WikiChatStage2.MaxInputChars);
        System.Text.RegularExpressions.Regex.Matches(cut, "решили: вариант").Count
            .Should().BeLessThanOrEqualTo(WikiChatStage2.MaxWindows, "окон вокруг сигналов не больше четырёх");
        System.Text.RegularExpressions.Regex.Matches(cut, "решили: вариант").Count.Should().BeGreaterThan(0);

        WikiChatStage2.Slice([]).Should().BeEmpty();
    }

    // ─── 5. Разбор ответа ──────────────────────────────────────────────────

    [Theory]
    [InlineData("""{"hasKnowledge":"true"}""")]
    [InlineData("""{"hasKnowledge":1}""")]
    [InlineData("""{"hasKnowledge":null}""")]
    [InlineData("[]")]
    [InlineData("{")]
    public void Вердикт_hasKnowledgeНеБулев_Отказ(string raw)
    {
        WikiChatStage2.TryParseVerdict(raw, out _, out _, out _).Should().BeFalse();
    }

    // ─── 6. c3: гонка трёх писателей стора ─────────────────────────────────

    [Fact]
    public async Task Гонка_UpdateStage2_UpdateScoring_Upsert_ТриНабораПолейСохранены()
    {
        for (var round = 0; round < 25; round++)
        {
            var id = "race-" + round;
            Directory.CreateDirectory(Path.GetDirectoryName(TriageFile())!);
            WikiChatTriageStore.Upsert(_root, new WikiChatTriageEntry(id, 1, null, null, null, 0, default)).Ok.Should().BeTrue();

            await Task.WhenAll(
                Task.Run(() => WikiChatTriageStore.UpdateStage2(_root, id, "triage", "ступень 2", 7).Ok.Should().BeTrue()),
                Task.Run(() => WikiChatTriageStore.UpdateScoring(_root, id, 9, [new WikiTriageSignal("links", 3)],
                    WikiChatScorer.ZoneGray, "серая").Ok.Should().BeTrue()),
                Task.Run(() => WikiChatTriageStore.Upsert(_root, new WikiChatTriageEntry(id, 5, "h", "nothing_new", "пусто", 2, default)).Ok.Should().BeTrue()));

            var e = WikiChatTriageStore.Get(_root).Entries.Single(x => x.SessionId == id);
            e.Stage2Zone.Should().Be("triage", $"раунд {round}: поля ступени 2");
            e.Stage2Reason.Should().Be("ступень 2");
            e.Stage2HistoryCount.Should().Be(7);
            e.Score.Should().Be(9, $"раунд {round}: поля скорера");
            e.Stage1Zone.Should().Be(WikiChatScorer.ZoneGray);
            e.Cursor.Should().Be(5, $"раунд {round}: поля разбора");
            e.Zone.Should().Be("nothing_new");
            e.NothingNewStreak.Should().Be(2);
        }
        // Мутация «убрать Stage2FieldNames из переноса в Upsert» (WikiChatTriageStore.Upsert): Upsert
        // после UpdateStage2 затирает stage2Zone — раунды с таким порядком красные.
    }

    [Fact]
    public void Upsert_ПереноситПоляСтупени2_ИзПрежнейЗаписи()
    {
        WikiChatTriageStore.UpdateStage2(_root, "c", "skipped", "нет знания", 4).Ok.Should().BeTrue();

        var up = WikiChatTriageStore.Upsert(_root, new WikiChatTriageEntry("c", 3, "h", "nothing_new", "пусто", 1, default));

        up.Ok.Should().BeTrue();
        var e = WikiChatTriageStore.Get(_root).Entries.Single();
        (e.Stage2Zone, e.Stage2Reason, e.Stage2HistoryCount).Should().Be(("skipped", "нет знания", 4));
        e.Cursor.Should().Be(3);
    }

    [Fact]
    public void UpdateScoring_НеТрогаетПоляСтупени2()
    {
        WikiChatTriageStore.UpdateStage2(_root, "c", "triage", "есть", 6).Ok.Should().BeTrue();

        WikiChatTriageStore.UpdateScoring(_root, "c", 12, [], WikiChatScorer.ZoneGray, "серая").Ok.Should().BeTrue();

        var e = WikiChatTriageStore.Get(_root).Entries.Single();
        (e.Stage2Zone, e.Stage2HistoryCount).Should().Be(("triage", 6));
        e.Score.Should().Be(12);
    }

    [Fact]
    public void UpdateStage2_ОтказыФормыИНечитаемыйСтор()
    {
        WikiChatTriageStore.UpdateStage2(_root, "", "triage", null, 1).Code.Should().Be(WikiChatTriageStore.CodeShape);
        WikiChatTriageStore.UpdateStage2(_root, "c", " ", null, 1).Code.Should().Be(WikiChatTriageStore.CodeShape);
        WikiChatTriageStore.UpdateStage2(_root, "c", "triage", null, -1).Code.Should().Be(WikiChatTriageStore.CodeShape);

        Directory.CreateDirectory(Path.GetDirectoryName(TriageFile())!);
        File.WriteAllText(TriageFile(), "битый");
        WikiChatTriageStore.UpdateStage2(_root, "c", "triage", null, 1).Code.Should().Be(WikiChatTriageStore.CodeInvalid);
        File.ReadAllText(TriageFile()).Should().Be("битый");
    }

    [Fact]
    public void Стор_ОтрицательныйСчётчикСтупени2ВФайле_Invalid()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TriageFile())!);
        File.WriteAllText(TriageFile(),
            """[{"sessionId":"c","cursor":0,"triagedAt":"2026-10-20T12:00:00+00:00","stage2HistoryCount":-1}]""");

        WikiChatTriageStore.Get(_root).State.Should().Be(WikiTriageFileState.Invalid);
    }

    // ─── 7. Показанная зона: nothing_new > ступень 2 > ступень 1 ───────────

    [Fact]
    public void ПоказаннаяЗона_ПриоритетыИУстаревшийВердикт()
    {
        WikiChatTriageEntry E(string? zone, string? s1, string? s2) => new("c", 0, null, zone, "причина разбора", 0, default,
            5, null, s1, "причина 1", s2, s2 is null ? null : "причина 2", s2 is null ? null : 3);

        WikiChatsController.ShownZone(E(null, "gray", "triage")).Should().Be("triage", "ступень 2 перекрывает ступень 1");
        WikiChatsController.ShownReason(E(null, "gray", "triage")).Should().Be("причина 2");
        WikiChatsController.ShownZone(E("nothing_new", "gray", "triage")).Should().Be("nothing_new", "nothing_new выше ступени 2");
        WikiChatsController.ShownReason(E("nothing_new", "gray", "triage")).Should().Be("причина разбора");
        WikiChatsController.ShownZone(E(null, "gray", null)).Should().Be("gray", "вердикта нет — зона ступени 1");
        WikiChatsController.ShownZone(E(null, "excluded", "triage")).Should().Be("excluded",
            "пересчёт увёл чат из серой (пометка skip): устаревший вердикт ступени 2 не показывается");
        WikiChatsController.ShownReason(E(null, "excluded", "triage")).Should().Be("причина 1");
    }

    // ─── Помощники ─────────────────────────────────────────────────────────

    private WikiChatStage2 Stage2() => new(_llm, _sessions, _config);

    private Task<WikiChatStage2.Stage2Result> Judge(CancellationToken ct = default, string sessionId = ChatId) =>
        Stage2().JudgeAsync(User, sessionId, ct);

    private string TriageFile() => Path.Combine(_root, "keeper", "chat-triage.json");

    private WikiChatTriageEntry StoredEntry(string id = ChatId) =>
        WikiChatTriageStore.Get(_root).Entries.Single(e => e.SessionId == id);

    // Серый чат: сессия + запись пересчёта в сторе (stage1Zone по умолчанию gray) + короткая история.
    private void SeedGrayChat(string id = ChatId, int score = 9, string stage1Zone = WikiChatScorer.ZoneGray,
        string? wikiMark = null, bool taskExecution = false, List<StoredMessage>? history = null)
    {
        _sessions.All.Add(new Session { Id = id, OwnerId = User, ProjectId = "p1", Name = "Чат", WikiMark = wikiMark, TaskExecution = taskExecution });
        _sessions.History[id] = history ??
        [
            new StoredUserMessage("Давай перенесём сборку на Linux, решили?"),
            new StoredTextMessage("Да, переносим."),
            new StoredUserMessage("Тогда тесты гоняем через CI."),
            new StoredTextMessage("Принято."),
        ];
        WikiChatTriageStore.UpdateScoring(_root, id, score, [new WikiTriageSignal("human_turns", 2)], stage1Zone, "серая").Ok
            .Should().BeTrue();
    }

    // Чат заданного объёма: маркер в начале и в конце, сигналы «решили» по всей середине.
    private static List<StoredMessage> BigHistory(int totalChars)
    {
        var h = new List<StoredMessage> { new StoredUserMessage("HEAD-МАРКЕР " + new string('а', 500)) };
        var filler = 0;
        while (filler < totalChars)
        {
            var text = "обсуждаем сборку, решили вариант " + filler + " " + new string('б', 800);
            h.Add(new StoredTextMessage(text));
            h.Add(new StoredUserMessage("дальше " + new string('в', 300)));
            filler += text.Length + 310;
        }
        h.Add(new StoredUserMessage("TAIL-МАРКЕР " + new string('д', 500)));
        return h;
    }

    private sealed class FakeLlm : ICheapTextRunner
    {
        public List<(string ActionKey, string Prompt)> Calls { get; } = [];
        public string Answer = "";
        public bool Route = true;
        public Exception? Throw;
        public Action? OnCall;

        public bool UsesLocal(string actionKey) => true;
        public bool HasFreeRoute(string actionKey) => Route;
        public bool HasRoute(string actionKey) => Route;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "fake";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default) =>
            Task.FromResult(Run(actionKey, prompt, ct));

        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default) =>
            Task.FromResult<string?>(Run(actionKey, prompt, ct));

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default) =>
            Task.FromResult(new OneShotResult(Run(actionKey, prompt, ct), null, 1));

        private string Run(string actionKey, string prompt, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add((actionKey, prompt));
            OnCall?.Invoke();
            if (Throw is not null) throw Throw;
            return Answer;
        }
    }

    private sealed class FakeSessions : ISessionDirectory
    {
        public readonly List<Session> All = [];
        public readonly Dictionary<string, List<StoredMessage>> History = [];

        public Session? GetById(string id) => All.FirstOrDefault(s => s.Id == id);
        public IReadOnlyCollection<Session> GetAll() => All;
        public string? ResolveOwnerId(Session s) => s.OwnerId;
        public Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId) =>
            Task.FromResult<IReadOnlyList<StoredMessage>>(
                History.TryGetValue(sessionId, out var h) ? [.. h] : new List<StoredMessage>());
        public bool SetWikiMark(string sessionId, string? mark) => true;
    }
}
