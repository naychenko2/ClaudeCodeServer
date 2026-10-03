using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Subsystems;

// Контекст чата при выключенной вертикали (ADR-023, ADR-014 «Пилот отключаемости»): в файле лежит
// элемент вида audio, а Subsystems:AudioEditor:Enabled=false — провайдера нет. GET отдаёт элемент с
// missing, ручки отвечают 400/200, но не 500.
public class ChatContextDisabledVerticalTests : IDisposable
{
    private sealed class AudioOffFactory : TestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Subsystems:AudioEditor:Enabled", "false");
        }
    }

    private readonly AudioOffFactory _factory = new();

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());

    [Fact]
    public async Task Элемент_выключенной_вертикали_приходит_с_missing_и_ручки_не_500()
    {
        var users = _factory.Services.GetRequiredService<UserStore>();
        var ownerId = users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        users.SetFeatureFlag(ownerId, FeatureFlagKeys.ComposerContextRow, true);
        var root = Path.Combine(_factory.TempDir, "off_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var project = _factory.Services.GetRequiredService<ProjectManager>()
            .Create("off", root, ownerId, TestWebApplicationFactory.TestUsername);
        var chat = await _factory.Services.GetRequiredService<SessionManager>()
            .CreateAsync(project.Id, ClaudeMode.AcceptEdits, name: "Чат");
        _factory.Services.GetService<AudioThreadStore>().Should().BeNull("вертикаль выключена — её сервисов нет");

        // Файл контекста, записанный, пока вертикаль была включена: основной объект audio и референс audio
        var dir = Path.Combine(_factory.TempDir, "chat-context", ownerId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, chat.Id + ".json"), """
            {"revision":3,
             "primary":{"id":"ci_a","kind":"audio","ref":{"threadId":"t1"},"role":null,"by":"human","addedAt":"2026-10-03T09:00:00Z"},
             "refs":[{"id":"ci_b","kind":"audio","ref":{"threadId":"t2"},"role":"reference","by":"agent","addedAt":"2026-10-03T09:01:00Z"}]}
            """);
        var client = _factory.CreateAuthenticatedClient();
        var url = $"/api/chats/{chat.Id}/context";

        var get = await client.GetAsync(url);
        get.StatusCode.Should().Be(HttpStatusCode.OK, await get.Content.ReadAsStringAsync());
        var body = await Json(get);
        body.GetProperty("primary").GetProperty("missing").GetBoolean().Should().BeTrue();
        body.GetProperty("refs")[0].GetProperty("missing").GetBoolean().Should().BeTrue();
        body.GetProperty("refs")[0].GetProperty("usedBy").GetArrayLength().Should().Be(0);

        var write = await client.PutAsJsonAsync($"{url}/primary", new { kind = "audio", @ref = new { threadId = "t1" } });
        write.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(write)).GetProperty("error").GetString().Should().Be("kind_unknown");

        (await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"{url}/refs/ci_b"))).StatusCode
            .Should().Be(HttpStatusCode.OK, "убрать элемент мёртвой вертикали можно");
        (await client.DeleteAsync(url)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"{url}/saved-files")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
