using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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
/// Сторож поверхности DenyAll (Г14-д1, ADR-041 ред. 9): второй замороженный подсписок —
/// [ChildRequiresProfile] (закрытые нечитаемому профилю платные операции и запись) и открытая
/// ему часть поверхности (FrozenDenyAllOpen = Allowed \ RequiresProfile). Новая метка без
/// правки этих списков краснит тест: класс эндпоинта при DenyAll — осознанное решение.
/// Правило пишущих методов (Г16, A-Г14-д1): из открытой DenyAll части каждый не-GET/HEAD метод
/// обязан нести [ChildRequiresProfile] или быть в FrozenDenyAllWriteExceptions с обоснованием —
/// семантику записи в FrozenDenyAllOpen сторож сам не выводит.
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

    // Заморожено (Г14-д1, ADR-041 ред. 9): [ChildRequiresProfile] — эндпоинты, закрытые ребёнку с
    // нечитаемым профилем (DenyAll) до тела: платные операции и запись вне «своего экрана».
    // Любая из них — [ChildAllowed]: ребёнок до них доходит, но не платит и не пишет.
    private static readonly string[] FrozenRequiresProfile =
    [
        "POST api/tts",
        // Голос чата и согласие правил — запись в чат/профиль (Г14-д1, Major 2)
        "PUT api/child/chats/{id}/voice",
        "POST api/child/rules/agree",
        // Запись заметок (чтение двух GET остаётся открытым)
        "POST api/notes",
        "PUT api/notes/{id}",
        "DELETE api/notes/{id}",
        // Запись рельсы «Файлы» (шесть читающих GET и stream/zip остаются открытыми)
        "PUT api/projects/{projectId}/files/content",
        "POST api/projects/{projectId}/files/create",
        "POST api/projects/{projectId}/files/mkdir",
        "POST api/projects/{projectId}/files/rename",
        "POST api/projects/{projectId}/files/upload",
        "DELETE api/projects/{projectId}/files",
        "POST api/projects/{projectId}/files/restore",
        // Задача: создание/правка/удаление (список GET остаётся открытым)
        "POST api/projects/{projectId}/tasks",
        "PUT api/tasks/{taskId}",
        "DELETE api/tasks/{taskId}",
    ];

    // Заморожено: то, что открыто DenyAll — оставшаяся часть [ChildAllowed]-поверхности:
    // bootstrap (вход, личность и снимок поверхности, health, SPA-корень, хаб), статус/присутствие,
    // предметы и аватары репетиторов, стоп-сигнал (явное исключение безопасности), чтение своих
    // предметов-предметов и чтение своих уроков. Контроллеры этих ручек отвечают DenyAll по-детски
    // сами (503 policy_unavailable / 403 not_child_subject / 404) — поведение не меняется.
    private static readonly string[] FrozenDenyAllOpen =
    [
        "GET api/auth/me",
        "POST api/auth/login",
        "POST api/child/heartbeat",
        "GET api/child/status",
        "POST api/child/spaces/{space}/chat",
        "GET api/child/spaces/{space}/chats",
        "POST api/child/chats/{id}/attachments",
        "POST api/child/rules/start",
        "POST api/child/stop-signal",
        "GET api/chats/{id}",
        "GET api/chats/{id}/history",
        "GET api/chats/{id}/attachments/raw",
        "GET api/chats/{sessionId}/context",
        "GET api/chats/{sessionId}/context/saved-files",
        "GET api/child/subjects",
        "GET api/personas/{id}/avatar",
        "GET api/notes",
        "GET api/notes/{id}",
        "GET api/projects/{projectId}/knowledge",
        "GET api/projects/{projectId}/knowledge/search",
        "GET api/projects/{projectId}/tasks",
        "GET api/projects",
        "GET api/projects/{id}",
        "GET api/projects/{projectId}/sessions",
        "GET api/projects/{projectId}/services",
        "GET api/projects/{projectId}/files",
        "GET api/projects/{projectId}/files/tree",
        "GET api/projects/{projectId}/files/search",
        "GET api/projects/{projectId}/files/content",
        "GET api/projects/{projectId}/files/stream",
        "GET api/projects/{projectId}/files/zip",
        "GET,HEAD api/health",
        "ANY /hubs/session",
        "ANY /hubs/session/negotiate",
        "GET,HEAD {*path:nonfile}",
    ];

    // Заморожено (Г16, ADR-041 ред. 9): пишущие ручки открытой DenyAll части без [ChildRequiresProfile].
    // Сторож DenyAllOpen_WritingEndpoints_HaveMarkOrAreNamedExceptions требует: всё открытое DenyAll —
    // чтение (GET/HEAD), либо [ChildRequiresProfile], либо эта запись с обоснованием. Пустой
    // IHttpMethodMetadata (хабы) — не «чтение»: транспорт не ограничен GET/HEAD, поэтому хаб —
    // тоже запись здесь. Новый пишущий [ChildAllowed] без метки и без записи краснит сторож —
    // семантику записи в FrozenDenyAllOpen он сам не выводит (ограничение из A-Г14-д1).
    private static readonly string[] FrozenDenyAllWriteExceptions =
    [
        // Bootstrap-вход: без него ребёнок не доходит до своего экрана (Г14, дефект 65aa2189).
        // [AllowAnonymous], пишет только собственный auth-токен — не детская операция.
        "POST api/auth/login",
        // Явное исключение безопасности (Г14-д1, ручка №9): «Мне неприятно» доступен и DenyAll —
        // сигнал не требует читаемого профиля; поведение держит DenyAllToken_OnStopSignal_StillAccepted.
        "POST api/child/stop-signal",
        // Остальные честно отказывают 503 policy_unavailable ДО побочного эффекта: каждая ручка
        // проверена своим тестом, который наблюдает состояние ДО и ПОСЛЕ запроса (usage-файл,
        // чаты ребёнка, папку предмета), а не только код ответа: DenyAllToken_OnHeartbeat_...,
        // OnChatCreate_..., OnAttachmentUpload_... (real chat), OnRulesStart_...; порядок в
        // контроллерах: ChildStatusController.cs:69 до RecordMinute :84; ChildChatsController.cs:242
        // до провижна и создания; ChildAttachmentsController.cs:60 до поиска чата и записи;
        // ChildRulesController.cs:62 до лока и создания chats-чата) — ни записи, ни расхода.
        "POST api/child/heartbeat",
        "POST api/child/spaces/{space}/chat",
        "POST api/child/chats/{id}/attachments",
        "POST api/child/rules/start",
        // Транспорт своего экрана (Г14): DenyAll-ребёнок доходит до подключения к session-хабу,
        // но каждый метод-вызов отказывает ChildHubFilter (ChildHubFilter.cs:40-42) до тела метода —
        // «подключиться может, сделать в хабе ничего нельзя»
        // (ChildHubFilterTests.DenyAllChild_CanConnect_ButEveryMethodIsChildMode).
        "ANY /hubs/session",
        "ANY /hubs/session/negotiate",
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

    // ── фикстура наблюдения эффекта (Г16-д1, ревью Р-Г16) ─────────────────────────
    // Сторож выше судит список и код ответа; ниже — что за отказом НИЧЕГО не записано.
    // Каждый тест берёт своего нового пользователя: чужая запись в usage-файле или чате
    // сделала бы «не изменилось» верным без отказа. Формат фикстуры — как KidWithSubject
    // в ChildDenyAllNoSideEffectsTests: пользователь и его проект-предмет по маркеру
    // ChildSubjectId (сам провижер здесь не нужен).

    private UserStore Users => factory.Services.GetRequiredService<UserStore>();
    private ProjectManager Projects => factory.Services.GetRequiredService<ProjectManager>();
    private SessionManager Sessions => factory.Services.GetRequiredService<SessionManager>();

    private const string SubjectId = "russian";
    private const string KidPassword = "g16d1-kid-pw-123456";

    private (string Id, string Username, Project Subject) KidWithSubject(string tag)
    {
        var user = Users.Add($"g16d1-kid-{tag}", KidPassword, "user");
        var subject = Projects.Create("Предмет-" + tag, Path.Combine(factory.TempDir, "g16d1_" + tag),
            user.Id, user.Username, createDirectory: true, childSubjectId: SubjectId);
        return (user.Id, user.Username, subject);
    }

    // Нечитаемый профиль (тот же DenyAll, что SetChild(valid:false) выше): ребёнок остаётся
    // ребёнком, но всё ему закрыто. Предмет и пространство «lessons» — чтобы мутация «перенести
    // проверку за побочный эффект» дошла до провижна/создания: в реальном коде DenyAll-проверка
    // стоит раньше этих сверок, и ответ от этих полей не зависит.
    private void DenyAllProfile(string childId) => Users.GetById(childId)!.ChildMode = new ChildProfile
    {
        Enabled = true, TutorPersonaId = "", ProviderKey = "key", Model = "sonnet",
        Subjects = { new ChildSubject { Id = SubjectId, Label = "Русский язык", Glyph = "Аа", Color = "info" } },
        Spaces = { ["lessons"] = new SpaceLimits() },
    };

    private HttpClient KidClient(string username) => factory.CreateAuthenticatedClient(username, KidPassword);

    private static async Task<(HttpStatusCode, string)> PostJson(HttpClient client, string path)
    {
        using var resp = await client.PostAsync(path, new StringContent("{}", Encoding.UTF8, "application/json"));
        return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    // Все чаты ребёнка: вне проектов + в его проектах (обе формы «Уроков»).
    private int KidChatCount(string childId) =>
        Sessions.GetProjectlessChats(childId).Count
        + Projects.GetByOwner(childId).Sum(p => Sessions.GetVisibleByProject(p.Id, childId).Count);

    // Содержимое папки предмета (относительный путь → размер): вложение чата пишется в «Вложения/».
    private static SortedDictionary<string, long> SubjectTree(string root) => new(
        Directory.Exists(root)
            ? Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .ToDictionary(f => Path.GetRelativePath(root, f), f => new FileInfo(f).Length)
            : new Dictionary<string, long>());

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

    // «Только чтение» для правила Г16: у эндпоинта есть HTTP-методы и все они GET/HEAD.
    // Пустой IHttpMethodMetadata (хабы) — не чтение: транспорт не ограничен GET/HEAD.
    private static bool IsReadingOnly(RouteEndpoint e) =>
        e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods is { Count: > 0 } m
        && m.All(x => x is "GET" or "HEAD");

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

    // Поверхность DenyAll заморожена двумя списками (Г14-д1): закрытое профилем и открытое.
    // Их объединение — ровно [ChildAllowed]-поверхность, пересечения нет: класс каждого
    // размеченного эндпоинта при DenyAll решён явно. Новый [ChildAllowed] без решения о нём
    // краснит и этот тест, и ChildAllowedList_IsFrozen.
    [Fact]
    public void DenyAllSurface_IsFrozen()
    {
        FrozenDenyAllOpen.Intersect(FrozenRequiresProfile).Should().BeEmpty();
        FrozenDenyAllOpen.Concat(FrozenRequiresProfile).Order(StringComparer.Ordinal)
            .Should().Equal(FrozenAllowed.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ChildRequiresProfileList_IsFrozen()
    {
        var actual = Endpoints()
            .Where(e => e.Metadata.GetMetadata<ChildRequiresProfileAttribute>() is not null)
            .Select(Describe).Distinct().Order(StringComparer.Ordinal).ToArray();
        actual.Should().Equal(FrozenRequiresProfile.Order(StringComparer.Ordinal));
    }

    // Default-deny для DenyAll (ADR-041 ред. 9): каждая помеченная [ChildRequiresProfile] ручка
    // получает детский отказ 503 policy_unavailable — того же контракта И3, что контроллерные
    // отказы DenyAll — до тела контроллера: ни записи, ни расхода. Мутация «снять метку с любой»
    // краснит этот тест: запрос доходит до тела и отвечает не-детским телом (not_configured,
    // 404, запись).
    [Fact]
    public async Task DenyAllToken_OnRequiredProfileEndpoint_Gets503PolicyUnavailable()
    {
        SetChild(valid: false); // профиль есть, но нечитаем → DenyAll
        using var client = ChildClient();
        var marked = Endpoints()
            .Where(e => e.Metadata.GetMetadata<ChildRequiresProfileAttribute>() is not null)
            .ToList();
        marked.Should().NotBeEmpty();

        foreach (var e in marked)
        {
            var method = e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.FirstOrDefault() ?? "GET";
            var path = System.Text.RegularExpressions.Regex.Replace(e.RoutePattern.RawText ?? "/", @"\{[^}]*\}", "x");
            using var req = new HttpRequestMessage(new HttpMethod(method), path);
            // Тело по типу ручки: POST/PUT — пустой JSON (проходит [FromBody]-валидации как
            // «поля пусты», а DenyAll-отказ всё равно до тела); files/upload — multipart: у неё
            // контент-констрейнт (IFormFile), и запрос с другим телом матчит не её, а
            // /api-фолбэк — проверено диагностикой: multipart матчит upload, JSON/пустое — нет.
            if (method is "POST" or "PUT")
                req.Content = (e.RoutePattern.RawText ?? "").EndsWith("files/upload")
                    ? new MultipartFormDataContent { { new StringContent("a"), "file" } }
                    : (HttpContent)new StringContent("{}", Encoding.UTF8, "application/json");
            var resp = await client.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
                $"{Describe(e)} закрыта DenyAll до тела: платная операция и запись вне своего экрана; тело: {body}");
            body.Should().Contain("policy_unavailable");
        }
    }

    // Стоп-сигнал — явное исключение безопасности (Г14-д1): остаётся доступен DenyAll-ребёнку.
    // Мутация «поставить [ChildRequiresProfile] на ChildStopSignalController.Raise» краснит тест.
    [Fact]
    public async Task DenyAllToken_OnStopSignal_StillAccepted()
    {
        SetChild(valid: false);
        using var client = ChildClient();
        var resp = await client.PostAsync("/api/child/stop-signal",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        ((int)resp.StatusCode).Should().BeLessThan(400);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().NotContain("policy_unavailable").And.NotContain("child_mode");
    }

    // Г16 (ограничение сторожа из A-Г14-д1): для DenyAll открытая часть поверхности — только
    // чтение (GET/HEAD), либо [ChildRequiresProfile], либо именованное исключение с обоснованием
    // (FrozenDenyAllWriteExceptions). Семантику записи в FrozenDenyAllOpen сторож сам не выводит —
    // поэтому пишущая ручка без метки обязана назвать себя здесь. Мутация: пометь [ChildAllowed]
    // пишущую взрослую ручку (например PUT api/chats/{id}/visibility) и внеси её в FrozenAllowed
    // и FrozenDenyAllOpen, не поставив [ChildRequiresProfile], — этот тест краснеет.
    [Fact]
    public void DenyAllOpen_WritingEndpoints_HaveMarkOrAreNamedExceptions()
    {
        var open = Endpoints()
            .Where(e => e.Metadata.GetMetadata<ChildAllowedAttribute>() is not null
                && e.Metadata.GetMetadata<ChildRequiresProfileAttribute>() is null)
            .ToList();
        open.Should().NotBeEmpty();

        foreach (var e in open)
            (IsReadingOnly(e) || FrozenDenyAllWriteExceptions.Contains(Describe(e), StringComparer.Ordinal))
                .Should().BeTrue(
                    $"{Describe(e)} открыт DenyAll: это чтение (GET/HEAD), [ChildRequiresProfile] или именованное исключение");

        // Список исключений минимален и живой: ровно те пишущие ручки открытой части, что есть сейчас.
        var actualWrites = open.Where(e => !IsReadingOnly(e)).Select(Describe).Distinct()
            .Order(StringComparer.Ordinal).ToArray();
        actualWrites.Should().Equal(FrozenDenyAllWriteExceptions.Order(StringComparer.Ordinal));
    }

    // Г16-д1 (ревью Р-Г16): обоснование «отказывает до побочного эффекта» — контракт, и каждая
    // ручка проверяется наблюдением состояния ДО и ПОСЛЕ запроса, а не только кодом ответа.
    // Мутация «перенести проверку DenyAll за побочный эффект» — измерено в копии дерева:
    // • heartbeat — тест КРАСНЫЙ: usage-файл появляется (проверка — единственный страж
    //   перед записью);
    // • chat — тест КРАСНЫЙ: ответ становится 400 unknown_subject (отказ ушёл за проверку);
    // • attachments «no-such» — тест КРАСНЫЙ: доходит до 404 chat_not_found;
    // • rules и attachments с живым чатом — тест зелёный, и это честно: сам эффект недоступен
    //   DenyAll-ребёнку и без проверки — создание детского чата отказывает детская сверка
    //   старта (И1-бис, ChildRouteRefusedException), а лимитная сверка (тот же 503) стоит
    //   перед записью. Наблюдение «чата нет / файла нет» держится в обоих случаях.
    // Каждая фикстура — новый ребёнок: «не изменилось» значимо только на пустом состоянии.

    [Fact]
    public async Task DenyAllToken_OnHeartbeat_Gets503AndUsageFileAbsent()
    {
        var (id, name, _) = KidWithSubject("hb");
        DenyAllProfile(id);
        // Журнал присутствия ребёнка — data/child-usage/{ownerId}.json (ChildUsageStore.DirName);
        // до первого принятого heartbeat файла нет.
        var usagePath = Path.Combine(factory.TempDir, ChildUsageStore.DirName, id + ".json");

        var (status, body) = await PostJson(KidClient(name), "/api/child/heartbeat");
        status.Should().Be(HttpStatusCode.ServiceUnavailable);
        body.Should().Contain("policy_unavailable");
        File.Exists(usagePath).Should().BeFalse(
            "отказ стоит до RecordMinute (ChildStatusController.cs:84): минута не засчитана; "
            + "мутация «перенести проверку за запись» — файл появляется, тест краснеет");
    }

    [Fact]
    public async Task DenyAllToken_OnChatCreate_Gets503AndNoChatCreated()
    {
        var (id, name, subject) = KidWithSubject("chat");
        DenyAllProfile(id);

        var (status, body) = await PostJson(KidClient(name), "/api/child/spaces/lessons/chat");
        status.Should().Be(HttpStatusCode.ServiceUnavailable);
        body.Should().Contain("policy_unavailable");
        // Тело 503 policy_unavailable — именно DenyAll-отказ (CheckSpace, ChildChatsController.cs:242),
        // а не отказ по состоянию: «нет такого предмета» и «предмет не готов» — другие тела (400/409).
        body.Should().NotContain("unknown_subject").And.NotContain("subject_not_ready");
        Sessions.GetVisibleByProject(subject.Id, id).Should().BeEmpty(
            "отказ стоит до провижна и создания: урока нет");
        KidChatCount(id).Should().Be(0,
            "мутация «снять DenyAll-ветку с CheckSpace» краснеет: запрос доходит до проверки "
            + "предмета и отвечает 400 unknown_subject — отказ был до неё");
    }

    [Fact]
    public async Task DenyAllToken_OnAttachmentUpload_Gets503AndSubjectFolderUnchanged()
    {
        // Чат заведён, пока пользователь ещё не ребёнок: детская сверка при создании чата
        // (И1-бис) отказывает и просто чат ребёнка-владельца предмета (тот же приём, что
        // фикстура voice в ChildDenyAllNoSideEffectsTests).
        var (id, name, subject) = KidWithSubject("att");
        var chat = await Sessions.CreateAsync(subject.Id, ClaudeMode.Auto, name: "Урок", createdByUserId: id);
        chat.ChildSpace = ChildSpaces.Lessons;
        chat.ChildSubject = SubjectId;
        Sessions.SaveSessions();
        DenyAllProfile(id);
        SubjectTree(subject.RootPath!).Should().BeEmpty("папка предмета пуста до запроса");

        using var client = KidClient(name);
        using var form = new MultipartFormDataContent { { new StringContent("Текст вложения."), "file", "заметка.txt" } };
        var resp = await client.PostAsync($"/api/child/chats/{chat.Id}/attachments", form);
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        body.Should().Contain("policy_unavailable");
        SubjectTree(subject.RootPath!).Should().BeEmpty(
            "отказ стоит до поиска чата и любой записи в папку предмета (ChildAttachmentsController.cs:60); "
            + "и без этой проверки запись невозможна: лимитная сверка того же 503 стоит до писателя "
            + "(измерено мутацией «перенести проверку за поиск» — файл не появляется)");
    }

    [Fact]
    public async Task DenyAllToken_OnRulesStart_Gets503AndNoChatCreated()
    {
        var (id, name, _) = KidWithSubject("rules");
        DenyAllProfile(id);

        var (status, body) = await PostJson(KidClient(name), "/api/child/rules/start");
        status.Should().Be(HttpStatusCode.ServiceUnavailable);
        body.Should().Contain("policy_unavailable");
        // Тело 503 — DenyAll-отказ (ChildRulesController.cs:62), а не «правила уже согласованы» (409).
        body.Should().NotContain("rules_already_agreed");
        KidChatCount(id).Should().Be(0,
            "отказ стоит до лока и создания chats-чата; и без этой проверки чата не будет: "
            + "создание детского чата отказывает детская сверка старта (И1-бис) — измерено "
            + "мутацией «перенести проверку за создание»");
        Sessions.GetProjectlessChats(id)
            .Should().NotContain(s => s.OnboardingKind == OnboardingKinds.ChildRules);
    }

    // Та же граница на загрузке вложения, до поиска чата: «no-such» здесь законен — до 404 не
    // доходит (ChildAttachmentsController.cs:60). Мутация «перенести проверку за поиск чата»
    // краснит этот тест: ответ становится 404 chat_not_found. Отсутствие записи в живой папке
    // предмета проверяет DenyAllToken_OnAttachmentUpload_Gets503AndSubjectFolderUnchanged.
    [Fact]
    public async Task DenyAllToken_OnAttachmentUpload_Gets503PolicyUnavailable()
    {
        SetChild(valid: false);
        using var client = ChildClient();
        using var form = new MultipartFormDataContent { { new StringContent("a"), "file" } };
        var resp = await client.PostAsync("/api/child/chats/no-such/attachments", form);
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, $"тело: {body}");
        body.Should().Contain("policy_unavailable");
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

    // Г14 (дефект 65aa2189): неполный профиль (DenyAll) — не повод уводить ребёнка во взрослую поверхность.
    // Раньше этот тест проверял обратное: 403 на /me даже на размеченном эндпоинте. Теперь:
    // /me — чтение личности — отвечает по-детски (isChild=true + замороженный снимок поверхности,
    // по которому фронт рисует детский экран, а не взрослый хаб — useChildSurface). Операционная
    // детская ручка даёт свой детский отказ контроллера (503 policy_unavailable), а не 403 child_mode.
    // Allow-list не изменился: непомеченная взрослая поверхность остаётся закрытой (403 child_mode).
    [Fact]
    public async Task DenyAllProfile_ReachesOwnChildSurface_NotAdult()
    {
        SetChild(valid: false); // профиль есть, но нечитаем (пустой TutorPersonaId) → DenyAll
        using var client = ChildClient();

        // Доходит до своего экрана: /me 200, isChild=true, снимок поверхности есть.
        var me = await client.GetAsync("/api/auth/me");
        me.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("isChild").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("childSurface").ValueKind.Should().NotBe(JsonValueKind.Null,
            "неполный профиль не лишает ребёнка поверхности: снимок заморожен и не зависит от профиля");

        // Операционная детская ручка — отказ контроллера по-детски (503), а не 403 child_mode: до тела доходит ребёнок.
        var subjects = await client.GetAsync("/api/child/subjects");
        subjects.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await subjects.Content.ReadAsStringAsync()).Should().Contain("policy_unavailable");

        // Allow-list прежний: непомеченная взрослая поверхность закрыта и DenyAll-ребёнку.
        var adult = await client.GetAsync("/api/projects/builtin-prompt");
        adult.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await adult.Content.ReadAsStringAsync()).Should().Contain("child_mode");
    }

    // Настроенный ребёнок (валидный профиль) ведёт себя как прежде: /me 200 с его пространствами.
    // Мутация «снять [ChildAllowed] с /me» краснит и этот тест, и ChildToken_OnAllowedEndpoint_Passes.
    [Fact]
    public async Task ValidProfile_MeUnchanged_IsChildWithOwnSpaces()
    {
        SetChild(); // валидный профиль: DenyAll=false
        using var client = ChildClient();
        var me = await client.GetAsync("/api/auth/me");
        me.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("isChild").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("childSurface").ValueKind.Should().NotBe(JsonValueKind.Null);
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
