using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// KM-T1e (И5, разрез §4 №3–4): GET /api/child/subjects — проекция репетиторов ребёнка
/// (только включённые предметы, порядок профиля, без промпта/маршрута/голоса), и
/// GET /api/personas/{id}/avatar с [ChildAllowed]: ребёнку — только персона-репетитор
/// его профиля или его предмета, чужая — 404.
///
/// Ребёнок — второй пользователь фабрики: профиль пишется прямо в сторе (тот же приём,
/// что у ChildModeEndpointGuardTests.SetChild); персоны-репетиторы — через PersonaManager,
/// владелец — сам ребёнок (как ChildTutorProvisioner).
/// </summary>
[Trait("Category", "Host")]
public class ChildSubjectsEndpointsTests : IClassFixture<ChildSubjectsEndpointsTests.Hosts>
{
    private readonly Hosts _factory;
    private readonly UserStore _users;
    private readonly PersonaManager _personas;
    private readonly HttpClient _admin;

    public ChildSubjectsEndpointsTests(Hosts factory)
    {
        _factory = factory;
        _users = factory.Services.GetRequiredService<UserStore>();
        _personas = factory.Services.GetRequiredService<PersonaManager>();
        _admin = factory.CreateAuthenticatedClient();
    }

    // Минимальный «PNG»: валидная сигнатура + хвост (без size миниатюра не генерируется,
    // файл отдаётся как есть)
    private static readonly byte[] FakePng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5, 6, 7, 8];

    private sealed record ChildSetup(string ChildId, HttpClient Child, string ProfileTutorId, Dictionary<string, string> TutorOf);

    // Ребёнок с профилем и предметами: каждому предмету — своя живая персона ребёнка
    // (owner — сам ребёнок); withAvatar — аватар на диске и в записи персоны.
    private ChildSetup SetupChild(params (string Id, string Label, bool Enabled, bool WithAvatar)[] subjects)
    {
        var user = _users.FindByUsername(TestWebApplicationFactory.SecondUsername)!;
        var childId = user.Id;

        var profileTutor = MakeTutor(childId, "Помощник", withAvatar: true);
        var tutorOf = new Dictionary<string, string>();
        var list = new List<ChildSubject>();
        foreach (var (id, label, enabled, withAvatar) in subjects)
        {
            var persona = MakeTutor(childId, $"Репетитор {label}", withAvatar);
            tutorOf[id] = persona.Id;
            list.Add(new ChildSubject
            {
                Id = id, Label = label, Glyph = "Аа", Color = "info",
                TutorPersonaId = persona.Id, ProviderKey = "key", Model = "sonnet", Enabled = enabled,
            });
        }

        user.ChildMode = new ChildProfile
        {
            Enabled = true,
            TutorPersonaId = profileTutor.Id,
            ProviderKey = "key",
            Model = "sonnet",
            Subjects = list,
        };
        return new ChildSetup(childId,
            _factory.CreateAuthenticatedClient(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword),
            profileTutor.Id, tutorOf);
    }

    private Persona MakeTutor(string childId, string name, bool withAvatar)
    {
        var persona = _personas.Create(childId, name, role: null, description: null, systemPrompt: null,
            model: null, effort: null, scope: PersonaScope.Global, projectId: null,
            color: null, greeting: null, memoryEnabled: false);
        if (withAvatar)
        {
            var file = "avatar-test.png";
            Directory.CreateDirectory(Path.Combine(_personas.AssetsDir, persona.Id));
            File.WriteAllBytes(Path.Combine(_personas.AssetsDir, persona.Id, file), FakePng);
            // Create возвращает экземпляр стора (PersonaManager.cs:649/657) — запись аватара
            // видна Get'у: тот же признак, что у 404 в самой аватар-ручке
            persona.Avatar = new PersonaAvatar { Kind = PersonaAvatarKind.Image, ImageFile = file };
        }
        return persona;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage r) =>
        JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync());

    // ── DenyAll внутри контроллера (возврат Глеба, п.2–3) ───────────────────
    //
    // Статичный DenyAll-ребёнок до тела контроллера не доходит: гейт отбивает его 403
    // на любом эндпоинте (ChildModeGateMiddleware.cs:37, сторож DenyAllProfile_Gets403_EvenOnAllowedEndpoint).
    // HTTP-дорога к ветке DenyAll в теле — только гонка двух чтений (тот же приём, что у
    // ChildStatusControllerTests.RacePolicy): первое чтение (гейт) — валидный профиль,
    // между чтениями «взрослый» правит его посреди запроса (пустой ProviderKey),
    // второе чтение (контроллер) — настоящий DenyAll. Обёртка роняет профиль только
    // детей с префиксом RacePrefix; все прочие дети — точная делегация боевой политике.

    private const string RacePrefix = "kid-denyall-";

    public sealed class Hosts : TestWebApplicationFactory
    {
        public Hosts() => ExtraServices = services =>
            services.AddSingleton<ClaudeHomeServer.Services.ChildMode.IChildModePolicy>(
                sp => new RacePolicy(sp.GetRequiredService<IUserStore>()));
    }

    private sealed class RacePolicy(IUserStore users) : ClaudeHomeServer.Services.ChildMode.IChildModePolicy
    {
        private readonly ChildModePolicy _inner = new(users);

        public ClaudeHomeServer.Services.ChildMode.ChildPolicySnapshot? For(string ownerId)
        {
            var snapshot = _inner.For(ownerId);
            // Первое успешное чтение — как есть (гейт проходит) и роняет ProviderKey:
            // следующее чтение — невалидный по IsValid профиль (ChildModePolicy.cs:38) → DenyAll.
            if (snapshot is { DenyAll: false } && users.GetById(ownerId) is { Username: string u, ChildMode: { } profile }
                && u.StartsWith(RacePrefix, StringComparison.Ordinal))
                profile.ProviderKey = "";
            return snapshot;
        }
    }

    // Ребёнок гонки: новый пользователь с валидным профилем и аватарным тьютором профиля;
    // обёртка роняет ProviderKey после первого чтения — второе чтение (тела контроллера) — DenyAll.
    private async Task<(string ChildId, HttpClient Child, string ProfileTutorId)> SetupRaceChild(string suffix)
    {
        var username = RacePrefix + suffix;
        var created = await _admin.PostAsJsonAsync("/api/users", new
        {
            username,
            password = TestWebApplicationFactory.SecondPassword,
            role = "user",
            executionEnvironment = ExecutionEnvironments.Container,
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var childId = (await JsonAsync(created)).GetProperty("id").GetString()!;

        var profileTutor = MakeTutor(childId, "Помощник", withAvatar: true);
        _users.GetById(childId)!.ChildMode = new ChildProfile
        {
            Enabled = true,
            TutorPersonaId = profileTutor.Id,
            ProviderKey = "key",
            Model = "sonnet",
        };
        return (childId,
            _factory.CreateAuthenticatedClient(username, TestWebApplicationFactory.SecondPassword),
            profileTutor.Id);
    }

    // Мутация «убрать child.DenyAll ||» (PersonasController.cs:662) — этот тест краснеет:
    // тьютор есть в невалидном профиле (IsChildTutor true), аватар отдался бы 200.
    [Fact]
    public async Task Avatar_ТьюторПриDenyAll_404()
    {
        var (_, child, profileTutorId) = await SetupRaceChild("avatar");
        var resp = await child.GetAsync($"/api/personas/{profileTutorId}/avatar");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // Тот же DenyAll для /subjects: 503 policy_unavailable (ChildSubjectsController.cs:38),
    // а не 200 с плитками из нечитаемого профиля.
    [Fact]
    public async Task Subjects_DenyAll_503PolicyUnavailable()
    {
        var (_, child, _) = await SetupRaceChild("subjects");
        var resp = await child.GetAsync("/api/child/subjects");
        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await JsonAsync(resp)).GetProperty("code").GetString().Should().Be("policy_unavailable");
    }

    // ── GET /api/child/subjects ─────────────────────────────────────────────

    [Fact]
    public async Task Subjects_ТолькоВключённые_ВПорядкеПрофиля()
    {
        var setup = SetupChild(
            ("math", "Математика", true, true),
            ("nature", "Окружающий мир", true, false),
            ("sport", "Физкультура", false, true));   // выключенный — не отдаётся

        var resp = await setup.Child.GetAsync("/api/child/subjects");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await JsonAsync(resp);

        var subjects = body.GetProperty("subjects");
        subjects.GetArrayLength().Should().Be(2);
        subjects[0].GetProperty("id").GetString().Should().Be("math");
        subjects[1].GetProperty("id").GetString().Should().Be("nature");   // порядок профиля, не алфавит

        var tile = subjects[0];
        tile.GetProperty("label").GetString().Should().Be("Математика");
        tile.GetProperty("glyph").GetString().Should().Be("Аа");
        tile.GetProperty("color").GetString().Should().Be("info");
        // hasAvatar — по признаку аватара персоны: у math есть, у nature нет
        tile.GetProperty("tutor").GetProperty("personaId").GetString().Should().Be(setup.TutorOf["math"]);
        tile.GetProperty("tutor").GetProperty("name").GetString().Should().Be("Репетитор Математика");
        tile.GetProperty("tutor").GetProperty("hasAvatar").GetBoolean().Should().BeTrue();
        subjects[1].GetProperty("tutor").GetProperty("hasAvatar").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Subjects_БезПромптаМаршрутаИГолоса()
    {
        var setup = SetupChild(("math", "Математика", true, true));
        var text = await (await setup.Child.GetAsync("/api/child/subjects")).Content.ReadAsStringAsync();

        // И5: ребёнку — проекция, а не настройка. Ни ключей маршрута/промпта/голоса, ни их значений
        text.Should().NotContain("providerKey")
            .And.NotContain("model")
            .And.NotContain("character")
            .And.NotContain("voice")
            .And.NotContain("routeAcknowledged");
    }

    [Fact]
    public async Task Subjects_Взрослый_403NotChild()
    {
        var admin = _factory.CreateAuthenticatedClient();
        var resp = await admin.GetAsync("/api/child/subjects");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await JsonAsync(resp)).GetProperty("code").GetString().Should().Be("not_child");
    }

    // Висящий ключ репетитора (возврат Глеба, п.4): персона удалена (id из профиля, живой
    // персоны нет) или чужого владельца — резолв в сторе персон даёт null (per-owner),
    // проекция не выдумывает имя и аватар: tutor: null, сама плитка остаётся.
    [Fact]
    public async Task Subjects_ВисящийКлючРепетитора_TutorNull()
    {
        var admin = _factory.CreateAuthenticatedClient();
        var foreign = await admin.PostAsJsonAsync("/api/personas", new { name = "Чужая" });
        var foreignId = (await JsonAsync(foreign)).GetProperty("id").GetString()!;

        var setup = SetupChild(
            ("math", "Математика", true, false),   // ключ «удалённой» персоны: живой с таким id нет
            ("hist", "История", true, false));     // ключ персоны другого владельца
        var user = _users.FindByUsername(TestWebApplicationFactory.SecondUsername)!;
        user.ChildMode!.Subjects[0].TutorPersonaId = Guid.NewGuid().ToString("N");
        user.ChildMode.Subjects[1].TutorPersonaId = foreignId;

        var resp = await setup.Child.GetAsync("/api/child/subjects");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var subjects = (await JsonAsync(resp)).GetProperty("subjects");
        subjects.GetArrayLength().Should().Be(2);
        subjects[0].GetProperty("id").GetString().Should().Be("math");
        subjects[0].GetProperty("tutor").ValueKind.Should().Be(JsonValueKind.Null);
        subjects[1].GetProperty("id").GetString().Should().Be("hist");
        subjects[1].GetProperty("tutor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ── GET /api/personas/{id}/avatar + [ChildAllowed] ──────────────────────

    [Fact]
    public async Task Avatar_РепетиторПредметаРебёнка_200()
    {
        var setup = SetupChild(("math", "Математика", true, true));
        var resp = await setup.Child.GetAsync($"/api/personas/{setup.TutorOf["math"]}/avatar");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsByteArrayAsync()).Should().Equal(FakePng);
    }

    [Fact]
    public async Task Avatar_ТьюторПрофиляРебёнка_200()
    {
        var setup = SetupChild(("math", "Математика", true, false));
        var resp = await setup.Child.GetAsync($"/api/personas/{setup.ProfileTutorId}/avatar");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // Мутация «убрать проверку репетитора» (IsChildTutor в PersonasController) — этот тест
    // краснеет: своя не-репетиторская персона с аватаром отдалась бы 200.
    [Fact]
    public async Task Avatar_СвояПерсонаНеРепетитор_404()
    {
        var setup = SetupChild(("math", "Математика", true, false));
        var other = MakeTutor(setup.ChildId, "Не репетитор", withAvatar: true);   // своя, но не в профиле

        var resp = await setup.Child.GetAsync($"/api/personas/{other.Id}/avatar");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Avatar_ЧужаяПерсонаДругогоВладельца_404()
    {
        var setup = SetupChild(("math", "Математика", true, false));
        var admin = _factory.CreateAuthenticatedClient();
        var created = await admin.PostAsJsonAsync("/api/personas", new { name = "Взрослая" });
        var foreignId = (await JsonAsync(created)).GetProperty("id").GetString()!;

        var resp = await setup.Child.GetAsync($"/api/personas/{foreignId}/avatar");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // Выключенный предмет скрыт с главной, но его репетитор — всё ещё репетитор из профиля
    // ребёнка (разрез §4 №3 перечисляет Subjects[*] без оговорки про Enabled).
    [Fact]
    public async Task Avatar_РепетиторВыключенногоПредмета_200()
    {
        var setup = SetupChild(("sport", "Физкультура", false, true));
        var resp = await setup.Child.GetAsync($"/api/personas/{setup.TutorOf["sport"]}/avatar");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
