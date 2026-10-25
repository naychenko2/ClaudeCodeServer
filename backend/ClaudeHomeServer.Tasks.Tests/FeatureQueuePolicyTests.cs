using System.Reflection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks.Projector;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tasks.Tests;

// Очередь фич, шаг Q1 (волна 6, разрез Александра 25.10): чистые правила — OccupiesSlot,
// FeatureQueuePolicy.Order/Limit, вычисляемое queue { position, length }. Инварианты И3, И5, И9.
public class FeatureQueuePolicyTests
{
    private static readonly DateTime T0 = new(2026, 10, 25, 10, 0, 0, DateTimeKind.Utc);

    // Ожидаемая таблица «занимает слот» по ВСЕМ значениям FeatureStates (разрез §1).
    private static readonly Dictionary<string, bool> Expected = new()
    {
        [FeatureStates.Merged] = false,
        [FeatureStates.Closed] = false,
        [FeatureStates.MergePending] = false,
        [FeatureStates.Merging] = false,
        [FeatureStates.Paused] = false,
        [FeatureStates.Draft] = false,
        [FeatureStates.Stopped] = true,
        [FeatureStates.Waiting] = true,
        [FeatureStates.Running] = true,
        [FeatureStates.Idle] = true,
        [FeatureStates.NotStarted] = true,
    };

    private static FeatureRun RunIn(string state) =>
        new() { FeatureId = "f", Epic = new TaskItem { Id = "f", Role = TaskRoles.Feature }, State = state };

    private static string[] AllStates() => typeof(FeatureStates)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral)
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToArray();

    // Фича в очереди: пауза с причиной queue, Hold.Since и проект задаются.
    private static TaskItem QueuedEpic(string id, DateTime since, string? projectId = "p1") => new()
    {
        Id = id,
        Role = TaskRoles.Feature,
        ProjectId = projectId,
        Hold = new FeatureHold(FeatureHoldReasons.Queue, since, null, true),
    };

    private static FeatureRun Queued(string id, DateTime since, string? projectId = "p1")
    {
        var epic = QueuedEpic(id, since, projectId);
        return FeatureProjector.Build(epic, [epic]);
    }

    // ── OccupiesSlot (И3) ───────────────────────────────────────────────────────────────────

    // Каждое значение FeatureStates — явная ветка. Новая константа без строки в Expected краснит
    // тест; убрать ветку (например merge_pending) — значение падает в `_ => true` и расходится с
    // таблицей.
    [Fact]
    public void OccupiesSlot_КаждоеЗначениеFeatureStates_ЯвнаяВетка()
    {
        var states = AllStates();
        states.Should().BeEquivalentTo(Expected.Keys,
            "новое состояние фичи обязано получить явное решение «занимает ли слот» (и строку здесь)");
        foreach (var state in states)
            FeatureProjector.OccupiesSlot(RunIn(state)).Should().Be(Expected[state], $"state={state}");
    }

    [Theory]
    [InlineData(FeatureStates.Merged)]
    [InlineData(FeatureStates.Closed)]
    [InlineData(FeatureStates.MergePending)]
    [InlineData(FeatureStates.Merging)]
    [InlineData(FeatureStates.Paused)]
    [InlineData(FeatureStates.Draft)]
    public void OccupiesSlot_НеЗанимает(string state) =>
        FeatureProjector.OccupiesSlot(RunIn(state)).Should().BeFalse();

    [Theory]
    [InlineData(FeatureStates.Stopped)]
    [InlineData(FeatureStates.Waiting)]
    [InlineData(FeatureStates.Running)]
    [InlineData(FeatureStates.Idle)]
    [InlineData(FeatureStates.NotStarted)]
    [InlineData("что-то-новое")]
    [InlineData("")]
    public void OccupiesSlot_Занимает_ВключаяНеизвестное(string state) =>
        FeatureProjector.OccupiesSlot(RunIn(state)).Should().BeTrue("fail-closed: неизвестное занимает слот");

    // ── Order / queue (И5) ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Order_Fifo_ПоHoldSince()
    {
        var late = Queued("a-late", T0.AddHours(2));
        var early = Queued("z-early", T0);
        var mid = Queued("m-mid", T0.AddHours(1));

        FeatureQueuePolicy.Order([late, early, mid]).Select(r => r.FeatureId)
            .Should().Equal("z-early", "m-mid", "a-late");
    }

    [Fact]
    public void Order_РавныйSince_ПоIdЭпика()
    {
        var b = Queued("epic-b", T0);
        var a = Queued("epic-a", T0);

        FeatureQueuePolicy.Order([b, a]).Select(r => r.FeatureId).Should().Equal("epic-a", "epic-b");
    }

    [Fact]
    public void Order_ПустаяКоллекция_Пусто() =>
        FeatureQueuePolicy.Order([]).Should().BeEmpty();

    // Не в очереди: manual-пауза, черновик, фича без удержания — в Order не входят.
    [Fact]
    public void Order_ТолькоQueue_ManualDraftИБезУдержанияОтброшены()
    {
        TaskItem Held(string id, string? reason)
        {
            var e = new TaskItem { Id = id, Role = TaskRoles.Feature, ProjectId = "p1" };
            if (reason is not null) e.Hold = new FeatureHold(reason, T0, null, true);
            return e;
        }
        var runs = new[]
        {
            Held("manual", FeatureHoldReasons.Manual),
            Held("draft", FeatureHoldReasons.Draft),
            Held("free", null),
            Held("queued", FeatureHoldReasons.Queue),
        }.Select(e => FeatureProjector.Build(e, [e])).ToList();

        FeatureQueuePolicy.Order(runs).Select(r => r.FeatureId).Should().Equal("queued");
    }

    // Позиция 1 — у той фичи, которую Order ставит первой; очередь считается по проекту.
    [Fact]
    public void Positions_ПерваяПоOrder_ИмеетПозицию1_ОчередьПоПроекту()
    {
        var first = Queued("f-first", T0);
        var second = Queued("f-second", T0.AddMinutes(5));
        var third = Queued("f-third", T0.AddMinutes(9));
        var otherProject = Queued("f-other", T0.AddMinutes(1), projectId: "p2");

        var positions = FeatureQueuePolicy.Positions([third, otherProject, first, second]);

        positions[FeatureQueuePolicy.Order([first, second, third])[0].FeatureId]
            .Should().Be(new FeatureQueueInfo(1, 3));
        positions["f-second"].Should().Be(new FeatureQueueInfo(2, 3));
        positions["f-third"].Should().Be(new FeatureQueueInfo(3, 3));
        positions["f-other"].Should().Be(new FeatureQueueInfo(1, 1), "очередь другого проекта — своя");
    }

    [Fact]
    public void Positions_ПустаяКоллекция_Пусто() =>
        FeatureQueuePolicy.Positions([]).Should().BeEmpty();

    // Строка индекса несёт queue; у фичи не в очереди — null. Позиция не зависит от выборки:
    // очередь проекта достраивается из задач владельца.
    [Fact]
    public void Индекс_Queue_ПозицияИДлина_УНеОчередиNull()
    {
        var a = QueuedEpic("epic-a", T0);
        var b = QueuedEpic("epic-b", T0.AddMinutes(1));
        var free = new TaskItem { Id = "epic-free", Role = TaskRoles.Feature, ProjectId = "p1" };

        var index = FeatureProjector.BuildIndexCore([free, b, a], Array.Empty<FeatureIndexSession>());

        index.Features.Single(r => r.FeatureId == "epic-a").Queue.Should().Be(new FeatureQueueInfo(1, 2));
        index.Features.Single(r => r.FeatureId == "epic-b").Queue.Should().Be(new FeatureQueueInfo(2, 2));
        index.Features.Single(r => r.FeatureId == "epic-free").Queue.Should().BeNull();
    }

    // ── Limit ───────────────────────────────────────────────────────────────────────────────

    private static IConfiguration Cfg(string? value) => new ConfigurationBuilder()
        .AddInMemoryCollection(value is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?> { [FeatureQueuePolicy.LimitConfigKey] = value })
        .Build();

    [Theory]
    [InlineData(null, 1)]
    [InlineData("0", 1)]
    [InlineData("-3", 1)]
    [InlineData("abc", 1)]
    [InlineData("1", 1)]
    [InlineData("3", 3)]
    public void Limit_ЧитаетКонфиг_НижеЕдиницыИМусор_ЭтоЕдиница(string? value, int expected) =>
        FeatureQueuePolicy.Limit(Cfg(value)).Should().Be(expected);

    [Fact]
    public void Limit_БезКонфига_Единица() =>
        FeatureQueuePolicy.Limit(null).Should().Be(1);

    // ── И9: merge_pending уступает слот, при этом у фичи остаётся merge_pending ─────────────

    [Fact]
    public void ФичаНаМерже_СлотНеЗанимает_СостояниеОстаётсяMergePending()
    {
        var epic = new TaskItem { Id = "epic-mp", Role = TaskRoles.Feature };
        var final = new TaskItem
        {
            Id = "f1", FeatureId = "epic-mp", Role = TaskRoles.Final, Status = TaskItemStatus.Done,
            CompletedAt = T0,
            Verification = new TaskVerification { Verdict = TaskVerdicts.Accepted },
        };

        var run = FeatureProjector.Build(epic, [epic, final]);

        run.State.Should().Be(FeatureStates.MergePending);
        FeatureProjector.OccupiesSlot(run).Should().BeFalse("финал принят — слот уступлен очереди");
    }
}
