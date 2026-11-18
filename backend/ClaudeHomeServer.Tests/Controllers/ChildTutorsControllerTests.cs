using System.IO;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Т2c (И5, разрез «Репетиторы» §5 / «Знания» §5): админские ручки репетитора —
/// POST /api/users/{id}/child-subjects/{subjectId}/tutor (провижн, идемпотентно) и
/// PUT /api/users/{id}/child-tutors/{personaId} (name/character/greeting/voice/gender +
/// mustDo/mustNot). Т2c3 (блокер Т4e): GET /api/users/{id}/child-tutors/{personaId}/avatar
/// и /avatar/candidate/{file} — чтение аватара и кандидатов админом (персона ребёнка
/// в /api/personas/{id}/avatar* админу не видна). Пути допуска: ребёнок → 403, чужая
/// персона и своя-не-репетитор → 404 tutor_not_found, admin-claim без роли в сторе →
/// 403 (AdminByStore). Путь №11: автоматика персон персоне-репетитору ребёнка не
/// создаётся ни админской ручкой (404 — чужая глобальная персона), ни самим ребёнком
/// (403 — closed-периметр).
///
/// Ребёнок — второй пользователь фабрики, профиль пишется прямо в сторе (тот же приём,
/// что у ChildSubjectsEndpointsTests.SetupChild): ручки работают с записью профиля,
/// политика и провижн — настоящие сервисы хоста.
/// </summary>
[Trait("Category", "Host")]
public class ChildTutorsControllerTests : IClassFixture<ChildTutorsControllerTests.Hosts>
{
    public sealed class Hosts : TestWebApplicationFactory { }

    private readonly Hosts _hosts;
    private readonly HttpClient _admin;
    private readonly UserStore _users;
    private readonly PersonaManager _personas;

    public ChildTutorsControllerTests(Hosts hosts)
    {
        _hosts = hosts;
        _admin = _hosts.CreateAuthenticatedClient();
        _users = _hosts.Services.GetRequiredService<UserStore>();
        _personas = _hosts.Services.GetRequiredService<PersonaManager>();
    }

    private sealed record ChildSetup(
        string ChildId, HttpClient Child, string ProfileTutorId, Dictionary<string, string> TutorOf);

    /// <summary>Ребёнок с валидным профилем и предметами: каждому — живая персона-репетитор
    /// (owner — сам ребёнок, как у ChildTutorProvisioner). Профиль валиден (ChildModePolicy.IsValid):
    /// 403 теста «ребёнок» обязан приходить из допуска ручки, а не из DenyAll-политики.</summary>
    private ChildSetup SetupChild(params (string Id, string Label)[] subjects)
    {
        var childId = _users.FindByUsername(TestWebApplicationFactory.SecondUsername)!.Id;

        var profileTutor = MakeTutor(childId, "Помощник");
        var tutorOf = new Dictionary<string, string>();
        var list = new List<ChildSubject>();
        foreach (var (id, label) in subjects)
        {
            var persona = MakeTutor(childId, $"Репетитор {label}");
            tutorOf[id] = persona.Id;
            list.Add(new ChildSubject
            {
                Id = id, Label = label, Glyph = "М", Color = "info",
                TutorPersonaId = persona.Id, ProviderKey = "key", Model = "sonnet", Enabled = true,
            });
        }

        _users.GetById(childId)!.ChildMode = new ChildProfile
        {
            Enabled = true,
            TutorPersonaId = profileTutor.Id,
            ProviderKey = "key",
            Model = "sonnet",
            Subjects = list,
        };
        return new ChildSetup(childId,
            _hosts.CreateAuthenticatedClient(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword),
            profileTutor.Id, tutorOf);
    }

    private Persona MakeTutor(string childId, string name) =>
        _personas.Create(childId, name, role: null, description: null, systemPrompt: null,
            model: null, effort: null, scope: PersonaScope.Global, projectId: null,
            color: null, greeting: null, memoryEnabled: false);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage r) =>
        JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync());

    private static string CodeOf(JsonElement body) =>
        body.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";

    // ── POST child-subjects/{subjectId}/tutor ───────────────────────────────

    // Идемпотентность: живой ключ возвращает ту же персону без создания второй
    // (тот же ключ, что держит EnsureSubjectTutorAsync после PUT child-profile).
    [Fact]
    public async Task EnsureTutor_ЖивойКлюч_ТотЖеIdБезВторойПерсоны()
    {
        var setup = SetupChild(("math", "Математика"));
        var first = await _admin.PostAsync($"/api/users/{setup.ChildId}/child-subjects/math/tutor", null);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var id1 = (await JsonAsync(first)).GetProperty("personaId").GetString();

        var second = await _admin.PostAsync($"/api/users/{setup.ChildId}/child-subjects/math/tutor", null);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var id2 = (await JsonAsync(second)).GetProperty("personaId").GetString();

        id1.Should().Be(setup.TutorOf["math"]);
        id2.Should().Be(id1);
        _personas.Get(id1!, setup.ChildId).Should().NotBeNull();
    }

    // Предмет без ключа репетитора (недозаполненная запись редактора): провижн создаёт
    // персону, закрепляет ключ и повторяет вызов уже не трогает.
    [Fact]
    public async Task EnsureTutor_ПустойКлюч_СоздаётИЗакрепляет()
    {
        var setup = SetupChild(("phys", "Физика"));
        _users.GetById(setup.ChildId)!.ChildMode!.Subjects.Single(s => s.Id == "phys").TutorPersonaId = "";

        var resp = await _admin.PostAsync($"/api/users/{setup.ChildId}/child-subjects/phys/tutor", null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var created = (await JsonAsync(resp)).GetProperty("personaId").GetString()!;

        _users.GetById(setup.ChildId)!.ChildMode!.Subjects.Single(s => s.Id == "phys")
            .TutorPersonaId.Should().Be(created);
        _personas.Get(created, setup.ChildId)!.Name.Should().Be("Репетитор: Физика");

        var again = await _admin.PostAsync($"/api/users/{setup.ChildId}/child-subjects/phys/tutor", null);
        (await JsonAsync(again)).GetProperty("personaId").GetString().Should().Be(created);
    }

    [Fact]
    public async Task EnsureTutor_НетПредмета_404SubjectNotFound()
    {
        var setup = SetupChild(("math", "Математика"));
        var resp = await _admin.PostAsync($"/api/users/{setup.ChildId}/child-subjects/no-such/tutor", null);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        CodeOf(await JsonAsync(resp)).Should().Be("subject_not_found");
    }

    // Путь №8: ребёнок — 403 (closed-периметр И5: свой репетитор он не правит).
    [Fact]
    public async Task EnsureTutor_Ребёнок_403()
    {
        var setup = SetupChild(("math", "Математика"));
        var resp = await setup.Child.PostAsync($"/api/users/{setup.ChildId}/child-subjects/math/tutor", null);
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── PUT child-tutors/{personaId} ────────────────────────────────────────

    [Fact]
    public async Task PutTutor_МеняетИмяХарактерИПол_ModelИгнорируется()
    {
        var setup = SetupChild(("math", "Математика"));
        var tutorId = setup.TutorOf["math"];

        var resp = await _admin.PutAsJsonAsync($"/api/users/{setup.ChildId}/child-tutors/{tutorId}", new
        {
            name = "Марья Ивановна",
            character = "Ты — спокойный наставник: ведёшь ученицу вопросами.",
            greeting = "Привет! С чего начнём?",
            gender = "female",
            model = "opus",   // маршрут — в записи предмета, не в персоне (редакция 2 §12)
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var persona = _personas.Get(tutorId, setup.ChildId)!;
        persona.Name.Should().Be("Марья Ивановна");
        persona.Contract!.Character.Should().Be("Ты — спокойный наставник: ведёшь ученицу вопросами.");
        persona.Greeting.Should().Be("Привет! С чего начнём?");
        persona.Gender.Should().Be(PersonaGender.Female);
        // Persona.Model репетитора остаётся пустым: поле model тела игнорируется.
        persona.Model.Should().BeNull();
    }

    // Путь №8: чужая персона (не репетитор этого ребёнка) — 404 tutor_not_found;
    // своя персона ребёнка, не стоящая в профиле, — тот же 404.
    [Fact]
    public async Task PutTutor_ЧужаяПерсона_404TutorNotFound()
    {
        var setup = SetupChild(("math", "Математика"));
        var foreign = await _admin.PostAsJsonAsync("/api/personas", new { name = "Взрослая персона" });
        var foreignId = (await JsonAsync(foreign)).GetProperty("id").GetString()!;

        var resp = await _admin.PutAsJsonAsync($"/api/users/{setup.ChildId}/child-tutors/{foreignId}",
            new { name = "Переименовать" });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        CodeOf(await JsonAsync(resp)).Should().Be("tutor_not_found");

        // Своя, но не репетитор — тот же отказ.
        var own = MakeTutor(setup.ChildId, "Не репетитор");
        var ownResp = await _admin.PutAsJsonAsync($"/api/users/{setup.ChildId}/child-tutors/{own.Id}",
            new { name = "Переименовать" });
        ownResp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        CodeOf(await JsonAsync(ownResp)).Should().Be("tutor_not_found");
    }

    // Тьютор профиля («Помощник») — тоже репетитор ребёнка: правится этой же ручкой.
    [Fact]
    public async Task PutTutor_ТьюторПрофиля_200()
    {
        var setup = SetupChild(("math", "Математика"));
        var resp = await _admin.PutAsJsonAsync($"/api/users/{setup.ChildId}/child-tutors/{setup.ProfileTutorId}",
            new { name = "Помощник" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // Путь №8: ребёнок — 403.
    [Fact]
    public async Task PutTutor_Ребёнок_403()
    {
        var setup = SetupChild(("math", "Математика"));
        var resp = await setup.Child.PutAsJsonAsync($"/api/users/{setup.ChildId}/child-tutors/{setup.TutorOf["math"]}",
            new { name = "Переименовать" });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _personas.Get(setup.TutorOf["math"], setup.ChildId)!.Name.Should().Be("Репетитор Математика");
    }

    // Путь №8: admin-claim в токене без роли в сторе — 403 (AdminByStore: роль читают
    // по стору, снятое админство не ждёт истечения токена).
    [Fact]
    public async Task PutTutor_AdminClaimБезРолиВСторе_403()
    {
        var setup = SetupChild(("math", "Математика"));
        var created = await _admin.PostAsJsonAsync("/api/users", new
        {
            username = "tutors-ex-admin",
            password = "tutors-ex-password",
            role = "admin",
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var client = _hosts.CreateAuthenticatedClient("tutors-ex-admin", "tutors-ex-password");

        // Снятие админства по стору: токен остаётся «админским» по claim.
        _users.GetById(_users.FindByUsername("tutors-ex-admin")!.Id)!.Role = "user";

        var resp = await client.PutAsJsonAsync($"/api/users/{setup.ChildId}/child-tutors/{setup.TutorOf["math"]}",
            new { name = "Переименовать" });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── mustDo / mustNot (редакция 3 §22) ───────────────────────────────────

    private async Task<HttpStatusCode> PutRulesAsync(ChildSetup setup, object body)
    {
        var resp = await _admin.PutAsJsonAsync($"/api/users/{setup.ChildId}/child-tutors/{setup.TutorOf["math"]}", body);
        return resp.StatusCode;
    }

    private Persona StoredTutor(ChildSetup setup) => _personas.Get(setup.TutorOf["math"], setup.ChildId)!;

    // Два пункта пишутся в Persona.Contract.MustDo в том же порядке (порядок пунктов —
    // порядок списка запроса, «## Всегда» в слое идёт по нему).
    [Fact]
    public async Task PutTutor_MustDo_ДваПункта_ВТомЖеПорядке()
    {
        var setup = SetupChild(("math", "Математика"));
        (await PutRulesAsync(setup, new { mustDo = new[] { "хвали за попытку", "не решай за неё" },
            mustNot = new[] { "не давай готовый ответ" } }))
            .Should().Be(HttpStatusCode.OK);

        var contract = StoredTutor(setup).Contract!;
        contract.MustDo.Should().Equal("хвали за попытку", "не решай за неё");
        contract.MustNot.Should().Equal("не давай готовый ответ");
    }

    // [] — явная очистка: после вызова в сторе не остаётся ни пункта (пустой
    // список нормализуется в null — та же семантика, что у PUT персоны).
    [Fact]
    public async Task PutTutor_MustDo_ПустойСписок_Очищает()
    {
        var setup = SetupChild(("math", "Математика"));
        (await PutRulesAsync(setup, new { mustDo = new[] { "правило одно" } }))
            .Should().Be(HttpStatusCode.OK);
        StoredTutor(setup).Contract!.MustDo.Should().Equal("правило одно");

        (await PutRulesAsync(setup, new { mustDo = Array.Empty<string>() }))
            .Should().Be(HttpStatusCode.OK);
        StoredTutor(setup).Contract?.MustDo.Should().BeNullOrEmpty();
    }

    // null — поле не трогать: прежние списки переживают правку других полей.
    [Fact]
    public async Task PutTutor_MustDo_Null_ОставляетПрежние()
    {
        var setup = SetupChild(("math", "Математика"));
        (await PutRulesAsync(setup, new { mustDo = new[] { "правило одно" }, mustNot = new[] { "запрет один" } }))
            .Should().Be(HttpStatusCode.OK);

        (await PutRulesAsync(setup, new { name = "Новое имя" })).Should().Be(HttpStatusCode.OK);

        var contract = StoredTutor(setup).Contract!;
        contract.MustDo.Should().Equal("правило одно");
        contract.MustNot.Should().Equal("запрет один");
        StoredTutor(setup).Name.Should().Be("Новое имя");
    }

    // Пустые строки — не пункты: отбрасываются до счёта предела.
    [Fact]
    public async Task PutTutor_MustDo_ПустыеСтрокиОтбрасываются()
    {
        var setup = SetupChild(("math", "Математика"));
        (await PutRulesAsync(setup, new { mustDo = new[] { "правильное", "", "   " } }))
            .Should().Be(HttpStatusCode.OK);
        StoredTutor(setup).Contract!.MustDo.Should().Equal("правильное");
    }

    // Границы предела включаются: 12 пунктов по 300 символов — проходят.
    [Fact]
    public async Task PutTutor_MustDo_ГраницыЛимита_Проходят()
    {
        var setup = SetupChild(("math", "Математика"));
        var rules = Enumerable.Range(0, 12).Select(_ => new string('а', 300)).ToArray();
        (await PutRulesAsync(setup, new { mustDo = rules })).Should().Be(HttpStatusCode.OK);
        StoredTutor(setup).Contract!.MustDo.Should().HaveCount(12);
    }

    [Fact]
    public async Task PutTutor_MustDo_ТринадцатьПунктов_400RulesTooLong()
    {
        var setup = SetupChild(("math", "Математика"));
        var rules = Enumerable.Range(0, 13).Select(i => $"пункт {i}").ToArray();
        var resp = await _admin.PutAsJsonAsync($"/api/users/{setup.ChildId}/child-tutors/{setup.TutorOf["math"]}",
            new { mustDo = rules });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        CodeOf(await JsonAsync(resp)).Should().Be("rules_too_long");
        // отказанный вызов не оставил половины применённой
        StoredTutor(setup).Contract?.MustDo.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task PutTutor_MustNot_Пункт301Символ_400RulesTooLong()
    {
        var setup = SetupChild(("math", "Математика"));
        var resp = await _admin.PutAsJsonAsync($"/api/users/{setup.ChildId}/child-tutors/{setup.TutorOf["math"]}",
            new { mustNot = new[] { new string('б', 301) } });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        CodeOf(await JsonAsync(resp)).Should().Be("rules_too_long");
    }

    // Прочие слоты контракта (tone, outputFormat…) — вне ручки (разрез §5): поле tone в
    // теле не меняет Contract.Tone.
    [Fact]
    public async Task PutTutor_ToneВТелеНеМеняетContractTone()
    {
        var setup = SetupChild(("math", "Математика"));
        var stored = StoredTutor(setup);
        stored.Contract = new PersonaContract { Tone = "бережно" };

        (await PutRulesAsync(setup, new { tone = "сухо и по делу", character = "Характер" }))
            .Should().Be(HttpStatusCode.OK);

        StoredTutor(setup).Contract!.Tone.Should().Be("бережно");
        StoredTutor(setup).Contract!.Character.Should().Be("Характер");
    }

    // ── Конкурентность и автоматика ─────────────────────────────────────────

    // Два PUT целиком: стор персон под замком (PersonaManager.Update — один _saveLock),
    // итог гонки — одна из двух записей целиком, не смесь полей из разных вызовов.
    [Fact]
    public async Task PutTutor_Parallel_NoMixedFields()
    {
        var setup = SetupChild(("math", "Математика"));
        var bodyA = new { name = "Имя А", character = "Характер А", greeting = "Привет А" };
        var bodyB = new { name = "Имя Б", character = "Характер Б", greeting = "Привет Б" };

        var putA = _admin.PutAsJsonAsync($"/api/users/{setup.ChildId}/child-tutors/{setup.TutorOf["math"]}", bodyA);
        var putB = _admin.PutAsJsonAsync($"/api/users/{setup.ChildId}/child-tutors/{setup.TutorOf["math"]}", bodyB);
        await Task.WhenAll(putA, putB);
        (await putA).StatusCode.Should().Be(HttpStatusCode.OK);
        (await putB).StatusCode.Should().Be(HttpStatusCode.OK);

        var persona = StoredTutor(setup);
        persona.Contract!.Character.Should().BeOneOf("Характер А", "Характер Б");
        var wholeA = persona.Name == "Имя А" && persona.Contract.Character == "Характер А" && persona.Greeting == "Привет А";
        var wholeB = persona.Name == "Имя Б" && persona.Contract.Character == "Характер Б" && persona.Greeting == "Привет Б";
        (wholeA || wholeB).Should().BeTrue("два одновременных PUT дают одну целую запись, не смесь полей");
    }

    // Путь №11: автоматика персон (И1 п.18) репетитору ребёнка не создаётся: админской
    // ручкой — 404 (чужая глобальная персона не резолвится: ProjectAccessService.ResolvePersona),
    // ребёнком — 403 (closed-периметр). Правила на персоне не появляются.
    [Fact]
    public async Task Automation_ПерсонеРебёнка_НеСоздаётся()
    {
        var setup = SetupChild(("math", "Математика"));
        var tutorId = setup.TutorOf["math"];
        var rule = new { triggerType = "timer", name = "проба" };

        var asAdmin = await _admin.PostAsJsonAsync($"/api/personas/{tutorId}/automation", rule);
        asAdmin.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var asChild = await setup.Child.PostAsJsonAsync($"/api/personas/{tutorId}/automation", rule);
        asChild.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        StoredTutor(setup).AutomationRules.Should().BeNullOrEmpty();
    }

    // ── GET child-tutors/{personaId}/avatar и /avatar/candidate/{file} (Т2c3) ──

    // Сигнатурные байты PNG: читающая ручка проверяет только существование файла
    // (PhysicalFileByExt содержимое не декодирует; для size растр эту «картинку» не
    // распознаёт — ручка отдаёт оригинал с no-cache, 200 в любом случае).
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // Аватар-картинка на записи персоны + файлы на диске (тот же ключ, что у реальной
    // загрузки: {AssetsDir}/{personaId}/avatar-….png + original-….png).
    private void SeedAvatar(Persona persona, string childId)
    {
        var dir = Path.Combine(_personas.AssetsDir, persona.Id);
        Directory.CreateDirectory(dir);
        var imageName = $"avatar-{Guid.NewGuid():N}.png";
        var originalName = $"original-{Guid.NewGuid():N}.png";
        File.WriteAllBytes(Path.Combine(dir, imageName), PngSignature);
        File.WriteAllBytes(Path.Combine(dir, originalName), PngSignature);
        _personas.SetAvatarUploaded(persona.Id, childId, imageName, originalName, null);
    }

    // Файл кандидата в {personaId}/candidates/ (candidate-ручка читает файл, а не запись).
    private string SeedCandidate(Persona persona)
    {
        var dir = Path.Combine(_personas.AssetsDir, persona.Id, "candidates");
        Directory.CreateDirectory(dir);
        var name = $"cand-{Guid.NewGuid():N}.png";
        File.WriteAllBytes(Path.Combine(dir, name), PngSignature);
        return name;
    }

    [Fact]
    public async Task GetTutorAvatar_АдминРепетитор_200ОригиналИФолбэкМиниатюры()
    {
        var setup = SetupChild(("math", "Математика"));
        var tutorId = setup.TutorOf["math"];
        SeedAvatar(_personas.Get(tutorId, setup.ChildId)!, setup.ChildId);

        var original = await _admin.GetAsync($"/api/users/{setup.ChildId}/child-tutors/{tutorId}/avatar");
        original.StatusCode.Should().Be(HttpStatusCode.OK);
        original.Content.Headers.ContentType?.MediaType.Should().Be("image/png");

        // size на файл, который растр не декодирует: миниатюра не генерируется —
        // фолбэк на оригинал (тот же, что у персоны): 200.
        var thumb = await _admin.GetAsync($"/api/users/{setup.ChildId}/child-tutors/{tutorId}/avatar?size=64");
        thumb.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // Репетитор без аватара — тот же пустой 404, что у персоны без файла.
    [Fact]
    public async Task GetTutorAvatar_НетФайла_404()
    {
        var setup = SetupChild(("math", "Математика"));
        var resp = await _admin.GetAsync($"/api/users/{setup.ChildId}/child-tutors/{setup.TutorOf["math"]}/avatar");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // Путь И5: ребёнок своего репетитора через админскую ручку не читает — 403.
    [Fact]
    public async Task GetTutorAvatar_Ребёнок_403()
    {
        var setup = SetupChild(("math", "Математика"));
        SeedAvatar(_personas.Get(setup.TutorOf["math"], setup.ChildId)!, setup.ChildId);
        var resp = await setup.Child.GetAsync($"/api/users/{setup.ChildId}/child-tutors/{setup.TutorOf["math"]}/avatar");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // Чужая (взрослая) персона и своя-но-не-репетитор — один 404 tutor_not_found.
    // Своей-не-репетитору аватар засеян: без проверки «репетитор этого ребёнка»
    // ручка отдала бы файл (мутация краснит именно эту проверку).
    [Fact]
    public async Task GetTutorAvatar_ЧужаяИСвояНеРепетитор_404()
    {
        var setup = SetupChild(("math", "Математика"));

        var foreign = await _admin.PostAsJsonAsync("/api/personas", new { name = "Взрослая персона" });
        var foreignId = (await JsonAsync(foreign)).GetProperty("id").GetString()!;
        var asForeign = await _admin.GetAsync($"/api/users/{setup.ChildId}/child-tutors/{foreignId}/avatar");
        asForeign.StatusCode.Should().Be(HttpStatusCode.NotFound);
        CodeOf(await JsonAsync(asForeign)).Should().Be("tutor_not_found");

        var own = MakeTutor(setup.ChildId, "Не репетитор");
        SeedAvatar(own, setup.ChildId);
        var asOwn = await _admin.GetAsync($"/api/users/{setup.ChildId}/child-tutors/{own.Id}/avatar");
        asOwn.StatusCode.Should().Be(HttpStatusCode.NotFound);
        CodeOf(await JsonAsync(asOwn)).Should().Be("tutor_not_found");
    }

    [Fact]
    public async Task GetTutorAvatarCandidate_АдминРепетитор_200()
    {
        var setup = SetupChild(("math", "Математика"));
        var tutor = _personas.Get(setup.TutorOf["math"], setup.ChildId)!;
        var file = SeedCandidate(tutor);

        var resp = await _admin.GetAsync($"/api/users/{setup.ChildId}/child-tutors/{tutor.Id}/avatar/candidate/{file}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
    }

    [Fact]
    public async Task GetTutorAvatarCandidate_Ребёнок_403()
    {
        var setup = SetupChild(("math", "Математика"));
        var tutor = _personas.Get(setup.TutorOf["math"], setup.ChildId)!;
        var file = SeedCandidate(tutor);
        var resp = await setup.Child.GetAsync($"/api/users/{setup.ChildId}/child-tutors/{tutor.Id}/avatar/candidate/{file}");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // Своя-не-репетитор с живым файлом кандидата — 404 (та же точка мутации, что у avatar).
    [Fact]
    public async Task GetTutorAvatarCandidate_СвояНеРепетитор_404()
    {
        var setup = SetupChild(("math", "Математика"));
        var own = MakeTutor(setup.ChildId, "Не репетитор");
        var file = SeedCandidate(own);
        var resp = await _admin.GetAsync($"/api/users/{setup.ChildId}/child-tutors/{own.Id}/avatar/candidate/{file}");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        CodeOf(await JsonAsync(resp)).Should().Be("tutor_not_found");
    }

    // Path-traversal: ..%2Fx — файл на уровень выше candidates/ не отдаётся: 404.
    [Fact]
    public async Task GetTutorAvatarCandidate_Трейверсал_404()
    {
        var setup = SetupChild(("math", "Математика"));
        var tutor = _personas.Get(setup.TutorOf["math"], setup.ChildId)!;
        // Файл лежит в папке персоны (на уровень выше candidates/): просочился бы traversal —
        // ручка отдала бы его.
        var dir = Path.Combine(_personas.AssetsDir, tutor.Id);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "x.png"), PngSignature);

        var resp = await _admin.GetAsync($"/api/users/{setup.ChildId}/child-tutors/{tutor.Id}/avatar/candidate/..%2Fx");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
