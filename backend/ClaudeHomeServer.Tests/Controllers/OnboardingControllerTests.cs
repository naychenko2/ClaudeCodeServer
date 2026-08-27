using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// Онбординги: старт/резюм сессий, идемпотентность
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
        // Предохранитель personas_create держит, пока жива заготовка-ассистент (её заводит
        // стартовый проход провижна). Здесь предмет теста — досев профиля персоне, созданной
        // мастером, поэтому снимаем статус заготовки: та же деградация, что и «заготовку удалили».
        var users = _factory.Services.GetRequiredService<UserStore>();
        users.SetAssistantPersona(users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id, null);

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

    // Сброс онбординга дефолтного пользователя: удаляем живую сессию знакомства, если осталась
    // от соседних тестов класса (фабрика общая), — иначе start становится резюмом чужой
    // сессии, и счётчик ходов фейк-адаптера зависит от порядка тестов
    private async Task ResetOnboardingAsync()
    {
        var me = JsonSerializer.Deserialize<JsonElement>(
            await (await _client.GetAsync("/api/auth/me")).Content.ReadAsStringAsync());
        if (me.TryGetProperty("onboardingSessionId", out var sid) && sid.ValueKind == JsonValueKind.String)
            (await _client.DeleteAsync($"/api/chats/{sid.GetString()}")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task StartUser_ЗапускаетПервыйХодМастера()
    {
        await EnsureHomeConfiguredAsync();
        await ResetOnboardingAsync();

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
        await ResetOnboardingAsync();

        var first = await PostJsonAsync("/api/onboarding/user/start");
        var sessionId = first.GetProperty("id").GetString()!;

        // Kickoff «в полёте»: фейк-адаптер принял ход, но завершения не эмитит — статус
        // сессии Working. Второй start (вторая вкладка, повторный логин) возвращает
        // существующую сессию, а повторный kickoff отсекает проверка немоты по статусу —
        // иначе первая реплика мастера задублировалась бы и сбила интервью
        var second = await PostJsonAsync("/api/onboarding/user/start");
        second.GetProperty("id").GetString().Should().Be(sessionId);

        var adapter = _factory.LlmAdapters.Adapters[sessionId];
        adapter.SentMessages.Should().ContainSingle("kickoff в полёте — второй не уходит");
    }

    [Fact]
    public async Task StartUser_НемойЧат_ПовторныйStartЛечитKickoff()
    {
        await EnsureHomeConfiguredAsync();
        await ResetOnboardingAsync();

        var onboarding = await PostJsonAsync("/api/onboarding/user/start");
        var sessionId = onboarding.GetProperty("id").GetString()!;
        var adapter = _factory.LlmAdapters.Adapters[sessionId];
        adapter.SentMessages.Should().ContainSingle("первый kickoff ушёл при создании");

        // Имитация сбоя первого хода: директива в истории есть, а ответа собеседника нет.
        // Фейк-адаптер не завершает ход (статус Working навсегда) и живого прогона у него
        // нет (HasLiveTurn=false) — «Стоп» реанимирует такой чат: статус Active плюс
        // служебная плашка в истории. Получаем в точности «немой чат»: человек видит пустую
        // ленту, а OnboardingSessionId уже записан.
        (await _client.PostAsync($"/api/board/agents/{sessionId}/interrupt", null)).EnsureSuccessStatusCode();
        await WaitChatStatusAsync(sessionId, "active");

        // Повторный start (человек вернулся в пустой чат) обязан заново запустить собеседника:
        // реплик нет, неслужебных сообщений нет (плашка stuck_reset и kickoff-директива —
        // служебные), статус не Working/Waiting — все три условия немоты
        var resumed = await PostJsonAsync("/api/onboarding/user/start");
        resumed.GetProperty("id").GetString().Should().Be(sessionId);
        adapter.SentMessages.Should().HaveCount(2, "немой чат лечится повторным kickoff");
        adapter.SentMessages[1].Should().Be(adapter.SentMessages[0],
            "лечение шлёт ту же затравку личного знакомства");
    }

    // Реанимация зависшего чата асинхронна (ReviveStuckSessionAsync в фоне) — ждём смену
    // статуса опросом с дедлайном, а не фиксированной задержкой
    private async Task WaitChatStatusAsync(string sessionId, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var chat = JsonSerializer.Deserialize<JsonElement>(
                await (await _client.GetAsync($"/api/chats/{sessionId}")).Content.ReadAsStringAsync());
            if (chat.GetProperty("status").GetString() == expected) return;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Сессия {sessionId} не перешла в статус {expected}");
    }

    [Fact]
    public async Task StartProject_БезЛичногоДефолта_400()
    {
        await EnsureHomeConfiguredAsync();
        // Второй пользователь — изоляция от других тестов класса. Дефолт ему завёл стартовый
        // проход провижна, поэтому снимаем его явно: состояние «дефолта нет» штатно
        // (заготовку удалили), но через HTTP не воспроизводится.
        var second = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var users = _factory.Services.GetRequiredService<UserStore>();
        var secondId = users.FindByUsername(TestWebApplicationFactory.SecondUsername)!.Id;
        users.SetDefaultPersona(secondId, null);
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

        // Затравка выбрана по типу знакомства: проектная знакомит с проектом и не спрашивает
        // о пользователе — он уже знаком с ассистентом (Знакомство v2, п.0)
        adapter.SentMessages[0].Should().Contain("знакомство с проектом");
        adapter.SentMessages[0].Should().NotContain("как обращаться к пользователю",
            "вопросы о пользователе — из личной затравки, в проектной их быть не должно");
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
    public async Task Финализация_УжеПоднятаяДоСoordinatorFull_ДосеваетТолькоПривязки()
    {
        await EnsureHomeConfiguredAsync();

        var onboarding = await PostJsonAsync("/api/onboarding/user/start");
        var sessionId = onboarding.GetProperty("id").GetString()!;
        // Персона создана в онбординге (OnboardingCreatedPersonaId проставлен) — досев разрешён.
        var personaId = await CreateGlobalPersonaFromSessionAsync("Поднятая мастером", sessionId);

        // Пользователь сам поднял профиль до Coordinator+Full ЕЩЁ ВНУТРИ онбординга, до make-default:
        // имитация краевого случая — ужеSeeded=true, казалось бы «досевать нечего».
        var update = await _client.PutAsJsonAsync($"/api/personas/{personaId}",
            new { specialty = "coordinator", access = "full" });
        update.EnsureSuccessStatusCode();

        (await MakeDefaultFromSessionAsync(personaId, sessionId)).StatusCode.Should().Be(HttpStatusCode.OK);

        var persona = JsonSerializer.Deserialize<JsonElement>(
            await (await _client.GetAsync($"/api/personas/{personaId}")).Content.ReadAsStringAsync());
        // Права не перетёрты — как были Coordinator+Full, так и остались (повторный досев идемпотентен)
        persona.GetProperty("specialty").GetString().Should().Be("coordinator");
        persona.GetProperty("access").GetString().Should().Be("full");
        // ...но Tool-привязки добавились ВОПРЕКИ ужеSeeded — без них персона выглядела бы
        // настроенной, но без инструментов. Это и есть баг Major 1, который закрываем.
        var targets = persona.GetProperty("bindings").EnumerateArray()
            .Select(b => b.GetProperty("target").GetString()).ToList();
        targets.Should().Contain(["personas-manage", "tasks", "notes"]);
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

    // --- Знакомство v2, п.5: сценарий проектного знакомства и шаг команды ---

    private async Task<JsonElement> GetAsync(string url) =>
        JsonSerializer.Deserialize<JsonElement>(
            await (await _client.GetAsync(url)).Content.ReadAsStringAsync());

    // Создать проектную персону (scope=project) из онбординг-сессии — путь MCP personas_create
    private async Task<string> CreateProjectPersonaFromSessionAsync(string name, string projectId, string sessionId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/personas");
        request.Headers.Add("X-Caller-Session-Id", sessionId);
        request.Content = JsonContent.Create(new { name, scope = "project", projectId });
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(
            await response.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
    }

    // Назначение руководителя в первом же ходе НЕ гасит остаток сценария: пока каркас
    // не применён (presetKey == "pending"), точка входа возвращает ту же сессию, а не
    // заводит вторую с новым kickoff
    [Fact]
    public async Task StartProject_ПослеНазначенияРуководителя_ПриPendingКаркасеВозвращаетТуЖеСессию()
    {
        await EnsureHomeConfiguredAsync();
        var personaId = await CreateGlobalPersonaAsync("Проводница");
        (await _client.PostAsync($"/api/personas/{personaId}/make-default", null)).EnsureSuccessStatusCode();
        var projectId = await CreateProjectAsync();

        var onboarding = await PostJsonAsync($"/api/onboarding/project/{projectId}/start");
        var sessionId = onboarding.GetProperty("id").GetString()!;

        // Руководитель выбран в первом же ходе (make-default из онбординг-сессии)
        var leaderId = await CreateProjectPersonaFromSessionAsync("Руководитель", projectId, sessionId);
        (await MakeDefaultFromSessionAsync(leaderId, sessionId)).EnsureSuccessStatusCode();

        // Каркас ещё pending → повторный start — резюм той же сессии, а не вторая с kickoff
        var resumed = await PostJsonAsync($"/api/onboarding/project/{projectId}/start");
        resumed.GetProperty("id").GetString().Should().Be(sessionId,
            "пока PresetKey == pending, OnboardingSessionId не чистится — вторая сессия не заводится");
        _factory.LlmAdapters.Adapters[sessionId].SentMessages.Should().ContainSingle(
            "повторный start живой сессии не шлёт второй kickoff");
    }

    // После решения по каркасу (отказ) повторный start всё равно не заводит вторую сессию —
    // OnboardingSessionId, не очищенный при pending-финализации, резюмит ту же. Инвариант
    // п.5 — «точка входа не создаёт вторую сессию с новым kickoff», а не «поле обязано
    // опустеть»: живой чат знакомства остаётся точкой входа навсегда.
    [Fact]
    public async Task StartProject_ПослеОтказаОтКаркаса_НеЗаводитВторуюСессию()
    {
        await EnsureHomeConfiguredAsync();
        var personaId = await CreateGlobalPersonaAsync("Проводница");
        (await _client.PostAsync($"/api/personas/{personaId}/make-default", null)).EnsureSuccessStatusCode();
        var projectId = await CreateProjectAsync();

        var onboarding = await PostJsonAsync($"/api/onboarding/project/{projectId}/start");
        var sessionId = onboarding.GetProperty("id").GetString()!;
        var leaderId = await CreateProjectPersonaFromSessionAsync("Руководитель", projectId, sessionId);
        (await MakeDefaultFromSessionAsync(leaderId, sessionId)).EnsureSuccessStatusCode();

        // Отказ от каркаса (кнопка «Не нужно» — POST /preset с none)
        (await _client.PostAsJsonAsync($"/api/projects/{projectId}/preset", new { presetKey = "none" }))
            .EnsureSuccessStatusCode();

        var resumed = await PostJsonAsync($"/api/onboarding/project/{projectId}/start");
        resumed.GetProperty("id").GetString().Should().Be(sessionId,
            "точка входа возвращает живую сессию знакомства, а не создаёт новую с kickoff");
        _factory.LlmAdapters.Adapters[sessionId].SentMessages.Should().ContainSingle(
            "второго kickoff после решения по каркасу не уходит");
    }

    // Повторный make-default из живой сессии не шлёт второе onboarding_completed
    [Fact]
    public async Task MakeDefault_ПовторныйИзОнбордингСессии_НеШлётВтороеСобытие()
    {
        await EnsureHomeConfiguredAsync();
        var personaId = await CreateGlobalPersonaAsync("Проводница");
        (await _client.PostAsync($"/api/personas/{personaId}/make-default", null)).EnsureSuccessStatusCode();
        var projectId = await CreateProjectAsync();

        var onboarding = await PostJsonAsync($"/api/onboarding/project/{projectId}/start");
        var sessionId = onboarding.GetProperty("id").GetString()!;
        var leaderId = await CreateProjectPersonaFromSessionAsync("Руководитель", projectId, sessionId);

        (await MakeDefaultFromSessionAsync(leaderId, sessionId)).EnsureSuccessStatusCode();
        // Повторное назначение той же персоны из той же живой сессии — не вторая финализация
        (await MakeDefaultFromSessionAsync(leaderId, sessionId)).EnsureSuccessStatusCode();

        var chat = await GetAsync($"/api/chats/{sessionId}");
        chat.GetProperty("id").GetString().Should().Be(sessionId);
        // Флаг финализации персистится: сессия помнит, что onboarding уже завершён
        // (проверяем отсутствием побочных эффектов — вторая доза досева и событий не ушла)
        var leader = await GetAsync($"/api/personas/{leaderId}");
        leader.GetProperty("specialty").GetString().Should().Be("coordinator",
            "досев прошёл ровно один раз");
    }
}
