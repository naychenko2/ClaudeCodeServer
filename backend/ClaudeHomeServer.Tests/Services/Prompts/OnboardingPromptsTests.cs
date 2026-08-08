using ClaudeHomeServer.Services.Prompts;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.Prompts;

// Промпт «Мастера настройки» (OnboardingPrompts.UserMaster, фича default-personas-onboarding).
// Живая заготовка (assistantPersonaId задан) — мастер дорабатывает её через personas_update,
// а не создаёт новую (соответствует серверному предохранителю в PersonasController.Create).
// Мёртвая заготовка (id null — резолв не удался) — деградация к пути «создай персону».
// Знакомство необязательное (план: «знакомство вместо обязательного онбординга») — текст не
// должен требовать ответить немедленно и не должен запрещать пропустить его насовсем.
public class OnboardingPromptsTests
{
    [Fact]
    public void UserMaster_СЖивойЗаготовкой_НеПредлагаетСоздатьНовую()
    {
        var text = OnboardingPrompts.UserMaster("Андрей", "persona-live-123", "Ассистент");

        text.Should().Contain("persona-live-123", "id заготовки должен попасть в инструкцию");
        text.Should().Contain("mcp__personas__personas_update",
            "живую заготовку дорабатывают через update, а не создают заново");
        text.Should().NotContain("2. По итогам создай персону инструментом mcp__personas__personas_create",
            "путь создания — только для деградации без заготовки");
        text.Should().Contain("НЕ вызывай personas_create",
            "явный запрет создавать дубликат при живой заготовке");
    }

    [Fact]
    public void UserMaster_БезЖивойЗаготовки_ПредлагаетСоздать()
    {
        var text = OnboardingPrompts.UserMaster("Андрей", assistantPersonaId: null);

        text.Should().Contain("mcp__personas__personas_create",
            "деградация: заготовка мертва — мастер создаёт персону с нуля");
        text.Should().NotContain("Дорабатывай уже созданного ассистента");
    }

    [Fact]
    public void UserMaster_ЗнакомствоНеобязательное_МожноОтложитьБезЗапрета()
    {
        var text = OnboardingPrompts.UserMaster("Андрей", "persona-live-123", "Ассистент");

        text.Should().Contain("прервать в любой момент",
            "знакомство — необязательный шаг, а не блокирующий гейт");
        text.Should().NotContain("нельзя пропустить");
        text.Should().NotContain("обязательно ответь");
    }
}
