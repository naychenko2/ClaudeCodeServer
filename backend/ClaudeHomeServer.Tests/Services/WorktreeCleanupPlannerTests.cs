using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Выбор кандидатов уборки: чистая логика. Цена ошибки несимметрична — лишний пропуск стоит
// гигабайты на диске, лишняя чистка живого дерева стоит работающей сборки или потерянных правок.
public class WorktreeCleanupPlannerTests
{
    private static readonly DateTime Now = new(2026, 10, 26, 3, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Idle = TimeSpan.FromHours(24);

    private static WorktreeFacts Idle25h(Func<WorktreeFacts, WorktreeFacts>? tweak = null)
    {
        var f = new WorktreeFacts("/w/a", IsPrimary: false, Exists: true, Locked: false, Busy: false,
            LastActivityUtc: Now.AddHours(-25), HasArtifacts: true);
        return tweak?.Invoke(f) ?? f;
    }

    [Fact]
    public void ОсновноеДерево_ПропускаетсяДажеЕслиПростаивает()
    {
        var d = WorktreeCleanupPlanner.Decide(Idle25h(f => f with { IsPrimary = true }), Now, Idle, true);
        d.Action.Should().Be(CleanupAction.Skip);
    }

    [Fact]
    public void ДеревоСПроцессом_Пропускается()
    {
        var d = WorktreeCleanupPlanner.Decide(Idle25h(f => f with { Busy = true }), Now, Idle, true);
        d.Action.Should().Be(CleanupAction.Skip);
        d.Reason.Should().Contain("процесс");
    }

    [Fact]
    public void НедавноПравленноеДерево_Пропускается()
    {
        var d = WorktreeCleanupPlanner.Decide(
            Idle25h(f => f with { LastActivityUtc = Now.AddHours(-23) }), Now, Idle, true);
        d.Action.Should().Be(CleanupAction.Skip);
    }

    [Fact]
    public void ПростаивающееДерево_ЧиститАртефакты()
    {
        var d = WorktreeCleanupPlanner.Decide(Idle25h(), Now, Idle, true);
        d.Action.Should().Be(CleanupAction.CleanArtifacts);
    }

    [Fact]
    public void ПростаивающееБезАртефактов_Пропускается()
    {
        var d = WorktreeCleanupPlanner.Decide(Idle25h(f => f with { HasArtifacts = false }), Now, Idle, true);
        d.Action.Should().Be(CleanupAction.Skip);
    }

    [Fact]
    public void ВлитоеЧистоеПростаивающее_Снимается()
    {
        var d = WorktreeCleanupPlanner.Decide(
            Idle25h(f => f with { IsMerged = true, IsClean = true }), Now, Idle, true);
        d.Action.Should().Be(CleanupAction.RemoveWorktree);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(true, null)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void НеПодтверждённоВлитоеИлиГрязное_НеСнимается(bool? merged, bool? clean)
    {
        var d = WorktreeCleanupPlanner.Decide(
            Idle25h(f => f with { IsMerged = merged, IsClean = clean }), Now, Idle, true);
        d.Action.Should().Be(CleanupAction.CleanArtifacts);
    }

    [Fact]
    public void ЗаблокированноеИлиСОткрытойЗадачей_НеСнимается()
    {
        var merged = Idle25h(f => f with { IsMerged = true, IsClean = true });
        WorktreeCleanupPlanner.Decide(merged with { Locked = true }, Now, Idle, true)
            .Action.Should().Be(CleanupAction.CleanArtifacts);
        WorktreeCleanupPlanner.Decide(merged with { HasOpenTask = true }, Now, Idle, true)
            .Action.Should().Be(CleanupAction.CleanArtifacts);
    }

    [Fact]
    public void СнятиеВыключено_ВлитоеТолькоЧистится()
    {
        var d = WorktreeCleanupPlanner.Decide(
            Idle25h(f => f with { IsMerged = true, IsClean = true }), Now, Idle, removeMerged: false);
        d.Action.Should().Be(CleanupAction.CleanArtifacts);
    }

    [Theory]
    [InlineData(2, 6, 3, true)]
    [InlineData(2, 6, 6, false)]
    [InlineData(22, 4, 23, true)]
    [InlineData(22, 4, 1, true)]
    [InlineData(22, 4, 12, false)]
    public void НочноеОкно_УчитываетПереходЧерезПолночь(int from, int to, int hour, bool expected)
    {
        var o = new WorktreeCleanupOptions { NightFromHour = from, NightToHour = to };
        o.IsNight(new DateTime(2026, 10, 26, hour, 0, 0)).Should().Be(expected);
    }

    [Fact]
    public void Проба_СовпадаетПоГраницеКомпонентаПути()
    {
        var procs = new List<(string?, string)>
        {
            ("/w/ClaudeCodeServer-ab/backend", ""),
            (null, "dotnet /w/ClaudeCodeServer-x/backend/A/bin/Debug/net10.0/A.dll"),
        };
        ProcWorktreeProcessProbe.Matches(procs, "/w/ClaudeCodeServer-a").Should().BeFalse();
        ProcWorktreeProcessProbe.Matches(procs, "/w/ClaudeCodeServer-ab").Should().BeTrue();
        ProcWorktreeProcessProbe.Matches(procs, "/w/ClaudeCodeServer-x").Should().BeTrue();
    }
}
