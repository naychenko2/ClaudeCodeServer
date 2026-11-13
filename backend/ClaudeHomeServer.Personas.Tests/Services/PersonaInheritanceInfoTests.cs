using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// B09: данные формы персоны и панели специальности — дата копирования прав и число отличий,
/// наследники специальности, версия набора секций и «изменено админом с тех пор».
/// </summary>
public class PersonaInheritanceInfoTests : IDisposable
{
    private const string Owner = "owner-1";
    private readonly string _tempDir;
    private readonly SpecialtySettingsStore _specialty;
    private readonly PersonaManager _sut;

    public PersonaInheritanceInfoTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "pmgr_inhinfo_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var config = TestConfig.Build(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            ["PersonasPath"] = Path.Combine(_tempDir, "personas.json"),
            ["Ollama:Model"] = "",
        });
        _specialty = TestSpecialtyStore.Create(config);
        _sut = new PersonaManager(config, NullLogger<PersonaManager>.Instance, specialty: _specialty);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private static readonly string Key = SpecialtyCatalog.KeyOf(PersonaSpecialty.Reviewer);

    private void SetTemplate(PersonaAccess access, List<string>? tools, List<string>? disallowed,
        string? sectionText = null)
    {
        var record = new SpecialtyTemplateSettings { Access = access, Tools = tools, DisallowedTools = disallowed };
        if (sectionText is not null)
            record.PromptSections =
                [new SpecialtyPromptSectionSettings { Id = SpecialtyPromptPresets.Sections[0].Id, Enabled = true, Text = sectionText }];
        var layer = new SpecialtySettingsLayer();
        layer.Specialties[Key] = record;
        _specialty.SetGlobal(layer).Should().BeNull();
    }

    private Persona Create(string name = "Тест", PersonaSpecialty specialty = PersonaSpecialty.Reviewer,
        bool? inherits = null) =>
        _sut.Create(Owner, name, null, null, null, null, null, PersonaScope.Global, null, null, null, true,
            specialty: specialty, inheritsAccess: inherits);

    private void Update(string id, bool? inheritsAccess = null, PersonaAccess? access = null,
        List<string>? tools = null, List<string>? disallowed = null) =>
        _sut.Update(id, Owner, null, null, null, null, null, null, null, null, null, null, null,
            tools, null, access, disallowed, inheritsAccess: inheritsAccess);

    // ---- AccessCopiedAt / AccessDiffCount ----

    [Fact]
    public void ВыключениеНаследования_ФиксируетДатуКопирования_ПовторноеВключение_Сбрасывает()
    {
        SetTemplate(PersonaAccess.ReadOnly, ["tasks"], null);
        var p = Create(inherits: true);
        _sut.Get(p.Id, Owner)!.AccessCopiedAt.Should().BeNull("пока наследует — копии нет");

        var before = DateTime.UtcNow;
        Update(p.Id, inheritsAccess: false, access: PersonaAccess.ReadOnly, tools: ["tasks"]);
        _sut.Get(p.Id, Owner)!.AccessCopiedAt.Should().NotBeNull().And.BeOnOrAfter(before);

        Update(p.Id, inheritsAccess: true);
        _sut.Get(p.Id, Owner)!.AccessCopiedAt.Should().BeNull("повторное включение наследования стирает дату");
    }

    [Fact]
    public void ЧастичнаяПравка_ДатуКопированияНеТрогает()
    {
        SetTemplate(PersonaAccess.ReadOnly, ["tasks"], null);
        var p = Create(inherits: true);
        Update(p.Id, inheritsAccess: false, access: PersonaAccess.ReadOnly, tools: ["tasks"]);
        var stamp = _sut.Get(p.Id, Owner)!.AccessCopiedAt;

        Update(p.Id); // флаг не передан

        _sut.Get(p.Id, Owner)!.AccessCopiedAt.Should().Be(stamp);
    }

    [Fact]
    public void AccessDiffCount_СчитаетОтличияКопииОтТекущегоШаблона()
    {
        SetTemplate(PersonaAccess.ReadOnly, ["tasks"], null);
        var p = Create(inherits: true);
        Update(p.Id, inheritsAccess: false, access: PersonaAccess.ReadOnly, tools: ["tasks"]);
        _sut.Get(p.Id, Owner)!.AccessDiffCount.Should().Be(0, "копия совпадает с шаблоном");

        SetTemplate(PersonaAccess.Custom, ["notes"], ["Bash"]); // профиль, инструменты и запреты разошлись

        _sut.Get(p.Id, Owner)!.AccessDiffCount.Should().Be(3);
        _sut.GetRaw(p.Id, Owner)!.AccessDiffCount.Should().BeNull("поле ответа в запись кэша не пишется");
    }

    [Fact]
    public void AccessDiffCount_БезКопии_Null()
    {
        SetTemplate(PersonaAccess.ReadOnly, ["tasks"], null);
        _sut.Get(Create(inherits: true).Id, Owner)!.AccessDiffCount.Should().BeNull("наследует — отличий нет по построению");
        _sut.Get(Create("Своя", inherits: false).Id, Owner)!.AccessDiffCount.Should().BeNull("копирования не было");
    }

    // ---- Наследники специальности ----

    [Fact]
    public void SpecialtyInheritors_ПерсоныСтойСпециальностью_СЧислом_СвоихСекций()
    {
        var a = Create("Аня");
        var b = Create("Борис");
        Create("Чужая", PersonaSpecialty.Planner);
        _sut.Update(b.Id, Owner, null, null, null, null, null, null, null, null, null, null, null,
            null, null, null, null,
            promptSections: [new PromptSectionOverride(SpecialtyPromptPresets.Sections[0].Id, false, null)]);

        var inheritors = _sut.SpecialtyInheritors(Owner, PersonaSpecialty.Reviewer);

        inheritors.Should().Equal([(a.Id, "Аня", 0), (b.Id, "Борис", 1)]);
        _sut.SpecialtyInheritors("другой-владелец", PersonaSpecialty.Reviewer).Should().BeEmpty();
        _sut.SpecialtyInheritors(Owner, PersonaSpecialty.None).Should().BeEmpty();
    }

    // ---- Версия секций ----

    [Fact]
    public void ChangedByAdminSince_БезОтметки_Null_ПослеОтметки_False_ПослеПравкиАдмина_True()
    {
        var p = Create();
        _sut.Get(p.Id, Owner)!.ChangedByAdminSince.Should().BeNull("базы нет — признак не считается");

        var seen = _sut.MarkSectionsSeen(p.Id, Owner);
        seen.SectionsSeenVersion.Should().NotBeNullOrEmpty();
        seen.ChangedByAdminSince.Should().BeFalse();

        SetTemplate(PersonaAccess.Full, null, null, sectionText: "Новый текст от админа");

        var after = _sut.Get(p.Id, Owner)!;
        after.ChangedByAdminSince.Should().BeTrue();
        after.SectionsVersion.Should().NotBe(after.SectionsSeenVersion);

        _sut.MarkSectionsSeen(p.Id, Owner).ChangedByAdminSince.Should().BeFalse("отметка двигает базу на текущий набор");
    }

    [Fact]
    public void СменаСпециальности_СбрасываетБазуСекций_ЛожногоОбновленияНет()
    {
        var p = Create();
        _sut.MarkSectionsSeen(p.Id, Owner).SectionsSeenVersion.Should().NotBeNullOrEmpty();

        _sut.Update(p.Id, Owner, null, null, null, null, null, null, null, null, null, null, null,
            null, null, null, null, specialty: PersonaSpecialty.Planner);

        var after = _sut.Get(p.Id, Owner)!;
        after.SectionsSeenVersion.Should().BeNull("база привязана к старой специальности");
        after.ChangedByAdminSince.Should().NotBe(true);
    }

    [Fact]
    public void AccessDiffCount_НеCustomШаблонСЗапретами_ЛишнейЕдиницыНет()
    {
        SetTemplate(PersonaAccess.ReadOnly, ["tasks"], ["Bash"]);
        var p = Create(inherits: true);
        Update(p.Id, inheritsAccess: false, access: PersonaAccess.ReadOnly, tools: ["tasks"]);

        _sut.Get(p.Id, Owner)!.AccessDiffCount.Should().Be(0, "запреты не-Custom шаблона не считаются");
    }

    [Fact]
    public void ПравкаСвоегоСлояПерсоны_ВерсиюСекцийСпециальностиНеДвигает()
    {
        var p = Create();
        _sut.MarkSectionsSeen(p.Id, Owner);

        _sut.Update(p.Id, Owner, null, null, null, null, null, null, null, null, null, null, null,
            null, null, null, null,
            promptSections: [new PromptSectionOverride(SpecialtyPromptPresets.Sections[0].Id, null, "Своё")]);

        _sut.Get(p.Id, Owner)!.ChangedByAdminSince.Should().BeFalse();
    }

    [Fact]
    public void SpecialtyNone_ОтметкаНеПишется()
    {
        var p = Create(specialty: PersonaSpecialty.None);
        _sut.MarkSectionsSeen(p.Id, Owner).SectionsSeenVersion.Should().BeNull("секций у специальности нет");
    }

    [Fact]
    public void SectionsVersion_ЗависитОтТекстаВключённостиИПорядка()
    {
        EffectivePromptSection S(string id, bool on, string t) =>
            new(id, on, t, ClaudeHomeServer.Services.Llm.SectionSource.Code, ClaudeHomeServer.Services.Llm.SectionSource.Code);
        var baseline = PersonaManager.SectionsVersion([S("a", true, "x"), S("b", true, "y")]);

        PersonaManager.SectionsVersion([S("a", true, "x"), S("b", true, "y")]).Should().Be(baseline);
        PersonaManager.SectionsVersion([S("a", true, "x"), S("b", true, "z")]).Should().NotBe(baseline);
        PersonaManager.SectionsVersion([S("a", true, "x"), S("b", false, "y")]).Should().NotBe(baseline);
        PersonaManager.SectionsVersion([S("b", true, "y"), S("a", true, "x")]).Should().NotBe(baseline);
    }

    [Fact]
    public void AccessDiffCount_ДваОтличия_Два()
    {
        SetTemplate(PersonaAccess.ReadOnly, ["tasks"], null);
        var p = Create(inherits: true);
        Update(p.Id, inheritsAccess: false, access: PersonaAccess.ReadOnly, tools: ["tasks"]);

        SetTemplate(PersonaAccess.Custom, ["tasks"], ["Bash"]); // инструменты те же; профиль и запреты разошлись

        var got = _sut.Get(p.Id, Owner)!;
        got.AccessDiffCount.Should().Be(2);
        got.AccessCopiedAt.Should().NotBeNull();
    }

    [Fact]
    public void UpdatedFromRole_ТолькоПриИзменении_ИТолькоДляСекцийОтСпециальности()
    {
        var inheriting = Create("Наследует");
        var own = Create("Своё");
        _sut.MarkSectionsSeen(inheriting.Id, Owner);
        var sectionId = SpecialtyPromptPresets.Sections[0].Id;
        _sut.Update(own.Id, Owner, null, null, null, null, null, null, null, null, null, null, null,
            null, null, null, null, promptSections: [new PromptSectionOverride(sectionId, true, "Своё")]);
        _sut.MarkSectionsSeen(own.Id, Owner);
        SetTemplate(PersonaAccess.Full, null, null, sectionText: "Новый текст от админа");

        EffectivePromptSection Section(Persona p) => PromptSectionOverlay
            .Apply(_specialty.EffectivePromptSectionStates(Owner, PersonaSpecialty.Reviewer),
                _sut.Get(p.Id, Owner)!.PromptSections, ClaudeHomeServer.Services.Llm.SectionSource.Persona)
            .Single(x => x.Id == sectionId);

        var changed = _sut.Get(inheriting.Id, Owner)!.ChangedByAdminSince == true;
        changed.Should().BeTrue();
        PersonaManager.UpdatedFromRole(changed, Section(inheriting)).Should().BeTrue("секция идёт от специальности");
        PersonaManager.UpdatedFromRole(true, Section(own)).Should().BeFalse("у персоны своё значение секции");
        PersonaManager.UpdatedFromRole(false, Section(inheriting)).Should().BeFalse("набор не менялся");
    }

    [Fact]
    public void СтарыйPersonasJson_БезНовыхПолей_ЧитаетсяБезОшибок()
    {
        var dir = Path.Combine(_tempDir, "old");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "personas.json");
        File.WriteAllText(path, """
            [{"id":"old1","ownerId":"owner-1","name":"Старая","handle":"staraya","specialty":"Reviewer","inheritsAccess":false}]
            """);
        var config = TestConfig.Build(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(dir, "projects.json"),
            ["PersonasPath"] = path,
            ["Ollama:Model"] = "",
        });

        var sut = new PersonaManager(config, NullLogger<PersonaManager>.Instance, specialty: _specialty);

        var p = sut.Get("old1", Owner);
        p.Should().NotBeNull();
        p!.AccessCopiedAt.Should().BeNull();
        p.AccessDiffCount.Should().BeNull();
        p.SectionsSeenVersion.Should().BeNull();
        p.ChangedByAdminSince.Should().BeNull();
    }
}
