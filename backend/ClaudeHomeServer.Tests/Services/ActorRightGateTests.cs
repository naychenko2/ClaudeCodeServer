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
/// • убрать default-deny (строки нет → null) — «НеизвестныйИнструментУУчастника_Отказ» краснеет;
/// • вернуть прежнюю алиасу «IsNullOrEmpty(actor) → права арендатора» (до О2) —
///   «ОбщийПроект_НеизвестныйАктор_ВсёЧтоТребуетПрава_Отказ» краснеет на первом инструменте;
/// • убрать ветку рамки (отказывать неизвестному актору и в личном проекте) —
///   «ЛичныйПроект_НеизвестныйАктор_КакРаньше_ГейтНеОтказывает» краснеет.
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

    // Рамка хода — тот же вердикт по той же модели, что у SessionManager.GetTurnFrame:
    // общий проект (есть участники) — Narrow, личный или чат вне проекта — Wide.
    private sealed class PureFrames(Project project) : ITurnFrameSource
    {
        public TurnFrameKind Frame(string? sessionId, string ownerId) =>
            project.Members is { Count: > 0 } ? TurnFrameKind.Narrow : TurnFrameKind.Wide;
    }

    private const string Owner = "owner";
    private const string Editor = "editor";
    private const string Reader = "reader";

    // По умолчанию — общий проект владельца (с редактором и читателем).
    private static (ActorRightGate Gate, Actors Actors, Project Project) Arrange(Project? project = null)
    {
        project ??= new Project
        {
            OwnerId = Owner,
            Members =
            [
                new ProjectMember { UserId = Editor, Role = ProjectRole.Editor },
                new ProjectMember { UserId = Reader, Role = ProjectRole.Reader },
            ],
        };
        var actors = new Actors();
        return (new ActorRightGate(actors, new PureAccess(project), new PureFrames(project)), actors, project);
    }

    private static Project PersonalProject() => new() { OwnerId = Owner };

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

    [Theory]
    [InlineData("watch", "watch_start")]
    [InlineData("memory", "memory_remember")]
    [InlineData("memory", "team_memory_remember")]
    [InlineData("memory", "memory_to_note")]
    [InlineData("dify", "create_document_by_text")]
    [InlineData("architecture", "arch_create_element")]
    [InlineData("local-media", "local_generate_image")]
    [InlineData("dev", "build")]
    [InlineData("tests", "run_tests")]
    [InlineData("wsp", "chats_send")]
    [InlineData("personas", "personas_update")]
    [InlineData("personas", "personas_bindings_set")]
    [InlineData("personas", "personas_suggest_bindings")]
    // pmem_<handle> — алиас тулсета memory (ключ консультанта): тот же инструмент закрывает строка
    // OwnerOnly memory/memory_remember, а не default-deny (решение 12.11)
    [InlineData("pmem_timur", "memory_remember")]
    [InlineData("higgsfield", "generate_video")]
    public void ЛюбойУчастник_ПишущееИИсполняющееДругихСерверов_Отказ(string server, string tool)
    {
        var (gate, actors, project) = Arrange();
        foreach (var who in new[] { Editor, Reader })
        {
            actors.Map["chat"] = who;
            gate.Denied(server, tool, "chat", Owner, project.Id).Should().Be(ActorRightGate.DeniedText, $"{who}: {server}/{tool}");
        }
        actors.Map["chat"] = Owner;
        gate.Denied(server, tool, "chat", Owner, project.Id).Should().BeNull("арендатор");
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

    // Названный арендатор — разрешающее короткое замыкание гейта: его собственные вызовы не
    // читаются через таблицу (иtools вне таблицы ему открыты).
    [Fact]
    public void НазванныйВладелец_ПраваАрендатора_Проходит()
    {
        var (gate, actors, project) = Arrange();
        actors.Map["owner-chat"] = Owner;
        foreach (var (server, tool) in new (string, string)[]
        {
            ("wsp", "git_commit"), ("wsp", "some_new_write_tool"), ("watch", "watch_start"),
            ("memory", "memory_remember"), ("tasks", "tasks_complete"),
        })
            gate.Denied(server, tool, "owner-chat", Owner, project.Id).Should().BeNull($"{server}/{tool}");

        gate.Denied("wsp", "git_commit", null, Owner, project.Id).Should().BeNull("вызова без чата-вызывателя нет актора");
    }

    // О2 финального ревью «Общих проектов» (892deb00): неизвестный актор общего проекта
    // резолвится как «самое узкое» (контракт ITurnActorRegistry) — отказано всё, что требует
    // права, и всё, чего нет в таблице. Мутация: вернуть «IsNullOrEmpty(actor) → null» строкой
    // до О2 — этот тест краснеет на первом же инструменте.
    [Theory]
    [InlineData("wsp", "git_commit")]
    [InlineData("wsp", "chats_send")]
    [InlineData("wsp", "deploy_start")]
    [InlineData("watch", "watch_start")]
    [InlineData("memory", "memory_remember")]
    [InlineData("image-editor", "image_generate")]
    [InlineData("tasks", "tasks_complete")]
    [InlineData("notes", "notes_create")]
    [InlineData("wsp", "some_new_write_tool")]
    public void ОбщийПроект_НеизвестныйАктор_ВсёЧтоТребуетПрава_Отказ(string server, string tool)
    {
        var (gate, actors, project) = Arrange();
        // и чат с обнулённым реестром (после рестарта, дренаж), и чужой/удалённый чат —
        // рамка у обоих Narrow (fail-closed, как GetTurnFrame)
        foreach (var chat in new[] { "bg-chat", "no-such-chat" })
        {
            actors.Map[chat] = null;
            gate.Denied(server, tool, chat, Owner, project.Id)
                .Should().Be(ActorRightGate.DeniedText, $"{chat}: {server}/{tool}");
        }
    }

    // Открыто только то, что права не требует: его держат рамка хода и доступ участника,
    // а не догадка гейта о ранге безымянного актора.
    [Theory]
    [InlineData("notes", "notes_read")]
    [InlineData("tasks", "tasks_list")]
    [InlineData("wsp", "files_read")]
    [InlineData("watch", "watch_list")]
    public void ОбщийПроект_НеизвестныйАктор_Чтение_Проходит(string server, string tool)
    {
        var (gate, actors, project) = Arrange();
        actors.Map["bg-chat"] = null;
        gate.Denied(server, tool, "bg-chat", Owner, project.Id).Should().BeNull($"{server}/{tool}");
    }

    // Критерий 2: в личном (необщем) проекте разведение TurnFrame — поведение не изменилось.
    [Theory]
    [InlineData("wsp", "git_commit")]
    [InlineData("watch", "watch_start")]
    [InlineData("memory", "memory_remember")]
    [InlineData("tasks", "tasks_complete")]
    [InlineData("wsp", "some_new_write_tool")]
    public void ЛичныйПроект_НеизвестныйАктор_КакРаньше_ГейтНеОтказывает(string server, string tool)
    {
        var (gate, actors, project) = Arrange(PersonalProject());
        actors.Map["bg-chat"] = null;
        gate.Denied(server, tool, "bg-chat", Owner, project.Id).Should().BeNull($"{server}/{tool}");
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
        // WikiTools (master, ADR-035/036) отдаются тулсетом всегда, рядом с ядром
        var notes = NotesToolset.CoreTools.Concat(NotesToolset.WikiTools).Concat(NotesToolset.AnnotationTools).Select(t => t.Name);

        var live = wsp.Select(n => ActorToolRights.Key("wsp", n))
            .Concat(tasks.Select(n => ActorToolRights.Key("tasks", n)))
            .Concat(notes.Select(n => ActorToolRights.Key("notes", n)))
            .ToList();
        live.Count.Should().BeGreaterThan(40, "каталоги не должны быть пустыми — иначе сверка вакуумна");

        live.Except(ActorToolRights.All.Keys).Should().BeEmpty(
            "у каждого инструмента должна быть строка таблицы прав актора (default-deny отказал бы участнику)");
        ActorToolRights.All.Keys.Where(k => k.StartsWith("wsp/") || k.StartsWith("tasks/") || k.StartsWith("notes/"))
            .Except(live).Should().BeEmpty("в таблице нет мёртвых строк");
    }
}

/// <summary>
/// S2c-6, проводка: реальные тулсеты через /mcp/{server}/{chat} с подменённым реестром актора.
/// Ход редактора: заметки/файлы/задачи проходят, git и настройки — отказ; ход читателя — любая
/// запись отказ; ход названного владельца — как раньше. Ход без инициатора в общем проекте
/// (О2 финального ревью): отказано всё, что требует права, включая запись контента; чтение
/// открыто. В личном проекте тот же безымянный фон — как раньше (разведение TurnFrame).
///
/// Мутации: убрать вызов actorGate.Denied из одного тулсета — его тест краснеет
/// («Редактор…»/«Читатель…» по серверу); вернуть алиасу «unknown → права арендатора» —
/// краснеют «Фон_ВОбщемПроект…».
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
    public async Task НазванныйВладелец_КакРаньше_ГейтНеОтказывает()
    {
        var (projectId, chat) = await ArrangeAsync("editor");
        _actors.Map[chat] = OwnerId;

        var (n, nErr) = await CallAsync("notes", chat, "notes_create", new { title = "Владелец", content = "x" });
        nErr.Should().BeFalse(n);
        var (git, _) = await CallAsync("wsp", chat, "git_stage", new { projectId, path = "a.txt" });
        git.Should().NotBe(ActorRightGate.DeniedText, "названный арендатор — права арендатора");
    }

    // О2 финального ревью: ход без инициатора в общем проекте (реестр молчит) не получает
    // прав арендатора — отказано всё, что требует права. Мутация: алиаса «unknown → права
    // арендатора» — этот тест и «Фон…ЗаписьКонтентаТожеОтказана» краснеют.
    [Theory]
    [MemberData(nameof(ЗакрытыеУчастнику))]
    public async Task Фон_ВОбщемПроект_ВсёЧтоТребуетПрава_Отказ(string server, string tool)
    {
        var (_, chat) = await ArrangeAsync("editor");
        _actors.Map[chat] = null;
        var (text, isError) = await CallAsync(server, chat, tool, new { });
        isError.Should().BeTrue($"{server}/{tool}: {text}");
        text.Should().Be(ActorRightGate.DeniedText, $"{server}/{tool}");
    }

    // Запись контента (открытая участнику по Q1) безымянному актору общего проекта тоже
    // отказана: права нет ни у чего. Чтение — открыто (права не требует).
    [Fact]
    public async Task Фон_ВОбщемПроект_ЗаписьКонтентаОтказана_ЧтениеПроходит()
    {
        var (_, chat) = await ArrangeAsync("editor");
        _actors.Map[chat] = null;

        var (n, nErr) = await CallAsync("notes", chat, "notes_create", new { title = "Фон", content = "x" });
        nErr.Should().BeTrue(n);
        n.Should().Be(ActorRightGate.DeniedText);

        var (list, listErr) = await CallAsync("notes", chat, "notes_list", new { });
        listErr.Should().BeFalse(list);
        var (watch, watchErr) = await CallAsync("watch", chat, "watch_list", new { });
        watchErr.Should().BeFalse(watch);
    }

    // Критерий 2 в живом потоке: чат личного проекта (участников нет) — то же разведение,
    // что у TurnFrame: поведение не изменилось.
    [Fact]
    public async Task ЛичныйПроект_Фон_КакРаньше_ГейтНеОтказывает()
    {
        var dir = Path.Combine(_factory.TempDir, "ar_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var created = await Owner().PostAsJsonAsync("/api/projects",
            new { name = "Личный" + Guid.NewGuid().ToString("N")[..6], rootPath = dir, createDirectory = true });
        created.EnsureSuccessStatusCode();
        var projectId = IdOf(await created.Content.ReadAsStringAsync());

        var session = await Owner().PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "acceptEdits" });
        session.EnsureSuccessStatusCode();
        var chat = IdOf(await session.Content.ReadAsStringAsync());
        _actors.Map[chat] = null;

        var (w, wErr) = await CallAsync("watch", chat, "watch_start", new { name = "стор", poll_command = "exit 0" });
        wErr.Should().BeFalse(w);
        var (n, nErr) = await CallAsync("notes", chat, "notes_create", new { title = "Фон личного проекта", content = "x" });
        nErr.Should().BeFalse(n);
    }

    // Пишущие и исполняющие инструменты ДРУГИХ серверов: единая точка в McpTransportController отказывает
    // и редактору, и читателю с тем же текстом (проверка идёт до тулсета, хвост маршрута не важен)
    public static IEnumerable<object[]> ЗакрытыеУчастнику() =>
    [
        ["watch", "watch_start"],
        ["memory", "memory_remember"],
        ["memory", "team_memory_remember"],
        ["dify", "create_document_by_text"],
        ["architecture", "arch_create_element"],
        ["local-media", "local_generate_image"],
        ["dev", "build"],
        ["tests", "run_tests"],
        ["turn-context", "context_attach"],
    ];

    [Theory]
    [MemberData(nameof(ЗакрытыеУчастнику))]
    public async Task РедакторИЧитатель_ПишущееДругихСерверов_Отказ(string server, string tool)
    {
        foreach (var role in new[] { "editor", "reader" })
        {
            var (_, chat) = await ArrangeAsync(role);
            _actors.Map[chat] = MemberId;
            var (text, isError) = await CallAsync(server, chat, tool, new { });
            isError.Should().BeTrue($"{role}: {server}/{tool}: {text}");
            text.Should().Be(ActorRightGate.DeniedText, $"{role}: {server}/{tool}");
        }
    }

    [Theory]
    [MemberData(nameof(ЗакрытыеУчастнику))]
    public async Task НазванныйВладелец_ПишущееДругихСерверов_ГейтНеОтказывает(string server, string tool)
    {
        var (_, chat) = await ArrangeAsync("editor");
        _actors.Map[chat] = OwnerId;
        var (text, _) = await CallAsync(server, chat, tool, new { });
        text.Should().NotBe(ActorRightGate.DeniedText, $"названный арендатор: {server}/{tool}");
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
