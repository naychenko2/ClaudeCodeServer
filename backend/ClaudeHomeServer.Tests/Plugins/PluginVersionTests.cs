using ClaudeHomeServer.Models;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Plugins;

// Версия пакета плагина (ADR-031 §2.2, M3): semver MAJOR.MINOR.PATCH + опциональный
// pre-release. Валидация в Core; «..» отклоняется до SafePath.Join (иначе 500, не 400).
public class PluginVersionTests
{
    // --- Валидные версии ---

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("0.1.2")]
    [InlineData("10.20.30")]
    [InlineData("1.0.0-beta")]
    [InlineData("1.0.0-alpha.1")]
    [InlineData("1.0.0-rc.2")]
    public void IsValid_КорректнаяВерсия_Принята(string version)
    {
        PluginVersion.IsValid(version).Should().BeTrue(version);
    }

    // --- Невалидные версии (M3: 400, не 500) ---

    [Theory]
    [InlineData("..")]          // «..» — не semver
    [InlineData("1.0")]         // не хватает PATCH
    [InlineData("1")]           // только MAJOR
    [InlineData("1.0.0.0")]     // четыре сегмента
    [InlineData("v1.0.0")]      // префикс v
    [InlineData("1.0.0 ")]      // хвостовой пробел
    [InlineData("")]
    [InlineData(null)]
    public void IsValid_НекорректнаяВерсия_Отклонена(string? version)
    {
        PluginVersion.IsValid(version).Should().BeFalse(version);
    }

    // --- Нормализация ---

    [Fact]
    public void TryNormalize_Корректная_ВозвращаетКаноническую()
    {
        PluginVersion.TryNormalize("1.2.3", out var v).Should().BeTrue();
        v.Value.Should().Be("1.2.3");
    }

    [Fact]
    public void TryNormalize_Некорректная_ВозвращаетFalse()
    {
        PluginVersion.TryNormalize("..", out _).Should().BeFalse();
    }
}
