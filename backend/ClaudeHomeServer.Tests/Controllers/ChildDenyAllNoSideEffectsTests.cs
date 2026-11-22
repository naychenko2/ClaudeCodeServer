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
/// Г14-д1 (ADR-041 ред. 9): DenyAll — детский экран без побочных эффектов.
/// Ребёнок с нечитаемым профилем доходит до своей оболочки (личность, статус, свои
/// предметы-предметы, чтение, стоп-сигнал), но платные операции и запись закрыты гейтом
/// до тела ручки ([ChildRequiresProfile]): ни расхода, ни изменённого профиля/чата/файла/
/// заметки/задачи, и проекты не уходят во взрослую ветку GetAccessible.
///
/// Каждая проверка записи — полный цикл: сначала действие читаемым профилем (оно пишет —
/// иначе «не изменилось» было бы верно и без метки), потом тот же запрос DenyAll-ом, потом
/// состояние. Для voice/agree базовую половину несёт мутация: живой детский чат с предметом
/// в этой фикстуре не завести (И1-бис отказывает и просто чат ребёнка-владельца предмета),
/// а без метки эти ручки пишут сами — см. мутации ниже.
/// Мутации (каждая краснит ровно названное):
/// • снять [ChildRequiresProfile] с voice/agree/notes-записи/files-записи/tasks-записи —
///   соответствующий тест краснеет: запрос доходит до тела и пишет (VoiceMode/RulesAgreedAt/
///   файл/заметка/задача меняются);
/// • снять [ChildRequiresProfile] с TtsController.Synthesize — Tts_... краснеет: ответ —
///   контроллерный not_configured, не policy_unavailable;
/// • вернуть в ProjectsController.ChildScope null для DenyAll (прежняя форма) —
///   ProjectsList_... и ProjectCard_... краснеют: обычный проект возвращается по взрослой
///   ветке GetAccessible (список) и 200 по id (карточка).
/// </summary>
[Trait("Category", "Host")]
public class ChildDenyAllNoSideEffectsTests : IClassFixture<ChildDenyAllNoSideEffectsTests.DenyHost>
{
    public sealed class DenyHost : TestWebApplicationFactory { }

    private const string SubjectId = "russian";
    private const string KidPassword = "g14d-kid-pw-123456";

    private readonly DenyHost _host;
    public ChildDenyAllNoSideEffectsTests(DenyHost host) => _host = host;

    private UserStore Users => _host.Services.GetRequiredService<UserStore>();
    private ProjectManager Projects => _host.Services.GetRequiredService<ProjectManager>();
    private SessionManager Sessions => _host.Services.GetRequiredService<SessionManager>();

    // ── фикстура: пользователь, его проект-предмет и его обычный проект ──────────
    // Профиль не ставлен сразу: детская сверка при создании чата (И1-бис) отказывает и просто
    // чат ребёнка-владельца предмета — фикстура voice/agree заводит чат, пока пользователь ещё
    // не ребёнок, и только потом ставит профиль. Профиль — записью в стор (тот же способ, что
    // SetChild в ChildModeEndpointGuardTests); проект-предмет — по маркеру ChildSubjectId, как
    // его ставит провижер (сам провижер здесь не нужен); обычный проект того же владельца —
    // именно та примесь, которую DenyAll не должен вернуть через взрослую ветку.

    private (string Id, string Username, Project Subject, Project Ordinary) KidWithSubject(string tag)
    {
        var user = Users.Add($"g14d-kid-{tag}", KidPassword, "user");
        var subject = Projects.Create("Предмет-" + tag, Path.Combine(_host.TempDir, "g14d_" + tag),
            user.Id, user.Username, createDirectory: true, childSubjectId: SubjectId);
        var ordinary = Projects.Create("Обычный-" + tag, Path.Combine(_host.TempDir, "g14d_ord_" + tag),
            user.Id, user.Username, createDirectory: true);
        return (user.Id, user.Username, subject, ordinary);
    }

    private void ValidProfile(string childId) => Users.GetById(childId)!.ChildMode = new ChildProfile
    {
        Enabled = true, TutorPersonaId = "tutor", ProviderKey = "key", Model = "sonnet",
        Subjects = { new ChildSubject { Id = SubjectId, Label = "Русский язык", Glyph = "Аа", Color = "info" } },
    };

    // Нечитаемый профиль (тот же DenyAll, что SetChild(valid:false) в стороже): ребёнок
    // остаётся ребёнком, но ни одна его операционная граница не читается.
    private void DenyAllProfile(string childId) => Users.GetById(childId)!.ChildMode = new ChildProfile
    {
        Enabled = true, TutorPersonaId = "", ProviderKey = "key", Model = "sonnet",
        Subjects = { new ChildSubject { Id = SubjectId, Label = "Русский язык", Glyph = "Аа", Color = "info" } },
    };

    private HttpClient As(string username) => _host.CreateAuthenticatedClient(username, KidPassword);

    private static async Task<(HttpStatusCode, JsonElement)> Read(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        return (r.StatusCode, text.Length > 0 ? JsonSerializer.Deserialize<JsonElement>(text) : default);
    }

    private static string CodeOf(JsonElement b) => b.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";

    private static async Task<List<string>> IdsOf(HttpResponseMessage r)
    {
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        var doc = JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync());
        return doc.EnumerateArray().Select(e => e.GetProperty("id").GetString()!).ToList();
    }

    // ─── Major 1: проекты не уходят во взрослую ветку ─────────────────────────────

    [Fact]
    public async Task ProjectsList_DenyAll_OnlyOwnSubjectProjects_NotAdultBranch()
    {
        var (id, name, subject, ordinary) = KidWithSubject("plist");
        DenyAllProfile(id);

        var ids = await IdsOf(await As(name).GetAsync("/api/projects"));
        ids.Should().Contain(subject.Id, "свои предметы-предметы ребёнку видны (чтение открыто)");
        ids.Should().NotContain(ordinary.Id, "взрослый GetAccessible (собственные проекты) DenyAll закрыт");
    }

    [Fact]
    public async Task ProjectCard_DenyAll_OwnSubjectOk_OrdinaryNotFound()
    {
        var (id, name, subject, ordinary) = KidWithSubject("pcard");
        DenyAllProfile(id);

        (await As(name).GetAsync($"/api/projects/{subject.Id}")).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await As(name).GetAsync($"/api/projects/{ordinary.Id}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "существование обычного проекта ребёнку не раскрываем");
    }

    // ─── Major 2: платная операция и запись закрыты до тела ──────────────────────

    [Fact]
    public async Task Tts_DenyAll_RefusedBeforeTheBody_NoPaidCall()
    {
        var (id, name, _, _) = KidWithSubject("tts");
        DenyAllProfile(id);

        var (status, body) = await Read(await As(name).PostAsJsonAsync("/api/tts", new { text = "Текст для синтеза." }));
        status.Should().Be(HttpStatusCode.ServiceUnavailable);
        CodeOf(body).Should().Be("policy_unavailable");
        // Тело контроллера в ответе нет: в тестовом хосте синтез не настроен, и его собственный
        // отказ (not_configured) появился бы только дошедшему туда запросу — то есть платный
        // вызов (и расход) не состоялся.
        body.ToString().Should().NotContain("not_configured");
    }

    [Fact]
    public async Task VoiceSetting_DenyAll_NotWritten_ChatUnchanged()
    {
        // Чат заведён, пока пользователь ещё не ребёнок: детская сверка при создании чата
        // (И1-бис) отказывает и просто чат ребёнка-владельца предмета — она вне этой проверки.
        var (id, name, subject, _) = KidWithSubject("voice");
        var chat = await Sessions.CreateAsync(subject.Id, ClaudeMode.Auto, name: "Урок", createdByUserId: id);
        chat.ChildSpace = ChildSpaces.Lessons;
        chat.ChildSubject = SubjectId;
        Sessions.SaveSessions();
        DenyAllProfile(id);

        var (status, body) = await Read(await As(name).PutAsJsonAsync($"/api/child/chats/{chat.Id}/voice", new { enabled = true }));
        status.Should().Be(HttpStatusCode.ServiceUnavailable);
        CodeOf(body).Should().Be("policy_unavailable");
        Sessions.GetOwnedVisible(chat.Id, id)!.VoiceMode.Should().NotBe(true,
            "отказанный DenyAll-запрос не тронул настройку чата (мутация «снять метку» пишет true)");
    }

    [Fact]
    public async Task RulesAgree_DenyAll_ProfileNotWritten()
    {
        var (id, name, _, _) = KidWithSubject("agree");
        var chat = await Sessions.CreateChatAsync(id, ClaudeMode.Auto);
        chat.OnboardingKind = OnboardingKinds.ChildRules;
        chat.ChildSpace = ChildSpaces.Lessons;
        Sessions.SaveSessions();
        DenyAllProfile(id);

        var (status, body) = await Read(await As(name).PostAsJsonAsync("/api/child/rules/agree",
            new { sessionId = chat.Id, summary = "Договорились учиться по будням." }));
        status.Should().Be(HttpStatusCode.ServiceUnavailable);
        CodeOf(body).Should().Be("policy_unavailable");
        Users.GetById(id)!.ChildMode!.RulesAgreedAt.Should().BeNull(
            "согласие не записано в нечитаемый профиль (мутация «снять метку» ставит его)");
        Users.GetById(id)!.ChildMode!.RulesSummary.Should().BeNull();
    }

    [Fact]
    public async Task NotesWrite_DenyAll_Refused_NoteUnchanged()
    {
        var (id, name, subject, _) = KidWithSubject("notes");
        ValidProfile(id);
        var (status, created) = await Read(await As(name).PostAsJsonAsync("/api/notes",
            new { title = "Урок", content = "# Урок\n\nОригинал.\n", source = subject.Id }));
        status.Should().Be(HttpStatusCode.OK);
        var noteId = created.GetProperty("id").GetString()!;

        DenyAllProfile(id);
        (status, var deniedBody) = await Read(await As(name).PutAsJsonAsync($"/api/notes/{noteId}",
            new { content = "# Урок\n\nПравка DenyAll.\n" }));
        status.Should().Be(HttpStatusCode.ServiceUnavailable);
        CodeOf(deniedBody).Should().Be("policy_unavailable");

        // Чтение открыто (ред. 9): заметка на месте и не изменена.
        var (getStatus, note) = await Read(await As(name).GetAsync($"/api/notes/{noteId}"));
        getStatus.Should().Be(HttpStatusCode.OK);
        note.GetProperty("content").GetString().Should().Contain("Оригинал");
    }

    [Fact]
    public async Task FilesWrite_DenyAll_Refused_ContentUnchanged()
    {
        var (id, name, subject, _) = KidWithSubject("files");
        // Своё имя в корне предмета: ChildPathPolicy разрешает ребёнку запись здесь
        // («Материалы» и защищённые имена — вне этого случая).
        const string file = "задача.md";
        var disk = Path.Combine(subject.RootPath!, file);
        ValidProfile(id);

        var ok = await As(name).PutAsJsonAsync(
            $"/api/projects/{subject.Id}/files/content?path={Uri.EscapeDataString(file)}",
            new { content = "Первая запись.\n" });
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        File.ReadAllText(disk).Should().Be("Первая запись.\n");

        DenyAllProfile(id);
        var (status, body) = await Read(await As(name).PutAsJsonAsync(
            $"/api/projects/{subject.Id}/files/content?path={Uri.EscapeDataString(file)}",
            new { content = "Правка DenyAll.\n" }));
        status.Should().Be(HttpStatusCode.ServiceUnavailable);
        CodeOf(body).Should().Be("policy_unavailable");
        File.ReadAllText(disk).Should().Be("Первая запись.\n", "отказанный запрос не тронул файл");

        // Чтение открыто: GET того же пути отвечает содержимое.
        var (getStatus, doc) = await Read(await As(name).GetAsync(
            $"/api/projects/{subject.Id}/files/content?path={Uri.EscapeDataString(file)}"));
        getStatus.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TasksWrite_DenyAll_Refused_TaskUnchanged()
    {
        var (id, name, subject, _) = KidWithSubject("tasks");
        ValidProfile(id);
        // Детское создание — 201 (контракт Б20d, c1).
        var (status, created) = await Read(await As(name).PostAsJsonAsync(
            $"/api/projects/{subject.Id}/tasks", new { title = "Своя задача" }));
        status.Should().Be(HttpStatusCode.Created);
        var taskId = created.GetProperty("id").GetString()!;

        DenyAllProfile(id);
        (status, _) = await Read(await As(name).PutAsJsonAsync($"/api/tasks/{taskId}", new { title = "Правка DenyAll" }));
        status.Should().Be(HttpStatusCode.ServiceUnavailable);

        // Список открыт (чтение): задача на месте с прежним названием.
        var (listStatus, doc) = await Read(await As(name).GetAsync($"/api/projects/{subject.Id}/tasks"));
        listStatus.Should().Be(HttpStatusCode.OK);
        doc.GetProperty("tasks").EnumerateArray()
            .Single(t => t.GetProperty("id").GetString() == taskId)
            .GetProperty("title").GetString().Should().Be("Своя задача");
    }
}
