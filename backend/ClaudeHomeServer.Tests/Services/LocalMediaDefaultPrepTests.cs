using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Turn;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Шаг 2 «локальная модель по умолчанию»: флаг в каталоге и единый признак «хода без человека»
public class LocalMediaDefaultPrepTests
{
    [Fact]
    public void Флаг_есть_в_каталоге_и_выключен_по_умолчанию()
    {
        var flag = FeatureFlagCatalog.All.SingleOrDefault(f => f.Key == "local-media-default");

        flag.Should().NotBeNull();
        flag!.Default.Should().BeFalse();
        FeatureFlagKeys.LocalMediaDefault.Should().Be("local-media-default");
    }

    [Fact]
    public void Обычный_ход_с_человеком() =>
        TurnAudience.IsUnattended(new Session(), agentDepth: 0).Should().BeFalse();

    [Fact]
    public void Исполнитель_задачи_без_человека() =>
        TurnAudience.IsUnattended(new Session { TaskExecution = true }, agentDepth: 0).Should().BeTrue();

    [Fact]
    public void Ход_правила_автоматизации_без_человека() =>
        TurnAudience.IsUnattended(new Session { AutomationRuleId = "rule-1" }, agentDepth: 0).Should().BeTrue();

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Делегированный_ход_без_человека(int depth) =>
        TurnAudience.IsUnattended(new Session(), depth).Should().BeTrue();
}
