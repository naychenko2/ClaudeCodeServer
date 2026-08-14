using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

public class ModelsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ModelsControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Get_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/models");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Get_AssignmentsRespectUserTierOverrides()
    {
        // Основной тестовый пользователь — admin (TestUsername), второй — user
        var admin = _factory.CreateAuthenticatedClient();
        var userClient = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        // Гарантируем известное состояние: у admin — личный strong, у user — наследование
        await admin.PutAsJsonAsync("/api/me/model-tiers", new { strong = "admin-opus", medium = "", weak = "" });
        await userClient.PutAsJsonAsync("/api/me/model-tiers", new { strong = "", medium = "", weak = "" });

        // GET /api/models отдаёт resolved assignments с учётом caller-id
        var response = await admin.GetAsync("/api/models");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        var assignments = body.GetProperty("assignments");
        assignments.GetProperty("chat-new").GetString().Should().Be("admin-opus");

        // У второго пользователя личного слота нет — назначение chat-new должно совпадать
        // с глобальным (null, если глобальный тоже пуст)
        var userResponse = await userClient.GetAsync("/api/models");
        userResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var userBody = JsonSerializer.Deserialize<JsonElement>(await userResponse.Content.ReadAsStringAsync());
        var userAssignments = userBody.GetProperty("assignments");
        userAssignments.GetProperty("chat-new").GetString().Should().BeNull();
    }

    [Fact]
    public async Task Preview_ЯчейкаПерсоныБезУровня_ИдётМодельюЯчейки()
    {
        // п.3 (запись tierStrong через API) + п.1 (preview): персона с заполненной ячейкой,
        // без явного уровня, в чате персоны идёт моделью своей ячейки (source=persona-cell).
        var admin = _factory.CreateAuthenticatedClient();

        var create = await admin.PostAsJsonAsync("/api/personas", new
        {
            name = "Тест-превью",
            tierStrong = "persona-opus",
        });
        create.StatusCode.Should().Be(HttpStatusCode.OK);
        var persona = JsonSerializer.Deserialize<JsonElement>(await create.Content.ReadAsStringAsync());
        var personaId = persona.GetProperty("id").GetString()!;
        // Ячейка сохранена и отдаётся в ответе (п.3 — проброс полей)
        persona.GetProperty("tierStrong").GetString().Should().Be("persona-opus");

        var resp = await admin.GetAsync($"/api/models/preview?place=chat-persona&personaId={personaId}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var d = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        d.GetProperty("model").GetString().Should().Be("persona-opus");
        d.GetProperty("source").GetString().Should().Be("persona-cell");
        d.GetProperty("tier").GetString().Should().Be("strong");
        d.GetProperty("tierOrigin").GetString().Should().Be("place");
    }

    [Fact]
    public async Task PresetUsage_СчитаетМестаПоСторам()
    {
        var admin = _factory.CreateAuthenticatedClient();
        var presetId = "pu-" + Guid.NewGuid().ToString("N");

        // Общий пресет
        (await admin.PutAsJsonAsync("/api/specialties/settings/global", new
        {
            specialties = new Dictionary<string, object>(),
            presets = new[] { new { id = presetId, name = "Где я", steps = new[] { "tier:strong" } } },
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        // Ставим пресет в личный слот strong текущего админа
        (await admin.PutAsJsonAsync("/api/me/model-tiers",
            new { strong = $"preset:{presetId}", medium = "", weak = "" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var resp = await admin.GetAsync($"/api/models/presets/{presetId}/usage");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        body.GetProperty("presetId").GetString().Should().Be(presetId);
        body.GetProperty("count").GetInt32().Should().BeGreaterThan(0);
        var kinds = body.GetProperty("usages").EnumerateArray()
            .Select(u => u.GetProperty("kind").GetString()).ToList();
        kinds.Should().Contain("owner-slot");
    }

    [Fact]
    public async Task Preview_СпециальностьБезПерсоны_ПоАпи()
    {
        var admin = _factory.CreateAuthenticatedClient();
        (await admin.PutAsJsonAsync("/api/specialties/settings/global", new
        {
            specialties = new Dictionary<string, object>
            {
                ["backendExecutor"] = new { tierStrong = "spec-opus", defaultTier = "strong" },
            },
            presets = Array.Empty<object>(),
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        // Без personaId/place — превью карточки специальности: модель из ячейки специальности
        var resp = await admin.GetAsync("/api/models/preview?specialty=backendExecutor");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var d = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        d.GetProperty("model").GetString().Should().Be("spec-opus");
        d.GetProperty("source").GetString().Should().Be("specialty-cell");
        d.GetProperty("tier").GetString().Should().Be("strong");
        d.GetProperty("tierOrigin").GetString().Should().Be("specialty");
    }

    // --- Контекст задачи (дефект A1): превью = боевая формула ExecutorModel ---

    private static JsonElement D(HttpResponseMessage r)
    {
        // 200 (GET/PUT/DELETE) и 201 (POST-создания) — оба валидные успехи
        r.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);
        return JsonSerializer.Deserialize<JsonElement>(r.Content.ReadAsStringAsync().Result);
    }

    [Fact]
    public async Task Preview_Задача_УровеньРазворачиваетсяПоМатрицеПерсоныИсполнителя()
    {
        // Дефект A1: у задачи с уровнем strong и персоной-исполнителем с ячейкой «сильная»
        // подпись раньше показывала модель слота ВЛАДЕЛЬЦА. Превью обязано совпадать с боевой
        // формулой (ExecutorModel: уровень задачи → матрица персоны), а не со слотом.
        var admin = _factory.CreateAuthenticatedClient();
        // Слот владельца — заведомо другой модели, чтобы поймать регрессию «взял слот»
        await admin.PutAsJsonAsync("/api/me/model-tiers", new { strong = "owner-slot-opus", medium = "", weak = "" });

        var create = await admin.PostAsJsonAsync("/api/personas", new
        {
            name = "Исполнитель превью",
            specialty = "backendExecutor",
            tierStrong = "persona-opus",
        });
        var persona = D(create);
        var personaId = persona.GetProperty("id").GetString()!;

        var taskResp = await admin.PostAsJsonAsync("/api/tasks", new
        {
            title = "Превью задачи",
            personaId,
            modelTier = "strong",
        });
        var task = D(taskResp);
        var taskId = task.GetProperty("id").GetString()!;

        var d = D(await admin.GetAsync($"/api/models/preview?taskId={taskId}"));
        d.GetProperty("model").GetString().Should().Be("persona-opus",
            "уровень задачи разворачивается по матрице ПЕРСОНЫ-исполнителя (боевая формула ExecutorModel)");
        d.GetProperty("source").GetString().Should().Be("persona-cell");
        d.GetProperty("tier").GetString().Should().Be("strong");
        d.GetProperty("tierOrigin").GetString().Should().Be("task");
    }

    [Fact]
    public async Task Preview_Задача_УровеньСильнееЯвнойМоделиПерсоны()
    {
        // Боевая формула: при заданном уровне задачи явная модель персоны НЕ участвует —
        // превью не должно её показывать (дефект «подпись врёт», аудит §2 «Задача»).
        var admin = _factory.CreateAuthenticatedClient();

        var create = await admin.PostAsJsonAsync("/api/personas", new
        {
            name = "Персона с моделью",
            model = "persona-explicit-glm",
            tierWeak = "persona-haiku",
        });
        var personaId = D(create).GetProperty("id").GetString()!;

        var taskResp = await admin.PostAsJsonAsync("/api/tasks", new
        {
            title = "Уровень сильнее модели",
            personaId,
            modelTier = "weak",
        });
        var taskId = D(taskResp).GetProperty("id").GetString()!;

        var d = D(await admin.GetAsync($"/api/models/preview?taskId={taskId}"));
        d.GetProperty("model").GetString().Should().Be("persona-haiku",
            "уровень задачи (weak) берёт слабую ячейку персоны, а не её явную модель");
        d.GetProperty("tierOrigin").GetString().Should().Be("task");
    }

    [Fact]
    public async Task Preview_ЗадачаБезУровня_МодельПерсоны()
    {
        // Без уровня задачи боевой ExecutorModel берёт модель персоны, без — её уровень
        // с дефолтом места tasks-executor (Strong).
        var admin = _factory.CreateAuthenticatedClient();

        var create = await admin.PostAsJsonAsync("/api/personas", new
        {
            name = "Персона без уровня задачи",
            model = "persona-explicit-glm",
        });
        var personaId = D(create).GetProperty("id").GetString()!;

        var taskResp = await admin.PostAsJsonAsync("/api/tasks", new
        {
            title = "Без уровня",
            personaId,
        });
        var taskId = D(taskResp).GetProperty("id").GetString()!;

        var d = D(await admin.GetAsync($"/api/models/preview?taskId={taskId}"));
        d.GetProperty("model").GetString().Should().Be("persona-explicit-glm");
        d.GetProperty("source").GetString().Should().Be("persona-model");
    }

    [Fact]
    public async Task Preview_ЗадачаБезПерсоныИУровня_НазначениеМеста()
    {
        // Задача без персоны и уровня: боевая формула отдаёт null → сессия берёт модель
        // по назначению места tasks-executor. Превью падает туда же (с заглушкой-персоной).
        var admin = _factory.CreateAuthenticatedClient();
        await admin.PutAsJsonAsync("/api/me/model-tiers", new { strong = "owner-slot-opus", medium = "", weak = "" });

        var taskResp = await admin.PostAsJsonAsync("/api/tasks", new { title = "Простая задача" });
        var taskId = D(taskResp).GetProperty("id").GetString()!;

        var d = D(await admin.GetAsync($"/api/models/preview?taskId={taskId}"));
        d.GetProperty("model").GetString().Should().Be("owner-slot-opus",
            "без персоны и уровня задача идёт слотом владельца дефолта места (Strong)");
        d.GetProperty("source").GetString().Should().Be("owner-slot");
    }

    [Fact]
    public async Task Preview_ЗадачаЧужая_404()
    {
        var admin = _factory.CreateAuthenticatedClient();
        var user = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var taskResp = await admin.PostAsJsonAsync("/api/tasks", new { title = "Чужая задача" });
        var taskId = D(taskResp).GetProperty("id").GetString()!;

        (await user.GetAsync($"/api/models/preview?taskId={taskId}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "чужая задача не раскрывается");
    }

    // --- Контекст чата (C1): превью = модель следующего хода сессии ---

    [Fact]
    public async Task Preview_Чат_ЗамороженнаяМодельИПутьЦепочки()
    {
        var admin = _factory.CreateAuthenticatedClient();
        // Слот strong = пресет: цепочка чата разворачивается в его шаги (B5 — путь цепочки)
        var presetId = "chat-" + Guid.NewGuid().ToString("N");
        (await admin.PutAsJsonAsync("/api/specialties/settings/global", new
        {
            specialties = new Dictionary<string, object>(),
            presets = new[] { new { id = presetId, name = "Рабочий каскад", steps = new[] { "opus", "glm-5.2", "deepseek" } } },
        })).StatusCode.Should().Be(HttpStatusCode.OK);
        await admin.PutAsJsonAsync("/api/me/model-tiers", new { strong = $"preset:{presetId}", medium = "", weak = "" });

        // Новый чат: модель не задана явно → резолв по месту chat-new... под флагом
        // default-personas создаётся персона; спрашиваем превью без модели и проверяем
        // заморозку/цепочку на сессии с ЯВНОЙ моделью.
        var chatResp = await admin.PostAsJsonAsync("/api/chats", new { mode = "auto", model = "glm-5.2" });
        var chat = D(chatResp);
        var chatId = chat.GetProperty("id").GetString()!;

        var d = D(await admin.GetAsync($"/api/models/preview?sessionId={chatId}"));
        // Явная модель чата заморожена (C1): путь = модель + хвост её тира (слот strong =
        // пресет «opus, glm-5.2, deepseek», glm-5.2 — второй шаг → хвост после неё)
        d.GetProperty("model").GetString().Should().Be("glm-5.2");
        d.GetProperty("frozen").GetBoolean().Should().BeTrue();
        d.GetProperty("source").GetString().Should().Be("explicit");
        var chain = d.GetProperty("chain").EnumerateArray().Select(c => c.GetString()).ToList();
        chain.Should().ContainInOrder(new[] { "glm-5.2", "deepseek" },
            "хвост тира: шаги цепочки слота strong после позиции glm-5.2");
    }

    [Fact]
    public async Task Preview_ЧатСоПустымиСлотами_Незаморожен_РешаетCLI()
    {
        var admin = _factory.CreateAuthenticatedClient();
        // Все слоты пусты (личные сброшены, глобальные не заданы): модель места не
        // резолвится — Session.Model остаётся пустой, чат НЕ заморожен (C1), ход решает CLI
        await admin.PutAsJsonAsync("/api/me/model-tiers", new { strong = "", medium = "", weak = "" });

        var chatResp = await admin.PostAsJsonAsync("/api/chats", new { mode = "auto", model = (string?)null });
        var chatId = D(chatResp).GetProperty("id").GetString()!;

        var d = D(await admin.GetAsync($"/api/models/preview?sessionId={chatId}"));
        d.GetProperty("model").GetString().Should().BeNull("пустые слоты — модель не определена, решает CLI");
        d.GetProperty("frozen").GetBoolean().Should().BeFalse("нечего замораживать: сессия без модели");
    }

    [Fact]
    public async Task Preview_ЧужойЧат_404()
    {
        var admin = _factory.CreateAuthenticatedClient();
        var user = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var chatResp = await admin.PostAsJsonAsync("/api/chats", new { mode = "auto", model = "glm-5.2" });
        var chatId = D(chatResp).GetProperty("id").GetString()!;

        (await user.GetAsync($"/api/models/preview?sessionId={chatId}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "чужая сессия не раскрывается");
    }

    // --- Переименование и удаление пресетов (спец. блок 6) ---

    [Fact]
    public async Task Пресет_ПереименованиеИУдалениеЛичногоПресета()
    {
        var admin = _factory.CreateAuthenticatedClient();
        var presetId = "own-" + Guid.NewGuid().ToString("N");
        // Личный пресет — слой вызывающего
        (await admin.PutAsJsonAsync("/api/specialties/settings/owner", new
        {
            specialties = new Dictionary<string, object>(),
            presets = new[] { new { id = presetId, name = "Черновик", steps = new[] { "opus" } } },
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        // Переименование
        var rename = await admin.PutAsJsonAsync($"/api/models/presets/{presetId}/name",
            new { name = "Рабочая цепочка" });
        rename.StatusCode.Should().Be(HttpStatusCode.OK);
        var renamed = JsonSerializer.Deserialize<JsonElement>(await rename.Content.ReadAsStringAsync());
        renamed.GetProperty("name").GetString().Should().Be("Рабочая цепочка");

        // Имя видно в объединённом списке настроек
        var settings = D(await admin.GetAsync("/api/specialties/settings"));
        settings.GetProperty("presets").EnumerateArray()
            .First(p => p.GetProperty("id").GetString() == presetId)
            .GetProperty("name").GetString().Should().Be("Рабочая цепочка");

        // Удаление: пресет выбран в личном слоте strong → ответ отдаёт места использования
        await admin.PutAsJsonAsync("/api/me/model-tiers", new { strong = $"preset:{presetId}", medium = "", weak = "" });
        var del = await admin.DeleteAsync($"/api/models/presets/{presetId}");
        del.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await del.Content.ReadAsStringAsync());
        body.GetProperty("preset").GetProperty("name").GetString().Should().Be("Рабочая цепочка");
        body.GetProperty("scope").GetString().Should().Be("owner");
        body.GetProperty("count").GetInt32().Should().BeGreaterThanOrEqualTo(1);
        body.GetProperty("usages").EnumerateArray()
            .Any(u => u.GetProperty("kind").GetString() == "owner-slot").Should().BeTrue();

        // Пресета больше нет в списке; usage показывает осиротевшую ссылку (удаление ссылки
        // не чистит — fail-open, ADR-007 §3), а сам слот чистим, чтобы не фонить в соседние
        // тесты класса (стор один на фабрику)
        var after = D(await admin.GetAsync($"/api/models/presets/{presetId}/usage"));
        after.GetProperty("count").GetInt32().Should().Be(1,
            "ссылка в личном слоте осталась битой — удаление пресета не чистит сторы");
        (await admin.GetAsync("/api/specialties/settings")).StatusCode.Should().Be(HttpStatusCode.OK);
        await admin.PutAsJsonAsync("/api/me/model-tiers", new { strong = "", medium = "", weak = "" });
    }

    [Fact]
    public async Task Пресет_ОбщийТолькоАдминПереименовываетИУдаляет()
    {
        var admin = _factory.CreateAuthenticatedClient();
        var user = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var presetId = "glob-" + Guid.NewGuid().ToString("N");
        (await admin.PutAsJsonAsync("/api/specialties/settings/global", new
        {
            specialties = new Dictionary<string, object>(),
            presets = new[] { new { id = presetId, name = "Общий", steps = new[] { "tier:strong" } } },
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        // Не-админ не трогает общий пресет
        (await user.PutAsJsonAsync($"/api/models/presets/{presetId}/name", new { name = "Взлом" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await user.DeleteAsync($"/api/models/presets/{presetId}"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Админ может и переименовать, и удалить
        (await admin.PutAsJsonAsync($"/api/models/presets/{presetId}/name", new { name = "Общий 2" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.DeleteAsync($"/api/models/presets/{presetId}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Пресет_УдалениеНеРоняетПревью_БитаяСсылкаБезопасна()
    {
        // ADR-007 §3: удаление оставляет битые ссылки fail-open. Превью места с битой
        // ссылкой не падает — показывает «пресет удалён» (broken) вместо модели.
        var admin = _factory.CreateAuthenticatedClient();
        var presetId = "broken-" + Guid.NewGuid().ToString("N");
        (await admin.PutAsJsonAsync("/api/specialties/settings/global", new
        {
            specialties = new Dictionary<string, object>(),
            presets = new[] { new { id = presetId, name = "Ломаем", steps = new[] { "opus" } } },
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var create = await admin.PostAsJsonAsync("/api/personas", new
        {
            name = "Персона с пресетом",
            tierStrong = $"preset:{presetId}",
        });
        var personaId = D(create).GetProperty("id").GetString()!;

        (await admin.DeleteAsync($"/api/models/presets/{presetId}")).StatusCode.Should().Be(HttpStatusCode.OK);

        var d = D(await admin.GetAsync($"/api/models/preview?place=chat-persona&personaId={personaId}"));
        d.GetProperty("preset").GetProperty("broken").GetBoolean().Should().BeTrue();
        d.GetProperty("model").GetString().Should().BeNull();
    }
}
