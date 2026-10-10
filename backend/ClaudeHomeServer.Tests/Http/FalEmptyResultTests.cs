using ClaudeHomeServer.Services.Http;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Http;

// Причина пустого успешного ответа fal — общий разбор для картинок, звука и видео
public class FalEmptyResultTests
{
    [Theory]
    [InlineData("""{"images":[],"description":"Уточните сюжет"}""", false, "модель ответила: «Уточните сюжет»")]
    [InlineData("""{"detail":"Blocked by safety filters"}""", true, "модель ответила: «Blocked by safety filters»")]
    [InlineData("""{"has_nsfw_concepts":[false,true]}""", true, "результат скрыт фильтром безопасности")]
    public void Причина_Найдена(string body, bool rejected, string reason)
    {
        FalEmptyResult.Explain(body).Should().Be((rejected, reason));
    }

    [Theory]
    [InlineData("""{"images":[]}""")]
    [InlineData("""{"has_nsfw_concepts":[false],"description":"  "}""")]
    [InlineData("""[]""")]
    [InlineData("не json")]
    public void Причины_Нет_Null(string body)
    {
        FalEmptyResult.Explain(body).Should().BeNull();
    }

    [Fact]
    public void ДлинныйТекстМодели_Обрезается()
    {
        var why = FalEmptyResult.Explain($$"""{"description":"{{new string('а', 500)}}"}""");

        why!.Value.Reason.Should().EndWith("…»").And.HaveLength("модель ответила: «".Length + 300 + 2);
    }
}
