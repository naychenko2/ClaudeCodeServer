using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Живой инцидент 2026-08-03: Interrupt() считал, что смерть процесса хода убивает и
// Workflow-агентов, и хоронил карточку немедленным «прерван» — а DeepSeek-прогон дописал
// файлы и прошёл verify уже ПОСЛЕ этого. NoteOwnerProcessGone заменил немедленный абort
// коротким окном проверки — тесты на её главный контракт (см. WorkflowWatcher.cs).
public class WorkflowWatcherTests
{
    [Fact]
    public async Task NoteOwnerProcessGone_БезАктивности_ЗавершаетWorkflowПослеОкна()
    {
        // Детерминированный сигнал вместо слепой паузы: окно проверки истекает в коллбэке
        // Timer, а тот идёт через ThreadPool. На загруженном CI (параллельные тесты с
        // WebApplicationFactory) пул голодает, и коллбэк опаздывает сильнее любой
        // фиксированной задержки — отсюда flaky-провал «collection is empty».
        var sent = new TaskCompletionSource<ServerMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new WorkflowWatcher("несуществующий-путь-xyz", "tool-1",
            m => { sent.TrySetResult(m); return Task.CompletedTask; },
            ownerGoneGrace: TimeSpan.FromMilliseconds(30));

        watcher.NoteOwnerProcessGone();

        var finished = await Task.WhenAny(sent.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        finished.Should().BeSameAs(sent.Task, "после окна без активности ватчер обязан прислать финальный workflow_progress");

        var progress = (await sent.Task).Should().BeOfType<WorkflowProgressMessage>().Subject;
        progress.ToolUseId.Should().Be("tool-1");
        progress.IsDone.Should().BeTrue();
    }

    [Fact]
    public void NoteOwnerProcessGone_ПовторныйВызов_НеПадает()
    {
        var watcher = new WorkflowWatcher("несуществующий-путь-xyz", "tool-1",
            _ => Task.CompletedTask, ownerGoneGrace: TimeSpan.FromMinutes(5));

        var act = () => { watcher.NoteOwnerProcessGone(); watcher.NoteOwnerProcessGone(); };
        act.Should().NotThrow();
    }

    [Fact]
    public async Task NoteOwnerProcessGone_ПослеDispose_НичегоНеШлёт()
    {
        var messages = new List<ServerMessage>();
        var watcher = new WorkflowWatcher("несуществующий-путь-xyz", "tool-1",
            m => { messages.Add(m); return Task.CompletedTask; },
            ownerGoneGrace: TimeSpan.FromMilliseconds(30));
        watcher.Dispose();

        watcher.NoteOwnerProcessGone();
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        messages.Should().BeEmpty();
    }
}
