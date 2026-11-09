using ClaudeHomeServer.Services.Composition;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Composition;

// Ревью R-S3-LLM-CORE, находка 1: контракт ADR-014 п.5 («`llm` не отключается») не охранялся
// ни одним тестом — ветка проверки в Program.cs не исполнялась ни в одном прогоне (ни один тест
// не выставлял `Subsystems:llm:Enabled=false`), и снять её при слиянии было нечем. Инвариант,
// который закрепляет этот тест: конфиг с `Subsystems:llm:Enabled=false` даёт отказ старта с
// явной причиной ДО сборки контейнера — а не «No service for type 'LlmProviderRegistry' has
// been registered» глубже в запуске.
//
// Тест бьёт в производителя (в само тело проверки в NonDisablableSubsystems), а не в хост:
// через TestWebApplicationFactory эта ветка недостижима в принципе — top-level statements
// Program.cs выполняются раньше, чем фабрика подмешивает ExtraConfig (та же грабля
// `LlmOff`-тестов, ревью п.5). Поэтому здесь чистый юнит на конфиге, без DI и без хоста.
public class NonDisablableSubsystemsTests
{
    // Текст отказа — тот, что требовал ревью (п.4: «текст сообщения совпадает с предложением
    // архитектора»). По нему отказ опознаётся в логе старта, поэтому проверяется равенством,
    // а не подстрокой.
    private const string ReasonText =
        "Подсистема llm не отключается: ядро сессий зависит от неё (ADR-014, „Неотключаемые подсистемы“)";

    [Theory]
    [InlineData("false", true)]  // явный выключатель → отказ старта с явной причиной
    [InlineData("true", false)]  // явный включатель → обычная загрузка
    [InlineData(null, false)]    // секции нет → дефолт гейта «включена» (IAppSubsystem.cs:135)
    public void Llm_Отказывается_Только_НаЯвномВыключении(string? enabledValue, bool shouldThrow)
    {
        var builder = new ConfigurationBuilder();
        if (enabledValue is not null)
        {
            builder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Subsystems:llm:Enabled"] = enabledValue,
            });
        }
        var config = builder.Build();

        var act = () => NonDisablableSubsystems.EnsureLlmEnabled(config);

        if (shouldThrow)
        {
            act.Should().Throw<InvalidOperationException>()
                .Which.Message.Should().Be(ReasonText);
        }
        else
        {
            act.Should().NotThrow("llm включена по умолчанию — отказ старта был бы поломкой запуска");
        }
    }

    // Гейт по ключу — не про другой ключ: проверка обязана читать именно `llm` (мутация
    // «Subsystems:llm» → «Subsystems:notes» краснеет здесь). Конфиг с выключенными notes и
    // включённой llm не должен отказывать: иначе проверка перестала бы быть про llm.
    [Fact]
    public void Llm_НеОтказывается_КогдаВыключенаДругаяПодсистема()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Subsystems:notes:Enabled"] = "false",
            })
            .Build();

        var act = () => NonDisablableSubsystems.EnsureLlmEnabled(config);
        act.Should().NotThrow();
    }
}
