using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Services.Prompts;
using ClaudeHomeServer.Services.Spheres;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Инструменты вики в сервере заметок (S6c; ADR-035 §5 «нет MCP-принятия пакета», §7 «ссылка
/// вместо текста», §8 «wiki_query без модели»; ADR-036 §2 «видимость», §3 «дом источника»).
///
/// Проверяются границы каждого из четырёх:
/// c2 — вопрос видит только набор WikiVisibility этого хода (не личный раздел и не проекты
///      другой сферы; из чата вне проекта — никаких разделов проектов; без контекста — только
///      «Общее»);
/// c3 — текст вместо ссылки → отказ схемы; target=common проектному источнику → отказ;
///      21-я открытая заявка → отказ «разберите Входящие»;
/// c4 — ссылка на раздел вне набора в отданной странице — [скрыто].
/// Плюс: не заведённая вика — отказ на вызове (состав tools/list от неё не зависит, сторож —
/// McpToolsetStabilityTests), и дословная цитата как условие факта. Плюс хвосты FIN3-3:
/// приватный чат не принимает ни один из трёх входов — «Запиши это», форма session:{id}#m{a}-{b}
/// и пересчёт (WikiChatRecalc) — и отвечает как несуществующий.
///
/// Никакого прямого письма в файлы здесь нет: заявка идёт через захват (S6h) и очередь
/// разбора (S6b-3, подменённая модель), чтение — каталог и страницы внутренним читателем
/// Хранителя.
/// </summary>
public class NotesToolsetWikiTests : IDisposable
{
    private const string User = "u1";
    private const string Own = "projects/p1";
    // Автор приватного чата в общем проекте (FIN3-3): чат резолвится на владельца (OwnerId),
    // приватность держит PrivateToUserId — как в фикстурах WikiPrivateChatTests.
    private const string Participant = "u2";

    // p1 и q1 — одна сфера s1 (q1 виден из хода p1), q2 — другая сфера s2 (не виден).
    private const string OtherSphere = "projects/q2";

    private const string Question = "перенести сборку на linux";

    private readonly string _dir;
    private readonly IConfiguration _config;
    private readonly FakeProjects _projects = new();
    private readonly SpheresDir _spheres = new();
    private readonly NotesService _notes;
    private readonly NotesKnowledgeService _kb;
    private readonly NotesAiService _ai;
    private readonly NoteTaskSyncService _noteTasks;
    private readonly WikiKeeper _keeper;
    private readonly WikiIngestService _ingest;
    private readonly WikiSourceCapture _capture;
    private readonly WikiChatIngestService _chatIngest;
    private readonly FakeSessions _sessions = new();

    public NotesToolsetWikiTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "notes_toolset_wiki_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();

        var users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        // Проекты — тестовые, с корнем под temp: заметка проекта (c3) обязана иметь свой notes/.
        AddProject("p1");
        AddProject("q1");
        AddProject("q2");
        _spheres.Spheres["s1"] = ["p1", "q1"];
        _spheres.Spheres["s2"] = ["q2"];

        _notes = new NotesService(_projects, _config, NullLogger<NotesService>.Instance, flags: new WikiFlagOn());
        _kb = new NotesKnowledgeService(new NoopKnowledgeIndex(), _notes, users, _config,
            NullLogger<NotesKnowledgeService>.Instance);
        _ai = new NotesAiService(_notes, _config, new NoopCheapRunner());
        _noteTasks = new NoteTaskSyncService(_notes, new NoopNoteTaskBridge(), _projects, _kb,
            new NoopNotesNotifier(), NullLogger<NoteTaskSyncService>.Instance);

        _keeper = new WikiKeeper(_notes, _config);
        var apply = new WikiApplyService(_notes, _keeper, _spheres, _config);
        _ingest = new WikiIngestService(new NothingLlm(), _spheres, apply, _config);
        _capture = new WikiSourceCapture(_notes, _projects, _sessions, _config, _keeper);
        _chatIngest = new WikiChatIngestService(_capture, _ingest, _sessions, _keeper, _config);
        _keeper.EnsureInitializedAsync(User).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string Root => WikiPaths.Root(_dir, User);

    private void AddProject(string id)
    {
        var root = Path.Combine(_dir, "projects", id);
        Directory.CreateDirectory(Path.Combine(root, "notes"));
        _projects.Items[id] = new Project { Id = id, OwnerId = User, Name = "Проект " + id, RootPath = root };
    }

    // ── обвязка вызова ──

    private (NotesToolset Toolset, McpToolCallContext Ctx) McpEnv(string? projectId, string sessionId = "s-call",
        ClaudeHomeServer.Services.Composition.IFeatureFlagGate? flags = null, string? privateTo = null)
    {
        var session = new Session { Id = sessionId, OwnerId = User, ProjectId = projectId, Name = "Чат вызова",
            PrivateToUserId = privateTo };
        _sessions.All.Add(session);
        return (
            new NotesToolset(new AllowAllPersonas(), new AllowAllBindings(), new OwnSession(session),
                new ClaudeHomeServer.Tests.Helpers.NotesNoopHubNotifier(), new ClaudeHomeServer.Tests.Helpers.NotesWideTurnFrame(),
                new ClaudeHomeServer.Tests.Helpers.NotesNoActorDenials(), _notes, _kb, _ai, _noteTasks,
                _config, _ingest, _capture, _chatIngest, _sessions, _spheres, _projects,
                flags ?? new WikiFlagOn()),
            new McpToolCallContext(User, sessionId, sessionId));
    }

    private static McpToolCallResult Call(NotesToolset toolset, McpToolCallContext ctx, string tool, JsonObject args) =>
        toolset.CallAsync(tool, args, ctx, CancellationToken.None).GetAwaiter().GetResult();

    private static JsonObject Args(params (string Key, string Value)[] pairs)
    {
        var obj = new JsonObject();
        foreach (var (k, v) in pairs) obj[k] = v;
        return obj;
    }

    private static void AssertDenied(McpToolCallResult res, string because)
    {
        res.IsError.Should().BeTrue($"отказ обязан быть отказом: {res.Text}");
        res.Text.Should().Contain(because, $"причина отказа: {res.Text}");
    }

    // ── состояние вики: страницы в четырёх разделах и их каталоги ──

    private static string TopicPage(string title, string summary, string extra = "") => $$"""
        ---
        type: topic
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # {{title}}

        ## Сводка
        {{summary}}

        ## Хронология

        ## Противоречия

        ## История
        {{extra}}
        """;

    private void Write(string sectionKey, string pageKey, string content)
    {
        var r = _keeper.WritePageAsync(User, sectionKey, pageKey, content).GetAwaiter().GetResult();
        r.Status.Should().Be(WikiOpStatus.Applied, r.Reason);
    }

    /// <summary>
    /// p1 — дом вызывающего чата: его страница с ответом на вопрос и со ссылками в чужую
    /// сферу и в личное. q1 — сосед по сфере (виден), q2 — другая сфера (нет), personal и
    /// common — свои страницы с тем же словом, чтобы различие было о наборе, а не о тексте.
    /// </summary>
    private void SeedWiki()
    {
        Write(Own, "Проекты/p1/Сущность-сборки.md", TopicPage(
            "Сущность-сборки", "Сборку решено перенести на linux.",
            "\nСвязано: [[Проекты/q2/Сущность-чужой-сферы]] и [[Личное/Личный-факт]]."));
        Write("projects/q1", "Проекты/q1/Соседняя-сущность.md", TopicPage("Соседняя-сущность", "Соседний проект: сборка на linux."));
        Write(OtherSphere, "Проекты/q2/Сущность-чужой-сферы.md", TopicPage("Сущность-чужой-сферы", "Другая сфера: сборка на linux."));
        Write(WikiSections.Personal, "Личное/Личный-факт.md", TopicPage("Личный-факт", "Личное: сборка на linux."));
        Write(WikiSections.Common, "Общее/Общий-факт.md", TopicPage("Общий-факт", "Общее: сборку перенести на linux."));
        foreach (var key in new[] { Own, "projects/q1", OtherSphere, WikiSections.Personal, WikiSections.Common })
            WikiProjections.RegenerateSection(Root, key);
    }

    // ── c2: вопрос видит только набор видимого хода ──

    [Fact]
    public void C2_ВопросИзЧатаПроекта_НеВидитЛичныйРазделИПроектДругойСферы()
    {
        SeedWiki();
        var (toolset, ctx) = McpEnv("p1");

        var res = Call(toolset, ctx, "wiki_query", Args(("question", Question)));

        res.IsError.Should().BeFalse(res.Text);
        // Свой раздел и сосед по сфере — есть; «Общее» — вверх по цепочке, тоже есть.
        res.Text.Should().Contain("Сущность-сборки");
        res.Text.Should().Contain("Соседняя-сущность");
        res.Text.Should().Contain("Общий-факт");
        // Личный раздел и проект другой сферы — ни строкой каталога, ни текстом страницы.
        res.Text.Should().NotContain("Личный-факт", "личный раздел в проектном ходу не виден никогда (ADR-036 §2)");
        res.Text.Should().NotContain("Сущность-чужой-сферы", "проект другой сферы не виден никогда (ADR-036 §2)");
    }

    [Fact]
    public void C2_ВопросИзЧатаВнеПроекта_НеВидитРазделовПроектов()
    {
        SeedWiki();
        var (toolset, ctx) = McpEnv(null);

        var res = Call(toolset, ctx, "wiki_query", Args(("question", Question)));

        res.IsError.Should().BeFalse(res.Text);
        res.Text.Should().Contain("Личный-факт");
        res.Text.Should().Contain("Общий-факт");
        res.Text.Should().NotContain("Проекты/", "разделы проектов не видны и по явному запросу (ADR-036 §2)");
        res.Text.Should().NotContain("Сущность-сборки");
    }

    [Fact]
    public void C2_БезКонтекста_ТолькоОбщее()
    {
        SeedWiki();
        // Чат с пропавшим проектом: контекста нет — fail-closed, набор сужается до «Общего».
        var (toolset, ctx) = McpEnv("p-gone");

        var res = Call(toolset, ctx, "wiki_query", Args(("question", Question)));

        res.IsError.Should().BeFalse(res.Text);
        res.Text.Should().Contain("Общий-факт");
        res.Text.Should().NotContain("Личный-факт", "без контекста виден только common (ADR-036 §2)");
        res.Text.Should().NotContain("Сущность-сборки");
        res.Text.Should().NotContain("Сущность-чужой-сферы");
    }

    // ── c4: ссылка на раздел вне набора — [скрыто] и в тексте страницы, и в каталоге ──

    [Fact]
    public void C4_СсылкаВнеНабораВОданнойСтранице_Скрыта()
    {
        SeedWiki();
        var (toolset, ctx) = McpEnv("p1");

        var res = Call(toolset, ctx, "wiki_query", Args(("question", Question)));

        res.IsError.Should().BeFalse(res.Text);
        // Страница своего раздела отдана, но её ссылка в чужую сферу и в личное — закрыты:
        // заголовок чужого раздела — тоже текст (ADR-036 §2).
        res.Text.Should().Contain("Сущность-сборки");
        res.Text.Should().NotContain("[[Проекты/q2/Сущность-чужой-сферы]]");
        res.Text.Should().NotContain("[[Личное/Личный-факт]]");
        res.Text.Should().Contain("[скрыто]");
    }

    // ── c3: ссылка вместо текста, target и лимит открытых заявок ──

    [Fact]
    public void C3_ТекстВместоСсылки_ОтказСхемы()
    {
        SeedWiki();
        var (toolset, ctx) = McpEnv("p1");

        var res = Call(toolset, ctx, "wiki_ingest",
            Args(("source", "Перенести сборку на linux — вот это и надо разобрать, весь абзац.")));

        res.IsError.Should().BeTrue(res.Text);
        res.Text.Should().Contain("ССЫЛКА", "текст источника вместо ссылки — отказ схемы (ADR-035 §7)");
    }

    [Fact]
    public async Task C3_TargetCommonДляЗаметкиПроекта_Отказ()
    {
        SeedWiki();
        var note = _notes.Create(User, new CreateNoteRequest(Title: "Заметка проекта", Content: "Содержимое заметки проекта.", Source: "p1"));
        var (toolset, ctx) = McpEnv("p1");

        var res = await toolset.CallAsync("wiki_ingest",
            Args(("source", note.Id), ("target", "common")), ctx, CancellationToken.None);

        AssertDenied(res, "проектный источник не идёт в 'common'");
        // Отказ — до записи: карточки в «Общем» не появилось.
        File.Exists(SafePath.Join(Root, "pages/Общее/Источники")).Should().BeFalse();
    }

    [Fact]
    public void C3_ДвадцатьОткрытыхПакетов_ДвадцатьПерваяЗаявкаОтказана()
    {
        SeedWiki();
        var note = _notes.Create(User, new CreateNoteRequest(Title: "Заметка для лимита", Content: "Содержимое.", Source: "p1"));
        AddOpenPackages(WikiPackage.MaxOpenPackages);
        var (toolset, ctx) = McpEnv("p1");

        // Состояние видно и в status: лимит выбран не из воздуха.
        var status = Call(toolset, ctx, "wiki_status", Args());
        status.IsError.Should().BeFalse(status.Text);
        status.Text.Should().Contain("\"open\":20", "состояние вики называет полный лимит");

        var res = Call(toolset, ctx, "wiki_ingest", Args(("source", note.Id)));

        AssertDenied(res, "разберите Входящие");
    }

    // ── S7b0a: «разобрать этот чат» (session:{id} без диапазона) — только свой чат ──
    // Путь идёт через WikiChatIngestService.AnalyzeAsync (тот же, что у REST analyze).
    // Инвариант «разбор только по требованию владельца и только своих чатов» держится на
    // обоих входах: REST — WikiChatsControllerTests, MCP — здесь. Отказ per-owner — до
    // обращения к модели; чужой и несуществующий неразличимы кодом.

    [Fact]
    public void С7b0a_РазборЧата_ЧужойЧат_ОтказДоМодели()
    {
        var (toolset, ctx) = McpEnv("p1");
        _sessions.All.Add(new Session { Id = "s-foreign", OwnerId = "u2", ProjectId = "p1", Name = "Чужой чат" });

        var res = Call(toolset, ctx, "wiki_ingest", Args(("source", "session:s-foreign")));
        AssertDenied(res, "чат не существует");

        // Несуществующий — тот же путь и та же форма отказа, одним и тем же сообщением
        // (W3-6 F1): «принадлежит другому владельцу» выдавало бы существование чужого.
        var missing = Call(toolset, ctx, "wiki_ingest", Args(("source", "session:s-none")));
        AssertDenied(missing, "чат не существует");
    }

    private void AddOpenPackages(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var r = WikiPackageStore.Create(Root, new WikiPackage
            {
                Id = WikiPackageStore.NewId(),
                Section = Own,
                Sources = [new WikiPackage.SourceRef("note", "note:x")],
                Operations = [new WikiPackage.Operation
                {
                    Op = WikiPackage.OpAppend,
                    // Страница пакета — путь, принадлежащий его разделу (ShapeErrors).
                    Page = "Проекты/p1/Сущность-сборки.md",
                    Text = "утверждение из чата",
                    Class = WikiPackage.ClassKey("note", WikiPackage.OpAppend),
                    Decision = WikiPackage.DecisionInbox,
                    Reason = "спорное: ждёт человека",
                    BaseHashes = new Dictionary<string, string>(),
                }],
                // Новый пакет рождается в «checking»: это начальный статус, из которого
                // работа продолжается — он открытый и занимает место в лимите.
                Status = WikiPackage.StatusChecking,
            });
            r.Ok.Should().BeTrue(r.Reason ?? "пакет обязан быть записан");
        }
        // Fixture сама себе проверка: это валидные открытые пакеты, а не мусор в каталоге.
        var list = WikiPackageStore.List(Root);
        list.InvalidFiles.Should().BeEmpty();
        list.Packages.Count.Should().Be(count);
    }

    // ── не заведённая вика: отказ на вызове, а не исчезновение инструмента ──

    [Fact]
    public void НезаведённаяВика_ОтказНаВызовеБезУтечки()
    {
        SeedWiki();
        File.Delete(SafePath.Join(Root, "pages/_schema.md"));
        var (toolset, ctx) = McpEnv("p1");

        foreach (var tool in new[] { "wiki_query", "wiki_status", "wiki_add_fact" })
        {
            var res = Call(toolset, ctx, tool, Args(("question", Question), ("fact", "ф"), ("quote", "цитата из чата"),
                ("sessionRange", "m1-2")));
            AssertDenied(res, "не заведена");
        }
        // Состав tools/list при этом не изменился: инструменты вики в нём остаются.
        var (toolset2, _) = McpEnv("p1");
        toolset2.ToolsFor(ctx).Select(t => t.Name).Should().Contain(["wiki_ingest", "wiki_add_fact", "wiki_query", "wiki_status"]);
    }

    // ── wiki_add_fact: факт без дословной цитаты из этого чата не принимается ──

    private Session AddChat(string id, string? projectId, string? privateTo = null)
    {
        var session = new Session { Id = id, OwnerId = User, ProjectId = projectId, Name = "Чат разбора",
            PrivateToUserId = privateTo };
        _sessions.All.Add(session);
        _sessions.History[id] =
        [
            new StoredUserMessage("Решили: нарезку делает код, выдержка не больше 8 КБ."),
            new StoredTextMessage("Принято, режу по диапазону m{a}-m{b}."),
        ];
        return session;
    }

    [Fact]
    public async Task AddFact_ЦитатаПодтверждена_ЗаявкаДошлаДоРазбора()
    {
        SeedWiki();
        AddChat("s-call", "p1");
        var (toolset, ctx) = McpEnv("p1");

        var res = await toolset.CallAsync("wiki_add_fact", Args(
            ("fact", "Нарезку выдержки делает код"),
            ("quote", "Решили: нарезку делает код"),
            ("sessionRange", "m1-2")), ctx, CancellationToken.None);

        // Цепочка прошла: карточка выдержки на диске, заявка дошла до модели (её ответ —
        // штатное «нечего добавить»: модель здесь подменена, это не проверяется).
        res.IsError.Should().BeFalse(res.Text);
        res.Text.Should().Contain("нечего добавить");
        var cards = Directory.GetFiles(SafePath.Join(Root, "pages/Проекты/p1/Источники"), "*.md");
        cards.Should().NotBeEmpty("выдержка чата обязана стать карточкой источника");
        File.ReadAllText(cards[0]).Should().Contain("session:s-call#m1-2");
    }

    /// <summary>Порядок здесь такой и он осознанный: цитата сверяется с выдержкой, а
    /// выдержку даёт захват — значит карточка источника появляется раньше отказа. Отказ
    /// касается заявки: пакета нет, модель не звана. Карточка без пакета инертна: она не
    /// кандидат и сама по себе в очередь не попадёт.</summary>
    [Fact]
    public async Task AddFact_ЦитатыНетВВыдержке_ОтказБезПакета()
    {
        SeedWiki();
        AddChat("s-call", "p1");
        var (toolset, ctx) = McpEnv("p1");

        var res = await toolset.CallAsync("wiki_add_fact", Args(
            ("fact", "Нарезку выдержки делает код"),
            ("quote", "Никогда не было такой фразы в этом чате"),
            ("sessionRange", "m1-2")), ctx, CancellationToken.None);

        AssertDenied(res, "цитаты нет");
        WikiPackageStore.CountOpen(Root).Should().Be(0, "отказанная заявка не оставила пакета");
    }

    [Fact]
    public async Task AddFact_КривойДиапазон_ОтказСхемыДоДиска()
    {
        SeedWiki();
        AddChat("s-call", "p1");
        var (toolset, ctx) = McpEnv("p1");

        foreach (var bad in new[] { "1-2", "m2-m1", "m0-2", "m1", "м1-2", "m1-2..", "a-b" })
        {
            var res = await toolset.CallAsync("wiki_add_fact", Args(
                ("fact", "Нарезку выдержки делает код"),
                ("quote", "Решили: нарезку делает код"),
                ("sessionRange", bad)), ctx, CancellationToken.None);
            AssertDenied(res, "sessionRange");
        }
        Directory.Exists(SafePath.Join(Root, "pages/Проекты/p1/Источники")).Should().BeFalse();
    }

    // ── FIN3-3, хвосты: ни один из трёх входов не принимает приватный чат ──
    // Тот же инвариант, что у WikiPrivateChatTests (очередь ночи, разбор, список): приватный
    // чат виден только автору, машинные потребители вики не читают его вовсе. Здесь три
    // оставшихся входа: «Запиши это» и форма session:{id}#m{a}-{b} — оба через общий
    // chokepoint WikiSourceCapture.CaptureSession — и пересчёт (WikiChatRecalc), который
    // пишет балл/зону в стор и counts ответа. Отказ неотличим от несуществующего.

    [Fact]
    public async Task ПриватныйЧат_ЗапишиЭто_ОтказКакНесуществующий_ОбычныйРаботает()
    {
        SeedWiki();
        AddChat("s-priv", "p1", privateTo: User); // чат-вызыватель приватный: его автор — владелец
        var (toolsetPriv, ctxPriv) = McpEnv("p1", "s-priv", privateTo: User);

        var res = await toolsetPriv.CallAsync("wiki_add_fact", Args(
            ("fact", "Нарезку выдержки делает код"),
            ("quote", "Решили: нарезку делает код"),
            ("sessionRange", "m1-2")), ctxPriv, CancellationToken.None);

        // Мутация «убрать проверку PrivateToUserId в CaptureSession»: выдержка режется,
        // заявка доходит до модели, карточка ложится на диск — IsError и BeFalse краснеют.
        AssertDenied(res, "не существует");
        Directory.Exists(SafePath.Join(Root, "pages/Проекты/p1/Источники")).Should().BeFalse(
            "отказ — до захвата: карточки выдержки нет");

        // Тот же вызов из обычного чата того же проекта работает как прежде (путь —
        // AddFact_ЦитатаПодтверждена выше).
        AddChat("s-open", "p1");
        var (toolsetOpen, ctxOpen) = McpEnv("p1", "s-open");
        var ok = await toolsetOpen.CallAsync("wiki_add_fact", Args(
            ("fact", "Нарезку выдержки делает код"),
            ("quote", "Решили: нарезку делает код"),
            ("sessionRange", "m1-2")), ctxOpen, CancellationToken.None);
        ok.IsError.Should().BeFalse(ok.Text);
        ok.Text.Should().Contain("нечего добавить");
    }

    [Fact]
    public async Task ПриватныйЧат_ФормаВыдержки_ОтказКакНесуществующий_ОбычныйПринят()
    {
        SeedWiki();
        AddChat("s-participant-private", "p1", privateTo: Participant); // чат участника, резолвится на владельца
        AddChat("s-owner-private", "p1", privateTo: User);              // и свой приватный — тот же отказ
        var (toolset, ctx) = McpEnv("p1");

        var foreign = await toolset.CallAsync("wiki_ingest",
            Args(("source", "session:s-participant-private#m1-2")), ctx, CancellationToken.None);
        AssertDenied(foreign, "не существует");

        var own = await toolset.CallAsync("wiki_ingest",
            Args(("source", "session:s-owner-private#m1-2")), ctx, CancellationToken.None);
        AssertDenied(own, "не существует");

        // Несуществующий чат неотличим: та же форма отказа — приватность не выдаёт
        // существования чата вызывающему.
        var missing = await toolset.CallAsync("wiki_ingest",
            Args(("source", "session:s-none#m1-2")), ctx, CancellationToken.None);
        AssertDenied(missing, "не существует");

        Directory.Exists(SafePath.Join(Root, "pages/Проекты/p1/Источники")).Should().BeFalse(
            "отказанные выдержки не оставили карточек");

        // Та же форма для обычного чата принимается: карточка на диске.
        AddChat("s-open", "p1");
        var ok = await toolset.CallAsync("wiki_ingest",
            Args(("source", "session:s-open#m1-2")), ctx, CancellationToken.None);
        ok.IsError.Should().BeFalse(ok.Text);
        var cards = Directory.GetFiles(SafePath.Join(Root, "pages/Проекты/p1/Источники"), "*.md");
        cards.Should().NotBeEmpty("выдержка обычного чата стала карточкой источника");
        File.ReadAllText(cards[0]).Should().Contain("session:s-open#m1-2");
    }

    [Fact]
    public async Task ПриватныйЧат_Пересчёт_НеСчитаетИНеПишет_ОбычныйСчитает()
    {
        AddChat("c-open", "p1");
        AddChat("c-participant-private", "p1", privateTo: Participant);
        AddChat("c-owner-private", "p1", privateTo: User);

        var res = await new WikiChatRecalc(_sessions, _config).RecalculateAsync(User);

        res.Ok.Should().BeTrue(res.Reason ?? "стор пуст — пересчёт валиден");
        // Мутация «убрать фильтр PrivateToUserId из цикла пересчёта»: Counted и сумма counts
        // краснеют (3 вместо 1), а в сторе появляются записи приватных чатов.
        res.Counted.Should().Be(1, "приватные чаты не пересчитываются");
        res.Counts.Values.Sum().Should().Be(1, "приватные чаты не попадают в counts ответа");

        // Данные о чужих чатах не оседают в сторе разбора владельца: запись одна — обычная.
        var store = WikiChatTriageStore.Get(Root);
        store.State.Should().Be(WikiTriageFileState.Valid);
        store.Entries.Select(e => e.SessionId).Should().Equal("c-open");
    }

    // ── гейт флага llm-wiki на входах с расходом модели (хвост финала W1-3) ──

    private sealed class WikiFlagOff : ClaudeHomeServer.Services.Composition.IFeatureFlagGate
    {
        public bool IsEnabled(string userId, string key) => false;
    }

    private int PackageCount() =>
        Directory.Exists(SafePath.Join(Root, "keeper/packages"))
            ? Directory.GetFiles(SafePath.Join(Root, "keeper/packages"), "*", SearchOption.AllDirectories).Length
            : 0;

    [Fact]
    public void FlagOff_WikiIngest_ОтказБезПакетаИЗаявки()
    {
        SeedWiki();
        var note = _notes.Create(User, new CreateNoteRequest(Title: "Заметка", Content: "Содержимое заметки.", Source: "p1"));
        var (toolset, ctx) = McpEnv("p1", flags: new WikiFlagOff());
        var before = PackageCount();

        var res = Call(toolset, ctx, "wiki_ingest", Args(("source", note.Id)));

        AssertDenied(res, "выключена");
        PackageCount().Should().Be(before, "при выключенном флаге пакет не создаётся");
        File.Exists(SafePath.Join(Root, "pages/Проекты/p1/Источники")).Should().BeFalse();
    }

    [Fact]
    public void FlagOff_WikiAddFact_ОтказБезЗаписи()
    {
        SeedWiki();
        var (toolset, ctx) = McpEnv("p1", flags: new WikiFlagOff());
        var before = PackageCount();

        var res = Call(toolset, ctx, "wiki_add_fact", Args(
            ("fact", "Нарезку делает код"), ("quote", "Решили: нарезку делает код"), ("sessionRange", "m1-2")));

        AssertDenied(res, "выключена");
        PackageCount().Should().Be(before);
    }

    [Fact]
    public void FlagOff_ЧитающиеИнструментыНеГейтятся()
    {
        SeedWiki();
        var (toolset, ctx) = McpEnv("p1", flags: new WikiFlagOff());

        Call(toolset, ctx, "wiki_status", Args()).IsError.Should().BeFalse();
        toolset.ToolsFor(ctx).Select(t => t.Name).Should().Contain(["wiki_ingest", "wiki_add_fact", "wiki_query", "wiki_status"],
            "состав tools/list от флага не зависит");
    }

    // ── обвязка швов ──

    private sealed class WikiFlagOn : ClaudeHomeServer.Services.Composition.IFeatureFlagGate
    {
        public bool IsEnabled(string userId, string key) => key == FeatureFlagKeys.LlmWiki;
    }

    private sealed class SpheresDir : ISphereDirectory
    {
        public readonly Dictionary<string, List<string>> Spheres = new();

        public bool Enabled(string ownerId) => true;
        public string? SphereOf(string ownerId, string projectId) =>
            Spheres.Keys.FirstOrDefault(s => Spheres[s].Contains(projectId));
        public IReadOnlyList<string> ProjectsOf(string ownerId, string sphereId) =>
            Spheres.TryGetValue(sphereId, out var list) ? list.ToList() : [];
        public string? SphereName(string ownerId, string sphereId) => Spheres.ContainsKey(sphereId) ? "Сфера" : null;
        public string? CharterOf(string ownerId, string sphereId) => null;
    }

    private sealed class FakeProjects : IProjectManager
    {
        public readonly Dictionary<string, Project> Items = new();

        public Project? GetById(string id) => Items.GetValueOrDefault(id);
        public IReadOnlyCollection<Project> GetByOwner(string userId) => Items.Values.Where(p => p.OwnerId == userId).ToList();
        public IReadOnlyCollection<Project> GetAll() => Items.Values.ToList();
        public IReadOnlyCollection<Project> GetByRootPath(string rootPath) => [];
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
                History.TryGetValue(sessionId, out var h) ? h : new List<StoredMessage>());
        public bool SetWikiMark(string sessionId, string? mark) => true;
    }

    private sealed class OwnSession(Session session) : IMcpSessionAccessor
    {
        public Session? GetOwned(string sessionId, string ownerId) =>
            sessionId == session.Id && ownerId == session.OwnerId ? session : null;
    }

    private sealed class AllowAllPersonas : IPersonaResolver
    {
        public Persona? Get(string id, string userId) => null;
    }

    // Инертные швы соседних сервисов: они нужны только чтобы тулсет был собран (все четыре
    // co-регистрируются одним рубильником вертикали). В этих тестах они не зовутся.
    private sealed class NoopKnowledgeIndex : IKnowledgeIndex
    {
        public bool IsConfigured => false;
        public Task<string> CreateDatasetAsync(string name, string permission = "only_me",
            string? description = null, string? indexingTechnique = null) => No<string>();
        public Task DeleteDocumentAsync(string datasetId, string documentId) => No();
        public Task DeleteDatasetAsync(string datasetId) => No();
        public Task RenameDatasetAsync(string datasetId, string newName) => No();
        public Task<DifyDocumentInfo> IndexFileByTextAsync(string datasetId, string fileName,
            string content, List<string>? tags = null,
            IReadOnlyDictionary<string, string>? metadata = null) => No<DifyDocumentInfo>();
        public Task<IReadOnlyList<DifyRetrieveChunk>> RetrieveAsync(string datasetId, string query,
            int topK = 8, IReadOnlyList<KnowledgeMetadataFilter>? filters = null, string logic = "and") =>
            No<IReadOnlyList<DifyRetrieveChunk>>();
        private static Task No() => Task.FromException(new InvalidOperationException("Dify не настроен"));
        private static Task<T> No<T>() => Task.FromException<T>(new InvalidOperationException("Dify не настроен"));
    }

    private sealed class NoopNoteTaskBridge : INoteTaskBridge
    {
        public IReadOnlyList<NoteTaskRef> GetBySourceNote(string ownerId, string noteId) => [];
        public NoteTaskRef Create(string? projectId, string ownerId, NoteTaskCreateRequest req) =>
            throw new InvalidOperationException("задача из заметки в этом тесте не ожидается");
        public NoteTaskRef? Update(string ownerId, string taskId, NoteTaskUpdateRequest req) => null;
        public NoteTaskRef? SpawnNextOccurrence(string ownerId, string completedTaskId) => null;
    }

    private sealed class NoopNotesNotifier : INotesHubNotifier
    {
        public Task BroadcastNotesChangedAsync(string userId, string action, string noteId, string? projectId = null) => Task.CompletedTask;
        public Task BroadcastTaskChangedAsync(string userId, string action, string taskId, string? projectId = null) => Task.CompletedTask;
    }

    private sealed class NoopCheapRunner : ICheapTextRunner
    {
        public bool UsesLocal(string actionKey) => false;
        public bool HasFreeRoute(string actionKey) => false;
        public bool HasRoute(string actionKey) => false;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "none";
        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default) =>
            Throw<string>(actionKey);
        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default) => Throw<string?>(actionKey);
        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default) =>
            Throw<OneShotResult>(actionKey);
        private static Task<T> Throw<T>(string actionKey) =>
            Task.FromException<T>(new InvalidOperationException($"ИИ-действие {actionKey} здесь не ожидается"));
    }

    private sealed class AllowAllBindings : IMcpPersonaBindings
    {
        public bool EffectiveToolEnabled(string? ownerId, Persona? persona, string key) => true;
        public bool SectionEnabled(string? ownerId, Persona? persona, string key) => true;
    }

    /// <summary>Модель разбора подменена: проход A даёт утверждение с дословной цитатой,
    /// проход B — штатное «нечего добавить». Проверяется путь заявки до модели и обратно,
    /// а не качество разбора (это — территория WikiIngestTests).</summary>
    private sealed class NothingLlm : ICheapTextRunner
    {
        private static readonly string Claims = """{"claims":[{"text":"пересказ","quote":"Решили: нарезку делает код"}]}""";
        private static readonly string Nothing = $$"""{"outcome":"{{WikiKeeperPrompts.NothingToAdd}}","reason":"нет ничего нового"}""";

        public bool UsesLocal(string actionKey) => false;
        public bool HasFreeRoute(string actionKey) => true;
        public bool HasRoute(string actionKey) => true;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "fake";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default) =>
            Task.FromResult(Answer(prompt));

        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default) =>
            Task.FromResult<string?>(Answer(prompt));

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default) =>
            Task.FromResult(new OneShotResult(Answer(prompt), new OneShotUsage(10, 0, 0, 5, 0.0, "fake"), 1));

        // Проход определяется по директиве своего промпта (WikiKeeperPrompts), как в
        // WikiIngestTests: порядок вызовов угадывать нельзя.
        private static string Answer(string prompt) =>
            prompt.Contains("extract durable", StringComparison.Ordinal) ? Claims : Nothing;
    }
}
