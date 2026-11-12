using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Memory;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// W3-B1c (разрез docs/research/shared-projects-personas-cut-2026-11.md §3.1 строки B1 с RP(Read),
/// §5 п.3; И-1, И-2, И-3): read-пути {id}-действий персон и действия владельца ключуются
/// арендатором, а не вызывающим.
///
/// Матрица «роль × маршрут» на настоящем сервере (образец — SharedProjectTaskWriteRightsTests):
/// проектная персона общего проекта видна владельцу, редактору и читателю на всех read-путях —
/// самой персоне, аватаре, оригинале, кандидате, привязках, автоматике, памяти, поиске, recall,
/// фокусе; постороннему — 404 на любом {id}: существование не раскрываем. DELETE и make-default —
/// Configure (только владелец, разрез §2): редактору и читателю — 403 с машинным кодом
/// project_scope, персона и руководитель проекта остаются на месте; постороннему — 404.
/// И-3: глобальная персона владельца участнику не видна никогда — 404 по любому {id}, включая
/// аватар существующего файла.
///
/// Проверка — состояние хранилища, а не только код ответа: тела контроллера читают и пишут
/// персону под scope.TenantOwnerId (И-2) — поэтому записанные под ключом владельца память и
/// фокус видны участнику (с ключом вызывающего чтение вернуло бы пустой стор), а отказанное
/// действие не оставляет следа. Мутации, на которых матрица краснеет: вернуть в Get
/// `_personas.Get(id, UserId)` или снять с Get атрибут [ProjectScope(From = Persona)].
///
/// W3-B1d (тот же файл, §3.1 строки B1 с RP(WriteContent)/RP(Configure)): вторая матрица —
/// write-пути (память, фокус, автоматика). Запись идёт под ключом арендатора: редактор пишет
/// в тот же стор, который читает владелец (с ключом вызывающего персону не нашёл бы ни гейт
/// тела, ни валидация сервиса — записи не было бы вовсе), читатель отказан 403 и не оставляет
/// следа. Гонка: ParallelMemoryWrite_OwnerAndEditor_BothEntriesKept — две одновременные
/// записи остаются обе. Мутации: вернуть в Remember `_personas.Get(id, UserId)` — строка
/// «запись памяти» краснеет для Editor. Снятие блокировки в Remember тест гонки не покраснил
/// (два прогона, копия мутации): критическая секция короткая, два запроса в неё не попали —
/// разбор в отчёте задачи.
///
/// W3-B3a (тот же файл, §3.1 строки «POST /», «POST /ai/quick-create», «POST /ai/team»): третья
/// матрица — создание проектной персоны. [ProjectScope] на этих действиях невозможен (id проекта
/// не в маршруте), право решает IProjectAccess.Resolve в теле, ключ — TenantOwnerId. Ожидания те
/// же, что у матрицы {id}-путей: редактор — 2xx, и OwnerId новой персоны — владелец проекта
/// (с ключом вызывающего ValidProject отбил бы ему 400, а персона досталась бы ему самому);
/// читатель — 403 project_scope, посторонний — 404; отказанное создание не оставляет ни
/// полусозданной персоны, ни следа в сторе. Глобальную персону участник создаёт в своём
/// пространстве (последний тест). Мутации (проверены на копиях дерева): вернуть в Create ключ
/// вызывающего (tenant = area.CallerUserId) — строка «создать проектную» краснеет для Editor
/// (400 вместо 2xx: ValidProject не пропускает чужой проект); вернуть в AiTeam ключ вызывающего
/// (AiTeamAsync(…, area.CallerUserId)) — краснеет строка «ai-команда проекта» для Editor
/// (400 «Проект не найден» из сверки project.OwnerId с первым аргументом, PersonasCrudService.cs:444).
///
/// W3-B3b (тот же файл, §3.1 строки «PUT /{id}», «PUT {id}/voice», «POST {id}/avatar/select»):
/// четвёртая матрица — правка карточки. Поля зоны и денег (Scope/ProjectId/SphereId, профиль
/// доступа, модель/уровень/усилие/матрица) — право владельца: редактору — 403 с кодом
/// owner_settings до записи и без частичной записи прочих полей DTO; поле, принесённое без
/// изменения, отказом не считается (форма шлёт карточку целиком). Содержание (имя, тон) и
/// голос/аватар — WriteContent: редактор пишет под ключом арендатора, читатель отказан
/// фильтром (project_scope). Гонка ParallelUpdate_OwnerAndEditor_SinglePersonaOwnerKept:
/// обе правки одной персоны под _saveLock, запись одна, OwnerId не изменён. Мутации (проверены
/// на копиях дерева): убрать сверку ProjectId в ChangesOwnerSettings — краснеет строка «сменить
/// зону» для Editor (200 вместо 403, зона бы уехала в чужой проект); вернуть ключ вызывающего
/// в Update/SetVoice/SelectAvatar — краснеют строки «править имя», «голос», «выбор аватара»
/// для Editor (404: персоны под его ключом нет).
///
/// Доработка по ревью e6029e19 (W3-B3b returned, contract): профиль доступа — не только поле
/// Access: DisallowedTools, Tools и Specialty — его части, и гейт сверяет их со ЭФФЕКТИВНЫМ
/// значением записи (те же нормализаторы, что у писателя). Строки матрицы: «сменить профиль
/// доступа», «сменить зону», «сменить уровень модели», «сменить ячейку матрицы» и два обхода
/// прежнего гейта: «очистить запреты при неизменённом доступе» (Access тот же, меняется только
/// DisallowedTools) и «сменить специальность» (шаблон специальности даёт Access/Tools из
/// шаблона при неизменном Access в теле). Мутации: снять сверку DisallowedTools — краснеет
/// строка «очистить запреты» для Editor; снять сверку Specialty — краснеет «сменить
/// специальность» для Editor (обе — 200 вместо 403).
/// </summary>
[Trait("Category", "Host")]
public class SharedProjectPersonaRightsTests : IClassFixture<TestWebApplicationFactory>
{
    private const string Owner = "owner", Editor = "editor", Reader = "reader", Outsider = "outsider";

    // 200 в ожиданиях значит «любой 2xx»
    private const int Ok2xx = 200;

    private const string NoRight = ProjectScopeAttribute.ErrorCode;

    private readonly TestWebApplicationFactory _factory;

    // Управляемая заглушка дешёвой модели для LLM-путей создания (W3-B3a: quick-create, ai/team):
    // на эти две action'ы — ответ текущего теста, всё прочее — «[]», как во встроенном стабе хоста,
    // поэтому существующие тесты класса видят ровно то же, что без заглушки (фоновые one-shot
    // создания — типовые умения, авто-привязки, голос — остаются «ничего не подошло»). STATIC:
    // экземпляр класса создаётся на каждый тест, а хост строится один раз на первом — раннер
    // обязан быть тем же объектом, что зарегистрирован в хосте (тот же приём, что
    // SharedProjectPersonaListTests).
    private static readonly CreateLlm _llm = new();

    public SharedProjectPersonaRightsTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
        _factory.ExtraServices = services => services.AddSingleton<ICheapTextRunner>(_llm);
    }

    /// <summary>Заглушка one-shot: Answer — ответ текущего теста; Owners — ключи, под которыми
    /// шли вызовы (проверка И-2 на LLM-пути создания: черновик проектной персоны оплачивает
    /// владелец проекта, а не вызывающий участник).</summary>
    private sealed class CreateLlm : ICheapTextRunner
    {
        public string Answer { get; set; } = "[]";
        public readonly List<string?> Owners = [];

        // Состояние каждого теста начинается заново (методы одного класса xUnit гоняет
        // последовательно): ответ — пустой, как у встроенного стаба хоста, ключи — чистые.
        public void Reset()
        {
            Answer = "[]";
            Owners.Clear();
        }

        public bool UsesLocal(string actionKey) => false;
        public bool HasFreeRoute(string actionKey) => false;
        public bool HasRoute(string actionKey) => false;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "stub";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            Owners.Add(ownerId);
            return Task.FromResult(Answer);
        }

        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default)
        {
            Owners.Add(ownerId);
            return Task.FromResult<string?>(Answer);
        }

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

    // Второй пользователь — участник общего проекта (Editor/Reader) или посторонний (Outsider
    // не приглашён): его личный ключ нужен, чтобы проверить «персона не легла в вызывающего».
    private string MemberId => _factory.Services.GetRequiredService<UserStore>()
        .FindByUsername(TestWebApplicationFactory.SecondUsername)!.Id;

    // Стор персон на диске: DataPath тестового хоста — TempDir (TestWebApplicationFactory.cs:115),
    // PersonasPath не переопределён → personas.json рядом с projects.json.
    private string StorePath => Path.Combine(_factory.TempDir, "personas.json");

    private PersonaManager Personas => _factory.Services.GetRequiredService<PersonaManager>();
    private ProjectManager Projects => _factory.Services.GetRequiredService<ProjectManager>();
    private PersonaMemoryService Memory => _factory.Services.GetRequiredService<PersonaMemoryService>();
    // Личный vault владельца — проверка «заметка появилась / не появилась» (to-note, from-note)
    private INoteAccessor Notes => _factory.Services.GetRequiredService<INoteAccessor>();

    private static async Task SetFlagAsync(HttpClient client) =>
        (await client.PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = true }))
            .EnsureSuccessStatusCode();

    private static string IdOf(string json) =>
        JsonSerializer.Deserialize<JsonElement>(json).GetProperty("id").GetString()!;

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage r) =>
        JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync());

    // Минимальный «PNG»: валидная сигнатура + произвольный хвост — чтение файла идёт по
    // существованию и расширению, декодирование здесь не нужно (миниатюру не просим)
    private static byte[] FakePng() =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5, 6, 7, 8];

    /// <summary>Проект владельца с проектной персоной в нём; второй пользователь — участник роли role
    /// (Owner/Outsider — участников нет). Персона снабжена аватаром (image + original), файлом
    /// кандидата и записью памяти — всё под ключом владельца: чтение по ключу арендатора обязано
    /// достать их, а не дать пустой стор или 404 «нет файла».</summary>
    private async Task<(string ProjectId, string PersonaId)> ArrangeAsync(string role)
    {
        var owner = OwnerClient();
        await SetFlagAsync(owner);
        if (role is Editor or Reader) await SetFlagAsync(SecondClient());

        var dir = Directory.CreateDirectory(Path.Combine(_factory.TempDir, "prw_" + Guid.NewGuid().ToString("N")[..8])).FullName;
        var created = await owner.PostAsJsonAsync("/api/projects",
            new { name = "ЧтениеПерсон" + Guid.NewGuid().ToString("N")[..6], rootPath = dir });
        created.EnsureSuccessStatusCode();
        var projectId = IdOf(await created.Content.ReadAsStringAsync());

        if (role is Editor or Reader)
            (await owner.PostAsJsonAsync($"/api/projects/{projectId}/members",
                new { username = TestWebApplicationFactory.SecondUsername, role })).EnsureSuccessStatusCode();

        var persona = await owner.PostAsJsonAsync("/api/personas",
            new { name = "Командир" + Guid.NewGuid().ToString("N")[..6], scope = "project", projectId });
        persona.EnsureSuccessStatusCode();
        var personaId = IdOf(await persona.Content.ReadAsStringAsync());

        // Запись аватара — не предмет шага (W3-B3b): прямая запись стора под ключом владельца +
        // файлы. Read-пути обязаны находить настоящие файлы.
        Personas.SetAvatarUploaded(personaId, TenantId, "avatar-arr.png", "original-arr.png", null);
        var assetDir = Path.Combine(Personas.AssetsDir, personaId);
        Directory.CreateDirectory(Path.Combine(assetDir, "candidates"));
        File.WriteAllBytes(Path.Combine(assetDir, "avatar-arr.png"), FakePng());
        File.WriteAllBytes(Path.Combine(assetDir, "original-arr.png"), FakePng());
        File.WriteAllBytes(Path.Combine(assetDir, "candidates", "cand.png"), FakePng());

        // Память — под ключом арендатора: с ключом вызывающего List вернул бы пустой список
        // и статусная матрица не увидела бы разницы.
        await Memory.RememberAsync(TenantId, personaId, PersonaMemoryType.Semantic, "Факт проекта", null, null);

        return (projectId, personaId);
    }

    private static async Task<(int Status, string? Code)> SendAsync(HttpClient client, HttpMethod method, string url,
        object? body = null)
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
                // тело отказа — объект с code; тела списков (массив) кода не имеют
                if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("code", out var c)) code = c.GetString();
            }
            catch (JsonException) { /* ответ не JSON — кода нет */ }
        }
        return ((int)response.StatusCode, code);
    }

    // Тот же разбор ответа, что SendAsync, плюс тело: принятые LLM-пути создания проверяются по
    // содержимому ответа (состав команды), а не только по статусу.
    private static async Task<(int Status, string? Code, JsonElement Body)> SendBodyAsync(HttpClient client,
        string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        var text = await response.Content.ReadAsStringAsync();
        string? code = null;
        JsonElement json = default;
        if (text.Length > 0)
        {
            try
            {
                json = JsonSerializer.Deserialize<JsonElement>(text);
                if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("code", out var c)) code = c.GetString();
            }
            catch (JsonException) { /* ответ не JSON (text/plain) — кода и тела нет */ }
        }
        return ((int)response.StatusCode, code, json);
    }

    /// <summary>Действие матрицы: {p} — id проектной персоны проекта владельца. Ожидания по ролям.</summary>
    private sealed record Act(string Name, HttpMethod Method, string Path,
        int ForOwner, int ForEditor, int ForReader, int ForOutsider)
    {
        public int For(string role) => role switch
        {
            Owner => ForOwner, Editor => ForEditor, Reader => ForReader, _ => ForOutsider,
        };
    }

    private static readonly Act[] Actions =
    [
        new("читать персону", HttpMethod.Get, "/api/personas/{p}", Ok2xx, Ok2xx, Ok2xx, 404),
        new("аватар", HttpMethod.Get, "/api/personas/{p}/avatar", Ok2xx, Ok2xx, Ok2xx, 404),
        new("оригинал аватара", HttpMethod.Get, "/api/personas/{p}/avatar/original", Ok2xx, Ok2xx, Ok2xx, 404),
        new("кандидат аватара", HttpMethod.Get, "/api/personas/{p}/avatar/candidate/cand.png", Ok2xx, Ok2xx, Ok2xx, 404),
        new("привязки", HttpMethod.Get, "/api/personas/{p}/bindings", Ok2xx, Ok2xx, Ok2xx, 404),
        new("автоматика", HttpMethod.Get, "/api/personas/{p}/automation", Ok2xx, Ok2xx, Ok2xx, 404),
        new("память", HttpMethod.Get, "/api/personas/{p}/memory", Ok2xx, Ok2xx, Ok2xx, 404),
        // q не пустой — пустой запрос это 400 тела, не предмет матрицы
        new("поиск памяти", HttpMethod.Get, "/api/personas/{p}/memory/search?q=%D0%A4%D0%B0%D0%BA%D1%82", Ok2xx, Ok2xx, Ok2xx, 404),
        new("recall памяти", HttpMethod.Get, "/api/personas/{p}/memory/recall?q=%D0%A4%D0%B0%D0%BA%D1%82", Ok2xx, Ok2xx, Ok2xx, 404),
        // фокуса нет — 204; постороннему — 404, а не 204: существование не раскрываем
        new("фокус", HttpMethod.Get, "/api/personas/{p}/focus", 204, 204, 204, 404),
        // Configure-действия — владельцу (разрез §2): участнику 403 с кодом, персона на месте
        new("удалить", HttpMethod.Delete, "/api/personas/{p}", 204, 403, 403, 404),
        new("сделать руководителем", HttpMethod.Post, "/api/personas/{p}/make-default", Ok2xx, 403, 403, 404),
    ];

    public static IEnumerable<object[]> Matrix() =>
        from a in Actions
        from role in new[] { Owner, Editor, Reader, Outsider }
        select new object[] { a.Name, role };

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task РольНаПутиПерсоны_ДаётОжидаемыйОтказ(string actionName, string role)
    {
        var action = Actions.Single(a => a.Name == actionName);
        var (projectId, personaId) = await ArrangeAsync(role);

        var (status, code) = await SendAsync(ClientOf(role), action.Method, action.Path.Replace("{p}", personaId));

        var expected = action.For(role);
        if (expected == Ok2xx)
        {
            (status >= 200 && status < 300).Should().BeTrue($"{role}: {actionName} → {status}");
        }
        else
        {
            status.Should().Be(expected, $"{role}: {actionName}");
            if (expected == 403)
                code.Should().Be(NoRight, $"{role}: {actionName} — отказ различается машинным кодом");
        }

        // Состояние хранилища после полного запроса: отказанное действие не трогает персону и
        // руководителя, действие владельца отражено в сторе.
        switch (actionName, role)
        {
            case ("удалить", Owner):
                Personas.Get(personaId, TenantId).Should().BeNull("владелец удалил персону");
                break;
            case ("удалить", _):
                Personas.Get(personaId, TenantId).Should().NotBeNull($"{role}: отказанное удаление не тронуло персону");
                break;
            case ("сделать руководителем", Owner):
                Projects.GetById(projectId)!.DefaultPersonaId.Should().Be(personaId,
                    "руководитель записан под ключом арендатора: MakeDefaultAsync читает персону и проект по первому аргументу");
                break;
            case ("сделать руководителем", _):
                Personas.Get(personaId, TenantId).Should().NotBeNull($"{role}: персона на месте");
                Projects.GetById(projectId)!.DefaultPersonaId.Should().NotBe(personaId,
                    $"{role}: отказанный make-default не назначил руководителя");
                break;
        }
    }

    // ── И-2: память и фокус проектной персоны под ключом арендатора — участник видит содержимое ──

    [Fact]
    public async Task ПамятьИФокусПроектнойПерсоныПодКлючомАрендатора_УчастникВидитСодержимое()
    {
        var (_, personaId) = await ArrangeAsync(Editor);
        // Фокус — тоже под ключом владельца (write-путь фокуса — W3-B1d, здесь — прямая запись стора)
        Memory.SetFocus(TenantId, personaId, "Работа проекта", "В процессе", "Следующий шаг", null);

        var mem = await BodyAsync(await SecondClient().GetAsync($"/api/personas/{personaId}/memory"));
        mem.EnumerateArray().Select(e => e.GetProperty("text").GetString())
            .Should().Contain("Факт проекта",
                "память проектной персоны лежит под ключом арендатора: с ключом вызывающего чтение вернуло бы пустой список");

        var search = await BodyAsync(await SecondClient().GetAsync($"/api/personas/{personaId}/memory/search?q=факт"));
        search.EnumerateArray().Select(h => h.GetProperty("text").GetString())
            .Should().Contain("Факт проекта", "поиск идёт по тому же стору");

        var focus = await SecondClient().GetAsync($"/api/personas/{personaId}/focus");
        focus.StatusCode.Should().Be(HttpStatusCode.OK);
        (await BodyAsync(focus)).GetProperty("what").GetString().Should().Be("Работа проекта",
            "фокус проектной персоны — в сторе памяти владельца, не вызывающего");
    }

    // ── И-3: глобальная персона владельца участнику не видна ни по одному {id} ────────────────

    [Fact]
    public async Task ГлобальнаяПерсонаВладельца_Участнику404НаЧтенииИНаАватареСуществующегоФайла()
    {
        await ArrangeAsync(Editor); // общий проект владельца с редактором-участником

        // Личная (глобальная) персона владельца с настоящим аватаром: отказ обязан идти по
        // фильтру (И-3), а не по отсутствию персоны или файла
        var globalId = IdOf(await (await OwnerClient().PostAsJsonAsync("/api/personas",
            new { name = "Личный" + Guid.NewGuid().ToString("N")[..6] })).Content.ReadAsStringAsync());
        Personas.SetAvatarUploaded(globalId, TenantId, "avatar-gl.png", "original-gl.png", null);
        var assetDir = Directory.CreateDirectory(Path.Combine(Personas.AssetsDir, globalId)).FullName;
        File.WriteAllBytes(Path.Combine(assetDir, "avatar-gl.png"), FakePng());
        File.WriteAllBytes(Path.Combine(assetDir, "original-gl.png"), FakePng());

        (await SecondClient().GetAsync($"/api/personas/{globalId}")).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "И-3: глобальная персона владельца участнику не видна никогда — как несуществующая");
        (await SecondClient().GetAsync($"/api/personas/{globalId}/avatar")).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "существующий файл аватара персону не делает видимой");

        // Для самого владельца оба пути открыты: проверка не вакуумная
        (await OwnerClient().GetAsync($"/api/personas/{globalId}")).EnsureSuccessStatusCode();
        (await OwnerClient().GetAsync($"/api/personas/{globalId}/avatar")).EnsureSuccessStatusCode();
    }

    // ── W3-B1d: write-пути {id}-действий — запись под ключом арендатора (§3.1 строки B1
    // с RP(WriteContent)/RP(Configure)) ──────────────────────────────────────────────────────
    //
    // Матрица та же, что для read-путей, но для записывающих действий: редактор (WriteContent)
    // пишет в тот же стор, который читает владелец; с ключом вызывающего персону не нашёл бы ни
    // гейт тела, ни валидация сервиса — записи не было бы вовсе (404). Читатель (права нет)
    // отказан фильтром — 403 с машинным кодом, запись не оставляет следа; Configure-пути
    // (to-note/from-note/автоматика) — только владелец. Состояние стора проверяется после
    // полного цикла. LLM-пути автоматики (/suggest, /generate) здесь не проверяются: для
    // владельца они доходят до тела и зависят от провайдера — результат не детерминирован;
    // для отказанных ролей отказ решает фильтр (тот же код, что в строках матрицы).
    // Правила в фикстурах заводятся выключенными: ручной прогон правила (FireAsync) гасится
    // на проверке Enabled до создания чата — фоновый агентский ход в тесте не заводится.

    private sealed record WriteAct(string Name, HttpMethod Method, string Path,
        Func<string, Dictionary<string, string>, object?>? Body,
        int ForOwner, int ForEditor, int ForReader, int ForOutsider)
    {
        public int For(string role) => role switch
        {
            Owner => ForOwner, Editor => ForEditor, Reader => ForReader, _ => ForOutsider,
        };
    }

    // Уникальный текст каждого прогона: стор памяти и vault класса общие (одна фабрика)
    private static string RunId() => Guid.NewGuid().ToString("N")[..8];

    private static readonly WriteAct[] WriteActions =
    [
        new("запись памяти", HttpMethod.Post, "/api/personas/{p}/memory",
            (r, _) => new { type = "semantic", text = "Запись " + r }, Ok2xx, Ok2xx, 403, 404),
        new("правка записи", HttpMethod.Put, "/api/personas/{p}/memory/{e}",
            (r, _) => new { text = "Изменено " + r }, Ok2xx, Ok2xx, 403, 404),
        new("подтвердить запись", HttpMethod.Post, "/api/personas/{p}/memory/{e}/confirm", null, Ok2xx, Ok2xx, 403, 404),
        new("забыть запись", HttpMethod.Delete, "/api/personas/{p}/memory/{e}", null, 204, 204, 403, 404),
        new("сбросить фокус", HttpMethod.Delete, "/api/personas/{p}/focus", null, 204, 204, 403, 404),
        new("в заметку", HttpMethod.Post, "/api/personas/{p}/memory/{e}/to-note", null, Ok2xx, 403, 403, 404),
        new("из заметки", HttpMethod.Post, "/api/personas/{p}/memory/from-note",
            (_, ids) => new { noteId = ids["n"] }, Ok2xx, 403, 403, 404),
        new("запись автоматики", HttpMethod.Post, "/api/personas/{p}/automation",
            (r, _) => new { name = "Правило " + r, enabled = false, actionInstruction = "Сказать слово" },
            Ok2xx, 403, 403, 404),
        new("изменить правило", HttpMethod.Put, "/api/personas/{p}/automation/{a}",
            (r, _) => new { actionInstruction = "Изменено " + r }, Ok2xx, 403, 403, 404),
        new("удалить правило", HttpMethod.Delete, "/api/personas/{p}/automation/{a}", null, 204, 403, 403, 404),
        new("прогнать правило", HttpMethod.Post, "/api/personas/{p}/automation/{a}/test", null, 202, 403, 403, 404),
        // W3-B3b (разрез §3.1 строки «PUT {id}/voice», «POST {id}/avatar/select»): WriteContent-пути
        // карточки — те же ожидания, что у памяти: отказ читателя решает фильтр (project_scope).
        // Голос валиден по TtsVoiceCatalog: «sofia» с амплуа «casual» (TtsVoiceCatalogTests.cs:144).
        new("голос", HttpMethod.Put, "/api/personas/{p}/voice",
            (_, _) => new { voice = "sofia", role = "casual" }, Ok2xx, Ok2xx, 403, 404),
        new("выбор аватара", HttpMethod.Post, "/api/personas/{p}/avatar/select",
            (_, _) => new { file = "cand.png" }, Ok2xx, Ok2xx, 403, 404),
    ];

    // Фикстура действия — прямая запись стора под ключом владельца: проверяемому действию должно
    // на что писать (запись, правило, заметка, фокус) и чем проверять результат
    private async Task<Dictionary<string, string>> PrepareWriteFixtureAsync(string actionName, string personaId, string runId)
    {
        var ids = new Dictionary<string, string>();
        switch (actionName)
        {
            case "правка записи" or "забыть запись":
                ids["e"] = Memory.List(TenantId, personaId, null).First(e => e.Text == "Факт проекта").Id;
                break;
            case "подтвердить запись":
                ids["e"] = (await Memory.RememberAsync(TenantId, personaId, PersonaMemoryType.Episodic,
                    "Предложено " + runId, null, null, pending: true))!.Id;
                break;
            case "в заметку":
                ids["e"] = (await Memory.RememberAsync(TenantId, personaId, PersonaMemoryType.Semantic,
                    "Запись в заметку " + runId, null, null))!.Id;
                break;
            case "сбросить фокус":
                Memory.SetFocus(TenantId, personaId, "Фокус " + runId, "В процессе", "Следующий шаг", null);
                break;
            case "из заметки":
                ids["n"] = Notes.Create(TenantId, new CreateNoteRequest(
                    Title: "Заметка " + runId, Content: "Содержимое заметки " + runId)).Id;
                break;
            case "изменить правило" or "удалить правило" or "прогнать правило":
                var created = await OwnerClient().PostAsJsonAsync($"/api/personas/{personaId}/automation",
                    new { name = "Правило " + runId, enabled = false, actionInstruction = "Сказать слово" });
                created.EnsureSuccessStatusCode();
                ids["a"] = (await BodyAsync(created)).GetProperty("id").GetString()!;
                break;
        }
        return ids;
    }

    public static IEnumerable<object[]> WriteMatrix() =>
        from a in WriteActions
        from role in new[] { Owner, Editor, Reader, Outsider }
        select new object[] { a.Name, role };

    [Theory]
    [MemberData(nameof(WriteMatrix))]
    public async Task ЗаписьНаПутиПерсоны_ДаётОжидаемыйОтказИПишетПодКлючомАрендатора(string actionName, string role)
    {
        var action = WriteActions.Single(a => a.Name == actionName);
        var (_, personaId) = await ArrangeAsync(role);
        var runId = RunId();
        var ids = await PrepareWriteFixtureAsync(actionName, personaId, runId);

        var url = action.Path.Replace("{p}", personaId);
        foreach (var (key, value) in ids) url = url.Replace("{" + key + "}", value);

        var (status, code) = await SendAsync(ClientOf(role), action.Method, url, action.Body?.Invoke(runId, ids));

        var expected = action.For(role);
        if (expected == Ok2xx)
        {
            (status >= 200 && status < 300).Should().BeTrue($"{role}: {actionName} → {status}");
        }
        else
        {
            status.Should().Be(expected, $"{role}: {actionName}");
            if (expected == 403)
                code.Should().Be(NoRight, $"{role}: {actionName} — отказ различается машинным кодом");
        }

        // Состояние стора после полного цикла: разрешённое записано под ключом владельца и видно
        // из его чтения, отказанное оставило всё как было
        var entries = Memory.List(TenantId, personaId, null);
        var rules = Personas.Get(personaId, TenantId)!.AutomationRules ?? [];
        switch (actionName, role)
        {
            case ("запись памяти", Owner or Editor):
                entries.Select(e => e.Text).Should().Contain("Запись " + runId,
                    $"{role}: запись легла в тот же стор, который читает владелец");
                break;
            case ("запись памяти", _):
                entries.Select(e => e.Text).Should().NotContain("Запись " + runId,
                    $"{role}: отказанная запись не оставила следа");
                break;
            case ("правка записи", Owner or Editor):
                entries.Single(e => e.Id == ids["e"]).Text.Should().Be("Изменено " + runId,
                    $"{role}: правка записана в сторе владельца");
                break;
            case ("правка записи", _):
                entries.Single(e => e.Id == ids["e"]).Text.Should().Be("Факт проекта",
                    $"{role}: отказанная правка не тронула запись");
                break;
            case ("подтвердить запись", Owner or Editor):
                entries.Single(e => e.Id == ids["e"]).Pending.Should().BeFalse($"{role}: запись подтверждена");
                break;
            case ("подтвердить запись", _):
                entries.Single(e => e.Id == ids["e"]).Pending.Should().BeTrue(
                    $"{role}: отказанный confirm оставил запись pending");
                break;
            case ("забыть запись", Owner or Editor):
                entries.Any(e => e.Id == ids["e"]).Should().BeFalse($"{role}: запись удалена");
                break;
            case ("забыть запись", _):
                entries.Any(e => e.Id == ids["e"]).Should().BeTrue($"{role}: отказанное удаление не тронуло запись");
                break;
            case ("сбросить фокус", Owner or Editor):
                Memory.GetFocus(TenantId, personaId).Should().BeNull($"{role}: фокус сброшен");
                break;
            case ("сбросить фокус", _):
                Memory.GetFocus(TenantId, personaId)?.What.Should().Be("Фокус " + runId,
                    $"{role}: отказанный сброс не тронул фокус");
                break;
            case ("в заметку", Owner):
                Notes.GetSummaries(TenantId, null, null).Any(s => s.Title.Contains(runId)).Should().BeTrue(
                    "заметка из записи появилась в личном vault владельца");
                break;
            case ("в заметку", _):
                Notes.GetSummaries(TenantId, null, null).Any(s => s.Title.Contains(runId)).Should().BeFalse(
                    $"{role}: отказанный to-note не создал заметку в vault владельца");
                break;
            case ("из заметки", Owner):
                entries.Select(e => e.Text).Should().Contain("Содержимое заметки " + runId,
                    "заметка записана в память персоны");
                break;
            case ("из заметки", _):
                entries.Select(e => e.Text).Should().NotContain("Содержимое заметки " + runId,
                    $"{role}: отказанный from-note ничего не записал");
                break;
            case ("запись автоматики", Owner):
                rules.Any(r => r.Name == "Правило " + runId).Should().BeTrue("правило записано под ключом владельца");
                break;
            case ("запись автоматики", _):
                rules.Any(r => r.Name == "Правило " + runId).Should().BeFalse($"{role}: отказанное правило не родилось");
                break;
            case ("изменить правило", Owner):
                rules.Single(r => r.Id == ids["a"]).Action.Instruction.Should().Be("Изменено " + runId,
                    "правка правила записана в сторе владельца");
                break;
            case ("изменить правило", _):
                rules.Single(r => r.Id == ids["a"]).Action.Instruction.Should().Be("Сказать слово",
                    $"{role}: отказанная правка не тронула правило");
                break;
            case ("удалить правило", Owner):
                rules.Any(r => r.Id == ids["a"]).Should().BeFalse("правило удалено");
                break;
            case ("удалить правило", _):
                rules.Any(r => r.Id == ids["a"]).Should().BeTrue($"{role}: отказанное удаление не тронуло правило");
                break;
            case ("прогнать правило", _):
                rules.Any(r => r.Id == ids["a"]).Should().BeTrue($"{role}: отказанный прогон оставил правило на месте");
                break;
            case ("голос", Owner or Editor):
                Personas.Get(personaId, TenantId)!.Voice?.Voice.Should().Be("sofia",
                    $"{role}: голос записан под ключом арендатора");
                break;
            case ("голос", _):
                Personas.Get(personaId, TenantId)!.Voice.Should().BeNull($"{role}: отказанный голос ничего не записал");
                break;
            case ("выбор аватара", Owner or Editor):
                var chosen = Personas.Get(personaId, TenantId)!.Avatar.ImageFile;
                chosen.Should().NotBe("avatar-arr.png", $"{role}: кандидат стал аватаром — новым файлом");
                Directory.Exists(Path.Combine(Personas.AssetsDir, personaId, "candidates")).Should().BeFalse(
                    $"{role}: папка кандидатов вычищена (SelectAvatarAsync, PersonasCrudService.cs:594)");
                break;
            case ("выбор аватара", _):
                Personas.Get(personaId, TenantId)!.Avatar.ImageFile.Should().Be("avatar-arr.png",
                    $"{role}: отказанный выбор не тронул аватар");
                File.Exists(Path.Combine(Personas.AssetsDir, personaId, "candidates", "cand.png")).Should().BeTrue(
                    $"{role}: отказанный выбор не съел кандидата");
                break;
        }
    }

    // ── W3-B1d: гонка — владелец и редактор пишут память одной персоны одновременно ──────────────
    //
    // До B1d ключи были разные (каждый свой UserId) и записи не пересекались; теперь оба идут в
    // один стор. Защита от гонки — блокировка сервиса: дописывание и сохранение в Remember идут
    // под _saveLock (PersonaMemoryService.Remember: state.Entries.Add + JsonFileStore.Save под
    // lock), дописывание — не перезапись списка (Append, не замена). Мутация «снять lock в
    // Remember» на копии дерева теста не покраснила: критическая секция короткая (дедуп +
    // Add + Save), два запроса в неё за два прогона не пересеклись — разбор в отчёте задачи.

    [Fact]
    public async Task ParallelMemoryWrite_OwnerAndEditor_BothEntriesKept()
    {
        var (_, personaId) = await ArrangeAsync(Editor);
        var ownerText = "Запись владельца " + RunId();
        var editorText = "Запись редактора " + RunId();

        var ownerPost = OwnerClient().PostAsJsonAsync($"/api/personas/{personaId}/memory",
            new { type = "semantic", text = ownerText });
        var editorPost = SecondClient().PostAsJsonAsync($"/api/personas/{personaId}/memory",
            new { type = "semantic", text = editorText });
        foreach (var response in await Task.WhenAll(ownerPost, editorPost))
            response.EnsureSuccessStatusCode();

        var texts = Memory.List(TenantId, personaId, null).Select(e => e.Text);
        texts.Should().Contain(ownerText, "запись владельца в сторе");
        texts.Should().Contain(editorText,
            "запись редактора в том же сторе: одновременная запись ничего не перезаписала и не потеряла");
    }

    // ── W3-B3a: создание проектной персоны — право по проекту из тела, владелец = арендатор
    // (разрез §3.1 строки «POST /», «POST /ai/quick-create», «POST /ai/team»; И-2, И-7) ──────────
    //
    // Та же матрица ролей, что у read/write-путей {id}, но для создания. [ProjectScope] здесь
    // невозможен: id проекта не в маршруте и не в аргументах действия — право решает
    // IProjectAccess.Resolve в теле (PersonasController.ProjectWriteArea), ключ дальнейших
    // вызовов — TenantOwnerId резолва. Ожидания те же: редактор — 2xx и OwnerId новой персоны
    // равен владельцу (с ключом вызывающего ValidProject отбил бы ему 400, а персона досталась
    // бы ему самому), читатель — 403 project_scope, посторонний — 404. Отказанное создание не
    // оставляет ни полусозданной записи, ни обращения к модели: право проверяется до работы.
    // Принятые LLM-пути (quick-create, ai/team) — на управляемой заглушке: без неё тестовый хост
    // отвечает «[]», черновик не распознан и ответ был бы 502 — проверка вышла бы про парсер,
    // а не про право.

    private sealed record CreateAct(string Name, string Path,
        Func<string, Dictionary<string, string>, object> Body,
        int ForOwner, int ForEditor, int ForReader, int ForOutsider)
    {
        public int For(string role) => role switch
        {
            Owner => ForOwner, Editor => ForEditor, Reader => ForReader, _ => ForOutsider,
        };
    }

    private static readonly CreateAct[] CreateActions =
    [
        new("создать проектную", "/api/personas",
            (r, ids) => new { name = "Созданная-" + r, scope = "project", projectId = ids["p"] },
            Ok2xx, Ok2xx, 403, 404),
        new("quick-create проектной", "/api/personas/ai/quick-create",
            (r, ids) => new { prompt = "Аналитик для проекта " + r, scope = "project", projectId = ids["p"] },
            Ok2xx, Ok2xx, 403, 404),
        // ai/team пишет только черновики в ответ, персон не создаёт — это отдельная проверка ниже
        new("ai-команда проекта", "/api/personas/ai/team",
            (r, ids) => new { projectId = ids["p"], prompt = "Команда для анализа " + r },
            Ok2xx, Ok2xx, 403, 404),
    ];

    // Имя, под которым принятое действие создало бы персону: «создать проектную» — из тела
    // запроса, quick-create — из ответа заглушки, ai-team — имя черновика команды (персон ai/team
    // не создаёт: этим же именем проверяется «ничего не создалось» и на принятых, и на отказанных
    // ролях). Уникально на каждый прогон — стор памяти и стора персон общие (одна фабрика).
    private static string CreatedName(string actionName, string runId) => actionName switch
    {
        "создать проектную" => "Созданная-" + runId,
        "quick-create проектной" => "Черновик-" + runId,
        _ => "Бортовой-" + runId,
    };

    private static string DraftJson(string runId) =>
        $"{{\"name\":\"Черновик-{runId}\",\"role\":\"Аналитик\",\"description\":\"Аналитик проекта {runId}\"," +
        "\"tone\":\"сухо и по делу\",\"color\":\"blue\"}}";

    private static string TeamJson(string runId) =>
        $"[{{\"name\":\"Бортовой-{runId}\",\"role\":\"Аналитик\",\"description\":\"Аналитик проекта\"}}]";

    public static IEnumerable<object[]> CreateMatrix() =>
        from a in CreateActions
        from role in new[] { Owner, Editor, Reader, Outsider }
        select new object[] { a.Name, role };

    [Theory]
    [MemberData(nameof(CreateMatrix))]
    public async Task СозданиеПроектнойПерсоны_ДаётОжидаемыйОтказИВладельцаПоАрендатору(string actionName, string role)
    {
        var action = CreateActions.Single(a => a.Name == actionName);
        var (projectId, _) = await ArrangeAsync(role);
        var runId = RunId();
        var name = CreatedName(actionName, runId);

        _llm.Reset();
        _llm.Answer = actionName switch
        {
            "quick-create проектной" => DraftJson(runId),
            "ai-команда проекта" => TeamJson(runId),
            _ => "[]",
        };

        var (status, code, body) = await SendBodyAsync(ClientOf(role), action.Path,
            action.Body(runId, new Dictionary<string, string> { ["p"] = projectId }));

        var expected = action.For(role);
        if (expected == Ok2xx)
        {
            (status >= 200 && status < 300).Should().BeTrue($"{role}: {actionName} → {status}");
        }
        else
        {
            status.Should().Be(expected, $"{role}: {actionName}");
            if (expected == 403)
                code.Should().Be(NoRight, $"{role}: {actionName} — отказ различается машинным кодом");
        }

        // Состояние стора после полного запроса: созданная принадлежит владельцу проекта и видна
        // его списку; отказанное создание не оставило записи — ни в сторе проекта, ни в личном
        // сторе вызывающего.
        switch (actionName, role)
        {
            case ("создать проектную", Owner or Editor):
            case ("quick-create проектной", Owner or Editor):
                var created = Personas.GetByOwner(TenantId).Single(p => p.Name == name);
                created.OwnerId.Should().Be(TenantId,
                    $"{role}: OwnerId новой персоны — владелец проекта, не вызывающий (И-7)");
                created.Scope.Should().Be(PersonaScope.Project);
                created.ProjectId.Should().Be(projectId);
                Personas.GetByOwner(MemberId).Any(p => p.Name == name).Should().BeFalse(
                    $"{role}: персона не легла в личное пространство вызывающего (И-2)");
                var list = await BodyAsync(await OwnerClient().GetAsync($"/api/personas?scope=project&projectId={projectId}"));
                list.EnumerateArray().Select(p => p.GetProperty("id").GetString()).Should().Contain(created.Id,
                    "персона в том же сторе, что персон владельца: его список проекта её находит");
                if (actionName == "quick-create проектной")
                {
                    _llm.Owners.Should().Contain(TenantId,
                        "one-shot черновика проектной персоны оплачивает арендатор проекта (И-2)");
                    _llm.Owners.Should().NotContain(MemberId, "ни один вызов создания не пошёл под ключом вызывающего");
                }
                break;
            case ("ai-команда проекта", Owner or Editor):
                body.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("name").GetString())
                    .Should().Contain(name, $"{role}: черновики команды дошли до фронта");
                Personas.GetByOwner(TenantId).Any(p => p.Name == name).Should().BeFalse(
                    "ai/team возвращает черновики и не создаёт персон");
                _llm.Owners.Should().Contain(TenantId, "one-shot состава команды оплачивает арендатор проекта (И-2)");
                _llm.Owners.Should().NotContain(MemberId, "ни один вызов не пошёл под ключом вызывающего");
                break;
            case ("создать проектную", _):
            case ("quick-create проектной", _):
            case ("ai-команда проекта", _):
                Personas.GetByOwner(TenantId).Any(p => p.Name == name).Should().BeFalse(
                    $"{role}: отказанное создание не оставило персоны в сторе проекта");
                Personas.GetByOwner(MemberId).Any(p => p.Name == name).Should().BeFalse(
                    $"{role}: и в личном сторе вызывающего тоже — полусозданной записи нет");
                _llm.Owners.Should().BeEmpty($"{role}: отказ решил гейт до любой работы, в том числе до модели");
                break;
        }
    }

    // Глобальную и сферную персону участник создаёт как раньше — в своём пространстве (разрез
    // §3.1 «без изменений», И-3): она принадлежит ему и владельцу общего проекта не видна.
    [Fact]
    public async Task ГлобальнуюПерсонуУчастникСоздаётВСвоёмПространстве()
    {
        await ArrangeAsync(Editor); // общий проект владельца с редактором-участником
        var name = "Своя глобальная-" + RunId();

        var created = await SecondClient().PostAsJsonAsync("/api/personas", new { name, scope = "global" });
        created.EnsureSuccessStatusCode();
        var id = IdOf(await created.Content.ReadAsStringAsync());

        Personas.Get(id, MemberId)!.OwnerId.Should().Be(MemberId,
            "сфера создания глобальной персоны — личное пространство вызывающего");
        Personas.Get(id, TenantId).Should().BeNull("она не легла в стор владельца общего проекта");
        (await OwnerClient().GetAsync($"/api/personas/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "И-3: личная персона участника не видна владельцу проекта");
    }

    // ── W3-B3a: гонка — владелец и редактор создают персону одного проекта одновременно ────────
    //
    // До B3a ключи были разные (каждый свой UserId) и записи не пересекались; теперь обе идут в
    // стор проекта. Защита — существующая блокировка PersonaManager: вставка в ConcurrentDictionary
    // и JsonFileStore.Save — под _saveLock, генерация handle там же (атомарна, без TOCTOU,
    // PersonaManager.cs:423). Ожидаем ровно две персоны, обе под ключом владельца, и читаемый
    // personas.json.

    [Fact]
    public async Task ParallelCreate_OwnerAndEditor_TwoPersonas()
    {
        var (projectId, fixturePersonaId) = await ArrangeAsync(Editor);
        var ownerName = "Парная владельца-" + RunId();
        var editorName = "Парная редактора-" + RunId();

        var ownerPost = OwnerClient().PostAsJsonAsync("/api/personas",
            new { name = ownerName, scope = "project", projectId });
        var editorPost = SecondClient().PostAsJsonAsync("/api/personas",
            new { name = editorName, scope = "project", projectId });
        var ids = new List<string>();
        foreach (var response in await Task.WhenAll(ownerPost, editorPost))
        {
            response.EnsureSuccessStatusCode();
            ids.Add(IdOf(await response.Content.ReadAsStringAsync()));
        }

        var team = Personas.GetProjectTeam(TenantId, projectId);
        team.Select(p => p.Id).Should().BeEquivalentTo(new[] { fixturePersonaId, ids[0], ids[1] },
            "ровно три записи команды: существующая не потеряна, новые — две, без дублей");
        foreach (var (id, name) in new[] { (ids[0], ownerName), (ids[1], editorName) })
            Personas.Get(id, TenantId)!.OwnerId.Should().Be(TenantId,
                $"{name}: созданная участником проекта — под ключом его арендатора (И-7)");

        // Стор на диске читается: после двух одновременных сохранений personas.json — валидный JSON
        // с обеими записями (оборванный файл или потерянная запись краснеют здесь). Имена в файле
        // экранированы (\uXXXX), поэтому проверка — по разобранному JSON, а не по подстроке.
        var stored = JsonDocument.Parse(File.ReadAllText(StorePath)).RootElement.EnumerateArray()
            .Select(e => e.GetProperty("Name").GetString()).ToList();
        stored.Should().Contain(ownerName, "запись владельца лежит на диске");
        stored.Should().Contain(editorName,
            "запись редактора лежит на диске: одновременное сохранение ничего не потеряло и не оборвало файл");
    }

    // ── W3-B3b: правка карточки (PUT /{id}) — поля зоны и денег принадлежат владельцу ──────────
    // (разрез §3.1 строка «PUT /{id}», §2 строка «Поля зоны и денег»; И-2, И-7) ────────────────
    //
    // Содержание (имя, тон) редактор правит под ключом арендатора; поля зоны и денег
    // (Scope/ProjectId/SphereId, профиль доступа, модель/уровень/усилие/матрица) — право Configure:
    // редактору — 403 с кодом owner_settings ДО записи, без частичной записи прочих полей DTO
    // (тот же код, что у настроек чата, S2a-10b). Поле, принесённое без изменения текущего значения,
    // отказом не считается: строка «править имя с неизменённой зоной» держит это — форма персоны
    // шлёт карточку целиком. Читателя отказывает фильтр атрибута (project_scope), постороннему — 404.

    private sealed record UpdateAct(string Name, Func<string, Dictionary<string, string>, object?> Body,
        int ForOwner, int ForEditor, int ForReader, int ForOutsider, string EditorCode)
    {
        public int For(string role) => role switch
        {
            Owner => ForOwner, Editor => ForEditor, Reader => ForReader, _ => ForOutsider,
        };

        // Ожидаемый машинный код 403: редактору — по строке (owner_settings, когда отказывает
        // гейт зоны/денег, или project_scope, когда правки без изменения), читателю — всегда по
        // праву атрибута.
        public string? CodeFor(string role) => role switch
        {
            Editor => EditorCode, Reader => NoRight, _ => null,
        };
    }

    private static readonly UpdateAct[] UpdateActions =
    [
        new("править имя", (r, _) => new { name = "Имя-" + r }, Ok2xx, Ok2xx, 403, 404, NoRight),
        // Карточка целиком, как её приносит форма: зона/доступ/модель — текущие значения или
        // пустые сбросы. Ни одно поле не изменено — отказа нет.
        new("править имя с неизменённой зоной",
            (r, ids) => new { name = "Имя-" + r, scope = "project", projectId = ids["p"], access = "full", model = "", effort = "" },
            Ok2xx, Ok2xx, 403, 404, NoRight),
        new("сменить модель", (r, _) => new { model = "sonnet" }, Ok2xx, 403, 403, 404, OwnerSettingsGate.ErrorCode),
        new("сменить модель и имя", (r, _) => new { model = "sonnet", name = "Имя-" + r }, Ok2xx, 403, 403, 404, OwnerSettingsGate.ErrorCode),
        new("сменить зону на другой проект", (r, ids) => new { projectId = ids["p2"] }, Ok2xx, 403, 403, 404, OwnerSettingsGate.ErrorCode),
        new("сменить усилие", (r, _) => new { effort = "low" }, Ok2xx, 403, 403, 404, OwnerSettingsGate.ErrorCode),
        // П1 ревью e6029e19: профиль доступа (Access/DisallowedTools/Tools/Specialty) — тоже поле
        // владельца. Каждая новая строка несёт name: на отказанной роли проверяется «прочие поля
        // DTO не записаны», на разрешённой — записаны обе части правки.
        new("сменить профиль доступа", (r, _) => new { access = "readOnly", name = "Имя-" + r }, Ok2xx, 403, 403, 404, OwnerSettingsGate.ErrorCode),
        new("сменить зону", (r, _) => new { scope = "global", name = "Имя-" + r }, Ok2xx, 403, 403, 404, OwnerSettingsGate.ErrorCode),
        new("сменить уровень модели", (r, _) => new { modelTier = "strong", name = "Имя-" + r }, Ok2xx, 403, 403, 404, OwnerSettingsGate.ErrorCode),
        new("сменить ячейку матрицы", (r, _) => new { tierStrong = "sonnet", name = "Имя-" + r }, Ok2xx, 403, 403, 404, OwnerSettingsGate.ErrorCode),
        // Обход (A): Access не меняется (тот же Custom), меняется только список запретов —
        // прямой сверки поля DTO раньше было недостаточно (PersonasCrudService.cs:232 → PersonaManager.cs:775)
        new("очистить запреты при неизменённом доступе",
            (r, _) => new { access = "custom", disallowedTools = Array.Empty<string>(), name = "Имя-" + r },
            Ok2xx, 403, 403, 404, OwnerSettingsGate.ErrorCode),
        // Обход (B): специальность через шаблон меняет профиль доступа (SpecialtyTemplatesService.cs:41,
        // SpecialtyCatalog.cs:37-38) — при неизменном Access в теле
        new("сменить специальность", (r, _) => new { specialty = "backendExecutor", name = "Имя-" + r }, Ok2xx, 403, 403, 404, OwnerSettingsGate.ErrorCode),
    ];

    // Подготовка фитчюра под строку: «сменить зону» — второй проект того же владельца (правка
    // ProjectId на чужой проект — смена зоны, она же проверка И-7); «очистить запреты» — персона
    // с owner-списком запретов под Custom: обход (A) ровно про него — Access в запросе тот же,
    // меняется только DisallowedTools.
    private async Task<Dictionary<string, string>> PrepareUpdateFixtureAsync(string actionName, string personaId)
    {
        var ids = new Dictionary<string, string>();
        if (actionName == "очистить запреты при неизменённом доступе")
        {
            Personas.Update(personaId, TenantId, null, null, null, null, null, null, null, null, null, null, null,
                access: PersonaAccess.Custom, disallowedTools: ["web"]);
            return ids;
        }
        if (actionName != "сменить зону на другой проект") return ids;
        var dir = Directory.CreateDirectory(Path.Combine(_factory.TempDir, "prx_" + Guid.NewGuid().ToString("N")[..8])).FullName;
        var created = await OwnerClient().PostAsJsonAsync("/api/projects",
            new { name = "ВторойПроектПерсон" + Guid.NewGuid().ToString("N")[..6], rootPath = dir });
        created.EnsureSuccessStatusCode();
        ids["p2"] = IdOf(await created.Content.ReadAsStringAsync());
        return ids;
    }

    public static IEnumerable<object[]> UpdateMatrix() =>
        from a in UpdateActions
        from role in new[] { Owner, Editor, Reader, Outsider }
        select new object[] { a.Name, role };

    [Theory]
    [MemberData(nameof(UpdateMatrix))]
    public async Task ПравкаКарточкиПерсоны_ОтказываетНеВладельцуПолямЗоныИБезЧастичнойЗаписи(string actionName, string role)
    {
        var action = UpdateActions.Single(a => a.Name == actionName);
        var (projectId, personaId) = await ArrangeAsync(role);
        var runId = RunId();
        var ids = await PrepareUpdateFixtureAsync(actionName, personaId);
        ids["p"] = projectId; // текущий проект персоны — для строк с неизменённой зоной

        // Снимок ДО запроса: personas.Get отдаёт живую ссылку — сравнивать можно только со
        // скопированным значением (тот же приём, что PersonasCrudService.UpdateAsync:240-244).
        var beforeName = Personas.Get(personaId, TenantId)!.Name;

        var (status, code) = await SendAsync(ClientOf(role), HttpMethod.Put,
            $"/api/personas/{personaId}", action.Body(runId, ids));

        var expected = action.For(role);
        if (expected == Ok2xx)
        {
            (status >= 200 && status < 300).Should().BeTrue($"{role}: {actionName} → {status}");
        }
        else
        {
            status.Should().Be(expected, $"{role}: {actionName}");
            if (expected == 403)
                code.Should().Be(action.CodeFor(role), $"{role}: {actionName} — отказ различается машинным кодом");
        }

        var persona = Personas.Get(personaId, TenantId)!;
        switch (actionName, role)
        {
            case ("править имя", Owner or Editor):
                persona.Name.Should().Be("Имя-" + runId, $"{role}: имя записано под ключом арендатора");
                break;
            case ("править имя", _):
                persona.Name.Should().Be(beforeName, $"{role}: отказанная правка не тронула имя");
                break;
            case ("править имя с неизменённой зоной", Owner or Editor):
                persona.Name.Should().Be("Имя-" + runId, $"{role}: неизменённые поля зоны не дали отказа");
                persona.Scope.Should().Be(PersonaScope.Project);
                persona.ProjectId.Should().Be(projectId);
                break;
            case ("править имя с неизменённой зоной", _):
                persona.Name.Should().Be(beforeName, $"{role}: отказанная правка не тронула имя");
                persona.ProjectId.Should().Be(projectId, $"{role}: зона на месте");
                break;
            case ("сменить модель", Owner):
                persona.Model.Should().NotBeNull("владелец может менять модель персоны");
                break;
            case ("сменить модель", _):
                persona.Model.Should().BeNull($"{role}: отказанный owner_settings не тронул модель");
                break;
            case ("сменить модель и имя", Owner):
                persona.Name.Should().Be("Имя-" + runId);
                persona.Model.Should().NotBeNull();
                break;
            case ("сменить модель и имя", _):
                persona.Name.Should().Be(beforeName,
                    $"{role}: отказ без частичной записи: имя из того же DTO не записано");
                persona.Model.Should().BeNull($"{role}: и модель тоже");
                break;
            case ("сменить зону на другой проект", Owner):
                persona.ProjectId.Should().Be(ids["p2"], "владелец переносит персону в свой проект");
                break;
            case ("сменить зону на другой проект", _):
                persona.ProjectId.Should().Be(projectId,
                    $"{role}: ProjectId персоны прежний — зону не-владелец не меняет (И-7)");
                break;
            case ("сменить усилие", Owner):
                persona.Effort.Should().Be("low", "усилие — поле денег, владельцу можно");
                break;
            case ("сменить усилие", _):
                persona.Effort.Should().BeNull($"{role}: отказанное усилие не записано");
                break;
            case ("сменить профиль доступа", Owner):
                persona.Access.Should().Be(PersonaAccess.ReadOnly, "владелец меняет профиль доступа");
                persona.Name.Should().Be("Имя-" + runId, "и прочие поля той же правки записаны");
                break;
            case ("сменить профиль доступа", _):
                persona.Access.Should().Be(PersonaAccess.Full, $"{role}: отказанный профиль доступа не записан");
                persona.Name.Should().Be(beforeName, $"{role}: и имя из того же DTO не записано");
                break;
            case ("сменить зону", Owner):
                persona.Scope.Should().Be(PersonaScope.Global, "владелец переводит персону в глобальную зону");
                persona.Name.Should().Be("Имя-" + runId);
                break;
            case ("сменить зону", _):
                persona.Scope.Should().Be(PersonaScope.Project, $"{role}: отказанная смена зоны не записана");
                persona.ProjectId.Should().Be(projectId, $"{role}: проект персоны прежний");
                persona.Name.Should().Be(beforeName, $"{role}: имя из того же DTO не записано");
                break;
            case ("сменить уровень модели", Owner):
                persona.ModelTier.Should().NotBeNull("владелец выбирает уровень модели");
                persona.Name.Should().Be("Имя-" + runId);
                break;
            case ("сменить уровень модели", _):
                persona.ModelTier.Should().BeNull($"{role}: отказанный уровень модели не записан");
                persona.Name.Should().Be(beforeName, $"{role}: имя из того же DTO не записано");
                break;
            case ("сменить ячейку матрицы", Owner):
                persona.TierStrong.Should().NotBeNull("владелец заполняет ячейку матрицы");
                persona.Name.Should().Be("Имя-" + runId);
                break;
            case ("сменить ячейку матрицы", _):
                persona.TierStrong.Should().BeNull($"{role}: отказанная ячейка матрицы не записана");
                persona.Name.Should().Be(beforeName, $"{role}: имя из того же DTO не записано");
                break;
            case ("очистить запреты при неизменённом доступе", Owner):
                persona.Access.Should().Be(PersonaAccess.Custom, "Access в запросе был текущим — он и остался");
                persona.DisallowedTools.Should().BeNull("владелец обнуляет свои запреты");
                persona.Name.Should().Be("Имя-" + runId);
                break;
            case ("очистить запреты при неизменённом доступе", _):
                persona.DisallowedTools.Should().Equal(new[] { "web" },
                    $"{role}: обход (A) — обнуление чужих запретов отказано до записи");
                persona.Access.Should().Be(PersonaAccess.Custom, $"{role}: профиль на месте");
                persona.Name.Should().Be(beforeName, $"{role}: имя из того же DTO не записано");
                break;
            case ("сменить специальность", Owner):
                persona.Specialty.Should().Be(PersonaSpecialty.BackendExecutor, "владелец выбирает специальность");
                persona.Name.Should().Be("Имя-" + runId);
                break;
            case ("сменить специальность", _):
                persona.Specialty.Should().Be(PersonaSpecialty.None,
                    $"{role}: обход (B) — специальность через шаблон меняет профиль доступа, отказана до записи");
                persona.Access.Should().Be(PersonaAccess.Full, $"{role}: и эффект шаблона (Access) не записан");
                persona.Name.Should().Be(beforeName, $"{role}: имя из того же DTO не записано");
                break;
        }
    }

    // ── W3-B3b: гонка — владелец правит имя, редактор правит тон одной персоны одновременно ────
    //
    // Оба идут в один стор под _saveLock PersonaManager.Update (мутация полей и Save,
    // PersonaManager.cs:706-802). Partial-update не перечёркивает чужие поля: обе правки остаются.
    // Устаревшая запись (оба меняют ОДНО поле) — побеждает последняя запись целиком: семантика
    // стора как есть, версионной сверки не заводилось. Здесь поля разные — проверка: запись одна,
    // OwnerId не изменился (И-7), обе правки в сторе, personas.json читается.

    [Fact]
    public async Task ParallelUpdate_OwnerAndEditor_SinglePersonaOwnerKept()
    {
        var (_, personaId) = await ArrangeAsync(Editor);
        var ownerName = "Имя владельца-" + RunId();
        var editorTone = "Тон редактора-" + RunId();

        var ownerPut = OwnerClient().PutAsJsonAsync($"/api/personas/{personaId}", new { name = ownerName });
        var editorPut = SecondClient().PutAsJsonAsync($"/api/personas/{personaId}",
            new { contract = new { tone = editorTone } });
        foreach (var response in await Task.WhenAll(ownerPut, editorPut))
            response.EnsureSuccessStatusCode();

        var records = Personas.GetByOwner(TenantId).Where(p => p.Id == personaId).ToList();
        records.Should().HaveCount(1, "запись персоны одна: одновременные правки не размножили её");
        var persona = records[0];
        persona.OwnerId.Should().Be(TenantId, "OwnerId не изменился (И-7): правка участника не переносит персону");
        persona.Name.Should().Be(ownerName, "имя владельца в сторе");
        persona.Contract?.Tone.Should().Be(editorTone,
            "тон редактора в том же сторе: partial-update не перечеркнул чужие поля");

        // Стор на диске читается и содержит ровно эту запись один раз (тот же приём, что ParallelCreate)
        JsonDocument.Parse(File.ReadAllText(StorePath)).RootElement.EnumerateArray()
            .Count(e => e.GetProperty("Id").GetString() == personaId)
            .Should().Be(1, "personas.json после двух одновременных сохранений: запись одна, файл не оборван");
    }
}
