using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Каталог провайдера glm в отгружаемом appsettings.json. Тест смотрит именно на РЕАЛЬНЫЙ
// файл, а не на синтетический конфиг: 19.08.2026 линейка пересобрана под живые пробы z.ai
// (glm-5.2/5.1/5 → алиасы glm-5.3, glm-4.5-air → алиас glm-4.7), и цена ошибки здесь —
// окно контекста хода: BuildCliEnv ставит CLAUDE_CODE_MAX_CONTEXT_TOKENS по ТОЧНОМУ id
// каталога, поэтому пин на исчезнувший id молча возвращает чат к окну по умолчанию.
public class GlmCatalogTests
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
            ["LlmProviders:glm:ApiKey"] = "zai-key",
            ["ClaudeUserProfileDir"] = TestConfig.EmptyClaudeProfileDir(),
        })
        .Build());

    [Fact]
    public void Каталог_СодержитТолькоДействующиеМодели()
    {
        var ids = RealRegistry().GetByKey("glm")!.Models.Select(m => m.Id).ToList();

        ids.Should().Equal("glm-5.3[1m]", "glm-5.3", "glm-4.7");
    }

    // Ради этого всё и затевалось: суффикс окна должен доезжать до CLI числом 1M
    [Fact]
    public void BuildCliEnv_Glm53_1m_СтавитОкно1M()
    {
        var env = RealRegistry().BuildCliEnv("glm-5.3[1m]")!;

        env["CLAUDE_CODE_MAX_CONTEXT_TOKENS"].Should().Be("1000000");
        env["ANTHROPIC_MODEL"].Should().Be("glm-5.3[1m]");
    }

    // Базовая запись намеренно с окном 200K: расширенное окно в Coding Plan включает
    // только суффикс (см. LlmProviders:glm#comment-models)
    [Fact]
    public void BuildCliEnv_Glm53_БезСуффикса_СтавитОкно200K()
    {
        RealRegistry().BuildCliEnv("glm-5.3")!["CLAUDE_CODE_MAX_CONTEXT_TOKENS"].Should().Be("200000");
    }

    // Уровни каталога обязаны указывать на существующие записи — иначе слот резолвится
    // в id, которого в каталоге нет, и окно снова теряется
    [Fact]
    public void Уровни_УказываютНаМоделиКаталога()
    {
        var glm = RealRegistry().GetByKey("glm")!;
        var ids = glm.Models.Select(m => m.Id).ToList();

        ids.Should().Contain(glm.TierStrong).And.Contain(glm.TierMedium).And.Contain(glm.TierWeak);
        ids.Should().Contain(glm.MediumModel).And.Contain(glm.SmallModel);
    }

    // Каждый адресат миграции обязан существовать в каталоге, а каждый источник — исчезнуть:
    // иначе миграция переписала бы пины на такой же мёртвый id
    [Fact]
    public void КартаМиграции_ИсточникиУшлиАдресатыНаМесте()
    {
        var ids = RealRegistry().GetByKey("glm")!.Models.Select(m => m.Id).ToList();

        foreach (var (from, to) in GlmModelAliasMigration.Map)
        {
            ids.Should().NotContain(from, "алиас z.ai в каталоге держать нельзя");
            ids.Should().Contain(to);
        }
    }

    // Прямой REST (CheapHttpSources) знает те же модели, кроме записи с суффиксом окна —
    // такого id у z.ai не существует
    [Fact]
    public void ПрямойREST_СодержитТеЖеМоделиБезСуффикса()
    {
        var config = new ConfigurationBuilder().AddJsonFile(AppSettingsPath()).Build();
        var ids = config.GetSection("CheapHttpSources:glm:Models").GetChildren()
            .Select(m => m["Id"]).ToList();

        ids.Should().Equal("glm-5.3", "glm-4.7");
    }
}
