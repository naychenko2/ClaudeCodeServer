using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Тесты предиката авто-удаления временных чатов (ShouldExpire)
public class ChatExpiryServiceTests
{
    // «Сейчас»: 2026-07-11 12:00 UTC
    private static readonly DateTime Now = new(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc);

    private static Session Chat(
        int? expiresAfterMinutes = null,
        SessionStatus status = SessionStatus.Finished,
        DateTime? updatedAt = null) => new()
    {
        ExpiresAfterMinutes = expiresAfterMinutes,
        Status = status,
        UpdatedAt = updatedAt ?? Now.AddHours(-2),
    };

    [Fact]
    public void ShouldExpire_СрокПрошёл_True()
    {
        // TTL 60 мин, последняя активность 2 часа назад
        var chat = Chat(expiresAfterMinutes: 60);
        ChatExpiryService.ShouldExpire(chat, Now).Should().BeTrue();
    }

    [Fact]
    public void ShouldExpire_СрокНеПрошёл_False()
    {
        // TTL 24 часа, последняя активность 2 часа назад
        var chat = Chat(expiresAfterMinutes: 1440);
        ChatExpiryService.ShouldExpire(chat, Now).Should().BeFalse();
    }

    [Fact]
    public void ShouldExpire_ОбычныйЧат_False()
    {
        var chat = Chat(expiresAfterMinutes: null, updatedAt: Now.AddDays(-365));
        ChatExpiryService.ShouldExpire(chat, Now).Should().BeFalse();
    }

    [Theory]
    [InlineData(SessionStatus.Working)]
    [InlineData(SessionStatus.Waiting)]
    public void ShouldExpire_ХодИдёт_False(SessionStatus status)
    {
        // Просроченный, но с идущим ходом — не удаляем посреди работы
        var chat = Chat(expiresAfterMinutes: 60, status: status);
        ChatExpiryService.ShouldExpire(chat, Now).Should().BeFalse();
    }

    [Theory]
    // Starting = «создан, ходов не было»: пустой временный чат тоже должен удаляться
    [InlineData(SessionStatus.Starting)]
    [InlineData(SessionStatus.Active)]
    [InlineData(SessionStatus.Finished)]
    [InlineData(SessionStatus.Error)]
    [InlineData(SessionStatus.Orphaned)]
    public void ShouldExpire_ПокоящийсяСтатус_True(SessionStatus status)
    {
        var chat = Chat(expiresAfterMinutes: 60, status: status);
        ChatExpiryService.ShouldExpire(chat, Now).Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ShouldExpire_НекорректныйTtl_False(int ttl)
    {
        var chat = Chat(expiresAfterMinutes: ttl);
        ChatExpiryService.ShouldExpire(chat, Now).Should().BeFalse();
    }

    [Fact]
    public void ShouldExpire_РовноНаГранице_True()
    {
        // Дедлайн включительно: прошло ровно TTL
        var chat = Chat(expiresAfterMinutes: 60, updatedAt: Now.AddMinutes(-60));
        ChatExpiryService.ShouldExpire(chat, Now).Should().BeTrue();
    }
}
