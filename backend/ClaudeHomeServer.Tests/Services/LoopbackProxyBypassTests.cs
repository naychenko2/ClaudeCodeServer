using ClaudeHomeServer.Services.Mcp.Http;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Значение NO_PROXY для хода (ADR-012). С http-транспортом MCP локальный адрес бэкенда
/// обязан быть исключён из прокси — иначе запрос CLI уедет в HTTP_PROXY и инструмент
/// пропадёт у модели молча. Унаследованное окружение при этом трогать нельзя: у владельца
/// там могут быть свои исключения, и затирание сломало бы их.
/// </summary>
public class LoopbackProxyBypassTests
{
    [Fact]
    public void БезУнаследованного_ДаётЛокальныеАдреса()
    {
        var value = LoopbackProxyBypass.Merge(null);

        value.Split(',').Should().BeEquivalentTo("localhost", "127.0.0.1", "::1", "host.docker.internal");
    }

    [Fact]
    public void УнаследованноеСохраняется_АЛокальныеДобавляются()
    {
        var value = LoopbackProxyBypass.Merge("corp.example.com, 10.0.0.0/8");

        var parts = value.Split(',');
        parts.Should().StartWith(["corp.example.com", "10.0.0.0/8"], "чужие исключения не теряем");
        parts.Should().Contain("localhost").And.Contain("127.0.0.1")
            .And.Contain("host.docker.internal");
    }

    [Fact]
    public void УжеПеречисленныйАдрес_НеЗадваивается()
    {
        var value = LoopbackProxyBypass.Merge("LOCALHOST,127.0.0.1");

        value.Split(',').Count(p => p.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            .Should().Be(1, "сравнение без учёта регистра — иначе список пухнет каждый ход");
        value.Split(',').Count(p => p == "127.0.0.1").Should().Be(1);
    }

    [Fact]
    public void ПустыеЭлементыИПробелы_Отбрасываются()
    {
        var value = LoopbackProxyBypass.Merge(" , foo , ");

        value.Split(',').Should().NotContain("").And.Contain("foo");
        value.Should().NotContain(" ");
    }

    /// <summary>
    /// Хост берётся из ФАКТИЧЕСКОГО адреса эндпоинта: сопоставление в NO_PROXY идёт по имени,
    /// и адрес вида http://ccs-host:5000 не покрывается ни localhost, ни 127.0.0.1.
    /// </summary>
    [Fact]
    public void ХостФактическогоАдреса_ПопадаетВСписок()
    {
        var value = LoopbackProxyBypass.Merge(null, "http://ccs-host:5000");

        value.Split(',').Should().Contain("ccs-host").And.Contain("localhost");
    }

    [Fact]
    public void НегодныйURL_Игнорируется() =>
        LoopbackProxyBypass.Merge(null, "не-адрес", null, "")
            .Split(',').Should().BeEquivalentTo("localhost", "127.0.0.1", "::1", "host.docker.internal");

    /// <summary>
    /// Значение входит в сигнатуру запуска CLI — мерцание перезапускало бы процесс со всеми
    /// MCP-серверами между ходами. Порядок и состав обязаны быть детерминированными.
    /// </summary>
    [Fact]
    public void ЗначениеДетерминированно()
    {
        var first = LoopbackProxyBypass.Merge("corp.example.com", "http://localhost:5000");
        var second = LoopbackProxyBypass.Merge("corp.example.com", "http://localhost:5000");

        second.Should().Be(first);
    }
}
