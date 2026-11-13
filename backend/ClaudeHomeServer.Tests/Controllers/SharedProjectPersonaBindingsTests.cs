using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// W3-B5a (разрез docs/research/shared-projects-personas-cut-2026-11.md §3.1 строка
/// «POST/PUT/DELETE {id}/bindings», §4 И-4): привязки проектной персоны — ключ арендатора и
/// сужение целей для не-владельца одним методом PersonaBindingsService.NonOwnerBindingEditDenied.
///
/// Правило с 12.11 (решение Андрея): правка привязок — Configure, только владелец проекта.
/// Редактор и читатель на любом записывающем пути получают 403 с машинным кодом project_scope
/// ещё до тела (отказ решает [ProjectScope]), посторонний — 404 (существование не раскрываем);
/// отказ — без записи: ни новой привязки, ни изменённой, ни под ключом вызывающего. Чтение
/// привязок открыто всем участникам (редактор владельческую привязку видит).
///
/// Матрица «роль × действие × цель» на настоящем сервере (образец — SharedProjectTaskWriteRightsTests,
/// SharedProjectPersonaRightsTests): владелец — без сужения, как раньше (mcp-цель без реестра у него
/// даёт 400 валидации, а не 403); остальные роли — отказ по праву независимо от цели.
///
/// Сужение NonOwnerBindingEditDenied осталось вторым рубежом: до не-владельца через
/// записывающие действия оно не доходит (фильтр отказывает раньше), тестов прямым вызовом
/// сервиса нет. Путь без фильтра на действии — каталог целей (GET binding-targets, [ProjectScope]
/// на действии нет): каждый его элемент проходит тот же сужение, путь держит
/// BindingTargets_РедакторЧужойПроектнойПерсоны_ТолькоЦелиЭтогоПроекта.
///
/// Порядок отказа: сужение решается ДО ValidateAsync (отказ по праву не зависит от валидности
/// цели: mcp-сервера может не быть в реестре — 403 всё равно 403). Отдельный тест держит это:
/// mcp-цель без реестра — 403, а не 400 «не найден в реестре».
///
/// Ключ: ValidateAsync и UpdateBindings — под scope.TenantOwnerId (И-2). Сторож PersonaScopeGuard
/// этих вызовов не видит (CallRe не покрывает _bindings./_personas.UpdateBindings) — путь держат
/// строки матрицы.
///
/// Конкурентность: ParallelAddBinding_TwoOwnerRequests_BothKept (два запроса владельца — две
/// вкладки) и ParallelAddAndUpdateBinding_BothKept — одновременные записи одной персоны не
/// перечёркивают друг друга. UpdateBindings пишет список ЦЕЛИКОМ из переданного значения —
/// additive- и точечная правка поверх снимка теряют чужую одновременную запись (классическая
/// потерянная правка). Поэтому все три пути идут через read-modify-write под _saveLock в
/// PersonaManager: AppendBinding (POST), UpdateBinding/RemoveBinding (PUT/DELETE одной;
/// доработка возврата ревью 5a794ef5). Полная замена PUT {id}/bindings остаётся last-write-wins
/// по семантике; MCP personas_bindings_set — под ключом владельца, без сужения.
///
/// W3-B5b (та же строка §3.1 «GET /binding-targets», «POST {id}/bindings/suggest, /generate,
/// /apply-defaults»; И-2, И-4): каталог целей — под ключом арендатора; элемент каталога
/// проходит тот же NonOwnerBindingEditDenied до ответа: редактор не видит в выдаче ни одной
/// личной цели владельца и ни одной mcp/плагин-цели. ИИ-подбор и типовые умения (suggest /
/// generate / apply-defaults) — только владельцу: редактору и читателю отказывает фильтр до
/// работы. Без personaId или для своей персоны каталог — как раньше; owner — без сужения.
///
/// Создание проектной персоны — тоже только владелец (Configure, 12.11): редактор получает 403
/// и на POST /api/personas (в том числе с autoBindings и явными bindings), и на quick-create;
/// владелец тем же вызовом — с авто-подбором и явными привязками как раньше.
///
/// Мутации, которые краснеют сейчас (проверяются на копиях дерева, scripts/mutation-copy.sh):
/// - снять [ProjectScope] (или понизить право до WriteContent) с записывающих действий
///   привязок — краснеют строки матрицы для Editor (2xx/400 вместо 403);
/// - вернуть в AddBinding ключ вызывающего вместо арендатора — краснеют строки владельца,
///   если вызывающий не владелец проекта (см. «свою проектную» и привязки на проект);
/// - вернуть запись из снимка в PUT/DELETE одной привязки (UpdateBindings поверх
///   persona.Bindings!.ToList() вместо UpdateBinding/RemoveBinding) — краснеет
///   ParallelAddAndUpdateBinding_BothKept по числу tool-привязок;
/// - убрать фильтр TargetAllowed в BindingTargets — краснеет
///   BindingTargets_РедакторЧужойПроектнойПерсоны_ТолькоЦелиЭтогоПроекта (редактор видит
///   «personal» и чужие проекты);
/// - снять отказ Configure с suggest/generate/apply-defaults — краснеют
///   ПодборИГенерацияПривязок_РедакторуТолькоРазрешённыеЦели и
///   ТиповыеУмения_РедакторНеДобавляетЛичныхЦелейВладельца;
/// - снять отказ Configure с создания проектной персоны — краснеют
///   СозданиеПроектнойПерсоныРедактором_403ВладелецСАвтоПодбором,
///   КвикСозданиеПроектнойПерсоныРедактором_403ВладелецСАвтоПодбором и
///   СозданиеПроектнойПерсоныРедактором_ЯвныеПривязкиЧужихЦелейОтказаныБезСоздания.
/// </summary>
[Trait("Category", "Host")]
public class SharedProjectPersonaBindingsTests : IClassFixture<TestWebApplicationFactory>
{
    private const string Owner = "owner", Editor = "editor", Reader = "reader", Outsider = "outsider";

    // 200 в ожиданиях значит «любой 2xx»
    private const int Ok2xx = 200;

    private const string NoRight = ProjectScopeAttribute.ErrorCode;

    // Заглушка дешёвой модели: на её вызовы ставятся auto-bindings и ИИ-подбор (в тестовом хосте
    // без неё это фоновый one-shot без маршрута). Для матрицы привязок их результат не важен —
    // важен отказ до любой работы; «ничего не подобрано» — штатный исход ([]), как во встроенном
    // стабе хоста. STATIC: экземпляр класса создаётся на каждый тест, а хост — один раз на
    // первом: раннер обязан быть тем же объектом, что зарегистрирован в хосте (тот же приём,
    // что SharedProjectPersonaRightsTests).
    private static readonly NoLlm _llm = new();

    private readonly TestWebApplicationFactory _factory;

    public SharedProjectPersonaBindingsTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
        _factory.ExtraServices = services => services.AddSingleton<ICheapTextRunner>(_llm);
    }

    private sealed class NoLlm : ICheapTextRunner
    {
        // W3-B5b: маркер «живого» подбора. Промпты suggest/generate/материализации несут имя
        // персоны («Персона: {role} ({name})») — тесты W3-B5b ставят маркер в имя, остальные
        // ответы стаба остаются «[]» (прежнее поведение всех прочих тестов класса).
        public const string Marker = "ТестовыйПодбор";

        // Фиксированный набор кандидатов: личный vault владельца, чужая база знаний, mcp-сервер
        // владельца и ключ статического каталога. Первые три — чужие или невалидные цели,
        // «web» — разрешённая: по ней видно, что конвейер живой, по прочим — что сужение работает.
        private const string CandidatesJson =
            """[{"type":"notes","target":"personal"},{"type":"knowledge","target":"dataset-тест"},""" +
            """{"type":"tool","target":"mcp:тест"},{"type":"tool","target":"web"}]""";

        // Черновик quick-create (W3-B5b доработка): имя несёт маркер — промпты авто-подбора
        // привязок несут имя персоны, поэтому следующий за черновиком suggest отвечает
        // CandidatesJson и созданная персона проходит тот же конвейер подбора, что тесты выше.
        private const string DraftJson =
            """{"name":"ТестовыйПодбор","role":"Аналитик","description":"Тестовый аналитик привязок","tone":"сухо и по делу","color":"blue"}""";

        public bool UsesLocal(string actionKey) => false;
        public bool HasFreeRoute(string actionKey) => false;
        public bool HasRoute(string actionKey) => false;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "stub";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
            => Task.FromResult(!prompt.Contains(Marker) ? "[]"
                : actionKey == LocalActionCatalog.PersonaQuickCreate ? DraftJson : CandidatesJson);

        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default)
            => Task.FromResult<string?>("[]");

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private HttpClient OwnerClient() => _factory.CreateAuthenticatedClient();

    private HttpClient SecondClient() => _factory.CreateAuthenticatedClient(
        TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

    private HttpClient ClientOf(string role) => role == Owner ? OwnerClient() : SecondClient();

    private string TenantId => _factory.Services.GetRequiredService<UserStore>()
        .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

    private string MemberId => _factory.Services.GetRequiredService<UserStore>()
        .FindByUsername(TestWebApplicationFactory.SecondUsername)!.Id;

    private PersonaManager Personas => _factory.Services.GetRequiredService<PersonaManager>();
    private INoteAccessor Notes => _factory.Services.GetRequiredService<INoteAccessor>();

    private static async Task SetFlagAsync(HttpClient client) =>
        (await client.PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = true }))
            .EnsureSuccessStatusCode();

    private static string IdOf(string json) =>
        JsonSerializer.Deserialize<JsonElement>(json).GetProperty("id").GetString()!;

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage r) =>
        JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync());

    private static string RunId() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>Проект владельца с проектной персоной и редактором-участником роли role
    /// (Owner/Outsider — участников нет). Под ключом владельца — личный источник заметок
    /// ("personal") и проект-цель: они нужны, чтобы ValidateAsync(арендатор) пропускал обе
    /// цели и отказ различался сужением, а не «цель не найдена».
    /// nameSuffix — добавка к имени персоны (W3-B5b: маркер стаба — промпты подбора несут
    /// имя персоны, по нему стаб отвечает фиксированным набором кандидатов).</summary>
    private async Task<(string ProjectId, string PersonaId)> ArrangeAsync(string role, string? nameSuffix = null)
    {
        var owner = OwnerClient();
        await SetFlagAsync(owner);
        if (role is Editor or Reader) await SetFlagAsync(SecondClient());

        var dir = Directory.CreateDirectory(
            Path.Combine(_factory.TempDir, "pbn_" + Guid.NewGuid().ToString("N")[..8])).FullName;
        var created = await owner.PostAsJsonAsync("/api/projects",
            new { name = "ПривязкиПерсон" + Guid.NewGuid().ToString("N")[..6], rootPath = dir });
        created.EnsureSuccessStatusCode();
        var projectId = IdOf(await created.Content.ReadAsStringAsync());

        if (role is Editor or Reader)
            (await owner.PostAsJsonAsync($"/api/projects/{projectId}/members",
                new { username = TestWebApplicationFactory.SecondUsername, role })).EnsureSuccessStatusCode();

        // Личный источник заметок под ключом владельца — до создания персоны: авто-посев
        // дефолтных привязок её не должен задеть (матрица стартует с пустого набора).
        Notes.Create(TenantId, new CreateNoteRequest(Title: "Личная " + RunId(), Content: "тело"));

        var persona = await owner.PostAsJsonAsync("/api/personas",
            new { name = "Привязываемая" + Guid.NewGuid().ToString("N")[..6] + nameSuffix, scope = "project", projectId });
        persona.EnsureSuccessStatusCode();
        var personaId = IdOf(await persona.Content.ReadAsStringAsync());

        // Создание проектной персоны севит дефолтные привязки к данным проекта
        // (SeedProjectDefaults, PersonasCrudService.cs:143) — матрица должна стартовать
        // с пустого набора, иначе «Notes проекта» упала бы на дубликате посевной цели.
        Personas.UpdateBindings(personaId, TenantId, null);
        return (projectId, personaId);
    }

    private static async Task<(int Status, string? Code, string Body)> SendAsync(HttpClient client,
        HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        string? code = null;
        if (text.Length > 0)
        {
            try
            {
                var json = JsonSerializer.Deserialize<JsonElement>(text);
                if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("code", out var c))
                    code = c.GetString();
            }
            catch (JsonException) { /* ответ не JSON — кода нет */ }
        }
        return ((int)response.StatusCode, code, text);
    }

    // Тела запросов — анонимные объекты (тот же способ, что у SharedProjectPersonaRightsTests):
    // JsonContent c JsonElement-значением тестовый клиент роняет на сериализации ObjectType.
    private static object ToolBody(string target) => new { type = "tool", target };

    private static object NotesBody(string target) => new { type = "notes", target };

    private static object NotesBodyWithCondition(string target, string condition) =>
        new { type = "notes", target, condition };

    private static object KnowledgeBody(string target) => new { type = "knowledge", target };

    private static object ProjectBody(string target) => new { type = "project", target };

    private static bool HasBinding(Persona persona, string type, string target) =>
        persona.Bindings?.Any(b =>
            b.Type.ToString().Equals(type, StringComparison.OrdinalIgnoreCase)
            && b.Target == target) == true;

    private static bool HasBindingWithCondition(Persona persona, string type, string target, string condition) =>
        persona.Bindings?.Any(b =>
            b.Type.ToString().Equals(type, StringComparison.OrdinalIgnoreCase)
            && b.Target == target && b.Condition == condition) == true;

    // Матрица «роль × действие × цель». Тело — JSON строкой: цель вида «mcp:x» — обычный
    // JSON-escape (тот же разбор, что у ParseBinding).
    private sealed record Act(string Name, HttpMethod Method, string Path,
        Func<string, Dictionary<string, string>, object?> Body,
        int ForOwner, int ForEditor, int ForReader, int ForOutsider)
    {
        public int For(string role) => role switch
        {
            Owner => ForOwner, Editor => ForEditor, Reader => ForReader, _ => ForOutsider,
        };
    }

    private static readonly Act[] Actions =
    [
        // С 12.11 правка привязок — Configure (только владелец): редактору и читателю отказывает
        // фильтр (403 project_scope) до тела, постороннему — 404. Сужение NonOwnerBindingEditDenied
        // осталось вторым рубежом и до не-владельца через REST не доходит.
        new("Notes проекта", HttpMethod.Post, "/api/personas/{p}/bindings",
            (r, ids) => NotesBody(ids["p"]), Ok2xx, 403, 403, 404),
        new("личная цель", HttpMethod.Post, "/api/personas/{p}/bindings",
            (r, _) => NotesBody("personal"), Ok2xx, 403, 403, 404),
        // mcp-цель: не-владельцу — 403 сужением (до валидности цели); владельцу — сужения нет,
        // он доходит до ValidateAsync и получает 400 «не найден в вашем реестре» (в тестовом
        // хосте реестра владельца пуст — отдельный тест McpЦельБезРеестра… держит этот порядок).
        new("mcp-цель", HttpMethod.Post, "/api/personas/{p}/bindings",
            (r, _) => ToolBody("mcp:" + r), 400, 403, 403, 404),
        new("Tool каталога", HttpMethod.Post, "/api/personas/{p}/bindings",
            (r, _) => ToolBody("web"), Ok2xx, 403, 403, 404),
        // PUT/DELETE существующей привязки: личная цель владельца — 403 (не-владелец её не
        // меняет и не удаляет, И-4); своя проектная — правится и удаляется.
        new("личную не изменить", HttpMethod.Put, "/api/personas/{p}/bindings/{b}",
            (r, _) => NotesBodyWithCondition("personal", "Условие " + r), Ok2xx, 403, 403, 404),
        new("личную не удалить", HttpMethod.Delete, "/api/personas/{p}/bindings/{b}",
            (r, _) => null, Ok2xx, 403, 403, 404),
        new("свою проектную изменить", HttpMethod.Put, "/api/personas/{p}/bindings/{b}",
            (r, ids) => NotesBodyWithCondition(ids["p"], "Условие " + r), Ok2xx, 403, 403, 404),
        new("свою проектную удалить", HttpMethod.Delete, "/api/personas/{p}/bindings/{b}",
            (r, _) => null, 204, 403, 403, 404),
    ];

    // Фикстур под строки PUT/DELETE: существующая привязка, записанная владельцем —
    // личная (Notes=personal) или проектная (Notes=projectId).
    private static async Task<string> SeedBindingAsync(HttpClient owner, string personaId, bool notes,
        string target)
    {
        var created = await owner.PostAsJsonAsync($"/api/personas/{personaId}/bindings",
            notes ? NotesBody(target) : ToolBody(target));
        created.EnsureSuccessStatusCode();
        return (await BodyAsync(created)).GetProperty("id").GetString()!;
    }

    public static IEnumerable<object[]> Matrix() =>
        from a in Actions
        from role in new[] { Owner, Editor, Reader, Outsider }
        select new object[] { a.Name, role };

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task ПривязкаПроектнойПерсоны_СужаетЦелиНеВладельцуБезЗаписи(string actionName, string role)
    {
        var action = Actions.Single(a => a.Name == actionName);
        var (projectId, personaId) = await ArrangeAsync(role);
        var runId = RunId();

        var ids = new Dictionary<string, string> { ["p"] = projectId };
        switch (actionName)
        {
            case "личную не изменить" or "личную не удалить":
                ids["b"] = await SeedBindingAsync(OwnerClient(), personaId, true, "personal");
                break;
            case "свою проектную изменить" or "свою проектную удалить":
                ids["b"] = await SeedBindingAsync(OwnerClient(), personaId, true, projectId);
                break;
        }

        var url = action.Path.Replace("{p}", personaId);
        foreach (var (key, value) in ids) url = url.Replace("{" + key + "}", value);

        var (status, code, _) = await SendAsync(ClientOf(role), action.Method, url, action.Body(runId, ids));

        var expected = action.For(role);
        if (expected == Ok2xx)
        {
            (status >= 200 && status < 300).Should().BeTrue($"{role}: {actionName} → {status}");
        }
        else
        {
            status.Should().Be(expected, $"{role}: {actionName}");
            if (expected == 403)
                code.Should().Be(NoRight, $"{role}: {actionName} — отказ решает фильтр (machine code)");
        }

        // Состояние стора после полного цикла: разрешённое лежит под ключом арендатора и видно
        // его чтением; отказанное не оставило ни записи, ни правки — ни под чужим ключом.
        var persona = Personas.Get(personaId, TenantId)!;
        switch (actionName, role)
        {
            case ("Notes проекта", Owner):
                HasBinding(persona, "notes", projectId).Should().BeTrue(
                    $"{role}: привязка к проекту легла под ключом арендатора (И-2)");
                var view = await BodyAsync(await OwnerClient().GetAsync($"/api/personas/{personaId}/bindings"));
                view.EnumerateArray().Select(b => b.GetProperty("target").GetString())
                    .Should().Contain(projectId, "привязка участника видна чтению владельца");
                break;
            case ("Notes проекта", _):
                HasBinding(persona, "notes", projectId).Should().BeFalse(
                    $"{role}: отказанная привязка не оставила следа под ключом владельца");
                HasBinding(Personas.Get(personaId, MemberId) ?? NewSentinel(), "notes", projectId).Should().BeFalse(
                    $"{role}: и под ключом вызывающего её нет");
                break;
            case ("личная цель", Owner):
                HasBinding(persona, "notes", "personal").Should().BeTrue(
                    "владелец привязывает персону к своей личной цели — сужение его не касается");
                break;
            case ("личная цель", _):
                HasBinding(persona, "notes", "personal").Should().BeFalse($"{role}: отказанная цель не записана");
                break;
            case ("mcp-цель", _):
                HasBinding(persona, "tool", "mcp:" + runId).Should().BeFalse($"{role}: mcp-привязка не записана");
                break;
            case ("Tool каталога", Owner):
                HasBinding(persona, "tool", "web").Should().BeTrue(
                    $"{role}: Tool без mcp: — разрешённая цель (И-4)");
                break;
            case ("Tool каталога", _):
                HasBinding(persona, "tool", "web").Should().BeFalse($"{role}: отказанный Tool не записан");
                break;
            case ("личную не изменить", Owner):
                HasBindingWithCondition(persona, "notes", "personal", "Условие " + runId).Should().BeTrue(
                    "владелец меняет свою личную привязку");
                break;
            case ("личную не изменить", _):
                HasBindingWithCondition(persona, "notes", "personal", "Условие " + runId).Should().BeFalse(
                    $"{role}: личную привязку владельца не-владелец не меняет (И-4)");
                HasBinding(persona, "notes", "personal").Should().BeTrue($"{role}: привязка на месте");
                break;
            case ("личную не удалить", Owner):
                HasBinding(persona, "notes", "personal").Should().BeFalse("владелец удаляет свою привязку");
                break;
            case ("личную не удалить", _):
                HasBinding(persona, "notes", "personal").Should().BeTrue(
                    $"{role}: отказанное удаление оставило привязку на месте");
                break;
            case ("свою проектную изменить", Owner):
                HasBindingWithCondition(persona, "notes", projectId, "Условие " + runId).Should().BeTrue(
                    $"{role}: правка своей проектной привязки записана");
                break;
            case ("свою проектную изменить", _):
                HasBindingWithCondition(persona, "notes", projectId, "Условие " + runId).Should().BeFalse(
                    $"{role}: отказанная правка не тронула привязку");
                break;
            case ("свою проектную удалить", Owner):
                HasBinding(persona, "notes", projectId).Should().BeFalse($"{role}: привязка удалена");
                break;
            case ("свою проектную удалить", _):
                HasBinding(persona, "notes", projectId).Should().BeTrue($"{role}: отказанное удаление оставило её");
                break;
        }
    }

    // Пустой несуществующий объект вместо «личного стора вызывающего»: там персоны нет при
    // любом ключе — проверка «нет под чужим ключом» не вакуумная.
    private static Persona NewSentinel() => new() { Id = "sentinel" };

    // ── И-4: сужение решает право, а не валидность цели ────────────────────────────────────────
    //
    // mcp-сервера нет ни в каком реестре (в тестовом хосте реестра владельца пусто). Если бы
    // сужение шло после ValidateAsync, редактор получил бы 400 «MCP-сервер не найден в вашем
    // реестре» — отказ по телу вместо отказа по праву. Ожидание: 403 для не-владельца до
    // валидации; владелец доходит до ValidateAsync и получает 400 (для него mcp-цель —
    // его право, но цели нет).

    [Fact]
    public async Task McpЦельБезРеестра_НеВладельцу403ВладелецДоВалидации400()
    {
        var (_, personaId) = await ArrangeAsync(Editor);
        var target = "mcp:" + RunId();

        var (editorStatus, _, _) = await SendAsync(SecondClient(), HttpMethod.Post,
            $"/api/personas/{personaId}/bindings", ToolBody(target));
        editorStatus.Should().Be(403,
            "отказ по праву (Configure) не зависит от того, есть ли сервер в реестре: сужение до ValidateAsync");

        var (ownerStatus, _, ownerBody) = await SendAsync(OwnerClient(), HttpMethod.Post,
            $"/api/personas/{personaId}/bindings", ToolBody(target));
        ownerStatus.Should().Be(400, "владелец доходит до ValidateAsync: «не найден в вашем реестре»");
        ownerBody.Should().Contain("реестре", "отказ владельца — текстом валидации, не сужением");

        HasBinding(Personas.Get(personaId, TenantId)!, "tool", target).Should().BeFalse(
            "отказ — без записи ни на одном пути");
    }

    // ── И-4: личную привязку владельца участник видит, но не трогает ───────────────────────────
    //
    // «Видит» — GET bindings отдаёт обе привязки (личную и проектную); «не трогает» — строки
    // матрицы «личную не изменить/не удалить». Здесь — что отказ PUT/DELETE не превращает
    // личную привязку в проектную и не меняет её полей (цель и режим на месте).

    [Fact]
    public async Task ЛичнуюПривязкуВладельцаРедакторВидитНоНеТроает()
    {
        var (projectId, personaId) = await ArrangeAsync(Editor);
        var personalId = await SeedBindingAsync(OwnerClient(), personaId, true, "personal");
        var projectBindingId = await SeedBindingAsync(OwnerClient(), personaId, true, projectId);

        var view = await BodyAsync(await SecondClient().GetAsync($"/api/personas/{personaId}/bindings"));
        view.EnumerateArray().Select(b => b.GetProperty("target").GetString())
            .Should().Contain("personal", "И-4: существующие привязки владельца к личным целям участник видит")
            .And.Contain(projectId);

        var (putStatus, _, _) = await SendAsync(SecondClient(), HttpMethod.Put,
            $"/api/personas/{personaId}/bindings/{personalId}", NotesBody(projectId));
        putStatus.Should().Be(403, "личную привязку не-владелец не меняет — даже в проектную цель");

        var (delStatus, _, _) = await SendAsync(SecondClient(), HttpMethod.Delete,
            $"/api/personas/{personaId}/bindings/{personalId}");
        delStatus.Should().Be(403);

        var persona = Personas.Get(personaId, TenantId)!;
        var kept = persona.Bindings!.Single(b => b.Id == personalId);
        kept.Target.Should().Be("personal", "цель прежняя");
        kept.Type.Should().Be(PersonaBindingType.Notes);
        persona.Bindings!.Any(b => b.Id == projectBindingId).Should().BeTrue("проектная привязка тоже на месте");
    }

    // ── И-4: Knowledge — только датасет проекта; чужой/личный датасет — 400 ────────────────────
    //
    // Датасета у проекта в тестовом хосте нет (Dify не настроен, wk-записи нет) — «датасет
    // проекта» для не-владельца не из чего построить: любая Knowledge-цель — чужая. Владелец
    // с той же целью доходит до ValidateAsync и получает 400 «База знаний не найдена» (для
    // него сужения нет). Позитив ветки (цель = DifyDatasetId проекта) в тестовом хосте
    // не из чего построить — ветку держит код IsProjectBoundTarget (сверка с той же
    // wk-записью, что KnownDatasets).

    [Fact]
    public async Task KnowledgeЦельНеВладельцу403ВладельцуДоВалидации400()
    {
        var (_, personaId) = await ArrangeAsync(Editor);
        var target = "dataset-" + RunId();

        var (editorStatus, _, _) = await SendAsync(SecondClient(), HttpMethod.Post,
            $"/api/personas/{personaId}/bindings", KnowledgeBody(target));
        editorStatus.Should().Be(403, "правка привязок — только владелец (Configure): фильтр отказывает до сужения");

        var (ownerStatus, _, ownerBody) = await SendAsync(OwnerClient(), HttpMethod.Post,
            $"/api/personas/{personaId}/bindings", KnowledgeBody(target));
        ownerStatus.Should().Be(400, "владелец — через ValidateAsync");
        ownerBody.Should().Contain("не найдена", "для владельца сужения нет — отказ по валидации цели");

        HasBinding(Personas.Get(personaId, TenantId)!, "knowledge", target).Should().BeFalse("отказ — без записи");
    }

    // ── И-4: цель «проект» другого проекта — 400 даже когда ValidateAsync(арендатор) её пропускает ─
    //
    // Второй проект того же владельца — валидная цель для ValidateAsync под ключом арендатора
    // («Проект не найден или недоступен» его не отбивает). Для редактора это чужая цель:
    // Project/ProjectPath/ProjectPersonas/ProjectTasks разрешены только с ЭТИМ projectId.

    [Fact]
    public async Task ЧужойПроектЦелью_Редактору403Владельцу200()
    {
        var (_, personaId) = await ArrangeAsync(Editor);
        var dir = Directory.CreateDirectory(
            Path.Combine(_factory.TempDir, "pbx_" + Guid.NewGuid().ToString("N")[..8])).FullName;
        var other = await OwnerClient().PostAsJsonAsync("/api/projects",
            new { name = "ВторойПроектПривязок" + Guid.NewGuid().ToString("N")[..6], rootPath = dir });
        other.EnsureSuccessStatusCode();
        var otherId = IdOf(await other.Content.ReadAsStringAsync());

        var (editorStatus, _, _) = await SendAsync(SecondClient(), HttpMethod.Post,
            $"/api/personas/{personaId}/bindings", ProjectBody(otherId));
        editorStatus.Should().Be(403, "редактор привязок не правит вовсе (Configure), в том числе к другому проекту");

        var (ownerStatus, _, _) = await SendAsync(OwnerClient(), HttpMethod.Post,
            $"/api/personas/{personaId}/bindings", ProjectBody(otherId));
        (ownerStatus >= 200 && ownerStatus < 300).Should().BeTrue("своей цели владельца сужение не касается");
        HasBinding(Personas.Get(personaId, TenantId)!, "project", otherId).Should().BeTrue("записана под ключом владельца");
    }

    // ── И-2: запись редактора видна владельцу (общий стор) и отсутствует под ключом вызывающего ──

    [Fact]
    public async Task ПривязкаРедактора_403ИНичегоНеПишет_ВладелецПишетПодСвоимКлючом()
    {
        var (_, personaId) = await ArrangeAsync(Editor);

        var (status, code, _) = await SendAsync(SecondClient(), HttpMethod.Post,
            $"/api/personas/{personaId}/bindings", ToolBody("web"));
        status.Should().Be(403, "правка привязок — только владелец (решение 12.11)");
        code.Should().Be(NoRight);
        HasBinding(Personas.Get(personaId, TenantId)!, "tool", "web").Should().BeFalse("отказ — без записи");

        var post = await OwnerClient().PostAsJsonAsync($"/api/personas/{personaId}/bindings", ToolBody("web"));
        post.EnsureSuccessStatusCode();
        var id = (await BodyAsync(post)).GetProperty("id").GetString()!;
        var view = await BodyAsync(await SecondClient().GetAsync($"/api/personas/{personaId}/bindings"));
        view.EnumerateArray().Select(b => b.GetProperty("id").GetString())
            .Should().Contain(id, "редактор привязки видит (чтение открыто)");
        Personas.Get(personaId, MemberId).Should().BeNull("персона не дублируется в личное пространство участника");
    }

    // ── Конкурентность: гонка — два запроса владельца (две вкладки) добавляют привязку одной персоны ─
    //
    // До AppendBinding оба запроса читали пустой набор и каждый писал свой список — одна из
    // привязок терялась (снимок → полная замена). AppendBinding строит список под _saveLock
    // из текущего значения: каждая сторона приносит свою новую привязку поверх уже
    // записанной. Ожидаем ровно обе, без дублей.

    [Fact]
    public async Task ParallelAddBinding_TwoOwnerRequests_BothKept()
    {
        var (projectId, personaId) = await ArrangeAsync(Editor);
        var firstTarget = "personal"; // личная цель — законна владельцу
        var secondTarget = projectId; // цель проекта

        var firstPost = OwnerClient().PostAsJsonAsync($"/api/personas/{personaId}/bindings", NotesBody(firstTarget));
        var secondPost = OwnerClient().PostAsJsonAsync($"/api/personas/{personaId}/bindings", NotesBody(secondTarget));
        foreach (var response in await Task.WhenAll(firstPost, secondPost))
            response.EnsureSuccessStatusCode();

        var persona = Personas.Get(personaId, TenantId)!;
        persona.Bindings!.Count(b => b.Type == PersonaBindingType.Notes).Should().Be(2,
            "ровно две привязки: ни одна из одновременных не потеряна, дублей нет");
        HasBinding(persona, "notes", firstTarget).Should().BeTrue("привязка первой вкладки в сторе");
        HasBinding(persona, "notes", secondTarget).Should().BeTrue(
            "привязка второй вкладки в том же сторе: additive-слияние UpdateBindings ничего не перезаписало");

        var view = await BodyAsync(await OwnerClient().GetAsync($"/api/personas/{personaId}/bindings"));
        view.EnumerateArray().Count(b => b.GetProperty("type").GetString() == "notes").Should().Be(2,
            "GET после гонки видит обе");
    }

    // ── Конкурентность (возврат ревью 5a794ef5): правка/удаление одной привязки не стирают ──────
    // ── одновременное добавление ─────────────────────────────────────────────────────────────────
    //
    // PUT/DELETE {id}/bindings/{bindingId} писали весь список из снимка вызывающего
    // (PersonasController.cs:923/:943 в 8f5431cb2): привязка, добавленная после момента
    // снятия списка и до записи под _saveLock, исчезала без ошибки. Окно этой потери — тело
    // обработчика запроса; измерено: простая пара HTTP-запросов в него не попадает (прежняя
    // форма зелёная в любом порядке старта — окна запросов не пересекаются на микросекундах).
    // Поэтому тест расширяет окно тем, что расширяет его по-настоящему:
    //  - тяжёлая запись персоны (тысячи привязок): каждый Save под _saveLock занимает
    //    блокировку на десятки миллисекунд — запись запроса встаёт в очередь за ними;
    //  - непрерывная буря прямых AppendBinding из потока теста: у прямого вызова нет HTTP-
    //    конвейера, его критическая секция гарантированно конкурирует с запросом и стоит в
    //    очереди за чужими Save — то есть после момента снятия списка запросом.
    // В прежней форме бури, попавшей после снятия, затирается записью запроса. UpdateBinding/
    // RemoveBinding делают read-modify-write под тем же _saveLock, что AppendBinding: их
    // поиск и мутация — внутри той же очереди, никто не затирается.
    //
    // Мутация «вернуть запись из снимка» (контроллер снова пишет persona.Bindings!.ToList()
    // через UpdateBindings) — этот тест красный: стору не хватает привязок бури.

    // Сколько тяжёлых привязок вешают запись персоны: их JSON — сотни килобайт, каждый Save
    // занимает _saveLock дольше, чем длится HTTP-конвейер запроса (измерено: 10K привязок
    // дают ~30 мс на запись).
    private const int HeavyBindings = 5_000;

    // Многопоточная буря прямых добавлений: N потоков добавляют до остановки. У прямого
    // вызова нет HTTP-конвейера — его потоки встают в очередь на _saveLock раньше запроса,
    // но выполняются за чужими тяжёлыми Save, то есть после момента, когда запрос снял
    // список. Именно эта очередь и есть окно потерянной правки. Возвращает задачи
    // (их результат — числа добавлений каждого потока) и остановку.
    private (List<Task<int>> Work, CancellationTokenSource Stop) StartStorm(string personaId,
        string prefix, int threads)
    {
        var cts = new CancellationTokenSource();
        var work = Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            var n = 0;
            while (!cts.IsCancellationRequested)
            {
                Personas.AppendBinding(personaId, TenantId, new PersonaBinding
                { Type = PersonaBindingType.Tool, Target = $"{prefix}{t}-{n}" });
                n++;
            }
            return n;
        })).ToList();
        return (work, cts);
    }

    [Fact]
    public async Task ParallelAddAndUpdateBinding_BothKept()
    {
        var (_, personaId) = await ArrangeAsync(Editor);
        var editedId = await SeedBindingAsync(OwnerClient(), personaId, true, "personal");
        var condition = "Изменено " + RunId();

        // Tool-ключи каталога — законная цель редактора (матрица «Tool каталога»), все разные:
        // HTTP-добавления не сталкиваются с дубликатами.
        var toolKeys = PersonaBindingsService.ToolCatalog.Keys.ToList();
        toolKeys.Count.Should().BeGreaterThanOrEqualTo(11, "гоне нужны разные законные цели");

        // Тяжёлая запись: существующие привязки + балласт, чтобы каждая запись персоны
        // держала _saveLock десятки миллисекунд.
        var seeded = Personas.Get(personaId, TenantId)!.Bindings!;
        Personas.UpdateBindings(personaId, TenantId, [.. seeded,
            ..Enumerable.Range(0, HeavyBindings).Select(i => new PersonaBinding
            { Type = PersonaBindingType.Tool, Target = "heavy-" + i })]);

        const int stormThreads = 8;
        var httpKeys = toolKeys.Take(5).ToList();
        var httpKeys2 = toolKeys.Skip(5).Take(5).ToList();

        CancellationTokenSource? stop1 = null, stop2 = null;
        try
        {
            // Фаза 1: буря прямых добавлений + HTTP PUT существующей + HTTP POST новых.
            var (storm1, s1) = StartStorm(personaId, "storm1-", stormThreads);
            stop1 = s1;
            // Короткая пауза: потоки бури должны успеть встать в очередь на _saveLock
            // до запросов (у них нет конвейера, им доли миллисекунды хватает).
            await Task.Delay(5);
            var reqs = new List<Task<HttpResponseMessage>>
            {
                OwnerClient().PutAsJsonAsync($"/api/personas/{personaId}/bindings/{editedId}",
                    NotesBodyWithCondition("personal", condition)),
            };
            reqs.AddRange(httpKeys.Select(k =>
                OwnerClient().PostAsJsonAsync($"/api/personas/{personaId}/bindings", ToolBody(k))));
            foreach (var r in await Task.WhenAll(reqs))
                r.EnsureSuccessStatusCode();
            stop1.Cancel();
            var counts1 = await Task.WhenAll(storm1);

            var persona = Personas.Get(personaId, TenantId)!;
            HasBindingWithCondition(persona, "notes", "personal", condition).Should().BeTrue(
                "правка владельца записана");
            foreach (var k in httpKeys)
                persona.Bindings!.Any(b => b.Target == k).Should().BeTrue($"HTTP-добавление «{k}» в сторе");
            counts1.Sum().Should().BeGreaterThan(stormThreads,
                "буря должна состояться и пережить хотя бы одну тяжёлую запись");
            for (var t = 0; t < stormThreads; t++)
                for (var i = 0; i < counts1[t]; i++)
                    persona.Bindings!.Any(b => b.Target == $"storm1-{t}-{i}").Should().BeTrue(
                        $"добавление бури {t}-{i} в сторе: правка писалась не из устаревшего снимка");

            // Фаза 2: тот же сценарий с DELETE — удаление одной не стирает одновременные добавления.
            var doomedId = await SeedBindingAsync(OwnerClient(), personaId, false, toolKeys[10]);
            var (storm2, s2) = StartStorm(personaId, "storm2-", stormThreads);
            stop2 = s2;
            await Task.Delay(5);
            var reqs2 = new List<Task<HttpResponseMessage>>
            {
                OwnerClient().DeleteAsync($"/api/personas/{personaId}/bindings/{doomedId}"),
            };
            reqs2.AddRange(httpKeys2.Select(k =>
                OwnerClient().PostAsJsonAsync($"/api/personas/{personaId}/bindings", ToolBody(k))));
            foreach (var r in await Task.WhenAll(reqs2))
                r.EnsureSuccessStatusCode();
            stop2.Cancel();
            var counts2 = await Task.WhenAll(storm2);

            persona = Personas.Get(personaId, TenantId)!;
            persona.Bindings!.Any(b => b.Id == doomedId).Should().BeFalse("привязка удалена");
            foreach (var k in httpKeys2)
                persona.Bindings!.Any(b => b.Target == k).Should().BeTrue($"HTTP-добавление «{k}» в сторе");
            counts2.Sum().Should().BeGreaterThan(stormThreads, "буря должна состояться");
            for (var t = 0; t < stormThreads; t++)
                for (var i = 0; i < counts2[t]; i++)
                    persona.Bindings!.Any(b => b.Target == $"storm2-{t}-{i}").Should().BeTrue(
                        $"добавление бури {t}-{i} в сторе: удаление писалось не из устаревшего снимка");
            HasBindingWithCondition(persona, "notes", "personal", condition).Should().BeTrue(
                "правка первой фазы на месте");
        }
        finally
        {
            // Бурю останавливаем всегда: её поток не должен тянуть записи в следующие тесты.
            // Тяжёлую запись не оставляем в общем сторе фикстуры: её мегабайты замедлили бы
            // каждую запись следующих тестов.
            stop1?.Cancel();
            stop2?.Cancel();
            Personas.UpdateBindings(personaId, TenantId, null);
        }
    }

    // ── Полная замена (PUT {id}/bindings): ключ арендатора, личные привязки владельца ────────────
    // ── не-владельцу неприкосновенны (хвост 1 возврата 5a794ef5) ─────────────────────────────────
    //
    // SetBindings шла с ключом вызывающего (не-владелец получал 404 fail-closed). Теперь ключ —
    // арендатора, сужение — тот же NonOwnerBindingEditDenied: удалить или подменить личную
    // привязку владельца участник не может (403), личная цель в запросе — 400, проектные — может.
    // Дословно повторённая запросом личная привязка остаётся как была: id и поля не заменяются —
    // полная замена участника касается только проектной части набора.

    [Fact]
    public async Task ПолнаяЗаменаПривязок_РедакторуОтказ403_ВладелецМожет()
    {
        var (projectId, personaId) = await ArrangeAsync(Editor);
        var personalId = await SeedBindingAsync(OwnerClient(), personaId, true, "personal");
        var projectBindingId = await SeedBindingAsync(OwnerClient(), personaId, true, projectId);

        // Любая полная замена от редактора — 403 project_scope, стор нетронут (сужение — второй рубеж).
        foreach (var body in new object[]
        {
            new { bindings = new[] { NotesBody(projectId) } },
            new { bindings = new[] { NotesBodyWithCondition("personal", "Подменено " + RunId()), NotesBody(projectId) } },
            new { bindings = new[] { NotesBody("personal"), NotesBodyWithCondition(projectId, "Новое " + RunId()) } },
        })
        {
            var (status, code, _) = await SendAsync(SecondClient(), HttpMethod.Put,
                $"/api/personas/{personaId}/bindings", body);
            status.Should().Be(403, "полная замена привязок — только владелец (Configure)");
            code.Should().Be(NoRight);
        }

        var persona = Personas.Get(personaId, TenantId)!;
        persona.Bindings!.Single(b => b.Id == personalId).Condition.Should().Be("", "личная привязка не тронута");
        persona.Bindings!.Any(b => b.Id == projectBindingId).Should().BeTrue("проектная тоже на месте");
        Personas.Get(personaId, MemberId).Should().BeNull("под ключом вызывающего записи нет");

        // Владелец может удалить проектные: набор перезаписывается.
        (await OwnerClient().PutAsJsonAsync($"/api/personas/{personaId}/bindings",
            new { bindings = new[] { NotesBody("personal") } })).EnsureSuccessStatusCode();
        persona = Personas.Get(personaId, TenantId)!;
        persona.Bindings!.Any(b => b.Target == projectId).Should().BeFalse("владелец удалил проектную привязку");
        persona.Bindings!.Any(b => b.Target == "personal").Should().BeTrue("личная осталась");
    }

    // ── Граница W3-B5a: LLM-путь подбора не затронут шагом B5a и живёт под ключом ──────────────
    //
    // С момента W3-B5b ключ этого пути — тоже арендатора и сужение кандидатов для
    // не-владельца включено (тесты ниже); здесь остаётся граница B5a: для владельца
    // (Role=Owner) сужения нет и путь доходит до сервиса (заглушка «[]» → candidates пуст,
    // 200); читателя отказывает [ProjectScope] до любой работы.

    [Fact]
    public async Task ПодборПривязок_НеЗатронутСужением_Читателю403ДоРаботы()
    {
        var (_, personaId) = await ArrangeAsync(Reader);

        var suggest = await SecondClient().PostAsync($"/api/personas/{personaId}/bindings/suggest",
            new StringContent("", Encoding.UTF8, "application/json"));
        ((int)suggest.StatusCode).Should().Be(403, "читателя отказывает [ProjectScope(Configure)] — до работы");
        Personas.Get(personaId, MemberId).Should().BeNull("отказ не тронул ничего");

        var ownerSuggest = await OwnerClient().PostAsync($"/api/personas/{personaId}/bindings/suggest",
            new StringContent("", Encoding.UTF8, "application/json"));
        ownerSuggest.EnsureSuccessStatusCode();
        (await BodyAsync(ownerSuggest)).GetProperty("candidates").EnumerateArray()
            .Should().BeEmpty("заглушка отвечает «[]» — путь дошёл до сервиса под ключом владельца");
    }

    // ── Существование не раскрываем: посторонний на любом пути привязок — 404 ───────────────────

    [Fact]
    public async Task Посторонний_НаЧтенииИЗаписиПривязок404()
    {
        var (_, personaId) = await ArrangeAsync(Outsider);

        (await SecondClient().GetAsync($"/api/personas/{personaId}/bindings"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        var (postStatus, _, _) = await SendAsync(SecondClient(), HttpMethod.Post,
            $"/api/personas/{personaId}/bindings", ToolBody("web"));
        postStatus.Should().Be(404, "404, а не 403: существование персоны не раскрываем");

        HasBinding(Personas.Get(personaId, TenantId)!, "tool", "web").Should().BeFalse("отказ — без записи");
    }

    // ── W3-B5b: binding-targets чужой проектной персоны — только цели этого проекта ───────────────
    //
    // Каталог строится под ключом арендатора и каждый его элемент проходит тот же
    // NonOwnerBindingEditDenied, что запись привязки: Notes — только источник этого проекта
    // (личной «personal» и чужих проектов владельца не видно), Project — только этот проект,
    // Tool — статический каталог без mcp:* (это право Configure), Knowledge — только датасет
    // проекта (в тестовом хосте датасета нет → пусто), Skill — глобальные без plugin:.
    // Без personaId — своё пространство вызывающего, как раньше.
    // Мутация: убрать фильтр TargetAllowed в BindingTargets — тест красный (редактор видит
    // «personal» и чужой проект); тест владельца ниже при этом зелёный — сужение до него
    // не доходит.

    private static List<string> IdsOf(JsonElement doc) =>
        doc.EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToList();

    // Второй проект того же владельца: валидная цель ValidateAsync(арендатор) — и чужая для
    // редактора. В его каталоге её быть не должно, в каталоге владельца — должна.
    private async Task<string> CreateOwnerProjectAsync(string tag)
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(_factory.TempDir, tag + "_" + Guid.NewGuid().ToString("N")[..8])).FullName;
        var created = await OwnerClient().PostAsJsonAsync("/api/projects",
            new { name = "ПроектКаталога" + Guid.NewGuid().ToString("N")[..6], rootPath = dir });
        created.EnsureSuccessStatusCode();
        return IdOf(await created.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task BindingTargets_РедакторЧужойПроектнойПерсоны_ТолькоЦелиЭтогоПроекта()
    {
        var (projectId, personaId) = await ArrangeAsync(Editor);
        var otherId = await CreateOwnerProjectAsync("pbt");

        var notes = IdsOf(await BodyAsync(await SecondClient().GetAsync(
            $"/api/personas/binding-targets?type=notes&personaId={personaId}")));
        notes.Should().Contain(projectId, "цель этого проекта законна участнику (И-4)");
        notes.Should().NotContain("personal", "личный vault владельца редактору не показывается");
        notes.Should().NotContain(otherId, "другие проекты владельца редактору не показываются");

        var projects = IdsOf(await BodyAsync(await SecondClient().GetAsync(
            $"/api/personas/binding-targets?type=project&personaId={personaId}")));
        projects.Should().BeEquivalentTo(new[] { projectId },
            "каталог проектов редактора — только проект персоны");

        var tools = IdsOf(await BodyAsync(await SecondClient().GetAsync(
            $"/api/personas/binding-targets?type=tool&personaId={personaId}")));
        tools.Should().Contain("web", "Tool без mcp: — разрешённая цель (матрица «Tool каталога»)");
        tools.Should().NotContain(t => t.StartsWith("mcp:", StringComparison.OrdinalIgnoreCase),
            "MCP-серверы живут в личном реестре владельца (право Configure)");

        var knowledge = IdsOf(await BodyAsync(await SecondClient().GetAsync(
            $"/api/personas/binding-targets?type=knowledge&personaId={personaId}")));
        knowledge.Should().BeEmpty(
            "датасета у проекта в тестовом хосте нет; личные базы владельца закрыты тем же методом");

        var skills = IdsOf(await BodyAsync(await SecondClient().GetAsync(
            $"/api/personas/binding-targets?type=skill&personaId={personaId}")));
        skills.Should().NotContain(s => s.StartsWith("plugin:", StringComparison.OrdinalIgnoreCase),
            "плагин-скиллы владельца не-владельцу закрыты (право Configure)");

        // Без personaId — своё пространство вызывающего, как раньше: свой личный vault виден,
        // личный проект владельца — нет.
        var own = IdsOf(await BodyAsync(await SecondClient().GetAsync(
            "/api/personas/binding-targets?type=notes")));
        own.Should().Contain("personal", "без personaId каталог — свой, поведение не меняется");
        own.Should().NotContain(otherId, "личный проект участнику не виден");
    }

    // ── W3-B5b: регрессии нет — владелец своей проектной персоне видит личные цели ───────────────

    [Fact]
    public async Task BindingTargets_ВладелецСвоейПроектнойПерсоне_ЛичныеЦелиНаМесте()
    {
        var (projectId, personaId) = await ArrangeAsync(Owner);
        var otherId = await CreateOwnerProjectAsync("pbw");

        var notes = IdsOf(await BodyAsync(await OwnerClient().GetAsync(
            $"/api/personas/binding-targets?type=notes&personaId={personaId}")));
        notes.Should().Contain("personal", "своей персоны владельца сужение не касается (И-4)");
        notes.Should().Contain(projectId).And.Contain(otherId, "оба своих проекта — в своём каталоге");

        var projects = IdsOf(await BodyAsync(await OwnerClient().GetAsync(
            $"/api/personas/binding-targets?type=project&personaId={personaId}")));
        projects.Should().Contain(projectId).And.Contain(otherId);
    }

    // ── W3-B5b: ИИ-подбор и генерация — редактору только разрешённые цели ────────────────────────
    //
    // Маркер «ТестовыйПодбор» в имени персоны включает стабу фиксированный набор кандидатов
    // (промпты подбора несут имя персоны): личные заметки владельца, несуществующая база
    // знаний, mcp-сервер, ключ статического каталога. Для редактора первые три отбиты
    // сужением ДО ответа; «web» остаётся — конвейер живой, тест не вакуумный. Для владельца
    // «personal» доходит до ответа (регрессии нет; mcp и knowledge у него отбиты валидацией —
    // их нет в тестовом хосте, это не этот шаг). suggest и generate идут по одному пути
    // обработки кандидатов — проверяются оба.
    // Мутация: снять [ProjectScope] с действий SuggestBindings/GenerateBindings — красный:
    // редактор получает 500 вместо 403 (без фильтра GetRequestScope бросает — fail-closed).

    private static List<string> CandidateKeys(JsonElement body) =>
        body.GetProperty("candidates").EnumerateArray()
            .Select(c => c.GetProperty("type").GetString() + "|" + c.GetProperty("target").GetString())
            .ToList();

    [Fact]
    public async Task ПодборИГенерацияПривязок_РедакторуТолькоРазрешённыеЦели()
    {
        var (_, personaId) = await ArrangeAsync(Editor, nameSuffix: NoLlm.Marker);

        // С 12.11 ИИ-подбор привязок — только владельцу: редактору отказывает фильтр до работы.
        var suggest = await SecondClient().PostAsync($"/api/personas/{personaId}/bindings/suggest",
            new StringContent("", Encoding.UTF8, "application/json"));
        ((int)suggest.StatusCode).Should().Be(403, "подбор привязок — Configure");
        var generate = await SecondClient().PostAsJsonAsync($"/api/personas/{personaId}/bindings/generate",
            new { prompt = NoLlm.Marker + " подбери что-нибудь полезное" });
        ((int)generate.StatusCode).Should().Be(403, "генерация привязок — Configure");

        var ownerGenerate = await BodyAsync(await OwnerClient().PostAsJsonAsync(
            $"/api/personas/{personaId}/bindings/generate",
            new { prompt = NoLlm.Marker + " подбери что-нибудь полезное" }));
        var ownerKeys = CandidateKeys(ownerGenerate);
        ownerKeys.Should().Contain("notes|personal", "своей персоны владельца сужение не касается (И-4)");
        ownerKeys.Should().Contain("tool|web");
    }

    // ── W3-B5b: «Применить типовые» — редактор не привязывает чужую персону к личным целям ────────
    //
    // Кодовый профиль библиотекаря (Knowledge+Notes) — обе цели подбирает AI. По маркеру в
    // имени стаб отдаёт «personal»-заметки и несуществующую базу знаний. Для редактора обе
    // чужие: applied=0, стор нетронут. Владелец тем же вызовом добавляет «personal» —
    // кандидаты были достижимы тем же конвейером, тест не вакуумный.
    // Мутация: снять [ProjectScope] с действия ApplyDefaultBindings — красный: редактор
    // получает 500 вместо 403 (тот же механизм: без фильтра GetRequestScope бросает).

    [Fact]
    public async Task ТиповыеУмения_РедакторНеДобавляетЛичныхЦелейВладельца()
    {
        var (_, personaId) = await ArrangeAsync(Editor, nameSuffix: NoLlm.Marker);
        // Специальность — поле владельца (W3-B3b, owner_settings). Правка специальности профиля не
        // материализует (точки материализации — создание персоны и кнопка «Применить типовые»):
        // здесь профиль материализует только сам вызов кнопки.
        (await OwnerClient().PutAsJsonAsync($"/api/personas/{personaId}", new { specialty = "librarian" }))
            .EnsureSuccessStatusCode();

        var (status, _, _) = await SendAsync(SecondClient(), HttpMethod.Post,
            $"/api/personas/{personaId}/bindings/apply-defaults");
        status.Should().Be(403, "«Применить типовые» — Configure: редактору отказывает фильтр до работы");
        var persona = Personas.Get(personaId, TenantId)!;
        (persona.Bindings?.Count ?? 0).Should().Be(0, "отказанный подбор не оставил следа в сторе");
        HasBinding(persona, "notes", "personal").Should().BeFalse("привязки к личной цели владельца нет (И-4)");

        var (ownerStatus, _, ownerBody) = await SendAsync(OwnerClient(), HttpMethod.Post,
            $"/api/personas/{personaId}/bindings/apply-defaults");
        (ownerStatus >= 200 && ownerStatus < 300).Should().BeTrue();
        JsonSerializer.Deserialize<JsonElement>(ownerBody).GetProperty("applied").GetInt32()
            .Should().Be(1, "своей персоны владельца сужения нет: «personal» записана");
        HasBinding(Personas.Get(personaId, TenantId)!, "notes", "personal").Should().BeTrue(
            "тот же конвейер без сужения доносит личную цель — различие только в сужении");
    }

    // ── W3-B5b доработка (ревью aaf13fce): создание — ИИ-подбор сужен не-владельцу ──────────────
    //
    // POST /api/personas (autoBindings) и quick-create несли роль вызывающего дальше, чем право
    // на запись: CreateAsync/TryAutoBindAsync принимали только TenantOwnerId, авто-подбор шел
    // с ролью по умолчанию Owner и писал созданной редактором персоне личные цели владельца
    // и mcp:* — через ход с этой персоной участник получал личные данные владельца. Теперь
    // роль и проект из области запроса доходят до подбора: редактор получает только
    // разрешённые цели (каталожный «web» записан — конвейер живой, тест не вакуумный),
    // владелец тем же вызовом — личную цель (регрессии нет).
    // Мутация: не передать callerRole/projectId в CreateAsync/TryAutoBindAsync (Owner по
    // умолчанию) — оба теста краснеют: «notes|personal» в сторе редактора (mcp/чужая база
    // отбивает и валидация — по ней различие не виден, по личной цели — виден).

    // Создание проектной персоны вызывающим с autoBindings: true и маркером в имени:
    // по маркеру стаб отдаёт фиксированный набор кандидатов (тот же, что у suggest-тестов).
    private async Task<string> CreateWithAutoBindingsAsync(HttpClient client, string projectId, string tag)
    {
        var created = await client.PostAsJsonAsync("/api/personas",
            new { name = tag + RunId() + NoLlm.Marker, scope = "project", projectId, autoBindings = true });
        created.EnsureSuccessStatusCode();
        return IdOf(await created.Content.ReadAsStringAsync());
    }

    private async Task<string> QuickCreateWithAutoBindingsAsync(HttpClient client, string projectId)
    {
        var created = await client.PostAsJsonAsync("/api/personas/ai/quick-create",
            new { prompt = NoLlm.Marker + " собери аналитика для проекта", scope = "project", projectId });
        created.EnsureSuccessStatusCode();
        return IdOf(await created.Content.ReadAsStringAsync());
    }

    // Итог авто-подбора владельца на свежесозданной персоне: личная цель на месте, конвейер живой.
    private void AssertAutoBindResult(string personaId)
    {
        var persona = Personas.Get(personaId, TenantId)!;
        HasBinding(persona, "notes", "personal").Should().BeTrue(
            "своего создания владельца сужение не касается (регрессии нет)");
        HasBinding(persona, "tool", "web").Should().BeTrue();
    }

    [Fact]
    public async Task СозданиеПроектнойПерсоныРедактором_403ВладелецСАвтоПодбором()
    {
        var (projectId, _) = await ArrangeAsync(Editor);

        var name = "АвтоРед" + RunId() + NoLlm.Marker;
        var (status, code, _) = await SendAsync(SecondClient(), HttpMethod.Post, "/api/personas",
            new { name, scope = "project", projectId, autoBindings = true });
        status.Should().Be(403, "создание проектной персоны — только владелец (Configure, решение 12.11)");
        code.Should().Be(NoRight);
        AssertNoPersonaNamed(projectId, name);

        AssertAutoBindResult(await CreateWithAutoBindingsAsync(OwnerClient(), projectId, "АвтоВлац"));
    }

    [Fact]
    public async Task КвикСозданиеПроектнойПерсоныРедактором_403ВладелецСАвтоПодбором()
    {
        var (projectId, _) = await ArrangeAsync(Editor);

        var (status, code, _) = await SendAsync(SecondClient(), HttpMethod.Post, "/api/personas/ai/quick-create",
            new { prompt = NoLlm.Marker + " собери аналитика для проекта", scope = "project", projectId });
        status.Should().Be(403, "quick-create проектной персоны — только владелец");
        code.Should().Be(NoRight);

        AssertAutoBindResult(await QuickCreateWithAutoBindingsAsync(OwnerClient(), projectId));
    }

    // ── W3-B5b доработка 2 (возврат дельта-ревью 7ace425a): явные привязки в теле создания ─────
    //
    // Доработка aaf13fce донесла роль вызывающего до ИИ-подбора при создании, но req.Bindings
    // того же тела (POST /api/personas) проходили только ValidateAsync: редактор создал бы
    // проектную персону с привязкой к личным заметкам владельца (200) — и через неё участник
    // читал бы его личные данные. Теперь каждый элемент тела идёт тем же NonOwnerBindingEditDenied
    // ДО валидации — тот же порядок отказа, что у AddBinding: 403 по праву не зависит от валидности
    // цели (mcp-сервера в реестре тестового хоста нет — без сужения был бы 400 «не найден»).
    // Отказ — до personas.Create: полусозданной персоны в команде проекта быть не должно.

    private async Task<(int Status, string Body)> CreateWithBindingsAsync(HttpClient client,
        string projectId, string name, object[] bindings)
    {
        var (status, _, body) = await SendAsync(client, HttpMethod.Post, "/api/personas",
            new { name, scope = "project", projectId, bindings });
        return (status, body);
    }

    private void AssertNoPersonaNamed(string projectId, string name) =>
        Personas.GetProjectTeam(TenantId, projectId).Any(p => p.Name == name).Should().BeFalse(
            "отказ по праву решается до personas.Create — полусозданной персоны быть не должно");

    [Fact]
    public async Task СозданиеПроектнойПерсоныРедактором_ЯвныеПривязкиЧужихЦелейОтказаныБезСоздания()
    {
        var (projectId, _) = await ArrangeAsync(Editor);

        var personalName = "ЯвнаяЛичнаяРед" + RunId();
        var (personalStatus, _) = await CreateWithBindingsAsync(SecondClient(), projectId,
            personalName, [NotesBody("personal")]);
        personalStatus.Should().Be(403, "создание проектной персоны — Configure: отказ фильтра до тела");
        AssertNoPersonaNamed(projectId, personalName);

        var mcpName = "ЯвнаяMcpРед" + RunId();
        var (mcpStatus, _) = await CreateWithBindingsAsync(SecondClient(), projectId,
            mcpName, [ToolBody("mcp:" + RunId())]);
        mcpStatus.Should().Be(403, "тот же отказ фильтра, без 400 «нет в реестре»");
        AssertNoPersonaNamed(projectId, mcpName);
    }

    [Fact]
    public async Task СозданиеПроектнойПерсоныВладельцем_ЯвнаяЛичнаяПривязкаЗаписана()
    {
        var (projectId, _) = await ArrangeAsync(Editor);

        var (status, body) = await CreateWithBindingsAsync(OwnerClient(), projectId,
            "ЯвнаяЛичнаяВлац" + RunId(), [NotesBody("personal")]);
        (status >= 200 && status < 300).Should().BeTrue("своего создания владельца сужение не касается");
        HasBinding(Personas.Get(IdOf(body), TenantId)!, "notes", "personal").Should().BeTrue(
            "личная привязка на месте: различие — только в сужении, цель валидна и раньше");
    }
}
