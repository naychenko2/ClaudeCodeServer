using ClaudeHomeServer.Services.Llm;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Извлечение заголовка из ответа фонового действия: строгий JSON (structured output локали)
// с устойчивым фолбэком на первую строку (свободный текст claude/direct).
public class TitleExtractionTests
{
    [Fact]
    public void Extract_FromStrictJson()
        => Assert.Equal("Починка прода", TitleExtraction.Extract("{\"title\":\"Починка прода\"}"));

    [Fact]
    public void Extract_FromJsonWrappedInProse()
        => Assert.Equal("Деплой на порт 80", TitleExtraction.Extract("Вот заголовок: {\"title\": \"Деплой на порт 80\"}"));

    [Fact]
    public void Extract_FromCodeFence()
        => Assert.Equal("Тест", TitleExtraction.Extract("```json\n{\"title\": \"Тест\"}\n```"));

    [Fact]
    public void Extract_FallbackToFirstLine()
        => Assert.Equal("Настройка Ollama", TitleExtraction.Extract("Настройка Ollama\nещё текст"));

    [Fact]
    public void Extract_StripsQuotesAndMarkers()
        => Assert.Equal("Заголовок", TitleExtraction.Extract("## «Заголовок»"));

    // Главный кейс: qwen3:4b болтает вслух, но в конце отдаёт строгий JSON — берём title,
    // а НЕ первую строку рассуждения (та раньше отбрасывалась как > 80 символов).
    [Fact]
    public void Extract_PrefersJsonOverRamble()
        => Assert.Equal("Итог дня", TitleExtraction.Extract(
            "Хорошо, мне нужно придумать короткий заголовок из 3-6 слов по содержимому.\n" +
            "Подумаю над сутью разговора и оформлю.\n{\"title\": \"Итог дня\"}"));

    [Fact]
    public void Extract_NullOnEmpty()
    {
        Assert.Null(TitleExtraction.Extract("   "));
        Assert.Null(TitleExtraction.Extract(null));
    }
}
