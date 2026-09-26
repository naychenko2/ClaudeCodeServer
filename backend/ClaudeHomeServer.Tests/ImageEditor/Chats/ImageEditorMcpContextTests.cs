using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.ImageEditor.Chats;

// Сервер image-editor в конфиге хода (ADR-018 §7): чат картинки — есть, обычный чат — нет,
// флаг владельца выключен — нет. Модуль выключен — тулсета нет в реестре и контекста тоже
// (ImageEditorDisabledTests): иначе CLI получил бы сервер с 404 и «fetch failed» у всего хода.
public class ImageEditorMcpContextTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    private string OwnerId => _factory.Services.GetRequiredService<UserStore>()
        .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

    private Session Chat(bool imageChat) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        OwnerId = OwnerId,
        ProjectId = "p1",
        ImageChat = imageChat ? new SessionImageChat { CurrentPath = "images/hero.png" } : null,
    };

    private void SetFlag(bool on) =>
        _factory.Services.GetRequiredService<UserStore>().SetFeatureFlag(OwnerId, FeatureFlagKeys.ImageEditor, on)
            .Should().BeTrue();

    [Fact]
    public void Чат_картинки_получает_сервер_на_адресе_тулсетов()
    {
        SetFlag(true);
        var sessions = _factory.Services.GetRequiredService<SessionManager>();

        var context = sessions.BuildImageEditorContext(OwnerId, Chat(imageChat: true));

        context.Should().NotBeNull();
        _factory.Services.GetRequiredService<McpToolsetRegistry>().Find(McpEndpoints.ImageEditorName)
            .Should().NotBeNull("модуль загружен — тулсет в реестре");
    }

    [Fact]
    public void Обычный_чат_сервера_не_получает()
    {
        SetFlag(true);
        var sessions = _factory.Services.GetRequiredService<SessionManager>();

        sessions.BuildImageEditorContext(OwnerId, Chat(imageChat: false)).Should().BeNull();
    }

    [Fact]
    public void Выключенный_флаг_владельца_сервер_не_даёт()
    {
        SetFlag(false);
        var sessions = _factory.Services.GetRequiredService<SessionManager>();

        sessions.BuildImageEditorContext(OwnerId, Chat(imageChat: true)).Should().BeNull();
    }
}
