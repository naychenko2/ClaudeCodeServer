using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Composition;

/// <summary>
/// S2c-4: адресация шва notes_changed (NotesHubNotifier). Проектная заметка — группе
/// проекта И вызывающему (та же форма, что ToTaskBoard у доски); личная — только
/// владельцу. Полезная нагрузка одна для обоих адресов: id и действие — текста в
/// сообщении нет вовсе (читателю он открыт через REST).
/// </summary>
public class NotesHubNotifierTests
{
    [Fact]
    public async Task ПроектнаяЗаметка_ГруппеПроектаИВызывающему_ОднимИТемЖеСообщением()
    {
        var broadcaster = new TestSessionBroadcaster();

        await new NotesHubNotifier(broadcaster)
            .BroadcastNotesChangedAsync("user-1", "updated", "note-1", "proj-1");

        var expected = new NotesChangedMessage("updated", "note-1");
        var toProject = broadcaster.Project.Should().ContainSingle().Which;
        toProject.ProjectId.Should().Be("proj-1");
        toProject.Message.Should().Be(expected);

        var toOwner = broadcaster.Owner.Should().ContainSingle().Which;
        toOwner.OwnerId.Should().Be("user-1");
        // Тот же payload, что группе: ни поля, которого Read-доступ участнику не даёт.
        toOwner.Message.Should().Be(expected);
    }

    [Fact]
    public async Task ЛичнаяЗаметка_ТолькоВладельцу_КакРаньше()
    {
        var broadcaster = new TestSessionBroadcaster();

        await new NotesHubNotifier(broadcaster).BroadcastNotesChangedAsync("user-1", "deleted", "note-1");

        broadcaster.Project.Should().BeEmpty("личная заметка не касается группы проекта");
        broadcaster.Owner.Should().ContainSingle().Which.Message
            .Should().Be(new NotesChangedMessage("deleted", "note-1"));
    }

    [Fact]
    public async Task ПустойProjectId_КакЛичная_ТолькоВладельцу()
    {
        var broadcaster = new TestSessionBroadcaster();

        // Вызывающий не смог назвать источник (id не разобрался) — деградация к старому
        // поведению: owner-only, а не рассылка в несуществующую группу.
        await new NotesHubNotifier(broadcaster).BroadcastNotesChangedAsync("user-1", "updated", "note-1", "");

        broadcaster.Project.Should().BeEmpty();
        broadcaster.Owner.Should().ContainSingle();
    }
}
