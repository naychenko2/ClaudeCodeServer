using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tasks.Tests;

// B08: мета списка шаблонов — отличие встроенного от пресета, колонки параллели, число этапов.
public class WorkflowTemplateMetaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"wtm-{Guid.NewGuid():N}");

    public WorkflowTemplateMetaTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* уборка best-effort */ }
    }

    private WorkflowTemplate Builtin(string key = WorkflowTemplateStore.FullCycleKey) =>
        new WorkflowTemplateStore(Path.Combine(_dir, "t.json")).Get($"builtin-{key}", WorkflowTemplateScope.Global)!;

    [Fact]
    public void ВстроенныйБезПравок_Diff0()
    {
        foreach (var key in WorkflowTemplateStore.BuiltInKeys)
            WorkflowTemplateMeta.DiffFromPreset(Builtin(key)).Should().Be(0, key);
    }

    [Fact]
    public void ВстроенныйСПравкойДвухЭтапов_Diff2()
    {
        var t = Builtin();
        t.Stages.First(s => s.Role == TaskRoles.Qa).Presence = WorkflowStagePresence.Off;
        t.Stages.First(s => s.Role == TaskRoles.Design).Prompt = "правка";
        WorkflowTemplateMeta.DiffFromPreset(t).Should().Be(2);
    }

    [Fact]
    public void ЛишнийЭтап_СчитаетсяОтличием_НевстроенныйДаётNull()
    {
        var t = Builtin();
        t.Stages.Add(new WorkflowStage { Role = TaskRoles.Qa, Id = "qa-2", Title = "Ещё" });
        WorkflowTemplateMeta.DiffFromPreset(t).Should().Be(1);

        t.BuiltInKey = null;
        WorkflowTemplateMeta.DiffFromPreset(t).Should().BeNull();
    }

    [Fact]
    public void ПараллельныеКолонки_ЛинейныйШаблон0_ПараллельныйЦикл_Колонки()
    {
        WorkflowTemplateMeta.ParallelColumns(Builtin()).Should().Be(0);
        var parallel = Builtin(WorkflowTemplateStore.ParallelCycleKey);
        // step-back/step-front делят колонку; qa-plan стоит после design — рядом с plan
        WorkflowTemplateMeta.ParallelColumns(parallel).Should().Be(2);
        WorkflowTemplateMeta.StageCount(parallel).Should().Be(parallel.Stages.Count);
    }
}
