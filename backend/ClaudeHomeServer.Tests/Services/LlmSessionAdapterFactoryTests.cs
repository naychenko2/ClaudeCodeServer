using ClaudeHomeServer.Services.Llm;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Склейка источников оценки контекста (ADR-007 §4.4, задача «Реестр ёмкости не наполняется»):
// живое значение (usage текущего хода) приоритет; при его отсутствии — фолбэк на историю чата;
// нет нигде — 0 (фильтр fail-open, наблюдение не записывается). Контракт адаптера (Func<int>) не
// меняется, источник («живая»/«из истории»/«нет») идёт параллельным Func<string> для диагностики.
// ComposeContext вынесен в чистую функцию, чтобы тестировать композицию без подъёма фабрики.
public class LlmSessionAdapterFactoryTests
{
    [Fact]
    public void ComposeContext_ЖиваяПриоритет_ИсториюНеЗатирает()
    {
        // Живая оценка (от usage текущего хода) точнее истории (та — от ПРЕДЫДУЩЕГО хода): при
        // наличии живой история не нужна. Это инвариант «значение из истории не затирает более
        // свежее живое».
        var (tokens, source) = LlmSessionAdapterFactory.ComposeContext(live: 100_000, fromHistory: 200_000);

        tokens.Should().Be(100_000);
        source.Should().Be("живая");
    }

    [Fact]
    public void ComposeContext_ЖивойНет_БерётсяИзИстории()
    {
        // Живое значение 0 — ход упал до assistant-сообщения / рестарт / холодный старт чата:
        // оценка берётся из истории (последний StoredResultMessage.ContextTokens>0).
        var (tokens, source) = LlmSessionAdapterFactory.ComposeContext(live: 0, fromHistory: 80_000);

        tokens.Should().Be(80_000);
        source.Should().Be("из истории");
    }

    [Fact]
    public void ComposeContext_НетНигде_НольНейтральный()
    {
        // Оценки нет совсем (новый чат, нет истории) — 0: WouldFit уходит в fail-open, RecordOverflow
        // отсекается guard'ом contextTokens<=0. Источник «нет» — для честной диагностики в логе.
        var (tokens, source) = LlmSessionAdapterFactory.ComposeContext(live: 0, fromHistory: null);

        tokens.Should().Be(0);
        source.Should().Be("нет");
    }

    [Fact]
    public void ComposeContext_НулеваяИстория_КакБезНеё()
    {
        // fromHistory = 0 (последний result с ContextTokens=0/null и предыдущих нет) — нет оценки:
        // нулевая история не лучше её отсутствия, фолбэк на неё бессмысленен.
        var (tokens, source) = LlmSessionAdapterFactory.ComposeContext(live: 0, fromHistory: 0);

        tokens.Should().Be(0);
        source.Should().Be("нет");
    }
}
