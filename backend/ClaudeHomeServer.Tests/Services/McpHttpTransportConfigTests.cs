using System.Reflection;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using ClaudeHomeServer.Services.Mcp.Http;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Как виджеты объявляются ходу после переезда на MCP-over-HTTP (ADR-012): узел конфига,
/// адрес, заголовки и отпечаток транспорта в сигнатуре запуска. Конфиг строим настоящим
/// BuildTurnMcpConfig — текстовая проверка исходника тут ничего не доказала бы: ошибка в
/// узле видна только тем, что инструмент МОЛЧА исчезает у модели.
///
/// Сторожа стабильности состава (McpToolsetStabilityTests) это не заменяет и не трогает:
/// там инвариант «состав не зависит от хода», здесь — форма объявления сервера.
/// </summary>
public class McpHttpTransportConfigTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "ccs-mcp-http-config-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<string> _configs = [];

    public McpHttpTransportConfigTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var path in _configs)
            try { File.Delete(path); } catch { /* уборка best-effort */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* уборка best-effort */ }
    }

    // Настоящий BuildTurnMcpConfig сессии: путь temp-конфига + строка сигнатуры серверов
    private (JsonObject Servers, string ServerKeys) BuildConfig(WidgetsMcpContext? widgets = null,
        MemoryMcpContext? memory = null, PersonaAgentsContext? personaAgents = null,
        TasksMcpContext? tasks = null, NotesMcpContext? notes = null, PersonasMcpContext? personas = null,
        WorkspaceMcpContext? workspace = null, NotificationsMcpContext? notifications = null,
        CodeGraphMcpContext? codeGraph = null)
    {
        var context = new LlmSessionContext(
            RootPath: _root,
            OnMessage: _ => Task.CompletedTask,
            RawSystemPrompt: null,
            PermissionRules: null,
            TasksMcp: tasks,
            NotesMcp: notes,
            WidgetsMcp: widgets,
            MemoryMcp: memory,
            PersonasMcp: personas,
            WorkspaceMcp: workspace,
            NotificationsMcp: notifications,
            CodeGraphMcp: codeGraph,
            PersonaAgentsProvider: personaAgents is null ? null : () => personaAgents);
        var session = new ClaudeSession(new Session(), context);

        var method = typeof(ClaudeSession).GetMethod("BuildTurnMcpConfig",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var result = method.Invoke(session, [null, personaAgents])!;
        var type = result.GetType();
        // У ValueTuple элементы — ПОЛЯ, а не свойства
        var path = (string?)type.GetField("Item1")!.GetValue(result);
        var keys = (string)type.GetField("Item2")!.GetValue(result)!;

        // Пустой путь возможен только в stdio-ветке и только вне дерева репозитория
        // (index.js сервера не найден) — это тоже валидный исход: главное, что ход не
        // поехал по негодному http-адресу
        if (string.IsNullOrEmpty(path)) return ([], keys);
        _configs.Add(path);
        var doc = JsonNode.Parse(File.ReadAllText(path))!;
        return ((JsonObject)doc["mcpServers"]!, keys);
    }

    [Fact]
    public void ГодныйАдрес_ВиджетыОбъявленыHttpЭндпоинтомСТокеномВладельца()
    {
        var (servers, keys) = BuildConfig(
            new WidgetsMcpContext("http://localhost:5000", () => "tok-A", UseHttp: true));

        var widgets = servers["widgets"]!.AsObject();
        widgets["type"]!.GetValue<string>().Should().Be("http");
        widgets["url"]!.GetValue<string>().Should().Be("http://localhost:5000/mcp/widgets");
        widgets.ContainsKey("command").Should().BeFalse("процесс node ради виджетов не поднимается");
        // alwaysLoad у http остаётся: без него первый вызов в ходе падает «No such tool
        // available», а в режиме deferred-tools сервер и вовсе прячет инструменты
        widgets["alwaysLoad"]!.GetValue<bool>().Should().BeTrue();

        var headers = widgets["headers"]!.AsObject();
        headers["Authorization"]!.GetValue<string>().Should().Be("Bearer tok-A",
            "сервисный JWT владельца уезжает обычным заголовком — эндпоинт проверяет его сам");
        // При http заголовки шлёт CLI, а не наш код: что не положено статикой в конфиг,
        // до бэкенда не доедет. На этом заголовке держатся [DenyOnDelegatedTurn] и журнал
        // GET /api/mcp/calls — в фазе 2 его потеря вернула бы платный цикл делегирования.
        headers.ContainsKey("X-Caller-Session-Id").Should().BeTrue();
    }

    /// <summary>
    /// Не-http адрес (документированное лечение HTTPS-деплоя через McpTasksApiUrl) — сервер
    /// объявляется по-старому, через node. Именно fail-closed: молча остаться без инструмента
    /// нельзя, а по https CLI до локального Kestrel не доедет.
    /// </summary>
    [Fact]
    public void АдресНеHttp_FailClosed_ВиджетыЕдутПрежнимStdioСервером()
    {
        var (servers, keys) = BuildConfig(
            new WidgetsMcpContext("https://naychenko.me", () => "tok-A", UseHttp: false));

        if (servers["widgets"] is { } node)
        {
            var widgets = node.AsObject();
            widgets.ContainsKey("type").Should().BeFalse("http-узла быть не должно");
            widgets["command"]!.GetValue<string>().Should().Be("node",
                "инструмент обязан остаться у модели: тихая потеря недопустима");
            widgets["args"]![0]!.GetValue<string>().Should().EndWith("index.js");
            keys.Should().Contain("widgets:t:stdio");
        }
        else
        {
            // Сборка вне дерева репозитория: index.js stdio-сервера не найден. Тогда
            // сервера в конфиге нет вовсе — и это всё равно НЕ выход по http.
            keys.Should().NotContain("widgets", "негодный адрес не смеет протащить http-узел");
        }
    }

    /// <summary>
    /// Транспорт входит в сигнатуру запуска: переключение рубильника обязано пробить живой
    /// процесс доживания. Иначе рубильник отката «применится когда-нибудь потом» — то есть
    /// не будет работать ровно тогда, когда он понадобится.
    /// </summary>
    [Fact]
    public void ТранспортВходитВСигнатуруЗапуска()
    {
        var (_, httpKeys) = BuildConfig(
            new WidgetsMcpContext("http://localhost:5000", () => "tok-A", UseHttp: true));
        var (_, otherKeys) = BuildConfig(
            new WidgetsMcpContext("http://localhost:5000", () => "tok-A", UseHttp: false));

        httpKeys.Should().Contain("widgets:t:http");
        // Вне дерева репозитория вторая сигнатура вырождается в пустую — она всё равно другая,
        // то есть переключение рубильника обязано убить живой процесс доживания
        otherKeys.Should().NotBe(httpKeys, "смена транспорта меняет сигнатуру прогона");
    }

    [Theory]
    // Годный адрес: обычный loopback Kestrel и мост песочницы
    [InlineData("http://localhost:5000", true, true)]
    [InlineData("http://host.docker.internal:5000", true, true)]
    [InlineData("http://192.168.7.65", true, true)]
    // https — молчаливая смерть инструмента (ERR_TLS_CERT_ALTNAME_INVALID): fail-closed
    [InlineData("https://naychenko.me", true, false)]
    // Рубильник отката сильнее годного адреса
    [InlineData("http://localhost:5000", false, false)]
    // Мусор в McpTasksApiUrl — тоже не повод ехать по http
    [InlineData("не-адрес", true, false)]
    [InlineData("", true, false)]
    // Пробелы по краям и query/fragment: Uri молча прощает их при разборе, а адрес эндпоинта
    // строится из сырой строки — гейт обязан срезать такие адресы на stdio (находка ревью)
    [InlineData(" http://localhost:5000", true, false)]
    [InlineData("http://localhost:5000 ", true, false)]
    [InlineData("http://localhost:5000?x=1", true, false)]
    [InlineData("http://localhost:5000#frag", true, false)]
    public void ГейтТранспорта_РешаетСхемаАдресаИРубильник(string url, bool enabled, bool expected) =>
        McpHttpTransport.Usable(url, enabled).Should().Be(expected);

    [Fact]
    public void АдресЭндпоинта_КлеитсяБезДвойногоСлеша() =>
        McpHttpTransport.EndpointFor("http://localhost:5000/", "widgets")
            .Should().Be("http://localhost:5000/mcp/widgets");

    /// <summary>
    /// Эндпоинт строится из РАЗОБРАННОГО адреса, а не конкатенацией сырой строки: гейт судит
    /// по нормализованному Uri, и конфиг хода обязан с ним соглашаться (query не прилипает
    /// к маршруту хвостом «?x=1/mcp/widgets»).
    /// </summary>
    [Fact]
    public void АдресЭндпоинта_СтроитсяИзРазобранногоАдреса() =>
        McpHttpTransport.EndpointFor("http://localhost:5000?x=1", "widgets")
            .Should().Be("http://localhost:5000/mcp/widgets");

    // --- memory и pmem_* (фаза 2, волна 1): HTTP-ветка и откат ---

    /// <summary>
    /// Память на годном http-адресе: узел без command (процесса node нет), персона и проект
    /// чата — хвостом URL, токен — из ФАБРИКИ (вызывается на каждый ход, а не захватывается
    /// строкой при создании адаптера — урок фазы 1 про молчаливые 401 у старых чатов).
    /// </summary>
    [Fact]
    public void Memory_ГодныйАдрес_HttpЭндпоинтСТокеномИХвостом()
    {
        var issued = new[] { "tok-first", "tok-second" };
        var index = 0;
        var (servers, keys) = BuildConfig(memory: new MemoryMcpContext(
            "http://localhost:5000", () => issued[Math.Min(index++, 1)], "persona-1", "proj-1",
            DossierToolsEnabled: true, UseHttp: true));

        var memory = servers["memory"]!.AsObject();
        memory["type"]!.GetValue<string>().Should().Be("http");
        memory["url"]!.GetValue<string>()
            .Should().Be("http://localhost:5000/mcp/memory/persona-1/proj-1",
                "персона и проект едут в ПУТИ — конфиг хода наш, тело контролирует модель");
        memory.ContainsKey("command").Should().BeFalse("процесса node ради памяти быть не должно");
        memory["alwaysLoad"]!.GetValue<bool>().Should().BeTrue("модель зовёт память первым действием");
        var headers = memory["headers"]!.AsObject();
        headers["Authorization"]!.GetValue<string>().Should().Be("Bearer tok-first");
        headers.ContainsKey("X-Caller-Session-Id").Should().BeTrue();
        keys.Should().Contain("memory:d1:t:http", "dossier-флаг и транспорт — в сигнатуре запуска");
    }

    /// <summary>Фабрика токена живая: повторная сборка конфига берёт свежий токен.</summary>
    [Fact]
    public void Memory_ТокенФабрикой_АНеЗахваченСтрокой()
    {
        var issued = 0;
        string Factory() => $"tok-{++issued}";
        BuildConfig(memory: new MemoryMcpContext("http://localhost:5000", Factory, "p", null, UseHttp: true));
        BuildConfig(memory: new MemoryMcpContext("http://localhost:5000", Factory, "p", null, UseHttp: true));

        issued.Should().Be(2, "каждая сборка конфига хода зовёт фабрику — иначе старый чат умирает на 401");
    }

    /// <summary>
    /// Откат (не-http адрес, рубильник): memory объявляется прежним stdio-сервером с env —
    /// инструмент у модели остаётся, mcp/memory-server/index.js для того и заморожен.
    /// </summary>
    [Fact]
    public void Memory_Откат_StdioСерверомСEnv()
    {
        var (servers, keys) = BuildConfig(memory: new MemoryMcpContext(
            "https://naychenko.me", () => "tok", "persona-1", "proj-1", UseHttp: false));

        if (servers["memory"] is { } node)
        {
            var memory = node.AsObject();
            memory.ContainsKey("type").Should().BeFalse();
            memory["command"]!.GetValue<string>().Should().Be("node");
            memory["args"]![0]!.GetValue<string>().Should().EndWith("index.js");
            var env = memory["env"]!.AsObject();
            env["MEMORY_PERSONA_ID"]!.GetValue<string>().Should().Be("persona-1");
            env["MEMORY_PROJECT_ID"]!.GetValue<string>().Should().Be("proj-1");
            env["MEMORY_API_TOKEN"]!.GetValue<string>().Should().Be("tok");
            keys.Should().Contain("memory:d0:t:stdio");
        }
        else
        {
            // Сборка вне дерева репозитория: index.js не найден — но http-узла точно нет
            keys.Should().NotContain("memory:t:http");
        }
    }

    /// <summary>
    /// ПРИЁМКА-ЗАМЕР в терминах конфига хода: сессия с N консультантами + персона чата на
    /// http-транспорте — ни одного stdio-узла памяти (command: node). При stdio каждый
    /// консультант и сам сервер поднимали по процессу (N+1); здесь — ноль, «−N−1».
    /// </summary>
    [Fact]
    public void Pmem_КонсультантыНаХttp_НиОдногоПроцессаNode()
    {
        var consultants = new PersonaAgentsContext([],
        [
            new ConsultantMemoryServer("pmem_alex", "http://localhost:5000", () => "tok", "p-alex", "proj-1", UseHttp: true),
            new ConsultantMemoryServer("pmem_kira", "http://localhost:5000", () => "tok", "p-kira", "proj-1", UseHttp: true),
        ], ["alex", "kira"]);
        var (servers, keys) = BuildConfig(
            memory: new MemoryMcpContext("http://localhost:5000", () => "tok", "p-chat", "proj-1", UseHttp: true),
            personaAgents: consultants);

        servers["pmem_alex"]!.AsObject()["url"]!.GetValue<string>()
            .Should().Be("http://localhost:5000/mcp/memory/p-alex/proj-1",
                "каждый pmem — свой URL с персоной консультанта, ключ прежний (frontmatter агента)");
        servers["pmem_kira"]!.AsObject()["url"]!.GetValue<string>()
            .Should().Be("http://localhost:5000/mcp/memory/p-kira/proj-1");
        // Замер «−N−1»: ни у одного узла памяти нет запуска процесса
        foreach (var key in new[] { "memory", "pmem_alex", "pmem_kira" })
        {
            var node = servers[key]!.AsObject();
            node.ContainsKey("command").Should().BeFalse(
                $"узел {key} обязан быть http — процессов node больше нет");
        }
        // alwaysLoad: у сервера чата стоит (модель зовёт память первым действием), у pmem —
        // нет и на stdio: с ним главная сессия видела бы инструменты чужой памяти
        servers["memory"]!.AsObject()["alwaysLoad"]!.GetValue<bool>().Should().BeTrue();
        servers["pmem_alex"]!.AsObject().ContainsKey("alwaysLoad").Should().BeFalse();
        servers["pmem_kira"]!.AsObject().ContainsKey("alwaysLoad").Should().BeFalse();
        keys.Should().Contain("pmem_alex:t:http").And.Contain("pmem_kira:t:http");
    }

    /// <summary>Смешанный откат: любой stdio-pmem получает node-файл и env, http-сосед — нет.</summary>
    [Fact]
    public void Pmem_ОткатПоодиночке_StdioСерверомСEnv()
    {
        var consultants = new PersonaAgentsContext([],
        [
            new ConsultantMemoryServer("pmem_http", "http://localhost:5000", () => "tok", "p-1", null, UseHttp: true),
            new ConsultantMemoryServer("pmem_stdio", "http://localhost:5000", () => "tok", "p-2", null, UseHttp: false),
        ], ["http", "stdio"]);
        var (servers, keys) = BuildConfig(personaAgents: consultants);

        servers["pmem_http"]!.AsObject().ContainsKey("command").Should().BeFalse();
        if (servers["pmem_stdio"] is { } stdio)
        {
            stdio["command"]!.GetValue<string>().Should().Be("node");
            stdio["env"]!.AsObject()["MEMORY_PERSONA_ID"]!.GetValue<string>().Should().Be("p-2");
            keys.Should().Contain("pmem_stdio:t:stdio");
        }
        keys.Should().Contain("pmem_http:t:http");
    }

    // --- tasks/notes/personas (фаза 2, волна 2): HTTP-ветка и откат ---

    /// <summary>
    /// Три сервера на годном http-адресе: узлы без command, сессия-вызыватель — хвостом URL
    /// (по ней тулсет резолвит проект/персону/привязки), токен — из ФАБРИКИ на каждую сборку
    /// (дополнение волны 2: у tasks/notes/personas до этого был захваченный строкой JWT —
    /// у долгоживущего чата он истекал бы, как у widgets до 1.1).
    /// </summary>
    [Fact]
    public void Волна2_ГодныйАдрес_ТриСервераНаНttpСХвостомСессии()
    {
        var (servers, keys) = BuildConfig(
            tasks: new TasksMcpContext("http://localhost:5000", () => "tok-t", "proj-1", UseHttp: true),
            notes: new NotesMcpContext("http://localhost:5000", () => "tok-n", "proj-1", UseHttp: true),
            personas: new PersonasMcpContext("http://localhost:5000", () => "tok-p", "proj-1", UseHttp: true));

        foreach (var key in new[] { "tasks", "notes", "personas" })
        {
            var node = servers[key]!.AsObject();
            node["type"]!.GetValue<string>().Should().Be("http");
            // Хвост — id сессии BuildTurnMcpConfig (Info.Id), он не пуст и это не имя сервера
            node["url"]!.GetValue<string>().Should()
                .Match($"http://localhost:5000/mcp/{key}/*",
                    "сессия-вызыватель едет в ПУТИ — по ней тулсет резолвит контекст чата")
                .And.NotBe($"http://localhost:5000/mcp/{key}/",
                    "хвост обязан быть непустым id сессии");
            node.ContainsKey("command").Should().BeFalse($"процессов node ради {key} больше нет");
            node["alwaysLoad"]!.GetValue<bool>().Should().BeTrue();
            var headers = node["headers"]!.AsObject();
            headers["Authorization"]!.GetValue<string>().Should().Match("Bearer tok-*");
            headers.ContainsKey("X-Caller-Session-Id").Should().BeTrue();
            // Транспорт — в сигнатуре запуска; у notes/personas перед ним идёт отпечаток
            // состава (модули, скоупы), поэтому сверяем сегмент ключа целиком
            keys.Should().MatchRegex($"{key}:[^,]*t:http", "транспорт — в сигнатуре запуска");
        }
    }

    /// <summary>Фабрика токена живая у всех трёх: повторная сборка конфига зовёт фабрику.</summary>
    [Fact]
    public void Волна2_ТокенФабрикой_АНеЗахваченСтрокой()
    {
        var issued = 0;
        string Factory() => $"tok-{++issued}";
        var tasks = new TasksMcpContext("http://localhost:5000", Factory, null, UseHttp: true);
        var notes = new NotesMcpContext("http://localhost:5000", Factory, null, UseHttp: true);
        var personas = new PersonasMcpContext("http://localhost:5000", Factory, null, UseHttp: true);
        BuildConfig(tasks: tasks, notes: notes, personas: personas);
        BuildConfig(tasks: tasks, notes: notes, personas: personas);

        issued.Should().Be(6, "каждая сборка зовёт все три фабрики — старый чат не умирает на 401");
    }

    /// <summary>
    /// Откат (рубильник Mcp:HttpTransport, не-http адрес): все три объявляются прежними
    /// stdio-серверами с env (включая аварийные рубильники TASKS_EXECUTE/PERSONAS_WRITE —
    /// в stdio-ветке они остались для прямых запусков), инструмент у модели остаётся.
    /// </summary>
    [Fact]
    public void Волна2_Откат_ТриСервераНаStdioСEnv()
    {
        var (servers, keys) = BuildConfig(
            tasks: new TasksMcpContext("https://naychenko.me", () => "tok", "proj-1", UseHttp: false),
            notes: new NotesMcpContext("https://naychenko.me", () => "tok", "proj-1", UseHttp: false),
            personas: new PersonasMcpContext("https://naychenko.me", () => "tok", "proj-1", UseHttp: false));

        if (servers["tasks"] is { } tasksNode)
        {
            var env = tasksNode["env"]!.AsObject();
            env["TASKS_PROJECT_ID"]!.GetValue<string>().Should().Be("proj-1");
            env["TASKS_API_TOKEN"]!.GetValue<string>().Should().Be("tok");
            env["TASKS_EXECUTE"]!.GetValue<string>().Should().Be("1");
            keys.Should().Contain("tasks:t:stdio");
        }
        else keys.Should().NotContain("tasks:t:http");

        if (servers["notes"] is { } notesNode)
        {
            var env = notesNode["env"]!.AsObject();
            env["NOTES_PROJECT_ID"]!.GetValue<string>().Should().Be("proj-1");
            env["NOTES_API_TOKEN"]!.GetValue<string>().Should().Be("tok");
            keys.Should().Contain("notes:t:stdio");
        }
        else keys.Should().NotContain("notes:t:http");

        if (servers["personas"] is { } personasNode)
        {
            var env = personasNode["env"]!.AsObject();
            env["PERSONAS_PROJECT_ID"]!.GetValue<string>().Should().Be("proj-1");
            env["PERSONAS_API_TOKEN"]!.GetValue<string>().Should().Be("tok");
            env["PERSONAS_WRITE"]!.GetValue<string>().Should().Be("1");
            keys.Should().Contain("personas:t:stdio");
        }
        else keys.Should().NotContain("personas:t:http");
    }

    /// <summary>
    /// Волна 3 (wsp/notifications/codegraph): три сервера объявляются http-эндпоинтом с
    /// хвостом-сессией, без единого процесса node, а транспорт входит в сигнатуру запуска.
    /// </summary>
    [Fact]
    public void Волна3_ГодныйАдрес_ТриСервераНаНttpСХвостомСессии()
    {
        var (servers, keys) = BuildConfig(
            workspace: new WorkspaceMcpContext("http://localhost:5000", () => "tok-w", "proj-1",
                ["projects", "files", "knowledge", "search"], UseHttp: true),
            notifications: new NotificationsMcpContext("http://localhost:5000", () => "tok-n",
                "persona-1", UseHttp: true),
            codeGraph: new CodeGraphMcpContext("http://localhost:5000", () => "tok-c", "proj-1",
                UseHttp: true));

        foreach (var key in new[] { "wsp", "notifications", "codegraph" })
        {
            var node = servers[key]!.AsObject();
            node["type"]!.GetValue<string>().Should().Be("http");
            node["url"]!.GetValue<string>().Should()
                .Match($"http://localhost:5000/mcp/{key}/*",
                    "сессия-вызыватель едет в ПУТИ — по ней тулсет резолвит контекст чата")
                .And.NotBe($"http://localhost:5000/mcp/{key}/",
                    "хвост обязан быть непустым id сессии");
            node.ContainsKey("command").Should().BeFalse($"процессов node ради {key} больше нет");
            node["alwaysLoad"]!.GetValue<bool>().Should().BeTrue();
            var headers = node["headers"]!.AsObject();
            headers["Authorization"]!.GetValue<string>().Should().Match("Bearer tok-*");
            headers.ContainsKey("X-Caller-Session-Id").Should().BeTrue();
            // Отпечаток wsp сам содержит запятые (список секций) — сегмент ключа целиком
            // регуляркой без запятых не выделить, поэтому ищем пару «ключ … t:http»
            keys.Should().MatchRegex($"{key}:.*t:http", "транспорт — в сигнатуре запуска");
        }

        // Рабочее дерево (worktree) в адрес НЕ попадает: тулсет резолвит его живьём из
        // сессии, иначе смена дерева меняла бы сигнатуру и перезапускала CLI (ADR-012)
        servers["codegraph"]!["url"]!.GetValue<string>()
            .Should().NotContain("rootPath").And.NotContain("worktree");
    }

    /// <summary>Фабрика токена живая у всех трёх серверов волны 3.</summary>
    [Fact]
    public void Волна3_ТокенФабрикой_АНеЗахваченСтрокой()
    {
        var issued = 0;
        string Factory() => $"tok-{++issued}";
        var workspace = new WorkspaceMcpContext("http://localhost:5000", Factory, null,
            ["projects", "files", "search"], UseHttp: true);
        var notifications = new NotificationsMcpContext("http://localhost:5000", Factory, null, UseHttp: true);
        var codeGraph = new CodeGraphMcpContext("http://localhost:5000", Factory, "proj-1", UseHttp: true);
        BuildConfig(workspace: workspace, notifications: notifications, codeGraph: codeGraph);
        BuildConfig(workspace: workspace, notifications: notifications, codeGraph: codeGraph);

        issued.Should().Be(6, "каждая сборка зовёт все три фабрики — старый чат не умирает на 401");
    }

    /// <summary>
    /// Откат (рубильник Mcp:HttpTransport, не-http адрес): все три волны 3 объявляются
    /// прежними stdio-серверами с env — инструменты у модели остаются.
    /// </summary>
    [Fact]
    public void Волна3_Откат_ТриСервераНаStdioСEnv()
    {
        var (servers, keys) = BuildConfig(
            workspace: new WorkspaceMcpContext("https://naychenko.me", () => "tok", "proj-1",
                ["projects", "files", "knowledge", "search"], UseHttp: false),
            notifications: new NotificationsMcpContext("https://naychenko.me", () => "tok",
                "persona-1", UseHttp: false),
            codeGraph: new CodeGraphMcpContext("https://naychenko.me", () => "tok", "proj-1",
                RootPath: "C:/worktrees/chat-1", UseHttp: false));

        if (servers["wsp"] is { } wspNode)
        {
            var env = wspNode["env"]!.AsObject();
            env["WORKSPACE_PROJECT_ID"]!.GetValue<string>().Should().Be("proj-1");
            env["WORKSPACE_API_TOKEN"]!.GetValue<string>().Should().Be("tok");
            env["WORKSPACE_WRITE"]!.GetValue<string>().Should().Be("1");
            env["WORKSPACE_SECTIONS"]!.GetValue<string>().Should().Be("projects,files,knowledge,search");
            keys.Should().Contain("t:stdio");
        }
        else keys.Should().NotContain("wsp:w1:projects,files,knowledge,search:t:http");

        if (servers["notifications"] is { } ntfNode)
        {
            var env = ntfNode["env"]!.AsObject();
            env["NOTIFICATIONS_API_TOKEN"]!.GetValue<string>().Should().Be("tok");
            env["NOTIFICATIONS_SELF_PERSONA_ID"]!.GetValue<string>().Should().Be("persona-1");
            keys.Should().Contain("notifications:t:stdio");
        }
        else keys.Should().NotContain("notifications:t:http");

        if (servers["codegraph"] is { } cgNode)
        {
            var env = cgNode["env"]!.AsObject();
            env["CODEGRAPH_PROJECT_ID"]!.GetValue<string>().Should().Be("proj-1");
            env["CODEGRAPH_API_TOKEN"]!.GetValue<string>().Should().Be("tok");
            // На stdio рабочее дерево по-прежнему едет env — там его резолвить некому
            env["CODEGRAPH_ROOT_PATH"]!.GetValue<string>().Should().Be("C:/worktrees/chat-1");
            keys.Should().Contain("codegraph:t:stdio");
        }
        else keys.Should().NotContain("codegraph:t:http");
    }

    /// <summary>
    /// ЗАМЕР процессов node (приёмка волны 3): при http-транспорте полный набор продуктовых
    /// серверов всех трёх волн не поднимает НИ ОДНОГО процесса — в конфиге хода нет ни одного
    /// узла с «command». Это и есть цель ADR-012, зафиксированная тестом, а не разовым
    /// наблюдением: узел с command вернулся бы молча.
    /// </summary>
    [Fact]
    public void Замер_ПолныйНаборНаHttp_НиОдногоПроцессаNode()
    {
        var (servers, _) = BuildConfig(
            widgets: new WidgetsMcpContext("http://localhost:5000", () => "tok", UseHttp: true),
            memory: new MemoryMcpContext("http://localhost:5000", () => "tok", "persona-1",
                UseHttp: true),
            tasks: new TasksMcpContext("http://localhost:5000", () => "tok", "proj-1", UseHttp: true),
            notes: new NotesMcpContext("http://localhost:5000", () => "tok", "proj-1", UseHttp: true),
            personas: new PersonasMcpContext("http://localhost:5000", () => "tok", "proj-1", UseHttp: true),
            workspace: new WorkspaceMcpContext("http://localhost:5000", () => "tok", "proj-1",
                ["projects", "files", "knowledge", "search", "chats", "git", "knowledge_bases"],
                UseHttp: true),
            notifications: new NotificationsMcpContext("http://localhost:5000", () => "tok",
                "persona-1", UseHttp: true),
            codeGraph: new CodeGraphMcpContext("http://localhost:5000", () => "tok", "proj-1",
                UseHttp: true));

        var productServers = new[]
        {
            "widgets", "memory", "tasks", "notes", "personas", "wsp", "notifications", "codegraph",
        };
        foreach (var key in productServers)
        {
            servers.ContainsKey(key).Should().BeTrue($"{key} обязан быть объявлен ходу");
            servers[key]!.AsObject().ContainsKey("command")
                .Should().BeFalse($"{key} на http не поднимает процесс node");
            servers[key]!["type"]!.GetValue<string>().Should().Be("http");
        }

        servers.Count(kv => kv.Value?.AsObject().ContainsKey("command") == true)
            .Should().Be(0, "продуктовых node-процессов на ход не остаётся вовсе");
    }
}
