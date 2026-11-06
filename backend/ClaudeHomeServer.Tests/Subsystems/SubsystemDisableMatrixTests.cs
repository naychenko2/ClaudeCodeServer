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
using System.Reflection;
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
/// своей вертикали — ядро, и строка матрицы его не мерит. Finding S2a-1 («строка вакуумно
/// зелёная»: у отключаемого ключа нет ни одной меряемой ручки) закрыт S2b-1 (сдача 2):
/// <c>llm</c> привязан своими Main-ручками ([SubsystemUsage("llm")]: UsageController,
/// ProvidersController, ModelsController), <c>changelog</c> — своей ручкой HistoryController
/// (с гейтом 503: при выключенном changelog эти эндпоинты раньше давали 500),
/// <c>project-services</c> — своим входом PreviewController (сама вертикаль — строка ядра),
/// <c>images</c> починена (опциональная зависимость + 503; аватарные ручки PersonasController
/// привязаны атрибутом на МЕТОДЕ — на классе его ставить нельзя: остальные эндпоинты персон
/// — ядро). <c>project-icons</c> остаётся без ручки: единственный её потребитель — ядро
/// ProjectsController (ProjectsController.cs:25), и вертикаль объявлена ядром (CanDisable=false):
/// мерять «отказ выключенной» нечего. Повторить вакуумную зелёность не даёт сторож полноты
/// <see cref="Сторож_полноты_у_каждого_отключаемого_ключа_есть_привязанная_ручка"/>.
///
/// ═══ Фронтовый реестр (хвост R-S2b-2a) ═══
///
/// Не-ядровые ключи стора — ключи, которые фронтовый реестр <c>SUBSYSTEMS</c>
/// (frontend/src/lib/subsystems.ts) умеет гейтить (исключения — в доке кейса). Фронтовый тест
/// (frontend/src/lib/__tests__/subsystemsRegistry.test.ts) стережёт реестр только против своей
/// фикстуры, а связь «фикстура ↔ бэк» держалась комментарием. Кейс
/// <see cref="Фронтовый_реестр_содержит_каждый_ключ_стора"/> читает фронтовый файл и закрывает
/// цепочку со стороны бэка: новый ключ стора без пары в реестре — красный здесь, а не тихое
/// отставание на следующем шаге (меню/рельса/маршрут).
///
/// ═══ Строки ядра и CanDisable (S2b-1) ═══
///
/// Ключ с `CanDisable=false` (вертикали, которых ядро требует обязательными параметрами) и строка
/// `core:` (`RecordCore` — сессии/чат, проекты, вход, файлы: не `IAppSubsystem`, гейта нет) в строке
/// матрицы как «выключаемые» не меряются: тест строки пропускает их Skip-ом. Согласованность
/// держит сторож <see cref="Сторож_согласованности_CanDisable_и_метки_ядро_сквозная"/>: метка
/// «ядро/сквозная» в <see cref="KnownViolations"/> требует `CanDisable=false` в классе подсистемы,
/// а `CanDisable=false` — задокументированной строки (или `core:`); строка KnownViolations без
/// метки (`llm`) держится отключаемой — невыключаемость объявляется только явной меткой (п. 3
/// хвоста R-S2b-1). Строки ядра входят в
/// <see cref="MatrixKeys"/>: сторож полноты видит их состав, тест строки их пропускает.
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
        // Строки ядра — RecordCore(Program.cs): не `IAppSubsystem`, гейта нет, не отключаются.
        // Строка матрицы пропускает их как «невыключаемые» (Skip по IsCoreKey), а панель
        // «Компоненты» показывает их группой ядра (canDisable=false).
        "core:sessions", "core:projects", "core:auth", "core:files",
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
    /// S2a-2 закрыл шесть ключей (tts, yandex, video, backgrounds, deploy, reader): их контроллеры в Main
    /// держат сервисы вертикалей опциональными (= null) и отвечают 503 с причиной
    /// (TtsController.cs:61, VideoController.cs:42, YandexController.cs:45,
    /// ProjectBackgroundsController.cs:40, DeployController.cs:37, TrayDeployController.cs:37,
    /// ReaderController.cs:36). Убраны и четыре обязательные зависимости вне ядра — без них хост
    /// не поднимался бы вовсе, даже после починки контроллеров: отказ текстом там, где у вызова нет
    /// HTTP-кода (WorkspaceToolset.cs:1757, WebSearchToolset.cs:120, PersonasCrudService.cs:549
    /// и :638), и GetService на пути остановки хоста (Program.cs:2338).
    ///
    /// Метка «ядро/сквозная» — объявление ядра: строка с ней требует `CanDisable=false` в классе
    /// подсистемы, и согласованность метки с кодом держит сторож
    /// <see cref="Сторож_согласованности_CanDisable_и_метки_ядро_сквозная"/>. S2b-1 (сдача 1) объявил
    /// ядром все такие строки: code-graph, dossiers, git, knowledge, memory, project-icons,
    /// project-services, skills, tasks — их выключение невозможно, пока ядро требует их сервисов
    /// обязательными параметрами (цепочки в самих строках); опционализация этих швов — отдельное
    /// решение, не этого шага. Строка <c>llm</c> намеренно БЕЗ метки: по решению постановщика
    /// вертикаль остаётся отключаемой, `CanDisable=false` — только по явному решению Александра/Андрея;
    /// направление 3 сторожа держит это: `CanDisable=false` строки без метки красит его.
    /// Строка <c>images</c> закрыта и снята из перечня в сдаче 2 S2b-1 (привязка ручек + отказ 503).
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
        // Строка без метки «ядро/сквозная» намеренно: по решению постановщика (S2a-2, S2b-1)
        // вертикаль остаётся отключаемой, CanDisable=false — только по явному решению Александра/Андрея.
        ["llm"] = "ядро сессий (CanDisable не объявлен — вертикаль отключаемая): ядро сессий "
            + "(backend/ClaudeHomeServer/Services/SessionManager.cs:739-743) требует "
            + "ILlmSessionAdapterFactory, UsageService и LlmProviderRegistry гейтнутой сборки ClaudeHomeServer.Llm "
            + "(backend/ClaudeHomeServer.Llm/LlmSubsystem.cs:327 — фабрика адаптеров, :356 — UsageService, "
            + ":296 — LlmProviderRegistry) обязательными параметрами — первый запрос к хосту не отвечен.",
        ["memory"] = "ядро/сквозная: шов контекста хода PersonaRecallSourceAdapter "
            + "(backend/ClaudeHomeServer/Services/Composition/PromptSeamAdapters.cs:18, ADR-023) и ядро проектов "
            + "(backend/ClaudeHomeServer/Controllers/ProjectsController.cs:25) требуют PersonaMemoryService "
            + "обязательным параметром. Первый запрос к хосту не отвечен. CanDisable или опционализацию "
            + "этого шва решает S2b-1.",
        ["project-icons"] = "ядро/сквозная: хост стартует и ключ виден выключенным, но соседняя ручка мертва: "
            + "ядро проектов (backend/ClaudeHomeServer/Controllers/ProjectsController.cs:25) требует "
            + "ProjectIconGlyphService обязательным параметром → GET /api/projects 500. Единственная строка, "
            + "где рвётся не старт, а сосед. Решает S2b-1 (CanDisable).",
        ["project-services"] = "ядро/сквозная: ядро чата (backend/ClaudeHomeServer/Hubs/SessionHub.cs:27) требует "
            + "DevServerService обязательным параметром — первый запрос к хосту не отвечен. Обязательный резолв "
            + "на пути остановки хоста починен в S2a-2 (Program.cs:2338 — GetService, как соседние строки того "
            + "же блока). Решает S2b-1 (CanDisable).",
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

    // ─── Фронтовый реестр: ключи стора → SUBSYSTEMS (хвост R-S2b-2a) ─────────────────

    /// <summary>
    /// Фронтовый реестр <c>SUBSYSTEMS</c> (frontend/src/lib/subsystems.ts) обязан содержать каждый
    /// не-ядровой ключ <see cref="SubsystemStateStore"/> включённого хоста. Фронтовый тест
    /// (frontend/src/lib/__tests__/subsystemsRegistry.test.ts) стережёт реестр только против своей
    /// фикстуры <c>BACKEND_KEYS</c>, а связь «фикстура ↔ бэк» держалась комментарием: бэк добавляет
    /// подсистему (новый класс <c>IAppSubsystem</c> + <c>AddSubsystems</c> в Program.cs или запись
    /// <c>DynamicModules</c> в appsettings.json), фикстуру не тронули — фронтовый тест зелёный, а
    /// реестр молча отстаёт, и следующий шаг (меню/рельса/маршрут) не может гейтить новый ключ.
    /// Этот кейс закрывает цепочку со стороны бэка: новый ключ стора без пары в реестре — красный
    /// здесь, а не молча.
    ///
    /// Два исключения, оба объявлены в коде: строки ядра (<see cref="SubsystemStateStore.IsCoreKey"/>;
    /// <see cref="SubsystemStateStore.ActiveKeys"/> не отдаёт их фронту — ядро на фронте не гейтится,
    /// IAppSubsystem.cs:157-158) и тестовые заглушки с префиксом <c>__</c> (<c>__stub</c> — PoC-модуль
    /// appsettings.Testing.json для StubModuleEndpointTests, не подсистема пользователя). Новая строка
    /// ядра или новый тестовый стаб — не повод трогать реестр; новая пользовательская вертикаль — повод.
    /// </summary>
    [Fact]
    public async Task Фронтовый_реестр_содержит_каждый_ключ_стора()
    {
        var live = await LiveKeysAsync(Enabled);
        var registry = FrontendRegistryKeys();

        var missing = live
            .Where(k => !SubsystemStateStore.IsCoreKey(k) && !IsTestStubKey(k))
            .Where(k => !registry.Contains(k))
            .ToList();

        missing.Should().BeEmpty(
            "ключи стора — ключи, которые фронтовый реестр умеет гейтить: ключ SubsystemStateStore "
            + "без пары в SUBSYSTEMS (frontend/src/lib/subsystems.ts) значит, что реестр отстал от "
            + "бэка — его собственный тест зелёный против своей фикстуры. Добавь пару в реестр; "
            + "если ключ — ядро или тестовая заглушка, объяви это здесь (IsCoreKey/IsTestStubKey)");
    }

    // Тестовые заглушки с префиксом `__` (__stub — PoC-модуль appsettings.Testing.json): не
    // пользовательские вертикали, фронтовый реестр их не гейтит и знать не обязан.
    private static bool IsTestStubKey(string key) => key.StartsWith("__", StringComparison.Ordinal);

    /// <summary>
    /// Значения фронтового реестра: блок <c>export const SUBSYSTEMS = { … } as const;</c> из
    /// frontend/src/lib/subsystems.ts. Без блока — явная ошибка, а не «пустой реестр»: сторож
    /// потерял источник, а не строку.
    /// </summary>
    private static HashSet<string> FrontendRegistryKeys()
    {
        var body = FrontendRegistryBlock.Match(File.ReadAllText(RepoFile("frontend", "src", "lib", "subsystems.ts")));
        if (!body.Success)
            throw new InvalidOperationException(
                "блок `export const SUBSYSTEMS = { … } as const;` не найден в "
                + "frontend/src/lib/subsystems.ts — фронтовый реестр нечем померить");
        return new HashSet<string>(
            RegistryValue.Matches(body.Groups["body"].Value).Select(m => m.Groups[1].Value),
            StringComparer.OrdinalIgnoreCase);
    }

    private static readonly Regex FrontendRegistryBlock =
        new(@"export const SUBSYSTEMS = \{(?<body>.*?)\} as const;", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex RegistryValue = new(@"'([^']+)'", RegexOptions.Compiled);

    // Файл репозитория по пути от корня: корень — подъём от bin-каталога тестов до .git
    // (каталог в основном дереве, файл в worktree и в копии мутации) — паттерн
    // MemoryToolsetParityTests.RepoFile.
    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
               && !Directory.Exists(Path.Combine(dir.FullName, ".git"))
               && !File.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        var path = dir is null ? null : Path.Combine([dir.FullName, .. parts]);
        if (path is null || !File.Exists(path))
            throw new InvalidOperationException(
                $"{Path.Combine(parts)} не найден — сторож фронтового реестра не может работать");
        return path;
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

    private static async Task<List<string>> LiveKeysAsync(TestWebApplicationFactory factory) =>
        [.. (await SnapshotRowsAsync(factory)).Select(s => s.GetProperty("key").GetString()!)];

    private static async Task<List<JsonElement>> SnapshotRowsAsync(TestWebApplicationFactory factory)
    {
        var snapshot = await factory.CreateAuthenticatedClient()
            .GetFromJsonAsync<JsonElement>("/api/admin/subsystems");
        return [.. snapshot.EnumerateArray()];
    }

    private static JsonElement RowOf(IEnumerable<JsonElement> rows, string key) =>
        rows.Single(r => string.Equals(r.GetProperty("key").GetString(), key, StringComparison.OrdinalIgnoreCase));

    /// Метка «ядро/сквозная» в причине KnownViolations — объявление ядра. Её ровно этот префикс
    /// и есть: строка без него (llm) — задокументированное нарушение НЕ-ядра, и сторож держит её
    /// отключаемой (направление 3 ниже). Метка и `CanDisable=false` — две половины одного решения:
    /// метка без кода красит направление 2, код без метки — направление 3, и переформулировать
    /// причину, чтобы обойти сторож, больше нечем; менять формулировку может только
    /// тот, кто меняет CanDisable в классе подсистемы.
    private const string CoreReasonPrefix = "ядро/сквозная";

    /// <summary>
    /// Сторож согласованности CanDisable и метки «ядро/сквозная» (S2b-1), три направления:
    /// 1. ключ с `CanDisable=false` в снапшоте обязан быть строкой ядра (`core:`) или строкой
    ///    KnownViolations — иначе его тестовая строка мерит его как «выключаемый», а он не;
    /// 2. ключ KnownViolations с причиной «ядро/сквозная» обязан иметь `CanDisable=false`
    ///    в классе подсистемы — метка без кода была бы обещанием, которое никто не держит;
    /// 3. ключ KnownViolations БЕЗ метки ядра обязан иметь `CanDisable=true` в снапшоте —
    ///    сделать такую вертикаль невыключаемой можно только явным объявлением ядра (меткой),
    ///    а не молча CanDisable=false (п. 3 хвоста R-S2b-1: строка `llm` без метки, вертикаль
    ///    отключаема, пока решение Александра/Андрея не принято).
    /// Мутация «убрать CanDisable=false у любого из этих ключей» краснит направление 2,
    /// «поставить CanDisable=false строке без метки (llm)» — направление 3.
    /// </summary>
    [Fact]
    public async Task Сторож_согласованности_CanDisable_и_метки_ядро_сквозная()
    {
        var rows = await SnapshotRowsAsync(Enabled);

        // 1. CanDisable=false — только как задокументированная строка ядра.
        var undeclared = rows
            .Where(r => !r.GetProperty("canDisable").GetBoolean())
            .Select(r => r.GetProperty("key").GetString()!)
            .Where(k => !SubsystemStateStore.IsCoreKey(k) && !KnownViolations.ContainsKey(k))
            .ToList();
        undeclared.Should().BeEmpty(
            "вертикаль с CanDisable=false вне KnownViolations тест строки мерит как «выключаемую»; "
            + "или верни CanDisable=true, или документируй строку ядра");

        // 2. Метка «ядро/сквозная» требует CanDisable=false в классе.
        var notDeclared = KnownViolations.Keys
            .Where(k => KnownViolations[k].StartsWith(CoreReasonPrefix, StringComparison.Ordinal))
            .Where(k => RowOf(rows, k).GetProperty("canDisable").GetBoolean())
            .ToList();
        notDeclared.Should().BeEmpty(
            "строка KnownViolations с меткой «ядро/сквозная» обязана иметь CanDisable=false "
            + "в классе подсистемы (список причин — в шапке KnownViolations)");

        // 3. Строка без метки ядра остаётся отключаемой: сделать её ядром — явное решение
        //    (метка в причине), а не молчаливый CanDisable=false в классе.
        var silentlyCore = KnownViolations.Keys
            .Where(k => !KnownViolations[k].StartsWith(CoreReasonPrefix, StringComparison.Ordinal))
            .Where(k => !RowOf(rows, k).GetProperty("canDisable").GetBoolean())
            .ToList();
        silentlyCore.Should().BeEmpty(
            "строка KnownViolations без метки «ядро/сквозная» остаётся отключаемой (CanDisable=true "
            + "в классе): объявить её ядром — решение Александра/Андрея, оно ставится меткой в "
            + "причине, а не молча CanDisable=false");
    }

    /// <summary>
    /// Ручка состава отдаёт `canDisable` у каждой записи, и строки ядра в составе есть
    /// (критерий c1 S2b-1): core:sessions, core:projects, core:auth, core:files —
    /// active=true, canDisable=false.
    /// </summary>
    [Fact]
    public async Task Состав_отдаёт_canDisable_и_строки_ядра()
    {
        var rows = await SnapshotRowsAsync(Enabled);
        rows.Should().NotBeEmpty("включённый хост обязан отдавать непустой состав");

        foreach (var r in rows)
            r.TryGetProperty("canDisable", out _).Should().BeTrue(
                $"запись «{r.GetProperty("key").GetString()}» без поля canDisable — панель «Компоненты» "
                + "не сможет отличить строку ядра от тумблера");

        foreach (var key in new[] { "core:sessions", "core:projects", "core:auth", "core:files" })
        {
            var row = RowOf(rows, key);
            row.GetProperty("active").GetBoolean().Should().BeTrue($"{key} — ядро, оно всегда активно");
            row.GetProperty("canDisable").GetBoolean().Should().BeFalse($"{key} — строка ядра, не отключается");
        }
    }

    /// <summary>
    /// Сторож полноты (S2b-1): у каждого ОТКЛЮЧАЕМОГО ключа (canDisable=true) строка обязана
    /// мерять хотя бы одну привязанную ручку — маршрут (явный <c>[SubsystemUsage]</c>, на классе
    /// или на методе, либо карта «сборка → ключ») или MCP-тулсет. Строка без меряемой ручки
    /// «вакуумно зелёна»: тест строки проверяет старт, снапшот и соседей, но не саму
    /// вертикаль — ровно так в S2a-1 нашли llm/changelog/project-services. Строки ядра
    /// (canDisable=false) не проверяются: они пропускаются тестом строки намеренно.
    /// </summary>
    [Fact]
    public async Task Сторож_полноты_у_каждого_отключаемого_ключа_есть_привязанная_ручка()
    {
        var rows = await SnapshotRowsAsync(Enabled);
        var empty = new List<string>();

        foreach (var key in MatrixKeys)
        {
            if (!RowOf(rows, key).GetProperty("canDisable").GetBoolean()) continue;
            if (RoutesOf(key).Count == 0 && McpNamesOf(key).Count == 0) empty.Add(key);
        }

        empty.Should().BeEmpty(
            "отключаемый ключ без единой привязанной ручки даёт вакуумно зелёную строку: "
            + "её маршруты не меряются ничем. Привяжи ручку [SubsystemUsage] (классу или, "
            + "для смешанной ручки, методу) или тулсет — или объявляй вертикаль ядром");
    }

    // ─── Строка матрицы ────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> MatrixRows => MatrixKeys.Select(k => new object[] { k });

    [SkippableTheory]
    [MemberData(nameof(MatrixRows))]
    public async Task КаждаяПодсистема_ВыключаетсяБезПятисотки(string key)
    {
        // Строки ядра (RecordCore) не имеют гейта: их нечего мерить как «отказ выключенной
        // вертикали». Вертикали с CanDisable=false до Skip-а не доходят — они в KnownViolations
        // ниже, и согласованность метки с CanDisable держит сторож «Сторож_согласованности…».
        Skip.If(SubsystemStateStore.IsCoreKey(key),
            "core: строка — RecordCore, гейта нет, как «выключаемая» не меряется");

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
        foreach (var (method, url, body) in RoutesOf(key))
        {
            var status = await StatusOfAsync(client, method, url, body);
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
    private static async Task<string> StatusOfAsync(HttpClient client, string method, string url,
        string body = EmptyBody) => (await ProbeAsync(client, method, url, body)).Status;

    /// <summary>
    /// Тот же запрос, что мерит строка матрицы, но вместе с телом ответа: тест тела 503 читает
    /// текст отказа, а не только его код.
    /// </summary>
    private static async Task<(string Status, string Body)> ProbeAsync(HttpClient client, string method, string url,
        string body = EmptyBody)
    {
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), url);
            if (method is "POST" or "PUT" or "PATCH")
                // Тело валидное по форме: цель — дойти до роутинга, активации и гейта
                // вертикали, а не поймать 400 модельной валидацией (BodyFor ниже).
                request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request);
            return (((int)response.StatusCode).ToString(), await response.Content.ReadAsStringAsync());
        }
        catch (Exception ex) { return ($"500 ({FirstLine(ex)})", ""); }
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

    // ─── Тело 503: отказ называет причину (по образцу тестов плагинов) ──────────────

    /// <summary>
    /// Ключи, починенные отказом 503 с причиной (S2a-2: tts, yandex, video, backgrounds, deploy,
    /// reader; S2b-1: images, changelog — шапка <see cref="KnownViolations"/>): их контроллеры
    /// держат сервисы вертикалей опциональными и на выключенном хосте отвечают 503, тело которого
    /// называет рубильник, вызвавший отказ. Строки вне списка (ядро, KnownViolations)
    /// здесь не меряются: их 503 — не «отказ починенной вертикали».
    /// </summary>
    internal static readonly string[] ReasonBearingKeys =
        ["tts", "yandex", "video", "backgrounds", "deploy", "reader", "images", "changelog"];

    /// <summary>
    /// Строка матрицы меряет код отказа (404/405/503); этот тест мерит его тело: 503 починенной
    /// вертикали обязан нести «Subsystems:{key}:Enabled=false» — ту же форму тела, что у отказа
    /// плагинов (PluginsController.cs:53); сами тесты плагинов при этом меряют только фразу
    /// «Подсистема плагинов выключена» (PluginsControllerTests.cs:226, PluginsToolsetTests.cs:162),
    /// а метку в теле держит этот тест. Инвариант S2a-2
    /// «отказ с настоящей причиной, а не голым кодом» держится сейчас только текстом контроллера
    /// (TtsController.cs:67, YandexController.cs:49, VideoController.cs:45,
    /// ProjectBackgroundsController.cs:43, DeployController.cs:40, ReaderController.cs:39,
    /// ImageGenerationController.cs:29, PersonasController.cs:257, HistoryController.cs:29); здесь
    /// он становится проверяемым. У tts поле reason остаётся not_configured по контракту фронта
    /// (TtsController.cs:15-18) — настоящая причина в error, и её мерит этот тест. Минимум один
    /// 503 на ключ обязателен: без него строка не видела бы отказа вовсе (тот же вакуум, что
    /// закрывал сторож полноты).
    /// </summary>
    [Theory]
    [MemberData(nameof(ReasonBearingRows))]
    public async Task Тело_503_несёт_причину_выключенной_вертикали(string key)
    {
        var client = _hosts.DisabledFor(key).CreateAuthenticatedClient();

        var refusals = new List<string>();
        foreach (var (method, url, body) in RoutesOf(key))
        {
            var (status, text) = await ProbeAsync(client, method, url, body);
            if (status == "503") refusals.Add($"{method} {url} → {text}");
        }

        var marker = $"Subsystems:{key}:Enabled=false";
        refusals.Should().NotBeEmpty(
            $"вертикаль «{key}» починена 503-гейтом: ни один её привязанный маршрут не ответил 503 "
            + "на своём выключенном хосте — гейт здесь не проверен");
        refusals.Where(r => !r.Contains(marker, StringComparison.Ordinal))
            .Should().BeEmpty($"503 вертикали «{key}» обязан называть причину — «{marker}» в теле отказа");
    }

    public static IEnumerable<object[]> ReasonBearingRows =>
        ReasonBearingKeys.Select(k => new object[] { k });

    // ─── Состав строки: маршруты и тулсеты вертикали, снятые с включённого хоста ───

    /// <summary>
    /// Маршруты вертикали с ВКЛЮЧЁННОГО хоста: каждый эндпоинт с контрольным действием, чей
    /// контроллер относится к этому ключу — по явной <c>[SubsystemUsage(key)]</c> (на классе или,
    /// для смешанных ручек, на методе — <see cref="SubsystemUsageAttribution.KeyOfAction"/>) или по
    /// карте «сборка → ключ» для контроллеров в сборке самой вертикали. Параметры подставлены
    /// фиктивными значениями по inline-конверсии маршрута, чтобы отказ дал именно гейт, а не
    /// несовпадение шаблона (пустой/неподходящий параметр дал бы 404 «по неверной причине»).
    /// </summary>
    private List<(string Method, string Url, string Body)> RoutesOf(string key)
    {
        var endpoints = Enabled.Services.GetRequiredService<EndpointDataSource>();
        var attribution = Enabled.Services.GetRequiredService<SubsystemUsageAttribution>();

        // Порядок и дедупликация — по (метод, маршрут): тело у одного эндпоинта одно
        var routes = new SortedSet<(string Method, string Url, string Body)>(
            Comparer<(string Method, string Url, string Body)>.Create(
                (a, b) => string.Compare($"{a.Method} {a.Url}", $"{b.Method} {b.Url}", StringComparison.Ordinal)));

        foreach (var endpoint in endpoints.Endpoints)
        {
            if (endpoint is not RouteEndpoint route || route.RoutePattern.RawText is not { } rawText) continue;
            var action = route.Metadata.GetMetadata<ControllerActionDescriptor>();
            // Действия вне контроллеров (SignalR-хабы, раздача статики, SPA-фолбэк) неатрибутируемы.
            if (action is null) continue;
            if (!string.Equals(attribution.KeyOfAction(action), key, StringComparison.OrdinalIgnoreCase)) continue;

            var url = WithQueryParams(FillParams(rawText), action);
            var body = BodyFor(action);
            foreach (var method in HttpMethodsOf(route)) routes.Add((method, url, body));
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

    private const string EmptyBody = "{}";

    /// <summary>
    /// Тело пробы строится по типу [FromBody]-параметра экшена: «{}» для эндпоинта с
    /// обязательными полями — это 400 модельной валидацией ДО экшена, и строка меряла бы форму
    /// запроса, а не отказ выключенной вертикали (первый прогон S2a-2: api/reader/read и
    /// api/reader/embed-check дали ровно это).
    /// </summary>
    private static string BodyFor(ControllerActionDescriptor action)
    {
        var param = Array.Find(action.MethodInfo.GetParameters(),
            p => p.GetCustomAttributes(typeof(FromBodyAttribute), inherit: false).Length > 0);
        return param is null ? EmptyBody : JsonOfType(param.ParameterType, 0);
    }

    /// <summary>
    /// Фиктивный JSON по типу: только поля, которые валидация MVC считает обязательными
    /// (не-nullable ссылка или значение), со значениями по их типу. Необязательные поля не
    /// трогаются, коллекции — пустые: цель — пройти валидацию и дойти до экшена, а не насытить
    /// запрос. Вложенность глубже двух уровней не раскрывается (там остаётся пустой объект) —
    /// такой эндпоинт дал бы 400 и был бы виден красной строкой, а не молча пропущен.
    /// </summary>
    private static string JsonOfType(Type type, int depth)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;

        if (t == typeof(string)) return "\"x\"";
        if (t == typeof(bool)) return "true";
        if (t == typeof(int) || t == typeof(long) || t == typeof(short)) return "1";
        if (t == typeof(double) || t == typeof(float) || t == typeof(decimal)) return "1";
        if (t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(DateOnly)) return "\"2026-01-01\"";
        if (t == typeof(JsonElement) || t == typeof(System.Text.Json.Nodes.JsonObject)) return EmptyBody;
        // Enum как число — так его читает связывание модели по умолчанию
        if (t.IsEnum) return Convert.ToString(Convert.ToInt32(Enum.GetValues(t).GetValue(0)))!;

        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(t))
            return t.IsGenericType && t.GetGenericArguments().Length == 2 ? "{}" : "[]";

        if (depth >= 2) return EmptyBody;

        var fields = new List<string>();
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanWrite || p.GetIndexParameters().Length > 0) continue;
            if (IsOptionalField(p)) continue;
            // Имена — camelCase, как их ждёт связывание модели
            fields.Add($"\"{char.ToLowerInvariant(p.Name[0])}{p.Name[1..]}\":{JsonOfType(p.PropertyType, depth + 1)}");
        }

        return "{" + string.Join(",", fields) + "}";
    }

    private static readonly NullabilityInfoContext Nullability = new();

    // Обязательное для валидации поле: Nullable<T> или nullable-ссылка — необязательное
    private static bool IsOptionalField(System.Reflection.PropertyInfo p) =>
        p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(Nullable<>)
        || !p.PropertyType.IsValueType && Nullability.Create(p).WriteState == NullabilityState.Nullable;

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
