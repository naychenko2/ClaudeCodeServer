using ClaudeHomeServer.WebDav;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace ClaudeHomeServer.Tests.Services;

// Negotiate предлагаем только там, где Type3 есть чем проверить (SSPI или gss-ntlmssp с нашим
// файлом): иначе Windows Mini-Redirector цепляется за более сильную схему и крутит обречённое
// рукопожатие вместо отката на Basic. Проверка по запросу — WebDavNtlmUserFileTests.
public class WebDavAuthChallengeTests
{
    [Fact]
    public void ЕстьSSPI_ПредлагаемNegotiateИBasic()
    {
        WebDavHandler.BuildAuthChallenge(ntlmAvailable: true)
            .Should().Be("Negotiate, Basic realm=\"ClaudeHomeServer\"");
    }

    [Fact]
    public void НетSSPI_ТолькоBasic()
    {
        var challenge = WebDavHandler.BuildAuthChallenge(ntlmAvailable: false);

        challenge.Should().Be("Basic realm=\"ClaudeHomeServer\"");
        challenge.Should().NotContain("Negotiate");
    }

    [Fact]
    public void БезПроверкиNtlm_ВОтветеТолькоBasic()
    {
        // Нет NtlmUserFile в DI и http — Negotiate не предлагается ни на одной платформе
        var ctx = new DefaultHttpContext();

        WebDavHandler.SendAuthChallenge(ctx);

        ctx.Response.Headers["WWW-Authenticate"].ToString().Should().Be("Basic realm=\"ClaudeHomeServer\"");
    }
}
