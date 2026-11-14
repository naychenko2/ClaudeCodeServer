using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Tasks;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// И4 (B3): исполнение читает ТОЛЬКО слепок этапа (task.Stage) и подтверждённый маршрут эпика,
/// шаблон процесса — никогда. Правка шаблона после создания задачи не должна менять ни промпт,
/// ни модель, ни права, ни порог простоя уже созданных задач. Сторож — на IL: ни тело, ни
/// сигнатуры (включая вложенные типы и лямбды) не ссылаются на стор шаблонов.
/// </summary>
public class StageSnapshotIsolationGuardTests
{
    [Theory]
    [InlineData(typeof(TaskExecutionService))]
    [InlineData(typeof(ModelAssignmentResolver))]
    public void ИсполнениеЗадачи_НеСсылаетсяНаСторШаблонов(Type reader)
    {
        var referenced = BoundaryIlScanner.CollectAllReferencedTypes(reader).Distinct().ToList();

        referenced.Should().NotContain(typeof(WorkflowTemplateStore),
            $"{reader.Name} читает только task.Stage (И4): шаблон после создания задачи на исполнение не влияет");
    }
}
