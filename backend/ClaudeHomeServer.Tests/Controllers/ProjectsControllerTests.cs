using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services.Images;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

public class ProjectsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly string _tempProjectDir;

    public ProjectsControllerTests(TestWebApplicationFactory factory)
    {
        _client = factory.CreateAuthenticatedClient();
        _tempProjectDir = Path.Combine(factory.TempDir, "projects");
        Directory.CreateDirectory(_tempProjectDir);
    }

    private string MkProjectDir(string name)
    {
        var dir = Path.Combine(_tempProjectDir, name + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private async Task<JsonElement> CreateProjectAsync(string name, string? dir = null)
    {
        var path = dir ?? MkProjectDir(name);
        var response = await _client.PostAsJsonAsync("/api/projects", new { name, rootPath = path });
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    [Fact]
    public async Task GetAll_ReturnsOk()
    {
        var response = await _client.GetAsync("/api/projects");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Create_ValidRequest_Returns201WithProject()
    {
        var dir = MkProjectDir("new");
        var response = await _client.PostAsJsonAsync("/api/projects", new
        {
            name = "TestProject",
            rootPath = dir
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("name").GetString().Should().Be("TestProject");
        body.GetProperty("id").GetString().Should().NotBeEmpty();
    }

    [Fact]
    public async Task Create_NonExistentDir_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/projects", new
        {
            name = "Bad",
            rootPath = @"C:\nonexistent\path_" + Guid.NewGuid()
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetById_ExistingProject_Returns200()
    {
        var project = await CreateProjectAsync("GetByIdTest");
        var id = project.GetProperty("id").GetString()!;

        var response = await _client.GetAsync($"/api/projects/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("id").GetString().Should().Be(id);
    }

    [Fact]
    public async Task GetById_NonExistentProject_Returns404()
    {
        var response = await _client.GetAsync("/api/projects/nonexistent-id");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Update_ExistingProject_Returns200WithUpdatedName()
    {
        var project = await CreateProjectAsync("Original");
        var id = project.GetProperty("id").GetString()!;

        var response = await _client.PutAsJsonAsync($"/api/projects/{id}", new
        {
            name = "Updated",
            rootPath = (string?)null
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("name").GetString().Should().Be("Updated");
    }

    [Fact]
    public async Task Update_McpServersOn_PersistedAndReturnedByGet()
    {
        var project = await CreateProjectAsync("McpOn");
        var id = project.GetProperty("id").GetString()!;

        var putResponse = await _client.PutAsJsonAsync($"/api/projects/{id}", new
        {
            name = (string?)null,
            rootPath = (string?)null,
            mcpServersOn = new[] { "context7" }
        });
        putResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var putBody = JsonSerializer.Deserialize<JsonElement>(await putResponse.Content.ReadAsStringAsync());
        putBody.GetProperty("mcpServersOn").EnumerateArray().Select(e => e.GetString()).Should().Equal("context7");

        var getResponse = await _client.GetAsync($"/api/projects/{id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var getBody = JsonSerializer.Deserialize<JsonElement>(await getResponse.Content.ReadAsStringAsync());
        getBody.GetProperty("mcpServersOn").EnumerateArray().Select(e => e.GetString()).Should().Equal("context7");
    }

    [Fact]
    public async Task Update_NonExistentProject_Returns404()
    {
        var response = await _client.PutAsJsonAsync("/api/projects/nope", new
        {
            name = "X",
            rootPath = (string?)null
        });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Update_NonExistentNewPath_Returns400()
    {
        var project = await CreateProjectAsync("ToUpdate");
        var id = project.GetProperty("id").GetString()!;

        var response = await _client.PutAsJsonAsync($"/api/projects/{id}", new
        {
            name = (string?)null,
            rootPath = @"C:\fake_nonexistent_" + Guid.NewGuid()
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Delete_ExistingProject_Returns204()
    {
        var project = await CreateProjectAsync("ToDelete");
        var id = project.GetProperty("id").GetString()!;

        var response = await _client.DeleteAsync($"/api/projects/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Delete_NonExistentProject_Returns404()
    {
        var response = await _client.DeleteAsync("/api/projects/ghost-id");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_ThenGetById_Returns404()
    {
        var project = await CreateProjectAsync("DeleteThenGet");
        var id = project.GetProperty("id").GetString()!;
        await _client.DeleteAsync($"/api/projects/{id}");

        var response = await _client.GetAsync($"/api/projects/{id}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // --- PUT /api/projects/{id}/tags (реестр общих тегов) ---

    [Fact]
    public async Task UpdateTags_УспешныйReorder_СохраняетПорядокИСостав()
    {
        var project = await CreateProjectAsync("TagsTest");
        var id = project.GetProperty("id").GetString()!;

        var tags = new[]
        {
            new { name = "Bug", order = 0, color = "red" },
            new { name = "Feature", order = 1, color = "green" },
            new { name = "Refactor", order = 2, color = "yellow" }
        };

        var response = await _client.PutAsJsonAsync($"/api/projects/{id}/tags", tags);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        var registry = body.GetProperty("tagRegistry").EnumerateArray().ToList();

        registry.Should().HaveCount(3);
        registry[0].GetProperty("name").GetString().Should().Be("Bug");
        registry[0].GetProperty("order").GetInt32().Should().Be(0);
        registry[0].GetProperty("color").GetString().Should().Be("red");
        registry[1].GetProperty("name").GetString().Should().Be("Feature");
        registry[2].GetProperty("name").GetString().Should().Be("Refactor");
    }

    [Fact]
    public async Task UpdateTags_OrderНормализуетсяПоПозицииМассива()
    {
        var project = await CreateProjectAsync("OrderTest");
        var id = project.GetProperty("id").GetString()!;

        // Передаём order в случайном порядке — контроллер должен нормализовать по позиции
        var tags = new[]
        {
            new { name = "Third", order = 99, color = (string?)null },
            new { name = "First", order = -5, color = (string?)null },
            new { name = "Second", order = 0, color = (string?)null }
        };

        var response = await _client.PutAsJsonAsync($"/api/projects/{id}/tags", tags);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        var registry = body.GetProperty("tagRegistry").EnumerateArray().ToList();

        registry[0].GetProperty("order").GetInt32().Should().Be(0);
        registry[1].GetProperty("order").GetInt32().Should().Be(1);
        registry[2].GetProperty("order").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task UpdateTags_ПустойИмя_Возвращает400()
    {
        var project = await CreateProjectAsync("EmptyNameTest");
        var id = project.GetProperty("id").GetString()!;

        var tags = new[]
        {
            new { name = "Valid", order = 0, color = (string?)null },
            new { name = "", order = 1, color = (string?)null }, // пустое имя
            new { name = "AlsoValid", order = 2, color = (string?)null }
        };

        var response = await _client.PutAsJsonAsync($"/api/projects/{id}/tags", tags);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        error.GetProperty("error").GetString().Should().Contain("Тег #2");
    }

    [Fact]
    public async Task UpdateTags_ДубликатыИменCaseInsensitive_Возвращает400()
    {
        var project = await CreateProjectAsync("DupTest");
        var id = project.GetProperty("id").GetString()!;

        var tags = new[]
        {
            new { name = "Bug", order = 0, color = "red" },
            new { name = "bug", order = 1, color = "blue" }, // дубликат (case-insensitive)
            new { name = "BUG", order = 2, color = "green" }  // ещё один дубликат
        };

        var response = await _client.PutAsJsonAsync($"/api/projects/{id}/tags", tags);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        error.GetProperty("error").GetString().Should().Contain("уникальными");
    }

    [Fact]
    public async Task UpdateTags_ЧужойПроект_Возвращает403()
    {
        // Создаём проект от первого пользователя
        var project = await CreateProjectAsync("OwnerProject");
        var id = project.GetProperty("id").GetString()!;

        // Создаём клиент от второго пользователя
        var factory = new TestWebApplicationFactory();
        var otherClient = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername,
            TestWebApplicationFactory.SecondPassword);

        var tags = new[]
        {
            new { name = "Tag", order = 0, color = "red" }
        };

        var response = await otherClient.PutAsJsonAsync($"/api/projects/{id}/tags", tags);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateTags_НесуществующийПроект_Возвращает404()
    {
        var tags = new[]
        {
            new { name = "Tag", order = 0, color = "red" }
        };

        var response = await _client.PutAsJsonAsync("/api/projects/nonexistent/tags", tags);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateTags_ПустойСписок_ОчищаетРеестр()
    {
        var project = await CreateProjectAsync("ClearTest");
        var id = project.GetProperty("id").GetString()!;

        // Сначала добавляем теги
        var tags = new[]
        {
            new { name = "Tag1", order = 0, color = "red" },
            new { name = "Tag2", order = 1, color = "blue" }
        };
        await _client.PutAsJsonAsync($"/api/projects/{id}/tags", tags);

        // Затем очищаем
        var response = await _client.PutAsJsonAsync($"/api/projects/{id}/tags", new List<object>());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("tagRegistry").GetArrayLength().Should().Be(0);
    }

    // === Значок проекта (ADR-009 §8): suggest/select/mode/icon.svg ===

    // Стаб места модели: отвечает заготовленным JSON на любой вызов — как будто
    // «Поставщики моделей» настроены и модель ответила по контракту ADR-009 §2.2
    private sealed class StubCheapRunner(string reply) : ClaudeHomeServer.Services.Llm.ICheapTextRunner
    {
        public bool UsesLocal(string actionKey) => false;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "stub";
        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
            => Task.FromResult(reply);
        public Task<string?> RunFreeAsync(string actionKey, string prompt, object? jsonFormat = null,
            CancellationToken ct = default) => Task.FromResult<string?>(reply);
        public Task<string?> RunLocalOnlyAsync(string actionKey, string prompt, CancellationToken ct = default)
            => Task.FromResult<string?>(reply);
        public Task<ClaudeHomeServer.Services.Llm.OneShotResult> RunDetailedAsync(string actionKey,
            string prompt, string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default)
            => Task.FromResult(new ClaudeHomeServer.Services.Llm.OneShotResult(reply, null, 0));
    }

    // Годный ответ модели по контракту: 2 имени + 2 нарисованных, вперемешку
    private const string ModelGlyphsReply =
        """{"glyphs":[{"name":"piggy-bank"},{"name":"chart-line"},{"paths":["M3 21h18","M6 21V9l6-4 6 4v12"]},{"paths":["M4 18l5-4 4 4 7-4"]}]}""";

    [Fact]
    public async Task SuggestIcon_МодельНеНастроена_ПустыеКандидатыИПроектНеИзменён()
    {
        var project = await CreateProjectAsync("SuggestNoModel");
        var id = project.GetProperty("id").GetString()!;

        var response = await _client.PostAsJsonAsync($"/api/projects/{id}/icon/suggest", new { });

        // Сбой модели — фолбэк, а не ошибка: пустой набор с причиной, проект на инициалах (ADR-009 §7)
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("candidates").GetArrayLength().Should().Be(0);
        body.GetProperty("failReason").GetString().Should().NotBeNullOrEmpty();

        var after = JsonSerializer.Deserialize<JsonElement>(
            await (await _client.GetAsync($"/api/projects/{id}")).Content.ReadAsStringAsync());
        after.GetProperty("icon").GetProperty("kind").GetString().Should().Be("initials");
        after.GetProperty("icon").GetProperty("glyph").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task SuggestIcon_ОтветМодели_ДоЧетырёхКандидатовВперемешку()
    {
        using var factory = new TestWebApplicationFactory
        {
            ExtraServices = s => s.AddSingleton<ClaudeHomeServer.Services.Llm.ICheapTextRunner>(
                new StubCheapRunner(ModelGlyphsReply)),
        };
        var client = factory.CreateAuthenticatedClient();
        var dir = Path.Combine(factory.TempDir, "glyph-project");
        Directory.CreateDirectory(dir);
        var created = await client.PostAsJsonAsync("/api/projects", new { name = "GlyphTest", rootPath = dir });
        created.EnsureSuccessStatusCode();
        var id = JsonSerializer.Deserialize<JsonElement>(
            await created.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;

        var response = await client.PostAsJsonAsync($"/api/projects/{id}/icon/suggest", new { prompt = "копилка" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        var candidates = body.GetProperty("candidates");
        candidates.GetArrayLength().Should().Be(4);
        // Виды вперемешку: у первых двух только name, у остальных только paths
        candidates[0].TryGetProperty("name", out var nameEl).Should().BeTrue();
        nameEl.GetString().Should().Be("piggy-bank");
        candidates[0].TryGetProperty("paths", out _).Should().BeFalse();
        candidates[2].GetProperty("paths").GetArrayLength().Should().Be(2);

        // Стор не меняется: candidates нигде не хранятся между вызовами (ADR-009 §8)
        var after = JsonSerializer.Deserialize<JsonElement>(
            await (await client.GetAsync($"/api/projects/{id}")).Content.ReadAsStringAsync());
        after.GetProperty("icon").GetProperty("kind").GetString().Should().Be("initials");
    }

    // Стаб, чувствительный к пожеланию: годный JSON только когда подсказка дошла до
    // построенного промпта; иначе «модель отвечает мусором», парсер отвергает —
    // так тест ловит молчаливую потерю поля на биндинге (QA 2026-08-17: фронт шлёт "prompt")
    private sealed class HintSensitiveCheapRunner(string hintMarker) : ClaudeHomeServer.Services.Llm.ICheapTextRunner
    {
        public string? LastPrompt { get; private set; }

        public bool UsesLocal(string actionKey) => false;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "stub";
        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            LastPrompt = prompt;
            return Task.FromResult(prompt.Contains(hintMarker, StringComparison.Ordinal) ? ModelGlyphsReply : "");
        }
        public Task<string?> RunFreeAsync(string actionKey, string prompt, object? jsonFormat = null,
            CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> RunLocalOnlyAsync(string actionKey, string prompt, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
        public Task<ClaudeHomeServer.Services.Llm.OneShotResult> RunDetailedAsync(string actionKey,
            string prompt, string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default)
            => Task.FromResult(new ClaudeHomeServer.Services.Llm.OneShotResult("", null, 0));
    }

    [Fact]
    public async Task SuggestIcon_ПодсказкаИзПоляPrompt_ДоходитДоМоделиИВлияетНаРезультат()
    {
        // Тест обязан идти через реальный пайплайн биндинга (PostAsJsonAsync): при откате
        // [JsonPropertyName("prompt")] поле Hint не распакуется из тела, пожелание выпадет
        // из промпта — и стаб ответит мусором вместо кандидатов.
        var cheap = new HintSensitiveCheapRunner("копилка с монетками");
        using var factory = new TestWebApplicationFactory
        {
            ExtraServices = s => s.AddSingleton<ClaudeHomeServer.Services.Llm.ICheapTextRunner>(cheap),
        };
        var client = factory.CreateAuthenticatedClient();
        var dir = Path.Combine(factory.TempDir, "glyph-hint-project");
        Directory.CreateDirectory(dir);
        var created = await client.PostAsJsonAsync("/api/projects",
            new { name = "GlyphHintTest", rootPath = dir });
        created.EnsureSuccessStatusCode();
        var id = JsonSerializer.Deserialize<JsonElement>(
            await created.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;

        var response = await client.PostAsJsonAsync($"/api/projects/{id}/icon/suggest",
            new { prompt = "копилка с монетками" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        // Кандидаты есть только потому, что пожелание дошло до промпта и изменило ответ модели
        body.GetProperty("candidates").GetArrayLength().Should().Be(4);
        body.GetProperty("failReason").ValueKind.Should().Be(JsonValueKind.Null);
        cheap.LastPrompt.Should().Contain("Что изобразить (пожелание владельца): копилка с монетками");
    }

    [Fact]
    public async Task SuggestIconPreview_МаршрутЗарегистрирован_Отвечает200СКандидатами()
    {
        // Дефект волны: маршрут не был зарегистрирован — POST отдавал 405. Любой
        // не-200 (405/404) роняет тест, поэтому он чувствителен к снятию регистрации.
        using var factory = new TestWebApplicationFactory
        {
            ExtraServices = s => s.AddSingleton<ClaudeHomeServer.Services.Llm.ICheapTextRunner>(
                new StubCheapRunner(ModelGlyphsReply)),
        };
        var client = factory.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/projects/icon/suggest-preview",
            new { name = "Домашняя бухгалтерия", prompt = "копилка" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("candidates").GetArrayLength().Should().Be(4);
        body.GetProperty("failReason").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task SelectIcon_ИмяИзНабора_УстанавливаетЗначок()
    {
        var project = await CreateProjectAsync("SelectName");
        var id = project.GetProperty("id").GetString()!;

        var response = await _client.PostAsJsonAsync($"/api/projects/{id}/icon/select",
            new { name = "wallet" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        var icon = body.GetProperty("icon");
        icon.GetProperty("kind").GetString().Should().Be("glyph");
        icon.GetProperty("glyph").GetProperty("name").GetString().Should().Be("wallet");
        icon.GetProperty("glyph").GetProperty("v").GetString().Should().HaveLength(8);

        // Name-значок файла не имеет — icon.svg отдаёт 404 (ADR-009 §7, последняя строка)
        var svg = await _client.GetAsync($"/api/projects/{id}/icon.svg");
        svg.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SelectIcon_Пути_ОтдаютСобранныйСерверомSvg()
    {
        var project = await CreateProjectAsync("SelectPaths");
        var id = project.GetProperty("id").GetString()!;

        // Пример из ADR-009 §2.2 с координатами -5/-6 не проходит его же габарит §3.4 —
        // здесь годные данные (тот же приём, что в тестах сервиса)
        var response = await _client.PostAsJsonAsync($"/api/projects/{id}/icon/select",
            new { paths = new[] { "M3 21h18", "M6 21V9l6-4 6 4v12" } });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var svg = await _client.GetAsync($"/api/projects/{id}/icon.svg");

        svg.StatusCode.Should().Be(HttpStatusCode.OK);
        svg.Content.Headers.ContentType!.ToString().Should().Be("image/svg+xml");
        svg.Headers.CacheControl!.ToString().Should().Contain("max-age=604800");
        svg.Headers.CacheControl!.NoStore.Should().BeFalse();
        svg.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        svg.Headers.GetValues("Content-Security-Policy").Should().Equal("default-src 'none'");
        var content = await svg.Content.ReadAsStringAsync();
        content.Should().StartWith("<svg ");
        content.Should().Contain("<path d=\"M3 21h18\"");
        content.Should().Contain("stroke=\"currentColor\"");
        // Ни байта разметки от модели: путь едет экранированным атрибутом, не тегом
        content.Should().NotContainAny("<script", "onload");
    }

    [Theory]
    [InlineData("not-a-lucide-name", null)]      // имя вне белого списка
    [InlineData("wallet", "M3 21h18")]           // оба вида сразу
    [InlineData(null, "M0 0e5 5")]               // экспонента в d
    public async Task SelectIcon_НегодноеТело_400ИСторНеМеняется(string? name, string? path)
    {
        var project = await CreateProjectAsync("SelectBad");
        var id = project.GetProperty("id").GetString()!;

        object body = name is not null && path is not null
            ? new { name, paths = new[] { path } }
            : name is not null ? new { name } : new { paths = new[] { path! } };
        var response = await _client.PostAsJsonAsync($"/api/projects/{id}/icon/select", body);

        // Валидация стоит на входе в стор: клиент — такой же недоверенный источник, как
        // модель (ADR-009 §11.3)
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var after = JsonSerializer.Deserialize<JsonElement>(
            await (await _client.GetAsync($"/api/projects/{id}")).Content.ReadAsStringAsync());
        after.GetProperty("icon").GetProperty("kind").GetString().Should().Be("initials");
    }

    [Fact]
    public async Task SelectIcon_СыраяРазметкаВПутях_400()
    {
        var project = await CreateProjectAsync("SelectRaw");
        var id = project.GetProperty("id").GetString()!;

        var response = await _client.PostAsJsonAsync($"/api/projects/{id}/icon/select",
            new { paths = new[] { "<svg onload=alert(1)>" } });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SetIconMode_ИнициалыИЗначок_ПереключаютсяБезПотериЗначка()
    {
        var project = await CreateProjectAsync("ModeGlyph");
        var id = project.GetProperty("id").GetString()!;

        // Значка ещё нет — переходить на glyph некуда
        var early = await _client.PostAsJsonAsync($"/api/projects/{id}/icon/mode", new { kind = "glyph" });
        early.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await _client.PostAsJsonAsync($"/api/projects/{id}/icon/select", new { name = "rocket" });

        var toInitials = await _client.PostAsJsonAsync($"/api/projects/{id}/icon/mode", new { kind = "initials" });
        toInitials.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await toInitials.Content.ReadAsStringAsync());
        body.GetProperty("icon").GetProperty("kind").GetString().Should().Be("initials");
        // Значок не стёрт — путь «снова вперёд» без повторного подбора
        body.GetProperty("icon").GetProperty("glyph").GetProperty("name").GetString().Should().Be("rocket");

        var backToGlyph = await _client.PostAsJsonAsync($"/api/projects/{id}/icon/mode", new { kind = "glyph" });
        backToGlyph.StatusCode.Should().Be(HttpStatusCode.OK);
        var body2 = JsonSerializer.Deserialize<JsonElement>(await backToGlyph.Content.ReadAsStringAsync());
        body2.GetProperty("icon").GetProperty("kind").GetString().Should().Be("glyph");
    }

    [Fact]
    public async Task IconЭндпоинты_ЧужойПроект_404()
    {
        var project = await CreateProjectAsync("ForeignGlyph");
        var id = project.GetProperty("id").GetString()!;

        using var factory = new TestWebApplicationFactory();
        var other = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        (await other.PostAsJsonAsync($"/api/projects/{id}/icon/suggest", new { }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.PostAsJsonAsync($"/api/projects/{id}/icon/select", new { name = "wallet" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.PostAsJsonAsync($"/api/projects/{id}/icon/mode", new { kind = "glyph" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.GetAsync($"/api/projects/{id}/icon.svg"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
