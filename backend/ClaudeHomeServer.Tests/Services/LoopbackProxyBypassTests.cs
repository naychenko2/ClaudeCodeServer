using ClaudeHomeServer.Services.Mcp.Http;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Правило NO_PROXY для хода (ADR-012). С http-транспортом MCP локальный адрес бэкенда
/// обязан быть исключён из прокси — иначе запрос CLI уедет в HTTP_PROXY и инструмент
/// пропадёт у модели молча.
///
/// Правило развязано по среде исполнения (находка консилиума по 3b764c58): Merge — спецификация
/// ТОЛЬКО local-ветки, песочнице хостовое окружение не доезжает вовсе (иначе exec-переменная
/// подменяет узкий egress-whitelist контейнера корпоративными исключениями хоста), а выключенный
/// транспорт не ставит оверрайд вовсе (откат рубильником обязан откатывать и env).
/// </summary>
public class LoopbackProxyBypassTests
{
    [Fact]
    public void БезУнаследованного_ДаётЛокальныеАдреса()
    {
        var value = LoopbackProxyBypass.Merge(null);

        value.Split(',').Should().BeEquivalentTo("localhost", "127.0.0.1", "::1", "host.docker.internal");
    }

    /// <summary>
    /// Спецификация local-ветки (Merge): унаследованное сохраняется и дополняется локальными.
    /// Для local-владельца HTTP_PROXY на машине бывает единственным маршрутом до провайдеров,
    /// поэтому его исключения затирать нельзя. Песочница сюда не попадает — см. ForTurn-тесты.
    /// </summary>
    [Fact]
    public void Merge_LocalВладелец_УнаследованноеСохраняется_ЛокальныеДобавляются()
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
    /// Хост берём из ФАКТИЧЕСКОГО адреса эндпоинта: сопоставление в NO_PROXY идёт по имени,
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

    /// <summary>
    /// БЛОКЕР консилиума №1: container-владелец не получает хостовой NO_PROXY. Хостовая
    /// переменная (например, корпоративный corp.example.com) через docker exec -e подменила бы
    /// узкий egress-whitelist песочницы — оверрайд не ставится вовсе, средой владеет контейнер.
    /// </summary>
    [Fact]
    public void ForTurn_Песочница_ХостовойNO_PROXY_НеНаследуетсяНичем()
    {
        var value = LoopbackProxyBypass.ForTurn(useHttp: true, isSandboxed: true,
            inherited: "corp.example.com,10.0.0.0/8", apiUrls: "http://host.docker.internal:5000");

        value.Should().BeNull("exec-оверрайд затёр бы egress-whitelist песочницы; " +
            "нужные адреса уже стоят в её собственном NO_PROXY");
    }

    /// <summary>
    /// БЛОКЕР консилиума №2: рубильник Mcp:HttpTransport=false возвращает stdio — env-оверрайд
    /// обязан откатиться вместе с транспортом, иначе «откат без выкатки кода» неполон.
    /// </summary>
    [Fact]
    public void ForTurn_ТранспортНеHttp_ОверрайдаНет()
    {
        var value = LoopbackProxyBypass.ForTurn(useHttp: false, isSandboxed: false,
            inherited: "corp.example.com", apiUrls: "http://localhost:5000");

        value.Should().BeNull("в бэкенд по этому адресу CLI не ходит — переменная не нужна");
    }

    [Fact]
    public void ForTurn_LocalВладелец_УнаследованноеДополняетсяАдресомЭндпоинта()
    {
        var value = LoopbackProxyBypass.ForTurn(useHttp: true, isSandboxed: false,
            inherited: "corp.example.com", apiUrls: "http://ccs-host:5000");

        value.Should().Be("corp.example.com,localhost,127.0.0.1,::1,host.docker.internal,ccs-host");
    }

    /// <summary>
    /// Блокер приёмки волны 1 (A): адресов у хода НЕСКОЛЬКО — widgets, memory и pmem-хвосты
    /// консультантов. Сценарий «на http только pmem» (чат вне проекта, память выключена,
    /// widgets Off): пустые URL widgets/memory отбрасываются, хост pmem попадает в обход —
    /// раньше один URL через ?? молча пропускал его в HTTP_PROXY вместе с JWT из заголовка.
    /// </summary>
    [Fact]
    public void ForTurn_ТолькоПmemНаХttp_ЕгоХостПопадаетВОбход()
    {
        var value = LoopbackProxyBypass.ForTurn(useHttp: true, isSandboxed: false,
            inherited: null, apiUrls: [null, null, "http://ccs-pmem-host:5000", null]);

        value!.Split(',').Should().Contain("ccs-pmem-host",
            "хост каждого http-сервера хода обязан попасть в обход, не только первого");
    }
}
