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
}
