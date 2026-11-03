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
        // Двусторонняя проверка: нет голоса без подписи и подписи без голоса. Перебираем сам
        // белый список, а не его копию: иначе голос, добавленный без подписи, тест не заметит
        TtsVoiceCatalog.KnownNames.Should().NotBeEmpty();
        foreach (var voice in TtsVoiceCatalog.KnownNames)
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

    // Амплуа новых голосов v3 (живые запросы 24.10.2026): лишняя роль дала бы 400 у Яндекса,
    // недостающая — спрятала бы рабочее амплуа из формы
    [Theory]
    [InlineData("saule_ru", new[] { "strict", "whisper" })]
    [InlineData("zamira_ru", new[] { "strict", "friendly" })]
    [InlineData("zhanar_ru", new[] { "strict", "friendly" })]
    [InlineData("yulduz_ru", new[] { "strict", "friendly", "whisper" })]
    public void All_НовыеГолосаV3_ЖенскиеСоСвоимиАмплуа(string voice, string[] roles)
    {
        var info = TtsVoiceCatalog.All.Single(v => v.Voice == voice);
        info.Gender.Should().Be(TtsVoiceCatalog.Gender.Female);
        info.Roles.Should().BeEquivalentTo(roles);
    }

    // --- LiveTTS: модель — поле каталога ---

    private static readonly string[] LiveTtsVoices = ["sofia", "vera", "irina", "denis", "sergey", "vasily"];

    // Амплуа LiveTTS замерены живыми запросами 24.10.2026 (MP3, без speed); neutral не пишем:
    // у четырёх голосов явный neutral — 400, у denis и irina он равен «без роли»
    [Theory]
    [InlineData("sofia", TtsVoiceCatalog.Gender.Female, new[] { "casual", "support" })]
    [InlineData("vera", TtsVoiceCatalog.Gender.Female, new[] { "casual", "support" })]
    [InlineData("irina", TtsVoiceCatalog.Gender.Female, new[] { "formal", "support", "narrator", "sales" })]
    [InlineData("denis", TtsVoiceCatalog.Gender.Male, new[] { "casual", "formal", "sales" })]
    [InlineData("sergey", TtsVoiceCatalog.Gender.Male, new[] { "support", "sales" })]
    [InlineData("vasily", TtsVoiceCatalog.Gender.Male, new[] { "support", "sales" })]
    public void LiveTts_ГолосаСМодельюПоломИАмплуа(string voice, TtsVoiceCatalog.Gender gender, string[] roles)
    {
        var info = TtsVoiceCatalog.All.Single(v => v.Voice == voice);
        info.Model.Should().Be(TtsVoiceCatalog.LiveTts);
        info.Gender.Should().Be(gender);
        info.Roles.Should().BeEquivalentTo(roles);
        info.Roles.Should().NotContain("neutral");
        TtsVoiceCatalog.ModelFor(voice).Should().Be(TtsVoiceCatalog.LiveTts);
        TtsVoiceCatalog.SupportsSpeed(voice).Should().BeFalse();
    }

    [Fact]
    public void Модель_УКаждогоГолоса_ИLiveTtsТолькоУШести()
    {
        TtsVoiceCatalog.All.Should().OnlyContain(v =>
            v.Model == TtsVoiceCatalog.General || v.Model == TtsVoiceCatalog.LiveTts);
        TtsVoiceCatalog.All.Where(v => v.Model == TtsVoiceCatalog.LiveTts).Select(v => v.Voice)
            .Should().BeEquivalentTo(LiveTtsVoices);
        TtsVoiceCatalog.ModelFor("zahar").Should().Be(TtsVoiceCatalog.General);
        TtsVoiceCatalog.ModelFor("madirus").Should().Be(TtsVoiceCatalog.General);
        TtsVoiceCatalog.ModelFor("зорро").Should().Be(TtsVoiceCatalog.General);
        TtsVoiceCatalog.SupportsSpeed("zahar").Should().BeTrue();
    }

    [Fact]
    public void LiveTts_ВКонцеСвоихГрупп_НикогдаНеПервыми()
    {
        // От порядка зависит «первый голос» редактора звука: дорогая модель не должна стать
        // умолчанием ни в одной группе
        foreach (var gender in new[] { TtsVoiceCatalog.Gender.Female, TtsVoiceCatalog.Gender.Male })
        {
            var group = TtsVoiceCatalog.All.Where(v => v.Gender == gender).Select(v => v.Model).ToList();
            group[0].Should().Be(TtsVoiceCatalog.General);
            var firstLive = group.IndexOf(TtsVoiceCatalog.LiveTts);
            firstLive.Should().BePositive();
            group.Skip(firstLive).Should().OnlyContain(m => m == TtsVoiceCatalog.LiveTts,
                "после первого голоса LiveTTS в группе идут только голоса LiveTTS");
        }
    }

    [Fact]
    public void LiveTts_ВсегдаВКаталоге()
    {
        TtsVoiceCatalog.All.Select(v => v.Voice).Should().Contain(LiveTtsVoices);
        TtsVoiceCatalog.IsKnown("sofia").Should().BeTrue();
        TtsVoiceCatalog.Canonical("SOFIA").Should().Be("sofia");
    }
}
