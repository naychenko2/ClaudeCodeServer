using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

public class TasksControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;      // основной юзер (владелец задач)
    private readonly HttpClient _stranger;    // второй юзер — для изоляции

    public TasksControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
        _stranger = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
    }

    private async Task<JsonElement> CreateTaskAsync(object body)
    {
        var response = await _client.PostAsJsonAsync("/api/tasks", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    // ─── CRUD ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_ЛичнаяЗадача_ВозвращаетЗадачуБезПроекта()
    {
        var task = await CreateTaskAsync(new { title = "Личная задача", dueDate = "2026-07-10" });

        task.GetProperty("id").GetString().Should().NotBeNullOrEmpty();
        task.GetProperty("projectId").ValueKind.Should().Be(JsonValueKind.Null);
        task.GetProperty("title").GetString().Should().Be("Личная задача");
        task.GetProperty("status").GetString().Should().Be("todo");
    }

    [Fact]
    public async Task Create_ПустойЗаголовок_400()
    {
        var response = await _client.PostAsJsonAsync("/api/tasks", new { title = "  " });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetById_СвояЗадача_200()
    {
        var task = await CreateTaskAsync(new { title = "найди меня" });
        var id = task.GetProperty("id").GetString();

        var response = await _client.GetAsync($"/api/tasks/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("title").GetString().Should().Be("найди меня");
    }

    [Fact]
    public async Task Update_МеняетПоля()
    {
        var task = await CreateTaskAsync(new { title = "до правки" });
        var id = task.GetProperty("id").GetString();

        var response = await _client.PutAsJsonAsync($"/api/tasks/{id}", new
        {
            title = "после правки",
            status = "inProgress",
            priority = "high"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("title").GetString().Should().Be("после правки");
        body.GetProperty("status").GetString().Should().Be("inProgress");
        body.GetProperty("priority").GetString().Should().Be("high");
    }

    [Fact]
    public async Task Delete_УдаляетЗадачу()
    {
        var task = await CreateTaskAsync(new { title = "на удаление" });
        var id = task.GetProperty("id").GetString();

        var deleteResponse = await _client.DeleteAsync($"/api/tasks/{id}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var getResponse = await _client.GetAsync($"/api/tasks/{id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetAll_ФильтрПоПоиску_НаходитСвоюЗадачу()
    {
        var marker = "уникальный_" + Guid.NewGuid().ToString("N")[..8];
        await CreateTaskAsync(new { title = marker });

        var response = await _client.GetAsync($"/api/tasks?q={marker}");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetArrayLength().Should().Be(1);
    }

    // ─── Изоляция по владельцу ───────────────────────────────────────────────

    [Fact]
    public async Task ЧужаяЗадача_НедоступнаВторомуЮзеру()
    {
        var task = await CreateTaskAsync(new { title = "приватная" });
        var id = task.GetProperty("id").GetString();

        // GET по id → 404
        (await _stranger.GetAsync($"/api/tasks/{id}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);

        // PUT → 404
        (await _stranger.PutAsJsonAsync($"/api/tasks/{id}", new { title = "взлом" })).StatusCode
            .Should().Be(HttpStatusCode.NotFound);

        // DELETE → 404 (и задача жива)
        (await _stranger.DeleteAsync($"/api/tasks/{id}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await _client.GetAsync($"/api/tasks/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetAll_НеСодержитЧужихЗадач()
    {
        var task = await CreateTaskAsync(new { title = "только моя" });
        var id = task.GetProperty("id").GetString();

        var response = await _stranger.GetAsync("/api/tasks");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.EnumerateArray().Select(t => t.GetProperty("id").GetString())
            .Should().NotContain(id);
    }

    // ─── Регулярные задачи: done → следующий экземпляр ───────────────────────

    private async Task SetRecurrenceFlagAsync(bool enabled)
    {
        var response = await _client.PutAsJsonAsync("/api/feature-flags/task-recurrence", new { enabled });
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Done_РегулярнаяЗадачаСФлагом_СпавнитСледующийЭкземпляр()
    {
        await SetRecurrenceFlagAsync(true);
        try
        {
            var marker = "рег_" + Guid.NewGuid().ToString("N")[..8];
            var task = await CreateTaskAsync(new
            {
                title = marker,
                dueDate = "2026-07-01",
                dueTime = "10:00",
                recurrence = new { type = "daily", interval = 1 }
            });
            var id = task.GetProperty("id").GetString();

            var response = await _client.PutAsJsonAsync($"/api/tasks/{id}", new { status = "done" });
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var listResponse = await _client.GetAsync($"/api/tasks?q={marker}");
            var list = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
            var items = list.EnumerateArray().ToList();

            items.Should().HaveCount(2);
            var done = items.Single(t => t.GetProperty("status").GetString() == "done");
            var next = items.Single(t => t.GetProperty("status").GetString() == "todo");
            done.GetProperty("dueDate").GetString().Should().Be("2026-07-01");
            next.GetProperty("dueDate").GetString().Should().Be("2026-07-02");
            next.GetProperty("dueTime").GetString().Should().Be("10:00");
            // Оба экземпляра — одна серия
            next.GetProperty("seriesId").GetString().Should().Be(done.GetProperty("seriesId").GetString());
        }
        finally
        {
            await SetRecurrenceFlagAsync(false);
        }
    }

    [Fact]
    public async Task Done_БезФлагаRecurrence_НеСпавнит()
    {
        // Флаг у второго юзера выключен (дефолт) — спавна нет
        var marker = "без_флага_" + Guid.NewGuid().ToString("N")[..8];
        var createResponse = await _stranger.PostAsJsonAsync("/api/tasks", new
        {
            title = marker,
            dueDate = "2026-07-01",
            recurrence = new { type = "daily", interval = 1 }
        });
        var id = (await createResponse.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString();

        await _stranger.PutAsJsonAsync($"/api/tasks/{id}", new { status = "done" });

        var list = await (await _stranger.GetAsync($"/api/tasks?q={marker}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        list.GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Done_ПовторноеСохранениеDone_НеСпавнитВторойРаз()
    {
        await SetRecurrenceFlagAsync(true);
        try
        {
            var marker = "идемпотент_" + Guid.NewGuid().ToString("N")[..8];
            var task = await CreateTaskAsync(new
            {
                title = marker,
                dueDate = "2026-07-01",
                recurrence = new { type = "daily", interval = 1 }
            });
            var id = task.GetProperty("id").GetString();

            await _client.PutAsJsonAsync($"/api/tasks/{id}", new { status = "done" });
            // Второй PUT по уже завершённой задаче (wasDone == true) — спавна быть не должно
            await _client.PutAsJsonAsync($"/api/tasks/{id}", new { status = "done" });

            var list = await (await _client.GetAsync($"/api/tasks?q={marker}"))
                .Content.ReadFromJsonAsync<JsonElement>();
            list.GetArrayLength().Should().Be(2);
        }
        finally
        {
            await SetRecurrenceFlagAsync(false);
        }
    }
}
