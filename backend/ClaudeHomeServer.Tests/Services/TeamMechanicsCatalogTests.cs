using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.Prompts;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Мост в командные механики (фича default-personas-onboarding): серверный мини-каталог
// TeamMechanicsPromptCatalog обязан совпадать по id с union TeamMechanicId фронтового
// реестра (frontend/src/features/team/teamMechanics.ts) — тест читает фронтовый файл
// напрямую, третьей копии списка нет. Путь строится от корня репозитория через
// Path.Combine (без Windows-литералов — CI гоняет тесты на Linux).
public class TeamMechanicsCatalogTests
{
    private static string? FindTeamMechanicsTs()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName,
                "frontend", "src", "features", "team", "teamMechanics.ts");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    [SkippableFact]
    public void КаталогМеханик_СовпадаетПоId_СФронтовымРеестром()
    {
        var path = FindTeamMechanicsTs();
        Skip.If(path is null, "teamMechanics.ts не найден (сборка вне дерева репозитория)");

        var source = File.ReadAllText(path!);
        var union = Regex.Match(source, @"export type TeamMechanicId =(?<body>[^;]*);",
            RegexOptions.Singleline);
        union.Success.Should().BeTrue("union TeamMechanicId обязан существовать в teamMechanics.ts");

        var frontIds = Regex.Matches(union.Groups["body"].Value, "'([A-Za-z]+)'")
            .Select(m => m.Groups[1].Value)
            .ToList();
        frontIds.Should().NotBeEmpty("union обязан перечислять id механик");

        TeamMechanicsPromptCatalog.All.Select(m => m.Id)
            .Should().BeEquivalentTo(frontIds,
                "серверный каталог механик не смеет дрейфовать от TeamMechanicId фронта");
    }

    [Fact]
    public void БлокПромпта_ФильтруетсяПоСкиллам_ИДержитПротоколМаркера()
    {
        // Без скиллов остаются только механики без requiredSkill
        var none = TeamMechanicsPromptCatalog.BuildPromptBlock(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        none.Should().NotBeNull();
        none.Should().Contain("discuss").And.Contain("implementMode");
        none.Should().NotContain("- panel —").And.NotContain("- qa —");
        none.Should().Contain("<team-mechanic id=");

        // Протокол повторного предложения: можно при изменении темы, нельзя после отказа
        none.Should().Contain("Повторное предложение той же механики");
        none.Should().Contain("не предлагай вновь");

        // Установленный скилл добавляет свою механику
        var withPanel = TeamMechanicsPromptCatalog.BuildPromptBlock(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "panel-of-experts" });
        withPanel.Should().Contain("- panel —");
    }
}
