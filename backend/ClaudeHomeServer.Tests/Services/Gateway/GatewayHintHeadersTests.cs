using ClaudeHomeServer.Services.Llm.Gateway;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.Gateway;

/// <summary>
/// Подсказочные заголовки CLI (CLAUDE_CODE_GATEWAY_HINT_HEADERS=1, ADR-029): парсер
/// GatewayHintHeaders. Значения — недоверенный ввод: фиксированные словари (request-class,
/// compaction), printable ASCII + обрезка (agent-type), trim, регистр. Сырые в лог не
/// попадают — сюда идёт только валидированное.
/// </summary>
public sealed class GatewayHintHeadersTests
{
    // (а) Полный набор: все четыре заголовка валидны → запись с ними.
    [Fact]
    public void ВсеЗаголовкиВалидны_ЗаписьСНими()
    {
        var h = GatewayHintHeaders.FromValues("main", "Explore", "auto", null)!;
        h.RequestClass.Should().Be("main");
        h.AgentType.Should().Be("Explore");
        h.Compaction.Should().Be("auto");
        h.ContextCompacted.Should().BeNull();
    }

    // (б) Ни одного заголовка → null (переменная выключена / старый CLI).
    [Fact]
    public void НиОдногоЗаголовка_Null()
    {
        GatewayHintHeaders.FromValues(null, null, null, null).Should().BeNull();
        GatewayHintHeaders.FromValues("", "", "", "").Should().BeNull();
    }

    // (в) request-class вне словаря (недоверенный) → null, остальные на месте.
    [Fact]
    public void RequestClassВнеСловаря_Null()
    {
        var h = GatewayHintHeaders.FromValues("bogus", "Plan", null, null)!;
        h.RequestClass.Should().BeNull("«bogus» не из словаря");
        h.AgentType.Should().Be("Plan");
    }

    // (г) request-class в верхнем регистре → канонический нижний (регистр на проводе не важен).
    [Fact]
    public void RequestClassВерхнийРегистр_КаноническийНижний()
    {
        GatewayHintHeaders.FromValues("MAIN", null, null, null)!.RequestClass.Should().Be("main");
    }

    // (д) compaction / context-compacted вне словаря → null; из словаря → как есть.
    [Theory]
    [InlineData("auto")]
    [InlineData("manual")]
    [InlineData("reactive")]
    public void CompactionИзСловаря_Принят(string kind)
    {
        GatewayHintHeaders.FromValues(null, null, kind, null)!.Compaction.Should().Be(kind);
    }

    [Fact]
    public void CompactionВнеСловаря_Null()
    {
        // Единственный заголовок валидируется в null → вся запись null (не «запись с null»).
        GatewayHintHeaders.FromValues(null, null, "weird", null).Should().BeNull();
    }

    // (е) context-compacted — те же значения, что compaction.
    [Fact]
    public void ContextCompactedИзСловаря_Принят()
    {
        GatewayHintHeaders.FromValues(null, null, null, "manual")!.ContextCompacted.Should().Be("manual");
    }

    // (ж) agent-type — свободное ASCII-имя: trim, пробелы внутри сохраняются.
    [Fact]
    public void AgentType_Трим_ПробелыВнутриСохранены()
    {
        GatewayHintHeaders.FromValues(null, "  general-purpose  ", null, null)!.AgentType
            .Should().Be("general-purpose");
    }

    // (з) agent-type длиннее потолка → обрезка до MaxValueLength.
    [Fact]
    public void AgentTypeДлинный_Обрезка()
    {
        var longName = new string('a', GatewayHintHeaders.MaxValueLength + 20);
        var h = GatewayHintHeaders.FromValues(null, longName, null, null)!;
        h.AgentType!.Length.Should().Be(GatewayHintHeaders.MaxValueLength);
    }

    // (и) agent-type с не-ASCII символом → обрыв на нём (docs: все значения printable ASCII).
    [Fact]
    public void AgentTypeНеAscii_Обрыв()
    {
        // 'а' (кириллица, U+0430) — не printable ASCII → обрыв до неё.
        GatewayHintHeaders.FromValues(null, "Exа", null, null)!.AgentType.Should().Be("Ex");
    }

    // (к) agent-type из чистых управляющих символов → null (trim → пусто).
    [Fact]
    public void AgentTypeТолькоПробелы_Null()
    {
        // Единственный заголовок (пробелы) валидируется в null → вся запись null.
        GatewayHintHeaders.FromValues(null, "   ", null, null).Should().BeNull();
    }

    // (л) Только один валидный заголовок → запись с ним (остальные null).
    [Fact]
    public void ТолькоОдинЗаголовок_ЗаписьСНим()
    {
        var h = GatewayHintHeaders.FromValues(null, null, "auto", null)!;
        h.Compaction.Should().Be("auto");
        h.RequestClass.Should().BeNull();
        h.AgentType.Should().BeNull();
    }

    // (м) CleanAscii: пустая/отсутствующая строка → null; обрезка ровно до потолка.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void CleanAsciiПусто_Null(string? v)
    {
        GatewayHintHeaders.CleanAscii(v).Should().BeNull();
    }

    [Fact]
    public void CleanAscii_РовноПотолок()
    {
        var v = new string('b', GatewayHintHeaders.MaxValueLength);
        GatewayHintHeaders.CleanAscii(v)!.Length.Should().Be(GatewayHintHeaders.MaxValueLength);
    }
}
