using ClaudeHomeServer.Services.Tts;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Белый список голосов синтеза: по нему проверяется имя из конфига
public class TtsVoiceCatalogTests
{
    [Theory]
    [InlineData("zahar")]   // достался от v1
    [InlineData("marina")]  // дефолт боевого конфига
    [InlineData("dasha")]   // появился только в v3
    [InlineData("masha")]
    [InlineData("madirus")] // старое написание
    [InlineData("madi_ru")] // и новое — одно и то же имя
    [InlineData("ZAHAR")]   // регистр не важен
    public void IsKnown_ПроверенныеГолоса_True(string voice)
    {
        TtsVoiceCatalog.IsKnown(voice).Should().BeTrue();
    }

    [Theory]
    [InlineData("zahra")]  // опечатка
    [InlineData("john")]   // англоязычный голос, у нас не используется
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsKnown_ЧужоеИмя_False(string? voice)
    {
        TtsVoiceCatalog.IsKnown(voice).Should().BeFalse();
    }

    [Fact]
    public void Resolve_НезнакомоеИмя_ПодставляетДефолт()
    {
        // Опечатка в конфиге не должна глушить озвучку: 503 фронт запоминает на всю
        // сессию и уходит на голос браузера до перезагрузки вкладки
        TtsVoiceCatalog.Resolve("зорро").Should().Be(TtsVoiceCatalog.Default);
        TtsVoiceCatalog.Resolve(null).Should().Be(TtsVoiceCatalog.Default);
        TtsVoiceCatalog.Resolve("").Should().Be(TtsVoiceCatalog.Default);
    }

    [Fact]
    public void Resolve_ЗнакомоеИмя_ВозвращаетЕгоБезПробелов()
    {
        TtsVoiceCatalog.Resolve("dasha").Should().Be("dasha");
        TtsVoiceCatalog.Resolve("  jane  ").Should().Be("jane");
    }

    [Fact]
    public void Default_СамПроходитПроверку()
    {
        TtsVoiceCatalog.IsKnown(TtsVoiceCatalog.Default).Should().BeTrue();
    }

    // --- Каталог для формы выбора голоса ---

    [Fact]
    public void All_УКаждогоГолосаЕстьПодпись()
    {
        // Выбрать голос глазами нельзя: имя вроде madi_ru человеку не говорит ничего
        TtsVoiceCatalog.All.Should().OnlyContain(v => !string.IsNullOrWhiteSpace(v.Label));
        TtsVoiceCatalog.All.Should().HaveCountGreaterThan(10);
    }

    [Fact]
    public void All_БезДубляОдногоГолосаПодДвумяИменами()
    {
        // madirus и madi_ru — одно и то же: в списке им место одно
        var names = TtsVoiceCatalog.All.Select(v => v.Voice).ToList();
        names.Should().OnlyHaveUniqueItems();
        names.Should().NotContain("madirus", "наружу выходит каноническое имя, алиас — только для проверки");
        names.Should().Contain("madi_ru");
    }

    [Fact]
    public void All_ГолосаКаталогаПроходятБелыйСписок()
    {
        // Иначе форма предложит голос, который сервер потом не примет
        TtsVoiceCatalog.All.Should().OnlyContain(v => TtsVoiceCatalog.IsKnown(v.Voice));
    }

    [Fact]
    public void All_РолиСовпадаютСКартойАмплуа()
    {
        foreach (var v in TtsVoiceCatalog.All)
            v.Roles.Should().BeEquivalentTo(TtsVoiceCatalog.RolesFor(v.Voice),
                $"список амплуа {v.Voice} в каталоге и в проверке — один источник");
    }

    [Fact]
    public void Canonical_АлиасПриводитсяКИмениИзКаталога()
    {
        // Персона, которой голос ставили алиасом, должна найтись в списке формы
        TtsVoiceCatalog.Canonical("madirus").Should().Be("madi_ru");
        TtsVoiceCatalog.Canonical("MADIRUS").Should().Be("madi_ru");
        TtsVoiceCatalog.Canonical("  masha ").Should().Be("masha");
        TtsVoiceCatalog.Canonical("зорро").Should().BeNull();
        TtsVoiceCatalog.Canonical(null).Should().BeNull();
    }

    [Fact]
    public void Canonical_ЛюбоеИмяБелогоСписка_ЕстьВКаталоге()
    {
        // Двусторонняя проверка: нет голоса без подписи и подписи без голоса
        foreach (var voice in new[] { "alena", "jane", "omazh", "marina", "filipp", "zahar", "ermil",
                     "madirus", "madi_ru", "dasha", "julia", "lera", "masha", "alexander", "kirill", "anton" })
        {
            var canonical = TtsVoiceCatalog.Canonical(voice);
            canonical.Should().NotBeNull($"{voice} есть в белом списке");
            TtsVoiceCatalog.All.Should().Contain(v => v.Voice == canonical);
        }
    }

    [Fact]
    public void All_ГолосаБезАмплуа_ПустойСписокАНеОтсутствие()
    {
        TtsVoiceCatalog.All.Single(v => v.Voice == "filipp").Roles.Should().BeEmpty();
        TtsVoiceCatalog.All.Single(v => v.Voice == "masha").Roles.Should().HaveCount(3);
    }
}
