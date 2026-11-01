using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Composition;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.Composition;

// S1-5: проектные рассылки идут группе проекта (участники общего проекта), личные — владельцу.
public class ProjectBroadcastRoutingTests
{
    private sealed class Spy : ISessionBroadcaster
    {
        public List<(string kind, string id)> Calls { get; } = [];
        public Task ToSession(string sessionId, ServerMessage message) { Calls.Add(("session", sessionId)); return Task.CompletedTask; }
        public Task ToSessionExcept(string sessionId, string exceptConnectionId, ServerMessage message) { Calls.Add(("session", sessionId)); return Task.CompletedTask; }
        public Task ToOwner(string ownerId, ServerMessage message) { Calls.Add(("owner", ownerId)); return Task.CompletedTask; }
        public Task ToProject(string projectId, ServerMessage message) { Calls.Add(("project", projectId)); return Task.CompletedTask; }
        public Task ToPreviewLog(string projectId, string serviceId, ServerMessage message) { Calls.Add(("log", projectId)); return Task.CompletedTask; }
    }

    private static readonly ServerMessage Msg = new TaskChangedMessage("updated", new TaskItem { Id = "t1" });

    [Fact]
    public async Task ToTaskBoard_ЗадачаПроекта_УчастникамГруппыИВладельцу()
    {
        var spy = new Spy();

        await spy.ToTaskBoard("owner-1", "proj-1", Msg);

        spy.Calls.Should().Equal(("project", "proj-1"), ("owner", "owner-1"));
    }

    [Fact]
    public async Task ToTaskBoard_ЛичнаяЗадача_ТолькоВладельцу_ГруппаПроектаНеТрогается()
    {
        var spy = new Spy();

        await spy.ToTaskBoard("owner-1", null, Msg);
        await spy.ToTaskBoard("owner-1", "", Msg);

        spy.Calls.Should().Equal(("owner", "owner-1"), ("owner", "owner-1"));
    }

    private sealed class Capture : ISessionBroadcaster
    {
        public ServerMessage? ToProjectMsg, ToOwnerMsg;
        public Task ToSession(string sessionId, ServerMessage message) => Task.CompletedTask;
        public Task ToSessionExcept(string sessionId, string exceptConnectionId, ServerMessage message) => Task.CompletedTask;
        public Task ToOwner(string ownerId, ServerMessage message) { ToOwnerMsg = message; return Task.CompletedTask; }
        public Task ToProject(string projectId, ServerMessage message) { ToProjectMsg = message; return Task.CompletedTask; }
        public Task ToPreviewLog(string projectId, string serviceId, ServerMessage message) => Task.CompletedTask;
    }

    [Fact]
    public async Task ToTaskBoard_ГруппеПроектаОблегчённаяКопия_ВладельцуПолная_ИсходникНеМутирует()
    {
        var cap = new Capture();
        var task = new TaskItem { Id = "t1", Title = "T", Description = "секрет-описание", ResultMarkdown = "секрет-итог" };

        await cap.ToTaskBoard("owner-1", "proj-1", new TaskChangedMessage("updated", task));

        var forProject = ((TaskChangedMessage)cap.ToProjectMsg!).Task;
        forProject.Description.Should().BeEmpty();
        forProject.ResultMarkdown.Should().BeNull();
        forProject.Title.Should().Be("T");
        var forOwner = ((TaskChangedMessage)cap.ToOwnerMsg!).Task;
        forOwner.Should().BeSameAs(task);
        forOwner.Description.Should().Be("секрет-описание");
        forOwner.ResultMarkdown.Should().Be("секрет-итог");
        task.Description.Should().Be("секрет-описание", "облегчается копия, а не исходная карточка");
    }

    // Тот же формат id заметки, что кодирует NotesService.EncodeId: base64url «source|путь».
    private static string NoteIdOf(string sourceKey, string relPath) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{sourceKey}|{relPath}"))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>S2c-9 доработка (п.1 вердикта): WS-копия для группы проекта идёт той же проекцией,
    /// что список REST (TaskView.ForMemberListItem): id заметки личного vault закрыт (он раскрывает
    /// участнику путь и название личной заметки владельца), заметка этого проекта остаётся, поля
    /// облегчённого элемента сохранены. Владельцу — исходное сообщение, хранимая карточка не правится.</summary>
    [Fact]
    public async Task ToTaskBoard_ГруппеПроектаЗакрытSourceNoteIdЧужогоVaultа_ЗаметкаПроектаОстаётся()
    {
        var personalNote = NoteIdOf("personal", "Личная/заметка.md");
        var projectNote = NoteIdOf("proj-1", "заметка-проекта.md");

        var cap = new Capture();
        var task = new TaskItem
        {
            Id = "t1", Title = "T", ProjectId = "proj-1",
            Description = "секрет-описание", ResultMarkdown = "секрет-итог",
            SourceNoteId = personalNote,
        };
        await cap.ToTaskBoard("owner-1", "proj-1", new TaskChangedMessage("updated", task));

        var forProject = ((TaskChangedMessage)cap.ToProjectMsg!).Task.Should().BeOfType<TaskListItem>().Subject;
        // поля элемента списка на месте: фронт по listItem догружает карточку, маркеры и превью — она же
        forProject.ListItem.Should().BeTrue();
        forProject.HasDescription.Should().BeTrue();
        forProject.HasResult.Should().BeTrue();
        forProject.DescriptionPreview.Should().Be("секрет-описание");
        forProject.SourceNoteId.Should().BeNull(
            "id личного vault раскрывает участнику путь и название личной заметки владельца");

        var forOwner = ((TaskChangedMessage)cap.ToOwnerMsg!).Task;
        forOwner.SourceNoteId.Should().Be(personalNote, "владельцу уходит исходное сообщение без изменений");
        task.SourceNoteId.Should().Be(personalNote, "маска правит копию, а не хранимую карточку");

        // своя заметка этого проекта: участник читает заметки проекта и так — маска бьёт только чужой источник
        var capOwn = new Capture();
        await capOwn.ToTaskBoard("owner-1", "proj-1", new TaskChangedMessage("updated",
            new TaskItem { Id = "t2", Title = "T", ProjectId = "proj-1", SourceNoteId = projectNote }));
        ((TaskChangedMessage)capOwn.ToProjectMsg!).Task.SourceNoteId.Should().Be(projectNote);
    }

    [Fact]
    public async Task ToProjectOrOwner_СПроектом_ТолькоГруппеПроекта_БезПроекта_Владельцу()
    {
        var spy = new Spy();

        await spy.ToProjectOrOwner("owner-1", "proj-1", Msg);
        await spy.ToProjectOrOwner("owner-1", null, Msg);

        spy.Calls.Should().Equal(("project", "proj-1"), ("owner", "owner-1"));
    }
}
