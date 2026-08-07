using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// Онбординги (фича default-personas-onboarding): старт/резюм сессий, идемпотентность
// double-start, кейс удалённой сессии, 400 без личного дефолта у проектного онбординга,
// финализация через make-default из онбординг-сессии и MCP-гейт обычного чата.
public class OnboardingControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly string _tempDir;

    public OnboardingControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
        _tempDir = Path.Combine(factory.TempDir, "onboarding_tests");
        Directory.CreateDirectory(_tempDir);
    }

    // Чат вне проекта требует домашнюю папку владельца — задаём DefaultProjectsPath
    // через настройки (пути только от TempDir, без Windows-литералов — CI на Linux)
    private async Task EnsureHomeConfiguredAsync()
    {
        var homes = Path.Combine(_factory.TempDir, "homes");
        Directory.CreateDirectory(homes);
        var response = await _client.PutAsJsonAsync("/api/settings", new { defaultProjectsPath = homes });
        response.EnsureSuccessStatusCode();
    }

    private async Task<JsonElement> PostJsonAsync(string url, object? body = null)
    {
        var response = body is null
            ? await _client.PostAsync(url, null)
            : await _client.PostAsJsonAsync(url, body);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }

    private async Task<string> CreateProjectAsync()
    {
        var dir = Path.Combine(_tempDir, "proj_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var project = await PostJsonAsync("/api/projects", new { name = "OnboardingProject", rootPath = dir });
        return project.GetProperty("id").GetString()!;
    }

    private async Task<string> CreateGlobalPersonaAsync(string name)
    {
        var persona = await PostJsonAsync("/api/personas", new { name });
        return persona.GetProperty("id").GetString()!;
    }

    // Создание персоны из чата-онбординга (через MCP personas_create): POST /api/personas
    // с заголовком сессии-вызывателя — так бэкенд помечает её как созданную в онбординге
    // (OnboardingCreatedPersonaId), и финализация досевает профиль дефолта именно ей.
    private async Task<string> CreateGlobalPersonaFromSessionAsync(string name, string sessionId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/personas");
        request.Headers.Add("X-Caller-Session-Id", sessionId);
        request.Content = JsonContent.Create(new { name });
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(
            await response.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
    }

    // make-default с заголовком сессии-вызывателя (путь MCP personas_set_default)
    private async Task<HttpResponseMessage> MakeDefaultFromSessionAsync(string personaId, string sessionId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/personas/{personaId}/make-default");
        request.Headers.Add("X-Caller-Session-Id", sessionId);
        return await _client.SendAsync(request);
    }

    [Fact]
    public async Task StartUser_СоздаётОнбордингЧат_ИИдемпотентен()
    {
        await EnsureHomeConfiguredAsync();

        var first = await PostJsonAsync("/api/onboarding/user/start");
        first.GetProperty("onboardingKind").GetString().Should().Be("user");
        first.GetProperty("personaId").ValueKind.Should().Be(JsonValueKind.Null,
            "онбординг первого входа ведёт системный мастер, а не персона");

        // Двойной start (две вкладки) не плодит вторую сессию
        var second = await PostJsonAsync("/api/onboarding/user/start");
        second.GetProperty("id").GetString().Should().Be(first.GetProperty("id").GetString());
    }

    [Fact]
    public async Task StartUser_ПослеУдаленияСессии_СоздаётНовую()
    {
        await EnsureHomeConfiguredAsync();

        var first = await PostJsonAsync("/api/onboarding/user/start");
        var firstId = first.GetProperty("id").GetString()!;
        (await _client.DeleteAsync($"/api/chats/{firstId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        // OnboardingSessionId указывает на удалённую сессию → создаётся новая
        var second = await PostJsonAsync("/api/onboarding/user/start");
        second.GetProperty("id").GetString().Should().NotBe(firstId);
        second.GetProperty("onboardingKind").GetString().Should().Be("user");
    }

    [Fact]
    public async Task StartUser_ЗапускаетПервыйХодМастера()
    {
        await EnsureHomeConfiguredAsync();

        var onboarding = await PostJsonAsync("/api/onboarding/user/start");
        var sessionId = onboarding.GetProperty("id").GetString()!;

        // Свежесозданная сессия: мастер здоровается первым — гейт не открывается «немым».
        // Пользователь видит первый вопрос, а не пустой экран под плашкой интервью.
        var adapter = _factory.LlmAdapters.Adapters[sessionId];
        adapter.SentMessages.Should().ContainSingle("старт онбординга запускает ровно один ход мастера");
        adapter.SentMessages[0].Should().Contain("онбординг",
            "первый ход — серверная директива kickoff (не пользовательская реплика)");
    }

    [Fact]
    public async Task StartUser_ДвойнойStart_НеДублируетХодМастера()
    {
        await EnsureHomeConfiguredAsync();

        var first = await PostJsonAsync("/api/onboarding/user/start");
        var sessionId = first.GetProperty("id").GetString()!;

        // Резюм: второй start (вторая вкладка, повторный логин) возвращает существующую
        // сессию — повторный kickoff НЕ запускается, иначе первая реплика мастера
        // задублировалась бы и сбила интервью
        var second = await PostJsonAsync("/api/onboarding/user/start");
        second.GetProperty("id").GetString().Should().Be(sessionId);

        var adapter = _factory.LlmAdapters.Adapters[sessionId];
        adapter.SentMessages.Should().ContainSingle("резюм не запускает второй kickoff");
    }

    [Fact]
    public async Task StartProject_БезЛичногоДефолта_400()
    {
        await EnsureHomeConfiguredAsync();
        // Второй пользователь — у него точно нет дефолт-персоны (изоляция от других тестов класса)
        var second = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var dir = Path.Combine(_tempDir, "proj2_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var projectResp = await second.PostAsJsonAsync("/api/projects", new { name = "NoDefault", rootPath = dir });
        projectResp.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(
            await projectResp.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;

        var response = await second.PostAsync($"/api/onboarding/project/{projectId}/start", null);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("дефолт-персоны");
    }

    [Fact]
    public async Task StartProject_СДефолтом_СоздаётПроектнуюСессиюСПерсоной()
    {
        await EnsureHomeConfiguredAsync();
        var personaId = await CreateGlobalPersonaAsync("Проводница");
        (await _client.PostAsync($"/api/personas/{personaId}/make-default", null)).EnsureSuccessStatusCode();
        var projectId = await CreateProjectAsync();

        var first = await PostJsonAsync($"/api/onboarding/project/{projectId}/start");
        first.GetProperty("onboardingKind").GetString().Should().Be("project");
        first.GetProperty("projectId").GetString().Should().Be(projectId);
        first.GetProperty("personaId").GetString().Should().Be(personaId,
            "онбординг проекта ведёт личная дефолт-персона");

        // Идемпотентность double-start
        var second = await PostJsonAsync($"/api/onboarding/project/{projectId}/start");
        second.GetProperty("id").GetString().Should().Be(first.GetProperty("id").GetString());
    }

    [Fact]
    public async Task StartProject_СДефолтом_ЗапускаетПервыйХодПерсоны()
    {
        await EnsureHomeConfiguredAsync();
        var personaId = await CreateGlobalPersonaAsync("Проводница");
        (await _client.PostAsync($"/api/personas/{personaId}/make-default", null)).EnsureSuccessStatusCode();
        var projectId = await CreateProjectAsync();

        var onboarding = await PostJsonAsync($"/api/onboarding/project/{projectId}/start");
        var sessionId = onboarding.GetProperty("id").GetString()!;

        // Проектный онбординг: первую реплику подаёт личная дефолт-персона (не пустой экран) —
        // тот же паттерн, что у личного онбординга, но ведёт персона, а не системный мастер
        var adapter = _factory.LlmAdapters.Adapters[sessionId];
        adapter.SentMessages.Should().ContainSingle("старт проектного онбординга запускает один ход дефолт-персоны");
    }

    [Fact]
    public async Task Финализация_ИзОнбордингСессии_СтавитДефолтДосеваетПрофильИЧиститСессию()
    {
        await EnsureHomeConfiguredAsync();

        var onboarding = await PostJsonAsync("/api/onboarding/user/start");
        var sessionId = onboarding.GetProperty("id").GetString()!;
        // Мастер создаёт персону через MCP из онбординг-сессии (header X-Caller-Session-Id) —
        // бэкенд помечает её как созданную в онбординге, финализация досевает профиль дефолта
        var personaId = await CreateGlobalPersonaFromSessionAsync("Созданная мастером", sessionId);

        (await MakeDefaultFromSessionAsync(personaId, sessionId)).StatusCode.Should().Be(HttpStatusCode.OK);

        // Дефолт назначен, онбординг-сессия очищена
        var me = JsonSerializer.Deserialize<JsonElement>(
            await (await _client.GetAsync("/api/auth/me")).Content.ReadAsStringAsync());
        me.GetProperty("defaultPersonaId").GetString().Should().Be(personaId);
        me.GetProperty("onboardingSessionId").ValueKind.Should().Be(JsonValueKind.Null);

        // Досев профиля дефолта: Coordinator + Tool-привязки personas-manage/tasks/notes
        var persona = JsonSerializer.Deserialize<JsonElement>(
            await (await _client.GetAsync($"/api/personas/{personaId}")).Content.ReadAsStringAsync());
        persona.GetProperty("specialty").GetString().Should().Be("coordinator");
        var targets = persona.GetProperty("bindings").EnumerateArray()
            .Select(b => b.GetProperty("target").GetString()).ToList();
        targets.Should().Contain(["personas-manage", "tasks", "notes"]);

        // «Просыпание»: персона назначена собеседником онбординг-сессии
        var chat = JsonSerializer.Deserialize<JsonElement>(
            await (await _client.GetAsync($"/api/chats/{sessionId}")).Content.ReadAsStringAsync());
        chat.GetProperty("personaId").GetString().Should().Be(personaId);
    }

    [Fact]
    public async Task Финализация_ВыборСуществующейПерсоны_НеДосеваетПрофиль()
    {
        await EnsureHomeConfiguredAsync();

        // Персона, созданная ВНЕ онбординга (пользователь выбрал из существующих):
        // создавалась без header онбординг-сессии → OnboardingCreatedPersonaId не проставлен
        var personaId = await CreateGlobalPersonaAsync("Существующая");
        var onboarding = await PostJsonAsync("/api/onboarding/user/start");
        var sessionId = onboarding.GetProperty("id").GetString()!;

        (await MakeDefaultFromSessionAsync(personaId, sessionId)).StatusCode.Should().Be(HttpStatusCode.OK);

        // Дефолт назначен, онбординг завершён — но профиль НЕ досевался:
        // права выбранной персоны как были, так и остались (молчаливая эскалация запрещена)
        var me = JsonSerializer.Deserialize<JsonElement>(
            await (await _client.GetAsync("/api/auth/me")).Content.ReadAsStringAsync());
        me.GetProperty("defaultPersonaId").GetString().Should().Be(personaId);
        me.GetProperty("onboardingSessionId").ValueKind.Should().Be(JsonValueKind.Null);

        var persona = JsonSerializer.Deserialize<JsonElement>(
            await (await _client.GetAsync($"/api/personas/{personaId}")).Content.ReadAsStringAsync());
        persona.GetProperty("specialty").GetString().Should().Be("none",
            "выбор существующей персоны не должен досевать Coordinator");
        var hasManageBinding = persona.TryGetProperty("bindings", out var bindings)
            && bindings.ValueKind == JsonValueKind.Array
            && bindings.EnumerateArray().Any(b => b.GetProperty("target").GetString() == "personas-manage");
        hasManageBinding.Should().BeFalse("выбор существующей персоны не должен досевать personas-manage");
    }

    [Fact]
    public async Task MakeDefault_ИзОбычногоЧата_400()
    {
        await EnsureHomeConfiguredAsync();
        // Обычный чат (без OnboardingKind) — MCP-путь make-default обязан отказать
        var chat = await PostJsonAsync("/api/chats", new { mode = "auto" });
        var chatId = chat.GetProperty("id").GetString()!;
        var personaId = await CreateGlobalPersonaAsync("Самозванка");

        var response = await MakeDefaultFromSessionAsync(personaId, chatId);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("онбординга");
    }
}
