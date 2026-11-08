using ClaudeHomeServer.Models;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Лок сохранения sessions.json переезжает в SessionStore (S3-1) без смены семантики.
// Тест: параллельные сохранения разных сессий — после перезагрузки на диске обе правки.
// Без лока две записи делят один временный файл (JsonFileStore.Save), и проигравшая
// запись молча теряется (ошибка уходит в консоль): последний писатель мог сериализовать
// реестр раньше правки соседа.
public class SessionStoreSaveLockTests : SessionManagerTestBase
{
    [Fact]
    public async Task ПараллельныеСохранения_РазныхСессий_ОбеПравкиНаДискеПослеПерезагрузки()
    {
        const int sessionsCount = 8;
        const int rounds = 60;
        var chats = new List<Session>();
        for (var i = 0; i < sessionsCount; i++)
        {
            var owner = _userStore.Add($"saver-{i}-" + Guid.NewGuid().ToString("N")[..8], "pw-123456", "user");
            chats.Add(await _sut.CreateChatAsync(owner.Id, ClaudeMode.Auto));
        }

        var go = new ManualResetEventSlim(false);
        var workers = chats.Select((chat, idx) => Task.Run(() =>
        {
            go.Wait();
            for (var r = 0; r < rounds; r++)
            {
                chat.Name = $"имя-{idx}-{r}";
                _sut.SaveSessions();
            }
        })).ToArray();
        go.Set();
        await Task.WhenAll(workers);

        var reloaded = BuildSecondManager();
        for (var i = 0; i < sessionsCount; i++)
        {
            reloaded.GetById(chats[i].Id)!.Name.Should().Be($"имя-{i}-{rounds - 1}",
                "последняя правка каждой сессии должна пережить гонку сохранений");
        }
    }
}
