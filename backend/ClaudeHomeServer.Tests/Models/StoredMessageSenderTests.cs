using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Models;

// S2a-6a: автор сообщения в history.json. Поле необязательное: без автора JSON остаётся
// прежним по форме (null не пишется), старая история без поля читается штатно —
// совместимость формата, а не новая схема хранения.
public class StoredMessageSenderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ChatHistoryService _sut;

    public StoredMessageSenderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "sender_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _sut = new ChatHistoryService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json")
            }).Build());
    }

    public void Dispose() => Helpers.TestFs.DeleteDirectoryResilient(_tempDir);

    [Fact]
    public async Task СтараяИсторияБезАвтора_ЧитаетсяБезОшибок_АвторNull()
    {
        // history.json чата, записанного до появления поля: ни ключа, ни null
        var dir = Directory.CreateDirectory(Path.Combine(_tempDir, "sessions", "old-chat"));
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "history.json"), """
            [{"kind":"user_message","text":"прежний запрос","timestamp":1700000000000},
             {"kind":"text","text":"прежний ответ"}]
            """);

        var history = await _sut.LoadAsync("old-chat");

        history.Should().HaveCount(2, "отсутствие поля не роняет ни запись, ни историю целиком");
        history[0].Should().BeOfType<StoredUserMessage>()
            .Which.SenderUserId.Should().BeNull("в старой истории автора нет — и не выдумывается");
    }

    [Fact]
    public async Task СообщениеБезАвтора_ПишетсяБезПоля_САвтором_ПишетсяИЧитаетсяОбратно()
    {
        await _sut.SaveAsync("no-author", [new StoredUserMessage("без автора")]);
        File.ReadAllText(Path.Combine(_tempDir, "sessions", "no-author", "history.json"))
            .Should().NotContain("senderUserId", "null не пишется: форма файла идентична старой истории");
        ((StoredUserMessage)(await _sut.LoadAsync("no-author")).Single()).SenderUserId.Should().BeNull();

        await _sut.SaveAsync("with-author", [new StoredUserMessage("от участника", senderUserId: "u-1")]);
        var raw = File.ReadAllText(Path.Combine(_tempDir, "sessions", "with-author", "history.json"));
        raw.Should().Contain("\"senderUserId\":\"u-1\"");
        ((StoredUserMessage)(await _sut.LoadAsync("with-author")).Single())
            .SenderUserId.Should().Be("u-1");
    }
}
