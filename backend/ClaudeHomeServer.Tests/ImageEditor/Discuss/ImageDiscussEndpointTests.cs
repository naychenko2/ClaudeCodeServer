using System.Net;
using System.Text.Json;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Files;
using ClaudeHomeServer.Tests.Helpers;
using ClaudeHomeServer.Tests.ImageEditor.Characters;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.ImageEditor.Discuss;

// «Обсудить с Claude» (ADR-017, раздел 6): новый чат проекта с размеченной копией
// вложением и исходником вторым вложением, id чата — в ответе. Ход уходит в стаб LLM
// тестового хоста, claude CLI не запускается.
public class ImageDiscussEndpointTests : IDisposable
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01];
    private readonly List<TestWebApplicationFactory> _factories = [];

    public void Dispose()
    {
        foreach (var f in _factories) f.Dispose();
        GC.SuppressFinalize(this);
    }

    private TestWebApplicationFactory Factory()
    {
        var factory = new TestWebApplicationFactory();
        _factories.Add(factory);
        return factory;
    }

    private static MultipartFormDataContent Form(string text, string? sourcePath = null, string? sessionId = null)
    {
        var form = new MultipartFormDataContent { { new StringContent(text), "text" } };
        var annotated = new ByteArrayContent(Png);
        annotated.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(annotated, "annotated", "annotated.png");
        if (sourcePath is not null) form.Add(new StringContent(sourcePath), "sourcePath");
        if (sessionId is not null) form.Add(new StringContent(sessionId), "sessionId");
        return form;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());

    [Fact]
    public async Task Обсудить_создаёт_новый_чат_проекта_с_вложением_и_возвращает_его_id()
    {
        var factory = Factory();
        CharacterEndpointsTests.EnableFlag(factory, TestWebApplicationFactory.TestUsername);
        var (projectId, root) = CharacterEndpointsTests.CreateProject(factory, TestWebApplicationFactory.TestUsername);
        Directory.CreateDirectory(Path.Combine(root, "images"));
        File.WriteAllBytes(Path.Combine(root, "images", "hero.png"), Png);
        var client = factory.CreateAuthenticatedClient();
        var sessions = factory.Services.GetRequiredService<SessionManager>();

        var resp = await client.PostAsync($"/api/projects/{projectId}/image-editor/discuss",
            Form("Как убрать провод справа?", "images/hero.png"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var body = await Json(resp);
        var sessionId = body.GetProperty("sessionId").GetString()!;
        body.GetProperty("created").GetBoolean().Should().BeTrue();

        var session = sessions.GetById(sessionId);
        session.Should().NotBeNull();
        session!.ProjectId.Should().Be(projectId, "обсуждение идёт в НОВОМ чате этого проекта");
        session.Name.Should().Be("Картинка: hero.png");
        sessions.GetByProject(projectId).Should().ContainSingle();

        var attachments = body.GetProperty("attachments").EnumerateArray().Select(a => a.GetString()!).ToList();
        attachments.Should().HaveCount(2);
        attachments[0].Should().StartWith(FileService.AttachmentsDir + "/").And.EndWith("/annotated.png");
        attachments[1].Should().Be("images/hero.png");
        File.ReadAllBytes(Path.Combine(root, attachments[0])).Should().Equal(Png);

        var message = (await sessions.GetHistoryAsync(sessionId)).OfType<StoredUserMessage>().Should().ContainSingle().Subject;
        message.Text.Should().Be("Как убрать провод справа?");
        message.AttachedPaths.Should().Equal(attachments);
        factory.LlmAdapters.Adapters[sessionId].SentMessages.Should().ContainSingle("ход ушёл в чат");
    }

    [Fact]
    public async Task Чат_сеанса_переиспользуется_а_чужой_id_даёт_новый()
    {
        var factory = Factory();
        CharacterEndpointsTests.EnableFlag(factory, TestWebApplicationFactory.TestUsername);
        var (projectId, _) = CharacterEndpointsTests.CreateProject(factory, TestWebApplicationFactory.TestUsername);
        var (otherProjectId, _) = CharacterEndpointsTests.CreateProject(factory, TestWebApplicationFactory.TestUsername);
        var client = factory.CreateAuthenticatedClient();
        var api = $"/api/projects/{projectId}/image-editor/discuss";

        var first = (await Json(await client.PostAsync(api, Form("раз")))).GetProperty("sessionId").GetString()!;
        var again = await Json(await client.PostAsync(api, Form("два", sessionId: first)));
        again.GetProperty("sessionId").GetString().Should().Be(first);
        again.GetProperty("created").GetBoolean().Should().BeFalse();

        var foreign = (await Json(await client.PostAsync($"/api/projects/{otherProjectId}/image-editor/discuss", Form("три"))))
            .GetProperty("sessionId").GetString()!;
        var fresh = await Json(await client.PostAsync(api, Form("четыре", sessionId: foreign)));
        fresh.GetProperty("sessionId").GetString().Should().NotBe(foreign, "чат другого проекта не переиспользуется");
        fresh.GetProperty("created").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Исходник_вне_проекта_и_пустой_текст_отклоняются_без_чата()
    {
        var factory = Factory();
        CharacterEndpointsTests.EnableFlag(factory, TestWebApplicationFactory.TestUsername);
        var (projectId, _) = CharacterEndpointsTests.CreateProject(factory, TestWebApplicationFactory.TestUsername);
        var client = factory.CreateAuthenticatedClient();
        var api = $"/api/projects/{projectId}/image-editor/discuss";

        (await client.PostAsync(api, Form("вопрос", "../secret.png"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsync(api, Form("вопрос", "/etc/passwd"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsync(api, Form("   "))).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        factory.Services.GetRequiredService<SessionManager>().GetByProject(projectId).Should().BeEmpty();
    }
}
