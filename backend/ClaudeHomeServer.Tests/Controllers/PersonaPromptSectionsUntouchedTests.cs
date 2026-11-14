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
/// П1e (разрез 2, §3 Г4): неприкосновенность слоя секций промпта на чужих ручках персоны.
///
/// Слой задаётся через PUT {id} (единственная REST-запись, P1d), затем по одному
/// представителю каждой группы ручек — bindings/*, automation/*, voice, avatar/*, memory/*,
/// make-default — вызываются по-настоящему (2xx, с записью в стор). После каждой из них
/// PromptSections побайтно тот же: ни одна из этих ручек не проходит PersonaManager.Update
/// со сменой специальности (сброс слоя живёт только там, PersonaManager.cs:1046), и ни одна
/// не пишет поле сама. IL-сторож PersonaPromptSectionsGuardTests закрывает тот же инвариант
/// по коду; этот тест закрывает его по поведению REST — включая путь, где класс-владелец
/// правки не назван в allow-list, а правка случилась бы через чужую ручку.
///
/// Ручки создания pantheon/connect и ai/quick-create: у новой персоны PromptSections == null —
/// они зовут Create без параметра promptSections (PersonaManager.cs:612, PersonasController.cs:879)
/// и не могут задать слой «случайно».
/// </summary>
[Trait("Category", "Host")]
public class PersonaPromptSectionsUntouchedTests : IClassFixture<PersonaPromptSectionsUntouchedTests.Hosts>
{
    // Хост на класс: своя фабрика, свои персоны; общий только с тестами этого класса.
    public sealed class Hosts : TestWebApplicationFactory;

    // Стаб дешёвой модели для ai/quick-create: черновик — фиксированный валидный JSON
    // (та же форма, что DraftJson у NoLlm в SharedProjectPersonaBindingsTests.cs:145),
    // остальные вызовы — «[]» (авто-подбор: «ничего не подобрано», штатный исход).
    // STATIC: экземпляр обязан быть тем же объектом, что зарегистрирован в хосте.
    private static readonly DraftLlm _llm = new();

    private sealed class DraftLlm : ICheapTextRunner
    {
        private const string DraftJson =
            """{"name":"ps-quick-draft","role":"Аналитик","description":"Черновик quick-create","tone":"сухо и по делу","color":"blue"}""";

        public bool UsesLocal(string actionKey) => false;
        public bool HasFreeRoute(string actionKey) => false;
        public bool HasRoute(string actionKey) => false;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "stub";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
            => Task.FromResult(actionKey == LocalActionCatalog.PersonaQuickCreate ? DraftJson : "[]");

        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default)
            => Task.FromResult<string?>("[]");

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public PersonaPromptSectionsUntouchedTests(Hosts hosts)
    {
        _factory = hosts;
        _factory.ExtraServices = services => services.AddSingleton<ICheapTextRunner>(_llm);
        _client = _factory.CreateAuthenticatedClient();
    }

    private PersonaManager Personas => _factory.Services.GetRequiredService<PersonaManager>();
    private string OwnerId => _factory.Services.GetRequiredService<UserStore>()
        .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

    private static string RunId() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>Персона-аналитик со своим слоем на roleRules (включён дефолтом кода —
    /// тот же выбор, что у P1d-тестов) через PUT, как делает фронт. Возвращает снимок слоя —
    /// сериализованный, для побайтного сравнения после каждой ручки.</summary>
    private async Task<string> NewPersonaWithLayerAsync()
    {
        var id = Personas.Create(OwnerId, "unt-" + RunId(), null, null, null, null, null,
            PersonaScope.Global, null, null, null, false,
            specialty: PersonaSpecialty.Analyst).Id;

        var own = "свой текст роли " + RunId();
        var resp = await _client.PutAsJsonAsync($"/api/personas/{id}",
            new { promptSections = new object[] { new { id = "roleRules", text = own } } });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "слой задаётся штатной записью P1d");

        var snapshot = SerializedLayer(id);
        snapshot.Should().Contain(own, "снимок обязан содержать заданный слой, иначе сравнение вакуумно");
        return id;
    }

    // Детерминированная сериализация для побайтного сравнения: без UnsafeRelaxedJsonEscaping
    // кириллица уезжает в \uXXXX и сравнение «снимок содержит заданный текст» ложно на
    // верном слое (экранирование стабильно, но текст в нём не читается).
    private static readonly JsonSerializerOptions LayerJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Побайтное состояние слоя в сторе: сериализованный список записей
    /// (null-слою здесь быть не может — это сбой, а не «то же состояние»).</summary>
    private string SerializedLayer(string id)
    {
        var raw = Personas.GetRaw(id, OwnerId)!;
        raw.PromptSections.Should().NotBeNull("слой задан заранее и не должен исчезать без записи поля");
        return JsonSerializer.Serialize(raw.PromptSections, LayerJson);
    }

    private async Task SendOkAsync(HttpMethod method, string url, object? body, string what)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        var resp = await _client.SendAsync(request);
        var text = await resp.Content.ReadAsStringAsync();
        ((int)resp.StatusCode).Should().BeGreaterThanOrEqualTo(200).And.BeLessThan(300,
            $"{what}: ручка обязана отработать по-настоящему (2xx), иначе проверка «не тронула» вакуумна. Тело: {text}");
    }

    [Fact]
    public async Task РучкиПривязокАвтоматикиГолосаАватараПамятиИDefault_НеТрогаютСлойПобайтно()
    {
        var id = await NewPersonaWithLayerAsync();
        var snapshot = SerializedLayer(id);

        // Аватар-ручке нужен настоящий кандидат: прямая запись стора + файлы, как в
        // SharedProjectPersonaRightsTests.cs:218-223 (иначе select дал бы 404 и «не тронула»
        // была бы проверкой отказа, а не записи).
        Personas.SetAvatarUploaded(id, OwnerId, "avatar-arr.png", "original-arr.png", null);
        var assetDir = Path.Combine(Personas.AssetsDir, id);
        Directory.CreateDirectory(Path.Combine(assetDir, "candidates"));
        File.WriteAllBytes(Path.Combine(assetDir, "avatar-arr.png"), FakePng());
        File.WriteAllBytes(Path.Combine(assetDir, "original-arr.png"), FakePng());
        File.WriteAllBytes(Path.Combine(assetDir, "candidates", "cand.png"), FakePng());

        // По одному представителю каждой группы ручек (8 всего — с двумя ручками создания
        // во втором тесте). Тела — валидные, из прецедента WriteActions
        // (SharedProjectPersonaRightsTests.cs:430-453): отказ здесь означал бы, что ручка
        // не дошла до записи.
        var calls = new (HttpMethod Method, string Url, object? Body, string What)[]
        {
            (HttpMethod.Post, $"/api/personas/{id}/bindings",
                new { type = "tool", target = "web" }, "POST bindings (добавить привязку)"),
            (HttpMethod.Put, $"/api/personas/{id}/bindings",
                new { bindings = new[] { new { type = "tool", target = "web" } } }, "PUT bindings (заменить набор)"),
            (HttpMethod.Post, $"/api/personas/{id}/automation",
                new { name = "Правило " + RunId(), enabled = false, actionInstruction = "Сказать слово" },
                "POST automation (добавить правило)"),
            (HttpMethod.Put, $"/api/personas/{id}/voice",
                new { voice = "sofia", role = "casual" }, "PUT voice (голос)"),
            (HttpMethod.Post, $"/api/personas/{id}/avatar/select",
                new { file = "cand.png" }, "POST avatar/select (выбор аватара)"),
            (HttpMethod.Post, $"/api/personas/{id}/memory",
                new { type = "semantic", text = "Запись " + RunId() }, "POST memory (запомнить)"),
            (HttpMethod.Post, $"/api/personas/{id}/make-default", null, "POST make-default"),
        };

        foreach (var (method, url, body, what) in calls)
        {
            await SendOkAsync(method, url, body, what);
            SerializedLayer(id).Should().Be(snapshot,
                $"{what}: чужая ручка персоны изменила слой секций промпта — запись шла бы мимо " +
                "PersonaManager.Update или со сменой специальности (сброс слоя, §2.3)");
        }
    }

    [Fact]
    public async Task PantheonConnect_ИQuickCreate_НоваяПерсонаБезСлоя()
    {
        // pantheon/connect: первая роль каталога (ключ берём из GET pantheon — каталожные
        // имена не должны жить в тесте). Ответ — список персон; каждая без слоя.
        var catalog = await _client.GetFromJsonAsync<JsonElement>("/api/personas/pantheon");
        var key = catalog.GetProperty("templates")[0].GetProperty("key").GetString()!;
        var connected = await _client.PostAsJsonAsync("/api/personas/pantheon/connect",
            new { keys = new[] { key } });
        connected.StatusCode.Should().Be(HttpStatusCode.OK);
        var created = (await connected.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        created.Should().NotBeEmpty("connect обязан вернуть подключённую роль");
        foreach (var persona in created)
        {
            var pid = persona.GetProperty("id").GetString()!;
            persona.TryGetProperty("promptSections", out var ps)
                .Should().BeTrue("поле аддитивное — в форме оно есть");
            ps.ValueKind.Should().Be(JsonValueKind.Null,
                $"пантеон ({key}) создаёт персону через Create без promptSections — слоя быть не может");
            Personas.GetRaw(pid, OwnerId)!.PromptSections.Should().BeNull("и в сторе");
        }

        // ai/quick-create: черновик — стаб модели; создание идёт тем же Create без параметра.
        var quick = await _client.PostAsJsonAsync("/api/personas/ai/quick-create",
            new { prompt = "собери аналитика для проверки слоя" });
        quick.StatusCode.Should().Be(HttpStatusCode.OK);
        var quickId = (await quick.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        Personas.GetRaw(quickId, OwnerId)!.PromptSections.Should().BeNull(
            "quick-create не принимает и не задаёт слой (Г4: поле задают только POST/PUT персоны)");
    }

    private static byte[] FakePng() =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5, 6, 7, 8];
}
