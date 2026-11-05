using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
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
/// </summary>
[Trait("Category", "Host")]
public class SharedProjectPersonaRightsTests : IClassFixture<TestWebApplicationFactory>
{
    private const string Owner = "owner", Editor = "editor", Reader = "reader", Outsider = "outsider";

    // 200 в ожиданиях значит «любой 2xx»
    private const int Ok2xx = 200;

    private const string NoRight = ProjectScopeAttribute.ErrorCode;

    private readonly TestWebApplicationFactory _factory;

    public SharedProjectPersonaRightsTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
    }

    private HttpClient OwnerClient() => _factory.CreateAuthenticatedClient();

    private HttpClient SecondClient() => _factory.CreateAuthenticatedClient(
        TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

    private HttpClient ClientOf(string role) => role == Owner ? OwnerClient() : SecondClient();

    private string TenantId => _factory.Services.GetRequiredService<UserStore>()
        .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

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
}
