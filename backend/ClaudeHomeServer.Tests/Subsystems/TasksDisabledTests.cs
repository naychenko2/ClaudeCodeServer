using System.Reflection;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Tasks;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Subsystems;

// Подсистема задач отключаемая (Subsystems:tasks:Enabled=false, ADR-014): TaskManager в контейнер
// не попадает, а ReviewTreePool регистрируется безусловно. Раньше пул требовал TaskManager
// обязательным параметром — стартовый RestoreLeasesAsync ронял хост, а удаление чата глотало
// исключение резолва, и worktree оставался на диске. Хост целиком с выключенными задачами не
// поднимается и по другим причинам (TeamWaveService, TaskLookupAdapter), поэтому здесь —
// точечная проверка конструктора без DI, как у NotesSubsystemGateTests.
public class TasksDisabledTests
{
    [Fact]
    public void ReviewTreePool_CtorParam_TaskManager_IsOptionalWithNullDefault()
    {
        var ctor = typeof(ReviewTreePool).GetConstructors(BindingFlags.Public | BindingFlags.Instance).Single();
        var param = ctor.GetParameters().SingleOrDefault(p => p.ParameterType == typeof(TaskManager));

        param.Should().NotBeNull("пул принимает TaskManager необязательным шлюзом к задачам");
        param!.HasDefaultValue.Should().BeTrue(
            "иначе контейнер не построит пул при выключенной подсистеме задач и уронит старт хоста");
        param.DefaultValue.Should().BeNull();
    }
}
