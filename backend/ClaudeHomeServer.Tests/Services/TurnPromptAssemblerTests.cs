using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;
using VoicePrompts = ClaudeHomeServer.Services.Prompts.VoicePrompts;

namespace ClaudeHomeServer.Tests.Services;

// Склейка системного промпта хода из секций. Сторож главного инварианта фичи «какой промпт
// ушёл»: показанное пользователю и отправленное модели собираются из ОДНОГО списка секций,
// поэтому разойтись не могут. Раньше текст копился отдельной переменной, и тихая потеря
// секции в рефакторинге не поймалась бы ничем.
public class TurnPromptAssemblerTests
{
    private static PromptSectionDto S(string key, string text, string kind = "system") =>
        new(key, key, text, kind);

    [Fact]
    public void Секции_КлеятсяЧерезПустуюСтроку_ВПорядкеСписка()
    {
        var text = TurnPromptAssembler.Combine([S("a", "Первая"), S("b", "Вторая")], null);

        text.Should().Be("Первая\n\nВторая");
    }

    [Fact]
    public void ПустыеСекции_НеДаютДвойныхРазделителей()
    {
        var text = TurnPromptAssembler.Combine(
            [S("a", "Первая"), S("empty", "   "), S("b", "Вторая")], null);

        text.Should().Be("Первая\n\nВторая");
        text.Should().NotContain("\n\n\n");
    }

    [Fact]
    public void СлойПерсоны_ОтделяетсяГоризонтальнойЧертой()
    {
        var text = TurnPromptAssembler.Combine([S("a", "Правила")], "Ты — Дмитрий");

        text.Should().Be("Правила\n\n---\n\nТы — Дмитрий");
    }

    [Fact]
    public void ТолькоСлойПерсоны_ИдётБезРазделителя()
    {
        var text = TurnPromptAssembler.Combine([], "Ты — Дмитрий");

        text.Should().Be("Ты — Дмитрий");
    }

    [Fact]
    public void ТекстХода_ВСистемныйПромптНеПопадает()
    {
        // Kind = turn — это сообщение с обвязками, оно уходит в stdin, а не в
        // --append-system-prompt: иначе «скопировать промпт» отдавало бы склейку,
        // которой модели никогда не отправляли
        var text = TurnPromptAssembler.Combine(
            [S("a", "Правила"), S("turn-text", "Почини сборку", "turn")], null);

        text.Should().Be("Правила");
    }

    [Fact]
    public void ПустойНабор_ДаётПустуюСтроку()
    {
        TurnPromptAssembler.Combine([], null).Should().BeEmpty();
    }

    [Fact]
    public void ГолосовойРежим_СлойПерсоныИдётПослеСекцииVoiceMode()
    {
        // Порядок — фундамент фичи голосового режима: слой персоны (с оговоркой
        // VoicePrompts.PersonaOverride в конце) обязан клеиться ПОСЛЕ секции voice-mode,
        // иначе оговорка теряет смысл «последнего слова»
        var personaLayer = "Ты — Дмитрий\n\n" + VoicePrompts.PersonaOverride;
        var text = TurnPromptAssembler.Combine(
            [S("voice-mode", VoicePrompts.SectionText)], personaLayer);

        text.IndexOf(VoicePrompts.PersonaOverride)
            .Should().BeGreaterThan(text.IndexOf(VoicePrompts.SectionText));
        text.Should().EndWith(VoicePrompts.PersonaOverride);
    }
}
