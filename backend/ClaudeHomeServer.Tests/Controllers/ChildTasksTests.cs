using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Б20d (ADR-041 ред. 8, разрез §14.4): «Задачи» ребёнку — доска своего проекта-предмета
/// (все задачи, включая поставленные куратором, и поле viewers — имена кураторов для строки
/// «Эту доску видят: …»; свой обычный или чужой предмет — 403 not_child_subject на обеих
/// ручках), создание только себе (assignee=me без персоны/исполнителя/фичи/плана/дерева/
/// привязки к чату — любое такое поле 400), правка/закрытие/удаление только своих
/// (createdByUserId == ребёнок), смена проекта в правке закрыта (задача не уходит с доски).
/// Закрытое (запуск исполнителя, колонки доски) отбивает гейт И1 — 403 child_mode.
///
/// Форма ответа детской доски — обёртка { tasks, viewers }: поле доски негде нести в списке;
/// взрослый ответ остаётся списком (отдельный тест). Детские правила — ChildTaskScope
/// (TasksController.cs), тот же код отказа, что у «Знаний» (not_child_subject).
///
/// Фикстура — как у ChildFilesRailTests: ребёнок публичным путём стора, его проект — провижером
/// предмета (единственная точка создания), куратор — Editor-участник при рубильнике инстанса
/// и своём флаге общих проектов. Свой tag имени и предмета на каждый тест: хост класса общий.
/// </summary>
[Trait("Category", "Host")]
public class ChildTasksTests : IClassFixture<ChildTasksTests.Hosts>
{
    // Рубильник инстанса общих проектов: без него куратор-участник не доходит до проекта-предмета
    // ребёнка (ProjectAccessService — fail-closed), а тесты чужих задач это предполагают.
    public sealed class Hosts : TestWebApplicationFactory
    {
        public Hosts() => ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
    }

    private const string ChildPassword = "kid-tasks-password";

    private readonly Hosts _host;

    public ChildTasksTests(Hosts host) => _host = host;

    private UserStore Users => _host.Services.GetRequiredService<UserStore>();

    // --- Фикстура ---------------------------------------------------------------

    private (string ChildId, HttpClient Child, Project Project) NewChild(string tag)
    {
        var username = "kid.tasks." + tag;
        var kid = Users.Add(username, ChildPassword, "user");
        var subject = new ChildSubject
        {
            Id = tag, Label = "Предмет " + tag, Glyph = "Аа", Color = "info",
        };
        Users.SetChildProfile(kid.Id, new ChildProfile
        {
            Enabled = true, TutorPersonaId = "tutor-1", ProviderKey = "pool-key", Model = "sonnet",
            Spaces = { [ChildSpaces.Lessons] = new SpaceLimits
            {
                Windows = [new TimeWindow { From = "00:00", To = "23:59" }], DailyMinutes = 600,
            } },
            Subjects = [subject],
        }, resetRules: false).Should().BeTrue("профиль ребёнка пишется публичным путём стора");

        var provided = _host.Services.GetRequiredService<ChildSubjectProjectProvisioner>()
            .EnsureSubjectProjectAsync(kid.Id, subject).GetAwaiter().GetResult();
        provided.Should().NotBeNull("проект-предмета обеспечивает только его провижер");
        return (kid.Id, _host.CreateAuthenticatedClient(username, ChildPassword), provided!);
    }

    // Куратор: взрослый как Editor в проекте-предмета + его флаг общих проектов (оба нужны:
    // роль даёт право, гейт — доступ участника к чужому проекту).
    private HttpClient CuratorOf(Project project)
    {
        var adult = Users.FindByUsername(TestWebApplicationFactory.TestUsername)!;
        adult.FeatureFlags = new Dictionary<string, bool> { [FeatureFlagKeys.SharedProjects] = true };
        _host.Services.GetRequiredService<ProjectManager>().SetMembers(project.Id, [new ProjectMember
        {
            UserId = adult.Id, Role = ProjectRole.Editor, AddedBy = project.OwnerId!,
        }]);
        return _host.CreateAuthenticatedClient();
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage r) =>
        JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync());

    // Создание задачи — id для последующих проверок полного цикла. Ребёнку — 201 (контракт
    // Б20d), взрослому — прежние 200: ожидаемый статус передаёт вызывающий.
    private static async Task<string> CreateTask(HttpClient client, Project project, string title,
        HttpStatusCode expected = HttpStatusCode.Created)
    {
        var resp = await client.PostAsJsonAsync($"/api/projects/{project.Id}/tasks", new { title });
        resp.StatusCode.Should().Be(expected);
        return (await JsonAsync(resp)).GetProperty("id").GetString()!;
    }

    private static List<string?> TaskIdsOf(JsonElement board) =>
        board.GetProperty("tasks").EnumerateArray().Select(t => t.GetProperty("id").GetString()).ToList();

    // Свой обычный проект ребёнка: без маркера предмета, владение — детское. Явный путь под
    // временной папкой хоста — чтобы не зависеть от домашней папки ребёнка.
    private Project RegularProjectOf(string childId, string tag)
    {
        var dir = Path.Combine(_host.TempDir, "regular_" + tag);
        return _host.Services.GetRequiredService<ProjectManager>()
            .Create("обычный " + tag, dir, childId, Users.GetById(childId)!.Username, createDirectory: true);
    }

    // --- Создание только себе ────────────────────────────────────────────────

    // Мутация «снять детскую ветку Create» (ChildTaskScope в ProjectTasksController.Create):
    // краснеет здесь — ответ взрослого 200 вместо 201.
    [Fact]
    public async Task СозданиеСвоё_201_лежит_на_доске_иОтнесено_ребёнку()
    {
        var (childId, child, project) = NewChild("create");
        var resp = await child.PostAsJsonAsync($"/api/projects/{project.Id}/tasks", new { title = "Сделать урок" });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var task = await JsonAsync(resp);
        var id = task.GetProperty("id").GetString()!;
        task.GetProperty("title").GetString().Should().Be("Сделать урок");
        task.GetProperty("projectId").GetString().Should().Be(project.Id);
        // Признак авторства ставит сервер (CreatedByActorUserId = вызывающий) — по нему
        // правка/удаление решают «своя»
        task.GetProperty("createdByUserId").GetString().Should().Be(childId);

        // Полный цикл: запись на детской доске
        var board = await JsonAsync(await child.GetAsync($"/api/projects/{project.Id}/tasks"));
        TaskIdsOf(board).Should().Contain(id);
    }

    // Разрешённые поля обычной задачи — срок, приоритет, колонка, подзадачи, метки: ребёнку
    // доступны (запрещён только блок «персона/исполнитель/фича/план/дерево»).
    [Fact]
    public async Task СозданиеСвоё_СРазрешённымиПолями_201()
    {
        var (_, child, project) = NewChild("allowed-fields");
        var resp = await child.PostAsJsonAsync($"/api/projects/{project.Id}/tasks", new
        {
            title = "Уборка стола",
            priority = "high",
            dueDate = "2026-12-01",
            subtasks = new[] { new { title = "протереть" }, new { title = "помыть" } },
            labels = new[] { "урок" },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var task = await JsonAsync(resp);
        task.GetProperty("priority").GetString().Should().Be("high");
        task.GetProperty("dueDate").GetString().Should().Be("2026-12-01");
        task.GetProperty("subtasks").GetArrayLength().Should().Be(2);
    }

    // Мутация c3-компаньон: убрать строку `CreateDenied` из Create — этот тест краснеет
    // (запрещённое поле дошло бы до обычной записи).
    [Theory]
    [InlineData("personaId", "\"personaId\":\"p-1\"")]
    [InlineData("assignee", "\"assignee\":\"claude\"")]
    [InlineData("featureId", "\"featureId\":\"f-1\"")]
    [InlineData("role", "\"role\":\"step\"")]
    [InlineData("planSteps", "\"planSteps\":[{\"stepRef\":\"a1\",\"title\":\"шаг\"}]")]
    [InlineData("worktreePath", "\"worktreePath\":\"/tmp/x\"")]
    [InlineData("linkedSessionId", "\"linkedSessionId\":\"s-1\"")]
    public async Task Создание_ЗапрещённыеПоля_400_СМашиннымКодом(string field, string json)
    {
        // Id предмета — slug [a-z0-9-]{1,32} (policy.SubjectsValid): имя поля с заглавной
        // буквой сделало бы профиль нечитаемым (DenyAll) и смешало бы отказ поля с 403 гейта.
        var (_, child, project) = NewChild("deny-" + field.ToLowerInvariant());
        var resp = await child.PostAsync($"/api/projects/{project.Id}/tasks",
            new StringContent($"{{\"title\":\"своё\",{json}}}", Encoding.UTF8, "application/json"));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await JsonAsync(resp);
        body.GetProperty("code").GetString().Should().Be(ChildTaskScope.FieldsCode);
        body.GetProperty("field").GetString().Should().Be(field);

        // Отказ — до любой записи: на доске пусто
        var board = await JsonAsync(await child.GetAsync($"/api/projects/{project.Id}/tasks"));
        TaskIdsOf(board).Should().BeEmpty();
    }

    // --- Правка/закрытие/удаление только своих ───────────────────────────────

    [Fact]
    public async Task ПравкаЗакрытиеИУдалениеСвоего_Разрешены()
    {
        var (_, child, project) = NewChild("own-cycle");
        var id = await CreateTask(child, project, "своё");

        var put = await child.PutAsJsonAsync($"/api/tasks/{id}", new { title = "исправлено", status = "done" });
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await JsonAsync(put);
        updated.GetProperty("title").GetString().Should().Be("исправлено");
        updated.GetProperty("status").GetString().Should().Be("done");

        (await child.DeleteAsync($"/api/tasks/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // Мутация c3: снять проверку IsOwnTask в Update (TasksController) — этот тест краснеет
    // (чужая задача правилась бы ребёнком: он владелец проекта-предмета, обычного права
    // записи ему хватает на любой задаче доски).
    [Fact]
    public async Task ПравкаЧужойЗадачи_403_ИЗаписьНеТронута()
    {
        var (_, child, project) = NewChild("foreign-put");
        var curator = CuratorOf(project);
        var id = await CreateTask(curator, project, "задача от куратора", HttpStatusCode.OK);

        var put = await child.PutAsJsonAsync($"/api/tasks/{id}", new { title = "перечёркнута ребёнком" });
        put.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await JsonAsync(put)).GetProperty("code").GetString().Should().Be(ChildTaskScope.ForeignCode);

        // Хранимая запись не тронута: тот же заголовок на детской доске (полный цикл)
        var board = await JsonAsync(await child.GetAsync($"/api/projects/{project.Id}/tasks"));
        var foreign = TaskIdsOf(board).Count(id2 => id2 == id);
        board.GetProperty("tasks").EnumerateArray()
            .First(t => t.GetProperty("id").GetString() == id)
            .GetProperty("title").GetString().Should().Be("задача от куратора");
        foreign.Should().Be(1);
    }

    [Fact]
    public async Task УдалениеЧужойЗадачи_403_ЗаписьЖива()
    {
        var (_, child, project) = NewChild("foreign-del");
        var curator = CuratorOf(project);
        var id = await CreateTask(curator, project, "задача от куратора", HttpStatusCode.OK);

        (await child.DeleteAsync($"/api/tasks/{id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await curator.GetAsync($"/api/tasks/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // «Без исполнителя» обходился бы одним PUT: поднятие своей задачи до Claude закрыто
    // и при правке (тот же список, что при создании).
    [Fact]
    public async Task ПравкаСвоего_ПоднятиеДоКлода_400_ИЗаписьНеТронута()
    {
        var (_, child, project) = NewChild("own-claude");
        var id = await CreateTask(child, project, "своё");

        var put = await child.PutAsJsonAsync($"/api/tasks/{id}", new { assignee = "claude" });
        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonAsync(put)).GetProperty("field").GetString().Should().Be("assignee");

        // Запись не тронута гонкой отказа: TaskManager.Create ставит Me как дефолт (TaskManager.cs:399),
        // поднятия до Claude нет.
        var board = await JsonAsync(await child.GetAsync($"/api/projects/{project.Id}/tasks"));
        board.GetProperty("tasks").EnumerateArray()
            .First(t => t.GetProperty("id").GetString() == id)
            .GetProperty("assignee").GetString().Should().Be("me",
                "назначенный исполнитель не появился");
    }

    // --- Доска закрыта за пределами предмета (находка 1 ревью R-B20d) ────────

    // Свой обычный проект (не проект-предмета) и чужой проект-предмета — 403 not_child_subject
    // на обеих ручках. Мутация «снять проверку IsOwnSubjectProject в GetAll/Create»
    // (TasksController.cs:90, :112) краснит оба теста: ребёнок увидел бы свою обычную доску
    // и чужую детскую.
    [Fact]
    public async Task СвойОбычныйПроект_Ребёнку_403_НаОбеихРучкахЗадач()
    {
        var (childId, child, _) = NewChild("own-regular");
        var regular = RegularProjectOf(childId, "own-regular");

        var get = await child.GetAsync($"/api/projects/{regular.Id}/tasks");
        get.StatusCode.Should().Be(HttpStatusCode.Forbidden, "ребёнку открыта только доска предмета");
        (await JsonAsync(get)).GetProperty("code").GetString().Should().Be(ChildSubjectAccess.NotSubjectCode);

        var post = await child.PostAsJsonAsync($"/api/projects/{regular.Id}/tasks", new { title = "не на доске" });
        post.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await JsonAsync(post)).GetProperty("code").GetString().Should().Be(ChildSubjectAccess.NotSubjectCode);
    }

    // Чужой проект-предмета: видимость есть (Editor-участник + флаг общих проектов) —
    // значит 403 даёт детская проверка «не твой предмет», а не невидимость ProjectScope.
    [Fact]
    public async Task ЧужойПроектПредмета_Ребёнку_403_НаОбеихРучкахЗадач()
    {
        var (childId, child, _) = NewChild("foreign-board");
        var (otherId, _, foreign) = NewChild("foreign-owner");
        _host.Services.GetRequiredService<ProjectManager>().SetMembers(foreign.Id,
            [new ProjectMember { UserId = childId, Role = ProjectRole.Editor, AddedBy = otherId }]);
        Users.SetFeatureFlag(childId, FeatureFlagKeys.SharedProjects, true);

        var get = await child.GetAsync($"/api/projects/{foreign.Id}/tasks");
        get.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await JsonAsync(get)).GetProperty("code").GetString().Should().Be(ChildSubjectAccess.NotSubjectCode);

        var post = await child.PostAsJsonAsync($"/api/projects/{foreign.Id}/tasks", new { title = "не своё" });
        post.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await JsonAsync(post)).GetProperty("code").GetString().Should().Be(ChildSubjectAccess.NotSubjectCode);
    }

    // --- Задача не уходит с доски предмета (находка 2 ревью R-B20d) ──────────

    // Смена проекта в PUT закрыта целиком: и «сделать личной» (пустая строка), и перенос
    // в другой свой проект (guid) — оба уносят задачу с доски. Мутация «снять строку
    // projectId из UpdateDenied» краснит этот тест: задача ушла бы в личные или в regular.
    [Fact]
    public async Task ПравкаСвоего_СменойПроекта_400_ИЗадачаНаДоске()
    {
        var (childId, child, subject) = NewChild("put-project");
        var id = await CreateTask(child, subject, "своё");
        var regular = RegularProjectOf(childId, "put-project");

        foreach (var target in new[] { "", regular.Id })
        {
            var put = await child.PutAsJsonAsync($"/api/tasks/{id}", new { projectId = target });
            put.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"projectId={target}");
            var body = await JsonAsync(put);
            body.GetProperty("code").GetString().Should().Be(ChildTaskScope.FieldsCode);
            body.GetProperty("field").GetString().Should().Be("projectId");
        }

        // Отказ до записи: задача осталась на доске предмета
        var board = await JsonAsync(await child.GetAsync($"/api/projects/{subject.Id}/tasks"));
        TaskIdsOf(board).Should().Contain(id);
    }

    // linkedSessionId закрыт и при правке (тот же список, что при создании). Мутация
    // «снять строку linkedSessionId из UpdateDenied» краснит этот тест.
    [Fact]
    public async Task ПравкаСвоего_LinkedSessionId_400_ИЗаписьНеТронута()
    {
        var (_, child, subject) = NewChild("put-linked");
        var id = await CreateTask(child, subject, "своё");

        var put = await child.PutAsJsonAsync($"/api/tasks/{id}", new { linkedSessionId = "some-session" });
        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await JsonAsync(put);
        body.GetProperty("code").GetString().Should().Be(ChildTaskScope.FieldsCode);
        body.GetProperty("field").GetString().Should().Be("linkedSessionId");

        // Частичной правки нет: та же запись на доске, привязки к чату нет
        var board = await JsonAsync(await child.GetAsync($"/api/projects/{subject.Id}/tasks"));
        var item = board.GetProperty("tasks").EnumerateArray().First(t => t.GetProperty("id").GetString() == id);
        item.GetProperty("title").GetString().Should().Be("своё");
        if (item.TryGetProperty("linkedSessionId", out var linked))
            linked.ValueKind.Should().Be(JsonValueKind.Null, "привязка к чату не записана");
    }

    // --- Закрытое: запуск исполнителя и колонки доски (гейт И1, метки нет) ───

    [Fact]
    public async Task ЗапускИсполнителя_РебёнкуЗакрыт_403ChildMode()
    {
        var (_, child, project) = NewChild("exec");
        var id = await CreateTask(child, project, "своё");

        var resp = await child.PostAsync($"/api/tasks/{id}/execute", new StringContent(""));
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("child_mode");
    }

    [Fact]
    public async Task КолонкиДоски_РебёнкуЗакрыты_403ChildMode()
    {
        var (_, child, project) = NewChild("columns");
        var resp = await child.PutAsJsonAsync($"/api/projects/{project.Id}/board-columns",
            new { columns = new[] { new { name = "Готово", category = "done" } } });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("child_mode");
    }

    // --- Доска: все задачи + viewers кураторов ───────────────────────────────

    // Имена — из живого профиля (та же точка правды, что у ChildSubjectsEndpointsTests):
    // снесённый аккаунт в строку не попадает. Мутация «viewers из Members проекта»: краснеет
    // на ghost-опекуне профиля, которого в Members нет.
    [Fact]
    public async Task Доска_ВсеЗадачиИViewersИменаКураторов()
    {
        var (childId, child, project) = NewChild("board");
        var curator = CuratorOf(project);
        var mine = await CreateTask(child, project, "моя");
        var theirs = await CreateTask(curator, project, "поставил куратор", HttpStatusCode.OK);

        var curatorId = Users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        var ghost = Guid.NewGuid().ToString("N"); // аккаунта с таким id в сторе нет
        Users.GetById(childId)!.ChildMode!.GuardianUserIds = [curatorId, ghost];

        var board = await JsonAsync(await child.GetAsync($"/api/projects/{project.Id}/tasks"));
        board.GetProperty("viewers").GetArrayLength().Should().Be(1, "несуществующему опекуну имя выдумывать нечем");
        board.GetProperty("viewers")[0].GetString().Should().Be(TestWebApplicationFactory.TestUsername);
        // Все задачи доски, включая поставленные куратором; авторство видно по createdByUserId
        TaskIdsOf(board).Should().Contain(mine).And.Contain(theirs);
        board.GetProperty("tasks").EnumerateArray()
            .First(t => t.GetProperty("id").GetString() == theirs)
            .GetProperty("createdByUserId").GetString().Should().Be(curatorId);
    }

    // Взрослый путь не тронут: ответ — список, без обёртки и без viewers.
    [Fact]
    public async Task ДоскаВзрослого_ОстаётсяСписком()
    {
        var (_, child, project) = NewChild("adult-shape");
        var curator = CuratorOf(project);
        await CreateTask(child, project, "детская");

        var resp = await curator.GetAsync($"/api/projects/{project.Id}/tasks");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonAsync(resp)).ValueKind.Should().Be(JsonValueKind.Array,
            "взрослый ответ — прежний список задач");
    }

    // --- Конкурентность ──────────────────────────────────────────────────────
    //
    // Предмет гонки — задача, которую куратор поставил на доске ребёнка: он её автор, и по
    // действующему правилу удаления (F2-T1: автор или владелец записи) только он её и может
    // удалить. Свою задачу ребёнка куратор не удаляет: он ни автор, ни арендатор записи
    // (403 not_task_author — это правило взрослой линии, этот шаг его не меняет; для ребёнка
    // на той же задаче — его собственная правка).

    // Детерминированный префикс гонки: удаление куратора завершилось раньше правки —
    // правка 404 (отсутствие раньше авторства, тот же порядок, что у TaskDeleteDenied).
    [Fact]
    public async Task Конкурентность_ПравкаПослеУдаленияКуратором_404()
    {
        var (_, child, project) = NewChild("race-after");
        var curator = CuratorOf(project);
        var id = await CreateTask(curator, project, "задача от куратора", HttpStatusCode.OK);

        (await curator.DeleteAsync($"/api/tasks/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await child.PutAsJsonAsync($"/api/tasks/{id}", new { title = "правка после удаления" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // Настоящая гонка: правка ребёнка и удаление куратором своей задачи одновременно,
    // 20 повторов. Ни одного 500; итог детерминирован: задача удалена, правка ребёнка —
    // 403 (чужая) или 404 (уже удалённой), третьего исхода нет.
    [Fact]
    public async Task Конкурентность_ГонкаПравкиИУдаления_20Повторов_Без500_ИтогУдалена()
    {
        var (_, child, project) = NewChild("race-20");
        var curator = CuratorOf(project);
        for (var i = 0; i < 20; i++)
        {
            var id = await CreateTask(curator, project, "гонка " + i, HttpStatusCode.OK);
            var putTask = child.PutAsJsonAsync($"/api/tasks/{id}", new { title = "правка " + i });
            var delTask = curator.DeleteAsync($"/api/tasks/{id}");
            var put = await putTask;
            var del = await delTask;

            var putStatus = (int)put.StatusCode;
            var delStatus = (int)del.StatusCode;
            putStatus.Should().NotBe(500, $"правка ребёнка в гонке с удалением (повтор {i}): {await put.Content.ReadAsStringAsync()}");
            // чужая задача (403) или уже удалённая (404) — третьего исхода нет
            putStatus.Should().BeOneOf(403, 404);
            delStatus.Should().NotBe(500, $"удаление куратора в гонке с правкой (повтор {i})");
            delStatus.Should().Be(204, "автор удаляет свою задачу при любой интерлейвинг");

            // Итог после полного цикла — удалена
            (await curator.GetAsync($"/api/tasks/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    // Двойной клик: два одновременных создания одной задачи — две записи, без исключения.
    // Дедуп не требуется — поведение фиксируется как есть.
    [Fact]
    public async Task Конкурентность_ДваОдновременныхСоздания_ДвеЗадачи()
    {
        var (_, child, project) = NewChild("double-click");
        var a = child.PostAsJsonAsync($"/api/projects/{project.Id}/tasks", new { title = "двойной клик" });
        var b = child.PostAsJsonAsync($"/api/projects/{project.Id}/tasks", new { title = "двойной клик" });
        var ra = await a;
        var rb = await b;
        ra.StatusCode.Should().Be(HttpStatusCode.Created);
        rb.StatusCode.Should().Be(HttpStatusCode.Created);
        var idA = (await JsonAsync(ra)).GetProperty("id").GetString()!;
        var idB = (await JsonAsync(rb)).GetProperty("id").GetString()!;
        idA.Should().NotBe(idB);

        var board = await JsonAsync(await child.GetAsync($"/api/projects/{project.Id}/tasks"));
        TaskIdsOf(board).Should().Contain(idA).And.Contain(idB);
    }
}
