using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// Дефект stdio-пути (ревью 3c6055aa): агентский personas_create через stdio-MCP приходил в
// REST /api/personas и записывался инициатором Human, актором — владельцем токена по
// совпадению. Исправление: у обоих путей один признак — живой ход чата-вызывателя. Его
// метит защёлка SessionEntry.TurnFromHuman (тот же признак, по которому ядро метит учёт
// хода): ход начал человек → Human, ход начал агент → Agent; актор — реестр актора
// (GetTurnActor), а не владелец токена. Канал доставки (REST/MCP) в расчёте не участвует.
//
// Ход поднимается боевыми точками подачи ядра: серверная auto-подача (форма исполнителя),
// подача агента (та же SendMessageAndWaitAsync, что зовёт chats_send), человеческая подача
// (тот же REST /api/sessions/{id}/messages). Стаб адаптера ход не завершает — он живой, как
// в бою во время работы агента, когда тот и зовёт инструменты. Измеряется выбор значений в
// PersonasController.CallerSpendAttribution; защёлка и реестр актора — настоящие, их ставит
// живой SessionManager этого хоста.
[Trait("Category", "Host")]
public class PersonasCreateTurnInitiatorTests : IClassFixture<SharedChatAvatarSpendFactory>
{
    private readonly SharedChatAvatarSpendFactory _factory;
    private readonly HttpClient _owner;
    private readonly HttpClient _member;

    public PersonasCreateTurnInitiatorTests(SharedChatAvatarSpendFactory factory)
    {
        _factory = factory;
        // Серверный рубильник общих проектов (без него общие проекты отвечают 503) — тот же
        // состав, что у AvatarSpendTurnActorTests: он нужен человеческой подаче участника в
        // чат общего проекта, предметом теста не является
        _factory.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
        _owner = factory.CreateAuthenticatedClient();
        _member = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
    }

    private UserStore Users => _factory.Services.GetRequiredService<UserStore>();
    private string OwnerId => Users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
    private string MemberId => Users.FindByUsername(TestWebApplicationFactory.SecondUsername)!.Id;
    private SessionManager Sessions => _factory.Services.GetRequiredService<SessionManager>();

    private List<SpendRecord> ImageRecords() =>
        _factory.Spend.Records.Where(r => r.Generations > 0).ToList();

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    // POST /api/personas с autoAvatar: трата аватара — измеряемый объект атрибуции.
    // callerSessionId — заголовок X-Caller-Session-Id: его ставят MCP-серверы (stdio-сервер
    // персон шлёт на каждый вызов), браузер — никогда
    private async Task CreatePersonaAsync(string name, string? callerSessionId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/personas")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { name, autoAvatar = true }),
                System.Text.Encoding.UTF8, "application/json"),
        };
        if (callerSessionId is not null) req.Headers.Add("X-Caller-Session-Id", callerSessionId);
        var resp = await _owner.SendAsync(req);
        resp.EnsureSuccessStatusCode();
    }

    // POST /api/personas/{id}/avatar/generate: тот же REST-вход, что у stdio-инструмента
    // personas_generate_avatar; заголовок-вызыватель ставит MCP-сервер, браузер — никогда
    private async Task GenerateAvatarAsync(string? callerSessionId)
    {
        var created = await _owner.PostAsJsonAsync("/api/personas", new { name = "Аватар" + Tag(), autoAvatar = false });
        created.EnsureSuccessStatusCode();
        var personaId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/personas/{personaId}/avatar/generate")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { prompt = "рыжий кот", count = 1 }),
                System.Text.Encoding.UTF8, "application/json"),
        };
        if (callerSessionId is not null) req.Headers.Add("X-Caller-Session-Id", callerSessionId);
        (await _owner.SendAsync(req)).EnsureSuccessStatusCode();
    }

    private async Task<string> CreateSessionAsync(HttpClient client, string pathName)
    {
        var project = await client.PostAsJsonAsync("/api/projects", new { name = pathName });
        project.EnsureSuccessStatusCode();
        var projectId = (await project.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var session = await client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "acceptEdits" });
        session.EnsureSuccessStatusCode();
        return (await session.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    // Чат общего проекта с участником-редактором — для человеческой подачи участника
    private async Task<string> SharedSessionAsync(string pathName)
    {
        foreach (var c in new[] { _owner, _member })
            (await c.PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = true }))
                .EnsureSuccessStatusCode();
        var project = await _owner.PostAsJsonAsync("/api/projects", new { name = pathName });
        project.EnsureSuccessStatusCode();
        var projectId = (await project.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        // Чат до участников: пока проект личный, провижна владельца нет; в общем проекта без
        // руководителя она не запускается никому (W4-B1a доработка 2, финал ed64271c)
        var session = await _owner.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "acceptEdits" });
        session.EnsureSuccessStatusCode();
        var sessionId = (await session.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        (await _owner.PostAsJsonAsync($"/api/projects/{projectId}/members",
            new { username = TestWebApplicationFactory.SecondUsername, role = "editor" })).EnsureSuccessStatusCode();
        return sessionId;
    }

    // Живой ход подтверждён состоянием чата, а не фактом вызова подачи: иначе «страховка»
    // мерил бы пустоту (защёлка не взведена и когда хода нет)
    private void AssertLiveTurn(string sessionId) =>
        Sessions.GetById(sessionId)!.Status.Should().Be(SessionStatus.Working, "ход должен быть живым");

    // Половина пары «из UI»: вызов без чата-вызывателя — человек; актор — сам обратившийся
    [Fact]
    public async Task СозданиеИзUI_ИнициаторЧеловека()
    {
        var before = ImageRecords().Count;
        await CreatePersonaAsync("ИзUI" + Tag(), null);

        var added = ImageRecords().Skip(before).ToList();
        added.Should().ContainSingle("autoAvatar=true — аватар генерирован");
        added[0].OwnerId.Should().Be(OwnerId);
        added[0].Initiator.Should().Be(SpendInitiators.Human, "вызова без чата-вызывателя нет — это форма");
        added[0].ActorUserId.Should().Be(OwnerId, "актор вызова из UI — сам обратившийся");
    }

    // Половина пары «из агентского хода»: тот же вызов с заголовком-вызывателем, но ход
    // поднял сервер (auto-подача — форма исполнителя и автономного хода): защёлка не
    // взведена → Agent. Актор хода неизвестен → права арендатора (тот же фолбэк, что у
    // HTTP-тулсета). Мутация «жёсткий Human» в CallerSpendAttribution краснит этот тест
    // и «по поручению участника»
    [Fact]
    public async Task СозданиеИзАвтономногоХода_ИнициаторАгента()
    {
        var sessionId = await CreateSessionAsync(_owner, $"ct-auto-{Tag()}");
        await Sessions.SendMessageAsync(sessionId, "автономный ход", [], auto: true);
        AssertLiveTurn(sessionId);
        Sessions.IsTurnFromHuman(sessionId, OwnerId).Should().BeFalse(
            "подача auto — не человеческая: иначе тест мерил бы совпадение");

        var before = ImageRecords().Count;
        await CreatePersonaAsync("ИзАвтономного" + Tag(), sessionId);

        var added = ImageRecords().Skip(before).ToList();
        added.Should().ContainSingle();
        added[0].Initiator.Should().Be(SpendInitiators.Agent,
            "хода человека нет: защёлка TurnFromHuman не взведена (SessionManager.IsTurnFromHuman)");
        added[0].ActorUserId.Should().Be(OwnerId, "актора хода нет (серверная подача) — права арендатора");
    }

    // Точная форма репро дефекта: агентский ход по поручению участника — та же точка подачи,
    // что у chats_send (SendMessageAndWaitAsync наследует актора вызывателя), а вызов
    // инструмента идёт токеном арендатора. Ожидание из репро: Initiator=Agent,
    // ActorUserId — актор хода, а не «владелец токена по совпадению»
    [Fact]
    public async Task АгентскийХодПоПоручениюУчастника_ИнициаторАгентаИАкторУчастника()
    {
        var sessionId = await SharedSessionAsync($"ct-ag-{Tag()}");
        await Sessions.SendMessageAndWaitAsync(sessionId, "поручение от участника", TimeSpan.Zero,
            agentDepth: 1, senderUserId: MemberId);
        AssertLiveTurn(sessionId);
        Sessions.GetTurnActor(sessionId, OwnerId).Should().Be(MemberId);
        Sessions.IsTurnFromHuman(sessionId, OwnerId).Should().BeFalse(
            "chats_send — всегда агентский ход, даже с id человека");

        var before = ImageRecords().Count;
        await CreatePersonaAsync("ПоПоручению" + Tag(), sessionId);

        var added = ImageRecords().Skip(before).ToList();
        added.Should().ContainSingle();
        added[0].OwnerId.Should().Be(OwnerId, "ключ записи — арендатор, не исполнивший");
        added[0].Initiator.Should().Be(SpendInitiators.Agent);
        added[0].ActorUserId.Should().Be(MemberId, "актор — подавший (реестр актора), а не владелец токена");
    }

    // П.2 (актор — подавший) в человеческом ходе: ход поднял участник общего проекта той же
    // точкой подачи, что зовёт его сообщение из UI (SessionHub.cs:306 → SendMessageAsync с
    // senderUserId; POST /api/sessions/{id}/messages — не она: это REST-канал агентов,
    // SessionMessagesController.cs:142). Вызов идёт токеном арендатора — та же форма
    // stdio-песочницы: токен владельца, ход участника. Инициатор — человек: запрос его, а не
    // канала доставки; актор — участник, а не владелец токена
    [Fact]
    public async Task СозданиеИзХодаУчастника_ИнициаторЧеловекаИАкторПодавшего()
    {
        var sessionId = await SharedSessionAsync($"ct-hm-{Tag()}");
        await Sessions.SendMessageAsync(sessionId, "создай персону", [], senderUserId: MemberId);
        AssertLiveTurn(sessionId);
        Sessions.GetTurnActor(sessionId, OwnerId).Should().Be(MemberId);
        Sessions.IsTurnFromHuman(sessionId, OwnerId).Should().BeTrue();

        var before = ImageRecords().Count;
        await CreatePersonaAsync("ИзХодаУчастника" + Tag(), sessionId);
        // Инвариант «признак хода переживает чтение стороннего слоя»: читатель не снимает
        // защёлку, её гасит только result ядра. Если IsTurnFromHuman станет «снял и посмотрел»,
        // ядро на result прочтёт 0 и запись хода получит инициатора agent вместо human
        Sessions.IsTurnFromHuman(sessionId, OwnerId).Should().BeTrue(
            "чтение признака вызовом контроллера не должно снимать защёлку хода");

        var added = ImageRecords().Skip(before).ToList();
        added.Should().ContainSingle();
        added[0].OwnerId.Should().Be(OwnerId, "ключ записи — арендатор, владелец чата");
        added[0].Initiator.Should().Be(SpendInitiators.Human,
            "ход начал человек (участник) — его запрос, а не канал доставки");
        added[0].ActorUserId.Should().Be(MemberId, "актор — подавший, а не владелец токена");
    }

    // Аватар, половина «из UI»: вызов без чата-вызывателя — человек, актор — обратившийся
    [Fact]
    public async Task ГенерацияАватараИзUI_ИнициаторЧеловека()
    {
        var before = ImageRecords().Count;
        await GenerateAvatarAsync(null);

        var added = ImageRecords().Skip(before).ToList();
        added.Should().NotBeEmpty();
        added.Should().OnlyContain(r => r.Initiator == SpendInitiators.Human && r.ActorUserId == OwnerId);
    }

    // Аватар, половина «агентский ход по поручению участника»: stdio-путь personas_generate_avatar.
    // Мутация «жёсткий Human» в GenerateAvatar краснит этот тест
    [Fact]
    public async Task ГенерацияАватараИзАгентскогоХода_ИнициаторАгентаИАкторУчастника()
    {
        var sessionId = await SharedSessionAsync($"ct-av-{Tag()}");
        await Sessions.SendMessageAndWaitAsync(sessionId, "поручение от участника", TimeSpan.Zero,
            agentDepth: 1, senderUserId: MemberId);
        AssertLiveTurn(sessionId);

        var before = ImageRecords().Count;
        await GenerateAvatarAsync(sessionId);

        var added = ImageRecords().Skip(before).ToList();
        added.Should().NotBeEmpty();
        added.Should().OnlyContain(r => r.Initiator == SpendInitiators.Agent && r.ActorUserId == MemberId);
    }
}
