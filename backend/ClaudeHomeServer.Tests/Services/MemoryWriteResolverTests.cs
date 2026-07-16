using ClaudeHomeServer.Services.Memory;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Парс решения LLM-резолвера записи памяти (Memory #2): ADD/UPDATE/DELETE/NOOP из JSON,
// мусор → консервативный фолбэк ADD, отсутствие targetId/mergedText → ADD.
public class MemoryWriteResolverTests
{
    [Fact]
    public void Add_ВалидныйJson()
    {
        var d = MemoryWriteResolver.ParseDecision("""{"op":"ADD"}""");

        d.Op.Should().Be(MemoryWriteOp.Add);
        d.TargetId.Should().BeNull();
        d.MergedText.Should().BeNull();
    }

    [Fact]
    public void Update_ВалидныйJson()
    {
        var d = MemoryWriteResolver.ParseDecision(
            """{"op":"UPDATE","targetId":"e1","mergedText":"Прод переехал на example.com"}""");

        d.Op.Should().Be(MemoryWriteOp.Update);
        d.TargetId.Should().Be("e1");
        d.MergedText.Should().Be("Прод переехал на example.com");
    }

    [Fact]
    public void Delete_ВалидныйJson()
    {
        var d = MemoryWriteResolver.ParseDecision("""{"op":"DELETE","targetId":"e7"}""");

        d.Op.Should().Be(MemoryWriteOp.Delete);
        d.TargetId.Should().Be("e7");
        d.MergedText.Should().BeNull();
    }

    [Fact]
    public void Noop_ВалидныйJson()
    {
        var d = MemoryWriteResolver.ParseDecision("""{"op":"NOOP"}""");

        d.Op.Should().Be(MemoryWriteOp.Noop);
    }

    [Fact]
    public void Op_РегистрНеважен()
    {
        MemoryWriteResolver.ParseDecision("""{"op":"update","targetId":"a","mergedText":"x"}""")
            .Op.Should().Be(MemoryWriteOp.Update);
        MemoryWriteResolver.ParseDecision("""{"op":"NoOp"}""")
            .Op.Should().Be(MemoryWriteOp.Noop);
    }

    [Fact]
    public void СПреамбулойИFence_Парсится()
    {
        var raw = "Вот решение:\n```json\n{\"op\":\"DELETE\",\"targetId\":\"e2\"}\n```";

        var d = MemoryWriteResolver.ParseDecision(raw);

        d.Op.Should().Be(MemoryWriteOp.Delete);
        d.TargetId.Should().Be("e2");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("не знаю что делать")]
    [InlineData("{сломанный json")]
    [InlineData("{\"foo\":\"bar\"}")]                 // нет поля op
    [InlineData("{\"op\":\"WAT\"}")]                  // неизвестная операция
    public void Мусор_ФолбэкНаAdd(string raw)
    {
        MemoryWriteResolver.ParseDecision(raw).Op.Should().Be(MemoryWriteOp.Add);
    }

    [Fact]
    public void UpdateБезTargetId_ФолбэкНаAdd()
    {
        MemoryWriteResolver.ParseDecision("""{"op":"UPDATE","mergedText":"текст"}""")
            .Op.Should().Be(MemoryWriteOp.Add);
    }

    [Fact]
    public void UpdateБезMergedText_ФолбэкНаAdd()
    {
        MemoryWriteResolver.ParseDecision("""{"op":"UPDATE","targetId":"e1","mergedText":"  "}""")
            .Op.Should().Be(MemoryWriteOp.Add);
    }

    [Fact]
    public void DeleteБезTargetId_ФолбэкНаAdd()
    {
        MemoryWriteResolver.ParseDecision("""{"op":"DELETE"}""")
            .Op.Should().Be(MemoryWriteOp.Add);
    }
}
