using ClaudeHomeServer.Services.Media;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Services.VideoEditor.Tests.Media;

// Хаб событий между редакторами (ADR-022 §3): in-memory pub/sub, падение подписчика не доходит до публикующего
public sealed class MediaEventHubTests
{
    private static ImageVersionAdded Image(string version) => new("o", "s", "p", "t", version, "human");

    [Fact]
    public async Task Событие_получают_подписчики_своего_типа_по_порядку()
    {
        var hub = new MediaEventHub();
        var got = new List<string>();
        hub.Subscribe<ImageVersionAdded>(e => { got.Add("1:" + e.VersionId); return Task.CompletedTask; });
        hub.Subscribe<ImageVersionAdded>(e => { got.Add("2:" + e.VersionId); return Task.CompletedTask; });
        hub.Subscribe<AudioVersionAdded>(_ => { got.Add("audio"); return Task.CompletedTask; });

        await hub.PublishAsync(Image("v1"));

        got.Should().Equal("1:v1", "2:v1");
    }

    [Fact]
    public async Task Упавший_подписчик_гасится_и_остальные_получают_событие()
    {
        var hub = new MediaEventHub();
        var got = 0;
        hub.Subscribe<ImageVersionAdded>(_ => throw new InvalidOperationException("упал"));
        hub.Subscribe<ImageVersionAdded>(_ => { got++; return Task.CompletedTask; });

        var act = () => hub.PublishAsync(Image("v1"));

        await act.Should().NotThrowAsync();
        got.Should().Be(1);
    }

    [Fact]
    public async Task Снятая_подписка_событий_больше_не_получает()
    {
        var hub = new MediaEventHub();
        var got = 0;
        var subscription = hub.Subscribe<ImageVersionAdded>(_ => { got++; return Task.CompletedTask; });
        await hub.PublishAsync(Image("v1"));

        subscription.Dispose();
        await hub.PublishAsync(Image("v2"));

        got.Should().Be(1);
    }
}
