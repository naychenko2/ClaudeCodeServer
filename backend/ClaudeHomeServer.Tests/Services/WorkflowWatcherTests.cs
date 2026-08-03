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
        var messages = new List<ServerMessage>();
        var watcher = new WorkflowWatcher("несуществующий-путь-xyz", "tool-1",
            m => { messages.Add(m); return Task.CompletedTask; },
            ownerGoneGrace: TimeSpan.FromMilliseconds(30));

        watcher.NoteOwnerProcessGone();
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        var progress = messages.OfType<WorkflowProgressMessage>().Should().ContainSingle().Subject;
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
