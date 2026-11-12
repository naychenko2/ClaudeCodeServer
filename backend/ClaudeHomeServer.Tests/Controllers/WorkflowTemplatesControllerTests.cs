using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// B1c: REST CRUD шаблонов процесса (разрез §6 И8). Пути к инварианту, проверяемые здесь:
// аноним → 401; не-admin пишет глобальный → 403; участник общего проекта, не владелец, пишет
// проектный → 403; посторонний читает проектные → 404; владелец пишет проектный → 200.
// Плюс контракт версии: PUT без expectedVersion → 400 (стор не вызывается), два PUT с одним
// expectedVersion — второй 409 и в сторе остаётся первый.
[Trait("Category", "Host")]
public class WorkflowTemplatesControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public WorkflowTemplatesControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
    }

    // Первый тестовый пользователь — admin (сид TestWebApplicationFactory), второй — обычный.
    private HttpClient Admin() => _factory.CreateAuthenticatedClient();
    private HttpClient User() => _factory.CreateAuthenticatedClient(
        TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

    private static string NewId() => Guid.NewGuid().ToString();

    private static object Body(string id, string name, object[] stages) =>
        new { id, name, description = "тестовый", stages };

    private static object[] Stage(string role, string presence = "on") =>
        [new { role, presence }];

    private async Task<string> CreateGlobalAsync(HttpClient client, string name = "Глобальный")
    {
        var id = NewId();
        var resp = await client.PostAsJsonAsync("/api/workflow-templates",
            Body(id, name, Stage("plan")));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return id;
    }

    // Общий проект владельца (admin) со вторым пользователем в роли role; id проекта.
    private async Task<string> SharedProjectAsync(string role)
    {
        var owner = Admin();
        var member = User();
        foreach (var c in new[] { owner, member })
            (await c.PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = true }))
                .EnsureSuccessStatusCode();
        var dir = Path.Combine(_factory.TempDir, "wft_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var created = await owner.PostAsJsonAsync("/api/projects",
            new { name = "Шаблоны" + Guid.NewGuid().ToString("N")[..6], rootPath = dir });
        created.EnsureSuccessStatusCode();
        var id = JsonSerializer.Deserialize<JsonElement>(await created.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
        if (role.Length > 0)
            (await owner.PostAsJsonAsync($"/api/projects/{id}/members",
                new { username = TestWebApplicationFactory.SecondUsername, role })).EnsureSuccessStatusCode();
        return id;
    }

    private async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        var resp = await client.GetAsync(path);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static JsonElement FindTemplate(JsonElement list, string id) => list
        .EnumerateArray().First(e => e.GetProperty("template").GetProperty("id").GetString() == id);

    // ─── Аноним и чтение глобальных ────────────────────────────────────────────

    [Fact]
    public async Task Аноним_ЧитаетСписок_401()
    {
        (await _factory.CreateClient().GetAsync("/api/workflow-templates"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _factory.CreateClient().GetAsync("/api/workflow-templates/builtin-full-cycle"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("full-cycle", "Полный цикл")]
    [InlineData("fast-track", "Быстрый трек")]
    public async Task ЛюбойЧитаетГлобальные_ВстроенныеВидны(string key, string name)
    {
        var list = await GetJsonAsync(User(), "/api/workflow-templates");
        var entry = FindTemplate(list, $"builtin-{key}");
        entry.GetProperty("scope").GetString().Should().Be("global");
        entry.GetProperty("template").GetProperty("name").GetString().Should().Be(name);
    }

    // ─── Запись глобальных: только admin ───────────────────────────────────────

    [Fact]
    public async Task НеАдминПишетГлобальный_403_СторНеТронут()
    {
        var put = await User().PostAsJsonAsync("/api/workflow-templates",
            Body(NewId(), "Чужой глобальный", Stage("plan")));
        put.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var list = await GetJsonAsync(User(), "/api/workflow-templates");
        list.EnumerateArray().Should().NotContain(e =>
            e.GetProperty("template").GetProperty("name").GetString() == "Чужой глобальный");
    }

    [Fact]
    public async Task АдминСоздаётГлобальный_200_Версия1_ЛюбойЧитаетКарточку()
    {
        var id = await CreateGlobalAsync(Admin(), "Видимый всем");

        var created = await GetJsonAsync(Admin(), $"/api/workflow-templates/{id}");
        created.GetProperty("version").GetInt32().Should().Be(1);
        created.GetProperty("builtInKey").ValueKind.Should().Be(JsonValueKind.Null);

        // карточку глобального шаблона читает и не-admin
        await GetJsonAsync(User(), $"/api/workflow-templates/{id}");
    }

    [Fact]
    public async Task СозданиеСУжеСуществующимId_409_АНеТихаяПерезапись()
    {
        var id = await CreateGlobalAsync(Admin(), "Первый");
        var again = await Admin().PostAsJsonAsync("/api/workflow-templates",
            Body(id, "Второй поверх", Stage("plan")));
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var kept = await GetJsonAsync(Admin(), $"/api/workflow-templates/{id}");
        kept.GetProperty("name").GetString().Should().Be("Первый");
    }

    // ─── Версия: expectedVersion (c5, c6) ──────────────────────────────────────

    [Fact]
    public async Task ПравкаБезExpectedVersion_400_ШаблонНеТронут()
    {
        var id = await CreateGlobalAsync(Admin(), "До правки");

        var put = await Admin().PutAsJsonAsync($"/api/workflow-templates/{id}",
            new { id, name = "После правки", stages = Stage("plan") });
        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var kept = await GetJsonAsync(Admin(), $"/api/workflow-templates/{id}");
        kept.GetProperty("name").GetString().Should().Be("До правки");
        kept.GetProperty("version").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task ДваПуткаСОднимExpectedVersion_Второй409_ВСтореПервый()
    {
        var id = await CreateGlobalAsync(Admin(), "Исходный");
        var version = (await GetJsonAsync(Admin(), $"/api/workflow-templates/{id}"))
            .GetProperty("version").GetInt32();

        var first = await Admin().PutAsJsonAsync($"/api/workflow-templates/{id}",
            new { id, name = "Первая правка", stages = Stage("plan"), expectedVersion = version });
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        (await first.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("version").GetInt32().Should().Be(version + 1);

        // тот же expectedVersion — устаревшая версия: отказ, а не перезапись первой правки
        var second = await Admin().PutAsJsonAsync($"/api/workflow-templates/{id}",
            new { id, name = "Вторая правка", stages = Stage("plan"), expectedVersion = version });
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var kept = await GetJsonAsync(Admin(), $"/api/workflow-templates/{id}");
        kept.GetProperty("name").GetString().Should().Be("Первая правка");
        kept.GetProperty("version").GetInt32().Should().Be(version + 1);
    }

    // ─── Валидатор: 400 со списком ─────────────────────────────────────────────

    [Fact]
    public async Task НевалидныйШаблон_400СоСпискомОшибок()
    {
        var resp = await Admin().PostAsJsonAsync("/api/workflow-templates",
            new
            {
                id = NewId(),
                name = "Битый",
                stages = new object[]
                {
                    new { role = "нет-такой-роли", presence = "on" },
                    new { role = "plan", presence = "on" },
                    new { role = "plan", presence = "off" },
                },
            });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errors").GetArrayLength().Should().BeGreaterThanOrEqualTo(2);
    }

    // ─── Проектные: читает имеющий доступ, пишет только владелец (И8) ──────────

    [Fact]
    public async Task ВладелецПишетПроектный_200_УчастникЧитаетНоНеПишет()
    {
        var projectId = await SharedProjectAsync("editor");
        var id = NewId();

        var created = await Admin().PostAsJsonAsync($"/api/workflow-templates?projectId={projectId}",
            Body(id, "Проектный шаблон", Stage("step")));
        created.StatusCode.Should().Be(HttpStatusCode.OK);

        // участник проекта читает проектные — список с меткой области и карточка
        var list = await GetJsonAsync(User(), $"/api/workflow-templates?projectId={projectId}");
        FindTemplate(list, id).GetProperty("scope").GetString().Should().Be("project");
        (await GetJsonAsync(User(), $"/api/workflow-templates/{id}?projectId={projectId}"))
            .GetProperty("name").GetString().Should().Be("Проектный шаблон");

        // но не пишет: ни созданием, ни правкой (право Configure — только владелец)
        (await User().PostAsJsonAsync($"/api/workflow-templates?projectId={projectId}",
            Body(NewId(), "Чужой проектный", Stage("step"))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await User().PutAsJsonAsync($"/api/workflow-templates/{id}?projectId={projectId}",
            new { id, name = "Чужая правка", stages = Stage("step"), expectedVersion = 1 }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var kept = await GetJsonAsync(Admin(), $"/api/workflow-templates/{id}?projectId={projectId}");
        kept.GetProperty("name").GetString().Should().Be("Проектный шаблон");
    }

    [Fact]
    public async Task ПостороннийЧитаетПроектные_404_ИСписокИКарточка()
    {
        var projectId = await SharedProjectAsync(role: ""); // без участников
        var id = await CreateGlobalAsync(Admin());

        (await User().GetAsync($"/api/workflow-templates?projectId={projectId}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await User().GetAsync($"/api/workflow-templates/{id}?projectId={projectId}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task УдалениеПроектного_Владелец204_Участник403()
    {
        var projectId = await SharedProjectAsync("editor");
        var id = NewId();
        (await Admin().PostAsJsonAsync($"/api/workflow-templates?projectId={projectId}",
            Body(id, "К удалению", Stage("step")))).EnsureSuccessStatusCode();

        (await User().DeleteAsync($"/api/workflow-templates/{id}?projectId={projectId}"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Admin().DeleteAsync($"/api/workflow-templates/{id}?projectId={projectId}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Admin().GetAsync($"/api/workflow-templates/{id}?projectId={projectId}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── Встроенные: удалить нельзя, сбросить — admin, версия+1 ────────────────

    [Fact]
    public async Task ВстроенныйУдалитьНельзя_403_СбросАдминомДаётВерсиюПлюсОдин()
    {
        var before = (await GetJsonAsync(Admin(), "/api/workflow-templates/builtin-full-cycle"))
            .GetProperty("version").GetInt32();

        (await Admin().DeleteAsync("/api/workflow-templates/builtin-full-cycle"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // не-admin сбросить не может
        (await User().PostAsync("/api/workflow-templates/builtin-full-cycle/reset", null))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var reset = await Admin().PostAsync("/api/workflow-templates/builtin-full-cycle/reset", null);
        reset.StatusCode.Should().Be(HttpStatusCode.OK);
        var after = await reset.Content.ReadFromJsonAsync<JsonElement>();
        after.GetProperty("version").GetInt32().Should().Be(before + 1);
        after.GetProperty("name").GetString().Should().Be("Полный цикл");
    }

    [Fact]
    public async Task СбросНеВстроенного_400_ЧужойId_404()
    {
        var id = await CreateGlobalAsync(Admin(), "Не встроенный");
        (await Admin().PostAsync($"/api/workflow-templates/{id}/reset", null))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Admin().PostAsync($"/api/workflow-templates/{NewId()}/reset", null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
