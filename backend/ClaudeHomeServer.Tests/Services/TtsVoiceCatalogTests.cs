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
}
