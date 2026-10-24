using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks.Projector;

namespace ClaudeHomeServer.Tasks.Tests;

// W4-ANB (разрез §4): «тихо» — фича стоит без движения ≥ 24 часов (решение Андрея, 24.10).
// Граница включена: ровно 24 часа — тихо; 23 ч 59 мин — не тихо. Признак живёт в FeatureRun.Quiet
// (единая точка правды), строка индекса берёт его как есть (BuildRow), не пересчитывает.
public class FeatureQuietTests
{
    private static readonly DateTime Now = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

    private static TaskItem Epic(DateTime createdAt) =>
        new TaskItem { Id = "epic-q", Role = TaskRoles.Feature, Title = "тихая фича", CreatedAt = createdAt };

    // Закрытый шаг — последняя активность фичи.
    private static TaskItem ClosedStep(DateTime completedAt) =>
        new TaskItem
        {
            Id = "s1", FeatureId = "epic-q", Role = TaskRoles.Step, Title = "шаг",
            Status = TaskItemStatus.Done,
            ClaudeStartedAt = completedAt - TimeSpan.FromMinutes(10),
            CompletedAt = completedAt,
        };

    // Мутация (критерий c1): «>=» на «>» в условии quiet (FeatureProjector, строка quiet) —
    // этот тест краснеет, тест «меньше порога» остаётся зелёным.
    [Fact]
    public void Quiet_Ровно24Часа_Тихо()
    {
        var epic = Epic(Now - TimeSpan.FromDays(2));
        var run = FeatureProjector.Build(epic, [epic, ClosedStep(Now - TimeSpan.FromHours(24))], Now);

        run.Quiet.Should().BeTrue("граница включена: ровно 24 часа без активности — тихо");
    }

    [Fact]
    public void Quiet_23Часа59Минут_НеТихо()
    {
        var epic = Epic(Now - TimeSpan.FromDays(2));
        var run = FeatureProjector.Build(
            epic, [epic, ClosedStep(Now - TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1))], Now);

        run.Quiet.Should().BeFalse("меньше порога — не тихо");
    }

    [Fact]
    public void Quiet_ЗакрытаяФича_НеТихо()
        // Закрытая фича закончена, а не стоит: сигнал «тихо» к ней не применяется.
    {
        var epic = Epic(Now - TimeSpan.FromDays(3));
        epic.Status = TaskItemStatus.Done;
        var run = FeatureProjector.Build(epic, [epic, ClosedStep(Now - TimeSpan.FromDays(2))], Now);

        run.State.Should().Be(FeatureStates.Closed);
        run.Quiet.Should().BeFalse("closed — не «тихо»");
    }

    [Fact]
    public void Quiet_ИдётЗадача_НеТихо()
    {
        var epic = Epic(Now - TimeSpan.FromDays(3));
        var step = new TaskItem
        {
            Id = "s1", FeatureId = "epic-q", Role = TaskRoles.Step, Title = "шаг",
            Status = TaskItemStatus.InProgress,
            ClaudeStartedAt = Now - TimeSpan.FromHours(30),
        };
        var run = FeatureProjector.Build(epic, [epic, step], Now);

        run.Now.Should().HaveCount(1);
        run.Quiet.Should().BeFalse("работа идёт — это не тишина; зависание — отдельный признак (NudgedAt/StaleAlertedAt)");
    }

    [Fact]
    public void Quiet_НикогдаНеЗапускалась_СчитаетсяССоздания()
    {
        // Активности не было никогда: якорь — создание эпика. Свежий черновик не тихо,
        // молчание дольше суток — тихо.
        var fresh = Epic(Now - TimeSpan.FromHours(1));
        FeatureProjector.Build(fresh, [fresh], Now).Quiet.Should().BeFalse("час от создания — не тихо");

        var old = Epic(Now - TimeSpan.FromHours(25));
        FeatureProjector.Build(old, [old], Now).Quiet.Should().BeTrue("полночного движения нет — тихо с создания");
    }

    // Строка индекса несёт тот же признак, что run (единая точка правды, как bucket/state).
    [Fact]
    public void IndexRow_QuietБерётсяИзRunБезПересчёта()
    {
        var epic = Epic(Now - TimeSpan.FromDays(2));
        var tasks = new List<TaskItem> { epic, ClosedStep(Now - TimeSpan.FromHours(24)) };
        var run = FeatureProjector.Build(epic, tasks, Now);
        var row = FeatureProjector.BuildRow(run, tasks, Array.Empty<FeatureIndexSession>(), 0);

        row.Quiet.Should().Be(run.Quiet, "строка не пересчитывает признак");
        row.Quiet.Should().BeTrue();
    }

    // Разрез §4: FeatureNow несёт отметки сторожа зависшего («зависла» = StaleAlertedAt,
    // предупреждение = NudgedAt) — поля задачи как есть. Мутация: убрать присваивания в
    // nowList (FeatureProjector) — тест краснеет.
    [Fact]
    public void FeatureNow_НесётОтметкиСторожа()
    {
        var epic = Epic(Now - TimeSpan.FromDays(1));
        var step = new TaskItem
        {
            Id = "s1", FeatureId = "epic-q", Role = TaskRoles.Step, Title = "шаг",
            Status = TaskItemStatus.InProgress,
            ClaudeStartedAt = Now - TimeSpan.FromHours(5),
            ExecutorNudgedAt = Now - TimeSpan.FromHours(1),
            ExecutorStaleAlertedAt = Now - TimeSpan.FromMinutes(30),
        };
        var run = FeatureProjector.Build(epic, [epic, step], Now);

        run.Now[0].NudgedAt.Should().Be(Now - TimeSpan.FromHours(1));
        run.Now[0].StaleAlertedAt.Should().Be(Now - TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void FeatureNow_БезОтметок_Nullы()
    {
        var epic = Epic(Now - TimeSpan.FromDays(1));
        var step = new TaskItem
        {
            Id = "s1", FeatureId = "epic-q", Role = TaskRoles.Step, Title = "шаг",
            Status = TaskItemStatus.InProgress,
            ClaudeStartedAt = Now - TimeSpan.FromHours(5),
        };
        var run = FeatureProjector.Build(epic, [epic, step], Now);

        run.Now[0].NudgedAt.Should().BeNull();
        run.Now[0].StaleAlertedAt.Should().BeNull();
    }
}
