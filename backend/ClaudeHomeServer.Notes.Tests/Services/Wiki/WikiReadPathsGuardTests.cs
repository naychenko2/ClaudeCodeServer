using System.Text;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Services.Spheres;
using ClaudeHomeServer.Services.Turn;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож-перебор S6g-2 (ADR-036, решение 2; риск «Б» — «утечка между разделами одной
/// ошибкой фильтра: новый путь чтения забыл позвать WikiVisibility»). Все пути чтения
/// источника wiki для модели на одной фикстуре: ход чата проекта p1 сферы s1; соседний
/// q1 той же сферы — видим (решение принято: соседи по сфере видны); проект r другой
/// сферы s2 и личный раздел — в проектный ход не попадают никогда.
///
/// Каждый тест: заголовки и текст страниц personal и projects/r не доходят до выдачи хода.
/// Фикстура сама себе проверка: все пять страниц содержат одно поисковое слово, значит
/// различие в выдаче — от набора, а не от текста.
///
/// Пути: recall (NotesRecallContributor), notes_semantic_search, notes_list, notes_search,
/// notes_read (и ссылки на страницы вне набора в ОБЫЧНЫХ заметках), notes_backlinks,
/// notes_graph, notes_resolve, notes_thread (корень-страница и обычный корень),
/// notes_suggest_title, notes_daily (ссылки дневника), wiki_query, wiki_status.
/// Полнота перебора — сама тест здесь (СторожПеречисляетВсеИнструментыТулсета, доработка
/// S6g-2: прежний список заявлял полноту ложно): каждый Tool(...) тулсета либо покрыт,
/// либо пишущий (запись в вики отбивается), либо пропуск с названной причиной. Пути, которых
/// нет в коде, — в итоге задачи, не здесь.
/// </summary>
public class WikiReadPathsGuardTests : IDisposable
{
    private const string User = "u1";
    private const string Own = "projects/p1";
    private const string Foreign = "projects/r";

    // Маркеры — заголовки и текст двух запрещённых разделов. Их отсутствие в выдаче —
    // тот самый критерий: «0 хитов projects/R» про проект вне сферы и «personal в ход
    // проекта не попадает».
    private const string ForeignTitle = "Сущность-чужой-сферы";
    private const string PersonalTitle = "Личный-факт";
    private const string ForeignText = "чужесферное";
    private const string PersonalText = "личное-тайное";

    private const string Query = "сборка на linux";

    private readonly string _dir;
    private readonly IConfiguration _config;
    private readonly UserStore _users;
    private readonly FakeProjects _projects = new();
    // s1: p1 и q1 (ход p1 видит обоих); s2: r (вне набора хода p1).
    private readonly SpheresOn _spheres = new();
    private readonly RecordingIndex _index = new();
    private readonly NotesService _notes;
    private readonly NotesKnowledgeService _kb;
    private readonly NotesAiService _ai;
    private readonly NoteTaskSyncService _noteTasks;
    private readonly WikiKeeper _keeper;
    private readonly FakeSessions _sessions = new();

    public WikiReadPathsGuardTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_read_paths_guard_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        // Порог recall опущен: тестовые чанки подменённого Dify имеют score ниже боевого.
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
                ["Notes:AutoRecallMinScore"] = "0.1",
            })
            .Build();

        _users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        AddProject("p1");
        AddProject("q1");
        AddProject("r");
        _spheres.Spheres["s1"] = ["p1", "q1"];
        _spheres.Spheres["s2"] = ["r"];

        _notes = new NotesService(_projects, _config, NullLogger<NotesService>.Instance, flags: new WikiFlagOn());
        _kb = new NotesKnowledgeService(_index, _notes, _users, _config,
            NullLogger<NotesKnowledgeService>.Instance, _spheres, _projects);
        _ai = new NotesAiService(_notes, _config, new NoopCheapRunner());
        _noteTasks = new NoteTaskSyncService(_notes, new NoopNoteTaskBridge(), _projects, _kb,
            new NoopNotesNotifier(), NullLogger<NoteTaskSyncService>.Instance);
        _keeper = new WikiKeeper(_notes, _config);
        // Заведённость вики — признак источника (pages/_schema.md): без неё источник wiki
        // не появляется в графе и ни один путь чтения её не видит.
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

    private (NotesToolset Toolset, McpToolCallContext Ctx) McpEnv(string? projectId, bool withHorizontals = true)
    {
        var session = new Session { Id = "s-turn", OwnerId = User, ProjectId = projectId, Name = "Чат хода" };
        _sessions.All.Add(session);
        var toolset = new NotesToolset(new AllowAllPersonas(), new AllowAllBindings(), new OwnSession(session),
            new ClaudeHomeServer.Tests.Helpers.NotesNoopHubNotifier(), new ClaudeHomeServer.Tests.Helpers.NotesWideTurnFrame(),
                new ClaudeHomeServer.Tests.Helpers.NotesNoActorDenials(), _notes, _kb, _ai, _noteTasks,
            _config, null, null, null, _sessions,
            withHorizontals ? _spheres : null, withHorizontals ? _projects : null);
        return (toolset, new McpToolCallContext(User, "s-turn", "s-turn"));
    }

    private static McpToolCallResult Call(NotesToolset toolset, McpToolCallContext ctx, string tool, JsonObject args) =>
        toolset.CallAsync(tool, args, ctx, CancellationToken.None).GetAwaiter().GetResult();

    private static JsonObject Args(params (string Key, string Value)[] pairs)
    {
        var obj = new JsonObject();
        foreach (var (k, v) in pairs) obj[k] = v;
        return obj;
    }

    // ── фикстура вики: пять страниц одного поискового слова в пяти разделах ──

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

    private void Write(string sectionKey, string relUnderPages, string content)
    {
        var r = _keeper.WritePageAsync(User, sectionKey, relUnderPages, content).GetAwaiter().GetResult();
        r.Status.Should().Be(WikiOpStatus.Applied, r.Reason);
    }

    /// <summary>Своя страница — со ссылками в чужой сферный проект и в личное (их заголовки
    /// не должны дойти до хода ни через текст, ни через структурные списки). Чужая страница —
    /// со ссылкой на свою (её обратная ссылка на своей странице — [скрыто]).</summary>
    private void SeedWiki()
    {
        Write(Own, "Проекты/p1/Сущность-сборки.md", TopicPage("Сущность-сборки", "Сборку решено перенести на linux.",
            "\nСвязано: [[Проекты/r/Сущность-чужой-сферы]] и [[Личное/Личный-факт]]."));
        Write("projects/q1", "Проекты/q1/Сущность-соседа.md", TopicPage("Сущность-соседа", "Соседний проект: сборка на linux."));
        Write(Foreign, "Проекты/r/Сущность-чужой-сферы.md", TopicPage(ForeignTitle, $"{ForeignText}: сборка на linux.",
            $"\nСвязано: [[Проекты/p1/Сущность-сборки]]."));
        Write(WikiSections.Personal, "Личное/Личный-факт.md", TopicPage(PersonalTitle, $"{PersonalText}: сборка на linux."));
        Write(WikiSections.Common, "Общее/Общий-факт.md", TopicPage("Общий-факт", "Общее: сборку перенести на linux."));
        foreach (var key in new[] { Own, "projects/q1", Foreign, WikiSections.Personal, WikiSections.Common })
            WikiProjections.RegenerateSection(Root, key);
    }

    /// <summary>Фикстура + индексация в подменённый Dify: пять страниц вики и одна своя
    /// обычная заметка. Заметка нужна path-тестам recall: гейт HasIndex смотрит реестр
    /// :notes, и без хоть одного обычного документа контрибьютор не вызывается вовсе.</summary>
    private void SeedAndIndex()
    {
        SeedWiki();
        _notes.Create(User, new CreateNoteRequest(
            Title: "Своя-обычная", Content: "обычная заметка своего проекта: сборка на linux.", Source: "p1"));
        (_kb.SyncAllAsync(User)).GetAwaiter().GetResult().Should().Be(6);
    }

    /// <summary>Id страницы — детерминированный (base64url(sourceKey|relPath), NotesService.EncodeId):
    /// строится до первого чтения графа.</summary>
    private static string PageId(string relUnderPages) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes("wiki|" + relUnderPages))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private string DocIdOf(string title) =>
        _index.Indexed.First(i => i.Title == title).DocId;

    private string DatasetIdOf(string suffix) =>
        _index.Datasets.First(d => d.Name.EndsWith(suffix, StringComparison.Ordinal)).Id;

    private DifyRetrieveChunk ChunkWithDoc(string title, double score) =>
        new("фрагмент " + title, score, DocIdOf(title), title);

    private DifyRetrieveChunk ChunkRaw(string title, string content, double score) =>
        new(content, score, DocIdOf(title), title);

    /// <summary>Подменённый Dify намеренно игнорирует свой фильтр запроса и отдаёт все
    /// страницы, включая чужие разделы, — ровно то состояние, ради которого написан
    /// пост-фильтр кода. ChunkRaw для своей страницы — строка с ссылкой в чужой раздел:
    /// заголовок чужого раздела не должен дойти до хода и через сниппет своей страницы.</summary>
    private void GiveDifyEverything()
    {
        var wikiDs = DatasetIdOf(":wiki");
        _index.Response[wikiDs] = [
            ChunkRaw("Сущность-сборки",
                "Сборку решено перенести на linux. Связано: [[Проекты/r/Сущность-чужой-сферы]] и [[Личное/Личный-факт]].", 0.9),
            ChunkWithDoc("Сущность-соседа", 0.8),
            ChunkWithDoc(ForeignTitle, 0.7),
            ChunkWithDoc(PersonalTitle, 0.6),
            ChunkWithDoc("Общий-факт", 0.5),
        ];
    }

    // Путь хода целиком: настоящий контрибьютор сессии с ProjectId.
    private async Task<PromptSectionContribution?> RecallOf(string? projectId)
    {
        var session = new Session { Id = "s-recall", OwnerId = User, ProjectId = projectId, Name = "Чат" };
        var ctx = new PromptSessionContext(session, User, null, _dir, HasNotesMcp: true);
        return await new NotesRecallContributor(_kb, _config, NullLogger<NotesRecallContributor>.Instance)
            .BuildAsync(ctx, Query);
    }

    private static void AssertNoForeignSections(string text) =>
        text.Should().NotContain(ForeignTitle, "проект другой сферы в проектный ход не виден никогда (ADR-036 §2)")
            .And.NotContain(ForeignText, "текст страницы чужого раздела — тоже текст")
            .And.NotContain(PersonalTitle, "личный раздел в проектном ходу не виден никогда (ADR-036 §2)")
            .And.NotContain(PersonalText, "текст страницы личного раздела — тоже текст");

    // ─── путь 1: recall (NotesRecallContributor) ───

    [Fact]
    public async Task Recall_НеОтдаётЧужиеРазделы_ДажеЧерезСниппетСвоейСтраницы()
    {
        SeedAndIndex();
        GiveDifyEverything();

        var contribution = await RecallOf("p1");

        contribution.Should().NotBeNull();
        var block = contribution!.Sections[0].Text;
        // Своя страница и сосед по сфере — в блоке; чужие разделы — нет, в том числе
        // их заголовки внутри сниппета своей страницы (чанк Dify берётся из содержимого).
        block.Should().Contain("Сущность-сборки");
        block.Should().Contain("Сущность-соседа");
        AssertNoForeignSections(block);
    }

    // ─── путь 2: notes_semantic_search (тот же SearchAsync, что recall по запросу модели) ───

    [Fact]
    public async Task СемантическийПоискМодели_НеОтдаётЧужиеРазделы()
    {
        SeedAndIndex();
        GiveDifyEverything();
        var (toolset, ctx) = McpEnv("p1");

        var res = await toolset.CallAsync("notes_semantic_search", Args(("query", Query)),
            ctx, CancellationToken.None);

        res.IsError.Should().BeFalse(res.Text);
        res.Text.Should().Contain("Сущность-сборки");
        AssertNoForeignSections(res.Text);
    }

    [Fact]
    public async Task СемантическийПоискБезГоризонтали_ОтказСПричинойАНеПустойСписок()
    {
        // Ревизия S6g-1 (Глеб): при несобранной горизонтали модель обязана получить отказ
        // с причиной (тот же текст, что у wiki_query), а не пустой список: «нет результатов»
        // неотличим от «поиска не было».
        SeedWiki();
        var (toolset, ctx) = McpEnv("p1", withHorizontals: false);

        var res = await toolset.CallAsync("notes_semantic_search", Args(("query", Query)),
            ctx, CancellationToken.None);

        res.IsError.Should().BeTrue(res.Text);
        res.Text.Should().Contain("Горизонталь вики не собрана");
    }

    // ─── путь 3: notes_list ───

    [Fact]
    public void НотесЛист_НеОтдаётСтраницЛичногоРазделаИЧужогоПроекта()
    {
        SeedWiki();
        var (toolset, ctx) = McpEnv("p1");

        var res = Call(toolset, ctx, "notes_list", Args());

        res.IsError.Should().BeFalse(res.Text);
        // Свой раздел, сосед по сфере и «Общее» — видны (решение по сферам принято:
        // соседи той же сферы видны).
        res.Text.Should().Contain("Сущность-сборки");
        res.Text.Should().Contain("Сущность-соседа");
        res.Text.Should().Contain("Общий-факт");
        AssertNoForeignSections(res.Text);
    }

    // ─── путь 4: notes_search + префикс раздела с живым именем (решение 6) ───

    [Fact]
    public void НотесСёрч_СтраницаВикиИдётСПрефиксомРазделаСЖивымИменем()
    {
        SeedWiki();
        var (toolset, ctx) = McpEnv("p1");

        // Поисковое слово есть в содержимом всех пяти страниц — различие выдачи только
        // от набора, не от текста.
        var res = Call(toolset, ctx, "notes_search", Args(("query", "linux")));

        res.IsError.Should().BeFalse(res.Text);
        AssertNoForeignSections(res.Text);
        // Префикс — с живым именем проекта, не с id: агент не должен принять чужой
        // раздел за свой (ADR-036, решение 6).
        var entries = JsonNode.Parse(res.Text)!.AsArray();
        var own = entries.First(e => e!["title"]!.GetValue<string>() == "Сущность-сборки");
        own!["path"]!.GetValue<string>().Should().StartWith("Проекты/Проект p1/");
        var neighbor = entries.First(e => e!["title"]!.GetValue<string>() == "Сущность-соседа");
        neighbor!["path"]!.GetValue<string>().Should().StartWith("Проекты/Проект q1/");
    }

    // ─── путь 5: notes_read ───

    [Fact]
    public void ЯвныйРедЛичнойОбычнойЗаметкиИзЧатаПроекта_Работает()
    {
        // ADR-036, решение 6: явные notes_* по обычным заметкам НЕ сужаются —
        // «посмотри мою заметку X» из чата проекта живой сценарий (подтверждено Андреем).
        Directory.CreateDirectory(Path.Combine(_dir, "notes", User));
        var note = _notes.Create(User, new CreateNoteRequest(
            Title: "Личная-обычная", Content: "личное содержимое обычной заметки.", Source: "personal"));
        var (toolset, ctx) = McpEnv("p1");

        var res = Call(toolset, ctx, "notes_read", Args(("id", note.Id)));

        res.IsError.Should().BeFalse(res.Text);
        res.Text.Should().Contain("личное содержимое обычной заметки.");
    }

    [Fact]
    public void НотесРедСтраницыРазделаВнеНабора_Отказ()
    {
        // Главный пункт шага (ревью S6g-1): до S6g-2 notes_read страницы вне набора не
        // ограничивал вовсе. Мутация «убрать фильтр в notes_read для источника wiki»
        // краснит этот тест: чужая и личная страницы вернулись бы с текстом.
        SeedWiki();
        var (toolset, ctx) = McpEnv("p1");

        var foreign = Call(toolset, ctx, "notes_read", Args(("id", PageId("Проекты/r/Сущность-чужой-сферы.md"))));
        foreign.IsError.Should().BeTrue(foreign.Text);
        foreign.Text.Should().NotContain(ForeignTitle, "отказ не называет того, чего нет: невидимый раздел неотличим от несуществующего");

        var personal = Call(toolset, ctx, "notes_read", Args(("id", PageId("Личное/Личный-факт.md"))));
        personal.IsError.Should().BeTrue(personal.Text);

        // Сосед по сфере — видим (решение принято); его страница читается.
        var neighbor = Call(toolset, ctx, "notes_read", Args(("id", PageId("Проекты/q1/Сущность-соседа.md"))));
        neighbor.IsError.Should().BeFalse(neighbor.Text);
    }

    [Fact]
    public void НотесРедВидимойСтраницы_СсылкиВнеНабораСкрытыБезИмениИТекста()
    {
        SeedWiki();
        var (toolset, ctx) = McpEnv("p1");

        var res = Call(toolset, ctx, "notes_read", Args(("id", PageId("Проекты/p1/Сущность-сборки.md"))));

        res.IsError.Should().BeFalse(res.Text);
        res.Text.Should().Contain("[скрыто]", "ссылка на страницу вне набора заменяется [скрыто] (ADR-036 §2)");
        AssertNoForeignSections(res.Text);
        // Путь в выдаче — с префиксом раздела и живым именем (решение 6).
        JsonNode.Parse(res.Text)!["path"]!.GetValue<string>().Should().StartWith("Проекты/Проект p1/");
    }

    // ─── путь 6: notes_backlinks ───

    [Fact]
    public void НотесБэклинкс_СсылкаЧужогоРазделаПоказанаСкрытойБезИмениИТекста()
    {
        // c3: страница r ссылается на страницу p1; её обратная ссылка на p1 — факт связи,
        // но без имени и текста чужого раздела.
        SeedWiki();
        var (toolset, ctx) = McpEnv("p1");

        var res = Call(toolset, ctx, "notes_backlinks", Args(("id", PageId("Проекты/p1/Сущность-сборки.md"))));

        res.IsError.Should().BeFalse(res.Text);
        AssertNoForeignSections(res.Text);
        var entries = JsonNode.Parse(res.Text)!.AsArray();
        entries.Should().HaveCount(1, "факт связи сохраняется: запись есть, закрыто её имя и текст");
        var e = entries[0]!;
        e["sourceId"]!.GetValue<string>().Should().BeEmpty();
        e["sourceTitle"]!.GetValue<string>().Should().Be("[скрыто]");
        e["snippet"]!.GetValue<string>().Should().BeEmpty();

        // Чтение обратных ссылок самой чужой страницы — тот же отказ, что у notes_read.
        var own = Call(toolset, ctx, "notes_backlinks", Args(("id", PageId("Проекты/r/Сущность-чужой-сферы.md"))));
        own.IsError.Should().BeTrue(own.Text);
    }

    // ─── путь 7: notes_graph ───

    [Fact]
    public void НотесГраф_НеОтдаётУзловЛичногоРазделаИЧужогоПроекта()
    {
        SeedWiki();
        var (toolset, ctx) = McpEnv("p1");

        var res = Call(toolset, ctx, "notes_graph", Args());

        res.IsError.Should().BeFalse(res.Text);
        res.Text.Should().Contain("Сущность-сборки");
        res.Text.Should().Contain("Сущность-соседа");
        AssertNoForeignSections(res.Text);
        // Рёбра к исключённым узлам не остаются висящими: их тоже нет.
        var graph = JsonNode.Parse(res.Text)!.AsObject();
        var nodeIds = graph["nodes"]!.AsArray().Select(n => n!["id"]!.GetValue<string>()).ToList();
        graph["edges"]!.AsArray().Should().NotContain(e =>
            !nodeIds.Contains(e!["source"]!.GetValue<string>()) || !nodeIds.Contains(e!["target"]!.GetValue<string>()));
    }

    // ─── путь 8: wiki_query (ограничен S6c — сторож подтверждает) ───

    [Fact]
    public void ВикиКвери_НеОтдаётЧужиеРазделы()
    {
        SeedWiki();
        var (toolset, ctx) = McpEnv("p1");

        var res = Call(toolset, ctx, "wiki_query", Args(("question", "перенести сборку на linux")));

        res.IsError.Should().BeFalse(res.Text);
        res.Text.Should().Contain("Сущность-сборки");
        AssertNoForeignSections(res.Text);
    }

    // ─── путь 9: wiki_status ───

    [Fact]
    public void ВикиСтатус_НеНазываетПакетыЧужогоРаздела()
    {
        SeedWiki();
        var ownId = AddOpenPackage(Own, "Проекты/p1/Сущность-сборки.md");
        var foreignId = AddOpenPackage(Foreign, "Проекты/r/Сущность-чужой-сферы.md");
        var (toolset, ctx) = McpEnv("p1");

        var res = Call(toolset, ctx, "wiki_status", Args());

        res.IsError.Should().BeFalse(res.Text);
        res.Text.Should().Contain(ownId);
        res.Text.Should().NotContain(foreignId, "пакет невидимого раздела не называется в ходу (ADR-036 §2)");
        res.Text.Should().NotContain(Foreign, "ни id пакета, ни его раздел");

        // Прямый запрос пакета невидимого раздела — отказ как о несуществующем.
        var one = Call(toolset, ctx, "wiki_status", Args(("packageId", foreignId)));
        one.IsError.Should().BeTrue(one.Text);
        one.Text.Should().Contain("пакета с таким id нет");
    }

    private string AddOpenPackage(string section, string page)
    {
        var id = WikiPackageStore.NewId();
        var r = WikiPackageStore.Create(Root, new WikiPackage
        {
            Id = id,
            Section = section,
            Sources = [new WikiPackage.SourceRef("note", "note:x")],
            Operations = [new WikiPackage.Operation
            {
                Op = WikiPackage.OpAppend,
                // Страница пакета — путь, принадлежащий его разделу (ShapeErrors).
                Page = page,
                Text = "утверждение из чата",
                Class = WikiPackage.ClassKey("note", WikiPackage.OpAppend),
                Decision = WikiPackage.DecisionInbox,
                Reason = "спорное: ждёт человека",
                BaseHashes = new Dictionary<string, string>(),
            }],
            Status = WikiPackage.StatusChecking,
        });
        r.Ok.Should().BeTrue(r.Reason ?? "пакет обязан быть записан");
        return id;
    }

    // ─── путь 10: notes_resolve (доработка S6g-2, M1) ───

    [Fact]
    public void НотесРезолвСтраницыВнеНабора_ОтказПоПрефиксуИГоломуИмени()
    {
        // Пул резолва глобален (NotesService.Resolve): страница вне набора достижима и по
        // префиксу раздела, и по голому имени — оба пути дают отказ, неотличимый от
        // несуществующей. Мутация «убрать фильтр visible в notes_resolve» красит этот тест:
        // чужая и личная страницы вернулись бы с полным содержимым (пробы ревью S6g-2).
        SeedWiki();
        var (toolset, ctx) = McpEnv("p1");

        var byPrefix = Call(toolset, ctx, "notes_resolve", Args(("name", "Проекты/r/Сущность-чужой-сферы")));
        byPrefix.IsError.Should().BeTrue(byPrefix.Text);
        byPrefix.Text.Should().NotContain(ForeignText, "текст страницы вне набора не отдаётся")
            .And.NotContain(PageId("Проекты/r/Сущность-чужой-сферы.md"), "и её id");

        var byBareName = Call(toolset, ctx, "notes_resolve", Args(("name", "Сущность-чужой-сферы")));
        byBareName.IsError.Should().BeTrue(byBareName.Text);
        byBareName.Text.Should().NotContain(ForeignText);

        var personal = Call(toolset, ctx, "notes_resolve", Args(("name", "Личный-факт")));
        personal.IsError.Should().BeTrue(personal.Text);
        personal.Text.Should().NotContain(PersonalText, "личный раздел в проектном ходе не виден никогда (ADR-036 §2)");

        // Сосед по сфере — резолвится и читается (решение по сферам: соседи видны).
        var neighbor = Call(toolset, ctx, "notes_resolve", Args(("name", "Сущность-соседа")));
        neighbor.IsError.Should().BeFalse(neighbor.Text);
        neighbor.Text.Should().Contain("Сущность-соседа");
    }

    [Fact]
    public void НотесРезолвВидимойСтраницы_СсылкиВнеНабораСкрытыБезИмениИИд()
    {
        // Видимая страница отдаётся как в notes_read: RedactWikiDetail, и id скрытых целей
        // не остаётся в выдаче (по нему доставалась вся чужая страница).
        SeedWiki();
        var (toolset, ctx) = McpEnv("p1");

        var res = Call(toolset, ctx, "notes_resolve", Args(("name", "Сущность-сборки")));

        res.IsError.Should().BeFalse(res.Text);
        res.Text.Should().Contain("[скрыто]");
        res.Text.Should().NotContain(PageId("Проекты/r/Сущность-чужой-сферы.md"))
            .And.NotContain(PageId("Личное/Личный-факт.md"));
        AssertNoForeignSections(res.Text);
    }

    // ─── путь 11: notes_thread (доработка S6g-2, M2; доработка 2 — обычный корень) ───

    [Fact]
    public void НотесТредКорневойСтраницыВнеНабора_ТотЖеОтказЧтоУРед()
    {
        // Мутация «убрать фильтр в notes_thread» красит этот тест: root вернулся бы с полным
        // содержимым чужой страницы (GetReplies для страницы — пустой список, не ошибка).
        SeedWiki();
        var (toolset, ctx) = McpEnv("p1");

        var foreign = Call(toolset, ctx, "notes_thread", Args(("id", PageId("Проекты/r/Сущность-чужой-сферы.md"))));
        foreign.IsError.Should().BeTrue(foreign.Text);
        foreign.Text.Should().NotContain(ForeignText, "текст страницы вне набора не отдаётся").And.NotContain(ForeignTitle);

        var personal = Call(toolset, ctx, "notes_thread", Args(("id", PageId("Личное/Личный-факт.md"))));
        personal.IsError.Should().BeTrue(personal.Text);
        personal.Text.Should().NotContain(PersonalText);

        // Корень-страница видимого соседа — читается (replies — пустой список).
        var neighbor = Call(toolset, ctx, "notes_thread", Args(("id", PageId("Проекты/q1/Сущность-соседа.md"))));
        neighbor.IsError.Should().BeFalse(neighbor.Text);
        neighbor.Text.Should().Contain("Сущность-соседа");
    }

    [Fact]
    public void НотесТредОбычногоКорня_СсылкиНаВикиВнеНабораБезИмениИTargetId()
    {
        // Ревью S6g-2, доработка 2, M3: обычный корень возвращался как есть — его Links
        // содержали настоящие targetId/targetTitle скрытых страниц, а по targetId дальше
        // достижимы notes_promote_task и memory_from_note. Мутация «не редактировать обычный
        // корень» краснит этот тест: targetId/targetTitle вернулись бы настоящими.
        SeedWiki();
        var noteId = SeedOrdinaryWithWikiLinks();
        var foreignId = PageId("Проекты/r/Сущность-чужой-сферы.md");
        var personalId = PageId("Личное/Личный-факт.md");
        var (toolset, ctx) = McpEnv("p1");

        var res = Call(toolset, ctx, "notes_thread", Args(("id", noteId)));

        res.IsError.Should().BeFalse(res.Text);
        res.Text.Should().NotContain(foreignId, "id скрытой страницы не отдаётся: по ней достижима вся страница")
            .And.NotContain(personalId);
        var root = JsonNode.Parse(res.Text)!["root"]!.AsObject();
        var links = root["links"]!.AsArray();
        links.Should().HaveCount(2, "факт связи сохраняется: запись есть, закрыты её имя и id");
        foreach (var l in links)
        {
            l!["targetId"]!.GetValue<string>().Should().BeEmpty();
            l!["targetTitle"]!.GetValue<string>().Should().Be("[скрыто]");
        }
        // Текст самой заметки — текст пользователя, он не сужается (решение 6): остаётся как есть.
        root["content"]!.GetValue<string>().Should().Contain("[[Проекты/r/Сущность-чужой-сферы]]");
    }

    // ─── путь 12: ссылки на страницы вне набора в обычных заметках (доработка S6g-2, M3) ───

    /// <summary>Обычная заметка своего проекта со ссылками на чужой проектный и личный разделы
    /// (префиксные — так их пишет код и человек между разделами, ADR-035, решение 1).</summary>
    private string SeedOrdinaryWithWikiLinks() =>
        _notes.Create(User, new CreateNoteRequest(
            Title: "Своя-обычная-со-ссылками",
            Content: "Работа с [[Проекты/r/Сущность-чужой-сферы]] и [[Личное/Личный-факт]]: сборка на linux.",
            Source: "p1")).Id;

    [Fact]
    public void ОбычнаяЗаметкаРед_СсылкиНаВикиВнеНабораБезИмениИTargetId()
    {
        // Сама обычная заметка не сужается (решение 6), но её Links — системная проекция
        // страниц-целей: targetTitle/targetId чужого раздела давали имя и id, по которому
        // доставалась вся скрытая страница (M1/M2). Мутация «не редактировать ссылки в обычной
        // заметке» красит этот тест: targetId/targetTitle вернулись бы настоящими.
        SeedWiki();
        var noteId = SeedOrdinaryWithWikiLinks();
        var foreignId = PageId("Проекты/r/Сущность-чужой-сферы.md");
        var personalId = PageId("Личное/Личный-факт.md");
        var (toolset, ctx) = McpEnv("p1");

        var res = Call(toolset, ctx, "notes_read", Args(("id", noteId)));

        res.IsError.Should().BeFalse(res.Text);
        res.Text.Should().NotContain(foreignId, "id скрытой страницы не отдаётся: по нему доставалась вся страница")
            .And.NotContain(personalId);
        var links = JsonNode.Parse(res.Text)!["links"]!.AsArray();
        links.Should().HaveCount(2, "факт связи сохраняется: запись есть, закрыты её имя и id");
        foreach (var l in links)
        {
            l!["targetId"]!.GetValue<string>().Should().BeEmpty();
            l!["targetTitle"]!.GetValue<string>().Should().Be("[скрыто]");
        }
        // Текст самой заметки — текст пользователя, он не сужается (решение 6): остаётся как есть.
        JsonNode.Parse(res.Text)!["content"]!.GetValue<string>().Should().Contain("[[Проекты/r/Сущность-чужой-сферы]]");
    }

    [Fact]
    public void Редакция_ВидимаяСтраницаСоседа_ЦельНеСкрыта()
    {
        // Обратное направление редакции (W3-7): HiddenWikiRef (NotesToolset.cs:629-631)
        // скрывает только цель вне набора. Ссылка обычной заметки на видимую страницу
        // соседа по сфере — системная проекция видимой цели: её targetId/targetTitle
        // настоящие. Мутация «закрывать любую вики-ссылку независимо от набора» (убрать
        // в HiddenWikiRef проверку раздела целиком) краснит этот тест: цель стала бы
        // [скрыто] с пустым id. Буквальное «убрать только !visible.Contains» оставляет
        // is not { } key — оно закрывает только неразобранные пути, тест при нём зелёный.
        SeedWiki();
        var noteId = _notes.Create(User, new CreateNoteRequest(
            Title: "Своя-обычная-на-соседа",
            Content: "Работа с [[Проекты/q1/Сущность-соседа]]: сборка на linux.",
            Source: "p1")).Id;
        var neighborId = PageId("Проекты/q1/Сущность-соседа.md");
        var (toolset, ctx) = McpEnv("p1");

        var res = Call(toolset, ctx, "notes_read", Args(("id", noteId)));

        res.IsError.Should().BeFalse(res.Text);
        var links = JsonNode.Parse(res.Text)!["links"]!.AsArray();
        links.Should().HaveCount(1);
        var l = links[0]!;
        l!["targetId"]!.GetValue<string>().Should().Be(neighborId,
            "видимая цель остаётся достижимой: по её id читается страница соседа");
        l!["targetTitle"]!.GetValue<string>().Should().Be("Сущность-соседа");

        // «Не скрыта» проверяется и достижимостью цели этим же ходом.
        var page = Call(toolset, ctx, "notes_read", Args(("id", neighborId)));
        page.IsError.Should().BeFalse(page.Text);
    }

    [Fact]
    public void НотесСёрчЛистОбычнойЗаметки_ЗаметкаВиднаНоИдСкрытыхСтраницВВыдачеНет()
    {
        // Список/поиск обычной заметки не сужаются (решение 6); их выдача — brief без ссылок,
        // и id скрытых страниц в ней появиться не может ни одним путём.
        SeedWiki();
        var noteId = SeedOrdinaryWithWikiLinks();
        var (toolset, ctx) = McpEnv("p1");

        var search = Call(toolset, ctx, "notes_search", Args(("query", "linux")));
        search.IsError.Should().BeFalse(search.Text);
        JsonNode.Parse(search.Text)!.AsArray().Should().Contain(
            e => e!["id"]!.GetValue<string>() == noteId, "обычная заметка не сужается");
        AssertNoForeignSections(search.Text);

        var list = Call(toolset, ctx, "notes_list", Args());
        list.IsError.Should().BeFalse(list.Text);
        AssertNoForeignSections(list.Text);
    }

    [Fact]
    public void NotesDaily_ЯвноСкрываетСсылкуНаСкрытуюСтраницу()
    {
        // c1 (W3-7): текст дневника — обычная заметка, не сужается (решение 6); но его Links —
        // системная проекция страниц-целей: тот же RedactHiddenWikiLinks, что в notes_read/
        // notes_resolve. Мутация «убрать вызов RedactHiddenWikiLinks в notes_daily» краснит
        // этот тест: targetId/targetTitle скрытых страниц вернулись бы настоящими.
        SeedWiki();
        var (toolset, ctx) = McpEnv("p1");

        var res = Call(toolset, ctx, "notes_daily",
            Args(("content", "Работа с [[Проекты/r/Сущность-чужой-сферы]] и [[Личное/Личный-факт]].")));

        res.IsError.Should().BeFalse(res.Text);
        var foreignId = PageId("Проекты/r/Сущность-чужой-сферы.md");
        var personalId = PageId("Личное/Личный-факт.md");
        res.Text.Should().NotContain(foreignId, "id скрытой страницы не отдаётся: по нему доставалась вся страница")
            .And.NotContain(personalId);
        var links = JsonNode.Parse(res.Text)!["links"]!.AsArray();
        links.Should().HaveCount(2, "факт связи сохраняется: запись есть, закрыты её имя и id");
        foreach (var l in links)
        {
            l!["targetId"]!.GetValue<string>().Should().BeEmpty();
            l!["targetTitle"]!.GetValue<string>().Should().Be("[скрыто]");
        }
        // Текст дневника — текст пользователя, он не сужается (решение 6): остаётся как есть.
        JsonNode.Parse(res.Text)!["content"]!.GetValue<string>().Should().Contain("[[Проекты/r/Сущность-чужой-сферы]]");
    }

    // ─── путь 13: notes_suggest_title (доработка S6g-2, M3) ───

    [Fact]
    public void НотесСаджестТитлВикиВнеНабора_ОтказДоВызоваСервиса()
    {
        // SuggestTitleAsync отправляет Truncate(Content, 2000) в промпт LLM. NoopCheapRunner
        // бросает именованную ошибку: если бы код дошёл до сервиса, отказ назвал бы её.
        SeedWiki();
        var (toolset, ctx) = McpEnv("p1");

        var foreign = Call(toolset, ctx, "notes_suggest_title", Args(("id", PageId("Проекты/r/Сущность-чужой-сферы.md"))));
        foreign.IsError.Should().BeTrue(foreign.Text);
        foreign.Text.Should().Contain("не найдена", "неотличима от несуществующей (ADR-036, решение 2)");
        foreign.Text.Should().NotContain("ИИ-действие", "сервис не вызван: текст чужой страницы не ушёл в промпт модели")
            .And.NotContain(ForeignTitle);

        // Видимая страница — доходит до сервиса: фильтр не ломает обычный путь.
        var neighbor = Call(toolset, ctx, "notes_suggest_title", Args(("id", PageId("Проекты/q1/Сущность-соседа.md"))));
        neighbor.IsError.Should().BeTrue(neighbor.Text);
        neighbor.Text.Should().Contain("ИИ-действие", "до сервиса дошло: текст видимой страницы может идти в модель");
    }

    // ─── путь 14: полнота перебора (доработка S6g-2: «сторож заявляет полноту ложно») ───

    /// <summary>Инструменты, не читающие страниц вики для модели, — с названной причиной.</summary>
    private static readonly Dictionary<string, string> SkippedWithReason = new(StringComparer.Ordinal)
    {
        ["notes_annotations"] = "документ — файл проекта или личного vault; пути вики (data/wiki/) не область документа: страница недостижима",
        ["notes_promote_task"] = "id скрытой страницы недостижим из покрытых путей; текст чекбокса становится текстом созданной задачи, а не чтением заметки",
        ["wiki_ingest"] = "заявка в очередь: текст источника уходит в захват и Хранителю, в выдачу модели не возвращается",
        ["wiki_add_fact"] = "то же: выдержка и факт идут в заявку, модель получает только исход",
    };

    /// <summary>Пишущие: запись в вики отбивается WikiReadOnlyException (EnsureNotWiki),
    /// содержимое страниц не отдают.</summary>
    private static readonly string[] WritingTools =
    [
        "notes_create", "notes_update", "notes_move", "notes_delete",
        "notes_annotate", "notes_reply", "notes_set_status",
    ];

    /// <summary>Читающие — каждый покрыт тестом этого файла.</summary>
    private static readonly string[] CoveredReadTools =
    [
        "notes_list", "notes_search", "notes_read", "notes_semantic_search",
        "notes_backlinks", "notes_graph", "notes_resolve", "notes_thread",
        "notes_suggest_title", "notes_daily", "wiki_query", "wiki_status",
    ];

    [Fact]
    public void СторожПеречисляетВсеИнструментыТулсета()
    {
        var all = NotesToolset.CoreTools.Concat(NotesToolset.WikiTools).Concat(NotesToolset.AnnotationTools)
            .Select(t => t.Name).ToList();

        all.Should().OnlyContain(n => CoveredReadTools.Contains(n, StringComparer.Ordinal)
            || WritingTools.Contains(n, StringComparer.Ordinal)
            || SkippedWithReason.ContainsKey(n),
            "новый инструмент без записи в списке — красный тест (Ревью S6g-2: прежнее перечисление заявляло полноту ложно)");
        all.Should().HaveCount(CoveredReadTools.Length + WritingTools.Length + SkippedWithReason.Count,
            "списки не должны устаревать: убранный инструмент — тоже находка");
    }

    // ─── обвязка швов ───

    private sealed class WikiFlagOn : IFeatureFlagGate
    {
        public bool IsEnabled(string userId, string key) => key == FeatureFlagKeys.LlmWiki;
    }

    // Сферы включены: s1 с p1 и q1 (тот же владелец); r — в сфере s2. Форма — как SpheresOn
    // в WikiRecallFilterTests / FakeDir в WikiVisibilityTests.
    private sealed class SpheresOn : ISphereDirectory
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
        public IReadOnlyCollection<Project> GetByOwner(string userId) =>
            Items.Values.Where(p => p.OwnerId == userId).ToList();
        public IReadOnlyCollection<Project> GetAll() => Items.Values.ToList();
        public IReadOnlyCollection<Project> GetByRootPath(string rootPath) => [];
    }

    private sealed class FakeSessions : ISessionDirectory
    {
        public readonly List<Session> All = [];

        public Session? GetById(string id) => All.FirstOrDefault(s => s.Id == id);
        public IReadOnlyCollection<Session> GetAll() => All;
        public string? ResolveOwnerId(Session s) => s.OwnerId;
        public Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId) =>
            Task.FromResult<IReadOnlyList<StoredMessage>>(new List<StoredMessage>());
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

    private sealed class AllowAllBindings : IMcpPersonaBindings
    {
        public bool EffectiveToolEnabled(string? ownerId, Persona? persona, string key) => true;
        public bool SectionEnabled(string? ownerId, Persona? persona, string key) => true;
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

    /// <summary>Подменённый Dify: помнит датасеты, индексацию и запросы; отвечает тем, что
    /// положено в Response, — фильтр запроса игнорирует намеренно (гарантию держит
    /// пост-фильтр кода, не Dify). Как в WikiRecallFilterTests.</summary>
    private sealed class RecordingIndex : IKnowledgeIndex
    {
        public bool IsConfigured => true;
        public readonly List<(string Name, string Id)> Datasets = new();
        public readonly List<(string Title, string DocId)> Indexed = new();
        public readonly List<(string DatasetId, int TopK, IReadOnlyList<KnowledgeMetadataFilter>? Filters, string Logic)> Requests = new();
        public readonly Dictionary<string, List<DifyRetrieveChunk>> Response = new();

        public Task<string> CreateDatasetAsync(string name, string permission = "only_me",
            string? description = null, string? indexingTechnique = null)
        {
            var id = "ds-" + name;
            Datasets.Add((name, id));
            return Task.FromResult(id);
        }

        public Task DeleteDocumentAsync(string datasetId, string documentId) => Task.CompletedTask;
        public Task DeleteDatasetAsync(string datasetId) => Task.CompletedTask;
        public Task RenameDatasetAsync(string datasetId, string newName) => Task.CompletedTask;

        public Task<DifyDocumentInfo> IndexFileByTextAsync(string datasetId, string fileName,
            string content, List<string>? tags = null,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            var docId = "doc-" + (Indexed.Count + 1);
            Indexed.Add((fileName, docId));
            return Task.FromResult(new DifyDocumentInfo(docId, fileName, "completed"));
        }

        public Task<IReadOnlyList<DifyRetrieveChunk>> RetrieveAsync(string datasetId, string query,
            int topK = 8, IReadOnlyList<KnowledgeMetadataFilter>? filters = null, string logic = "and")
        {
            Requests.Add((datasetId, topK, filters, logic));
            IReadOnlyList<DifyRetrieveChunk> r =
                Response.TryGetValue(datasetId, out var prepared) ? prepared : new List<DifyRetrieveChunk>();
            return Task.FromResult<IReadOnlyList<DifyRetrieveChunk>>(r.Take(topK).ToList());
        }
    }
}