using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Чистый роутер группового чата: выбор спикера по @упоминаниям участников.
public class GroupChatRouterTests
{
    private static readonly Persona Lead = MakePersona("lead-id", "arhitektor");
    private static readonly Persona Dev = MakePersona("dev-id", "razrabotchik");
    private static readonly Persona Qa = MakePersona("qa-id", "tester-1");

    private static Persona MakePersona(string id, string handle)
    {
        // Id — init-only с автогенерацией; фиксируем свой через инициализатор
        var p = new Persona { Id = id, OwnerId = "u1", Name = handle, Handle = handle };
        return p;
    }

    private static readonly List<Persona> Team = [Lead, Dev, Qa];

    [Fact]
    public void Упоминание_в_начале_переключает_спикера()
    {
        var r = GroupChatRouter.Resolve("@razrabotchik посмотри баг", Team, Lead.Id);

        r.SpeakerPersonaId.Should().Be(Dev.Id);
        r.Switched.Should().BeTrue();
        r.AlsoMentioned.Should().BeEmpty();
    }

    [Fact]
    public void Упоминание_в_середине_текста_тоже_работает()
    {
        var r = GroupChatRouter.Resolve("Мне кажется, @tester-1 знает ответ", Team, Lead.Id);

        r.SpeakerPersonaId.Should().Be(Qa.Id);
        r.Switched.Should().BeTrue();
    }

    [Fact]
    public void Регистр_handle_не_важен()
    {
        var r = GroupChatRouter.Resolve("@RaZrAbOtChIk привет", Team, Lead.Id);

        r.SpeakerPersonaId.Should().Be(Dev.Id);
    }

    [Fact]
    public void Упоминание_неучастника_игнорируется_остаётся_текущий()
    {
        var r = GroupChatRouter.Resolve("@chuzhak что скажешь?", Team, Dev.Id);

        r.SpeakerPersonaId.Should().Be(Dev.Id);
        r.Switched.Should().BeFalse();
        r.AlsoMentioned.Should().BeEmpty();
    }

    [Fact]
    public void Несколько_упоминаний_первый_спикер_остальные_AlsoMentioned()
    {
        var r = GroupChatRouter.Resolve("@tester-1 обсуди с @arhitektor и @razrabotchik", Team, Dev.Id);

        r.SpeakerPersonaId.Should().Be(Qa.Id);
        r.Switched.Should().BeTrue();
        r.AlsoMentioned.Should().Equal(Lead.Id, Dev.Id);
    }

    [Fact]
    public void Повторное_упоминание_того_же_участника_не_дублируется()
    {
        var r = GroupChatRouter.Resolve("@razrabotchik и ещё раз @razrabotchik", Team, Lead.Id);

        r.SpeakerPersonaId.Should().Be(Dev.Id);
        r.AlsoMentioned.Should().BeEmpty();
    }

    [Fact]
    public void Без_упоминаний_остаётся_текущий_активный()
    {
        var r = GroupChatRouter.Resolve("продолжай, пожалуйста", Team, Qa.Id);

        r.SpeakerPersonaId.Should().Be(Qa.Id);
        r.Switched.Should().BeFalse();
    }

    [Fact]
    public void Текущий_выбыл_из_участников_отвечает_ведущая()
    {
        var r = GroupChatRouter.Resolve("что дальше?", Team, "deleted-persona");

        r.SpeakerPersonaId.Should().Be(Lead.Id);
        r.Switched.Should().BeTrue();
    }

    [Fact]
    public void Упоминание_текущего_спикера_не_считается_сменой()
    {
        var r = GroupChatRouter.Resolve("@razrabotchik продолжай", Team, Dev.Id);

        r.SpeakerPersonaId.Should().Be(Dev.Id);
        r.Switched.Should().BeFalse();
    }

    [Fact]
    public void Хэндл_внутри_email_не_считается_упоминанием()
    {
        var r = GroupChatRouter.Resolve("напиши на adres@razrabotchik.ru", Team, Lead.Id);

        r.SpeakerPersonaId.Should().Be(Lead.Id);
        r.Switched.Should().BeFalse();
    }
}
