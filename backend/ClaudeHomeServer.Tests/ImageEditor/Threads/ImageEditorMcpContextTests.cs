using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Llm.Claude;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.ImageEditor.Threads;

// Сервер image-editor в конфиге хода (ADR-019 §4): любой чат проекта — есть, чат вне проекта —
// нет, флаг владельца выключен — нет. Модуль выключен — тулсета нет в реестре и контекста тоже
// (ImageEditorDisabledTests). Инструменты сервера идут без карточки разрешения — но только в
// ходе, куда сервер действительно едет.
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

    private Session Chat(string? projectId = "p1") => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        OwnerId = OwnerId,
        ProjectId = projectId,
    };

    private void SetFlag(bool on) =>
        _factory.Services.GetRequiredService<UserStore>().SetFeatureFlag(OwnerId, FeatureFlagKeys.ImageEditor, on)
            .Should().BeTrue();

    private SessionManager Sessions => _factory.Services.GetRequiredService<SessionManager>();

    [Fact]
    public void Любой_чат_проекта_получает_сервер_со_списком_авторазрешения()
    {
        SetFlag(true);

        var context = Sessions.BuildImageEditorContext(OwnerId, Chat());

        context.Should().NotBeNull();
        context!.AutoAllowTools.Should().BeEquivalentTo(ImageEditorAgentTools.AutoAllowTools);
        _factory.Services.GetRequiredService<McpToolsetRegistry>().Find(McpEndpoints.ImageEditorName)
            .Should().NotBeNull("модуль загружен — тулсет в реестре");
    }

    [Fact]
    public void Чат_вне_проекта_сервера_не_получает()
    {
        SetFlag(true);

        Sessions.BuildImageEditorContext(OwnerId, Chat(projectId: null)).Should().BeNull();
    }

    [Fact]
    public void Выключенный_флаг_владельца_сервер_не_даёт()
    {
        SetFlag(false);

        Sessions.BuildImageEditorContext(OwnerId, Chat()).Should().BeNull();
    }

    [Fact]
    public void Без_карточки_разрешения_только_инструменты_сервера_этого_хода()
    {
        SetFlag(true);
        var context = Sessions.BuildImageEditorContext(OwnerId, Chat());

        ClaudeSession.IsImageEditorAutoAllowed(context, "mcp__image-editor__image_generate").Should().BeTrue();
        ClaudeSession.IsImageEditorAutoAllowed(context, "mcp__image-editor__image_focus").Should().BeTrue();
        ClaudeSession.IsImageEditorAutoAllowed(context, "mcp__other__image_generate").Should().BeFalse();
        ClaudeSession.IsImageEditorAutoAllowed(null, "mcp__image-editor__image_generate")
            .Should().BeFalse("сервер в ход не едет — и разрешать нечего");
    }
}
