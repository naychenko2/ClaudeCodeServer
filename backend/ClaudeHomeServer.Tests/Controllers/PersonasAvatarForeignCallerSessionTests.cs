using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// Ветка «чужая сессия в заголовке» у avatar/generate (хвост ревью 4b78b8fc). По коду она
// fail-closed: X-Caller-Session-Id — заголовок клиентский, PersonasController.CallerSpendAttribution
// (:211) проверяет его через SessionManager.GetOwned (:11783), где резолвится владелец
// (ResolveOwnerId, :4556); у чужой сессии проверка не проходит → GetOwned отдаёт null →
// ветка (Human, null) (PersonasController.cs:215), а актор по AvatarSpendAttribution
// (PersonasCrudService.cs:544) равняется обратившемуся. То есть чужим заголовком нельзя
// назвать Agent и нельзя назвать актором владельца чужой сессии.
//
// Чужая сессия в тесте не пустая: в ней поднят живой агентский ход с актором-участником
// (та же точка подачи, что у PersonasCreateTurnInitiatorTests). Это делает тест не на
// совпадении: если снять проверку владельца в GetOwned (мутация ResolveOwnerId(s) == ownerId),
// атрибуция станет (Agent, MemberId) — краснота падает ровно на ассерты записи, все предусловия
// сценария при этом зелёные. Мутация «жёсткий Human» в агентской ветке CallerSpendAttribution
// поведение этой ветки не меняет (оно и есть Human) — тест на неё не краснеет, и это честно
// отмечено в отчёте задачи.
[Trait("Category", "Host")]
public class PersonasAvatarForeignCallerSessionTests : IClassFixture<AvatarSpendFactory>
{
    private readonly AvatarSpendFactory _factory;
    private readonly HttpClient _owner;
    private readonly HttpClient _member;

    public PersonasAvatarForeignCallerSessionTests(AvatarSpendFactory factory)
    {
        _factory = factory;
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

    // Свой проект участника и его собственный чат: резолвится владелец — MemberId, не обратившийся
    private async Task<string> CreateForeignSessionAsync(string pathName)
    {
        var project = await _member.PostAsJsonAsync("/api/projects", new { name = pathName });
        project.EnsureSuccessStatusCode();
        var projectId = (await project.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var session = await _member.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "acceptEdits" });
        session.EnsureSuccessStatusCode();
        return (await session.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    // Тот же вызов avatar/generate, что у PersonasCreateTurnInitiatorTests: персона своя
    // (без заголовка, без аватара), заголовок-вызыватель — чужая сессия
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

    [Fact]
    public async Task ГенерацияАватараСЧужимЗаголовком_ИнициаторЧеловекаИАкторОбратившийся()
    {
        var foreignId = await CreateForeignSessionAsync($"fc-{Tag()}");
        Sessions.ResolveOwnerId(Sessions.GetById(foreignId)!).Should().Be(MemberId,
            "сессия действительно чужая: её владелец — участник, а не обратившийся");

        // Чужая сессия не пустая: живой агентский ход с актором-участником — ровно то,
        // что атрибуция использовала бы, если бы проверка владельца в GetOwned была снята
        await Sessions.SendMessageAndWaitAsync(foreignId, "чужая заявка", TimeSpan.Zero,
            agentDepth: 1, senderUserId: MemberId);
        Sessions.GetById(foreignId)!.Status.Should().Be(SessionStatus.Working, "ход должен быть живым");
        Sessions.GetTurnActor(foreignId, MemberId).Should().Be(MemberId,
            "актор хода в реестре: иначе тест мерил бы пустоту чужого чата");
        Sessions.IsTurnFromHuman(foreignId, MemberId).Should().BeFalse(
            "ход агентский: человеческий дал бы ожидание, неотличимое от мутированного");

        var before = ImageRecords().Count;
        await GenerateAvatarAsync(foreignId);

        var added = ImageRecords().Skip(before).ToList();
        added.Should().NotBeEmpty("генерация прошла — запись траты есть");
        added.Should().OnlyContain(r => r.Initiator == SpendInitiators.Human,
            "чужой заголовок не может назвать Agent: GetOwned не проходит → ветка (Human, null)");
        added.Should().OnlyContain(r => r.ActorUserId == OwnerId,
            "актор — обратившийся: владелец чужой сессии не может стать актором траты");
    }
}
