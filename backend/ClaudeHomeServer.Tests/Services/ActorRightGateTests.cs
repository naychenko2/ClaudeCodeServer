using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// S2c-6: проверка АКТОРА в пишущих MCP-инструментах. Агент участника ходит с токеном арендатора,
/// поэтому право решается по актору хода (реестр, не заголовок клиента) через IProjectAccess.Resolve.
/// Таблица «инструмент → право» — default-deny. Решение: tasks_* агента редактора открыты (Q1,
/// временно, пишется от имени владельца), git-запись/индексация знаний/настройки — отказ.
///
/// Мутации (каждая — красный названный тест, откат — зелёный):
/// • ActorRightGate.Denied: вернуть null сразу после определения актора — «РедакторГитЗапись…» и
///   «ЧитательЛюбаяЗапись…» краснеют;
/// • убрать default-deny (строки нет → null) — «НеизвестныйИнструментУУчастника_Отказ» краснеет.
/// </summary>
public class ActorRightGateUnitTests
{
    private sealed class Actors : ITurnActorRegistry
    {
        public ConcurrentDictionary<string, string?> Map { get; } = new();
        public string? Actor(string sessionId, string ownerId) => Map.GetValueOrDefault(sessionId);
    }

    // Доступ поверх Core-примитива ProjectAccess без хоста: роль решает матрица, как в бою
    private sealed class PureAccess(Project project) : IProjectAccess
    {
        public RequestScope? Resolve(string? callerUserId, string? projectId, ProjectRight right = ProjectRight.Read)
        {
            if (projectId != project.Id || !ProjectAccess.Can(project, callerUserId, right)) return null;
            return new RequestScope(callerUserId!, project.OwnerId!, ProjectAccess.Check(project, callerUserId)!.Value);
        }

        public RequestScope? ResolveSession(string? callerUserId, string? sessionId, ProjectRight right = ProjectRight.Read) => null;
    }

    private const string Owner = "owner";
    private const string Editor = "editor";
    private const string Reader = "reader";

    private static (ActorRightGate Gate, Actors Actors, Project Project) Arrange()
    {
        var project = new Project
        {
            OwnerId = Owner,
            Members =
            [
                new ProjectMember { UserId = Editor, Role = ProjectRole.Editor },
                new ProjectMember { UserId = Reader, Role = ProjectRole.Reader },
            ],
        };
        var actors = new Actors();
        return (new ActorRightGate(actors, new PureAccess(project)), actors, project);
    }

    [Theory]
    [InlineData("wsp", "files_write")]
    [InlineData("wsp", "files_rename")]
    [InlineData("wsp", "files_delete")]
    [InlineData("notes", "notes_create")]
    [InlineData("notes", "notes_update")]
    [InlineData("notes", "notes_delete")]
    [InlineData("notes", "notes_move")]
    [InlineData("tasks", "tasks_create")]
    [InlineData("tasks", "tasks_update")]
    [InlineData("tasks", "tasks_complete")]
    [InlineData("tasks", "tasks_delete")]
    [InlineData("tasks", "tasks_add_subtask")]
    [InlineData("tasks", "tasks_toggle_subtask")]
    public void РедакторЗаписьКонтентаИЗадач_Проходит(string server, string tool)
    {
        var (gate, actors, project) = Arrange();
        actors.Map["chat"] = Editor;
        gate.Denied(server, tool, "chat", Owner, project.Id).Should().BeNull();
    }

    [Theory]
    [InlineData("wsp", "git_commit")]
    [InlineData("wsp", "git_stage")]
    [InlineData("wsp", "knowledge_index")]
    [InlineData("wsp", "projects_update")]
    [InlineData("wsp", "kb_add_document")]
    [InlineData("wsp", "deploy_start")]
    public void РедакторГитЗаписьЗнанияНастройки_Отказ(string server, string tool)
    {
        var (gate, actors, project) = Arrange();
        actors.Map["chat"] = Editor;
        gate.Denied(server, tool, "chat", Owner, project.Id).Should().Be(ActorRightGate.DeniedText);
    }

    [Theory]
    [InlineData("wsp", "files_write")]
    [InlineData("wsp", "git_commit")]
    [InlineData("notes", "notes_update")]
    [InlineData("notes", "notes_create")]
    [InlineData("tasks", "tasks_create")]
    [InlineData("tasks", "tasks_complete")]
    public void ЧитательЛюбаяЗапись_Отказ(string server, string tool)
    {
        var (gate, actors, project) = Arrange();
        actors.Map["chat"] = Reader;
        gate.Denied(server, tool, "chat", Owner, project.Id).Should().Be(ActorRightGate.DeniedText);
    }

    [Fact]
    public void ЧитательЧитает_Проходит()
    {
        var (gate, actors, project) = Arrange();
        actors.Map["chat"] = Reader;
        gate.Denied("notes", "notes_read", "chat", Owner, project.Id).Should().BeNull();
        gate.Denied("tasks", "tasks_list", "chat", Owner, project.Id).Should().BeNull();
        gate.Denied("wsp", "files_read", "chat", Owner, project.Id).Should().BeNull();
    }

    [Fact]
    public void ВладелецИНеизвестныйАктор_ПраваАрендатора_Проходит()
    {
        var (gate, actors, project) = Arrange();
        actors.Map["owner-chat"] = Owner;
        actors.Map["bg-chat"] = null;
        foreach (var chat in new[] { "owner-chat", "bg-chat", "no-such-chat" })
        {
            gate.Denied("wsp", "git_commit", chat, Owner, project.Id).Should().BeNull(chat);
            gate.Denied("wsp", "some_new_write_tool", chat, Owner, project.Id).Should().BeNull(chat);
        }
        gate.Denied("wsp", "git_commit", null, Owner, project.Id).Should().BeNull("вызова без чата-вызывателя нет актора");
    }

    // Мутация: убрать default-deny — тест краснеет
    [Fact]
    public void НеизвестныйИнструментУУчастника_Отказ()
    {
        var (gate, actors, project) = Arrange();
        actors.Map["chat"] = Editor;
        gate.Denied("wsp", "some_new_write_tool", "chat", Owner, project.Id).Should().Be(ActorRightGate.DeniedText);
        gate.Denied("notes", "notes_brand_new", "chat", Owner, project.Id).Should().Be(ActorRightGate.DeniedText);
        gate.Denied("tasks", "tasks_brand_new", "chat", Owner, project.Id).Should().Be(ActorRightGate.DeniedText);
    }

    // Отзыв участника во время хода агента: актор в реестре остался, право при следующем вызове — нет
    [Fact]
    public void ОтзывУчастникаВоВремяХода_СледующаяЗаписьОтказывает()
    {
        var (gate, actors, project) = Arrange();
        actors.Map["chat"] = Editor;
        gate.Denied("notes", "notes_update", "chat", Owner, project.Id).Should().BeNull();

        project.Members!.RemoveAll(m => m.UserId == Editor);

        gate.Denied("notes", "notes_update", "chat", Owner, project.Id).Should().Be(ActorRightGate.DeniedText);
    }

    // Новый инструмент без строки таблицы краснит тест: каталоги сверяются с таблицей в обе стороны
    [Fact]
    public void ТаблицаПравПокрываетВсеИнструментыТрёхТулсетов_БезЛишних()
    {
        var wsp = WorkspaceToolset.ToolSection.Keys;
        var tasks = TasksToolset.ProjectChatTools.Concat(TasksToolset.PersonalTools).Select(t => t.Name).Distinct();
        var notes = NotesToolset.CoreTools.Concat(NotesToolset.AnnotationTools).Select(t => t.Name);

        var live = wsp.Select(n => ActorToolRights.Key("wsp", n))
            .Concat(tasks.Select(n => ActorToolRights.Key("tasks", n)))
            .Concat(notes.Select(n => ActorToolRights.Key("notes", n)))
            .ToList();
        live.Count.Should().BeGreaterThan(40, "каталоги не должны быть пустыми — иначе сверка вакуумна");

        live.Except(ActorToolRights.All.Keys).Should().BeEmpty(
            "у каждого инструмента должна быть строка таблицы прав актора (default-deny отказал бы участнику)");
        ActorToolRights.All.Keys.Except(live).Should().BeEmpty("в таблице нет мёртвых строк");
    }
}

/// <summary>
/// S2c-6, проводка: реальные тулсеты через /mcp/{server}/{chat} с подменённым реестром актора.
/// Ход редактора: заметки/файлы/задачи проходят, git и настройки — отказ; ход читателя — любая
/// запись отказ; ход владельца и фон — как раньше.
///
/// Мутации: убрать вызов actorGate.Denied из одного тулсета — его тест краснеет
/// («Редактор…»/«Читатель…» по серверу).
/// </summary>
[Trait("Category", "Host")]
public class ActorRightMcpWiringTests : IDisposable
{
    private sealed class Actors : ITurnActorRegistry
    {
        public ConcurrentDictionary<string, string?> Map { get; } = new();
        public string? Actor(string sessionId, string ownerId) => Map.GetValueOrDefault(sessionId);
    }

    private readonly Actors _actors = new();
    private readonly TestWebApplicationFactory _factory = new();

    public ActorRightMcpWiringTests()
    {
        _factory.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
        _factory.ExtraServices = services => services.AddSingleton<ITurnActorRegistry>(_actors);
    }

    public void Dispose() => _factory.Dispose();

    private HttpClient Owner() => _factory.CreateAuthenticatedClient();
    private HttpClient As(string u, string p) => _factory.CreateAuthenticatedClient(u, p);
    private UserStore Users => _factory.Services.GetRequiredService<UserStore>();
    private string OwnerId => Users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
    private string MemberId => Users.FindByUsername(TestWebApplicationFactory.SecondUsername)!.Id;

    private static string IdOf(string json) => JsonSerializer.Deserialize<JsonElement>(json).GetProperty("id").GetString()!;

    private async Task<(string Text, bool IsError)> CallAsync(string server, string chat, string tool, object args)
    {
        var resp = await Owner().AsCaller(chat).PostAsJsonAsync($"/mcp/{server}/{chat}", new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call",
            @params = new { name = tool, arguments = args },
        });
        resp.EnsureSuccessStatusCode();
        var result = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync()).GetProperty("result");
        var isError = result.TryGetProperty("isError", out var e) && e.GetBoolean();
        return (result.GetProperty("content")[0].GetProperty("text").GetString()!, isError);
    }

    // Общий проект с участником роли role и вторым (запасным) участником, чтобы проект оставался общим
    // после отзыва первого; возвращает проект и чат владельца в нём
    private async Task<(string ProjectId, string Chat)> ArrangeAsync(string role)
    {
        var owner = Owner();
        (await owner.PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = true })).EnsureSuccessStatusCode();
        (await As(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword)
            .PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = true })).EnsureSuccessStatusCode();

        var dir = Path.Combine(_factory.TempDir, "ar_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var created = await owner.PostAsJsonAsync("/api/projects",
            new { name = "Права" + Guid.NewGuid().ToString("N")[..6], rootPath = dir, createDirectory = true });
        created.EnsureSuccessStatusCode();
        var projectId = IdOf(await created.Content.ReadAsStringAsync());
        (await owner.PostAsJsonAsync($"/api/projects/{projectId}/members",
            new { username = TestWebApplicationFactory.SecondUsername, role })).EnsureSuccessStatusCode();

        var session = await owner.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "acceptEdits" });
        session.EnsureSuccessStatusCode();
        return (projectId, IdOf(await session.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task Редактор_ЗаметкиФайлыЗадачиПроходят_ГитЗнанияНастройкиОтказ()
    {
        var (projectId, chat) = await ArrangeAsync("editor");
        _actors.Map[chat] = MemberId;

        var (n, nErr) = await CallAsync("notes", chat, "notes_create", new { title = "Заметка редактора", content = "x" });
        nErr.Should().BeFalse(n);
        var (nu, nuErr) = await CallAsync("notes", chat, "notes_update", new { id = IdOf(n), content = "y" });
        nuErr.Should().BeFalse(nu);
        var (f, fErr) = await CallAsync("wsp", chat, "files_write", new { projectId, path = "a.txt", content = "z" });
        fErr.Should().BeFalse(f);
        var (t, tErr) = await CallAsync("tasks", chat, "tasks_create", new { title = "Задача редактора" });
        tErr.Should().BeFalse(t);

        foreach (var (tool, args) in new (string, object)[]
        {
            ("git_commit", new { projectId, message = "m" }),
            ("git_stage", new { projectId, path = "a.txt" }),
            ("knowledge_index", new { projectId, path = "a.txt" }),
            ("projects_update", new { projectId, name = "Новое" }),
        })
        {
            var (text, isError) = await CallAsync("wsp", chat, tool, args);
            isError.Should().BeTrue($"{tool}: {text}");
        }
        // Текст ActorRightGate.DeniedText для git/знаний здесь не сверяем: у сессии секции git_write и
        // knowledge выключены и отказывают раньше гейта; таблицу этих инструментов держит юнит-набор.
    }

    [Fact]
    public async Task Читатель_ЛюбаяЗаписьОтказ_ЧтениеПроходит()
    {
        var (projectId, chat) = await ArrangeAsync("reader");
        _actors.Map[chat] = MemberId;

        foreach (var (server, tool, args) in new (string, string, object)[]
        {
            ("notes", "notes_create", new { title = "Читатель", content = "x" }),
            ("wsp", "files_write", new { projectId, path = "b.txt", content = "z" }),
            ("tasks", "tasks_create", new { title = "Задача читателя" }),
        })
        {
            var (text, isError) = await CallAsync(server, chat, tool, args);
            isError.Should().BeTrue($"{server}/{tool}: {text}");
            text.Should().Be(ActorRightGate.DeniedText, $"{server}/{tool}");
        }
        var (list, listErr) = await CallAsync("notes", chat, "notes_list", new { });
        listErr.Should().BeFalse(list);
    }

    [Fact]
    public async Task ВладелецИФон_КакРаньше_ГейтНеОтказывает()
    {
        var (projectId, chat) = await ArrangeAsync("editor");

        foreach (var actor in new string?[] { OwnerId, null })
        {
            _actors.Map[chat] = actor;
            var (n, nErr) = await CallAsync("notes", chat, "notes_create", new { title = "Владелец " + actor, content = "x" });
            nErr.Should().BeFalse(n);
            var (git, _) = await CallAsync("wsp", chat, "git_stage", new { projectId, path = "a.txt" });
            git.Should().NotBe(ActorRightGate.DeniedText, "актор неизвестен или арендатор — права арендатора");
        }
    }

    // Отзыв участника во время хода: запись до отзыва проходит, после — отказ (проверка идёт на каждый вызов)
    [Fact]
    public async Task ОтзывУчастникаВоВремяХода_СледующийВызовЗаписиОтказывает()
    {
        var (projectId, chat) = await ArrangeAsync("editor");
        _actors.Map[chat] = MemberId;
        var (first, firstErr) = await CallAsync("notes", chat, "notes_create", new { title = "До отзыва", content = "x" });
        firstErr.Should().BeFalse(first);

        var projects = _factory.Services.GetRequiredService<ProjectManager>();
        projects.SetMembers(projectId,
            [new ProjectMember { UserId = "запасной-участник", Role = ProjectRole.Editor }]);

        var (second, secondErr) = await CallAsync("notes", chat, "notes_create", new { title = "После отзыва", content = "x" });
        secondErr.Should().BeTrue(second);
        second.Should().Be(ActorRightGate.DeniedText);
    }
}
