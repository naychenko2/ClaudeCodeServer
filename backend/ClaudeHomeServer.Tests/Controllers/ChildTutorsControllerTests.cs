using System.IO;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
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
    /// <summary>Хост с подменой рассылки событий: барьерный broadcaster для теста гонки
    /// (PutTutor_Parallel_NoMixedFields). ExtraServices применяется после встроенных
    /// регистраций и перетирает их (TestWebApplicationFactory.cs:48, образец подмены —
    /// SpendControllerTests.cs:113). Прочие тесты класса событий не читают — no-op
    /// рассылка им не мешает.</summary>
    public sealed class Hosts : TestWebApplicationFactory
    {
        internal GateBroadcaster Barrier { get; } = new();

        public Hosts() => ExtraServices = s =>
            s.AddSingleton<ISessionBroadcaster>(Barrier);
    }

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

    // Голос в теле PUT применяется (хвост Т2c, найден при построении гонки): прежняя проверка
    // «updated.Value null» считала успех UpdateAsync отказом — его Ok() обёрнут в
    // ActionResult<Persona> (Value null, Result — исходный Ok, ActionResultOfT.cs в dotnet/aspnetcore)
    // — и SetVoiceAsync не выполнялся никогда.
    [Fact]
    public async Task PutTutor_ГолосПрименяется()
    {
        var setup = SetupChild(("math", "Математика"));
        var resp = await _admin.PutAsJsonAsync($"/api/users/{setup.ChildId}/child-tutors/{setup.TutorOf["math"]}",
            new { name = "Марья Ивановна", voice = new { voice = "masha", role = "friendly" } });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var persona = StoredTutor(setup);
        persona.Name.Should().Be("Марья Ивановна");
        persona.Voice?.Voice.Should().Be("masha");
        persona.Voice?.Role.Should().Be("friendly");
    }

    // ── Конкурентность и автоматика ─────────────────────────────────────────

    // Два PUT одной персоны — имя и голос (минор ревью Т2c). Голос — вторая мутация после
    // обновления (PersonasCrudService.SetVoiceAsync), и без замка пары «обновление + голос»
    // перемешиваются: имя от одного запроса, голос от другого. Гонка строится барьером на
    // событии «updated» — последней точке UpdateAsync после записи стора (PersonasCrudService.cs:406):
    // A удержан ровно между своей записью и своим голосом; барьер держит только первое событие,
    // поэтому начатый вслед B без замка ничем не держится и проходит свою пару целиком.
    // Без замка (мутация «снять замок»): B пишет имя B и голос B, пока A стоит на барьере, затем
    // A дописывает голос A — итог имя B + голос A, смесь. Краснеют обе проверки: bFirst (B прошёл,
    // хотя ждал бы замка) и целость записи. С замком (PutTutorGates по personaId): B ждёт
    // окончания A и проходит последним целиком — итог целая запись B.
    [Fact]
    public async Task PutTutor_Parallel_NoMixedFields()
    {
        var setup = SetupChild(("math", "Математика"));
        var tutorId = setup.TutorOf["math"];
        var url = $"/api/users/{setup.ChildId}/child-tutors/{tutorId}";
        var bodyA = new { name = "Имя А", character = "Характер А", greeting = "Привет А",
            voice = new { voice = "masha", role = "friendly" } };
        var bodyB = new { name = "Имя Б", character = "Характер Б", greeting = "Привет Б",
            voice = new { voice = "kirill", role = "strict" } };

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _hosts.Barrier.HoldNext(tutorId, release);
        var putA = _admin.PutAsJsonAsync(url, bodyA);
        (await Task.WhenAny(_hosts.Barrier.SeenTask, Task.Delay(10_000))).Should().Be(_hosts.Barrier.SeenTask,
            "запрос A не дошёл до рассылки «updated» — гонка не построена");

        var putB = _admin.PutAsJsonAsync(url, bodyB);
        // С замком B стоит на PutTutorGates, пока A держится на барьере, и за 500 мс не
        // проходит; без замка (мутация) B проходит свою пару целиком — assertion красный.
        // Отрицательную проверку («B не прошёл») нельзя заменить TCS: события, которого тут
        // ждут, нет — A своё уже испустил. 500 мс — нижний предел, при котором беззамковый
        // B гарантированно проходит (его ничем не держит); 3 с той же проверки — цена
        // прогона Host-класса без нужды.
        var bFirst = await Task.WhenAny(putB, Task.Delay(500));
        bFirst.Should().NotBe(putB,
            "пока A держится на барьере, B обязан ждать своей очереди на замке персоны");
        release.SetResult();
        _hosts.Barrier.Finish();
        await Task.WhenAll(putA, putB);

        (await putA).StatusCode.Should().Be(HttpStatusCode.OK);
        (await putB).StatusCode.Should().Be(HttpStatusCode.OK);

        var persona = StoredTutor(setup);
        persona.Contract!.Character.Should().BeOneOf("Характер А", "Характер Б");
        var wholeA = persona.Name == "Имя А" && persona.Contract.Character == "Характер А"
            && persona.Greeting == "Привет А" && persona.Voice?.Voice == "masha";
        var wholeB = persona.Name == "Имя Б" && persona.Contract.Character == "Характер Б"
            && persona.Greeting == "Привет Б" && persona.Voice?.Voice == "kirill";
        (wholeA || wholeB).Should().BeTrue(
            $"два одновременных PUT дают одну целую запись, не смесь полей: " +
            $"name={persona.Name}, voice={persona.Voice?.Voice ?? "нет"}");
    }

    // Замки не копятся (хвост ревью X4): после серии завершённых PUT разных персон в
    // статическом словаре не остаётся ни одной записи — её закрывает тот, кто отпустил
    // последнюю ссылку. Поле приватное static — наблюдается отражением, как приватные
    // состояния в других тестах (SessionModelStaticsTests.cs:37). Мутация «не удалять
    // запись» (UnrefTutorGate без Remove) красит именно этот тест.
    [Fact]
    public async Task PutTutorGates_ЗавершённыеPUT_СловарьЗамковПуст()
    {
        var setup = SetupChild(("math", "Математика"), ("phys", "Физика"));
        foreach (var tutorId in setup.TutorOf.Values)
        {
            var resp = await _admin.PutAsJsonAsync($"/api/users/{setup.ChildId}/child-tutors/{tutorId}",
                new { name = "Имя " + tutorId });
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var gates = typeof(ChildTutorsController)
            .GetField("PutTutorGates", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!;
        gates.GetType().GetProperty("Count")!.GetValue(gates).Should().Be(0,
            "запись замка живёт, пока её держат или ждут; после завершённых PUT — ни одной");
    }

    /// <summary>Барьер для теста гонки: первый ToOwner события «updated» указанной персоны
    /// помечает SeenTask и держится до release от теста, всё остальное проходит как no-op.
    /// Точка держания — внутри UpdateAsync на рассылке после записи стора
    /// (PersonasCrudService.cs:406): запрос, стоящий здесь, уже записал обновление, но до
    /// голоса ещё не дошёл. SeenTask нужен тесту, чтобы убедиться, что A дошёл до этой точки,
    /// прежде чем пускать B: без такого сигнала гонка строилась бы вслепую (первая версия
    /// барьера ждала гейт, который никто не заполнял, и проверяла несуществующее событие).
    /// Реализация ISessionBroadcaster — та же, что у TestSessionBroadcaster (тот же шов).</summary>
    internal sealed class GateBroadcaster : ISessionBroadcaster
    {
        private TaskCompletionSource _seen = NewGate();
        private TaskCompletionSource? _release;
        private string? _personaId;

        private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Держать ближайшую рассылку об этой персоне: SeenTask — что она дошла до шва,
        /// release — чем тест её отпускает.</summary>
        public void HoldNext(string personaId, TaskCompletionSource release)
        {
            _personaId = personaId;
            _release = release;
            _seen = NewGate();
        }

        public void Finish()
        {
            _personaId = null;
            _release = null;
        }

        public Task SeenTask => _seen.Task;

        public async Task ToOwner(string ownerId, ServerMessage message)
        {
            if (message is not PersonasChangedMessage { Action: "updated", PersonaId: var pid }) return;
            var release = _release;
            // Держим ровно первое событие: второе — того же запроса (голос) или соседнего
            // (B без замка) — проходит. Иначе барьер держал бы оба запроса, и после release
            // их голоса соревновались бы молча: итог — случайный, не смесь и не целая запись.
            if (release is null || pid != _personaId || _seen.Task.IsCompleted) return;
            _seen.TrySetResult();
            await release.Task;
        }

        public Task ToSession(string sessionId, ServerMessage message) => Task.CompletedTask;
        public Task ToSessionExcept(string sessionId, string exceptConnectionId, ServerMessage message) => Task.CompletedTask;
        public Task ToProject(string projectId, ServerMessage message) => Task.CompletedTask;
        public Task ToPreviewLog(string projectId, string serviceId, ServerMessage message) => Task.CompletedTask;
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

    // Путь браузерного <img>: заголовок он послать не умеет, JWT приезжает в ?access_token=
    // (OnMessageReceived — Program.cs:837-842). Допуск — тот же, что у заголовочного пути:
    // админ 200, ребёнок 403. Проба с анонимного клиента: без токена — 401, иначе тест
    // мерил бы совпадение с заголовочным путём.
    [Fact]
    public async Task GetTutorAvatar_ТокенТолькоВQuery_AccessToken_200и403()
    {
        var setup = SetupChild(("math", "Математика"));
        var tutorId = setup.TutorOf["math"];
        SeedAvatar(_personas.Get(tutorId, setup.ChildId)!, setup.ChildId);
        var url = $"/api/users/{setup.ChildId}/child-tutors/{tutorId}/avatar";

        var anon = _hosts.CreateClient();
        (await anon.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var asAdmin = await anon.GetAsync(url + "?access_token=" + Uri.EscapeDataString(
            _admin.DefaultRequestHeaders.Authorization!.Parameter!));
        asAdmin.StatusCode.Should().Be(HttpStatusCode.OK);
        asAdmin.Content.Headers.ContentType?.MediaType.Should().Be("image/png");

        var asChild = await anon.GetAsync(url + "?access_token=" + Uri.EscapeDataString(
            setup.Child.DefaultRequestHeaders.Authorization!.Parameter!));
        asChild.StatusCode.Should().Be(HttpStatusCode.Forbidden);
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
    // Измерено в копии мутации (18.11): %2F в сегменте пути не раскодировывается в '/',
    // значение {file} — литерал «..%2Fx» и не содержит разделителя: с одним литеральным
    // файлом candidates/..%2Fx ручка отдаёт 200, с одним файлом «x» в родителе — 404.
    // То есть запрос достает файлы только из candidates/, а мутация «без GetFileName»
    // не детектируема ни одним запросом к этой ручке (снят только факт «родителя не
    // отдаёт» — он и-guard'ится здесь). Защита имени — Path.GetFileName + SafePath.Join
    // (вторая линия); через HTTP она не проверяма, её ловит прямой вызов метода ниже.
    [Fact]
    public async Task GetTutorAvatarCandidate_Трейверсал_404()
    {
        var setup = SetupChild(("math", "Математика"));
        var tutor = _personas.Get(setup.TutorOf["math"], setup.ChildId)!;
        // Файл лежит в папке персоны (на уровень выше candidates/): ручка обязана его не
        // отдать.
        var dir = Path.Combine(_personas.AssetsDir, tutor.Id);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "x"), PngSignature);

        var resp = await _admin.GetAsync($"/api/users/{setup.ChildId}/child-tutors/{tutor.Id}/avatar/candidate/..%2Fx");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // Прямой вызов метода (мимо маршрутизатора) — единственная точка, где мутация
    // «без Path.GetFileName» краснеет: file="../x.png" при живом «x.png» в папке персоны.
    // Без GetFileName SafePath.Join разыменовывает «..» внутри AssetsDir (SafePath.cs:30
    // GetFullPath, отказа нет — путь остаётся под корнем) и файл существует → ручка
    // отдала бы PhysicalFileResult. С GetFileName имя — «x.png», в candidates/ его нет →
    // 404. Контроллер собран из боевых сервисов хоста через ActivatorUtilities
    // (тот же приём, что у ChildCliLayerFactoryTests.cs:172); допуск [Authorize]
    // прямым вызовом не проверяется —
    // он у HTTP-гардов выше, здесь проверяется только разрешение пути.
    [Fact]
    public void GetTutorAvatarCandidate_ПрямойВызов_ТрейверсалБезGetFileName_404()
    {
        var setup = SetupChild(("math", "Математика"));
        var tutor = _personas.Get(setup.TutorOf["math"], setup.ChildId)!;
        // Файл — в папке персоны, на уровень выше candidates/ (не в candidates/).
        var dir = Path.Combine(_personas.AssetsDir, tutor.Id);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "x.png"), PngSignature);

        var controller = ActivatorUtilities.CreateInstance<ChildTutorsController>(_hosts.Services);
        controller.GetTutorAvatarCandidate(setup.ChildId, tutor.Id, "../x.png")
            .Should().BeOfType<NotFoundResult>();
    }

    // ── POST child-tutors/{personaId}/avatar/select — форма отказа (минор ревью Т2c2) ──

    // Сервисный отказ {error} (PersonasCrudService.cs:802,:806) фронту не показывается:
    // карточка читает code/message (ChildTutorCard.tsx:70-74). Ручка переводит его в
    // {code,message}, как generate/upload: пустой файл — 400 avatar_file_missing, нет
    // файла кандидата на диске — 404 avatar_candidate_not_found. То, что code именно эти,
    // и есть проверка перевода: без него в теле остался бы сервисный {error} (прежняя
    // строка `return result.Result ?? new StatusCodeResult(500);`), а 400 от MVC-модели
    // дал бы ValidationProblemDetails без code — ни та, ни другая ветвь тест не проходят.
    [Fact]
    public async Task SelectTutorAvatar_ОтказФормаCodeMessage()
    {
        var setup = SetupChild(("math", "Математика"));
        var url = $"/api/users/{setup.ChildId}/child-tutors/{setup.TutorOf["math"]}/avatar/select";

        var noFile = await _admin.PostAsJsonAsync(url, new { file = "" });
        noFile.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var noFileBody = await JsonAsync(noFile);
        CodeOf(noFileBody).Should().Be("avatar_file_missing");
        noFileBody.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();

        var noCandidate = await _admin.PostAsJsonAsync(url, new { file = "cand-нет-такого.png" });
        noCandidate.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var noCandidateBody = await JsonAsync(noCandidate);
        CodeOf(noCandidateBody).Should().Be("avatar_candidate_not_found");
        noCandidateBody.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
    }
}
