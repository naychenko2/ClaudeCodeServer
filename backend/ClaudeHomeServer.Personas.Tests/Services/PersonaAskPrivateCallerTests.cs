using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Mcp.Http;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// S2d-5b: признак «вызов persona_ask из приватного чата» выводится на сервере из сессий
/// (маршрут MCP и X-Caller-Session-Id), не из аргументов инструмента. Приватный или
/// неопределённый вызыватель — fail-closed: память целевой персоны не пишется.
/// </summary>
public class PersonaAskPrivateCallerTests
{
    private static Session Open() => new() { Id = "s-open", ProjectId = "p" };
    private static Session Private() => new() { Id = "s-priv", ProjectId = "p", PrivateToUserId = "author" };

    [Fact]
    public void ОбщийЧат_НеПриватный() =>
        PersonasToolset.ConsultationCallerIsPrivateOrUnknown(Open(), Open()).Should().BeFalse();

    [Fact]
    public void ПриватныйЧат_Приватный() =>
        PersonasToolset.ConsultationCallerIsPrivateOrUnknown(Private(), Private()).Should().BeTrue();

    [Fact]
    public void ПриватнаяСессияВЗаголовке_ПриОбщемМаршруте_Приватный() =>
        PersonasToolset.ConsultationCallerIsPrivateOrUnknown(Open(), Private()).Should().BeTrue();

    [Fact]
    public void ЗаголовокНеОпределён_FailClosed() =>
        PersonasToolset.ConsultationCallerIsPrivateOrUnknown(Open(), null).Should().BeTrue();

    [Fact]
    public void МаршрутНеОпределён_FailClosed() =>
        PersonasToolset.ConsultationCallerIsPrivateOrUnknown(null, Open()).Should().BeTrue();
}
