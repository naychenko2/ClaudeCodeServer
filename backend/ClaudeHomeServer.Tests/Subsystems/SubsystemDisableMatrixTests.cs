using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Subsystems;

/// <summary>
/// Сквозной сторож «каждую подсистему можно выключить» (S2a-1, ADR-014). Для КАЖДОГО ключа
/// подсистемы поднимается хост с <c>Subsystems:{Key}:Enabled=false</c> и проверяется:
/// 1. хост стартует без исключения (первый запрос <c>GET /api/auth/me</c> под админом — 200);
/// 2. <c>GET /api/admin/subsystems</c> показывает этот ключ <c>active=false</c>;
/// 3. маршруты этой подсистемы (её явная привязка или её собственная сборка) на выключенном
///    хосте отдают 404/405/503 — не 500; <c>POST /mcp/{имя тулсета}</c> — не 500;
/// 4. соседи живы: <c>GET /api/projects</c> и <c>GET /api/tasks</c> — 200.
///
/// ═══ Как берётся «состав строки» (и где её границы) ═══
///
/// Маршруты вертикали не переписываются руками: они снимаются с ВКЛЮЧЁННОГО хоста через
/// <see cref="EndpointDataSource"/> и относятся к ключу тем же механизмом, которым пользуется
/// учёт обращений (<see cref="SubsystemUsageAttribution.KeyOf" />): явный <c>[SubsystemUsage(key)]</c>
/// на контроллере или тулсете — так привязаны вертикальные ручки, живущие в сборке Main
/// (VideoController.cs:19, TtsController.cs:25, TasksController.cs:30, …); для обработчика в сборке
/// самой вертикали — карта «сборка → ключ». Параметр маршрута подставляется фиктивным значением
/// по его inline-конверсии (в решении встречается только <c>:int</c> — GitController.cs:221,
/// ImageEditorController.cs:77; таблица ниже покрывает и остальные стандартные конверсии, чтобы
/// не разъехаться с первым <c>:guid</c>), и тем же способом подставляются обязательные
/// <c>[FromQuery]</c>-параметры метода: без них запрос падает в 400 привязкой модели ДО любого
/// гейта вертикали, и строка меряла бы форму запроса, а не отказ выключенной вертикали (первый
/// прогон S2a-2 дал ровно это на api/video/channels и api/video/feed).
///
/// Граница, которую надо знать, читая красный/зелёный: обработчик без атрибута и не из сборки
/// своей вертикали — ядро, и строка матрицы его не мерит. Из отключаемых ключей так сейчас
/// стоят <c>project-icons</c> (его сервис вызывает неатрибутованный ProjectsController — нарушение
/// видно только пунктом 4), <c>changelog</c> и <c>project-services</c> (их ручки не атрибутированы)
/// и <c>llm</c> (его услуги вызывают неатрибутованные Main-контроллеры UsageController.cs:12
/// (UsageService), ProvidersController.cs:10 (IProviderBalanceService), ModelsController.cs:15
/// (LlmProviderRegistry) — все они резолвят сервисы гейтнутой сборки ClaudeHomeServer.Llm,
/// LlmSubsystem.cs:93-95). Пустого состава её маршрутов после починки контроллеров нет: ядро
/// сессий требует сервисы этой сборки обязательными (SessionManager.cs:739-743 —
/// ILlmSessionAdapterFactory, UsageService, LlmProviderRegistry), поэтому строка <c>llm</c>
/// переживает S2a-2 и решается не опционализацией ручек, а ядром/CanDisable в S2b-1 (там же —
/// явная привязка <c>[SubsystemUsage("llm")]</c>).
/// Это не «матрица молчит»: это finding S2a-1, и его закрывает S2b-1.
///
/// ═══ Почему именно старт с выключенным ключом, а не горячее выключение ═══
///
/// Гейт читается один раз при сборке контейнера (<c>AddSubsystems</c>, <c>ModuleLoader.TryLoadOne</c>),
/// поэтому наблюдаемое поведение — свойство стартовавшего хоста. Матрица мерит старт.
///
/// ═══ Настройка доезжает до хоста через UseSetting ═══
///
/// Механика и почему это не гонка с параллельными хостами — разобраны в шапке
/// <see cref="NotesDisabledTests"/>; здесь тот же путь (<c>IWebHostBuilder.UseSetting</c> →
/// командная строка в <c>WebApplication.CreateBuilder</c> → успевает к <c>AddSubsystems</c>).
/// Динамические модули мерятся ТЕМ ЖЕ гейтом, без рубильника <c>DynamicModules</c>:
/// ModuleLoader проверяет <c>Subsystems:{Key}:Enabled</c> до загрузки сборки
/// (ModuleLoader.cs:64), поэтому «выключено подсистемой» и «не загружено вовсе» для него одно.
///
/// ═══ Найденные нарушения не чинятся здесь ═══
///
/// Нарушение документируется строкой <see cref="KnownViolations"/> (ключ → что падает, с
/// файл:строка) и выносится из прогона пропущенным тестом с той же причиной. Матрица
/// остаётся зелёной и полной, а перечень нарушений — входом S2a-2 и S2b-1.
/// </summary>
[Trait("Category", "Host")]
public class SubsystemDisableMatrixTests : IClassFixture<SubsystemDisableMatrixTests.Hosts>
{
    /// <summary>
    /// Статический перечень ключей матрицы: строки <c>AddSubsystems</c> в Program.cs (порядок
    /// регистрации) плюс динамические модули реестра <c>DynamicModules</c> (index 0 — тестовая
    /// заглушка <c>__stub</c>, включённая в appsettings.Testing.json ради StubModuleEndpointTests).
    /// Сторож <see cref="Матрица_Полна_СписокРавенКлючамСтораВключённогоХоста"/> сверяет этот
    /// список с реальным составом <see cref="SubsystemStateStore"/> включённого хоста: новая
    /// подсистема без строки здесь — красный тест.
    /// </summary>
    internal static readonly string[] MatrixKeys = [
        // Статические вертикали — строки Program.cs:990..1062.
        "git", "code-graph", "dossiers", "llm", "memory", "knowledge", "video", "yandex",
        "reader", "images", "tts", "deploy", "backgrounds", "project-icons", "project-services",
        "test-runs", "changelog", "tasks", "skills", "plugins",
        // Динамические модули — DynamicModules (index 0 — __stub).
        "__stub", "notes", "spend", "imageeditor", "architecture", "audioeditor", "videoeditor",
    ];

    /// <summary>
    /// Известные нарушения матрицы: ключ → что падает (файл:строка). Строка выносится из прогона
    /// пропущенным тестом с этим текстом.
    ///
    /// Общая форма всех строк: НЕ выключенная вертикаль, а её потребитель вне вертикали —
    /// обязательная зависимость от сервиса выключенной вертикали в конструкторе Main-сервиса,
    /// Main-контроллера или в явном <c>GetRequiredService</c> на пути старта. Образец обратной
    /// формы — строка <c>plugins</c>: её контроллеры держат те же зависимости опциональными и
    /// отвечают 503 (ClaudeHomeServer.Plugins/Controllers/PluginsController.cs:22-38).
    ///
    /// S2a-2 закрыл пять ключей (tts, yandex, video, backgrounds, deploy): их контроллеры в Main
    /// держат сервисы вертикалей опциональными (= null) и отвечают 503 с причиной
    /// (TtsController.cs:61, VideoController.cs:42, YandexController.cs:45,
    /// ProjectBackgroundsController.cs:40, DeployController.cs:37, TrayDeployController.cs:37).
    /// Убраны и четыре обязательные зависимости вне ядра — без них хост не поднимался бы вовсе,
    /// даже после починки контроллеров: отказ текстом там, где у вызова нет HTTP-кода
    /// (WorkspaceToolset.cs:1757, WebSearchToolset.cs:120, PersonasCrudService.cs:549 и :640),
    /// и GetService на пути остановки хоста (Program.cs:2338). Гейт <c>reader</c> починен тем же
    /// приёмом (ReaderController.cs:36) — строка осталась из-за формы пробы, её причина в самой
    /// строке.
    ///
    /// Одна починка на краю потребителя осталась — <c>images</c>: её три ручки перечислены в
    /// строке. Прочие строки — «ядро/сквозная»: потребитель является ядром сессий и проектов
    /// (SessionManager.cs:739-743 для llm, ProjectsController.cs:25 для
    /// git/tasks/knowledge/dossiers/memory/project-icons, SessionHub.cs:26 → FileWatcherService.cs:103
    /// для code-graph, SessionManager.cs:745 → PersonaBindingsService.cs:139 для skills) или швом
    /// контекста хода (PromptSeamAdapters.cs:18, ADR-023). Их решает S2b-1: CanDisable или
    /// опционализация названного шва.
    /// </summary>
    internal static readonly Dictionary<string, string> KnownViolations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["code-graph"] = "ядро/сквозная: ядро чата держит цепочку обязательных зависимостей — "
            + "SessionHub (backend/ClaudeHomeServer/Hubs/SessionHub.cs:26) требует FileWatcherService, "
            + "а он (backend/ClaudeHomeServer/Services/FileWatcherService.cs:103) — CodeGraphService. "
            + "Первый запрос к хосту не отвечен. CanDisable или опционализацию этого шва решает S2b-1.",
        ["dossiers"] = "ядро/сквозная: ядро проектов (backend/ClaudeHomeServer/Controllers/ProjectsController.cs:25) "
            + "требует DossierStore обязательным параметром; та же зависимость — у MemoryToolset "
            + "(backend/ClaudeHomeServer/Services/Mcp/Http/MemoryToolset.cs:45). Первый запрос к хосту не отвечен. "
            + "CanDisable или опционализацию этого шва решает S2b-1.",
        ["git"] = "ядро/сквозная: ReviewTreePool (backend/ClaudeHomeServer/Services/ReviewTreePool.cs:49) "
            + "и ядро проектов (Controllers/ProjectsController.cs:25 — GitService, GitServerService) требуют "
            + "GitService обязательным параметром; первый запрос к хосту не отвечен. Решает S2b-1 (CanDisable).",
        ["knowledge"] = "ядро/сквозная: ядро проектов (backend/ClaudeHomeServer/Controllers/ProjectsController.cs:25) "
            + "требует WorkspaceKnowledgeStore и KnowledgeService; та же зависимость — у PersonaBindingsService "
            + "(backend/ClaudeHomeServer/Services/PersonaBindingsService.cs:138), который сам обязателен ядру "
            + "сессий (backend/ClaudeHomeServer/Services/SessionManager.cs:745). Первый запрос к хосту не отвечен. "
            + "CanDisable или опционализацию этого шва решает S2b-1.",
        ["llm"] = "ядро/сквозная: ядро сессий (backend/ClaudeHomeServer/Services/SessionManager.cs:739-743) требует "
            + "ILlmSessionAdapterFactory, UsageService и LlmProviderRegistry гейтнутой сборки ClaudeHomeServer.Llm "
            + "(backend/ClaudeHomeServer.Llm/LlmSubsystem.cs:93-95) обязательными параметрами. Соседняя дыра той же "
            + "строки: ручки вертикали неатрибутованы — UsageController (backend/ClaudeHomeServer/Controllers/UsageController.cs:12), "
            + "ProvidersController (backend/ClaudeHomeServer/Controllers/ProvidersController.cs:10), ModelsController "
            + "(backend/ClaudeHomeServer/Controllers/ModelsController.cs:15). Первый запрос к хосту не отвечен. "
            + "Решает S2b-1: CanDisable и привязка [SubsystemUsage(\"llm\")].",
        ["memory"] = "ядро/сквозная: шов контекста хода PersonaRecallSourceAdapter "
            + "(backend/ClaudeHomeServer/Services/Composition/PromptSeamAdapters.cs:18, ADR-023) и ядро проектов "
            + "(backend/ClaudeHomeServer/Controllers/ProjectsController.cs:25) требуют PersonaMemoryService "
            + "обязательным параметром. Первый запрос к хосту не отвечен. CanDisable или опционализацию "
            + "этого шва решает S2b-1.",
        ["images"] = "маршруты вертикали дают 500: ImageGenerationController "
            + "(backend/ClaudeHomeServer/Controllers/ImageGenerationController.cs:20) требует ImageGenerationService "
            + "обязательным параметром → GET и PUT /api/image-generation. Тот же профиль у OnboardingController "
            + "(backend/ClaudeHomeServer/Controllers/OnboardingController.cs:31) и PersonasController "
            + "(backend/ClaudeHomeServer/Controllers/PersonasController.cs:39) — генерация аватара и очередь догона. "
            + "Починка на краю этих потребителей — следующий прогон; сервис-потребитель PersonasCrudService "
            + "закрыт в S2a-2 (PersonasCrudService.cs:549, :640).",
        ["project-icons"] = "ядро/сквозная: хост стартует и ключ виден выключенным, но соседняя ручка мертва: "
            + "ядро проектов (backend/ClaudeHomeServer/Controllers/ProjectsController.cs:25) требует "
            + "ProjectIconGlyphService обязательным параметром → GET /api/projects 500. Единственная строка, "
            + "где рвётся не старт, а сосед. Решает S2b-1 (CanDisable).",
        ["project-services"] = "ядро/сквозная: ядро чата (backend/ClaudeHomeServer/Hubs/SessionHub.cs:27) требует "
            + "DevServerService обязательным параметром — первый запрос к хосту не отвечен. Обязательный резолв "
            + "на пути остановки хоста починен в S2a-2 (Program.cs:2338 — GetService, как соседние строки того "
            + "же блока). Решает S2b-1 (CanDisable).",
        ["reader"] = "Гейт вертикали починен в S2a-2 (ReaderController.cs:36): GET /api/reader/image отвечает 503. "
            + "Мешает форма пробы: POST /api/reader/read и POST /api/reader/embed-check дают 400 модельной "
            + "валидацией ДО экшена — тело пробы «{}» не проходит обязательный Url записи ReadRequest "
            + "(backend/ClaudeHomeServer/Controllers/ReaderController.cs:113). Что выбирать — состав тела пробы "
            + "по типу параметра экшена или nullable Url в DTO (сейчас пустой Url отвечает 200 invalid-url, "
            + "отсутствующий — 400) — вне этой строки.",
        ["skills"] = "ядро/сквозная: PersonaBindingsService (backend/ClaudeHomeServer/Services/PersonaBindingsService.cs:139), "
            + "обязательный ядру сессий (backend/ClaudeHomeServer/Services/SessionManager.cs:745), требует SkillsService "
            + "обязательным параметром. Первый запрос к хосту не отвечен. CanDisable или опционализацию этого шва "
            + "решает S2b-1.",
        ["tasks"] = "ядро/сквозная: ядро проектов (backend/ClaudeHomeServer/Controllers/ProjectsController.cs:25) "
            + "требует TaskManager обязательным параметром; та же зависимость — у TeamWaveService "
            + "(backend/ClaudeHomeServer/Services/Team/TeamWaveService.cs:89). Первый запрос к хосту не отвечен. "
            + "Решает S2b-1 (CanDisable).",
    };

    // Соседи, которые обязаны остаться живыми при любом выключенном ключе (пункт 4).
    private static readonly string[] NeighborProbes = ["/api/projects", "/api/tasks"];

    private readonly Hosts _hosts;

    public SubsystemDisableMatrixTests(Hosts hosts) => _hosts = hosts;

    // Включённый хост класса: с него снимаются маршруты вертикалей, состав стора и MCP-тулсеты.
    private TestWebApplicationFactory Enabled => _hosts.Enabled;

    /// <summary>
    /// Хосты на КЛАСС: один включённый (с него снимаются маршруты и состав стора) и ленивый
    /// пул выключенных — по одному на ключ строки. Поднимаются при первом обращении,
    /// гасятся вместе с фикстурой; состояние хоста тесты не меняют.
    /// </summary>
    public sealed class Hosts : IDisposable
    {
        public EnabledFactory Enabled { get; } = new();

        private readonly Dictionary<string, DisabledFactory> _disabled = new(StringComparer.OrdinalIgnoreCase);

        public DisabledFactory DisabledFor(string key)
        {
            lock (_disabled) return _disabled.TryGetValue(key, out var f) ? f : _disabled[key] = new(key);
        }

        public void Dispose()
        {
            Enabled.Dispose();
            lock (_disabled) foreach (var f in _disabled.Values) f.Dispose();
        }
    }

    public sealed class EnabledFactory : TestWebApplicationFactory;

    public sealed class DisabledFactory(string key) : TestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            // Только гейт подсистемы — без DynamicModules-рубильника: для динамического модуля
            // этого хватает (ModuleLoader.cs:64 проверяет SubsystemGate до загрузки сборки).
            builder.UseSetting($"Subsystems:{key}:Enabled", "false");
        }
    }

    // ─── Сторож полноты матрицы ────────────────────────────────────────────────────

    [Fact]
    public async Task Матрица_Полна_СписокРавенКлючамСтораВключённогоХоста()
    {
        var live = await LiveKeysAsync(Enabled);

        live.Should().NotBeEmpty("включённый хост обязан отдавать непустой список подсистем");
        MatrixKeys.Should().BeEquivalentTo(live,
            "перечень матрицы обязан ровно совпадать с ключами SubsystemStateStore включённого "
            + "хоста: новая подсистема без строки в матрице — красный здесь, а не тихая дыра в гейте");
    }

    /// <summary>
    /// Структура словаря нарушений: ключ — из перечня матрицы (не из воздуха), причина — с файлом
    /// и строкой того, что падает. Пустая или безадресная причина — красный здесь, а не тихий
    /// Skip в прогоне.
    /// </summary>
    [Fact]
    public void Известные_наруения_полны_и_ссылаются_на_место()
    {
        KnownViolations.Keys.Should().BeSubsetOf(MatrixKeys,
            "нарушение может быть документировано только для ключа матрицы");
        var withAddress = new Regex(@"[\w.\-/]+\.cs:\d+");
        KnownViolations.Where(kv => !withAddress.IsMatch(kv.Value)).Select(kv => kv.Key)
            .Should().BeEmpty("причина пропущенной строки обязана называть файл:строку того, что падает");
    }

    private static async Task<List<string>> LiveKeysAsync(TestWebApplicationFactory factory)
    {
        var snapshot = await factory.CreateAuthenticatedClient()
            .GetFromJsonAsync<JsonElement>("/api/admin/subsystems");
        return [.. snapshot.EnumerateArray().Select(s => s.GetProperty("key").GetString()!)];
    }

    // ─── Строка матрицы ────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> MatrixRows => MatrixKeys.Select(k => new object[] { k });

    [SkippableTheory]
    [MemberData(nameof(MatrixRows))]
    public async Task КаждаяПодсистема_ВыключаетсяБезПятисотки(string key)
    {
        // Документированное нарушение S2a-1 вынесено из прогона Skip-ом с причиной: матрица
        // остаётся зелёной и полной, а перечень нарушений — входом S2a-2/S2b-1.
        Skip.If(KnownViolations.TryGetValue(key, out var known), known);

        var disabled = _hosts.DisabledFor(key);
        var problems = new List<string>();

        HttpClient client;
        try
        {
            client = disabled.CreateAuthenticatedClient();
        }
        catch (Exception ex)
        {
            // Хост не ответил ни одному запросу (или не собрался вовсе): дальше мерить нечего,
            // и причина во всех найденных строках одна — чужая обязательная зависимость от
            // выключенной вертикали на пути старта/первого запроса.
            Assert.Fail($"1. старт: первый запрос к хосту (POST /api/auth/login) не отвечен — {FirstLine(ex)}");
            return;
        }

        // 1. Первый запрос под админом живой: вертикаль не нужна приложению на старте.
        var start = await StatusOfAsync(client, "GET", "/api/auth/me");
        if (start != "200") problems.Add($"1. старт: GET /api/auth/me → {start}");

        // 2. Ключ виден админскому списку именно выключенным (Register не вызван).
        var active = await SnapshotActiveAsync(client, key);
        if (active != "false") problems.Add($"2. снапшот: /api/admin/subsystems отдал active={active}");

        // 3. Маршруты сборки вертикали и её MCP-тулсет: отказ — 404/405/503, не 500.
        foreach (var (method, url) in RoutesOf(key))
        {
            var status = await StatusOfAsync(client, method, url);
            if (status is "404" or "405" or "503") continue;
            problems.Add($"3. маршрут: {method} {url} → {status}");
        }

        foreach (var name in McpNamesOf(key))
        {
            var status = await StatusOfAsync(client, "POST", $"/mcp/{name}");
            if (status == "500") problems.Add($"3. mcp: POST /mcp/{name} → 500 (тулсет не зарегистрирован, "
                + "транспорт обязан ответить unknown_mcp_server 404)");
        }

        // 4. Соседи живы.
        foreach (var probe in NeighborProbes)
        {
            var status = await StatusOfAsync(client, "GET", probe);
            if (status != "200") problems.Add($"4. сосед: GET {probe} → {status}");
        }

        problems.Should().BeEmpty(
            $"вертикаль «{key}» выключена гейтом на регистрации: её собственные маршруты обязаны "
            + "выйти из MVC (404) или ответить заявленным отказом (503), а соседние ручки — жить. "
            + "500 — активированный контроллер или несозданный сервис без зависимостей выключенной "
            + "вертикали:\n" + string.Join("\n", problems.Distinct()));
    }

    /// <summary>
    /// Код ответа как строка строки матрицы. TestServer отдаёт необработанное исключение хоста
    /// как исключение запроса: для матрицы это ровно тот же дефект, что ProblemDetails 500 —
    /// хост не умеет ответить этот запрос, и он записывается как «500» с причиной.
    /// </summary>
    private static async Task<string> StatusOfAsync(HttpClient client, string method, string url)
    {
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), url);
            if (method is "POST" or "PUT" or "PATCH")
                // Тело валидное по форме: цель — дойти до роутинга/активации, а не поймать 400.
                request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request);
            return ((int)response.StatusCode).ToString();
        }
        catch (Exception ex) { return $"500 ({FirstLine(ex)})"; }
    }

    private static async Task<string> SnapshotActiveAsync(HttpClient client, string key)
    {
        try
        {
            var snapshot = await client.GetFromJsonAsync<JsonElement>("/api/admin/subsystems");
            var row = snapshot.EnumerateArray().Single(s =>
                string.Equals(s.GetProperty("key").GetString(), key, StringComparison.OrdinalIgnoreCase));
            return row.GetProperty("active").GetBoolean() ? "true" : "false";
        }
        catch (Exception ex) { return $"не читается ({FirstLine(ex)})"; }
    }

    private static string FirstLine(Exception ex) => (ex.Message ?? "").Split('\n')[0].Trim();

    // ─── Состав строки: маршруты и тулсеты вертикали, снятые с включённого хоста ───

    /// <summary>
    /// Маршруты вертикали с ВКЛЮЧЁННОГО хоста: каждый эндпоинт с контрольным действием, чей
    /// контроллер относится к этому ключу — по явной <c>[SubsystemUsage(key)]</c> или по карте
    /// «сборка → ключ» для контроллеров в сборке самой вертикали. Параметры подставлены
    /// фиктивными значениями по inline-конверсии маршрута, чтобы отказ дал именно гейт, а не
    /// несовпадение шаблона (пустой/неподходящий параметр дал бы 404 «по неверной причине»).
    /// </summary>
    private List<(string Method, string Url)> RoutesOf(string key)
    {
        var endpoints = Enabled.Services.GetRequiredService<EndpointDataSource>();
        var attribution = Enabled.Services.GetRequiredService<SubsystemUsageAttribution>();

        var routes = new SortedSet<(string, string)>(Comparer<(string, string)>.Create(
            (a, b) => string.Compare($"{a.Item1} {a.Item2}", $"{b.Item1} {b.Item2}", StringComparison.Ordinal)));

        foreach (var endpoint in endpoints.Endpoints)
        {
            if (endpoint is not RouteEndpoint route || route.RoutePattern.RawText is not { } rawText) continue;
            var action = route.Metadata.GetMetadata<ControllerActionDescriptor>();
            // Действия вне контроллеров (SignalR-хабы, раздача статики, SPA-фолбэк) неатрибутируемы.
            if (action is null) continue;
            if (!string.Equals(attribution.KeyOf(action.ControllerTypeInfo), key, StringComparison.OrdinalIgnoreCase)) continue;

            var url = WithQueryParams(FillParams(rawText), action);
            foreach (var method in HttpMethodsOf(route)) routes.Add((method, url));
        }

        return [.. routes];
    }

    /// <summary>
    /// Обязательные query-параметры экшена подставляются теми же фиктивными значениями, что и
    /// параметры маршрута. Без них запрос падает в 400 модельной привязкой ДО любого гейта
    /// вертикали, и строка меряла бы форму запроса, а не отказ выключенной вертикали (первый
    /// прогон S2a-2: на выключенном хосте api/video/channels и api/video/feed дали 400).
    /// </summary>
    private static string WithQueryParams(string url, ControllerActionDescriptor action)
    {
        var query = new List<string>();
        // Только явные [FromQuery] без значения по умолчанию (метод экшена читают так же,
        // как ProjectCapabilityGuard): дескриптор параметров MVC здесь не даёт ни источника
        // привязки, ни опциональности — а нужны ровно эти два факта
        foreach (var p in action.MethodInfo.GetParameters())
        {
            if (p.GetCustomAttributes(typeof(FromQueryAttribute), inherit: false).Length == 0) continue;
            if (p.IsOptional) continue;
            query.Add(p.Name + "=" + DummyOfType(p.ParameterType));
        }

        return query.Count == 0 ? url : url + (url.Contains('?') ? "&" : "?") + string.Join("&", query);
    }

    private static string DummyOfType(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(int) || t == typeof(long)) return "1";
        if (t == typeof(bool)) return "true";
        return "x";
    }

    private static IEnumerable<string> HttpMethodsOf(RouteEndpoint route)
    {
        var declared = route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
        // Экшен без верба ([Route] без [HttpGet]/[HttpPost]) принимает любой — пробираем GET:
        // отказ активации не зависит от метода.
        return declared is { Count: > 0 } ? declared : ["GET"];
    }

    /// <summary>
    /// Имена MCP-тулсетов вертикали с включённого хоста — той же картой (KeyOfMcp). Тулсет без
    /// атрибута и не из сборки своей вертикали (personas, wsp, watch, websearch, …) ключу не
    /// относится: его строка матрицы его не мерит — это часть того же finding про явную привязку.
    /// </summary>
    private List<string> McpNamesOf(string key)
    {
        var attribution = Enabled.Services.GetRequiredService<SubsystemUsageAttribution>();
        return [.. Enabled.Services.GetServices<IMcpToolset>()
            .Where(t => string.Equals(attribution.KeyOfMcp(t.Name), key, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Name)
            .Distinct(StringComparer.Ordinal)];
    }

    // ─── Фиктивные значения параметров маршрута ────────────────────────────────────

    private static readonly Regex ParameterToken = new(@"\{([^{}]*)\}", RegexOptions.Compiled);

    /// <summary>
    /// Подстановка фиктивных значений в шаблон маршрута: <c>{name}</c>, <c>{name:конверсия}</c>,
    /// <c>{name?}</c>, <c>{**rest}</c>. Значение подбирается ПО конверсии, чтобы шаблон совпал
    /// и отказ дал именно гейт вертикали.
    /// </summary>
    internal static string FillParams(string rawText) =>
        ParameterToken.Replace(rawText, m => DummyValue(m.Groups[1].Value));

    private static string DummyValue(string token)
    {
        if (token.StartsWith("**")) token = token[2..];
        if (token.EndsWith("?")) token = token[..^1];

        var parts = token.Split(':');
        foreach (var constraint in parts.Skip(1))
        {
            var name = constraint.Split('(')[0];
            var arg = constraint.Length > name.Length ? constraint[(name.Length + 1)..].Trim(')') : "";
            switch (name)
            {
                case "int" or "long": return "1";
                case "guid": return "2b6a2c6e-1c1a-4a1e-9a2f-6b0f1e2d3c4a";
                case "bool": return "true";
                case "alpha": return "abc";
                case "length":
                    return int.TryParse(arg, out var n) ? new string('a', Math.Max(n, 1)) : "abc";
                case "minlength" or "maxlength" or "range": return "abc";
            }
        }
        return "x";
    }
}
