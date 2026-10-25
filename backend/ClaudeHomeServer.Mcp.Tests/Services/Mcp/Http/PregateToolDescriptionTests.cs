using System.Text.Json;
using ClaudeHomeServer.Services.Mcp.Http;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.Mcp.Http;

// Описание инструмента pregate ↔ секция Pregate в appsettings.json и политика предупреждений
// в backend/Directory.Build.props. Расхождение слоёв читалось как доказательство строгой сборки:
// исполнитель и ревьюер видели зелёный предгейт и считали, что все предупреждения — в ошибку,
// тогда как в ошибку идут только nullable и xUnit1031 (полный гейт остался в CI).
public class PregateToolDescriptionTests
{
    private static string ServerDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (; dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "ClaudeHomeServer", "appsettings.json");
            if (File.Exists(candidate)) return Path.Combine(dir.FullName, "ClaudeHomeServer");
        }
        throw new FileNotFoundException("Не найден appsettings.json сервера от " + AppContext.BaseDirectory);
    }

    private static string Description()
    {
        var tool = TestsToolset.Tools.Single(t => t.Name == TestsToolset.PregateName);
        return tool.Description;
    }

    private static JsonElement Checks()
    {
        var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(ServerDir(), "appsettings.json")));
        return doc.RootElement.GetProperty("Pregate").GetProperty("Checks").Clone();
    }

    // Описание не обещает строгой сборки: «-warnaserror» в нём означал бы гейт, которого
    // в предгейте нет (в предгейте — только nullable и xUnit1031, см. следующий тест)
    [Fact]
    public void ОписаниеПредгейта_НеОбещаетСтрогуюСборку()
    {
        var description = Description();

        description.Should().NotContain("warnaserror",
            "предгейт собирает без полного гейта предупреждений — обещание строгой сборки вводит в заблуждение");
        description.Should().Contain("nullable").And.Contain("xUnit1031",
            "фактическая строгость сборки предгейта — эти два кода, их и надо назвать");
    }

    // Каждая проверка из секции Pregate названа в описании: добавили проверку в конфиг —
    // описание обязано её упомянуть, иначе исполнитель не знает, что прогоняется
    [Fact]
    public void ОписаниеПредгейта_НазываетКаждуюПроверкуИзКонфига()
    {
        var description = Description();

        foreach (var check in Checks().EnumerateArray())
        {
            var name = check.GetProperty("Name").GetString()!;
            description.Should().Contain(name, $"проверка «{name}» есть в секции Pregate, но не названа в описании инструмента");
        }
    }

    // Политика предупреждений в Directory.Build.props и обещание описания — одно и то же:
    // description называет nullable и xUnit1031, а в props в ошибку идут именно эти коды
    [Fact]
    public void ОписаниеПредгейта_СовпадаетСПолитикойПредупреждений()
    {
        var props = File.ReadAllText(Path.Combine(Directory.GetParent(ServerDir())!.FullName, "Directory.Build.props"));
        var warningsAsErrors = props.Split('\n')
            .First(l => l.Contains("<WarningsAsErrors>"))
            .Trim();

        warningsAsErrors.Should().Contain("nullable").And.Contain("xUnit1031",
            "в ошибку в предгейте должны идти nullable и xUnit1031 — иначе описание врёт");
        Description().Should().Contain("nullable").And.Contain("xUnit1031");
    }
}
