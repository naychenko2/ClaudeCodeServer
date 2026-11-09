using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Разовая чистка данных темы чата под свободный выбор lucide-иконки: снимает ведущий эмодзи
// с имени и обнуляет устаревшие ключевые Topic (bug/code — не PascalCase lucide-имёна).
// Чистая функция над списком сессий — без файлов и без поднятия SessionManager.
public class ChatTopicMigrationTests
{
    private static Session Chat(string? name, string? topic = null, bool locked = false)
        => new() { Name = name, Topic = topic, NameLocked = locked };

    [Fact]
    public void Apply_StripsLeadingEmojiFromName()
    {
        var s = Chat("🐛 Правка авторизации");
        Assert.True(ChatTopicMigration.Apply([s]));
        Assert.Equal("Правка авторизации", s.Name);
        // Маппинга эмодзи→lucide-имя нет: значок не восстанавливаем (проставится batch'ем)
        Assert.Null(s.Topic);
    }

    // Селектор вариации (U+FE0F) и ZWJ — первая графема срезается целиком
    [Fact]
    public void Apply_StripsVariationSelectorAndZwj()
    {
        var a = Chat("⚙️ Настройка путей");
        var b = Chat("👨‍💻 Разбор кода");
        ChatTopicMigration.Apply([a, b]);
        Assert.Equal("Настройка путей", a.Name);
        Assert.Equal("Разбор кода", b.Name);
    }

    // Имя задано человеком — не трогаем
    [Fact]
    public void Apply_SkipsNameLocked()
    {
        var s = Chat("🐛 Моё имя", locked: true);
        Assert.False(ChatTopicMigration.Apply([s]));
        Assert.Equal("🐛 Моё имя", s.Name);
    }

    // Имя из одного значка — пустой остаток, имя оставляем
    [Fact]
    public void Apply_EmojiOnlyNameKept()
    {
        var s = Chat("🐛");
        ChatTopicMigration.Apply([s]);
        Assert.Equal("🐛", s.Name);
    }

    // Устаревший ключ темы (с маленькой буквы) — не PascalCase, обнуляем: фронт icons["bug"]
    // ничего не найдёт, значок проставится заново lucide-именем через batch
    [Fact]
    public void Apply_ClearsLegacyLowercaseTopic()
    {
        var s = Chat("Правка", topic: "bug");
        Assert.True(ChatTopicMigration.Apply([s]));
        Assert.Null(s.Topic);
    }

    // PascalCase-имя lucide (Cat, Bug) — валидно, обнулять не надо
    [Fact]
    public void Apply_KeepsPascalCaseTopic()
    {
        var s = Chat("Кошка", topic: "Cat");
        Assert.False(ChatTopicMigration.Apply([s]));
        Assert.Equal("Cat", s.Topic);
    }

    [Fact]
    public void Apply_PlainNamesUntouched()
    {
        var a = Chat("Обычное имя");
        var b = Chat(null);
        Assert.False(ChatTopicMigration.Apply([a, b]));
        Assert.Null(a.Topic);
        Assert.Equal("Обычное имя", a.Name);
    }

    // Идемпотентность: второй прогон ничего не меняет
    [Fact]
    public void Apply_IsIdempotent()
    {
        var s = Chat("🐛 Правка", topic: "bug");
        Assert.True(ChatTopicMigration.Apply([s]));
        Assert.False(ChatTopicMigration.Apply([s]));
        Assert.Equal("Правка", s.Name);
        Assert.Null(s.Topic);
    }
}
