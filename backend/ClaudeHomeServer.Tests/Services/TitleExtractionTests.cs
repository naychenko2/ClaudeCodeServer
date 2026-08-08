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

    // Имя lucide-компонента: PascalCase, любого имени из ~1700 (белого списка нет — фронт
    // проверит icons[iconName]). Sanity отсекает явный мусор
    [Fact]
    public void ExtractIconName_PascalCase()
        => Assert.Equal("Cat", TitleExtraction.ExtractIconName("{\"title\":\"Кошка\",\"iconName\":\"Cat\"}"));

    [Fact]
    public void ExtractIconName_MultiWordPascalCase()
        => Assert.Equal("MousePointerClick", TitleExtraction.ExtractIconName("{\"iconName\":\"MousePointerClick\"}"));

    [Fact]
    public void ExtractIconName_FromProseAndFence()
    {
        Assert.Equal("Bug", TitleExtraction.ExtractIconName("Вот: {\"iconName\": \"Bug\"}"));
        Assert.Equal("Dog", TitleExtraction.ExtractIconName("```json\n{\"iconName\": \"Dog\"}\n```"));
    }

    [Fact]
    public void ExtractIconName_TrimsSpaces()
        => Assert.Equal("User", TitleExtraction.ExtractIconName("{\"iconName\": \"  User \"}"));

    [Fact]
    public void ExtractIconName_RejectsNonPascalCaseAndNoise()
    {
        Assert.Null(TitleExtraction.ExtractIconName("{\"iconName\":\"cat\"}"));      // с маленькой — не PascalCase
        Assert.Null(TitleExtraction.ExtractIconName("{\"iconName\":\"bug report\"}"));// с пробелом
        Assert.Null(TitleExtraction.ExtractIconName("{\"iconName\":\":cat:\"}"));     // шорткод
        Assert.Null(TitleExtraction.ExtractIconName("{\"iconName\":\"\"}"));          // пусто
        Assert.Null(TitleExtraction.ExtractIconName("{\"title\":\"Х\"}"));            // поля нет
        Assert.Null(TitleExtraction.ExtractIconName("Просто текст без JSON"));
        Assert.Null(TitleExtraction.ExtractIconName(null));
    }

    // Проверка «имя начинается со значка» — нужна миграции старых эмодзи-имён
    [Fact]
    public void HasEmoji_DetectsLeadingEmoji()
    {
        Assert.True(TitleExtraction.HasEmoji("🐛 Правка авторизации"));
        Assert.True(TitleExtraction.HasEmoji("🚀 Деплой"));
        Assert.False(TitleExtraction.HasEmoji("Правка авторизации"));
        Assert.False(TitleExtraction.HasEmoji("→ стрелка не значок"));
        Assert.False(TitleExtraction.HasEmoji(""));
        Assert.False(TitleExtraction.HasEmoji(null));
    }
}
