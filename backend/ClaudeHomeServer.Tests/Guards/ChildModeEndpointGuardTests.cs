using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Guards;

/// <summary>
/// Сторож гейта И1 детского режима (KM-B2, ADR-041): ребёнок может только то, что помечено
/// [ChildAllowed]. Перебирает ВСЕ эндпоинты EndpointDataSource хоста: детский токен на любом
/// эндпоинте без метки → 403 child_mode; список размеченных заморожен здесь.
/// Слепая зона та же, что у AnonymousEndpointsGuardTests: ветки конвейера без эндпоинта
/// (WebDAV, /preview) и YARP-модули — для них отдельные тесты ниже и проверка в самих ветках.
/// </summary>
[Trait("Category", "Host")]
public class ChildModeEndpointGuardTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    // Заморожено: «МЕТОДЫ шаблон» эндпоинтов с [ChildAllowed]. Новая метка — правка этого списка
    // осознанным решением (расширение детской поверхности).
    private static readonly string[] FrozenAllowed =
    [
        "GET api/auth/me",
        "POST api/auth/login",
        "POST api/tts",
        // Статус и присутствие ребёнка (KM-B5e, ADR-041 ручки №1–2)
        "POST api/child/heartbeat",
        "GET api/child/status",
        // Чат пространства «Уроки» (KM-B6a, ADR-041 ручки №3–5)
        "POST api/child/spaces/{space}/chat",
        "GET api/child/spaces/{space}/chats",
        "PUT api/child/chats/{id}/voice",
        // Вложения ребёнка (KM-B6e, ADR-041 ручка №8)
        "POST api/child/chats/{id}/attachments",
        // Онбординг правил (KM-B6b, ADR-041 ручки №6–7)
        "POST api/child/rules/start",
        "POST api/child/rules/agree",
        // Стоп-сигнал «Мне неприятно» (KM-B6d, ADR-041 ручка №9)
        "POST api/child/stop-signal",
        // Чтение своего детского чата (KM-B6f): обе формы истории — с курсором и без
        "GET api/chats/{id}",
        "GET api/chats/{id}/history",
        // Чтение картинки-вложения чата для миниатюры (KM-B7, ADR-041 ручка №12); у ребёнка — только свой ChildSpace-чат
        "GET api/chats/{id}/attachments/raw",
        // «Контекст» своего урока — только чтение (Б20c, ADR-041 ред. 8 решение 78): контекст и
        // сохранённые файлы; детская проверка «чат свой и детский» — в Scope контроллера. Записи
        // (PUT primary, POST/DELETE refs, сброс) метки не имеют намеренно: строку контекста
        // задают редакторы медиа, которых у ребёнка нет, — их добирает ChildToken_OnEveryEndpointWithoutChildAllowed_IsRefused.
        "GET api/chats/{sessionId}/context",
        "GET api/chats/{sessionId}/context/saved-files",
        // Проекция репетиторов (KM-T1e, разрез §4 №3–4): список плиток включённых предметов и
        // аватар репетитора; сама аватар-ручка дополнительно проверяет «это репетитор этого
        // ребёнка» — чужая персона даёт 404 уже внутри ручки
        "GET api/child/subjects",
        "GET api/personas/{id}/avatar",
        // «Заметки» ребёнка (Б20b, ADR-041 ред. 8, разрез §14.2): ровно CRUD notes/ его
        // проекта-предмета. Всё остальное вертикали (граф, бэклинки, перенос между источниками,
        // папки, аннотации, дневниковая, AI-ручки, вики) остаётся закрытым: внутри этих пяти
        // ребёнку отказывает источник и путь (403 child_source / child_path), а не гейт.
        "GET api/notes",
        "GET api/notes/{id}",
        "POST api/notes",
        "PUT api/notes/{id}",
        "DELETE api/notes/{id}",
        // Чтение «Знаний» своего проекта-предмета (Б20a, ADR-041 ред. 8 решение 81): статус/список
        // и поиск базы; детская проверка «проект — проект-предмета этого ребёнка» — в контроллере,
        // индексация/теги/удаление этого класса ребёнку не открыты (базу наполняет синк папки)
        "GET api/projects/{projectId}/knowledge",
        "GET api/projects/{projectId}/knowledge/search",
        // «Задачи» ребёнка (Б20d, ADR-041 ред. 8, разрез §14.4): доска своего проекта-предмета
        // (список с полем viewers), создание только себе, правка/закрытие/удаление только своих.
        // Детские проверки «ребёнок ли / свой ли предмет» — в общей точке ChildSubjectAccess,
        // правила задач — в ChildTaskScope (TasksController). Закрытые по
        // замыслу метки не имеют: запуск исполнителя, предгейт, ai-ручки, линия фичи, колонки
        // доски (PUT api/projects/{projectId}/board-columns) — их отсекает гейт И1 до тела.
        "GET api/projects/{projectId}/tasks",
        "POST api/projects/{projectId}/tasks",
        "PUT api/tasks/{taskId}",
        "DELETE api/tasks/{taskId}",
        // «Проекты» ребёнка (Б21, ADR-041 ред. 8, решение 84): «док проектов» ребёнка — штатный док
        // с фильтром «свои проекты-предметы»: ровно список и карточка, и только свои проекты-предметы
        // включённых предметов профиля (детская проверка — в контроллере; чужой/обычный проект — 404).
        // Прочие ручки класса (создание, правка, удаление, настройки, значок, фон, режим, builtin-prompt,
        // effective-prompt, events, team-memory) метки не имеют намеренно: проекты ребёнка заводит и
        // правит только взрослый — их отсекает гейт И1 до тела.
        "GET api/projects",
        "GET api/projects/{id}",
        // Список чатов и список сервисов (Х-W4-31, дефект d7c8477b): прямой доступ ребёнка к
        // взрослым page-level опросам закрыт всем, кроме своего проекта-предмета — метка +
        // «свой ли предмет» внутри ручки (тот же образец, что «Знания», решение 81). Чужой или
        // обычный проект — 403 not_child_subject (не child_mode: отказ решает проверка тела).
        // Прочие ручки классов (создание/правка/удаление чата, persona, context, icon-batch;
        // start/stop/active/external, launch.json) метки не имеют — их отсекает гейт И1 до тела.
        "GET api/projects/{projectId}/sessions",
        "GET api/projects/{projectId}/services",
        // Рельса «Файлы» проекта-предмета (Б11a, ADR-041 ред. 5–6, решения 53/54/56/65):
        // читающие и пишущие двери FilesController над проектом-предметом; каждая судит путь
        // ChildPathPolicy (для обычного проекта — обычные права). Закрытыми остаются diff,
        // revert, changed-by, save-from-url, locate, office-*, document-* — их нет в этом
        // списке, и ChildToken_OnEveryEndpointWithoutChildAllowed_IsRefused их добирает.
        // upload дописан Б11b (решения 56/89/90): загрузка ребёнка идёт через общий писатель
        // папки предмета (ChildTextCopyService), тип/размер/метр решаются в нём.
        "GET api/projects/{projectId}/files",
        "GET api/projects/{projectId}/files/tree",
        "GET api/projects/{projectId}/files/search",
        "GET api/projects/{projectId}/files/content",
        "PUT api/projects/{projectId}/files/content",
        "POST api/projects/{projectId}/files/create",
        "POST api/projects/{projectId}/files/mkdir",
        "POST api/projects/{projectId}/files/rename",
        "POST api/projects/{projectId}/files/upload",
        "DELETE api/projects/{projectId}/files",
        "POST api/projects/{projectId}/files/restore",
        "GET api/projects/{projectId}/files/stream",
        "GET api/projects/{projectId}/files/zip",
        // Health-пинг (хвост дельта-ревью R-KM-F10): probeHealth фронта шлёт GET /api/health
        // со своим токеном; ответ — пустой 204, данных нет
        "GET,HEAD api/health",
        "ANY /hubs/session",
        "ANY /hubs/session/negotiate",
        // SPA-фолбэк — интерфейс, а не управляющая поверхность
        "GET,HEAD {*path:nonfile}",
    ];

    private void SetChild(bool enabled = true, bool valid = true)
    {
        var user = factory.Services.GetRequiredService<UserStore>()
            .FindByUsername(TestWebApplicationFactory.SecondUsername)!;
        user.ChildMode = new ChildProfile
        {
            Enabled = enabled,
            TutorPersonaId = valid ? "tutor" : "",
            ProviderKey = "key",
            Model = "sonnet",
        };
    }

    private HttpClient ChildClient() =>
        factory.CreateAuthenticatedClient(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

    // Управляющая поверхность: REST и хабы. Остальное (статика, .well-known) — не гейт.
    private static bool IsManagedSurface(string raw) =>
        raw.StartsWith("api", StringComparison.OrdinalIgnoreCase)
        || raw.StartsWith("hubs/", StringComparison.OrdinalIgnoreCase);

    private static string Describe(RouteEndpoint e)
    {
        var methods = e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods is { Count: > 0 } m
            ? string.Join(",", m.Order(StringComparer.Ordinal)) : "ANY";
        return $"{methods} {e.RoutePattern.RawText}";
    }

    private IEnumerable<RouteEndpoint> Endpoints() =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();

    [Fact]
    public void ChildAllowedList_IsFrozen()
    {
        var actual = Endpoints()
            .Where(e => e.Metadata.GetMetadata<ChildAllowedAttribute>() is not null)
            .Select(Describe).Distinct().Order(StringComparer.Ordinal).ToArray();
        actual.Should().Equal(FrozenAllowed.Order(StringComparer.Ordinal));
    }

    // Ревью К4a (К3): WorkflowController.GetTimeline отдаёт ToolResult из транскрипта workflow-агента, а защита
    // у него одна — TranscriptRoots.IsPathAllowed. Ребёнку он закрыт тем, что у ручки нет [ChildAllowed]:
    // обе ручки (список агентов и таймлайн) отвечают детскому токену 403 до контроллера, а метки на них нет.
    [Fact]
    public async Task WorkflowAgents_ТаймлайнИСписок_РебёнкуЗакрыты_МеткиНет()
    {
        SetChild();
        var workflow = Endpoints()
            .Where(e => (e.RoutePattern.RawText ?? "").StartsWith("api/workflow-agents", StringComparison.OrdinalIgnoreCase))
            .ToList();
        workflow.Should().NotBeEmpty();
        workflow.Should().OnlyContain(e => e.Metadata.GetMetadata<ChildAllowedAttribute>() == null,
            "ручки транскриптов workflow-агентов ребёнку не открываются никогда");

        using var client = ChildClient();
        (await client.GetAsync("/api/workflow-agents/timeline?transcriptDir=x&agentId=y")).StatusCode
            .Should().Be(System.Net.HttpStatusCode.Forbidden);
        (await client.GetAsync("/api/workflow-agents?transcriptDir=x")).StatusCode
            .Should().Be(System.Net.HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ChildToken_OnEveryEndpointWithoutChildAllowed_IsRefused()
    {
        SetChild();
        using var client = ChildClient();
        // Проверяем только API и хабы: статика фронта (SPA-фолбэк) ребёнку отдавать можно —
        // это не управляющая поверхность, а файлы приложения.
        var denied = Endpoints().Where(e =>
                e.Metadata.GetMetadata<ChildAllowedAttribute>() is null
                && IsManagedSurface(e.RoutePattern.RawText ?? ""))
            .ToList();
        denied.Should().NotBeEmpty();

        // Счётчик в отчёт: сколько эндпоинтов обойдено и сколько из них без метки
        Console.WriteLine($"[child-gate] проверено эндпоинтов: {Endpoints().Count()}, без [ChildAllowed]: {denied.Count}");
        foreach (var e in denied.Where(e => (e.RoutePattern.RawText ?? "").StartsWith("api/modules", StringComparison.OrdinalIgnoreCase)))
            Console.WriteLine($"[child-gate] модуль: {Describe(e)}");

        var leaks = new List<string>();
        foreach (var e in denied)
        {
            var method = e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.FirstOrDefault() ?? "GET";
            var path = System.Text.RegularExpressions.Regex.Replace(e.RoutePattern.RawText ?? "/", @"\{[^}]*\}", "x");
            using var req = new HttpRequestMessage(new HttpMethod(method), path);
            var resp = await client.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            // Утечка — только успешный ответ. Отказ (4xx) может прийти из гейта
            // (403 child_mode), из своей схемы авторизации (401 — токен устройства)
            // или из правила роли (403 без тела) — всё это «ребёнку закрыто».
            var ok = (int)resp.StatusCode is >= 200 and < 400;
            if (ok) leaks.Add($"{Describe(e)} → {(int)resp.StatusCode} {body}");
        }
        leaks.Should().BeEmpty("ребёнку закрыто всё, что не помечено [ChildAllowed]");
    }

    [Fact]
    public async Task ChildToken_OnAllowedEndpoint_Passes()
    {
        SetChild();
        using var client = ChildClient();
        var me = await client.GetAsync("/api/auth/me");
        me.StatusCode.Should().Be(HttpStatusCode.OK);

        var tts = await client.PostAsync("/api/tts", new StringContent("{}", Encoding.UTF8, "application/json"));
        (await tts.Content.ReadAsStringAsync()).Should().NotContain("child_mode");
    }

    // Health-пинг ребёнка (хвост дельта-ревью R-KM-F10): probeHealth (offline.ts) шлёт GET /api/health
    // со своим токеном — без метки на действии гейт отвечал 403. Пинг отвечает 204 NoContent
    // (HealthController.Get), а не 200: важен сам факт ответа; тело обязано остаться пустым.
    // Мутация: сними [ChildAllowed] с действия HealthController.Get → этот тест краснеет (403).
    [Fact]
    public async Task ChildToken_OnHealthPing_PassesEmptyNoContent()
    {
        SetChild();
        using var client = ChildClient();
        var resp = await client.GetAsync("/api/health");
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await resp.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    // Ветки app.Use после гейта: SPA-фолбэк с [ChildAllowed] не должен пропускать ребёнка в них
    [Theory]
    [InlineData("GET", "/telemetry-proxy/x")]
    [InlineData("GET", "/9.4.0-hash/web-apps/x")]
    [InlineData("GET", "/modules/viaduct/")]
    [InlineData("GET", "/modules/viaduct/index.js")]
    [InlineData("POST", "/произвольный/путь")]
    [InlineData("POST", "/")]
    [InlineData("DELETE", "/some-page")]
    public async Task ChildToken_OnSpaFallbackBranchesOrWrites_Gets403ChildMode(string method, string path)
    {
        SetChild();
        using var client = ChildClient();
        var resp = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("child_mode");
    }

    [Fact]
    public async Task ChildToken_OnSpaRoot_Get_PassesGate()
    {
        SetChild();
        using var client = ChildClient();
        var resp = await client.GetAsync("/");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().NotContain("child_mode");
    }

    // Пример «обычной» ручки перенесён с /api/projects (Б21, ADR-041 ред. 8 решение 84: список и
    // карточка проектов получили [ChildAllowed]): builtin-prompt — читающая ручка того же класса
    // без метки, ей гейт отвечает 403 до тела.
    [Fact]
    public async Task ChildToken_OnPlainEndpoint_Gets403ChildMode()
    {
        SetChild();
        using var client = ChildClient();
        var resp = await client.GetAsync("/api/projects/builtin-prompt");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("child_mode");
    }

    [Fact]
    public async Task DenyAllProfile_Gets403_EvenOnAllowedEndpoint()
    {
        SetChild(valid: false);
        using var client = ChildClient();
        var me = await client.GetAsync("/api/auth/me");
        me.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task DisabledProfile_IsOrdinaryUser()
    {
        SetChild(enabled: false);
        using var client = ChildClient();
        (await client.GetAsync("/api/projects")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AdultToken_IsNotAffected()
    {
        SetChild();
        using var admin = factory.CreateAuthenticatedClient();
        (await admin.GetAsync("/api/projects")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task WebDav_ChildBasic_Gets403_AdultPasses()
    {
        SetChild();
        using var client = factory.CreateClient();
        HttpRequestMessage Propfind(string user, string pass)
        {
            var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), "/projects/");
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}")));
            return req;
        }

        var child = await client.SendAsync(Propfind(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword));
        child.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var adult = await client.SendAsync(Propfind(TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword));
        adult.StatusCode.Should().NotBe(HttpStatusCode.Forbidden).And.NotBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Preview_ChildCookieToken_Gets403()
    {
        SetChild();
        var token = factory.GetToken(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        using var client = factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/preview/some-project/");
        req.Headers.Add("Cookie", $"cc_preview={token}");
        req.Headers.Add("Sec-Fetch-Site", "same-origin");
        var resp = await client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("child_mode");
    }
}

/// <summary>Пустой владелец в гейте — fail-closed (замечание ревью R-KM-B1b, минор 1).</summary>
public class ChildModeGateEmptyOwnerTests
{
    private sealed class NullPolicy : IChildModePolicy
    {
        public ChildPolicySnapshot? For(string ownerId) => null;
    }

    private static async Task<(int Status, bool NextCalled)> Run(System.Security.Claims.ClaimsPrincipal user)
    {
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = user };
        ctx.Response.Body = new MemoryStream();
        var called = false;
        var gate = new ChildModeGateMiddleware(_ => { called = true; return Task.CompletedTask; });
        await gate.InvokeAsync(ctx, new NullPolicy());
        return (ctx.Response.StatusCode, called);
    }

    [Fact]
    public async Task AuthenticatedWithoutSub_Gets403()
    {
        var user = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim("name", "x")], "test"));
        (await Run(user)).Should().Be((403, false));
    }

    [Fact]
    public async Task Anonymous_Passes()
    {
        (await Run(new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity()))).Should().Be((200, true));
    }
}
