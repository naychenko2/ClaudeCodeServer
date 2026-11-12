using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Tests.Services;

// LLM-wiki S6b-1 + Ф2 NA-1: места Хранителя в каталоге (ADR-035 §7; Ф2 — решения 12, 12а).
// Каталог — единый источник правды и для диалога «Поставщики моделей»
// (UsageController.BuildOllamaInfo перечисляет LocalActionCatalog.All), и для маршрута —
// тест проверяет каталог, то же содержимое, которое видит диалог.
public class WikiKeeperCatalogTests
{
    [Fact]
    public void МестаХранителя_ЕстьВКаталоге_ГруппаЗаметки_СлотыПоADR()
    {
        var ingest = LocalActionCatalog.Find(LocalActionCatalog.WikiIngest);
        var fact = LocalActionCatalog.Find(LocalActionCatalog.WikiFact);

        Assert.NotNull(ingest);
        Assert.NotNull(fact);
        // Те же записи, что перечисляются в ответ мест (UsageController.cs:139)
        Assert.Contains(LocalActionCatalog.All, a => a.Key == LocalActionCatalog.WikiIngest);
        Assert.Contains(LocalActionCatalog.All, a => a.Key == LocalActionCatalog.WikiFact);

        Assert.Equal("Заметки", ingest.Group);
        Assert.Equal("Заметки", fact.Group);

        // Слот по умолчанию места — то, что диалог показывает в маршруте (tier:medium / tier:weak)
        Assert.Equal(ModelTier.Medium, LocalActionCatalog.DefaultTierOf(LocalActionCatalog.WikiIngest));
        Assert.Equal(ModelTier.Weak, LocalActionCatalog.DefaultTierOf(LocalActionCatalog.WikiFact));
    }

    [Fact]
    public void МестаФ2_ЕстьВКаталоге_ГруппаЗаметки_СлотыПоРешениям()
    {
        // wiki-nightly и wiki-triage — Ф2 (ADR-035, решения 12 и 12а)
        var nightly = LocalActionCatalog.Find(LocalActionCatalog.WikiNightly);
        var triage = LocalActionCatalog.Find(LocalActionCatalog.WikiTriage);

        Assert.NotNull(nightly);
        Assert.NotNull(triage);
        // Те же записи, что перечисляются в ответ мест (UsageController.cs:139)
        Assert.Contains(LocalActionCatalog.All, a => a.Key == LocalActionCatalog.WikiNightly);
        Assert.Contains(LocalActionCatalog.All, a => a.Key == LocalActionCatalog.WikiTriage);

        Assert.Equal("Заметки", nightly.Group);
        Assert.Equal("Заметки", triage.Group);
        Assert.True(nightly.DefaultLocal);
        Assert.True(triage.DefaultLocal);
        Assert.Equal(CheapProfile.Large, nightly.Profile);
        Assert.Equal(CheapProfile.Small, triage.Profile);

        // Слот по умолчанию места — то, что диалог показывает в маршруте (tier:medium / tier:weak)
        Assert.Equal(ModelTier.Medium, LocalActionCatalog.DefaultTierOf(LocalActionCatalog.WikiNightly));
        Assert.Equal(ModelTier.Weak, LocalActionCatalog.DefaultTierOf(LocalActionCatalog.WikiTriage));
    }

    [Fact]
    public void МестоСудьи_ОблачноеИСильное_ЗаписиНочиИОтбораНеТронуты()
    {
        // Судья спорных правок вики (J-J1a): место есть, оно НЕ локальное по умолчанию
        // (исполнителя выбирает админ в «Поставщиках моделей»), слот — сильный.
        var judge = LocalActionCatalog.Find(LocalActionCatalog.WikiJudge);
        Assert.NotNull(judge);
        Assert.Contains(LocalActionCatalog.All, a => a.Key == LocalActionCatalog.WikiJudge);

        Assert.Equal("Судья спорных правок вики", judge.Title);
        Assert.Equal("Заметки", judge.Group);
        Assert.Equal(CheapProfile.Large, judge.Profile);
        Assert.False(judge.DefaultLocal);
        Assert.Equal(ModelTier.Strong, judge.Tier);
        Assert.Equal(ModelTier.Strong, LocalActionCatalog.DefaultTierOf(LocalActionCatalog.WikiJudge));

        // Снимок записей ночи и отбора с master 648ff986e: эта задача их не меняет, и тест
        // ловит случайную правку полей (профиль, слот, локальные лимиты) как красную.
        var nightly = LocalActionCatalog.Find(LocalActionCatalog.WikiNightly);
        Assert.NotNull(nightly);
        Assert.Equal("wiki-nightly", nightly.Key);
        Assert.Equal("Ночной разбор вики", nightly.Title);
        Assert.Equal("Заметки", nightly.Group);
        Assert.Equal(CheapProfile.Large, nightly.Profile);
        Assert.True(nightly.DefaultLocal);
        Assert.Equal(ModelTier.Medium, nightly.Tier);
        Assert.Equal(65_536, nightly.NumCtx);
        Assert.Equal(16_384, nightly.NumPredict);
        Assert.Equal(600_000, nightly.TimeoutMs);

        var triage = LocalActionCatalog.Find(LocalActionCatalog.WikiTriage);
        Assert.NotNull(triage);
        Assert.Equal("wiki-triage", triage.Key);
        Assert.Equal("Отбор чатов для вики", triage.Title);
        Assert.Equal("Заметки", triage.Group);
        Assert.Equal(CheapProfile.Small, triage.Profile);
        Assert.True(triage.DefaultLocal);
        Assert.Equal(ModelTier.Weak, triage.Tier);
        Assert.Null(triage.NumCtx);
        Assert.Null(triage.NumPredict);
        Assert.Null(triage.TimeoutMs);
    }
}
