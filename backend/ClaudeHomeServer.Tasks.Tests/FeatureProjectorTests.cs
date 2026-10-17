using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks.Projector;
using ClaudeHomeServer.Tasks.Tests.Fixtures;

namespace ClaudeHomeServer.Tasks.Tests;

// Золотые тесты FeatureProjector (режим «Исполнение фичи»,
// docs/research/turn-plan-feature-mode-cut-2026-10.md): 12 фикстур feature-runs/*.json
// (задача 1f4e6a620) — 8 покрываем здесь, полная 12 — отдельный шаг.
//
// Каждый кейс: фикстура → FeatureProjector.Build → сверка конвейера с ЭТАЛОНОМ (числа шагов/
// ревью/проверок, вердикты, ↺, прогресс, готовность). Эталон — ручной срез фикстуры, НЕ вывод
// реализации: мутация Build (например, сброс Steps/Verdicts) краснит все кейсы.
public class FeatureProjectorTests
{
    // Эталон фичи: epic/branch + конвейер (steps=step+rework, reviews, qa, final/merge id) +
    // вердикты (acc/awr/ret/wd) + progress + ready.
    public sealed record Case(
        string Fixture, string EpicId, string Branch,
        int Steps, int Reviews, int Qa, string? FinalId, string? MergeId,
        int Acc, int Awr, int Ret, int Wd, double Progress, bool Ready);

    // Ручной срез фикстур (подсчёт ролей/вердиктов; сверка с expected.json: loops=ret+awr).
    // xUnit MemberData — object[][], каждая строка — один эталон (Case).
    public static readonly object[][] Cases =
    [
        Row("feat-curator-console.json", "epic-curator-console", "feat/curator-console",
            4, 3, 0, null, null, 1, 1, 0, 0, 0.6667, false),
        Row("spike-vdi-ocr.json", "epic-spike-vdi-ocr", "spike/vdi-ocr",
            9, 5, 0, null, null, 2, 0, 3, 0, 1.0, false),
        Row("feat-local-media-history.json", "epic-local-media-history", "feat/local-media-history",
            9, 5, 5, "mlh-f08b44510", null, 2, 0, 6, 0, 0.7273, false),
        Row("fix-local-model-live-ctx-and-stream-speed.json", "epic-local-model-live-ctx-and-stream-speed",
            "fix/local-model-live-ctx-and-stream-speed",
            4, 0, 0, null, null, 0, 0, 0, 0, 0, false),
        Row("fix-local-model-busy-throughput.json", "epic-local-model-busy-throughput",
            "fix/local-model-busy-throughput",
            2, 0, 0, null, null, 0, 0, 0, 0, 0, false),
        Row("feat-gw-hint-headers.json", "epic-gw-hint-headers", "feat/gw-hint-headers",
            1, 0, 0, null, null, 0, 0, 0, 0, 0, false),
        Row("feat-subagent-model-force.json", "epic-subagent-model-force", "feat/subagent-model-force",
            2, 0, 0, null, null, 0, 0, 0, 0, 0, false),
        Row("fix-haiku-1m-family.json", "epic-haiku-1m-family", "fix/haiku-1m-family",
            2, 0, 0, null, null, 0, 0, 0, 0, 0, false),
    ];

    private static object[] Row(string fixture, string epicId, string branch,
        int steps, int reviews, int qa, string? finalId, string? mergeId,
        int acc, int awr, int ret, int wd, double progress, bool ready) =>
        [new Case(fixture, epicId, branch, steps, reviews, qa, finalId, mergeId, acc, awr, ret, wd, progress, ready)];

    [Theory]
    [MemberData(nameof(Cases))]
    public void Build_СовпадаетСЭталоном(Case c)
    {
        var tasks = FeatureRunFixture.Load(c.Fixture);
        var epic = tasks.Single(t => t.Role == TaskRoles.Feature);
        epic.Id.Should().Be(c.EpicId);
        epic.WorktreeBranch.Should().Be(c.Branch);

        var run = FeatureProjector.Build(epic, tasks);

        run.FeatureId.Should().Be(c.EpicId);
        run.Epic.Id.Should().Be(c.EpicId);
        run.Steps.Should().HaveCount(c.Steps);
        run.Reviews.Should().HaveCount(c.Reviews);
        run.Qa.Should().HaveCount(c.Qa);
        run.Final?.Id.Should().Be(c.FinalId);
        run.Merge?.Id.Should().Be(c.MergeId);
        run.Verdicts.Accepted.Should().Be(c.Acc);
        run.Verdicts.AcceptedWithRemarks.Should().Be(c.Awr);
        run.Verdicts.Returned.Should().Be(c.Ret);
        run.Verdicts.Withdrawn.Should().Be(c.Wd);
        run.Verdicts.Loops.Should().Be(c.Ret + c.Awr);
        run.Progress.Should().BeApproximately(c.Progress, 0.0001);
        run.Ready.Should().Be(c.Ready);
    }

    // Прогноз «готово»: финал с accepted/accepted_with_remarks → Ready=true, иначе false.
    // Синтетический кейс (не из фикстур): финал есть и принят.
    [Fact]
    public void Build_ФиналПринят_Gотово()
    {
        var epic = new TaskItem { Id = "epic-x", Role = TaskRoles.Feature };
        var final = new TaskItem
        {
            Id = "f1", FeatureId = "epic-x", Role = TaskRoles.Final,
            Verification = new TaskVerification { Verdict = TaskVerdicts.Accepted },
        };
        var run = FeatureProjector.Build(epic, [epic, final]);

        run.Ready.Should().BeTrue();
        run.Final.Should().NotBeNull();
        run.Final!.Id.Should().Be("f1");
    }

    // Финал с returned → не готов; без финала → не готов.
    [Fact]
    public void Build_ФиналВозвращён_НеГотово()
    {
        var epic = new TaskItem { Id = "epic-y", Role = TaskRoles.Feature };
        var final = new TaskItem
        {
            Id = "f2", FeatureId = "epic-y", Role = TaskRoles.Final,
            Verification = new TaskVerification { Verdict = TaskVerdicts.Returned },
        };
        FeatureProjector.Build(epic, [epic, final]).Ready.Should().BeFalse();
        FeatureProjector.Build(epic, [epic]).Ready.Should().BeFalse();
    }

    // Минор 1: вход Build защищён. Пустой список → пустой конвейер (шагов/ревью/проверок/финала/мержа нет).
    [Fact]
    public void Build_ПустойВход_ПустыеШаги()
    {
        var epic = new TaskItem { Id = "epic-empty", Role = TaskRoles.Feature };
        var run = FeatureProjector.Build(epic, []);

        run.Steps.Should().BeEmpty();
        run.Reviews.Should().BeEmpty();
        run.Qa.Should().BeEmpty();
        run.Final.Should().BeNull();
        run.Merge.Should().BeNull();
    }

    // Минор 1: чужая задача (FeatureId ≠ epic.Id, и не сам эпик) → явный ArgumentException,
    // а не молчаливая фильтрация. Эпик сам (FeatureId=null) чужим не считается.
    [Fact]
    public void Build_ЧужойЭпик_Бросает()
    {
        var epic = new TaskItem { Id = "epic-a", Role = TaskRoles.Feature };
        var foreign = new TaskItem { Id = "f9", FeatureId = "epic-OTHER", Role = TaskRoles.Step };

        ((Action)(() => FeatureProjector.Build(epic, [epic, foreign]))).Should()
            .Throw<ArgumentException>("чужая задача обязана падать явно, а не фильтроваться молча");
    }
}
