using ClaudeHomeServer.Models;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Тёплый перенос в новый чат (docs/features/chat-handoff.md): чат локального проекта при
// офлайн-устройстве тёплым путём не идёт — ход передачи припарковался бы до возвращения
// устройства, а человек ждёт записку сейчас. Остальные предусловия — ChatsControllerHandoffTests.
public sealed class ChatHandoffWarmDeviceTests : IDisposable
{
    private readonly DeviceWaitTestHost _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task ЛокальныйПроект_УстройствоОфлайн_ТёплыйНельзя_ОнлайнМожно()
    {
        var (_, project) = _h.LocalProject();
        var chat = await _h._sessions.CreateAsync(project.Id, ClaudeMode.Auto, name: "Локальный чат");
        _h.StubProcess(chat);
        chat.ClaudeSessionId = "csid-local";

        _h._sessions.WarmHandoffBlocker(chat.Id).Should().Be("устройство проекта офлайн");

        _h._gate.GoOnline();
        _h._sessions.WarmHandoffBlocker(chat.Id).Should().BeNull();
    }
}
