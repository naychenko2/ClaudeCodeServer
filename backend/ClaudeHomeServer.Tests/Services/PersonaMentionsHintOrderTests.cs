using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Sessions;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services;

// S1 (prompt-prefix-stable): строки подсказки @упоминаний идут по возрастанию handle (Ordinal)
// внутри каждой группы и не зависят ни от порядка входного списка, ни от UpdatedAt: префикс
// системного промпта должен быть одинаковым у новых бесед (иначе локальная модель не берёт
// его из кэша префикса).
[Trait("Category", "Host")]
public class PersonaMentionsHintOrderTests : IClassFixture<PersonaMentionsHintOrderTests.Hosts>
{
    // Хост на КЛАСС (та же форма, что у SphereChatZoneTests). Проекты каждый тест заводит свои;
    // handle уникален на весь владелец (глобальная зона), поэтому у каждого теста свой суффикс.
    public sealed class Hosts : TestWebApplicationFactory;

    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public PersonaMentionsHintOrderTests(Hosts hosts)
    {
        _factory = hosts;
        _client = _factory.CreateAuthenticatedClient();
    }

    private T Svc<T>() where T : notnull => _factory.Services.GetRequiredService<T>();

    private string OwnerId => Svc<UserStore>().FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

    private static async Task<string> IdOf(HttpResponseMessage resp)
    {
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private async Task<string> CreateProjectAsync() =>
        await IdOf(await _client.PostAsJsonAsync("/api/projects", new { name = $"hint-{Guid.NewGuid():N}" }));

    // Три глобальные (не сферные) персоны с явными handle: alpha < mike < zeta по Ordinal.
    private (Persona Zeta, Persona Alpha, Persona Mike) CreateTrio(string suffix)
    {
        var zeta = Svc<PersonaManager>().Create(OwnerId, "Зета", null, null, null, null, null,
            PersonaScope.Global, null, null, null, false, handle: $"zeta-{suffix}");
        var alpha = Svc<PersonaManager>().Create(OwnerId, "Альфа", null, null, null, null, null,
            PersonaScope.Global, null, null, null, false, handle: $"alpha-{suffix}");
        var mike = Svc<PersonaManager>().Create(OwnerId, "Майк", null, null, null, null, null,
            PersonaScope.Global, null, null, null, false, handle: $"mike-{suffix}");
        return (zeta, alpha, mike);
    }

    // Тест A: порядок входного списка не влияет на текст — оба порядка дают одну строку
    [Fact]
    public async Task ПорядокВхода_ТекстОдинаковый()
    {
        var projectId = await CreateProjectAsync();
        var (zeta, alpha, mike) = CreateTrio("a");

        var first = Svc<SessionManager>().BuildMentionsHint(OwnerId, new Session { ProjectId = projectId }, [zeta, alpha, mike]);
        var second = Svc<SessionManager>().BuildMentionsHint(OwnerId, new Session { ProjectId = projectId }, [mike, zeta, alpha]);

        first.Should().NotBeNull();
        Assert.Equal(first, second);
    }

    // Тест B: касание UpdatedAt одной персоны не меняет текст (список передан в порядке
    // убывания нового UpdatedAt — как его вернул бы GetForContext)
    [Fact]
    public async Task СменаUpdatedAt_ТекстНеИзменился()
    {
        var projectId = await CreateProjectAsync();
        var (zeta, alpha, mike) = CreateTrio("b");

        var before = Svc<SessionManager>().BuildMentionsHint(OwnerId, new Session { ProjectId = projectId }, [zeta, alpha, mike]);

        alpha.UpdatedAt = DateTime.UtcNow.AddDays(1);
        var after = Svc<SessionManager>().BuildMentionsHint(OwnerId, new Session { ProjectId = projectId }, [alpha, zeta, mike]);

        before.Should().NotBeNull();
        Assert.Equal(before, after);
    }

    // Тест C: строки идут по возрастанию handle (Ordinal), независимо от порядка входа
    [Fact]
    public async Task СтрокиПоВозрастаниюHandle()
    {
        var projectId = await CreateProjectAsync();
        var (zeta, alpha, mike) = CreateTrio("c");

        var hint = Svc<SessionManager>().BuildMentionsHint(OwnerId, new Session { ProjectId = projectId }, [zeta, alpha, mike]);

        hint.Should().NotBeNull();
        var at = hint!.IndexOf("@alpha-c", StringComparison.Ordinal);
        var am = hint.IndexOf("@mike-c", StringComparison.Ordinal);
        var az = hint.IndexOf("@zeta-c", StringComparison.Ordinal);
        at.Should().BeGreaterThan(0);
        am.Should().BeGreaterThan(at);
        az.Should().BeGreaterThan(am);
    }

    // Тест D: handles сабагентов (AgentHandles плана --agents / «Доступные agentType») — тоже по handle
    [Fact]
    public async Task HandlesСабагентов_Отсортированы()
    {
        var projectId = await CreateProjectAsync();
        var (zeta, alpha, mike) = CreateTrio("d");

        // Participants — чтобы пройти ConsultantsEnabled по групповому чату; самого спикера нет
        var session = new Session { ProjectId = projectId, Participants = [alpha.Id, mike.Id] };
        var provider = Svc<PersonaLayerBuilder>().BuildPersonaAgentsProvider(OwnerId, session, null);
        provider.Should().NotBeNull("в полном хосте PersonaAgentFileSync зарегистрирован — план строится");

        var ctx = provider!();
        ctx.Should().NotBeNull();
        var handles = ctx!.AgentHandles;
        handles.Should().Contain(new[] { "alpha-d", "mike-d", "zeta-d" });
        Assert.True(handles.SequenceEqual(handles.OrderBy(h => h, StringComparer.Ordinal)),
            "AgentHandles должны идти по возрастанию handle");
    }
}
