using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
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

    // S2d-2a: адресация событий чата

    [Fact]
    public async Task ToChatAudience_ОбщийЧатПроекта_ГруппеПроекта()
    {
        var spy = new Spy();
        await spy.ToChatAudience(new Session { ProjectId = "proj-1", OwnerId = "o1" }, Msg);
        spy.Calls.Should().Equal(("project", "proj-1"));
    }

    [Fact]
    public async Task ToChatAudience_ПриватныйЧат_ТолькоАвтору()
    {
        var spy = new Spy();
        await spy.ToChatAudience(new Session { ProjectId = "proj-1", OwnerId = "o1", PrivateToUserId = "author" }, Msg);
        spy.Calls.Should().Equal(("owner", "author"));
    }

    [Fact]
    public async Task ToChatAudience_ЧатВнеПроекта_Владельцу()
    {
        var spy = new Spy();
        await spy.ToChatAudience(new Session { OwnerId = "o1" }, Msg);
        spy.Calls.Should().Equal(("owner", "o1"));
    }

    [Fact]
    public async Task ToChatAudience_ПриватностьЧитаетсяВМоментОтправки_ПослеСделатьОбщимИдётГруппе()
    {
        var spy = new Spy();
        var s = new Session { ProjectId = "proj-1", OwnerId = "o1", PrivateToUserId = "author" };
        await spy.ToChatAudience(s, Msg);
        s.PrivateToUserId = null;
        await spy.ToChatAudience(s, Msg);
        spy.Calls.Should().Equal(("owner", "author"), ("project", "proj-1"));
    }

    // S2d-3f (дефект b9a2601f): перегрузка ToChatAudience по id чата. В проде ISessionDirectory
    // зарегистрирован (Program.cs) и живой утечки нет — но при каталоге, которого нет, прежний
    // код откатывался к ToProjectOrOwner: событие с id приватного чата уходило группе проекта.
    // Это ровно та утечка, что закрывали S2d-2a/2b/2c; правка — fail-closed в одном месте Core-шва.

    private sealed class DirectoryStub : ISessionDirectory
    {
        private readonly Dictionary<string, Session> _map;
        public DirectoryStub(params Session[] sessions) => _map = sessions.ToDictionary(s => s.Id, s => s);
        public Session? GetById(string id) => _map.GetValueOrDefault(id);
        public IReadOnlyCollection<Session> GetAll() => _map.Values;
        public string? ResolveOwnerId(Session s) => s.OwnerId;
        public Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId) =>
            Task.FromResult<IReadOnlyList<StoredMessage>>([]);
    }

    [Fact]
    public async Task ToChatAudience_ПоId_КаталогНеВнедрён_FailClosedВладельцу_НеГруппеПроекта()
    {
        var spy = new Spy();
        await spy.ToChatAudience((ISessionDirectory?)null, "o1", "proj-1", "priv-1", Msg);
        // событие с id чата при недоступном каталоге не уходит группе проекта: приватность не раскрываем
        spy.Calls.Should().Equal(("owner", "o1"));
    }

    [Fact]
    public async Task ToChatAudience_БезId_ПрежняяПроектнаяАдресация_СКаталогомИБезНего()
    {
        var spy = new Spy();
        // id нет — событие не про чат: проектная/личная адресация прежняя (это не утечка приватности)
        await spy.ToChatAudience((ISessionDirectory?)null, "o1", "proj-1", null, Msg);
        await spy.ToChatAudience(new DirectoryStub(), "o1", "proj-1", "", Msg);
        await spy.ToProjectOrOwner("o1", null, Msg);
        spy.Calls.Should().Equal(("project", "proj-1"), ("project", "proj-1"), ("owner", "o1"));
    }

    [Fact]
    public async Task ToChatAudience_ПоId_КаталогЕсть_ПриватныйАвтору_ОбщийГруппе_УдалённыйВладельцу()
    {
        var spy = new Spy();
        var dir = new DirectoryStub(
            new Session { Id = "priv-1", ProjectId = "proj-1", OwnerId = "o1", PrivateToUserId = "author" },
            new Session { Id = "open-1", ProjectId = "proj-1", OwnerId = "o1" });
        await spy.ToChatAudience(dir, "o1", "proj-1", "priv-1", Msg);
        await spy.ToChatAudience(dir, "o1", "proj-1", "open-1", Msg);
        // id указан, чата уже нет (удалён) — только владельцу: это поведение было fail-closed и до правки
        await spy.ToChatAudience(dir, "o1", "proj-1", "gone-1", Msg);
        spy.Calls.Should().Equal(("owner", "author"), ("project", "proj-1"), ("owner", "o1"));
    }
}
