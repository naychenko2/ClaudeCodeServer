using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// A1a: права персоны с InheritsAccess=true подставляются эффективным шаблоном специальности
// на каждой выдаче (Get/GetByOwner/GetAllInternal); запись кэша остаётся материализованной
public class PersonaManagerHydrateTests : IDisposable
{
    private const string Owner = "owner-1";
    private readonly string _tempDir;
    private readonly SpecialtySettingsStore _specialty;
    private readonly PersonaManager _sut;

    public PersonaManagerHydrateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "pmgr_hydrate_" + Guid.NewGuid().ToString("N"));
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

    private void SetTemplate(PersonaAccess access, List<string>? tools, List<string>? disallowed)
    {
        var layer = new SpecialtySettingsLayer();
        layer.Specialties[SpecialtyCatalog.KeyOf(PersonaSpecialty.Reviewer)] = new SpecialtyTemplateSettings
        {
            Access = access, Tools = tools, DisallowedTools = disallowed,
        };
        _specialty.SetGlobal(layer);
    }

    // Персона с материализованным «Full» и флагом наследования (флаг ставим в кэше напрямую:
    // публичного пути записи у шага A1a нет)
    private Persona MakeInheriting(PersonaSpecialty specialty = PersonaSpecialty.Reviewer, bool inherits = true)
    {
        var created = _sut.Create(Owner, "Тест", null, null, null, null, null,
            PersonaScope.Global, null, null, null, true, specialty: specialty);
        _sut.GetRaw(created.Id, Owner)!.InheritsAccess = inherits;
        return created;
    }

    private Persona Cache(string id) => _sut.GetRaw(id, Owner)!;

    [Fact]
    public void Get_InheritsAccess_ВозвращаетПраваШаблона_КэшНеТронут()
    {
        SetTemplate(PersonaAccess.ReadOnly, ["tasks"], null);
        var p = MakeInheriting();

        var got = _sut.Get(p.Id, Owner)!;

        got.Access.Should().Be(PersonaAccess.ReadOnly);
        got.Tools.Should().Equal("tasks");
        got.Should().NotBeSameAs(Cache(p.Id), "кэш отдаётся копией");
        _sut.GetAllInternal().Single(x => x.Id == p.Id).Access.Should().Be(PersonaAccess.ReadOnly);
    }

    [Fact]
    public void Get_Override_ПраваПерсоныПереживаютШаблон()
    {
        SetTemplate(PersonaAccess.ReadOnly, ["tasks"], null);
        var p = MakeInheriting(inherits: false);

        var got = _sut.Get(p.Id, Owner)!;

        got.Access.Should().Be(PersonaAccess.Full);
        got.Tools.Should().BeNull();
    }

    [Fact]
    public void GetByOwner_ПравкаСпециальности_ДоходитБезПравкиПерсоны()
    {
        var p = MakeInheriting();
        SetTemplate(PersonaAccess.ReadOnly, ["tasks"], null);
        _sut.GetByOwner(Owner).Single().Access.Should().Be(PersonaAccess.ReadOnly);

        SetTemplate(PersonaAccess.Custom, ["notes"], ["Bash"]);
        var again = _sut.GetByOwner(Owner).Single();

        again.Access.Should().Be(PersonaAccess.Custom);
        again.Tools.Should().Equal("notes");
        again.DisallowedTools.Should().Equal("Bash");
        Cache(p.Id).Access.Should().Be(PersonaAccess.Full, "персистентное поле не перезаписывается");
    }

    [Fact]
    public void Get_СпециальностьNone_ДефолтКода_Full()
    {
        var p = MakeInheriting(PersonaSpecialty.None);
        Cache(p.Id).Access = PersonaAccess.ReadOnly; // материализованное значение устарело

        var got = _sut.Get(p.Id, Owner)!;

        got.Access.Should().Be(PersonaAccess.Full);
        got.Tools.Should().BeNull();
        got.DisallowedTools.Should().BeNull();
    }

    [Fact]
    public void Get_ШаблонСДоступомКоВсемУтилитам_ToolsНормализуетсяВNull()
    {
        SetTemplate(PersonaAccess.Full, ["tasks", "notes", "web"], null);
        var p = MakeInheriting();

        _sut.Get(p.Id, Owner)!.Tools.Should().BeNull();
    }

    [Fact]
    public void Get_ЧужойВладелец_Null()
    {
        var p = MakeInheriting();
        _sut.Get(p.Id, "stranger").Should().BeNull();
    }

    [Fact]
    public void Update_ПослеГидратации_ПравкаПопадаетВКэш()
    {
        SetTemplate(PersonaAccess.ReadOnly, ["tasks"], null);
        var p = MakeInheriting();

        _sut.Update(p.Id, Owner, "Новое имя", null, null, null, null, null, null, null, null, null, null);

        Cache(p.Id).Name.Should().Be("Новое имя");
    }

    [Fact]
    public async Task Hydrate_ПараллельноСоСменойСпециальности_ГруппаЦелая()
    {
        SetTemplate(PersonaAccess.ReadOnly, ["tasks"], null);
        var p = MakeInheriting();
        var broken = 0;
        using var stop = new CancellationTokenSource();

        var writer = Task.Run(() =>
        {
            for (var i = 0; !stop.IsCancellationRequested; i++)
            {
                if (i % 2 == 0) SetTemplate(PersonaAccess.Custom, ["notes"], ["Bash"]);
                else SetTemplate(PersonaAccess.ReadOnly, ["tasks"], null);
            }
        });

        Parallel.For(0, 100, _ =>
        {
            var got = _sut.Get(p.Id, Owner)!;
            var a = got.Access == PersonaAccess.ReadOnly && got.Tools is ["tasks"] && got.DisallowedTools is null;
            var b = got.Access == PersonaAccess.Custom && got.Tools is ["notes"] && got.DisallowedTools is ["Bash"];
            if (!a && !b) Interlocked.Increment(ref broken);
        });
        stop.Cancel();
        await writer;

        broken.Should().Be(0);
    }
}
