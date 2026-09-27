using ClaudeHomeServer.WebDav;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace ClaudeHomeServer.Tests.Services;

// Состав вызова аутентификации WebDAV зависит от платформы: NTLM валидирует SSPI, а он есть
// только на Windows. На Linux Negotiate предлагать нельзя — Windows Mini-Redirector цепляется
// за более сильную схему и крутит обречённое рукопожатие вместо отката на Basic.
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
    public void ВызовВОтвете_СоответствуетПлатформе()
    {
        var ctx = new DefaultHttpContext();

        WebDavHandler.SendAuthChallenge(ctx);

        var header = ctx.Response.Headers["WWW-Authenticate"].ToString();
        header.Should().Be(WebDavHandler.BuildAuthChallenge(OperatingSystem.IsWindows()));
        if (!OperatingSystem.IsWindows())
            header.Should().NotContain("Negotiate");
    }
}
