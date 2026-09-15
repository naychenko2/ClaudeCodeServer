using System.Net;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// OAuth-редирект Higgsfield: callback — GET + [AllowAnonymous], авторизация по
/// одноразовому state (в редиректе нет JWT), ответ — HTML-страница с postMessage.
/// Паттерн скопирован из McpOAuthController.
/// </summary>
public class HiggsfieldCallbackTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public HiggsfieldCallbackTests(TestWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Callback_GET_БезJWt_Вернёт200Html_СPostMessage()
    {
        // Анонимный клиент: JWT нет — до фикса падало 401, а POST-метод — 405.
        var client = _factory.CreateClient();

        var resp = await client.GetAsync(
            "/api/higgsfield/callback?state=nonexistent&code=code42");

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "callback должен быть анонимным (нет JWT) и GET (провайдер редиректит браузером)");
        resp.Content.Headers.ContentType!.MediaType.Should().Be("text/html");

        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain("postMessage");
        html.Should().Contain("\"type\":\"mcp-oauth\"");
        html.Should().Contain("\"ok\":false");
    }

    [Fact]
    public async Task Callback_ОшибкаОтПровайдера_ТожеPostMessage_Не405()
    {
        // Clerk может ответить ?error=access_denied — тот же GET-callback, тоже postMessage.
        var client = _factory.CreateClient();

        var resp = await client.GetAsync(
            "/api/higgsfield/callback?error=access_denied&error_description=user+cancelled");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain("\"ok\":false");
        html.Should().Contain("postMessage");
    }

    [Fact]
    public async Task Callback_ИнстансНеПодключён_ОшибкаСообщение_Не500()
    {
        // Служебный pending не существует → CompleteAsync бросит McpOAuthException,
        // контроллер ловит → 200 с error-page (не 500).
        var client = _factory.CreateClient();

        var resp = await client.GetAsync(
            "/api/higgsfield/callback?state=abc123&code=xyz");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain("Вход не найден");
    }
}
