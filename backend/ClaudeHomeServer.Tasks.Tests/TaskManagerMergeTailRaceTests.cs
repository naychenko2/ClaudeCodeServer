using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Tasks.Projector;

namespace ClaudeHomeServer.Tasks.Tests;

// FS-XT1, доработка по ревью 786941ae: снятие закрытого хвоста не перетирается конкурентной
// правкой вердикта. Два инварианта и по тесту на каждый:
//   1. решение человека о снятии применяется только к шагам, которые в момент применения ещё
//      закрытый хвост (перепроверка под тем же замком стора, ключи — из свежего прохода учёта);
//      шаг с живым витком или вердиктом не снимается, живой виток не закрывается;
//   2. вердикт с Origin = merge_tail не перетирается последующим Update (гард по образцу
//      DroppedByHumanAt).
// Гонка детерминирована швом MergeTailInterleaveHook (образец — PlanReplaceInterleaveHook),
// окон времени нет.
public class TaskManagerMergeTailRaceTests : IDisposable
{
    private readonly string _dir;
    private readonly TaskManager _sut;

    public TaskManagerMergeTailRaceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "merge_tail_race_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _sut = new TaskManager(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json")
            })
            .Build());
    }

    public void Dispose()
    {
        _sut.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private TaskItem CreateEpic() =>
        _sut.Create(null, "u", new CreateTaskRequest("эпик фичи", Role: TaskRoles.Feature));

    // Сданный непринятый шаг с кодом: по учёту — закрытый хвост (Review/pending).
    private TaskItem CreateClosedTailStep(string epicId, string stepRef) =>
        _sut.Create(null, "u", new CreateTaskRequest("шаг " + stepRef,
            FeatureId: epicId, Role: TaskRoles.Step, StepRef: stepRef, Status: TaskItemStatus.Done));

    // Ключи хвоста тем же путём, что их отдаёт вызывающему (FeatureRun.ClosedTailKeys).
    private List<string> TailKeys(string epicId) =>
        FeatureProjector.Build(_sut.GetById(epicId)!, _sut.GetByOwner("u"), DateTime.UtcNow)!.ClosedTailKeys;

    // (а) Между снимком хвоста (TasksController.MergeFeature) и применением снятия ревьюер завёл
    // виток на хвостовой шаг: шаг — уже не закрытый хвост, он не снят; виток остаётся живым.
    // Мутация «убрать перепроверку хвоста при снятии» (снимать заявленный ключ без сверки с учётом)
    // → красный: шаг получил бы withdrawn/merge_tail, а живой виток был бы закрыт вместе с ним.
    [Fact]
    public void WithdrawMergeTail_ЖивойВитокНаХвостовомШаге_ШагНеСнятИВитокЖивой()
    {
        var epic = CreateEpic();
        var step = CreateClosedTailStep(epic.Id, "Ш1");
        TailKeys(epic.Id).Should().Equal(["Ш1"], "фикстура: сданный непринятый шаг — закрытый хвост");

        var snapshot = TailKeys(epic.Id);
        _sut.MergeTailInterleaveHook = () => _sut.Create(null, "u", new CreateTaskRequest("ревью Ш1",
            FeatureId: epic.Id, Role: TaskRoles.Review, ReviewOf: [step.Id]));

        var res = _sut.WithdrawMergeTail(epic.Id, snapshot, "u");

        res.NotAppliedKeys.Should().Equal(["Ш1"], "шаг с живым витком в момент применения не закрытый хвост");
        res.Withdrawn.Should().BeEmpty();

        _sut.GetById(step.Id)!.Verification.Should().BeNull("шаг не снят");
        var review = _sut.GetByOwner("u").Single(t => t.Role == TaskRoles.Review);
        review.Status.Should().NotBe(TaskItemStatus.Done, "живой виток не закрывается снятием хвоста");
        review.Verification.Should().BeNull();
        TailKeys(epic.Id).Should().BeEmpty("цепочка с живым витком — не хвост: её снимает только решение о живом");
    }

    // Идемпотентность повторного нажатия не считается «не применено»: снятый этим же путём шаг
    // не даёт второй волны записей и не поднимает отказ. Мутация «не различать уже снятое при мерже»
    // → красный второй абзац.
    [Fact]
    public void WithdrawMergeTail_ПовторПослеСнятия_НеДаётВторойВолныИНеОтказывает()
    {
        var epic = CreateEpic();
        var step = CreateClosedTailStep(epic.Id, "Ш1");

        var first = _sut.WithdrawMergeTail(epic.Id, ["Ш1"], "u");
        first.Withdrawn.Should().ContainSingle();
        first.NotAppliedKeys.Should().BeEmpty();

        var second = _sut.WithdrawMergeTail(epic.Id, ["Ш1"], "u");
        second.Withdrawn.Should().BeEmpty();
        second.NotAppliedKeys.Should().BeEmpty("решение исполнено раньше — это не «не применено»");
    }

    // (б) Update с verification=accepted на шаге, снятом при мерже: вердикт не перетёрт.
    // Агентскому пути — внятный отказ (по образцу DroppedByHumanAt), человеческому — вердикт не
    // меняется, остальные поля сохраняются. Мутация «убрать гард» → красный: вердикт стал бы
    // accepted, Origin merge_tail и счётчик «смержено без ревью» потерялись.
    [Fact]
    public void Update_ВердикНаСнятомПриМержеШаге_НеПеретирается()
    {
        var epic = CreateEpic();
        var step = CreateClosedTailStep(epic.Id, "Ш1");
        _sut.WithdrawMergeTail(epic.Id, ["Ш1"], "u").Withdrawn.Should().ContainSingle();
        _sut.GetById(step.Id)!.Verification!.Origin.Should().Be(TaskVerificationOrigins.MergeTail);

        var agentCall = () => _sut.Update(step.Id, new UpdateTaskRequest(
            Verification: new TaskVerification { Verdict = TaskVerdicts.Accepted, Notes = "принят" }),
            isAgentCall: true);
        agentCall.Should().Throw<InvalidOperationException>().WithMessage("*снята при мерже*");
        _sut.GetById(step.Id)!.Verification!.Verdict.Should().Be(TaskVerdicts.Withdrawn);
        _sut.GetById(step.Id)!.Verification!.Origin.Should().Be(TaskVerificationOrigins.MergeTail);

        _sut.Update(step.Id, new UpdateTaskRequest(
            Title: "шаг Ш1 (уточнённый)",
            Verification: new TaskVerification { Verdict = TaskVerdicts.Accepted, Notes = "принят" }));
        var stored = _sut.GetById(step.Id)!;
        stored.Title.Should().Be("шаг Ш1 (уточнённый)", "гард касается вердикта, а не всей правки");
        stored.Verification!.Verdict.Should().Be(TaskVerdicts.Withdrawn);
        stored.Verification!.Origin.Should().Be(TaskVerificationOrigins.MergeTail);
    }

    // Легальный путь назад: переоткрытие (Status извне Done) снимает вердикт общим правилом
    // Update — шаг возвращается в знаменатель (§8.4 п.13). Гард не должен это блокировать.
    [Fact]
    public void Update_ПереоткрытиеСнятогоПриМержеШага_ВердикСнимаетсяОбычнымПорядком()
    {
        var epic = CreateEpic();
        var step = CreateClosedTailStep(epic.Id, "Ш1");
        _sut.WithdrawMergeTail(epic.Id, ["Ш1"], "u");

        _sut.Update(step.Id, new UpdateTaskRequest(Status: TaskItemStatus.Todo), isAgentCall: true);
        var reopened = _sut.GetById(step.Id)!;

        reopened.Status.Should().Be(TaskItemStatus.Todo);
        reopened.Verification.Should().BeNull("переоткрытие — легальный путь, не перетирание");
        TailKeys(epic.Id).Should().BeEmpty("открытый шаг — живая работа, не хвост");
    }
}
