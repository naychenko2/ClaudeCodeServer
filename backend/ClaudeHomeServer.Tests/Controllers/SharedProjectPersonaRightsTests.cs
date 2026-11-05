using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Memory;
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

    private static async Task<(int Status, string? Code)> SendAsync(HttpClient client, HttpMethod method, string url)
    {
        var response = await client.SendAsync(new HttpRequestMessage(method, url));
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
}
