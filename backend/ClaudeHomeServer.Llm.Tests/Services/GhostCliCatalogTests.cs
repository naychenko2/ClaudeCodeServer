using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Каталог провайдера ghostcli в отгружаемом appsettings.json. Тест смотрит именно на РЕАЛЬНЫЙ
// файл, а не на синтетический конфиг: цена ошибки здесь — формат id моделей. GhostCLI — прокси
// нативных Claude-моделей, и id уходят «как есть» в их API: любая подмена (префикс ghostcli-,
// точка вместо дефиса в номере версии) → модель не найдена в их каталоге → 400/404. Окно
// контекста (256K) доезжает до CLI числом CLAUDE_CODE_MAX_CONTEXT_TOKENS, поэтому пин на
// исчезнувший id молча возвращал бы чат к окну по умолчанию.
public class GhostCliCatalogTests
{
    // Отгружаемый appsettings.json сервера: ищем вверх от каталога сборки тестов —
    // путь одинаково работает и из bin проекта, и из общего --artifacts-path, и на CI
    private static string AppSettingsPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (; dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "ClaudeHomeServer", "appsettings.json");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Не найден appsettings.json сервера от " + AppContext.BaseDirectory);
    }

    // Реальный каталог + ключ провайдера (в отгружаемом файле ApiKey пустой = выключен)
    private static LlmProviderRegistry RealRegistry() => new(new ConfigurationBuilder()
        .AddJsonFile(AppSettingsPath())
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LlmProviders:ghostcli:ApiKey"] = "gcli-key",
            ["ClaudeUserProfileDir"] = TestConfig.EmptyClaudeProfileDir(),
        })
        .Build());

    [Fact]
    public void Каталог_СодержитТриМодели()
    {
        var ids = RealRegistry().GetByKey("ghostcli")!.Models.Select(m => m.Id).ToList();

        ids.Should().Equal("claude-opus-5-5", "claude-sonnet-5", "claude-fable-5.1");
    }

    // Ради этого всё и затевалось: окно 256K обязано доехать до CLI числом, а не схлопнуться
    // в 200K (CLI не знает сторонних id и по умолчанию держит 200K)
    [Theory]
    [InlineData("claude-opus-5-5")]
    [InlineData("claude-sonnet-5")]
    [InlineData("claude-fable-5.1")]
    public void Окно_ВсехМоделей_256K(string model)
    {
        RealRegistry().BuildCliEnv(model)!["CLAUDE_CODE_MAX_CONTEXT_TOKENS"].Should().Be("256000");
    }

    // Уровни каталога обязаны указывать на существующие записи — иначе слот резолвится
    // в id, которого в каталоге нет, и окно снова теряется
    [Fact]
    public void Уровни_УказываютНаМоделиКаталога()
    {
        var g = RealRegistry().GetByKey("ghostcli")!;
        var ids = g.Models.Select(m => m.Id).ToList();

        ids.Should().Contain(g.TierStrong).And.Contain(g.TierMedium).And.Contain(g.TierWeak);
        ids.Should().Contain(g.MediumModel).And.Contain(g.SmallModel);
    }

    // Префикс claude- обязателен: id моделей GhostCLI начинаются с claude-, и резолв по
    // точному id из каталога идёт ДО префиксного матча, поэтому коллизии с native Claude нет,
    // но без явного ModelPrefixes EffectiveModelPrefixes схлопнулся бы в Key ("ghostcli")
    [Fact]
    public void ModelPrefixes_СодержитClaude()
    {
        RealRegistry().GetByKey("ghostcli")!.ModelPrefixes.Should().Contain("claude-");
    }
}
