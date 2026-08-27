using System.Text.Json;
using AiHomeDesktop.Core.Policies;
using AiHomeDesktop.Core.Protocol;
using FluentAssertions;
using Xunit;

namespace AiHomeDesktop.Core.Tests;

/// <summary>
/// Главное правило тоста (ADR-008 §8): человек подтверждает ДЕЙСТВИЕ, а не рассказ о нём.
/// Текст собирается из фактических аргументов вызова по белому списку полей, поэтому любое
/// «зачем» и «почему», дописанное моделью в args, до человека не доезжает.
/// </summary>
public class ConfirmationTextTests
{
    private static DesktopCallCommand Call(string kind, string argsJson, string? chat = "Ремонт стенда") =>
        new(DesktopProtocol.Version, "c1", kind, JsonDocument.Parse(argsJson).RootElement,
            15, true, 3, "s1", chat, 0);

    [Fact]
    public void МодельноеРезюме_ВТостНеПопадает()
    {
        var prompt = ConfirmationText.For(Call(
            DesktopCallKinds.Open,
            """{"target":"notepad.exe","summary":"я аккуратно открою отчёт","reason":"так надо"}"""));

        prompt.Text.Should().Contain("notepad.exe");
        prompt.Text.Should().NotContain("аккуратно", "резюме модели человек не видит никогда");
        prompt.Text.Should().NotContain("так надо");
    }

    [Fact]
    public void ИмяЧата_ЕстьВсегда()
    {
        var withName = ConfirmationText.For(Call(DesktopCallKinds.Screen, """{"scope":"screen"}"""));
        withName.ChatLine.Should().Contain("Ремонт стенда");

        // Без имени строка всё равно есть: тост без чата не отвечает на вопрос «кто просит»
        var noName = ConfirmationText.For(Call(DesktopCallKinds.Screen, """{"scope":"screen"}""", chat: null));
        noName.ChatLine.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ОбластьКадра_НазываетсяФактическая()
    {
        ConfirmationText.For(Call(DesktopCallKinds.Screen, """{"scope":"window","window":"Диспетчер задач"}"""))
            .Text.Should().Contain("Диспетчер задач");

        ConfirmationText.For(Call(DesktopCallKinds.Screen, "{}"))
            .Text.Should().Contain("Активное окно", "scope по умолчанию — окно");
    }

    [Fact]
    public void CallId_ЕдетВПромпте_ЧтобыОтменаГасилаСвойТост()
    {
        ConfirmationText.For(Call(DesktopCallKinds.Open, """{"target":"https://example.com"}"""))
            .CallId.Should().Be("c1");
    }
}
