using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// G1 (разрез docs/research/ai-fabrika-workflow-2-cut-2026-11.md §1.4–1.7): чистые функции графа
// маршрута без хранилища. Форма графа — пресет parallel-cycle из §1.10.
public class WorkflowGraphTests
{
    private static WorkflowStage S(string role, string? id = null, string[]? after = null,
        string presence = WorkflowStagePresence.On, string? lane = null) =>
        new() { Role = role, Id = id, After = after?.ToList(), Presence = presence, Lane = lane };

    // cut → design → {plan, qa-plan}; plan → {step-back, step-front} → review → qa ← qa-plan; qa → final → merge.
    // design и qa-plan — optional: на них держатся тесты обхода снятых этапов.
    private static WorkflowTemplate ParallelCycle() => new()
    {
        Name = "parallel-cycle",
        Stages =
        [
            S(TaskRoles.Cut),
            S(TaskRoles.Design, presence: WorkflowStagePresence.Optional),
            S(TaskRoles.Plan, after: ["design"]),
            S(TaskRoles.Qa, "qa-plan", ["design"], WorkflowStagePresence.Optional),
            S(TaskRoles.Step, "step-back", ["plan"], lane: "back"),
            S(TaskRoles.Step, "step-front", ["plan"], lane: "front"),
            S(TaskRoles.Review, after: ["step-back", "step-front"]),
            S(TaskRoles.Qa, after: ["review", "qa-plan"]),
            S(TaskRoles.Final),
            S(TaskRoles.Merge),
        ],
    };

    private static string[] After(List<RouteStage> route, string id) => route.Single(s => s.Id == id).After.ToArray();

    // ─── Normalize ──────────────────────────────────────────────────────────────

    [Fact]
    public void Normalize_ПустыеИdИAfter_IdРавенРоли_AfterПредыдущийУПервого_Пусто()
    {
        var nodes = WorkflowGraph.Normalize([S(TaskRoles.Plan), S(TaskRoles.Step), S(TaskRoles.Review)]);

        nodes.Select(n => n.Id).Should().Equal("plan", "step", "review");
        nodes[0].After.Should().BeEmpty();
        nodes[1].After.Should().Equal("plan");
        nodes[2].After.Should().Equal("step");
    }

    [Fact]
    public void Normalize_ЯвныйAfterПустой_Корень_ПробелыСрезаются()
    {
        var nodes = WorkflowGraph.Normalize([S(TaskRoles.Plan), S(" step ", " s1 ", []), S(TaskRoles.Review, after: [" s1 "])]);

        nodes[1].Id.Should().Be("s1");
        nodes[1].Role.Should().Be("step");
        nodes[1].After.Should().BeEmpty("явный [] — корень, а не «после предыдущего»");
        nodes[2].After.Should().Equal("s1");
    }

    // ─── Resolve ────────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_НичегоНеСнято_ГрафШаблонаКакЕсть()
    {
        var route = WorkflowGraph.Resolve(ParallelCycle(), []);

        route.Select(s => s.Id).Should().Equal(
            "cut", "design", "plan", "qa-plan", "step-back", "step-front", "review", "qa", "final", "merge");
        After(route, "design").Should().Equal("cut");
        After(route, "qa").Should().Equal("review", "qa-plan");
        route.Single(s => s.Id == "step-front").Lane.Should().Be("front");
    }

    [Fact]
    public void Resolve_СнятOptional_НаследникПолучаетAfterУбранного()
    {
        var route = WorkflowGraph.Resolve(ParallelCycle(), ["qa-plan"]);

        route.Should().NotContain(s => s.Id == "qa-plan");
        After(route, "qa").Should().Equal(["review", "design"], "qa-plan убран: qa наследует его After (design)");
    }

    [Fact]
    public void Resolve_СнятыДваЗвенаПодряд_РёбраОбходятТранзитивно()
    {
        // design и qa-plan убраны: plan и qa наследуют After убранных, развёрнутые через design.
        var route = WorkflowGraph.Resolve(ParallelCycle(), ["design", "qa-plan"]);

        After(route, "plan").Should().Equal("cut");
        After(route, "qa").Should().Equal("review", "cut");
    }

    [Fact]
    public void Resolve_ОбходБезДублей_ОбщийПредокВписанОдинРаз()
    {
        // a → b(opt) → c; c ссылается и на b, и на a: после снятия b у c ровно один «a».
        var template = new WorkflowTemplate
        {
            Name = "т",
            Stages =
            [
                S(TaskRoles.Plan), S(TaskRoles.Design, presence: WorkflowStagePresence.Optional),
                S(TaskRoles.Step, after: ["design", "plan"]),
            ],
        };

        var route = WorkflowGraph.Resolve(template, ["design"]);

        After(route, "step").Should().Equal("plan");
    }

    [Fact]
    public void Resolve_Off_УбираетсяБезСкипа_ИЗаменяетсяРебром()
    {
        var template = new WorkflowTemplate
        {
            Name = "фаст-трек",
            Stages = [S(TaskRoles.Plan), S(TaskRoles.Design, presence: WorkflowStagePresence.Off), S(TaskRoles.Step)],
        };

        var route = WorkflowGraph.Resolve(template, null);

        route.Select(s => s.Id).Should().Equal("plan", "step");
        After(route, "step").Should().Equal("plan");
    }

    [Fact]
    public void Resolve_СкипНесъёмного_Игнорируется()
    {
        var route = WorkflowGraph.Resolve(ParallelCycle(), ["final", "cut", "нет-такого"]);

        route.Select(s => s.Id).Should().Contain(["final", "cut"]);
    }

    // ─── Columns ────────────────────────────────────────────────────────────────

    [Fact]
    public void Columns_ГлубинаЭтоДлиннейшийПутьОтКорня()
    {
        var columns = WorkflowGraph.Columns(WorkflowGraph.Resolve(ParallelCycle(), []));

        columns["cut"].Should().Be(0);
        columns["design"].Should().Be(1);
        columns["plan"].Should().Be(2);
        columns["qa-plan"].Should().Be(2);
        columns["step-back"].Should().Be(3);
        columns["step-front"].Should().Be(3);
        columns["review"].Should().Be(4);
        // qa идёт и после review (4), и после qa-plan (2): длиннейший путь, а не кратчайший.
        columns["qa"].Should().Be(5);
        columns["merge"].Should().Be(7);
    }

    [Fact]
    public void Columns_ПустойМаршрут_Пусто() =>
        WorkflowGraph.Columns([]).Should().BeEmpty();

    // ─── StageFor (§1.5) ────────────────────────────────────────────────────────

    private static List<RouteStage> Route(params string[] skipped) =>
        WorkflowGraph.Resolve(ParallelCycle(), skipped);

    [Fact]
    public void StageFor_ЕдинственныйЭтапРоли_Он()
    {
        var choice = WorkflowGraph.StageFor(Route(), TaskRoles.Review, null, null);

        choice.Ok.Should().BeTrue();
        choice.StageId.Should().Be("review");
    }

    [Fact]
    public void StageFor_РольСнята_ОшибкаСТекстомB2()
    {
        var route = WorkflowGraph.Resolve(new WorkflowTemplate
        {
            Name = "т", Stages = [S(TaskRoles.Step), S(TaskRoles.Qa, presence: WorkflowStagePresence.Optional)],
        }, ["qa"]);

        var choice = WorkflowGraph.StageFor(route, TaskRoles.Qa, null, null);

        choice.Ok.Should().BeFalse();
        choice.Error.Should().StartWith("этап пропущен маршрутом").And.Contain("«qa»");
    }

    [Theory]
    [InlineData("back", "step-back")]
    [InlineData("бэкенд", "step-back")]
    [InlineData("front", "step-front")]
    [InlineData("Фронтенд", "step-front")]
    public void StageFor_НесколькоStep_ВыборПоЛинии(string lane, string expected) =>
        WorkflowGraph.StageFor(Route(), TaskRoles.Step, lane, null).StageId.Should().Be(expected);

    [Fact]
    public void StageFor_НесколькоStep_НетСовпаденияИНетБезЛинии_ОшибкаСоСпискомId()
    {
        // Линия задачи — other, а этапов step с other или без линии нет.
        var choice = WorkflowGraph.StageFor(Route(), TaskRoles.Step, "other", null);

        choice.Ok.Should().BeFalse();
        choice.Error.Should().Contain("step-back").And.Contain("step-front").And.Contain("stageId");
    }

    [Fact]
    public void StageFor_НесколькоStep_НетСовпадения_БерётсяЕдинственныйБезЛинии()
    {
        var route = WorkflowGraph.Resolve(new WorkflowTemplate
        {
            Name = "т",
            Stages = [S(TaskRoles.Step, "s-back", lane: "back"), S(TaskRoles.Step, "s-any", [])],
        }, null);
        // Два корня: второй step сюда не после первого — для StageFor важна лишь линия.

        WorkflowGraph.StageFor(route, TaskRoles.Step, "front", null).StageId.Should().Be("s-any");
        WorkflowGraph.StageFor(route, TaskRoles.Step, "back", null).StageId.Should().Be("s-back");
    }

    [Fact]
    public void StageFor_ДваЭтапаQaБезStageId_Ошибка_НеПервыйПопавшийся()
    {
        var choice = WorkflowGraph.StageFor(Route(), TaskRoles.Qa, null, null);

        choice.Ok.Should().BeFalse();
        choice.Error.Should().Contain("несколько этапов").And.Contain("qa-plan").And.Contain("qa").And.Contain("stageId");
    }

    [Fact]
    public void StageFor_ЯвныйStageId_ВыбираетИменноЕго()
    {
        var choice = WorkflowGraph.StageFor(Route(), TaskRoles.Qa, null, "qa-plan");

        choice.StageId.Should().Be("qa-plan");
    }

    [Fact]
    public void StageFor_ЯвныйStageId_РольНеСовпала_Ошибка()
    {
        var choice = WorkflowGraph.StageFor(Route(), TaskRoles.Review, null, "qa-plan");

        choice.Ok.Should().BeFalse();
        choice.Error.Should().Contain("qa-plan").And.Contain("review");
    }

    [Fact]
    public void StageFor_ЯвныйStageId_НетВМаршруте_Ошибка()
    {
        // qa-plan снят маршрутом: явный id на него — отказ, а не молчаливая подмена.
        var choice = WorkflowGraph.StageFor(Route("qa-plan"), TaskRoles.Qa, null, "qa-plan");

        choice.Ok.Should().BeFalse();
        choice.Error.Should().Contain("не найден");
    }

    [Fact]
    public void StageFor_ПослеСнятияОдногоИзДвухQa_Остаётся_Однозначно()
    {
        WorkflowGraph.StageFor(Route("qa-plan"), TaskRoles.Qa, null, null).StageId.Should().Be("qa");
    }

    // ─── States (§1.7) ──────────────────────────────────────────────────────────

    private static TaskItem T(string stageId, TaskItemStatus status) =>
        new() { Status = status, Stage = new StageSnapshot { StageId = stageId, Role = "x" } };

    [Fact]
    public void States_DoneActivePending()
    {
        var route = Route();
        var states = WorkflowGraph.States(route,
        [
            T("cut", TaskItemStatus.Done), T("design", TaskItemStatus.Done),
            T("plan", TaskItemStatus.Done), T("plan", TaskItemStatus.Done),
            T("step-back", TaskItemStatus.InProgress),
        ]);

        states["cut"].Should().Be("done");
        states["plan"].Should().Be("done");
        states["step-back"].Should().Be("active");
        states["review"].Should().Be("pending");
        states["merge"].Should().Be("pending");
    }

    [Fact]
    public void States_ЭтапСОдной_ЗакрытойИОднойОткрытой_НеDone()
    {
        var states = WorkflowGraph.States(Route(),
            [T("plan", TaskItemStatus.Done), T("plan", TaskItemStatus.Todo)]);

        states["plan"].Should().Be("pending");
    }

    [Fact]
    public void States_Absent_ПоНаследникамВГрафе_ИНеПоСпискуВыше()
    {
        // Фронт начался. Бэк-этап пуст, а его наследники (review…) — тоже пусты: pending, не absent.
        // План тестирования пуст, и его наследник qa не начат: pending.
        var started = WorkflowGraph.States(Route(), [T("step-front", TaskItemStatus.InProgress)]);

        started["step-back"].Should().Be("pending", "наследник step-back — review, он не начат; фронт — не потомок бэка");
        started["qa-plan"].Should().Be("pending", "qa-plan не предок фронта, хотя стоит в списке раньше");

        // Ревью начато: оба step пусты, и review — их потомок → absent.
        var later = WorkflowGraph.States(Route(), [T("review", TaskItemStatus.InProgress)]);

        later["step-back"].Should().Be("absent");
        later["step-front"].Should().Be("absent");
        later["qa-plan"].Should().Be("pending", "потомок qa-plan — qa, он не начат; review ему не потомок");
        later["plan"].Should().Be("absent");
        later["review"].Should().Be("active");
        later["qa"].Should().Be("pending");
    }

    [Fact]
    public void States_Skipped_ОтдаётсяСнятымЭтапам_ЗадачиБезСлепкаНеСчитаются()
    {
        var route = Route("qa-plan");
        var states = WorkflowGraph.States(route, [new TaskItem { Status = TaskItemStatus.InProgress }], ["qa-plan"]);

        states["qa-plan"].Should().Be("skipped");
        states.Values.Should().NotContain("active", "задача без слепка в этап не попадает — её раскладывает проекция");
    }

    // ─── DanglingIds ────────────────────────────────────────────────────────────

    [Fact]
    public void DanglingIds_ВисящаяВетка_Названа_ПоследнийЭтапНе()
    {
        var route = WorkflowGraph.Resolve(new WorkflowTemplate
        {
            Name = "т", Stages = [S(TaskRoles.Plan), S(TaskRoles.Step, after: ["plan"]), S(TaskRoles.Review, after: ["plan"])],
        }, null);

        WorkflowGraph.DanglingIds(route).Should().Equal("step");
    }
}
