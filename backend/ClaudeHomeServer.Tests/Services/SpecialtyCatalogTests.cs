using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Каталог специальностей: wire-ключи, подписи (в т.ч. четырёх исполнительских),
// семейство исполнителя и дефолтные шаблоны прав.
public class SpecialtyCatalogTests
{
    [Fact]
    public void Keys_WireЗначения_CamelCase()
    {
        SpecialtyCatalog.KeyOf(PersonaSpecialty.Executor).Should().Be("executor");
        SpecialtyCatalog.KeyOf(PersonaSpecialty.BackendExecutor).Should().Be("backendExecutor");
        SpecialtyCatalog.KeyOf(PersonaSpecialty.FrontendExecutor).Should().Be("frontendExecutor");
        SpecialtyCatalog.KeyOf(PersonaSpecialty.DevopsExecutor).Should().Be("devopsExecutor");
        SpecialtyCatalog.KeyOf(PersonaSpecialty.None).Should().Be("none");
    }

    [Fact]
    public void Labels_ЧетыреИсполнительскиеПодписи_Утверждённые()
    {
        SpecialtyCatalog.Label(PersonaSpecialty.Executor).Should().Be("Исполнитель (универсальный)");
        SpecialtyCatalog.Label(PersonaSpecialty.BackendExecutor).Should().Be("Исполнитель (бэкенд)");
        SpecialtyCatalog.Label(PersonaSpecialty.FrontendExecutor).Should().Be("Исполнитель (фронтенд)");
        SpecialtyCatalog.Label(PersonaSpecialty.DevopsExecutor).Should().Be("Исполнитель (DevOps)");
    }

    [Fact]
    public void Labels_ОстальныеСпециальности_Стабильны()
    {
        SpecialtyCatalog.Label(PersonaSpecialty.Analyst).Should().Be("Аналитик");
        SpecialtyCatalog.Label(PersonaSpecialty.Tester).Should().Be("Тестировщик");
        SpecialtyCatalog.Label(PersonaSpecialty.None).Should().Be("Не задана");
    }

    [Fact]
    public void Catalog_ПокрываетВсеЗначенияEnum()
    {
        SpecialtyCatalog.All.Select(e => e.Specialty)
            .Should().BeEquivalentTo(Enum.GetValues<PersonaSpecialty>());
    }

    [Fact]
    public void ExecutorFamily_ЧетыреИсполнителя()
    {
        SpecialtyCatalog.IsExecutorKind(PersonaSpecialty.Executor).Should().BeTrue();
        SpecialtyCatalog.IsExecutorKind(PersonaSpecialty.BackendExecutor).Should().BeTrue();
        SpecialtyCatalog.IsExecutorKind(PersonaSpecialty.FrontendExecutor).Should().BeTrue();
        SpecialtyCatalog.IsExecutorKind(PersonaSpecialty.DevopsExecutor).Should().BeTrue();

        SpecialtyCatalog.IsExecutorKind(PersonaSpecialty.Tester).Should().BeFalse();
        SpecialtyCatalog.IsExecutorKind(PersonaSpecialty.Reviewer).Should().BeFalse();
        SpecialtyCatalog.IsExecutorKind(PersonaSpecialty.None).Should().BeFalse();
    }

    [Fact]
    public void DefaultTemplate_УИсполнительскихПолныйДоступИВсеИнструменты()
    {
        foreach (var specialty in new[]
                 {
                     PersonaSpecialty.Executor,
                     PersonaSpecialty.BackendExecutor,
                     PersonaSpecialty.FrontendExecutor,
                     PersonaSpecialty.DevopsExecutor,
                 })
        {
            var template = SpecialtyCatalog.Get(specialty).DefaultTemplate;
            template.Should().NotBeNull($"у специальности {specialty} есть шаблон");
            template!.Access.Should().Be(PersonaAccess.Full);
            template.Tools.Should().BeNull("null — все возможности");
            template.DisallowedTools.Should().BeNull();
        }
    }

    [Fact]
    public void DefaultTemplate_НеИсполнительские_БезШаблона()
    {
        SpecialtyCatalog.Get(PersonaSpecialty.Analyst).DefaultTemplate.Should().BeNull();
        SpecialtyCatalog.Get(PersonaSpecialty.None).DefaultTemplate.Should().BeNull();
    }

    [Fact]
    public void TryGetByKey_ИзвестныеКлючи_ВЛюбомРегистре()
    {
        SpecialtyCatalog.TryGetByKey("backendExecutor", out var entry).Should().BeTrue();
        entry.Specialty.Should().Be(PersonaSpecialty.BackendExecutor);

        SpecialtyCatalog.TryGetByKey("devopsExecutor", out var devops).Should().BeTrue();
        devops.Specialty.Should().Be(PersonaSpecialty.DevopsExecutor);

        SpecialtyCatalog.TryGetByKey("backend-executor", out _).Should().BeFalse();
        SpecialtyCatalog.TryGetByKey("BackendExecutor", out var ci).Should().BeTrue();
        ci.Specialty.Should().Be(PersonaSpecialty.BackendExecutor);

        SpecialtyCatalog.TryGetByKey("  executor  ", out var trimmed).Should().BeTrue();
        trimmed.Specialty.Should().Be(PersonaSpecialty.Executor);
    }

    [Fact]
    public void TryGetByKey_Неизвестные_Ложь()
    {
        SpecialtyCatalog.TryGetByKey("no-such", out _).Should().BeFalse();
        SpecialtyCatalog.TryGetByKey("", out _).Should().BeFalse();
        SpecialtyCatalog.TryGetByKey(null, out _).Should().BeFalse();
    }
}
