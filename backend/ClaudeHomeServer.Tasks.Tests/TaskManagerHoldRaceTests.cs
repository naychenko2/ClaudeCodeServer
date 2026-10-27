using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Tasks.Journal;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tasks.Tests;

// Q7-TAIL1 (минор 1 вердикта FINAL-Q6): снятие удержания атомарно под замком задачи.
// Гонка «человек снимает» (DELETE hold → ReleaseFeatureHold) ↔ «очередь снимает голову»
// (FeatureQueueService → тот же ReleaseFeatureHold): оба участники зовут TaskManager.ClearHold
// одной фичи, и без замка оба проходят проверку Hold до того, как любой её снял.
// Тест держит окно между «посмотрели» и «сделали» тестовыми швами ClearHoldInside/
// ClearHoldContended (образец: FeatureQueueServiceTests.Rebalance_ДваОдновременныхСобытия):
// первый вошедший держится внутри критической секции, пока второй не упрётся в занятый замок
// (contended) либо — без замка — не войдёт внутрь сам (secondInside). Ожидание — события,
// не паузы. Мутация «убрать замок (TryEnter/Enter/Exit в ClearHold)»: второй проходит
// проверку до снятия первым (первый держится в шве до снятия) — гонка кончается либо
// падением проигравшего на чтении уже снятого Hold (NRE в ClearHold), либо двумя
// hold_cleared и maxInside == 2; проверено прогоном: краснеет на обеих формах.
public class TaskManagerHoldRaceTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    private static (TaskManager Tasks, FeatureJournalStore Journal, string Dir) Make()
    {
        var dir = Path.Combine(Path.GetTempPath(), "q7_tail1_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(dir, "projects.json") })
            .Build();
        var journal = new FeatureJournalStore(Path.Combine(dir, "feature-journal"));
        return (new TaskManager(config, journal: journal), journal, dir);
    }

    [Fact]
    public async Task ClearHold_ДваУчастникаОдновременно_СнимаетРовноОдин_ЖурналОдин()
    {
        var (tasks, journal, dir) = Make();
        try
        {
            var epic = tasks.Create(null, "u", new CreateTaskRequest("фича", Role: TaskRoles.Feature));
            tasks.SetHold(epic.Id, FeatureHoldReasons.Manual);

            var contended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var inside = 0;
            var maxInside = 0;
            var entries = 0;
            tasks.ClearHoldContended = () => contended.TrySetResult();
            tasks.ClearHoldInside = () =>
            {
                var now = Interlocked.Increment(ref inside);
                int seen;
                do { seen = Volatile.Read(ref maxInside); }
                while (now > seen && Interlocked.CompareExchange(ref maxInside, now, seen) != seen);
                if (now == 2) secondInside.TrySetResult();
                if (Interlocked.Increment(ref entries) == 1)
                    Task.WhenAny(contended.Task, secondInside.Task).Wait(Watchdog)
                        .Should().BeTrue("второй участник обязан заявиться: занятый замок или вход без замка");
                Interlocked.Decrement(ref inside);
            };

            var first = Task.Run(() => tasks.ClearHold(epic.Id, "first"));
            var second = Task.Run(() => tasks.ClearHold(epic.Id, "second"));
            var results = await Task.WhenAll(first, second).WaitAsync(Watchdog);

            results.Count(r => r is not null).Should().Be(1,
                "ровно один участник снимает удержание; проигравший получает null — ветка 06aae1bf вызывающего");
            maxInside.Should().Be(1, "критическая секция «проверили Hold и сняли» — не больше одного участника");
            tasks.GetById(epic.Id)!.Hold.Should().BeNull("удержание снято победителем");
            journal.Read("u", epic.Id).Entries.Count(e => e.Kind == "hold_cleared")
                .Should().Be(1, "в журнал пишет только победитель: одна запись действия на одну паузу");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
