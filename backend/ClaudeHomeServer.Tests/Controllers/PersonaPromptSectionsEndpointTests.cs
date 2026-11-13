using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// P1d (разрез 2, §2.3 и §2.6, инварианты Г4/Г6): REST и MCP принимают слой секций промпта
/// персоны и отдают его эффективное состояние.
///
/// Запись — только через PersonaManager (нормализация P1b): неизвестный id и текст сверх
/// SectionTextLimit — 400 с текстом менеджера, а не 500, и персона нетронута; и тот же отказ
/// приходит через personas_update. Смена специальности сбрасывает свои секции, и ответ —
/// REST-тело и personas_update — называет это строкой менеджера. Чтение — только
/// GET …/prompt-sections (через PromptSectionsContributor, единственная точка наложения):
/// свой текст метится источником persona, соседние секции остаются базовыми; у персоны без
/// специальности — пустой массив. Доступ ручки — как у карточки персоны.
/// </summary>
[Trait("Category", "Host")]
public class PersonaPromptSectionsEndpointTests : IClassFixture<PersonaPromptSectionsEndpointTests.Hosts>
{
    // Хост на КЛАСС: общее состояние — персоны тестового пользователя; своего слоя админа
    // тесты не заводят, базовые секции берутся из дефолтов кода каталога
    public sealed class Hosts : TestWebApplicationFactory;

    private const string ResetText = "свои секции промпта сброшены: сменилась специальность";

    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public PersonaPromptSectionsEndpointTests(Hosts hosts)
    {
        _factory = hosts;
        _client = _factory.CreateAuthenticatedClient();
    }

    private PersonaManager Personas => _factory.Services.GetRequiredService<PersonaManager>();
    private string OwnerId => _factory.Services.GetRequiredService<UserStore>()
        .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

    // analyst: roleRules включён дефолтом кода и имеет текст — «свой» слой на нём виден
    // однозначно, а history/processes/codeGraph остаются базовыми (свой текст сравниваем
    // по нему, а не по смыслу)
    private Persona NewPersona(string specialty = "analyst", List<PromptSectionOverride>? sections = null) =>
        Personas.Create(OwnerId, "ps-" + Guid.NewGuid().ToString("N")[..6], null, null, null, null, null,
            PersonaScope.Global, null, null, null, false,
            specialty: Enum.Parse<PersonaSpecialty>(specialty, ignoreCase: true), promptSections: sections);

    private async Task<string> CreateByRestAsync(object body)
    {
        var resp = await _client.PostAsJsonAsync("/api/personas", body);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private async Task<(HttpStatusCode Code, JsonElement Body)> PutAsync(string id, object body)
    {
        var resp = await _client.PutAsJsonAsync($"/api/personas/{id}", body);
        return (resp.StatusCode, await resp.Content.ReadFromJsonAsync<JsonElement>());
    }

    private async Task<JsonElement> SectionsAsync(string id)
    {
        var resp = await _client.GetAsync($"/api/personas/{id}/prompt-sections");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static List<JsonElement> SectionsOf(JsonElement effective) => effective.EnumerateArray().ToList();

    private static JsonElement SectionOf(JsonElement effective, string id) =>
        SectionsOf(effective).Single(s => s.GetProperty("id").GetString() == id);

    // --- Запись через REST (Г4: POST/PUT → PersonaManager) ---

    [Fact]
    public async Task Put_СвоегоТекстаRoleRules_Пишется_GETОтдаётЕгоИсточникомPersona()
    {
        var id = NewPersona().Id;
        var own = "свой текст роли " + Guid.NewGuid().ToString("N")[..6];

        var (code, _) = await PutAsync(id, new { promptSections = new object[] { new { id = "roleRules", text = own } } });
        code.Should().Be(HttpStatusCode.OK);

        // Та же карточка, что видел GET {id}: слой дошёл до записи
        Personas.GetRaw(id, OwnerId)!.PromptSections!.Single().Text.Should().Be(own);

        var effective = await SectionsAsync(id);
        var roleRules = SectionOf(effective, "roleRules");
        roleRules.GetProperty("text").GetString().Should().Be(own);
        roleRules.GetProperty("textSource").GetString().Should().Be("persona",
            "свой текст метится слоем персоны (Г6) — иначе фронт не отличит «своё» от базового");
        roleRules.GetProperty("enabledSource").GetString().Should().NotBe("persona",
            "enabled не передан — он остаётся базовым (параметр сам по себе)");

        // Остальные секции — базовые: наложение одной не тронуло соседей
        SectionsOf(effective).Should().Contain(s => s.GetProperty("id").GetString() == "history");
        foreach (var other in SectionsOf(effective).Where(s => s.GetProperty("id").GetString() != "roleRules"))
            other.GetProperty("textSource").GetString().Should().NotBe("persona");
    }

    [Fact]
    public async Task Put_ВыключенияСекции_ОтдаётсяВыключенной_ТекстИПорядокОстаютсяБазовыми()
    {
        var id = NewPersona().Id;
        // roleRules у аналитика включён дефолтом кода — его и просим выключить
        var baseText = SectionOf(await SectionsAsync(id), "roleRules").GetProperty("text").GetString()!;

        (await PutAsync(id, new { promptSections = new object[] { new { id = "roleRules", enabled = false } } }))
            .Code.Should().Be(HttpStatusCode.OK);

        var roleRules = SectionOf(await SectionsAsync(id), "roleRules");
        roleRules.GetProperty("enabled").GetBoolean().Should().BeFalse();
        roleRules.GetProperty("enabledSource").GetString().Should().Be("persona",
            "форма по источнику различает «выключено мной» и «выключено специальностью»");
        roleRules.GetProperty("text").GetString().Should().Be(baseText);
        roleRules.GetProperty("textSource").GetString().Should().NotBe("persona",
            "текст не просили — он базовый: включить секцию можно без своего черновика");

        SectionsOf(await SectionsAsync(id)).Should().Contain(s => s.GetProperty("id").GetString() == "history",
            "выключение одной секции не тронуло соседние");
        // В ход выключенная не идёт — это фильтр BuildAsync (P1c), здесь состояние, а не выдача
    }

    // Выдача — полное состояние, а не только включённые (находка R-P1c): редактор обязан видеть
    // и «выключено специальностью» с её базовым текстом, чтобы предложить «включить себе».
    // Тот же порядок, что у слоя адмена: наложение идёт по всем состояниям каталога
    [Fact]
    public async Task Get_ВыключеннойСпециальностьюСекцииВидна_ИПерсонаМожетВключитьЕёСебе()
    {
        var id = NewPersona().Id;
        // codeGraph у аналитика выключен дефолтом кода — текст у него есть (его и показывают
        // черновиком при переходе в «своё»)
        var off = SectionOf(await SectionsAsync(id), "codeGraph");
        off.GetProperty("enabled").GetBoolean().Should().BeFalse();
        off.GetProperty("enabledSource").GetString().Should().Be("code");
        off.GetProperty("text").GetString().Should().NotBeNullOrEmpty();

        var own = "свой текст графа " + Guid.NewGuid().ToString("N")[..6];
        (await PutAsync(id, new
        {
            promptSections = new object[] { new { id = "codeGraph", enabled = true, text = own } },
        })).Code.Should().Be(HttpStatusCode.OK);

        var on = SectionOf(await SectionsAsync(id), "codeGraph");
        on.GetProperty("enabled").GetBoolean().Should().BeTrue("параметр задан — перекрывает базу");
        on.GetProperty("enabledSource").GetString().Should().Be("persona");
        on.GetProperty("text").GetString().Should().Be(own);
    }

    [Fact]
    public async Task Put_НеизвестногоIdИТекстаСверхЛимита_400СТекстомМенеджера_ПерсонаНеТронута()
    {
        var id = NewPersona().Id;
        var own = "базовый свой текст";
        (await PutAsync(id, new { promptSections = new object[] { new { id = "roleRules", text = own } } }))
            .Code.Should().Be(HttpStatusCode.OK);

        var unknown = await PutAsync(id, new { promptSections = new object[] { new { id = "no-such" } } });
        unknown.Code.Should().Be(HttpStatusCode.BadRequest, "отказ по каталогу — 400, а не 500");
        unknown.Body.GetProperty("error").GetString().Should().Contain("Неизвестная секция промпта");

        var tooLong = await PutAsync(id, new
        {
            promptSections = new object[] { new { id = "roleRules", text = new string('а', SpecialtyPromptPresets.SectionTextLimit + 1) } },
        });
        tooLong.Code.Should().Be(HttpStatusCode.BadRequest);
        tooLong.Body.GetProperty("error").GetString().Should().Contain("длиннее");

        // Ни один отказ не тронул запись и не оставил полуправки
        Personas.GetRaw(id, OwnerId)!.PromptSections!.Single().Text.Should().Be(own);
        SectionOf(await SectionsAsync(id), "roleRules").GetProperty("text").GetString().Should().Be(own);
    }

    [Fact]
    public async Task Post_СоСвоимиСекциями_СоздаётСоСлоем_ИПустойСписокСбрасываетЕго()
    {
        var own = "роль по-своему " + Guid.NewGuid().ToString("N")[..6];
        var id = await CreateByRestAsync(new
        {
            name = "ps-" + Guid.NewGuid().ToString("N")[..6],
            specialty = "analyst",
            promptSections = new object[] { new { id = "roleRules", text = own } },
        });

        SectionOf(await SectionsAsync(id), "roleRules").GetProperty("text").GetString().Should().Be(own);

        // Явный пустой список — сброс к «всё как у специальности» (не «не менять»)
        (await PutAsync(id, new { promptSections = Array.Empty<object>() })).Code.Should().Be(HttpStatusCode.OK);
        Personas.GetRaw(id, OwnerId)!.PromptSections.Should().BeNull();
        SectionsOf(await SectionsAsync(id)).Should().NotContain(s => s.GetProperty("textSource").GetString() == "persona");
    }

    // --- Смена специальности: сброс и его описание (Г4, §2.3) ---

    [Fact]
    public async Task Put_СоСменойСпециальности_ОтветНазываетСброс_GETБольшеНеPersona()
    {
        var own = "ролевый текст аналитика " + Guid.NewGuid().ToString("N")[..6];
        var id = NewPersona(sections: [new PromptSectionOverride("roleRules", null, own)]).Id;

        var (code, body) = await PutAsync(id, new { specialty = "reviewer" });
        code.Should().Be(HttpStatusCode.OK);
        body.GetProperty("notice").GetString().Should().Be(ResetText,
            "текст — та же строка, что описывает мутацию в менеджере");

        // Слой сброшен: карточка его не несёт, эффективное состояние — только базовое
        Personas.GetRaw(id, OwnerId)!.PromptSections.Should().BeNull();
        body.TryGetProperty("promptSections", out var stored).Should().BeTrue();
        stored.ValueKind.Should().Be(JsonValueKind.Null);
        SectionsOf(await SectionsAsync(id)).Should().NotContain(s => s.GetProperty("textSource").GetString() == "persona",
            "роли аналитика под ревьюером бессмысленны (§2.3)");

        // Строка о сбросе живёт в ответе, а не в записи: следующая выдача карточки её не несёт
        // (поле ставится на копии для ответа — в personas.json ему места нет)
        var again = await (await _client.GetAsync($"/api/personas/{id}")).Content.ReadFromJsonAsync<JsonElement>();
        again.TryGetProperty("notice", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Put_ТойЖеСпециальности_СбросаНетИСтрокиОВсбросеНет()
    {
        var own = "ролевый текст " + Guid.NewGuid().ToString("N")[..6];
        var id = NewPersona(sections: [new PromptSectionOverride("roleRules", null, own)]).Id;

        // Форма шлёт карточку целиком: та же специальность — не смена, слой жив
        var (code, body) = await PutAsync(id, new { specialty = "analyst", role = "Аналитик" });
        code.Should().Be(HttpStatusCode.OK);
        body.TryGetProperty("notice", out _).Should().BeFalse("строка о сбросе — только о реальном сбросе");

        Personas.GetRaw(id, OwnerId)!.PromptSections!.Single().Text.Should().Be(own);
    }

    // --- Чтение: доступ и пустая специальность ---

    [Fact]
    public async Task Get_ПерсоныБезСпециальности_ПустойМассив()
    {
        var id = NewPersona(specialty: "none").Id;

        SectionsOf(await SectionsAsync(id)).Should().BeEmpty(
            "у Specialty=None секций нет (§2.3): слой хранится, но не действует");
    }

    [Fact]
    public async Task Get_ЧужойПерсоны_ТотЖеОтказЧтоУКарточки()
    {
        var id = NewPersona().Id;
        var other = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var resp = await other.GetAsync($"/api/personas/{id}/prompt-sections");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "доступ ручки — как у GET {id}: чужая персона не доходит до тела");
    }

    // --- Тот же путь через MCP (Г4: personas_create / personas_update → тот же менеджер) ---

    private async Task<(string ProjectId, string SessionId)> ProjectChatAsync()
    {
        var project = await _client.PostAsJsonAsync("/api/projects", new { name = $"pp-{Guid.NewGuid():N}" });
        project.EnsureSuccessStatusCode();
        var projectId = (await project.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var session = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "acceptEdits" });
        session.EnsureSuccessStatusCode();
        return (projectId, (await session.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!);
    }

    private async Task<JsonElement> CallAsync(string sessionId, string tool, object args)
    {
        var resp = await _client.AsCaller(sessionId).PostAsJsonAsync($"/mcp/personas/{sessionId}", new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments = args },
        });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result");
    }

    private static bool IsError(JsonElement r) => r.TryGetProperty("isError", out var e) && e.GetBoolean();
    private static string TextOf(JsonElement r) => r.GetProperty("content")[0].GetProperty("text").GetString()!;

    [Fact]
    public async Task McpPersonasUpdate_СоСменойСпециальности_ОтветНазываетСброс()
    {
        var (_, sessionId) = await ProjectChatAsync();
        var own = "ролевый текст аналитика " + Guid.NewGuid().ToString("N")[..6];
        var target = NewPersona(sections: [new PromptSectionOverride("roleRules", null, own)]);

        var result = await CallAsync(sessionId, "personas_update", new { id = target.Id, specialty = "reviewer" });

        IsError(result).Should().BeFalse(TextOf(result));
        // Строка — явной строкой ответа: в JSON карточки модель её пропустит
        TextOf(result).Should().Contain(ResetText);
        Personas.GetRaw(target.Id, OwnerId)!.PromptSections.Should().BeNull();
        SectionsOf(await SectionsAsync(target.Id)).Should()
            .NotContain(s => s.GetProperty("textSource").GetString() == "persona");
    }

    [Fact]
    public async Task McpPersonasCreate_СоСвоимиСекциями_ПишетТотЖеСлой_НеизвестныйIdОтказываетТемЖеТекстом()
    {
        var (_, sessionId) = await ProjectChatAsync();
        var own = "роль по-своему " + Guid.NewGuid().ToString("N")[..6];
        var name = "ps-" + Guid.NewGuid().ToString("N")[..6];

        var created = await CallAsync(sessionId, "personas_create", new
        {
            name,
            specialty = "analyst",
            promptSections = new object[] { new { id = "roleRules", text = own } },
        });
        IsError(created).Should().BeFalse(TextOf(created));

        // Id — из стора по уникальному имени: текст ответа personas_create — карточка персоны
        // плюс строка автоподбора голоса, разбирать её ради id здесь не нужно
        var id = Personas.GetByOwner(OwnerId).Single(p => p.Name == name).Id;
        SectionOf(await SectionsAsync(id), "roleRules").GetProperty("text").GetString().Should().Be(own,
            "тот же PersonaManager, что у REST (Г4)");

        var bad = await CallAsync(sessionId, "personas_update", new
        {
            id,
            promptSections = new object[] { new { id = "no-such" } },
        });
        IsError(bad).Should().BeTrue();
        TextOf(bad).Should().Contain("Неизвестная секция промпта");
        Personas.GetRaw(id, OwnerId)!.PromptSections!.Single().Text.Should().Be(own);
    }
}
