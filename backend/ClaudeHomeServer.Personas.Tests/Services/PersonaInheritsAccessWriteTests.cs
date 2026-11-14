using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// A1d: запись флага наследования прав. PersonaManager.Update пишет InheritsAccess в запись
/// из GetRaw под _saveLock только когда флаг передан (частичный PUT его не сбрасывает);
/// Create при непереданном флаге — false (поведение MCP, ai/quick-create, ai/team,
/// pantheon/connect не меняется).
/// </summary>
public class PersonaInheritsAccessWriteTests : IDisposable
{
    private const string Owner = "owner-1";
    private readonly string _tempDir;
    private readonly SpecialtySettingsStore _specialty;
    private readonly PersonaManager _sut;

    public PersonaInheritsAccessWriteTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "pmgr_inhwrite_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var config = TestConfig.Build(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            ["PersonasPath"] = Path.Combine(_tempDir, "personas.json"),
            ["Ollama:Model"] = "",
        });
        _specialty = TestSpecialtyStore.Create(config);
        _sut = new PersonaManager(config, NullLogger<PersonaManager>.Instance, specialty: _specialty);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private void SetTemplate(PersonaAccess access, List<string>? tools, List<string>? disallowed)
    {
        var layer = new SpecialtySettingsLayer();
        layer.Specialties[SpecialtyCatalog.KeyOf(PersonaSpecialty.Reviewer)] = new SpecialtyTemplateSettings
        {
            Access = access, Tools = tools, DisallowedTools = disallowed,
        };
        _specialty.SetGlobal(layer);
    }

    private Persona Cache(string id) => _sut.GetRaw(id, Owner)!;

    private Persona CreatePersona(PersonaSpecialty specialty = PersonaSpecialty.Reviewer, bool? inherits = null) =>
        _sut.Create(Owner, "Тест", null, null, null, null, null, PersonaScope.Global, null, null, null, true,
            specialty: specialty, inheritsAccess: inherits);

    // Позиционные аргументы как в боевом вызове сервиса (name..memoryEnabled — 13), именованными —
    // только проверяемые поля
    private void Update(string id, string? name = null, bool? inheritsAccess = null,
        PersonaAccess? access = null, List<string>? tools = null, List<string>? disallowedTools = null) =>
        _sut.Update(id, Owner, name, null, null, null, null, null, null, null, null, null, null,
            tools, null, access, disallowedTools, inheritsAccess: inheritsAccess);

    [Fact]
    public void Update_БезФлага_ФлагНаследующейПерсоныНеСбрасывается()
    {
        SetTemplate(PersonaAccess.ReadOnly, ["tasks"], null);
        var p = CreatePersona(inherits: true);

        Update(p.Id, name: "Новое имя"); // частичный PUT: флаг не передан

        Cache(p.Id).InheritsAccess.Should().BeTrue("null — не менять: частичная правка флаг не сбрасывает");
        _sut.Get(p.Id, Owner)!.Access.Should().Be(PersonaAccess.ReadOnly, "персона продолжает наследовать");
    }

    [Fact]
    public void Update_ФлагFalse_ПишетсяВЗапись_УвиденноеФиксируется_ПравкаШаблонаНеТронет()
    {
        SetTemplate(PersonaAccess.ReadOnly, ["tasks"], null);
        var p = CreatePersona(inherits: true);
        var seen = _sut.Get(p.Id, Owner)!; // то, что человек видит в форме: эффективные права

        Update(p.Id, inheritsAccess: false, access: seen.Access, tools: seen.Tools,
            disallowedTools: seen.DisallowedTools);

        var raw = Cache(p.Id);
        raw.InheritsAccess.Should().BeFalse("флаг пишется в запись из GetRaw, не в гидратированную копию");
        raw.Access.Should().Be(PersonaAccess.ReadOnly);
        raw.Tools.Should().Equal("tasks");

        SetTemplate(PersonaAccess.Custom, ["notes"], ["Bash"]); // шаблон поменялся после фиксации

        var after = _sut.Get(p.Id, Owner)!;
        after.Access.Should().Be(PersonaAccess.ReadOnly, "override заморожен: подстановки уже нет");
        after.Tools.Should().Equal("tasks");
    }

    [Fact]
    public void Create_БезФлага_False_ЯвныйTrue_True()
    {
        var plain = CreatePersona();
        Cache(plain.Id).InheritsAccess.Should().BeFalse(
            "флаг не передан: поведение MCP, ai/quick-create, ai/team, pantheon/connect не меняется");

        var inheriting = CreatePersona(inherits: true);
        Cache(inheriting.Id).InheritsAccess.Should().BeTrue();
    }

    [Fact]
    public void Update_ПараллельноФлагИИмя_ОбаПрименены()
    {
        var p = CreatePersona(inherits: true);

        // 50 пар: PUT {name} (флаг не передан) и PUT {inheritsAccess:false} на одной персоне
        Parallel.For(0, 50, i =>
        {
            var rename = Task.Run(() => Update(p.Id, name: $"имя{i}"));
            var flag = Task.Run(() => Update(p.Id, inheritsAccess: false));
            Task.WaitAll(rename, flag);
        });

        var raw = Cache(p.Id);
        raw.InheritsAccess.Should().BeFalse("флаг под _saveLock: параллельная правка имени его не теряет");
        raw.Name.Should().StartWith("имя", "имя под тем же локом: правка флага его не теряет");
    }
}

/// <summary>
/// A1d на боевом REST-пути: PUT/POST принимает inheritsAccess; семантика групп прав —
/// итоговый флаг true: права запроса игнорируются, персистентные поля — снимок эффективного
/// шаблона (для отката кода); переход true → false: человек фиксирует ровно то, что видел
/// (непереданные поля — из гидратированной персоны), и правка шаблона их больше не трогает.
/// </summary>
[Trait("Category", "Host")]
public class PersonaInheritsAccessWriteEndpointTests : IClassFixture<PersonaInheritsAccessWriteEndpointTests.Hosts>
{
    // Хост на КЛАСС: одна тестовая конфигурация, правки персон поверх неё (держатель — xunit)
    public sealed class Hosts : TestWebApplicationFactory { }

    private readonly TestWebApplicationFactory _factory;

    public PersonaInheritsAccessWriteEndpointTests(Hosts hosts) => _factory = hosts;

    private SpecialtySettingsStore Specialty => _factory.Services.GetRequiredService<SpecialtySettingsStore>();
    private PersonaManager Personas => _factory.Services.GetRequiredService<PersonaManager>();

    private void SetTemplate(PersonaAccess access, List<string>? tools, List<string>? disallowed)
    {
        var layer = new SpecialtySettingsLayer();
        layer.Specialties[SpecialtyCatalog.KeyOf(PersonaSpecialty.Reviewer)] = new SpecialtyTemplateSettings
        {
            Access = access, Tools = tools, DisallowedTools = disallowed,
        };
        Specialty.SetGlobal(layer);
    }

    private static async Task<string> UserIdAsync(HttpClient client) =>
        (await GetJsonAsync(client, "/api/auth/me")).GetProperty("userId").GetString()!;

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        var resp = await client.GetAsync(path);
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> PostJsonAsync(HttpClient client, string path, object body)
    {
        var resp = await client.PostAsJsonAsync(path, body);
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> PutJsonAsync(HttpClient client, string path, object body)
    {
        var resp = await client.PutAsJsonAsync(path, body);
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
    }

    private static async Task<string> CreatePersonaAsync(HttpClient client, object body) =>
        (await PostJsonAsync(client, "/api/personas", body)).GetProperty("id").GetString()!;

    [Fact]
    public async Task PUT_БезФлага_НаследующаяПерсонаОстаётсяНаследующей()
    {
        var client = _factory.CreateAuthenticatedClient();
        var userId = await UserIdAsync(client);
        SetTemplate(PersonaAccess.ReadOnly, ["tasks"], null);
        var id = await CreatePersonaAsync(client, new { name = "Наследующая", specialty = "reviewer" });

        await PutJsonAsync(client, $"/api/personas/{id}", new { inheritsAccess = true });
        await PutJsonAsync(client, $"/api/personas/{id}", new { name = "Переименованная" }); // без флага

        var got = await GetJsonAsync(client, $"/api/personas/{id}");
        got.GetProperty("inheritsAccess").GetBoolean().Should().BeTrue(
            "частичный PUT флаг не сбрасывает: REST принимает его опционально");
        got.GetProperty("access").GetString().Should().Be("readOnly", "персона продолжает наследовать");
        got.GetProperty("tools")[0].GetString().Should().Be("tasks");
        Personas.GetRaw(id, userId)!.InheritsAccess.Should().BeTrue("в записи тоже флаг");
    }

    [Fact]
    public async Task PUT_ФлагFalse_ФиксируетУвиденное_СменаШаблонаНеТронет()
    {
        var client = _factory.CreateAuthenticatedClient();
        var userId = await UserIdAsync(client);
        SetTemplate(PersonaAccess.ReadOnly, ["tasks"], null);
        var id = await CreatePersonaAsync(client, new { name = "Фиксация", specialty = "reviewer" });
        await PutJsonAsync(client, $"/api/personas/{id}", new { inheritsAccess = true });

        // Шаблон поменялся: человеку форма показывает уже другой набор прав
        SetTemplate(PersonaAccess.Custom, ["notes"], ["Bash"]);
        (await GetJsonAsync(client, $"/api/personas/{id}"))
            .GetProperty("access").GetString().Should().Be("custom", "видимое сейчас — это");

        await PutJsonAsync(client, $"/api/personas/{id}", new { inheritsAccess = false }); // «своё», без полей

        var raw = Personas.GetRaw(id, userId)!;
        raw.InheritsAccess.Should().BeFalse();
        raw.Access.Should().Be(PersonaAccess.Custom,
            "в запись уходит увиденное (гидратированное), а не сырой материализованный снимок");
        raw.Tools.Should().Equal("notes");
        raw.DisallowedTools.Should().Equal("Bash");

        SetTemplate(PersonaAccess.Full, null, null); // шаблон правят снова — фиксированное не трогается

        var after = await GetJsonAsync(client, $"/api/personas/{id}");
        after.GetProperty("access").GetString().Should().Be("custom", "override переживает правку шаблона");
        after.GetProperty("tools")[0].GetString().Should().Be("notes");
    }

    [Fact]
    public async Task PUT_ФлагTrue_ПраваЗапросаИгнорируются_СнимокРавенШаблону()
    {
        var client = _factory.CreateAuthenticatedClient();
        var userId = await UserIdAsync(client);
        SetTemplate(PersonaAccess.ReadOnly, ["tasks"], null);
        var id = await CreatePersonaAsync(client, new { name = "Снимок", specialty = "reviewer" });

        await PutJsonAsync(client, $"/api/personas/{id}",
            new { inheritsAccess = true, access = "custom", disallowedTools = new[] { "Bash" } });

        var raw = Personas.GetRaw(id, userId)!;
        raw.InheritsAccess.Should().BeTrue();
        raw.Access.Should().Be(PersonaAccess.ReadOnly, "права запроса игнорируются: запись — шаблон");
        raw.Tools.Should().Equal("tasks");
        raw.DisallowedTools.Should().BeNull("у шаблона запретов нет");
    }

    [Fact]
    public async Task POST_СФлагомTrue_ПраваИзШаблона_БезФлагаПрежние()
    {
        var client = _factory.CreateAuthenticatedClient();
        SetTemplate(PersonaAccess.ReadOnly, ["tasks"], null);

        var inheriting = await PostJsonAsync(client, "/api/personas",
            new { name = "СозданнаяНаследующей", specialty = "reviewer", inheritsAccess = true });
        inheriting.GetProperty("inheritsAccess").GetBoolean().Should().BeTrue();
        inheriting.GetProperty("access").GetString().Should().Be("readOnly",
            "запись создаётся снимком эффективного шаблона (для отката кода)");
        inheriting.GetProperty("tools")[0].GetString().Should().Be("tasks");

        var plain = await PostJsonAsync(client, "/api/personas", new { name = "СозданнаяОбычно", specialty = "reviewer" });
        plain.GetProperty("inheritsAccess").GetBoolean().Should().BeFalse(
            "флаг не передан: поведение создания через REST не меняется");
        plain.GetProperty("access").GetString().Should().Be("readOnly",
            "шаблон при создании с специальностью применяется как раньше");
    }
}
