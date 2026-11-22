using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Б4a: узкая ручка галки PUT /api/users/{id}/child-mode (ADR-041 ред. 4, решение 30) и
/// расширение GET child-profile (автор согласия, готовность опекунов к общим проектам).
/// Включение заводит профиль «не настроен» — без предметов, маршрутов и тьютора: политика
/// ставит DenyAll (операционные детские ходы/тулзы закрыты). Но до своего экрана ребёнок доходит:
/// HTTP-гейт отвечает только за allow-list, /me отдаёт isChild=true и снимок поверхности, а не 403
/// во взрослую поверхность (Г14).
/// Запись — через стор под его замком: гонка двух одновременных записей (галка ‖ запись
/// профиля с телом), 20 раундов; ветка 500 — подменой стора (ответ Софьи по c3).
/// Хост поднимает рубильник SharedProjects:Enabled=true и запись пула с ApiKey: полное тело
/// PUT child-profile (тесты «настройки сохраняются» и готовности опекунов) требует валидного
/// маршрута — та же схема, что у UsersChildProfileTests.
/// </summary>
[Trait("Category", "Host")]
public class UsersChildModeToggleTests :
    IClassFixture<UsersChildModeToggleTests.Hosts>,
    IClassFixture<UsersChildModeToggleTests.SaveFailsHosts>
{
    private const string KidsKey = "kids-api";
    private const string KidsApiKey = "sk-ant-test-kids";
    private const string UserPassword = "kids-test-password";

    // Хост на КЛАСС (Гейт-4·Г): конфигурация ставится один раз в фикстуре, не в конструкторе
    // теста на каждый тест.
    public sealed class Hosts : TestWebApplicationFactory
    {
        public Hosts()
        {
            ExtraConfig[$"ClaudeSubscriptions:{KidsKey}:ApiKey"] = KidsApiKey;
            // Рубильник общих проектов: поле sharedProjectsInstanceEnabled ответа обязано показывать
            // значение конфигурации, а не молчать false.
            ExtraConfig["SharedProjects:Enabled"] = "true";
            // Полное тело с предметом провится в проект-предмет: корень проектов container-владельца
            // — песочница (ProjectManager.EnsureRootAllowed), та же настройка, что у UsersChildProfileTests.
            ExtraConfig["Sandbox:ProjectsRoot"] =
                Path.Combine(Path.GetTempPath(), "ccs-kids-childmode-" + Guid.NewGuid().ToString("N"));
        }
    }

    // Свой хост для ветки 500: подмена UserStore на SaveFailsUserStore — DI-регистрация целого
    // стора, общий хост класса обязан держать настоящий стор (на нём работают все остальные
    // тесты и обе гонки), поэтому подмена живёт в отдельной фикстуре, а не в общем хосте.
    public sealed class SaveFailsHosts : TestWebApplicationFactory
    {
        public SaveFailsHosts() => ExtraServices = services =>
        {
            services.RemoveAll<UserStore>();
            services.AddSingleton<UserStore>(sp => new SaveFailsUserStore(
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<IHostEnvironment>(),
                sp.GetRequiredService<ILogger<UserStore>>(),
                sp.GetService<ClaudeHomeServer.Services.Llm.LlmProviderRegistry>(),
                sp.GetService<ClaudeHomeServer.WebDav.NtlmUserFile>()));
        };
    }

    private readonly TestWebApplicationFactory _factory;
    private readonly SaveFailsHosts _saveFailsHosts;
    private readonly HttpClient _admin;
    private readonly UserStore _users;
    private readonly IChildModePolicy _policy;

    public UsersChildModeToggleTests(Hosts hosts, SaveFailsHosts saveFailsHosts)
    {
        _factory = hosts;
        _saveFailsHosts = saveFailsHosts;
        _admin = _factory.CreateAuthenticatedClient();
        _users = _factory.Services.GetRequiredService<UserStore>();
        _policy = _factory.Services.GetRequiredService<IChildModePolicy>();
    }

    private string AdminId => _users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

    /// <summary>Новый пользователь через админский POST (по умолчанию — в песочнице).</summary>
    private async Task<string> NewUser(string name, string env = ExecutionEnvironments.Container)
    {
        var resp = await _admin.PostAsJsonAsync("/api/users", new
        {
            username = name,
            password = UserPassword,
            role = "user",
            executionEnvironment = env,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        return body.GetProperty("id").GetString()!;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> ChildModeAsync(string id, bool enabled, HttpClient? client = null)
    {
        var resp = await (client ?? _admin).PutAsJsonAsync($"/api/users/{id}/child-mode", new { enabled });
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, text.Length > 0 ? JsonSerializer.Deserialize<JsonElement>(text) : default);
    }

    private static string CodeOf(JsonElement body) =>
        body.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";

    private async Task<JsonElement> GetProfileAsync(string id)
    {
        var resp = await _admin.GetAsync($"/api/users/{id}/child-profile");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        return root.GetProperty("profile");
    }

    /// <summary>Полное валидное тело (та же форма, что у UsersChildProfileTests.ValidBody):
    /// запись пула с ApiKey + согласие на риск — без него не годен ни один маршрут (И2″).</summary>
    private static object FullBody(string[] guardianIds) => new
    {
        enabled = true,
        acknowledgeRouteRisk = true,
        guardianUserIds = guardianIds,
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
        subjects = new[] { new { id = "math", label = "Математика", glyph = "×÷", color = "info", enabled = true } },
    };

    private async Task<string> EnableFullChild(string name, string[]? guardians = null)
    {
        var id = await NewUser(name);
        var resp = await _admin.PutAsJsonAsync($"/api/users/{id}/child-profile", FullBody(guardians ?? []));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return id;
    }

    // --- c1: включение/отказы ручки ---

    [Fact]
    public async Task Enable_ContainerUser_CreatesNotConfiguredProfile_DenyAllButReachesOwnScreen()
    {
        var id = await NewUser("cm-notconfigured");
        var (status, body) = await ChildModeAsync(id, true);
        status.Should().Be(HttpStatusCode.OK);
        var profile = body.GetProperty("profile");
        profile.GetProperty("enabled").GetBoolean().Should().BeTrue();
        profile.GetProperty("subjects").GetArrayLength().Should().Be(0);
        profile.GetProperty("providerKey").GetString().Should().BeEmpty();
        profile.GetProperty("model").GetString().Should().BeEmpty();
        // Галка ничего не провит: у «не настроенного» профиля нет тьютора — он придёт первой
        // полной записью окна.
        body.GetProperty("tutorPersonaId").GetString().Should().BeEmpty();

        // Политика по-прежнему fail-closed: нечитаемый профиль есть DenyAll — операционные детские
        // ходы и тулзы закрыты (ChildModeGate). Но HTTP-гейт отвечает только за allow-list: ребёнок
        // доходит до своего /me и получает свой детский экран (isChild=true + снимок поверхности),
        // а не 403 во взрослую поверхность (Г14, дефект 65aa2189).
        var snapshot = _policy.For(id);
        snapshot.Should().NotBeNull();
        snapshot!.DenyAll.Should().BeTrue();
        using var child = _factory.CreateAuthenticatedClient("cm-notconfigured", UserPassword);
        var me = await child.GetAsync("/api/auth/me");
        me.StatusCode.Should().Be(HttpStatusCode.OK);
        var meBody = JsonSerializer.Deserialize<JsonElement>(await me.Content.ReadAsStringAsync());
        meBody.GetProperty("isChild").GetBoolean().Should().BeTrue();
        meBody.GetProperty("childSurface").ValueKind.Should().NotBe(JsonValueKind.Null,
            "неполный профиль не лишает ребёнка его поверхности");
    }

    [Fact]
    public async Task Enable_LocalUser_GetsNotContainer_ProfileNotCreated()
    {
        var id = await NewUser("cm-local", ExecutionEnvironments.Local);
        var (status, body) = await ChildModeAsync(id, true);
        status.Should().Be(HttpStatusCode.BadRequest);
        CodeOf(body).Should().Be("not_container");
        _users.GetById(id)!.ChildMode.Should().BeNull();
    }

    [Fact]
    public async Task Enable_ByNonAdmin_Gets403_ProfileNotCreated()
    {
        var id = await NewUser("cm-byother");
        using var other = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var (status, _) = await ChildModeAsync(id, true, other);
        status.Should().Be(HttpStatusCode.Forbidden);
        _users.GetById(id)!.ChildMode.Should().BeNull();
    }

    // Ребёнку ручка закрыта и он ничего в ней не меняет. На админ-only эндпоинте первым
    // отказывает правило роли (403 без тела — тот же случай, который описывает guard'овый
    // «отказ может прийти… из правила роли»); тело child_mode на этой ручке не гарантируется,
    // он проверяется в ChildModeEndpointGuardTests на непомеченных эндпоинтах общей поверхности.
    [Fact]
    public async Task Enable_ByChild_Gets403_ProfileUnchanged()
    {
        var id = await EnableFullChild("cm-selftoggle");
        (await ChildModeAsync(id, false)).Status.Should().Be(HttpStatusCode.OK);
        using var child = _factory.CreateAuthenticatedClient("cm-selftoggle", UserPassword);
        var (status, _) = await ChildModeAsync(id, true, child);
        status.Should().Be(HttpStatusCode.Forbidden);
        // Ребёнок свой режим не включил: выключенный профиль остался выключенным.
        _users.GetById(id)!.ChildMode!.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task Enable_UnknownUser_Gets404()
    {
        var (status, body) = await ChildModeAsync("no-such-user", true);
        status.Should().Be(HttpStatusCode.NotFound);
        CodeOf(body).Should().Be("user_not_found");
    }

    // --- выключение: как сейчас ---

    [Fact]
    public async Task Disable_ProfileStaysWithSettings_UserBecomesOrdinary()
    {
        var id = await EnableFullChild("cm-disable");
        var (status, body) = await ChildModeAsync(id, false);
        status.Should().Be(HttpStatusCode.OK);
        // Та же ветка, что у PUT child-profile {enabled:false}: профиль сохраняется выключенным.
        body.GetProperty("profile").GetProperty("enabled").GetBoolean().Should().BeFalse();
        var stored = _users.GetById(id)!.ChildMode!;
        stored.Enabled.Should().BeFalse();
        stored.Model.Should().Be("sonnet");
        // «Профиль выключен» политика читает как обычного пользователя (ChildModePolicy).
        _policy.For(id).Should().BeNull();
        using var former = _factory.CreateAuthenticatedClient("cm-disable", UserPassword);
        (await former.GetAsync("/api/projects")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Disable_NotAChild_IsNoOp200()
    {
        var id = await NewUser("cm-never");
        var (status, body) = await ChildModeAsync(id, false);
        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("profile").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // --- идемпотентность и повторное включение ---

    [Fact]
    public async Task Enable_AgainAfterFullConfig_DoesNotTouchProfile()
    {
        var id = await EnableFullChild("cm-idempotent");
        var (status, body) = await ChildModeAsync(id, true);
        status.Should().Be(HttpStatusCode.OK);
        var profile = body.GetProperty("profile");
        profile.GetProperty("providerKey").GetString().Should().Be(KidsKey);
        profile.GetProperty("model").GetString().Should().Be("sonnet");
        profile.GetProperty("subjects").GetArrayLength().Should().Be(1);
        profile.GetProperty("spaces").GetProperty("lessons").GetProperty("dailyMinutes").GetInt32().Should().Be(40);
        body.GetProperty("tutorPersonaId").GetString().Should().NotBeNullOrEmpty();
        // И в сторе — то же: повторная галка не записала ничего.
        var stored = _users.GetById(id)!.ChildMode!;
        stored.Subjects.Count.Should().Be(1);
        stored.WarnMinutes.Should().Be(5);
    }

    [Fact]
    public async Task DisableThenEnable_SettingsReturn_NotANewEmptyProfile()
    {
        var id = await EnableFullChild("cm-reenable");
        (await ChildModeAsync(id, false)).Status.Should().Be(HttpStatusCode.OK);

        var (status, body) = await ChildModeAsync(id, true);
        status.Should().Be(HttpStatusCode.OK);
        var profile = body.GetProperty("profile");
        profile.GetProperty("enabled").GetBoolean().Should().BeTrue();
        profile.GetProperty("providerKey").GetString().Should().Be(KidsKey);
        profile.GetProperty("subjects").GetArrayLength().Should().Be(1);
        var after = _users.GetById(id)!.ChildMode!;
        after.Model.Should().Be("sonnet");
        // Тьютор, согласие и предметы вернулись живыми: слияние стора берёт их из живого профиля,
        // галка их не пересоздаёт и не стирает.
        after.TutorPersonaId.Should().NotBeNullOrEmpty();
        after.RouteRiskAcknowledgedAt.Should().NotBeNull();
        after.Subjects.Count.Should().Be(1);
    }

    // --- c3: гонка двух одновременных PUT ---

    [Fact]
    public async Task Race_TwoConcurrentEnable_OneNotConfiguredProfile()
    {
        for (var round = 0; round < 20; round++)
        {
            var name = $"cm-race{round}";
            var id = await NewUser(name);
            var first = _admin.PutAsJsonAsync($"/api/users/{id}/child-mode", new { enabled = true });
            var second = _admin.PutAsJsonAsync($"/api/users/{id}/child-mode", new { enabled = true });
            (await first).StatusCode.Should().Be(HttpStatusCode.OK);
            (await second).StatusCode.Should().Be(HttpStatusCode.OK);

            var profile = _users.GetById(id)!.ChildMode;
            profile.Should().NotBeNull();
            profile!.Enabled.Should().BeTrue();
            profile.Subjects.Should().BeEmpty();
            profile.ProviderKey.Should().BeEmpty();
            profile.Model.Should().BeEmpty();
            // Ни одного лишнего «Помощника»: галка не провит — ни поодиночке, ни в гонке.
            profile.TutorPersonaId.Should().BeEmpty();
        }
    }

    // --- гонка «галка ‖ запись профиля с телом» (доработка ревью 67f4a0f5, п.1) ---

    // Почему на уровне стора: опасное окно — «чтение галки → её запись», единицы микросекунд.
    // На HTTP путь PUT child-profile до своего запися длиннее (вся цепочка запроса + валидатор,
    // UsersController.cs:321 перед записем :333), и пересечение его с окном галки — вопрос
    // удачи планировщика, а не теста: гарантировать его может только гонка самих двух записей.
    // Она здесь и есть: EnableChildModeIfAbsentOrDisabled ‖ SetChildProfile с полным телом,
    // Barrier(2), 20 раундов, без Task.Delay. Под замком итог одинаков в любом порядке: галка
    // первой — пустой профиль, запись мержит тело по живому; запись первой — галка видит живой
    // включённый и не пишет. Без замка в методе галки — её снимок затирает только что записанное
    // тело пустым профилем: тест краснеет (на копии мутации — красный раунд 8 из 20).
    [Fact]
    public async Task Race_EnableToggleAgainstFullProfileWrite_BodyNotLost()
    {
        for (var round = 0; round < 20; round++)
        {
            var id = _users.Add($"cm-racebody{round}", UserPassword, "user",
                ExecutionEnvironments.Container).Id;
            var barrier = new Barrier(2);
            var toggle = Task.Run(() => { barrier.SignalAndWait(); return _users.EnableChildModeIfAbsentOrDisabled(id); });
            var write = Task.Run(() => { barrier.SignalAndWait(); return _users.SetChildProfile(id, FullProfileBody(), resetRules: false); });
            (await toggle).Should().NotBeNull($"раунд {round}");
            (await write).Should().BeTrue($"раунд {round}");

            var profile = _users.GetById(id)!.ChildMode;
            profile.Should().NotBeNull($"раунд {round}");
            profile!.Enabled.Should().BeTrue($"раунд {round}");
            // Тело живое в любом порядке: галка его не затёрла, запись его не стёрла.
            profile.Model.Should().Be("sonnet", $"раунд {round}: галка затёрла запись профиля пустым профилем");
            profile.ProviderKey.Should().Be(KidsKey, $"раунд {round}");
            profile.Subjects.Should().ContainSingle().Which.Id.Should().Be("math", $"раунд {round}");
        }
    }

    // Тело для записи стора (то же содержимое, что у FullBody, без HTTP-формы): модель,
    // поставщик и предмет — ровно то, что не должно потеряться в гонке.
    private static ChildProfile FullProfileBody() => new()
    {
        Enabled = true,
        ProviderKey = KidsKey,
        Model = "sonnet",
        Subjects = [new ChildSubject { Id = "math", Label = "Математика", Glyph = "×÷", Color = "info", Enabled = true }],
    };

    // --- ветка 500 profile_not_saved (доработка ревью 67f4a0f5, п.3; ответ Софьи: подмена, не гонка) ---

    // Реальная 500 достижима только в окне «первое чтение контроллера → запись стора» (пользователя
    // удалили между ними): контроллер проверяет отсутствие пользователя ДО вызова метода
    // (UsersController.cs:262 — 404), поэтому «удалить до запроса» даёт 404, а не 500, а гонка это
    // окно не ловит (проба с 500 попытками). Поэтому подмена: боевой контроллер, реальное состояние
    // (пользователь с записанным полным профилем), но метод стора (virtual-шов) возвращает
    // «не сохранено». Проверяется ветка контроллера — 500 { profile_not_saved } — и инвариант:
    // неудачное включение не трогает тело профиля.
    [Fact]
    public async Task Enable_SaveFails_Gets500_ProfileBodyUnchanged()
    {
        // Хост подмены — фикстура класса SaveFailsHosts (Гейт-4·Г: хост на класс, не на тест).
        var host = _saveFailsHosts;
        var users = host.Services.GetRequiredService<UserStore>();
        var id = users.Add("cm-savefails", UserPassword, "user", ExecutionEnvironments.Container).Id;
        users.SetChildProfile(id, FullProfileBody(), resetRules: false).Should().BeTrue();

        using var admin = host.CreateAuthenticatedClient();
        var (status, body) = await ChildModeAsync(id, true, admin);
        status.Should().Be(HttpStatusCode.InternalServerError);
        CodeOf(body).Should().Be("profile_not_saved");
        // Тело — { code, message }, как в остальных отказах ручки.
        body.GetProperty("message").GetString().Should().NotBeNullOrEmpty();
        // Тело профиля не изменилось: неудачная попытка ничего не записала.
        var stored = users.GetById(id)!.ChildMode!;
        stored.Enabled.Should().BeTrue();
        stored.Model.Should().Be("sonnet");
        stored.ProviderKey.Should().Be(KidsKey);
        stored.Subjects.Should().ContainSingle().Which.Id.Should().Be("math");
    }

    // Подмена стора (ответ Софьи по c3): все методы и состояние реальные, но включение галки
    // отказывает — возвращает null («не сохранено»). Шов — virtual на EnableChildModeIfAbsentOrDisabled
    // (тот же приём, что у virtual-точек ChatHistoryService/ChildTextCopyService).
    private sealed class SaveFailsUserStore(
        IConfiguration config, IHostEnvironment env, ILogger<UserStore> logger,
        ClaudeHomeServer.Services.Llm.LlmProviderRegistry? providers,
        ClaudeHomeServer.WebDav.NtlmUserFile? ntlm)
        : UserStore(config, env, logger, providers, ntlm)
    {
        public override ChildProfile? EnableChildModeIfAbsentOrDisabled(string id) => null;
    }

    // --- c2: расширение GET child-profile ---

    [Fact]
    public async Task GetChildProfile_ReturnsConsentAuthor_AndPerGuardianSharedProjectsFlags()
    {
        var guardianOn = await NewUser("cm-guardian-on");
        var guardianOff = await NewUser("cm-guardian-off");
        // Флаг опекуна — per-user override в сторе (та же запись, что делает PUT feature-flags):
        // у одного включён, у другого нет.
        _users.GetById(guardianOn)!.FeatureFlags = new Dictionary<string, bool> { [FeatureFlagKeys.SharedProjects] = true };

        var id = await EnableFullChild("cm-flags", [guardianOn, guardianOff]);
        var profile = await GetProfileAsync(id);

        // Согласие профиля (решение 26): момент и автор — поля ответа (Б4a: окно показывает,
        // кто согласился; прежде автор оставался только в сторе).
        profile.GetProperty("routeRiskAcknowledgedAt").ValueKind.Should().NotBe(JsonValueKind.Null);
        profile.GetProperty("routeRiskAcknowledgedBy").GetString().Should().Be(AdminId);
        // Рубильник инстанса включён конфигом хоста — поле показывает это.
        profile.GetProperty("sharedProjectsInstanceEnabled").GetBoolean().Should().BeTrue();
        // По каждому опекуну — его флаг, в порядке guardianUserIds. Сервер флаг за человека
        // не включает: выключенный остаётся выключенным.
        var guardians = profile.GetProperty("guardians").EnumerateArray().ToList();
        guardians.Should().HaveCount(2);
        guardians[0].GetProperty("userId").GetString().Should().Be(guardianOn);
        guardians[0].GetProperty("sharedProjectsFlag").GetBoolean().Should().BeTrue();
        guardians[1].GetProperty("userId").GetString().Should().Be(guardianOff);
        guardians[1].GetProperty("sharedProjectsFlag").GetBoolean().Should().BeFalse();
    }
}
