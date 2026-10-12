using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Тело POST /prompt ComfyUI: всегда prompt/client_id/extra_data; ключ "front" (в голову
// очереди) — только при явном запросе, иначе тело не отличается от прежнего
public class ComfyClientTests
{
    private readonly FakeComfy _comfy = new();

    private ComfyClient Build()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalMedia:ComfyUrl"] = "http://comfy.test:8188",
        }).Build();
        return new ComfyClient(new FakeComfyFactory(_comfy), config);
    }

    private static JsonObject Graph() => ComfyWorkflows.FaceDetect("ccs-local-media/in.png");

    [Fact]
    public async Task Front_КлючВТелеТолькоПриЯвномЗапросе()
    {
        var client = Build();

        await client.QueuePromptAsync(Graph(), CancellationToken.None, front: true);
        await client.QueuePromptAsync(Graph(), CancellationToken.None);

        var withFront = _comfy.Prompts[0];
        withFront.ContainsKey("front").Should().BeTrue();
        withFront["front"]!.GetValue<bool>().Should().BeTrue();

        var plain = _comfy.Prompts[1];
        plain.ContainsKey("front").Should().BeFalse("без front в теле ключа нет");
    }

    [Fact]
    public async Task ТелоБезFront_ПрежнийНаборКлючей()
    {
        var client = Build();

        await client.QueuePromptAsync(Graph(), CancellationToken.None);

        var sent = _comfy.Prompts.Should().ContainSingle().Subject;
        sent.Select(p => p.Key).Should().BeEquivalentTo(["prompt", "client_id", "extra_data"]);
        sent["client_id"]!.GetValue<string>().Should().Be(ComfyClient.ClientId);
        sent["extra_data"]!["preview_method"]!.GetValue<string>().Should().Be("latent2rgb");
        sent["prompt"]!["detect"]!["inputs"]!["references"]!.GetValue<string>().Should().Be("ccs-local-media/in.png");
    }
}
