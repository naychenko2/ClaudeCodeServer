using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Применение шаблона специальности к полям персоны: создание и смена специальности,
// приоритет явных полей, слой настроек поверх дефолтов кода.
// Шаблон теперь применяется безусловно (фич-флаг model-routing-rules снят).
public class SpecialtyTemplatesServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly UserStore _users;
    private readonly string _ownerId;
    private readonly SpecialtySettingsStore _settings;
    private readonly SpecialtyTemplatesService _sut;

    public SpecialtyTemplatesServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ccs_tmpl_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            }).Build();
        _users = new UserStore(config,
            new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(),
            NullLogger<UserStore>.Instance);
        _ownerId = _users.GetFirst()!.Id;
        _settings = new SpecialtySettingsStore(config, NullLogger<SpecialtySettingsStore>.Instance);
        _sut = new SpecialtyTemplatesService(_settings);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Apply_СозданиеСоСпециальностью_ПодставляетДефолтныйШаблон()
    {
        var result = _sut.Apply(_ownerId, PersonaSpecialty.BackendExecutor, currentSpecialty: null,
            explicitAccess: null, explicitTools: null, explicitDisallowedTools: null);

        result.TemplateApplied.Should().BeTrue();
        result.Access.Should().Be(PersonaAccess.Full, "дефолт кода для исполнителя");
        // Tools=null в шаблоне разворачивается в полный список, чтобы Update semantics
        // PersonaManager («null — не менять») не проглотила подстановку
        result.Tools.Should().BeEquivalentTo(SpecialtyTemplatesService.AllToolKeys);
        result.DisallowedTools.Should().BeNull();
    }

    [Fact]
    public void Apply_ЯвныеПоляПобеждаютШаблон()
    {
        var result = _sut.Apply(_ownerId, PersonaSpecialty.BackendExecutor, currentSpecialty: null,
            explicitAccess: PersonaAccess.ReadOnly,
            explicitTools: ["web"],
            explicitDisallowedTools: null);

        result.TemplateApplied.Should().BeTrue();
        result.Access.Should().Be(PersonaAccess.ReadOnly, "явный access сильнее шаблона");
        result.Tools.Should().BeEquivalentTo(["web"], "явные tools сильнее шаблона");
    }

    [Fact]
    public void Apply_ТаЖеСпециальностьВUpdate_ШаблонНеПрименяется()
    {
        var result = _sut.Apply(_ownerId, PersonaSpecialty.Executor,
            currentSpecialty: PersonaSpecialty.Executor,
            explicitAccess: null, explicitTools: null, explicitDisallowedTools: null);

        result.TemplateApplied.Should().BeFalse("специальность не сменилась");
        result.Access.Should().BeNull();
        result.Tools.Should().BeNull();
    }

    [Fact]
    public void Apply_СменаСпециальности_Подставляет()
    {
        var result = _sut.Apply(_ownerId, PersonaSpecialty.FrontendExecutor,
            currentSpecialty: PersonaSpecialty.Executor,
            explicitAccess: null, explicitTools: null, explicitDisallowedTools: null);

        result.TemplateApplied.Should().BeTrue();
        result.Access.Should().Be(PersonaAccess.Full);
    }

    [Fact]
    public void Apply_СбросВNone_ШаблонНеПрименяется()
    {
        var result = _sut.Apply(_ownerId, PersonaSpecialty.None,
            currentSpecialty: PersonaSpecialty.Executor,
            explicitAccess: null, explicitTools: null, explicitDisallowedTools: null);

        result.TemplateApplied.Should().BeFalse();
    }

    [Fact]
    public void Apply_СпециальностьБезШаблона_ПоляНеТрогаются()
    {
        var result = _sut.Apply(_ownerId, PersonaSpecialty.Analyst, currentSpecialty: null,
            explicitAccess: null, explicitTools: null, explicitDisallowedTools: null);

        result.TemplateApplied.Should().BeFalse("у аналитика нет дефолтного шаблона");
        result.Access.Should().BeNull();
    }

    [Fact]
    public void Apply_НастройкаВСторе_СильнееДефолтаКода()
    {
        _settings.SetOwner(_ownerId, new SpecialtySettingsLayer
        {
            Specialties = new Dictionary<string, SpecialtyTemplateSettings>
            {
                ["backendExecutor"] = new()
                {
                    Access = PersonaAccess.Custom,
                    Tools = ["web"],
                    DisallowedTools = ["Bash"],
                },
            },
        }).Should().BeNull();

        var result = _sut.Apply(_ownerId, PersonaSpecialty.BackendExecutor, currentSpecialty: null,
            explicitAccess: null, explicitTools: null, explicitDisallowedTools: null);

        result.TemplateApplied.Should().BeTrue();
        result.Access.Should().Be(PersonaAccess.Custom);
        result.Tools.Should().BeEquivalentTo("web");
        result.DisallowedTools.Should().BeEquivalentTo("Bash");
    }

    [Fact]
    public void Apply_НастройкаДляСпециальностиБезДефолта_ДаётШаблон()
    {
        _settings.SetGlobal(new SpecialtySettingsLayer
        {
            Specialties = new Dictionary<string, SpecialtyTemplateSettings>
            {
                ["analyst"] = new() { Access = PersonaAccess.ReadOnly },
            },
        });

        var result = _sut.Apply(_ownerId, PersonaSpecialty.Analyst, currentSpecialty: null,
            explicitAccess: null, explicitTools: null, explicitDisallowedTools: null);

        result.TemplateApplied.Should().BeTrue("стор задал шаблон даже для специальности без дефолта");
        result.Access.Should().Be(PersonaAccess.ReadOnly);
    }
}
