using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// S2a-6a: автор сообщения в истории чата. Что донёс податель (senderUserId — S2a-1) то и
// пишется в user_message: владелец → владелец, редактор → редактор, подача без отправителя
// → null. Запись идёт через существующий путь истории (OnUserMessage под _lock, диск — под
// _saveLock аккумулятора); своего лока и второго writer здесь нет.
public partial class SessionManagerTests
{
    private static string? AuthorOfMessage(List<StoredMessage> history, string text) =>
        ((StoredUserMessage)history.Single(m => m is StoredUserMessage u && u.Text == text)).SenderUserId;

    private async Task DrainNextForRaceAsync(string sessionId) =>
        await (Task)typeof(SessionManager).GetMethod("DrainNextPendingAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(_sut, [sessionId])!;

    [Fact]
    public async Task СообщенияУчастников_ПишутВИсторию_СвоимАвтором_ПодачаБезОтправителя_Null()
    {
        var (session, fake) = await RunChatAsync("ma-authors", SessionStatus.Active);
        var project = _projectManager.GetById(session.ProjectId!)!;
        project.Members = [new ProjectMember { UserId = Участник, Role = ProjectRole.Editor, AddedBy = TestUserId }];

        // Свободный чат: каждое сообщение — свой прямой ход (SendDirectAsync → OnUserMessage)
        await _sut.SendMessageAsync(session.Id, "от владельца", [], senderUserId: TestUserId);
        EndTurn(session, fake);
        await _sut.SendMessageAsync(session.Id, "от редактора", [], senderUserId: Участник);
        EndTurn(session, fake);
        // Агентская подача без отправителя (REST-канал без user id) — автора нет
        await _sut.SendMessageAndWaitAsync(session.Id, "от агента", TimeSpan.Zero);
        EndTurn(session, fake);

        // Снимок на диск — существующим путём сохранения (SaveSnapshotAsync под _saveLock)
        await GetAccumulator(GetEntry(session.Id)).SaveSnapshotAsync(_historyService);

        var history = await _historyService.LoadAsync(session.Id);
        AuthorOfMessage(history, "от владельца").Should().Be(TestUserId, "сообщение владельца — автор владелец");
        AuthorOfMessage(history, "от редактора").Should().Be(Участник, "сообщение редактора — автор редактор");
        AuthorOfMessage(history, "от агента").Should().BeNull("подача без отправителя — автора в истории нет");
    }

    // Гонка двух отправителей в одну сессию: пока идёт ход первого, оба пишут в очередь
    // одновременно; после разбора каждое сообщение в истории со своим автором —
    // перепутать их при доставке и записи нельзя.
    [Fact]
    public async Task ГонкаДвухОтправителей_ОбаСообщенияВИстории_КаждыйСоСвоимАвтором()
    {
        var (session, fake) = await RunChatAsync("ma-race", SessionStatus.Active);
        await _sut.SendMessageAsync(session.Id, "первый ход", [], senderUserId: TestUserId);
        session.Status = SessionStatus.Working; // ход в полёте: все участники гонки идут в очередь

        var start = new TaskCompletionSource();
        var senders = new[] { Участник, ДругойУчастник };
        var sends = Enumerable.Range(0, 4).Select(i => Task.Run(async () =>
        {
            await start.Task;
            await _sut.SendMessageAsync(session.Id, $"сообщение {i}", [], senderUserId: senders[i % 2]);
        })).ToArray();
        start.SetResult();
        await Task.WhenAll(sends);

        // Конец первого хода, затем разбор очереди по одному сообщению (как по result):
        // каждое доставляется своим ходом со своим подателем
        for (var i = 0; i < 4; i++)
        {
            EndTurn(session, fake);
            await DrainNextForRaceAsync(session.Id);
        }

        await GetAccumulator(GetEntry(session.Id)).SaveSnapshotAsync(_historyService);

        var history = await _historyService.LoadAsync(session.Id);
        history.OfType<StoredUserMessage>().Should().HaveCount(5, "ни одно сообщение гонки не потеряно");
        AuthorOfMessage(history, "первый ход").Should().Be(TestUserId);
        for (var i = 0; i < 4; i++)
            AuthorOfMessage(history, $"сообщение {i}").Should().Be(senders[i % 2],
                "автор не перепутан между участниками");
    }
}
