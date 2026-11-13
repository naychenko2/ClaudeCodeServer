using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Tasks;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tasks.Tests;

// B4a (разрез workflow §5, И7): рекомендатель маршрута фичи на фейковом ICheapTextRunner.
// Сервер режет по белым спискам: чужой templateId и токен, которого нет среди id optional-этапов
// выбранного шаблона (роль с Presence=on или роль вместо id графового этапа — R-B4a), отбрасываются;
// не-JSON и сбой модели — предложения нет, без исключения. И7: epic.Workflow не трогается
// никогда. Устаревшее предложение (маршрут подтвердили, пока ждали ответ) не пишется; два
// параллельных подбора оставляют в эпике одно целое предложение.
public class WorkflowRecommenderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"wrec-{Guid.NewGuid():N}");
    private readonly WorkflowTemplateStore _store;
    private readonly TaskManager _tasks;
    private const string Owner = "u";

    public WorkflowRecommenderTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new WorkflowTemplateStore(Path.Combine(_dir, "workflow-templates.json"));
        _tasks = new TaskManager(Config());
    }

    private IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dir, "tasks.json"),
            ["Tasks:SaveDebounceMs"] = "0",
        })
        .Build();

    public void Dispose()
    {
        _tasks.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* уборка best-effort */ }
    }

    // Фейковый раннер: текст ответа, сбой по запросу и Gates — вызов висит, пока не откроют
    // (для гонок: подтверждение маршрута и параллельные подбор arriving до ответа модели)
    private sealed class StubRunner : ICheapTextRunner
    {
        public int Calls;
        public string Text = "";
        public Exception? Failure;
        public bool GateMode;
        public readonly List<TaskCompletionSource<string>> Gates = [];
        public string LastPrompt = "";
        public string? LastActionKey;
        public object? LastJsonFormat;

        public bool UsesLocal(string actionKey) => true;
        public bool HasFreeRoute(string actionKey) => true;
        public bool HasRoute(string actionKey) => true;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "local=stub";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            Calls++;
            LastActionKey = actionKey;
            LastPrompt = prompt;
            LastJsonFormat = jsonFormat;
            if (Failure is not null) throw Failure;
            if (!GateMode) return Task.FromResult(Text);
            var gate = new TaskCompletionSource<string>();
            Gates.Add(gate);
            return gate.Task;
        }

        public async Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default) => await RunAsync(actionKey, prompt, null, ownerId, jsonFormat, ct);

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, TimeSpan? timeout = null, int? maxTokens = null,
            object? jsonFormat = null, CancellationToken ct = default)
            => Task.FromResult(new OneShotResult(RunAsync(actionKey, prompt, fallbackModel, ownerId, jsonFormat, ct).Result, null, 10));
    }

    private WorkflowRecommender MakeService(StubRunner runner) =>
        new WorkflowRecommender(_tasks, _store, Config(), runner);

    // Шаблон: qa — единственный этап с Presence=optional (тот же рисунок, что у TaskManagerWorkflowTests)
    private WorkflowTemplate Template(string name = "тест") => _store.Save(new WorkflowTemplate
    {
        Name = name,
        Description = "правка с ревью",
        Stages =
        [
            new() { Role = TaskRoles.Plan },
            new() { Role = TaskRoles.Step },
            new() { Role = TaskRoles.Review },
            new() { Role = TaskRoles.Qa, Presence = WorkflowStagePresence.Optional },
            new() { Role = TaskRoles.Final },
        ],
    }, WorkflowTemplateScope.Global);

    // Графовый шаблон (R-B4a Major 2): два этапа одной роли review с разными id — ровно та форма,
    // где id этапа НЕ равен роли. На step её не построить: роль несъёмная, Presence «optional»
    // ей запрещён (WorkflowRules.cs:22,127), а повторить без id нельзя — id = роль был бы дублем.
    // Имена (G7, a5d0c76e): общая роль — обязательный и различный Title у каждого этапа группы.
    private WorkflowTemplate GraphTemplate() => _store.Save(new WorkflowTemplate
    {
        Name = "граф",
        Description = "ревью своей веткой",
        Stages =
        [
            new() { Role = TaskRoles.Plan },
            new() { Role = TaskRoles.Step },
            new() { Id = "review-back", Role = TaskRoles.Review, Presence = WorkflowStagePresence.Optional, Title = "Ревью бэка" },
            new() { Id = "review-front", Role = TaskRoles.Review, Presence = WorkflowStagePresence.Optional, Title = "Ревью фронта" },
            new() { Role = TaskRoles.Final },
        ],
    }, WorkflowTemplateScope.Global);

    private TaskItem Epic(string? projectId = null, string description = "малая правка") =>
        _tasks.Create(projectId, Owner, new CreateTaskRequest("фича", Description: description, Role: TaskRoles.Feature));

    private static string Answer(string templateId, string skipped = "", string rationale = "по описанию") =>
        "{\"templateId\":\"" + templateId + "\",\"skipped\":[" + skipped + "],\"rationale\":\"" + rationale + "\"}";

    // Маршрут для гонки «человек подтвердил, пока ждали модель»: Stages — той же production-точкой,
    // что у TaskWorkflowController.Confirm (G1), переходный шим Route удалён (G3, п.8).
    private static FeatureWorkflow Route(WorkflowTemplate tpl) => new()
    {
        TemplateId = tpl.Id,
        Scope = "global",
        Stages = WorkflowGraph.Resolve(tpl, []),
        Skipped = [],
        ConfirmedVersion = tpl.Version,
        ConfirmedAt = DateTime.UtcNow,
    };

    // Белый список 1: шаблон, которого нет в каталоге вызова, — предложения нет (и на эпике тоже)
    [Fact]
    public async Task Recommend_ЧужойTemplateId_ПредложенияНет()
    {
        var tpl = Template();
        var epic = Epic();
        var service = MakeService(new StubRunner { Text = Answer("no-such-template") });

        var result = await service.RecommendAsync(epic.Id, Owner, CancellationToken.None);

        result.Should().BeNull();
        _tasks.GetById(epic.Id)!.WorkflowProposal.Should().BeNull();
    }

    // Белый список 2: роль этапа с Presence=on снять нельзя — она отброшена, optional осталась
    [Fact]
    public async Task Recommend_РольPresenceOn_ИзSkippedОтброшена()
    {
        var tpl = Template();
        var epic = Epic();
        var service = MakeService(new StubRunner { Text = Answer(tpl.Id, $"\"{TaskRoles.Qa}\",\"{TaskRoles.Review}\"") });

        var result = await service.RecommendAsync(epic.Id, Owner, CancellationToken.None);

        result.Should().NotBeNull();
        result!.TemplateId.Should().Be(tpl.Id);
        // review — Presence=on, снять его нельзя; qa — единственный optional, он остался
        result.Skipped.Should().Equal(new[] { TaskRoles.Qa });
        _tasks.GetById(epic.Id)!.WorkflowProposal!.Skipped.Should().Equal(new[] { TaskRoles.Qa });
    }

    // Графовый шаблон (R-B4a Major 1/2): белый список и промпт идут по id этапа — ответ модели
    // «review-front» остаётся в Skipped именно этим id (его читает WorkflowGraph.States)
    [Fact]
    public async Task Recommend_ГрафовыйШаблон_SkippedПоIdЭтапа()
    {
        var tpl = GraphTemplate();
        var epic = Epic();
        var runner = new StubRunner { Text = Answer(tpl.Id, "\"review-front\"") };

        var result = await MakeService(runner).RecommendAsync(epic.Id, Owner, CancellationToken.None);

        result.Should().NotBeNull();
        // ключ этапа — его id, а не роль: в Skipped ровно тот токен, что вернула модель
        result!.Skipped.Should().Equal(new[] { "review-front" });
        _tasks.GetById(epic.Id)!.WorkflowProposal!.Skipped.Should().Equal(new[] { "review-front" });
        // промпт показывает этапы как id(роль, presence, после=[…]): модель отвечает тем же
        // токеном, который потом читается по id (G5: виден и граф, а не только список ролей)
        runner.LastPrompt.Should().Contain("review-back(review, optional, после=[step])");
        runner.LastPrompt.Should().Contain("review-front(review, optional, после=[review-back])");
    }

    // G5: каталог для модели — id/роль/«после» каждого этапа после Normalize, то есть ровно тот
    // граф, который построит подтверждение маршрута: параллельные ветки видно по общему «после»
    [Fact]
    public async Task Recommend_КаталогПресета_ИдРолиИПослеКаждогоЭтапа()
    {
        var preset = WorkflowTemplateStore.BuiltInPreset(WorkflowTemplateStore.ParallelCycleKey);
        var epic = Epic();
        var runner = new StubRunner { Text = Answer(preset.Id) };

        await MakeService(runner).RecommendAsync(epic.Id, Owner, CancellationToken.None);

        runner.LastPrompt.Should().Contain("step-back(step, on, после=[plan])");
        runner.LastPrompt.Should().Contain("step-front(step, on, после=[plan])");
        runner.LastPrompt.Should().Contain("qa-plan(qa, optional, после=[design])");
        runner.LastPrompt.Should().Contain("qa(qa, optional, после=[review,qa-plan])");
        // линейные пресеты остаются списком ролей: их «после» — предыдущий в списке
        runner.LastPrompt.Should().Contain("final(final, on, после=[qa])");
    }

    // Устаревшее (G5): шаблон правили, пока модель думала, — id снятого этапа отбрасывается,
    // предложение пишется без него (остальной список — по теперешним id шаблона)
    [Fact]
    public async Task Recommend_ИдЭтапаУбранПравкой_ИзSkippedОтброшен()
    {
        var tpl = GraphTemplate();
        var epic = Epic();
        var runner = new StubRunner { GateMode = true };
        var service = MakeService(runner);

        var running = service.RecommendAsync(epic.Id, Owner, CancellationToken.None);
        var edited = _store.Get(tpl.Id, WorkflowTemplateScope.Global)!;
        edited.Stages.RemoveAll(s => s.Id == "review-front");
        _store.Save(edited, WorkflowTemplateScope.Global);
        runner.Gates[0].SetResult(Answer(tpl.Id, "\"review-front\",\"review-back\""));

        var result = await running;

        result.Should().NotBeNull();
        // id, которого в шаблоне уже нет, в предложение не попадает; остальное — по теперешним id
        result!.Skipped.Should().Equal(new[] { "review-back" });
        _tasks.GetById(epic.Id)!.WorkflowProposal!.Skipped.Should().Equal(new[] { "review-back" });
    }

    // Шаблон удалили, пока модель думала: предложения нет — оно не может ссылаться на несуществующий
    [Fact]
    public async Task Recommend_ШаблонУдалёнПравкой_ПредложенияНет()
    {
        var tpl = GraphTemplate();
        var epic = Epic();
        var runner = new StubRunner { GateMode = true };
        var service = MakeService(runner);

        var running = service.RecommendAsync(epic.Id, Owner, CancellationToken.None);
        _store.Delete(tpl.Id, WorkflowTemplateScope.Global);
        runner.Gates[0].SetResult(Answer(tpl.Id, "\"review-front\""));

        var result = await running;

        result.Should().BeNull();
        _tasks.GetById(epic.Id)!.WorkflowProposal.Should().BeNull();
    }

    // Роль вместо id: среди id этапов выбранного шаблона токена «review» нет — Skipped пустой.
    // На линейном шаблоне роль и есть id, там тот же ответ прошёл бы — дрейф ловит только граф
    [Fact]
    public async Task Recommend_ГрафовыйШаблон_РольВместоId_СкипПустой()
    {
        var tpl = GraphTemplate();
        var epic = Epic();

        var result = await MakeService(new StubRunner { Text = Answer(tpl.Id, "\"review\"") })
            .RecommendAsync(epic.Id, Owner, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Skipped.Should().BeEmpty("роль не ключ этапа: её нет среди id шаблона");
        _tasks.GetById(epic.Id)!.WorkflowProposal!.Skipped.Should().BeEmpty();
    }

    // Не-JSON (проза вместо объекта) — null, без исключения наружу и без записи на эпик
    [Fact]
    public async Task Recommend_НеJson_NullБезИсключения()
    {
        Template();
        var epic = Epic();
        var service = MakeService(new StubRunner { Text = "Подойдёт полный цикл, я уверен." });

        var result = await service.RecommendAsync(epic.Id, Owner, CancellationToken.None);

        result.Should().BeNull();
        _tasks.GetById(epic.Id)!.WorkflowProposal.Should().BeNull();
    }

    // Сбой вызова (таймаут модели) — null и лог, наружу ничего не уходит
    [Fact]
    public async Task Recommend_СбойМодели_NullБезИсключения()
    {
        Template();
        var epic = Epic();
        var service = MakeService(new StubRunner { Failure = new LlmTimeoutException("не ответил за отведённое время") });

        var result = await service.RecommendAsync(epic.Id, Owner, CancellationToken.None);

        result.Should().BeNull();
        _tasks.GetById(epic.Id)!.WorkflowProposal.Should().BeNull();
    }

    // И7: успешный подбор пишет WorkflowProposal и не трогает Workflow (его подтверждает человек)
    [Fact]
    public async Task Recommend_WorkflowЭпикаНеИзменяется()
    {
        var tpl = Template();
        var epic = Epic();
        var service = MakeService(new StubRunner { Text = Answer(tpl.Id, $"\"{TaskRoles.Qa}\"") });

        var result = await service.RecommendAsync(epic.Id, Owner, CancellationToken.None);

        var after = _tasks.GetById(epic.Id)!;
        result.Should().NotBeNull();
        after.Workflow.Should().BeNull("рекомендатель не подтверждает маршрут (И7)");
        after.WorkflowProposal.Should().NotBeNull();
        after.WorkflowProposal!.TemplateId.Should().Be(tpl.Id);
        after.WorkflowProposal.Scope.Should().Be("global");
        after.WorkflowProposal.ProposedAt.Should().NotBe(default);
    }

    // Чужой эпик (не тот владелец) — предложения нет: per-owner, как в остальном чтении задач
    [Fact]
    public async Task Recommend_ЧужойЭпик_ПредложенияНет()
    {
        var tpl = Template();
        var epic = Epic();
        var runner = new StubRunner { Text = Answer(tpl.Id) };

        var result = await MakeService(runner).RecommendAsync(epic.Id, "other-owner", CancellationToken.None);

        result.Should().BeNull();
        runner.Calls.Should().Be(0, "модель не зовётся для чужого эпика");
    }

    // Гонка: пока ждали ответ модели, человек подтвердил маршрут — устаревшее предложение не пишется
    [Fact]
    public async Task Recommend_ОтветПослеПодтверждения_НеПишется()
    {
        var tpl = Template();
        var epic = Epic();
        var runner = new StubRunner { GateMode = true };
        var service = MakeService(runner);

        var running = service.RecommendAsync(epic.Id, Owner, CancellationToken.None);
        // Подтверждение — ПОКА вызов модели висит: к его приходу решение уже принято
        _tasks.ConfirmWorkflow(epic.Id, Route(tpl));
        runner.Gates[0].SetResult(Answer(tpl.Id, $"\"{TaskRoles.Qa}\""));

        var result = await running;
        var after = _tasks.GetById(epic.Id)!;
        result.Should().BeNull("решение человека старше предложения модели");
        after.WorkflowProposal.Should().BeNull();
        after.Workflow.Should().NotBeNull();
    }

    // Два параллельных подбора одного эпика: в эпике остаётся предложение одного вызова целиком,
    // а не смесь полей (TemplateId одного и Skipped другого)
    [Fact]
    public async Task Recommend_ДваПараллельныхВызова_ОдноЦелоеПредложение()
    {
        var tplA = Template("шаблон A");
        var tplB = _store.Save(new WorkflowTemplate
        {
            Name = "шаблон B",
            Stages =
            [
                new() { Role = TaskRoles.Step },
                new() { Role = TaskRoles.Review },
                new() { Role = TaskRoles.Qa, Presence = WorkflowStagePresence.Optional },
            ],
        }, WorkflowTemplateScope.Global);
        var epic = Epic();
        var runner = new StubRunner { GateMode = true };
        var service = MakeService(runner);

        var first = service.RecommendAsync(epic.Id, Owner, CancellationToken.None);
        var second = service.RecommendAsync(epic.Id, Owner, CancellationToken.None);
        runner.Gates[0].SetResult(Answer(tplA.Id));
        runner.Gates[1].SetResult(Answer(tplB.Id, $"\"{TaskRoles.Qa}\""));
        await Task.WhenAll(first, second);

        var proposal = _tasks.GetById(epic.Id)!.WorkflowProposal;
        proposal.Should().NotBeNull();
        // Целое предложение = и пара из одного ответа: чужой Skipped к этому TemplateId не подмешан
        if (proposal!.TemplateId == tplA.Id)
        {
            proposal.Skipped.Should().BeEmpty("предложение вызова A целиком из ответа A");
        }
        else
        {
            proposal.TemplateId.Should().Be(tplB.Id, "предложение другого вызова");
            proposal.Skipped.Should().Equal(TaskRoles.Qa);
        }
    }

    // Каталог вызова — глобальные И проектные шаблоны; предложение по проектному помнит область
    [Fact]
    public async Task Recommend_ПроектныйШаблон_ScopeProject()
    {
        var projectTpl = _store.Save(new WorkflowTemplate
        {
            Name = "проектный",
            Stages = [new() { Role = TaskRoles.Step }, new() { Role = TaskRoles.Review }],
        }, WorkflowTemplateScope.Project, "p1");
        var epic = Epic(projectId: "p1");
        var runner = new StubRunner { Text = Answer(projectTpl.Id) };

        var result = await MakeService(runner).RecommendAsync(epic.Id, Owner, CancellationToken.None);

        result.Should().NotBeNull();
        result!.TemplateId.Should().Be(projectTpl.Id);
        result.Scope.Should().Be("project");
        runner.LastPrompt.Should().Contain(projectTpl.Id, "каталог промпта включает проектные шаблоны эпика");
    }

    // Бюджет промпта: описание эпика длиннее MaxDescriptionChars в промпт не уходит целиком —
    // иначе его хвост вытесняет каталог шаблонов (его Ollama режет молча)
    [Fact]
    public async Task Recommend_ДлинноеОписание_ОбрезаноВПромпте()
    {
        var tpl = Template();
        var description = new string('о', WorkflowRecommender.MaxDescriptionChars) + "ХВОСТ";
        var epic = Epic(description: description);
        var runner = new StubRunner { Text = Answer(tpl.Id) };

        await MakeService(runner).RecommendAsync(epic.Id, Owner, CancellationToken.None);

        runner.LastPrompt.Should().NotContain("ХВОСТ");
        runner.LastPrompt.Should().Contain(new string('о', WorkflowRecommender.MaxDescriptionChars));
        runner.LastJsonFormat.Should().Be("json", "строгий JSON-контракт места");
        runner.LastActionKey.Should().Be(LocalActionCatalog.FeatureWorkflowRecommend);
    }
}
