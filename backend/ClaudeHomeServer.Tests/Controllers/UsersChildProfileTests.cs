using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// KM-B1e: админ-ручка PUT /api/users/{id}/child-profile (ADR-041 «Контракт», ручка №10).
/// В тестовом хосте — запись пула с ApiKey (И2: ребёнку положена отдельная запись с ключом,
/// не подписка): без неё проверка provider_not_api_key срабатывала бы и на валидном теле,
/// а «валидный → 200» был бы зелёным на пустоте.
/// </summary>
[Trait("Category", "Host")]
public class UsersChildProfileTests : IClassFixture<UsersChildProfileTests.Hosts>
{
    /// <summary>
    /// Стаб вещания живёт на фикстуре, а не на тест-классе: xunit строит тест-класс на каждый
    /// тест, а хост — один на класс; экземпляр первого теста уже стоит в DI, и правка
    /// «своего» экземпляра в следующем тесте до него не доходит.
    /// Стаб бросает только по указанному владельцу: стартовый провижн хоста и чужие тесты
    /// он не трогает; им тест вызывает отказ провижнера тьютора (путь 500 + откат профиля) —
    /// другой управляемой точки отказа провижера в хосте нет.
    /// </summary>
    public sealed class Hosts : TestWebApplicationFactory
    {
        // internal: тип поля — приватный вложенный класс тест-класса, public-поле было бы
        // менее доступным, чем его тип (CS0052)
        internal readonly ThrowingBroadcaster BroadcasterStub = new();

        public Hosts() =>
            ExtraServices = services => services.AddSingleton<ISessionBroadcaster>(BroadcasterStub);
    }

    private const string KidsKey = "kids-api";
    private const string KidsApiKey = "sk-ant-test-kids";

    private static readonly DateTime RulesT0 = new(2026, 11, 10, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime RulesT1 = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

    private readonly Hosts _hosts;
    private readonly HttpClient _admin;
    private readonly UserStore _users;
    private readonly IChildModePolicy _policy;

    public UsersChildProfileTests(Hosts hosts)
    {
        _hosts = hosts;
        _hosts.ExtraConfig[$"ClaudeSubscriptions:{KidsKey}:ApiKey"] = KidsApiKey;
        _admin = _hosts.CreateAuthenticatedClient();
        _users = _hosts.Services.GetRequiredService<UserStore>();
        _policy = _hosts.Services.GetRequiredService<IChildModePolicy>();
    }

    private string AdminId => _users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

    /// <summary>Новый пользователь с container-средой через админский POST (ребёнка заводим
    /// сразу в песочнице: среда после чатов не меняется).</summary>
    private async Task<string> NewContainerUser(string name)
    {
        var resp = await _admin.PostAsJsonAsync("/api/users", new
        {
            username = name,
            password = "kids-test-password",
            role = "user",
            executionEnvironment = ExecutionEnvironments.Container,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        return body.GetProperty("id").GetString()!;
    }

    /// <summary>Валидное тело включения: провайдер — запись пула с ApiKey, «Уроки» с окном
    /// и дневным лимитом, опекун — существующий пользователь.</summary>
    private object ValidBody(string guardianId = "") => new
    {
        enabled = true,
        guardianUserIds = guardianId.Length > 0 ? new[] { guardianId } : Array.Empty<string>(),
        providerKey = KidsKey,
        model = "sonnet",
        spaces = new Dictionary<string, object>
        {
            ["lessons"] = new
            {
                windows = new[] { new { from = "09:00", to = "20:00", weekdays = new[] { 1, 3, 5 } } },
                dailyMinutes = 40,
            },
        },
        dailyBudgetUsd = 2,
        monthlyBudgetUsd = 40,
        warnMinutes = 5,
    };

    private async Task<(HttpStatusCode Status, JsonElement Body)> PutAsync(string id, object body)
    {
        var resp = await _admin.PutAsJsonAsync($"/api/users/{id}/child-profile", body);
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, text.Length > 0 ? JsonSerializer.Deserialize<JsonElement>(text) : default);
    }

    private static string CodeOf(JsonElement body) =>
        body.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";

    /// <summary>Включить режим и вернуть id ребёнка (общая преамбула тестов правил/гонки).</summary>
    private async Task<string> EnableChild(string name)
    {
        var id = await NewContainerUser(name);
        var (status, body) = await PutAsync(id, ValidBody());
        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("tutorPersonaId").GetString()!.Should().NotBeNullOrEmpty();
        return id;
    }

    /// <summary>Прямая запись согласия правил в стор — как сделает их свой agree (KM-B6b):
    /// отдельной ручки согласия в этом шаге нет, а её запись обязана переживать PUT профиля.</summary>
    private void SeedRules(string childId, DateTime at, string summary)
    {
        var profile = _users.GetById(childId)!.ChildMode!;
        profile.RulesAgreedAt = at;
        profile.RulesSummary = summary;
    }

    // --- отказы таблицы ADR: по одному кейсу на каждый, с точным кодом ---

    [Fact]
    public async Task Put_НеАдмин_403()
    {
        var child = await NewContainerUser("kid-403");
        var client = _hosts.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var resp = await client.PutAsJsonAsync($"/api/users/{child}/child-profile", ValidBody());
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        // режим не включился: права на запись у не-админа не было
        _users.GetById(child)!.ChildMode.Should().BeNull();
    }

    [Fact]
    public async Task Put_НетПользователя_404_user_not_found()
    {
        var (status, body) = await PutAsync("no-such-user-id", ValidBody());
        status.Should().Be(HttpStatusCode.NotFound);
        CodeOf(body).Should().Be("user_not_found");
    }

    [Fact]
    public async Task Put_ЛокальнаяСреда_400_not_container()
    {
        // Обычный пользователь (local) — режим не включается: ход ребёнка живёт в песочнице,
        // а среда после появления чатов не меняется (guard Update).
        var resp = await _admin.PostAsJsonAsync("/api/users", new
        {
            username = "kid-local",
            password = "kids-test-password",
            role = "user",
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;

        var (status, body) = await PutAsync(id, ValidBody());
        status.Should().Be(HttpStatusCode.BadRequest);
        CodeOf(body).Should().Be("not_container");
        _users.GetById(id)!.ChildMode.Should().BeNull();
    }

    [Fact]
    public async Task Put_ПровайдерНеЗаписьСApiKey_400_provider_not_api_key()
    {
        // Ключ вне пула и ключ «claude» (его нет в тестовом конфиге) — не запись с ApiKey:
        // ребёнку положена отдельная запись пула с ключом (И2), тихий откат на подписку запрещён.
        var id = await NewContainerUser("kid-provider");
        var body = new { enabled = true, providerKey = "claude", model = "sonnet", spaces = new Dictionary<string, object>() };

        var (status, resp) = await PutAsync(id, body);
        status.Should().Be(HttpStatusCode.BadRequest);
        CodeOf(resp).Should().Be("provider_not_api_key");
        _users.GetById(id)!.ChildMode.Should().BeNull();
    }

    [Fact]
    public async Task Put_ОкноНечитаемое_400_invalid_window()
    {
        var id = await NewContainerUser("kid-window");
        var body = new
        {
            enabled = true,
            providerKey = KidsKey,
            model = "sonnet",
            spaces = new Dictionary<string, object>
            {
                ["lessons"] = new { windows = new[] { new { from = "25:00", to = "20:00" } } },
            },
        };

        var (status, resp) = await PutAsync(id, body);
        status.Should().Be(HttpStatusCode.BadRequest);
        CodeOf(resp).Should().Be("invalid_window");
        _users.GetById(id)!.ChildMode.Should().BeNull();
    }

    [Fact]
    public async Task Put_ДниВнеISO_400_invalid_window()
    {
        var id = await NewContainerUser("kid-days");
        var body = new
        {
            enabled = true,
            providerKey = KidsKey,
            model = "sonnet",
            spaces = new Dictionary<string, object>
            {
                ["lessons"] = new { windows = new[] { new { from = "09:00", to = "20:00", weekdays = new[] { 0, 8 } } } },
            },
        };

        var (status, resp) = await PutAsync(id, body);
        status.Should().Be(HttpStatusCode.BadRequest);
        CodeOf(resp).Should().Be("invalid_window");
    }

    [Fact]
    public async Task Put_ПространствоНеизвестное_400_invalid_space()
    {
        var id = await NewContainerUser("kid-space");
        var body = new
        {
            enabled = true,
            providerKey = KidsKey,
            model = "sonnet",
            spaces = new Dictionary<string, object>
            {
                ["homework"] = new { windows = Array.Empty<object>() },
            },
        };

        var (status, resp) = await PutAsync(id, body);
        status.Should().Be(HttpStatusCode.BadRequest);
        CodeOf(resp).Should().Be("invalid_space");
        _users.GetById(id)!.ChildMode.Should().BeNull();
    }

    [Fact]
    public async Task Put_ОпекунНеСуществует_400_guardian_not_found()
    {
        var id = await NewContainerUser("kid-guardian");
        var (status, resp) = await PutAsync(id, ValidBody("no-such-guardian"));
        status.Should().Be(HttpStatusCode.BadRequest);
        CodeOf(resp).Should().Be("guardian_not_found");
        _users.GetById(id)!.ChildMode.Should().BeNull();
    }

    // --- валидный путь ---

    [Fact]
    public async Task Put_Валидный_200_ТьюторПроведён_ПолитикаВидитРебёнка()
    {
        var guardian = AdminId;
        var id = await NewContainerUser("kid-valid");

        var (status, resp) = await PutAsync(id, ValidBody(guardian));
        status.Should().Be(HttpStatusCode.OK);

        var tutorId = resp.GetProperty("tutorPersonaId").GetString()!;
        tutorId.Should().NotBeNullOrEmpty();

        var profile = resp.GetProperty("profile");
        profile.GetProperty("providerKey").GetString().Should().Be(KidsKey);
        profile.GetProperty("model").GetString().Should().Be("sonnet");
        profile.GetProperty("warnMinutes").GetInt32().Should().Be(5);
        profile.GetProperty("spaces").GetProperty("lessons").GetProperty("windows")[0]
            .GetProperty("from").GetString().Should().Be("09:00");
        profile.GetProperty("spaces").GetProperty("lessons").GetProperty("dailyMinutes")
            .GetInt32().Should().Be(40);

        // Тьютор проведён и закреплён в профиле стора (не только в ответе),
        // и сам профиль проходит политику — ребёнок виден её потребителям.
        var stored = _users.GetById(id)!.ChildMode!;
        stored.Enabled.Should().BeTrue();
        stored.TutorPersonaId.Should().Be(tutorId);
        var snapshot = _policy.For(id);
        snapshot.Should().NotBeNull("профиль включён — политика обязана видеть ребёнка");
        snapshot!.DenyAll.Should().BeFalse("проведённый профиль проходит валидацию политики");
    }

    [Fact]
    public async Task Put_Выключение_РежимСнят_НастройкиСохранены()
    {
        var id = await EnableChild("kid-off");
        _policy.For(id).Should().NotBeNull();

        var (status, resp) = await PutAsync(id, new { enabled = false });
        status.Should().Be(HttpStatusCode.OK);
        resp.GetProperty("profile").GetProperty("enabled").GetBoolean().Should().BeFalse();

        // Тело {enabled:false} просит выключить режим, а не стереть лимиты: профиль целён,
        // Enabled=false; для политики это уже обычный пользователь.
        var stored = _users.GetById(id)!.ChildMode!;
        stored.Enabled.Should().BeFalse();
        stored.ProviderKey.Should().Be(KidsKey);
        stored.Model.Should().Be("sonnet");
        _policy.For(id).Should().BeNull("выключенный профиль — обычный пользователь");
    }

    [Fact]
    public async Task Put_БезSubjects_ЖивыеПредметыСохранены_ОтветНесётЖивогоТьютора()
    {
        var id = await EnableChild("kid-subjects-keep");
        var tutor = _users.GetById(id)!.ChildMode!.TutorPersonaId;
        // Засев предмета прямой записью в живой профиль (как делает миграция): предмет
        // несёт поля профиля и его тьютора.
        ChildTestSubjects.Seed(_hosts.Services, id);

        // PUT вкладки «Режим и время»: тела без subjects — правка лимитов не стирает
        // предметы (живое слияние Т2a: ToChildProfile даёт стору сигнал null, он сохраняет живой список).
        var (status, body) = await PutAsync(id, ValidBody());
        status.Should().Be(HttpStatusCode.OK);

        var stored = _users.GetById(id)!.ChildMode!;
        stored.Subjects.Should().ContainSingle("PUT без subjects не стирает предметы")
            .Which.Id.Should().Be(ChildTestSubjects.Key);
        stored.Subjects[0].TutorPersonaId.Should().Be(tutor, "живой тьютор предмета сохранён");

        // Ответ отдаёт предметы с живым тьютором (Т2a: tutorPersonaId — поле ответа, из тела не читалось).
        body.GetProperty("profile").GetProperty("subjects")[0].GetProperty("tutorPersonaId")
            .GetString().Should().Be(tutor);
    }

    // --- правила ребёнка: их не принимает тело, resetRules — единственный сброс ---

    [Fact]
    public async Task Put_БезResetRules_ПравилаНеТронуты()
    {
        var id = await EnableChild("kid-rules-keep");
        SeedRules(id, RulesT0, "Уроки по будням после уроков");

        var (status, _) = await PutAsync(id, ValidBody());
        status.Should().Be(HttpStatusCode.OK);

        var stored = _users.GetById(id)!.ChildMode!;
        stored.RulesAgreedAt.Should().Be(RulesT0, "правка лимитов не трогает согласие ребёнка");
        stored.RulesSummary.Should().Be("Уроки по будням после уроков");
    }

    [Fact]
    public async Task Put_ResetRules_ПравилаСброшены()
    {
        var id = await EnableChild("kid-rules-reset");
        SeedRules(id, RulesT0, "Старые правила");

        var body = new { enabled = true, providerKey = KidsKey, model = "sonnet", resetRules = true };
        var (status, _) = await PutAsync(id, body);
        status.Should().Be(HttpStatusCode.OK);

        var stored = _users.GetById(id)!.ChildMode!;
        stored.RulesAgreedAt.Should().BeNull("resetRules=true — админ просил сброс обоих полей");
        stored.RulesSummary.Should().BeNull();
    }

    // --- конкурентность ---

    [Fact]
    public async Task PutProfile_ConcurrentWithAgree_KeepsRulesAgreedAt()
    {
        var id = await EnableChild("kid-race-agree");
        SeedRules(id, RulesT0, "Согласие до правки");
        // Ссылка на профиль до правки: её смена в сторе — знак, что запись PUT состоялась.
        var initial = _users.GetById(id)!.ChildMode!;

        // Тело с длинным списком опекунов: проверка опекуна — обращение к стору на каждый id.
        // Между первым чтением запроса (auth-проверка админа) и его записью под локом —
        // десятки обращений к стору; атомарная правка читает живое значение в конце этого
        // пути, вариант «читать пользователя вне блокировки» — фиксирует его на первом чтении.
        var manyGuardians = Enumerable.Repeat(AdminId, 60).ToArray();
        var body = new
        {
            enabled = true,
            guardianUserIds = manyGuardians,
            providerKey = KidsKey,
            model = "sonnet",
            spaces = new Dictionary<string, object>(),
        };

        const long minute = TimeSpan.TicksPerMinute;
        var stop = false;
        var lastTicks = RulesT0.Ticks;
        var agree = Task.Run(() =>
        {
            // Плотная серия «согласий» ребёнка: прямые записи в живой профиль, пока профиль
            // не заменён. Значения монотонны и различимы (по минуте на запись).
            // Пишем только в initial: после замены стора GetById вернул бы новый профиль,
            // и последняя итерация серии записалась бы в него — любое значение выглядело
            // «свежим» и проверка теряла смысл.
            while (!Volatile.Read(ref stop) && ReferenceEquals(_users.GetById(id)!.ChildMode, initial))
            {
                lastTicks += minute;
                initial.RulesAgreedAt = new DateTime(Volatile.Read(ref lastTicks), DateTimeKind.Utc);
                initial.RulesSummary = "Согласие во время правки";
            }
        });

        // Барьер на локе стора: публичный RemapModels держит лок и вызывает Func на каждом
        // непустом слоте — ставим слот админу, чтобы Func гарантированно вызвался. Пока барьер
        // держит лок, запрос ещё не начался; с выходом из-под лока первый же доступ запроса
        // к стору (auth-чтение) конкурирует с серией за тот же лок. Слот после барьера не
        // меняется (Func возвращает null — «не менять»), файл не переписывается.
        _users.SetModelTiers(AdminId, "sonnet", null, null);
        _users.RemapModels(_ => null);

        // Marker — значение серии на момент пуска запроса; дальше по потоку только
        // прибавляется. Сравнение — с КОНЦОМ серии, а не с marker: атомарная запись берёт
        // живое значение, и оно отличается от последнего записанного на 1–2 итерации
        // (текущая итерация серии, начатая до замены). «Снимок первого чтения» отстаёт на
        // весь путь запроса от чтения до записи (60 опекунов — десятки обращений к стору
        // при живой серии): на замере — 187 минут против порога 60.
        var atStart = new DateTime(Volatile.Read(ref lastTicks), DateTimeKind.Utc);
        var (status, _) = await PutAsync(id, body);
        Volatile.Write(ref stop, true);
        await agree;

        status.Should().Be(HttpStatusCode.OK);
        var kept = _users.GetById(id)!.ChildMode!.RulesAgreedAt!.Value;
        var duringEdit = new DateTime(Volatile.Read(ref lastTicks), DateTimeKind.Utc);
        duringEdit.Should().BeOnOrAfter(atStart.AddMinutes(20), "плотная серия согласия во время правки была");
        // Живое значение на момент записи = последнее значение серии (±1–2 итерации).
        var lagMinutes = (duringEdit.Ticks - kept.Ticks) / minute;
        lagMinutes.Should().BeLessThan(60,
            "согласие ребёнка во время правки сохранено: RulesAgreedAt — живое значение стора на момент записи, не снимок первого чтения");
    }

    [Fact]
    public async Task Put_ДваОдновременно_ЦелыйПрофильНеСмесь()
    {
        var id = await NewContainerUser("kid-race-put");
        var bodyA = ValidBody();
        var bodyB = new
        {
            enabled = true,
            providerKey = KidsKey,
            model = "haiku",
            spaces = new Dictionary<string, object>
            {
                ["lessons"] = new { windows = Array.Empty<object>(), dailyMinutes = 10 },
            },
        };

        var putA = PutAsync(id, bodyA);
        var putB = PutAsync(id, bodyB);
        await Task.WhenAll(putA, putB);
        (await putA).Status.Should().Be(HttpStatusCode.OK);
        (await putB).Status.Should().Be(HttpStatusCode.OK);

        // Результат — один целый профиль (чей-то целиком), а не смесь: модель и дневный
        // лимит приходят из одной записи.
        var stored = _users.GetById(id)!.ChildMode!;
        var whole = (stored.Model == "sonnet" && stored.Spaces["lessons"].DailyMinutes == 40)
            || (stored.Model == "haiku" && stored.Spaces["lessons"].DailyMinutes == 10);
        whole.Should().BeTrue("одновременные PUT не перемешивают поля");
    }

    // только персоны-тьюторы (роль «Репетитор…»): дефолт-ассистент ребёнка не в счёт
    private IReadOnlyList<Persona> PersonasOf(string ownerId) =>
        _hosts.Services.GetRequiredService<PersonaManager>().GetAllInternal()
            .Where(p => p.OwnerId == ownerId && p.Role?.StartsWith("Репетитор") == true).ToList();

    [Fact]
    public async Task Put_Повторный_ТотЖеТьютор_БезВторогоПомощника()
    {
        var id = await EnableChild("kid-tutor-once");
        var first = _users.GetById(id)!.ChildMode!.TutorPersonaId;

        var (status, body) = await PutAsync(id, ValidBody());
        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("tutorPersonaId").GetString().Should().Be(first);

        var (status2, body2) = await PutAsync(id, new { enabled = true, providerKey = KidsKey, model = "sonnet", resetRules = true });
        status2.Should().Be(HttpStatusCode.OK);
        body2.GetProperty("tutorPersonaId").GetString().Should().Be(first, "resetRules тьютора не сбрасывает");
        PersonasOf(id).Should().ContainSingle("тьютор у ребёнка один");
    }

    [Fact]
    public async Task Put_ПровижерУпал_ResetRules_ОткатСохраняетПрежниеПравилаИТьютора()
    {
        var id = await EnableChild("kid-rollback-rules");
        SeedRules(id, RulesT0, "Прежние правила");
        var tutor = _users.GetById(id)!.ChildMode!.TutorPersonaId;
        // ключ тьютора «повис» (персона жива, ключа нет): Ensure создаст новую персону,
        // вещание упадёт — откат обязан удалить новую и вернуть профиль как был
        var live = _users.GetById(id)!.ChildMode!;
        live.TutorPersonaId = "";
        _hosts.BroadcasterStub.ThrowFor = id;
        try
        {
            var (status, _) = await PutAsync(id, new { enabled = true, providerKey = KidsKey, model = "sonnet", resetRules = true });
            status.Should().Be(HttpStatusCode.InternalServerError);
            var stored = _users.GetById(id)!.ChildMode!;
            stored.RulesAgreedAt.Should().Be(RulesT0, "откат целиком: правила прежние, а не сброшенные resetRules");
            stored.RulesSummary.Should().Be("Прежние правила");
            PersonasOf(id).Select(p => p.Id).Should().BeEquivalentTo(new[] { tutor }, "новая персона удалена, прежняя цела");
        }
        finally
        {
            _hosts.BroadcasterStub.ThrowFor = null;
        }
    }

    [Fact]
    public async Task Put_ПровижерУпал_500_ПрофильНеСохранён()
    {
        var id = await NewContainerUser("kid-provisioner-fail");
        // Отказ провижнера — вещание тьютора бросает после создания персоны:
        // 500 с {code, message}, профиль остаётся таким, каким был (его не было).
        _hosts.BroadcasterStub.ThrowFor = id;
        try
        {
            var (status, resp) = await PutAsync(id, ValidBody());
            status.Should().Be(HttpStatusCode.InternalServerError);
            CodeOf(resp).Should().NotBeNullOrEmpty();
            _users.GetById(id)!.ChildMode.Should().BeNull("отказ включению — профиль не сохранён");
            PersonasOf(id).Should().BeEmpty("персона, созданная упавшим вызовом, не остаётся сиротой");
        }
        finally
        {
            _hosts.BroadcasterStub.ThrowFor = null;
        }
    }

    // Пропускающий стаб вещания: бросает только по указанному владельцу — стартовый
    // провижн хоста и чужие тесты он не трогает. internal: его держит internal-поле
    // фикстуры (private-тип для него был бы менее доступен — CS0052).
    internal sealed class ThrowingBroadcaster : ISessionBroadcaster
    {
        public string? ThrowFor { get; set; }

        private void MaybeThrow(string ownerId)
        {
            if (ownerId == ThrowFor) throw new InvalidOperationException("вещание тьютора не удалось");
        }

        public Task ToSession(string sessionId, ServerMessage message) => Task.CompletedTask;
        public Task ToSessionExcept(string sessionId, string exceptConnectionId, ServerMessage message) => Task.CompletedTask;
        public Task ToOwner(string ownerId, ServerMessage message)
        {
            MaybeThrow(ownerId);
            return Task.CompletedTask;
        }
        public Task ToProject(string projectId, ServerMessage message) => Task.CompletedTask;
        public Task ToPreviewLog(string projectId, string serviceId, ServerMessage message) => Task.CompletedTask;
    }
}
