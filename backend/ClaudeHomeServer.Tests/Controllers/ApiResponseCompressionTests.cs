using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// Сжатие ответов REST (Program.cs, UseWhen по /api): список чатов проекта — сотни КБ JSON,
// и через внешний домен он ехал несжатым. Ветка /api/auth из сжатия исключена — токен
// в теле ответа рядом со сжатием даёт материал для BREACH.
public class ApiResponseCompressionTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateAuthenticatedClient();

    private async Task<HttpResponseMessage> GetWithBrotliAsync(string url)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.AcceptEncoding.ParseAdd("br");
        var resp = await _client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return resp;
    }

    [Fact]
    public async Task RestОтвет_Сжимается()
    {
        var resp = await GetWithBrotliAsync("/api/chats");

        resp.Content.Headers.ContentEncoding.Should().Contain("br");
    }

    [Fact]
    public async Task ВеткаAuth_НеСжимается()
    {
        var resp = await GetWithBrotliAsync("/api/auth/me");

        resp.Content.Headers.ContentEncoding.Should().BeEmpty();
    }
}
