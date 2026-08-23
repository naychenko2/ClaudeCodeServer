using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Каталог секций промптов и типовых профилей умений (SpecialtyPromptPresets): состав,
// лимиты текстов, дефолты включённости и профили по согласованным таблицам плана v5.
public class SpecialtyPromptPresetsTests
{
    private static readonly PersonaSpecialty[] AllSpecialties = Enum.GetValues<PersonaSpecialty>()
        .Where(s => s != PersonaSpecialty.None).ToArray();

    [Fact]
    public void Sections_СоставКаталогаV5()
    {
        SpecialtyPromptPresets.Sections.Select(s => s.Id)
            .Should().Equal("history", "codeGraph", "processes", "roleRules");
        foreach (var section in SpecialtyPromptPresets.Sections)
        {
            section.Label.Should().NotBeNullOrWhiteSpace($"у секции {section.Id} есть подпись");
            section.Description.Should().NotBeNullOrWhiteSpace($"у секции {section.Id} есть описание");
        }
    }

    [Fact]
    public void DefaultText_ВсеТекстыНепустыИВЛимите()
    {
        foreach (var specialty in AllSpecialties)
        foreach (var section in SpecialtyPromptPresets.Sections)
        {
            var text = SpecialtyPromptPresets.DefaultText(section.Id, specialty);
            text.Should().NotBeNullOrWhiteSpace($"типовой текст {section.Id}×{specialty} нужен всем — секцию можно включить вручную");
            text.Length.Should().BeLessOrEqualTo(SpecialtyPromptPresets.SectionTextLimit,
                $"текст {section.Id}×{specialty} обязан влезать в собственный лимит");
        }
    }

    [Fact]
    public void DefaultText_None_Пусто()
    {
        foreach (var section in SpecialtyPromptPresets.Sections)
            SpecialtyPromptPresets.DefaultText(section.Id, PersonaSpecialty.None).Should().BeEmpty();
        SpecialtyPromptPresets.DefaultBindingsProfile(PersonaSpecialty.None).Should().BeEmpty();
    }

    [Fact]
    public void DefaultText_ИсторияСсылаетсяНаРазделениеТруда_DossierИCodegraph()
    {
        // Контракт плана: секция «история» несёт «структуру — codegraph_*, историю — dossier_*»
        foreach (var specialty in AllSpecialties)
        {
            var text = SpecialtyPromptPresets.DefaultText("history", specialty);
            text.Should().Contain("dossier_lookup", $"{specialty} зовёт lookup по досье");
            text.Should().Contain("dossier_get", $"{specialty} умеет читать паспорт по id");
            text.Should().Contain("codegraph_", $"{specialty} разводит структуру кода в граф");
        }
    }

    [Fact]
    public void DefaultEnabled_ТаблицаВключённости()
    {
        var historyOn = AllSpecialties.Where(s => SpecialtyPromptPresets.DefaultEnabled("history", s)).ToList();
        historyOn.Should().BeEquivalentTo(new[]
        {
            PersonaSpecialty.Analyst, PersonaSpecialty.Planner, PersonaSpecialty.Reviewer,
            PersonaSpecialty.Tester, PersonaSpecialty.Executor, PersonaSpecialty.BackendExecutor,
            PersonaSpecialty.FrontendExecutor, PersonaSpecialty.DevopsExecutor,
            PersonaSpecialty.Consultant,
        }, "история по умолчанию у 9 ролей");

        var graphOn = AllSpecialties.Where(s => SpecialtyPromptPresets.DefaultEnabled("codeGraph", s)).ToList();
        graphOn.Should().BeEquivalentTo(new[]
        {
            PersonaSpecialty.Planner, PersonaSpecialty.Reviewer, PersonaSpecialty.Tester,
            PersonaSpecialty.BackendExecutor, PersonaSpecialty.FrontendExecutor,
            PersonaSpecialty.DevopsExecutor,
        }, "граф кода по умолчанию у 6 ролей");

        var processesOn = AllSpecialties.Where(s => SpecialtyPromptPresets.DefaultEnabled("processes", s)).ToList();
        processesOn.Should().BeEquivalentTo(new[]
        {
            PersonaSpecialty.Executor, PersonaSpecialty.BackendExecutor,
            PersonaSpecialty.FrontendExecutor, PersonaSpecialty.DevopsExecutor,
            PersonaSpecialty.Tester, PersonaSpecialty.Planner, PersonaSpecialty.Reviewer,
            PersonaSpecialty.Secretary, PersonaSpecialty.Librarian,
        }, "процессы (DoD) по умолчанию у 9 ролей");

        AllSpecialties.Should().OnlyContain(
            s => SpecialtyPromptPresets.DefaultEnabled("roleRules", s),
            "правила роли включены у всех 14");

        SpecialtyPromptPresets.DefaultEnabled("history", PersonaSpecialty.None).Should().BeFalse(
            "у «Не задана» секций нет вовсе");
    }

    [Theory]
    [InlineData("no-such")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("  ")]
    public void DefaultEnabled_НеизвестнаяСекция_Ложь(string? id) =>
        SpecialtyPromptPresets.DefaultEnabled(id!, PersonaSpecialty.Analyst).Should().BeFalse();

    [Fact]
    public void DefaultBindingsProfile_ТаблицаПрофилей()
    {
        // Таблица плана (согласована с пользователем 2026-08-23)
        Expect(PersonaSpecialty.Librarian, PersonaBindingType.Knowledge, PersonaBindingType.Notes);
        Expect(PersonaSpecialty.Consultant, PersonaBindingType.Knowledge, PersonaBindingType.Notes);
        Expect(PersonaSpecialty.Secretary, PersonaBindingType.Notes, PersonaBindingType.ProjectTasks);
        Expect(PersonaSpecialty.Coordinator, PersonaBindingType.ProjectTasks, PersonaBindingType.ProjectPersonas);
        Expect(PersonaSpecialty.Planner, PersonaBindingType.ProjectTasks, PersonaBindingType.Project);
        Expect(PersonaSpecialty.Executor, PersonaBindingType.Project, PersonaBindingType.ProjectPath);
        Expect(PersonaSpecialty.BackendExecutor, PersonaBindingType.Project, PersonaBindingType.ProjectPath);
        Expect(PersonaSpecialty.FrontendExecutor, PersonaBindingType.Project, PersonaBindingType.ProjectPath);
        Expect(PersonaSpecialty.DevopsExecutor, PersonaBindingType.Project, PersonaBindingType.ProjectPath);
        Expect(PersonaSpecialty.Reviewer, PersonaBindingType.Project, PersonaBindingType.ProjectPath);
        Expect(PersonaSpecialty.Tester, PersonaBindingType.Project, PersonaBindingType.ProjectPath);
        Expect(PersonaSpecialty.Mentor, PersonaBindingType.Project, PersonaBindingType.Notes);
        Expect(PersonaSpecialty.Designer, PersonaBindingType.Project, PersonaBindingType.Notes);
        Expect(PersonaSpecialty.Analyst, PersonaBindingType.Project, PersonaBindingType.Notes);
        return;

        void Expect(PersonaSpecialty specialty, params PersonaBindingType[] types)
        {
            var profile = SpecialtyPromptPresets.DefaultBindingsProfile(specialty);
            profile.Select(b => b.Type).Should().Equal(types, $"профиль {specialty} — по таблице плана");
            profile.Should().OnlyContain(b => !string.IsNullOrWhiteSpace(b.Condition),
                $"у каждого типового умения {specialty} есть условие");
            profile.Should().OnlyContain(b => b.SkillName == null,
                "в кодовых дефолтах скиллов нет — каталог скиллов у каждого владельца свой");
        }
    }

    [Fact]
    public void TryGetSection_ИзвестныеИНезнакомыеКлючи()
    {
        SpecialtyPromptPresets.TryGetSection("history", out var meta).Should().BeTrue();
        meta.Id.Should().Be("history");
        SpecialtyPromptPresets.TryGetSection("  CodeGraph ", out var ci).Should().BeTrue(
            "id сравнивается без учёта регистра и триммится");
        ci.Id.Should().Be("codeGraph");
        SpecialtyPromptPresets.TryGetSection("no-such", out _).Should().BeFalse();
        SpecialtyPromptPresets.TryGetSection(null, out _).Should().BeFalse();
    }
}
