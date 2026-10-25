using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Tasks.Projector;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Sdk;

namespace ClaudeHomeServer.Tests.Services;

// Контракт соседнего слоя «ручка меняет состояние — вещает task_changed по id»: стор фронта
// живёт только по этому событию, молчаливая мутация оставляет экран устаревшим (линия паузы
// W4, 24.10). По тесту на каждый из четырёх дефектов через AssertBroadcasts. Мутация —
// удалить вещание в указанном месте: соответствующий тест краснеет на «не вызвало task_changed».
//   1) постановка паузы (1c47fb3b4)      — TasksController.SetFeatureHold: эпик и помеченный шаг
//   2) снятие паузы (70eb01877)          — TasksController.ClearFeatureHold: эпик
//   3) продолжение шага (ca193aa03)      — ResumeFeatureTurnsAfterHold: снятая пометка feature_held
//   4) чат исчез за паузу (8ef589f7e)    — ResumeFeatureTurnsAfterHold: пометка feature_held_expired
public class TaskChangedBroadcastContractTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly TestSessionBroadcaster _bc = new();
    private readonly TaskExecutionService _exec;
    private readonly TaskManager _tasks;
    private readonly SessionManager _sessions;
    private readonly string _owner;

    public TaskChangedBroadcastContractTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        var sp = factory.Services;
        _tasks = sp.GetRequiredService<TaskManager>();
        _sessions = sp.GetRequiredService<SessionManager>();
        _owner = sp.GetRequiredService<UserStore>().FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        // Тот же сервис, что в DI, но с тестовым броадкастером: вещание читаем из его списков.
        _exec = new TaskExecutionService(_tasks, _sessions, sp.GetRequiredService<PersonaManager>(), _bc,
            sp.GetRequiredService<PushService>(), sp.GetRequiredService<NotificationService>(),
            NullLogger<TaskExecutionService>.Instance, sp.GetRequiredService<IConfiguration>());
    }

    private TasksController BuildController()
    {
        var sp = _factory.Services;
        var controller = new TasksController(
            tasks: _tasks, broadcaster: _bc, ai: sp.GetRequiredService<TaskAiService>(),
            projects: sp.GetRequiredService<ProjectManager>(), personas: sp.GetRequiredService<PersonaManager>(),
            executor: _exec, bindings: sp.GetRequiredService<PersonaBindingsService>(), sessions: _sessions,
            spheres: sp.GetRequiredService<ClaudeHomeServer.Services.Spheres.ISphereDirectory>(),
            features: sp.GetRequiredService<FeatureProjector>());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(JwtRegisteredClaimNames.Sub, _owner)], "test")),
            },
        };
        return controller;
    }

    private TaskItem CreateEpic() => _tasks.Create(null, _owner,
        new CreateTaskRequest("эпик-" + Guid.NewGuid().ToString("N")[..6], Role: "feature"));

    private TaskItem CreateStep(string epicId, string sessionId)
    {
        var step = _tasks.Create(null, _owner, new CreateTaskRequest("шаг", Role: "step",
            FeatureId: epicId, Status: TaskItemStatus.InProgress));
        _tasks.MarkClaudeStarted(step.Id, sessionId, DateTime.UtcNow);
        return step;
    }

    [Fact]
    public async Task SetFeatureHold_ВещаетTaskChanged_ПоЭпикуИПомеченномуШагу()
    {
        var epic = CreateEpic();
        var step = CreateStep(epic.Id, "sess-contract-1");
        var controller = BuildController();

        // Один вызов ручки, два id: эпик (по Hold проектор считает paused) и помеченный шаг.
        var calls = 0;
        Task Post() { calls++; return controller.SetFeatureHold(epic.Id, new TasksController.FeatureHoldRequest("manual")); }
        await _bc.AssertBroadcasts("task_changed", epic.Id, _owner, Post);
        _tasks.ClearHold(epic.Id); // повторная постановка — тот же сценарий для проверки шага
        await _bc.AssertBroadcasts("task_changed", step.Id, _owner, Post);
        calls.Should().Be(2);
    }

    [Fact]
    public async Task ClearFeatureHold_ВещаетTaskChanged_ПоЭпику()
    {
        var epic = CreateEpic();
        var controller = BuildController();
        (await controller.SetFeatureHold(epic.Id, new TasksController.FeatureHoldRequest("manual")))
            .Should().BeOfType<OkObjectResult>();

        await _bc.AssertBroadcasts("task_changed", epic.Id, _owner, async () =>
            (await controller.ClearFeatureHold(epic.Id)).Should().BeOfType<OkObjectResult>());
    }

    [Fact]
    public async Task ResumeFeatureTurnsAfterHold_СнятиеПометки_ВещаетTaskChanged_ПоШагу()
    {
        var epic = CreateEpic();
        var chat = await _sessions.CreateChatAsync(_owner, ClaudeMode.AcceptEdits, name: "exec");
        var step = CreateStep(epic.Id, chat.Id);
        _tasks.MarkExecutorStopped(step.Id, DateTime.UtcNow, ExecutorStopClassifier.FeatureHeldReason);

        await _bc.AssertBroadcasts("task_changed", step.Id, _owner,
            () => _exec.ResumeFeatureTurnsAfterHold(epic.Id, _owner, FeatureHoldReasons.Manual));

        _tasks.GetById(step.Id)!.ExecutorStopReason.Should().BeNull("пометка паузы снята продолжением");
    }

    [Fact]
    public async Task ResumeFeatureTurnsAfterHold_ЧатИсчез_ВещаетTaskChanged_ПоШагу()
    {
        var epic = CreateEpic();
        var step = CreateStep(epic.Id, "chat-gone-" + Guid.NewGuid().ToString("N")[..6]);
        _tasks.MarkExecutorStopped(step.Id, DateTime.UtcNow, ExecutorStopClassifier.FeatureHeldReason);

        await _bc.AssertBroadcasts("task_changed", step.Id, _owner,
            () => _exec.ResumeFeatureTurnsAfterHold(epic.Id, _owner, FeatureHoldReasons.Manual));

        _tasks.GetById(step.Id)!.ExecutorStopReason.Should().Be(ExecutorStopClassifier.FeatureHeldExpiredReason);
    }

    // Невакуумность самого хелпера: вещание по ДРУГОЙ задаче не засчитывается. Мутация —
    // убрать фильтр по id в AssertBroadcasts: этот тест краснеет (хелпер молча пропускает).
    [Fact]
    public async Task AssertBroadcasts_ЧужойId_НеЗасчитывается()
    {
        var epic = CreateEpic();
        var other = CreateEpic();

        var act = () => _bc.AssertBroadcasts("task_changed", other.Id, _owner,
            () => _bc.ToOwner(_owner, new Protocol.TaskChangedMessage("updated", epic)));

        await act.Should().ThrowAsync<XunitException>("событие ушло по эпику, а не по искомому id");
    }

    // Адресат: событие ушло в группу другого владельца — не засчитывается. Мутация — вернуть
    // фильтр «ownerId is null || …» / убрать фильтр по OwnerId: этот тест краснеет.
    [Fact]
    public async Task AssertBroadcasts_ЧужойВладелец_НеЗасчитывается()
    {
        var epic = CreateEpic();

        var act = () => _bc.AssertBroadcasts("task_changed", epic.Id, _owner,
            () => _bc.ToOwner("other-owner", new Protocol.TaskChangedMessage("updated", epic)));

        await act.Should().ThrowAsync<XunitException>("событие ушло в группу другого владельца");
    }

    // Неподдержанный тип отвергается и при пустом вещании, до выполнения действия.
    [Fact]
    public async Task AssertBroadcasts_НеподдержанныйТип_ОтвергаетсяНаПустомСписке()
    {
        var ran = false;
        var act = () => _bc.AssertBroadcasts("session_changed", "x", _owner, () => { ran = true; return Task.CompletedTask; });

        await act.Should().ThrowAsync<ArgumentException>();
        ran.Should().BeFalse("тип проверяется до действия");
    }
}
