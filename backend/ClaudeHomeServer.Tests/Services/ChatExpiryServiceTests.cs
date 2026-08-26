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
        DateTime? updatedAt = null,
        DateTime? expiryAnchor = null) => new()
        {
            ExpiresAfterMinutes = expiresAfterMinutes,
            Status = status,
            UpdatedAt = updatedAt ?? Now.AddHours(-2),
            ExpiryAnchor = expiryAnchor,
        };

    // === Уборка архива (ShouldPurgeArchived) ===
    // Правило независимое от временных чатов: включается ключом Session:ArchiveRetentionDays,
    // отсчёт идёт от момента архивации

    [Fact]
    public void ShouldPurgeArchived_РетенцияВыключена_False()
    {
        // Дефолт продукта: архив вечен, сколько бы чат в нём ни лежал
        var chat = new Session { ArchivedAt = Now.AddYears(-1) };
        ChatExpiryService.ShouldPurgeArchived(chat, Now, 0).Should().BeFalse();
    }

    [Fact]
    public void ShouldPurgeArchived_НеАрхивныйЧат_False()
    {
        var chat = new Session { ArchivedAt = null, UpdatedAt = Now.AddYears(-1) };
        ChatExpiryService.ShouldPurgeArchived(chat, Now, 30).Should().BeFalse();
    }

    [Fact]
    public void ShouldPurgeArchived_СрокПрошёл_True()
    {
        var chat = new Session { ArchivedAt = Now.AddDays(-31) };
        ChatExpiryService.ShouldPurgeArchived(chat, Now, 30).Should().BeTrue();
    }

    [Fact]
    public void ShouldPurgeArchived_СрокНеПрошёл_False()
    {
        var chat = new Session { ArchivedAt = Now.AddDays(-29) };
        ChatExpiryService.ShouldPurgeArchived(chat, Now, 30).Should().BeFalse();
    }

    [Fact]
    public void ShouldPurgeArchived_СчитаетОтАрхивацииАНеОтАктивности_False()
    {
        // Чат пролежал без движения год, а в архив убран вчера — сносить его нельзя
        var chat = new Session { ArchivedAt = Now.AddDays(-1), UpdatedAt = Now.AddDays(-365) };
        ChatExpiryService.ShouldPurgeArchived(chat, Now, 30).Should().BeFalse();
    }

    [Theory]
    [InlineData(SessionStatus.Working)]
    [InlineData(SessionStatus.Waiting)]
    public void ShouldPurgeArchived_ХодИдёт_False(SessionStatus status)
    {
        // В архивном чате может доигрывать исполнитель задачи — удаление посреди работы недопустимо
        var chat = new Session { ArchivedAt = Now.AddDays(-90), Status = status };
        ChatExpiryService.ShouldPurgeArchived(chat, Now, 30).Should().BeFalse();
    }

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

    [Fact]
    public void ShouldExpire_СрокЗадалиТолькоЧтоНаСтаромЧате_False()
    {
        // Активности не было пять дней, но час хранения выбран минуту назад: отсчёт идёт
        // от якоря, иначе чат исчез бы на ближайшем тике сразу после выбора срока
        var chat = Chat(expiresAfterMinutes: 60,
            updatedAt: Now.AddDays(-5), expiryAnchor: Now.AddMinutes(-1));
        ChatExpiryService.ShouldExpire(chat, Now).Should().BeFalse();
    }

    [Fact]
    public void ShouldExpire_ЯкорьСтарееАктивности_СчитаемОтАктивности()
    {
        // Срок задали давно, а переписка шла 10 минут назад — якорь роли не играет
        var chat = Chat(expiresAfterMinutes: 60,
            updatedAt: Now.AddMinutes(-10), expiryAnchor: Now.AddDays(-3));
        ChatExpiryService.ShouldExpire(chat, Now).Should().BeFalse();
        ChatExpiryService.CountFrom(chat).Should().Be(Now.AddMinutes(-10));
    }

    [Fact]
    public void ShouldExpire_ПослеЯкоряСрокПрошёл_True()
    {
        // Якорь двухчасовой давности, TTL час — чат просрочен и без новой активности
        var chat = Chat(expiresAfterMinutes: 60,
            updatedAt: Now.AddDays(-5), expiryAnchor: Now.AddHours(-2));
        ChatExpiryService.ShouldExpire(chat, Now).Should().BeTrue();
    }
}
