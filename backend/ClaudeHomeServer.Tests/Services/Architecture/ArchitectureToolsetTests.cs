using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Architecture;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Mcp.Http;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.Architecture;

// MCP-тулсет arch_*: персона в чате проекта читает и правит C4-модель через общее
// хранилище раздела (версия → конфликт), доступ — по владельцу токена и сессии-вызывателю.
public class ArchitectureToolsetTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Stranger = "owner-2";

    private const string Model = """
        {"state":{"model":{
          "systems":[{"id":"s1","name":"CCS","type":"system","position":{"x":0,"y":0},"connections":[]}],
          "containers":[
            {"id":"c1","name":"Бэкенд","type":"container","systemId":"s1","technology":"ASP.NET Core",
             "position":{"x":10,"y":20},"connections":[{"targetId":"c2","label":"хранит"}],
             "documentations":[{"id":"d1","title":"Заметка","markdown":"Главный процесс сервера"}]},
            {"id":"c2","name":"Хранилище","type":"container","systemId":"s1","connections":[]}
          ],
          "components":[{"id":"k1","name":"SessionManager","type":"component","containerId":"c1","systemId":"s1","connections":[]}],
          "codeElements":[],"viewLevel":"system"}},"version":0}
        """;

    private readonly string _tempDir;
    private readonly string _root;
    private readonly ArchitectureModelStore _store = new();
    private readonly FakeSessions _sessions = new();
    private readonly FakeProjects _projects = new();
    private readonly FakePersonas _personas = new();
    private readonly FakeGate _gate = new();
    private readonly ArchitectureToolset _toolset;

    public ArchitectureToolsetTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "arch_toolset_tests_" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_tempDir, "proj");
        Directory.CreateDirectory(Path.Combine(_root, "docs", "architecture"));
        File.WriteAllText(ModelPath, Model);

        _projects.Items["p1"] = new Project { Id = "p1", Name = "Проект", OwnerId = Owner, RootPath = _root };
        _sessions.Items["chat-1"] = new Session { Id = "chat-1", ProjectId = "p1", OwnerId = Owner };
        _sessions.Items["chat-free"] = new Session { Id = "chat-free", OwnerId = Owner };
        _toolset = new ArchitectureToolset(_store, _sessions, _projects, _personas, _gate);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private string ModelPath => Path.Combine(_root, "docs", "architecture", "model.viaduct.json");

    private static McpToolCallContext Ctx(string session = "chat-1", string owner = Owner) =>
        new(owner, session, session);

    private Task<McpToolCallResult> Call(string tool, JsonObject? args = null, McpToolCallContext? ctx = null) =>
        _toolset.CallAsync(tool, args ?? [], ctx ?? Ctx(), CancellationToken.None);

    private JsonObject FileModel() => (JsonObject)JsonNode.Parse(File.ReadAllText(ModelPath))!;

    private static JsonObject ContainerOf(JsonObject doc, string id) =>
        doc["state"]!["model"]!["containers"]!.AsArray().OfType<JsonObject>().Single(o => (string?)o["id"] == id);

    // --- Состав ---

    [Fact]
    public void ToolsFor_ЧатПроектаВладельца_ПолныйПостоянныйСостав()
    {
        var first = _toolset.ToolsFor(Ctx()).Select(t => t.Name).ToList();
        first.Should().Equal("arch_context", "arch_search", "arch_get_element", "arch_create_element",
            "arch_update_element", "arch_delete_element", "arch_set_connection");
        // Состав от вызова к вызову (ходу к ходу) не меняется
        _toolset.ToolsFor(Ctx()).Select(t => t.Name).Should().Equal(first);
    }

    [Theory]
    [InlineData("chat-free", Owner)]      // чат вне проекта
    [InlineData("chat-1", Stranger)]      // чужая сессия
    [InlineData("нет/такого", Owner)]      // кривой хвост
    [InlineData("missing", Owner)]        // сессии нет
    public void ToolsFor_НеПроектныйИлиЧужой_ПустойСостав(string session, string owner) =>
        _toolset.ToolsFor(Ctx(session, owner)).Should().BeEmpty();

    [Fact]
    public async Task OffПривязкаПерсоны_СоставПуст_ВызовОтклонён()
    {
        _gate.Off = true;
        _toolset.ToolsFor(Ctx()).Should().BeEmpty();
        var result = await Call("arch_context");
        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("tool:architecture");
    }

    [Fact]
    public async Task ЧужойПроект_ВызовОтклонён()
    {
        _projects.Items["p1"].OwnerId = Stranger;
        var result = await Call("arch_context");
        result.IsError.Should().BeTrue();
    }

    // --- Чтение ---

    [Fact]
    public async Task Context_ДеревоСIdИВерсией()
    {
        var result = await Call("arch_context");
        result.IsError.Should().BeFalse();
        result.Text.Should().Contain("version=").And.Contain("CCS").And.Contain("id=c1")
            .And.Contain("SessionManager").And.Contain("связей: 1");
    }

    [Fact]
    public async Task Search_НаходитПоТехнологии()
    {
        var result = await Call("arch_search", new JsonObject { ["query"] = "asp.net" });
        result.Text.Should().Contain("Бэкенд").And.Contain("id=c1").And.NotContain("Хранилище");
    }

    [Fact]
    public async Task GetElement_ДокиСвязиИДети()
    {
        var result = await Call("arch_get_element", new JsonObject { ["id"] = "c1" });
        result.IsError.Should().BeFalse();
        result.Text.Should().Contain("Главный процесс сервера")        // документ целиком
            .And.Contain("Хранилище (id=c2) «хранит»")                  // исходящая связь с именем
            .And.Contain("SessionManager")                               // вложенный компонент
            .And.Contain("CCS / Бэкенд");                                // путь в иерархии
    }

    [Fact]
    public async Task МоделиНет_ЧестныйОтказ()
    {
        File.Delete(ModelPath);
        var result = await Call("arch_context");
        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("Собрать из кода");
    }

    // --- Правка ---

    [Fact]
    public async Task UpdateElement_ПишетФайлЧерезХранилище_ОстальноеНеТрогает()
    {
        _sessions.Items["chat-1"].PersonaId = "per-1";
        _personas.Items["per-1"] = new Persona { Id = "per-1", Name = "Максим", OwnerId = Owner };

        var result = await Call("arch_update_element", new JsonObject
        {
            ["id"] = "c1",
            ["description"] = "Ядро: сессии и ходы",
            ["technology"] = "",                        // пустая строка — очистить
            ["tags"] = new JsonArray("core", "Core", "api"),
        });

        result.IsError.Should().BeFalse(result.Text);
        var c1 = ContainerOf(FileModel(), "c1");
        ((string?)c1["description"]).Should().Be("Ядро: сессии и ходы");
        c1.ContainsKey("technology").Should().BeFalse();
        c1["tags"]!.AsArray().Select(t => (string?)t).Should().Equal("core", "api");
        // Позиция, доки и связи — как были
        ((int)c1["position"]!["x"]!).Should().Be(10);
        c1["documentations"]!.AsArray().Should().HaveCount(1);
        c1["connections"]!.AsArray().Should().HaveCount(1);

        // Раздел видит правку тем же хранилищем, автор — персона
        var snapshot = await _store.ReadAsync(_root, CancellationToken.None);
        result.Text.Should().Contain(snapshot.Version!);
        snapshot.Author.UpdatedBy.Should().Be("Максим");
    }

    [Fact]
    public async Task UpdateElement_УстаревшаяВерсия_КонфликтФайлНеТронут()
    {
        var before = File.ReadAllText(ModelPath);
        var result = await Call("arch_update_element", new JsonObject
        {
            ["id"] = "c1", ["name"] = "Другое", ["version"] = "deadbeef",
        });
        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("Конфликт версий");
        File.ReadAllText(ModelPath).Should().Be(before);
    }

    [Fact]
    public async Task UpdateElement_АктуальнаяВерсия_Проходит()
    {
        var version = (await _store.ReadAsync(_root, CancellationToken.None)).Version!;
        var result = await Call("arch_update_element", new JsonObject
        {
            ["id"] = "c2", ["name"] = "PostgreSQL", ["version"] = version,
        });
        result.IsError.Should().BeFalse(result.Text);
        ((string?)ContainerOf(FileModel(), "c2")["name"]).Should().Be("PostgreSQL");
    }

    [Fact]
    public async Task UpdateElement_НетЭлемента_ОтказБезЗаписи()
    {
        var before = File.ReadAllText(ModelPath);
        var result = await Call("arch_update_element", new JsonObject { ["id"] = "zzz", ["name"] = "X" });
        result.IsError.Should().BeTrue();
        File.ReadAllText(ModelPath).Should().Be(before);
    }

    [Fact]
    public async Task SetConnection_СоздаётИУдаляет()
    {
        var created = await Call("arch_set_connection", new JsonObject
        {
            ["sourceId"] = "c2", ["targetId"] = "c1", ["label"] = "отдаёт данные", ["technology"] = "SQL",
        });
        created.IsError.Should().BeFalse(created.Text);
        var link = ContainerOf(FileModel(), "c2")["connections"]!.AsArray().OfType<JsonObject>().Single();
        ((string?)link["targetId"]).Should().Be("c1");
        ((string?)link["label"]).Should().Be("отдаёт данные");

        var removed = await Call("arch_set_connection", new JsonObject
        {
            ["sourceId"] = "c2", ["targetId"] = "c1", ["remove"] = true,
        });
        removed.IsError.Should().BeFalse(removed.Text);
        ContainerOf(FileModel(), "c2")["connections"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public async Task SetConnection_ПравитСуществующуюНеДублируя()
    {
        var result = await Call("arch_set_connection", new JsonObject
        {
            ["sourceId"] = "c1", ["targetId"] = "c2", ["label"] = "читает и пишет",
        });
        result.IsError.Should().BeFalse(result.Text);
        var links = ContainerOf(FileModel(), "c1")["connections"]!.AsArray().OfType<JsonObject>().ToList();
        links.Should().ContainSingle();
        ((string?)links[0]["label"]).Should().Be("читает и пишет");
    }

    [Theory]
    [InlineData("arch_update_element")]
    [InlineData("arch_create_element")]
    [InlineData("arch_delete_element")]
    [InlineData("arch_set_connection")]
    public async Task ПерсонаТолькоЧтение_ЧитаетНоНеПишет(string tool)
    {
        _sessions.Items["chat-1"].PersonaId = "ro";
        _personas.Items["ro"] = new Persona { Id = "ro", Name = "Ревьюер", OwnerId = Owner, Access = PersonaAccess.ReadOnly };
        var before = File.ReadAllText(ModelPath);

        (await Call("arch_context")).IsError.Should().BeFalse();
        var write = await Call(tool, new JsonObject
        {
            ["id"] = "c1", ["name"] = "X", ["level"] = "component", ["parentId"] = "c1",
            ["sourceId"] = "c2", ["targetId"] = "c1",
        });
        write.IsError.Should().BeTrue();
        write.Text.Should().Contain("Только чтение");
        File.ReadAllText(ModelPath).Should().Be(before);
    }

    // --- Создание ---

    private static JsonArray ArrayOf(JsonObject doc, string name) => doc["state"]!["model"]![name]!.AsArray();

    private static JsonObject ById(JsonObject doc, string array, string id) =>
        ArrayOf(doc, array).OfType<JsonObject>().Single(o => (string?)o["id"] == id);

    private static string CreatedId(McpToolCallResult result) =>
        System.Text.RegularExpressions.Regex.Match(result.Text, @"id=([0-9a-f-]{36})").Groups[1].Value;

    [Fact]
    public async Task CreateElement_НаКаждомУровне_ЦепочкаПредковИСвободнаяКлетка()
    {
        var sys = await Call("arch_create_element", new JsonObject { ["level"] = "system", ["name"] = "Платёжка" });
        sys.IsError.Should().BeFalse(sys.Text);
        var sysId = CreatedId(sys);
        Guid.TryParse(sysId, out _).Should().BeTrue("id — UUID, как у элементов редактора Viaduct");
        var sysNode = ById(FileModel(), "systems", sysId);
        ((string?)sysNode["type"]).Should().Be("system");
        // (0,0) занят системой s1; соседей станет два → две колонки → вторая клетка первой строки
        ((int)sysNode["position"]!["x"]!, (int)sysNode["position"]!["y"]!)
            .Should().Be((ArchitectureModelMerger.GridStepX, 0));

        var ctr = await Call("arch_create_element", new JsonObject
        {
            ["level"] = "container", ["name"] = "Шлюз", ["parentId"] = "s1",
            ["technology"] = "Go", ["tags"] = new JsonArray("edge"), ["external"] = true,
            ["description"] = new string('я', 200),
        });
        ctr.IsError.Should().BeFalse(ctr.Text);
        var ctrNode = ById(FileModel(), "containers", CreatedId(ctr));
        ((string?)ctrNode["systemId"]).Should().Be("s1");
        ((string?)ctrNode["technology"]).Should().Be("Go");
        ((bool)ctrNode["external"]!).Should().BeTrue();
        ((string?)ctrNode["description"])!.Length.Should().Be(ArchitectureModelEditor.DescriptionMax);
        // Соседи c1 (10,20) и c2 (без позиции места не занимает): клетка (0,0) занята c1,
        // соседей станет три → две колонки → вторая клетка первой строки
        ((int)ctrNode["position"]!["x"]!, (int)ctrNode["position"]!["y"]!)
            .Should().Be((ArchitectureModelMerger.GridStepX, 0));

        var cmp = await Call("arch_create_element", new JsonObject
        {
            ["level"] = "component", ["name"] = "TurnRunner", ["parentId"] = "c1",
        });
        cmp.IsError.Should().BeFalse(cmp.Text);
        var cmpId = CreatedId(cmp);
        var cmpNode = ById(FileModel(), "components", cmpId);
        ((string?)cmpNode["containerId"]).Should().Be("c1");
        ((string?)cmpNode["systemId"]).Should().Be("s1");

        var code = await Call("arch_create_element", new JsonObject
        {
            ["level"] = "code", ["name"] = "RunAsync", ["parentId"] = cmpId,
        });
        code.IsError.Should().BeFalse(code.Text);
        var codeNode = ById(FileModel(), "codeElements", CreatedId(code));
        ((string?)codeNode["componentId"]).Should().Be(cmpId);
        ((string?)codeNode["containerId"]).Should().Be("c1");
        ((string?)codeNode["systemId"]).Should().Be("s1");
        codeNode["connections"]!.AsArray().Should().BeEmpty();

        // Ответ несёт новую version — ту, что лежит в хранилище
        var snapshot = await _store.ReadAsync(_root, CancellationToken.None);
        code.Text.Should().Contain("version=" + snapshot.Version);
    }

    [Theory]
    [InlineData("container", null)]         // нет родителя
    [InlineData("container", "c1")]         // родитель — контейнер, нужен system
    [InlineData("component", "s1")]         // через ступень
    [InlineData("code", "c1")]              // родитель — контейнер, нужен component
    [InlineData("system", "s1")]            // у системы родителя нет
    [InlineData("container", "zzz")]        // родителя нет в модели
    [InlineData("module", "s1")]            // неизвестный уровень
    public async Task CreateElement_НеверныйРодительИлиУровень_ОтказБезЗаписи(string level, string? parent)
    {
        var before = File.ReadAllText(ModelPath);
        var args = new JsonObject { ["level"] = level, ["name"] = "Новый" };
        if (parent is not null) args["parentId"] = parent;
        var result = await Call("arch_create_element", args);
        result.IsError.Should().BeTrue();
        File.ReadAllText(ModelPath).Should().Be(before);
    }

    [Fact]
    public async Task CreateElement_ИмяЗанятоУРодителя_ОтказСId()
    {
        var result = await Call("arch_create_element", new JsonObject
        {
            ["level"] = "container", ["name"] = " хранилище ", ["parentId"] = "s1",
        });
        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("id=c2");
    }

    [Fact]
    public async Task CreateElement_УстаревшаяВерсия_КонфликтФайлНеТронут()
    {
        var before = File.ReadAllText(ModelPath);
        var result = await Call("arch_create_element", new JsonObject
        {
            ["level"] = "container", ["name"] = "Очередь", ["parentId"] = "s1", ["version"] = "deadbeef",
        });
        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("Конфликт версий");
        File.ReadAllText(ModelPath).Should().Be(before);
    }

    // --- Создание модели с нуля ---

    // Скелет обёртки: ключи корня и модели плюс виды значений — без содержимого элементов
    private static string Skeleton(JsonObject doc)
    {
        var model = (JsonObject)doc["state"]!["model"]!;
        return string.Join(";", doc.Select(p => p.Key)) + "|"
            + string.Join(";", model.OrderBy(p => p.Key).Select(p => $"{p.Key}:{p.Value!.GetValueKind()}"))
            + "|version=" + doc["version"]!.ToJsonString() + "|viewLevel=" + (string?)model["viewLevel"];
    }

    [Fact]
    public async Task CreateElement_МоделиНет_СистемаЗаводитФайлГенераторскойФормы()
    {
        File.Delete(ModelPath);
        _sessions.Items["chat-1"].PersonaId = "per-1";
        _personas.Items["per-1"] = new Persona { Id = "per-1", Name = "Максим", OwnerId = Owner };

        var result = await Call("arch_create_element", new JsonObject { ["level"] = "system", ["name"] = "Платёжка" });

        result.IsError.Should().BeFalse(result.Text);
        File.Exists(ModelPath).Should().BeTrue();
        var doc = FileModel();
        var sysNode = ById(doc, "systems", CreatedId(result));
        ((string?)sysNode["name"]).Should().Be("Платёжка");
        ((int)sysNode["position"]!["x"]!, (int)sysNode["position"]!["y"]!).Should().Be((0, 0));
        foreach (var array in new[] { "containers", "components", "codeElements" })
            ArrayOf(doc, array).Should().BeEmpty();

        // Форма — ровно та, что пишет «Собрать из кода» (слияние с пустым файлом)
        var generated = ArchitectureModelMerger.Merge(null, new GeneratedModel(
            new GeneratedElement("sys", "Платёжка", null, null, null, []), [], [])).Document;
        Skeleton(doc).Should().Be(Skeleton(generated));
        // Генератор принимает файл как свой: система совпала по имени, дубля нет
        var remerged = ArchitectureModelMerger.Merge(doc, new GeneratedModel(
            new GeneratedElement("sys", "Платёжка", null, null, null, []), [], []));
        ArrayOf(remerged.Document, "systems").Should().ContainSingle();

        // Хранилище раздела видит файл, «кто и когда» записан, ответ несёт его version
        var snapshot = await _store.ReadAsync(_root, CancellationToken.None);
        result.Text.Should().Contain("version=" + snapshot.Version);
        snapshot.Author.UpdatedBy.Should().Be("Максим");
        snapshot.Author.UpdatedAt.Should().NotBeNull();
    }

    [Theory]
    [InlineData("container", "s1")]
    [InlineData("component", null)]
    public async Task CreateElement_МоделиНет_НеСистема_ОтказФайлНеПоявился(string level, string? parent)
    {
        File.Delete(ModelPath);
        var args = new JsonObject { ["level"] = level, ["name"] = "Новый" };
        if (parent is not null) args["parentId"] = parent;

        var result = await Call("arch_create_element", args);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("level=system");
        File.Exists(ModelPath).Should().BeFalse();
    }

    [Fact]
    public async Task CreateElement_МоделиНет_ПроизвольнаяVersion_СозданиеБезКонфликта()
    {
        File.Delete(ModelPath);
        // Агент выдумал version: сверять не с чем, «Конфликт версий… version=» с пустым
        // значением только сбил бы его — система создаётся как без version
        var result = await Call("arch_create_element", new JsonObject
        {
            ["level"] = "system", ["name"] = "Платёжка", ["version"] = "deadbeef",
        });

        result.IsError.Should().BeFalse(result.Text);
        result.Text.Should().NotContain("Конфликт версий");
        File.Exists(ModelPath).Should().BeTrue();
    }

    [Fact]
    public async Task CreateElement_МоделиНет_ВторойПервыйСоздатель_КонфликтНеЗатирает()
    {
        File.Delete(ModelPath);
        // Сосед успел завести модель между нашим чтением (файла нет) и записью: запись с
        // baseVersion = null обязана получить конфликт — моделируем это прямым вызовом
        // хранилища поверх снимка «файла нет», как делает тулсет
        var basis = await _store.ReadAsync(_root, CancellationToken.None);
        basis.Version.Should().BeNull();
        (await Call("arch_create_element", new JsonObject { ["level"] = "system", ["name"] = "Первая" }))
            .IsError.Should().BeFalse();
        var winner = File.ReadAllText(ModelPath);

        var late = ArchitectureModelEditor.NewDocument();
        ArchitectureModelEditor.CreateElement(late, new ArchitectureElementDraft("system", "Вторая"));
        var outcome = await _store.WriteAsync(_root, ArchitectureModelEditor.Serialize(late),
            basis.Version, "Сосед", CancellationToken.None);

        outcome.Saved.Should().BeFalse();
        File.ReadAllText(ModelPath).Should().Be(winner);
    }

    // --- Удаление ---

    [Fact]
    public async Task DeleteElement_КонтейнерСДетьмиБезCascade_ОтказФайлЦел()
    {
        var before = File.ReadAllBytes(ModelPath);
        var result = await Call("arch_delete_element", new JsonObject { ["id"] = "c1" });
        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("cascade=true").And.Contain("[контейнер]").And.Contain("Прямые дети")
            .And.Contain("id=k1");
        File.ReadAllBytes(ModelPath).Should().Equal(before);
    }

    [Fact]
    public async Task DeleteElement_КомпонентСКодом_БезCascadeОтказ_СCascadeУспех()
    {
        var code = await Call("arch_create_element", new JsonObject
        {
            ["level"] = "code", ["name"] = "RunAsync", ["parentId"] = "k1",
        });
        code.IsError.Should().BeFalse(code.Text);
        var before = File.ReadAllBytes(ModelPath);

        var refused = await Call("arch_delete_element", new JsonObject { ["id"] = "k1" });
        refused.IsError.Should().BeTrue();
        refused.Text.Should().Contain("[компонент]").And.Contain("RunAsync");
        File.ReadAllBytes(ModelPath).Should().Equal(before);

        var confirmed = await Call("arch_delete_element", new JsonObject { ["id"] = "k1", ["cascade"] = true });
        confirmed.IsError.Should().BeFalse(confirmed.Text);
        confirmed.Text.Should().Contain("Удалено элементов: 2");
        var doc = FileModel();
        ArrayOf(doc, "components").Should().BeEmpty();
        ArrayOf(doc, "codeElements").Should().BeEmpty();
        ArrayOf(doc, "containers").Should().HaveCount(2);
    }

    [Fact]
    public async Task DeleteElement_ПрямыхДетейНетАГлубокиеЕсть_ПеречисляетНайденных()
    {
        // Система s2 без контейнеров, но компонент ссылается на неё через битый containerId
        var doc = FileModel();
        ArrayOf(doc, "systems").Add(JsonNode.Parse(
            """{"id":"s2","name":"Сирота","type":"system","position":{"x":360,"y":0},"connections":[]}"""));
        ArrayOf(doc, "components").Add(JsonNode.Parse(
            """{"id":"k9","name":"Потеряшка","type":"component","containerId":"ghost","systemId":"s2","connections":[]}"""));
        File.WriteAllText(ModelPath, doc.ToJsonString());

        var result = await Call("arch_delete_element", new JsonObject { ["id"] = "s2" });

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("Найденные вложенные").And.Contain("id=k9").And.NotContain("Прямые дети");
    }

    [Fact]
    public async Task DeleteElement_КаскадДетиСвязиДокументы()
    {
        // Входящая связь к удаляемому от выжившего элемента
        await Call("arch_set_connection", new JsonObject { ["sourceId"] = "c2", ["targetId"] = "k1" });

        var result = await Call("arch_delete_element", new JsonObject { ["id"] = "c1", ["cascade"] = true });

        result.IsError.Should().BeFalse(result.Text);
        // c1 + вложенный k1; исходящая c1→c2 и входящая c2→k1; документ d1
        result.Text.Should().Contain("Удалено элементов: 2").And.Contain("связей: 2").And.Contain("документов: 1");
        var doc = FileModel();
        ArrayOf(doc, "containers").OfType<JsonObject>().Select(o => (string?)o["id"]).Should().Equal("c2");
        ArrayOf(doc, "components").Should().BeEmpty();
        ById(doc, "containers", "c2")["connections"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteElement_СистемаСДетьмиБезCascade_ОтказСПереченнем()
    {
        var before = File.ReadAllText(ModelPath);
        var result = await Call("arch_delete_element", new JsonObject { ["id"] = "s1" });
        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("cascade=true").And.Contain("id=c1").And.Contain("id=c2");
        File.ReadAllText(ModelPath).Should().Be(before);

        var confirmed = await Call("arch_delete_element", new JsonObject { ["id"] = "s1", ["cascade"] = true });
        confirmed.IsError.Should().BeFalse(confirmed.Text);
        confirmed.Text.Should().Contain("Удалено элементов: 4");
        var doc = FileModel();
        foreach (var array in new[] { "systems", "containers", "components" })
            ArrayOf(doc, array).Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteElement_ПустаяСистема_БезCascade()
    {
        var sys = await Call("arch_create_element", new JsonObject { ["level"] = "system", ["name"] = "Пустая" });
        var result = await Call("arch_delete_element", new JsonObject { ["id"] = CreatedId(sys) });
        result.IsError.Should().BeFalse(result.Text);
        ArrayOf(FileModel(), "systems").Should().ContainSingle();
    }

    [Fact]
    public async Task DeleteElement_УстаревшаяВерсия_КонфликтФайлНеТронут()
    {
        var before = File.ReadAllText(ModelPath);
        var result = await Call("arch_delete_element", new JsonObject { ["id"] = "c2", ["version"] = "deadbeef" });
        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("Конфликт версий");
        File.ReadAllText(ModelPath).Should().Be(before);
    }

    [Fact]
    public async Task DeleteElement_ХолстВнутриУдалённого_ВозвратНаУровеньСистем()
    {
        var doc = FileModel();
        doc["state"]!["model"]!["activeSystemId"] = "s1";
        doc["state"]!["model"]!["activeContainerId"] = "c1";
        doc["state"]!["model"]!["viewLevel"] = "component";
        File.WriteAllText(ModelPath, doc.ToJsonString());

        (await Call("arch_delete_element", new JsonObject { ["id"] = "c1", ["cascade"] = true })).IsError.Should().BeFalse();
        var model = (JsonObject)FileModel()["state"]!["model"]!;
        model.ContainsKey("activeContainerId").Should().BeFalse();
        ((string?)model["viewLevel"]).Should().Be("system");
    }

    // --- Фейки швов ---

    private sealed class FakeSessions : IMcpSessionAccessor
    {
        public Dictionary<string, Session> Items { get; } = [];
        public Session? GetOwned(string sessionId, string ownerId) =>
            Items.TryGetValue(sessionId, out var s) && s.OwnerId == ownerId ? s : null;
    }

    private sealed class FakeProjects : IProjectManager
    {
        public Dictionary<string, Project> Items { get; } = [];
        public Project? GetById(string id) => Items.GetValueOrDefault(id);
        public IReadOnlyCollection<Project> GetByOwner(string userId) => Items.Values.Where(p => p.OwnerId == userId).ToList();
        public IReadOnlyCollection<Project> GetAll() => Items.Values.ToList();
        public IReadOnlyCollection<Project> GetByRootPath(string rootPath) => [];
    }

    private sealed class FakePersonas : IPersonaResolver
    {
        public Dictionary<string, Persona> Items { get; } = [];
        public Persona? Get(string id, string userId) =>
            Items.TryGetValue(id, out var p) && p.OwnerId == userId ? p : null;
    }

    private sealed class FakeGate : IPersonaServerToolGate
    {
        public bool Off { get; set; }
        public bool IsServerToolEnabled(string? ownerId, Persona? persona, string toolKey) => !Off;
    }

}
