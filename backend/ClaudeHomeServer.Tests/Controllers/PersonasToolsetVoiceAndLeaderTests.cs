using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// Агент в обычном проектном чате: меняет голос персоны (валидация как у PUT /voice),
// получает подобранный голос у новой персоны без явного voice и назначает руководителя
// СВОЕГО проекта. Личный дефолт и руководитель чужого проекта из обычного чата — отказ.
public class PersonasToolsetVoiceAndLeaderTests : IDisposable
{
    private readonly VoiceCheap _cheap = new();
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public PersonasToolsetVoiceAndLeaderTests()
    {
        _factory = new TestWebApplicationFactory
        {
            ExtraServices = s => s.AddSingleton<ICheapTextRunner>(_cheap),
        };
        _client = _factory.CreateAuthenticatedClient();
    }

    public void Dispose() => _factory.Dispose();

    // Заглушка one-shot: на подбор голоса — управляемый ответ (или сбой), на прочие
    // действия создания — «ничего не подошло»
    private sealed class VoiceCheap : ICheapTextRunner
    {
        public string VoiceAnswer { get; set; } = "julia strict";
        public bool VoiceFails { get; set; }
        public int VoiceCalls;

        public bool UsesLocal(string actionKey) => false;
        public bool HasFreeRoute(string actionKey) => false;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "stub";
        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            if (actionKey != LocalActionCatalog.PersonaVoice) return Task.FromResult("[]");
            Interlocked.Increment(ref VoiceCalls);
            return VoiceFails
                ? Task.FromException<string>(new InvalidOperationException("модель недоступна"))
                : Task.FromResult(VoiceAnswer);
        }
        public Task<string?> RunFreeAsync(string actionKey, string prompt, object? jsonFormat = null,
            CancellationToken ct = default) => Task.FromResult<string?>("[]");
        public Task<string?> RunLocalOnlyAsync(string actionKey, string prompt, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private string OwnerId => _factory.Services.GetRequiredService<UserStore>()
        .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

    private PersonaManager Personas => _factory.Services.GetRequiredService<PersonaManager>();
    private ProjectManager Projects => _factory.Services.GetRequiredService<ProjectManager>();
    private UserStore Users => _factory.Services.GetRequiredService<UserStore>();

    private async Task<(string ProjectId, string SessionId)> ProjectChatAsync()
    {
        var project = await _client.PostAsJsonAsync("/api/projects", new { name = $"pv-{Guid.NewGuid():N}" });
        project.EnsureSuccessStatusCode();
        var projectId = (await project.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var session = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "acceptEdits" });
        session.EnsureSuccessStatusCode();
        return (projectId, (await session.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!);
    }

    private async Task<JsonElement> CallAsync(string sessionId, string tool, object args)
    {
        var resp = await _client.PostAsJsonAsync($"/mcp/personas/{sessionId}", new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments = args },
        });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result");
    }

    private static bool IsError(JsonElement r) => r.TryGetProperty("isError", out var e) && e.GetBoolean();
    private static string TextOf(JsonElement r) => r.GetProperty("content")[0].GetProperty("text").GetString()!;

    private Persona NewPersona(PersonaScope scope = PersonaScope.Global, string? projectId = null) =>
        Personas.Create(OwnerId, "П" + Guid.NewGuid().ToString("N")[..5], null, null, null, null, null,
            scope, projectId, null, null, false);

    // --- Голос: personas_update ---

    [Fact]
    public async Task Update_ГолосИАмплуа_Записываются()
    {
        var (_, sessionId) = await ProjectChatAsync();
        var target = NewPersona();

        var result = await CallAsync(sessionId, "personas_update",
            new { id = target.Id, voice = "masha", voiceRole = "friendly" });

        IsError(result).Should().BeFalse(TextOf(result));
        var voice = Personas.Get(target.Id, OwnerId)!.Voice;
        voice!.Voice.Should().Be("masha");
        voice.Role.Should().Be("friendly");
    }

    [Fact]
    public async Task Update_ЧужоеАмплуа_ВнятныйОтказ_ПерсонаНеТронута()
    {
        var (_, sessionId) = await ProjectChatAsync();
        var target = NewPersona();

        var result = await CallAsync(sessionId, "personas_update",
            new { id = target.Id, role = "Новая роль", voice = "masha", voiceRole = "whisper" });

        IsError(result).Should().BeTrue();
        TextOf(result).Should().Contain("не умеет «whisper»").And.Contain("friendly");
        var after = Personas.Get(target.Id, OwnerId)!;
        after.Voice.Should().BeNull();
        after.Role.Should().BeNull("отказ по голосу не должен применять соседние поля");
    }

    [Fact]
    public async Task Update_НеизвестныйГолос_ОтказСоСпискомГолосов()
    {
        var (_, sessionId) = await ProjectChatAsync();
        var target = NewPersona();

        var result = await CallAsync(sessionId, "personas_update", new { id = target.Id, voice = "зорро" });

        IsError(result).Should().BeTrue();
        TextOf(result).Should().Contain("Неизвестный голос синтеза").And.Contain("masha");
    }

    [Fact]
    public async Task Update_ПустойГолос_Сбрасывает_АТолькоАмплуа_МеняетАмплуаТекущего()
    {
        var (_, sessionId) = await ProjectChatAsync();
        var target = NewPersona();
        Personas.SetVoice(target.Id, OwnerId, new PersonaVoice { Voice = "masha", Speed = 1.2 });

        var roleOnly = await CallAsync(sessionId, "personas_update", new { id = target.Id, voiceRole = "strict" });
        IsError(roleOnly).Should().BeFalse(TextOf(roleOnly));
        var voice = Personas.Get(target.Id, OwnerId)!.Voice!;
        voice.Voice.Should().Be("masha");
        voice.Role.Should().Be("strict");
        voice.Speed.Should().Be(1.2, "темп — настройка человека, агент его не сбрасывает");

        var reset = await CallAsync(sessionId, "personas_update", new { id = target.Id, voice = "" });
        IsError(reset).Should().BeFalse(TextOf(reset));
        Personas.Get(target.Id, OwnerId)!.Voice.Should().BeNull();
    }

    // --- Голос: personas_create ---

    [Fact]
    public async Task Create_БезГолоса_ПодбираетсяПоХарактеру()
    {
        var (_, sessionId) = await ProjectChatAsync();

        var result = await CallAsync(sessionId, "personas_create",
            new { name = "Голос" + Guid.NewGuid().ToString("N")[..4], character = "Ты — строгий ревьюер." });

        IsError(result).Should().BeFalse(TextOf(result));
        var id = JsonDocument.Parse(TextOf(result)).RootElement.GetProperty("id").GetString()!;
        var voice = Personas.Get(id, OwnerId)!.Voice;
        voice!.Voice.Should().Be("julia");
        voice.Role.Should().Be("strict");
    }

    [Fact]
    public async Task Create_СбойПодбора_ПерсонаСоздана_СОтметкойВОтвете()
    {
        _cheap.VoiceFails = true;
        var (_, sessionId) = await ProjectChatAsync();
        var name = "Сбой" + Guid.NewGuid().ToString("N")[..4];

        var result = await CallAsync(sessionId, "personas_create", new { name });

        IsError(result).Should().BeFalse(TextOf(result));
        TextOf(result).Should().Contain("Голос подобрать не удалось");
        Personas.GetByOwner(OwnerId).Single(p => p.Name == name).Voice.Should().BeNull();
    }

    [Fact]
    public async Task Create_ЯвныйГолос_БезПодбора_ПустойГолос_БезГолоса()
    {
        var (_, sessionId) = await ProjectChatAsync();

        var explicitResult = await CallAsync(sessionId, "personas_create",
            new { name = "Явный" + Guid.NewGuid().ToString("N")[..4], voice = "kirill" });
        var silentResult = await CallAsync(sessionId, "personas_create",
            new { name = "Тихий" + Guid.NewGuid().ToString("N")[..4], voice = "" });

        IsError(explicitResult).Should().BeFalse(TextOf(explicitResult));
        IsError(silentResult).Should().BeFalse(TextOf(silentResult));
        _cheap.VoiceCalls.Should().Be(0, "явный voice (в т.ч. пустой) отключает подбор");
        JsonDocument.Parse(TextOf(explicitResult)).RootElement.GetProperty("voice").GetProperty("voice")
            .GetString().Should().Be("kirill");
        TextOf(silentResult).Should().NotContain("Голос подобрать не удалось");
    }

    [Fact]
    public async Task Create_НеверныйГолос_Отказ_ПерсонаНеСоздана()
    {
        var (_, sessionId) = await ProjectChatAsync();
        var before = Personas.GetByOwner(OwnerId).Count;

        var result = await CallAsync(sessionId, "personas_create",
            new { name = "Кривой" + Guid.NewGuid().ToString("N")[..4], voice = "filipp", voiceRole = "evil" });

        IsError(result).Should().BeTrue();
        TextOf(result).Should().Contain("не поддерживает амплуа");
        Personas.GetByOwner(OwnerId).Count.Should().Be(before);
    }

    [Fact]
    public async Task RestCreate_БезГолоса_ТожеПодбирает()
    {
        var resp = await _client.PostAsJsonAsync("/api/personas",
            new { name = "Форма" + Guid.NewGuid().ToString("N")[..4] });
        resp.EnsureSuccessStatusCode();
        var id = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        Personas.Get(id, OwnerId)!.Voice!.Voice.Should().Be("julia");
    }

    // --- Руководитель проекта из обычного чата ---

    [Fact]
    public async Task SetDefault_ПерсонаКомандыПроектаЧата_СтановитсяРуководителем()
    {
        var (projectId, sessionId) = await ProjectChatAsync();
        var lead = NewPersona(PersonaScope.Project, projectId);

        var result = await CallAsync(sessionId, "personas_set_default", new { personaId = lead.Id });

        IsError(result).Should().BeFalse(TextOf(result));
        Projects.GetById(projectId)!.DefaultPersonaId.Should().Be(lead.Id);
    }

    [Fact]
    public async Task SetDefault_ГлобальнаяИзОбычногоЧата_Отказ_ЛичныйДефолтНеМеняется()
    {
        var (_, sessionId) = await ProjectChatAsync();
        var global = NewPersona();
        var before = Users.GetById(OwnerId)!.DefaultPersonaId;

        var result = await CallAsync(sessionId, "personas_set_default", new { personaId = global.Id });

        IsError(result).Should().BeTrue();
        TextOf(result).Should().Contain("только руководителя проекта");
        Users.GetById(OwnerId)!.DefaultPersonaId.Should().Be(before);
    }

    [Fact]
    public async Task SetDefault_ПерсонаДругогоПроекта_Отказ()
    {
        var (_, sessionId) = await ProjectChatAsync();
        var (otherProjectId, _) = await ProjectChatAsync();
        var stranger = NewPersona(PersonaScope.Project, otherProjectId);

        var result = await CallAsync(sessionId, "personas_set_default", new { personaId = stranger.Id });

        IsError(result).Should().BeTrue();
        TextOf(result).Should().Contain("команды проекта этого чата");
        Projects.GetById(otherProjectId)!.DefaultPersonaId.Should().NotBe(stranger.Id);
    }
}
