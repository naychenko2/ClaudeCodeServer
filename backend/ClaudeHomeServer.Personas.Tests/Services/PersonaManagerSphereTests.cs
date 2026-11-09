using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Spheres;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Видимость персон сферы: Global ∪ Project(проект) ∪ Sphere(сфера проекта), приоритет handle
// Project > Sphere > Global, зона уникальности handle персоны сферы.
public class PersonaManagerSphereTests : IDisposable
{
    private const string OwnerId = "owner-1";
    private readonly string _tempDir;
    private readonly FakeDir _dir = new();
    private readonly PersonaManager _sut;

    // Справочник с изменяемым составом: тест выводит проект из сферы между вызовами
    private sealed class FakeDir : ISphereDirectory
    {
        public Dictionary<string, List<string>> Spheres = new();
        public bool Enabled(string ownerId) => true;
        public string? SphereOf(string ownerId, string projectId) =>
            Spheres.FirstOrDefault(s => s.Value.Contains(projectId)).Key;
        public IReadOnlyList<string> ProjectsOf(string ownerId, string sphereId) =>
            Spheres.TryGetValue(sphereId, out var l) ? l.ToList() : [];
        public string? SphereName(string ownerId, string sphereId) => null;
        public string? CharterOf(string ownerId, string sphereId) => null;
    }

    public PersonaManagerSphereTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "pmgr_sphere_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PersonasPath"] = Path.Combine(_tempDir, "personas.json"),
            })
            .Build();
        _sut = new PersonaManager(config, spheres: _dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private Persona MakeProject(string projectId, string name) =>
        _sut.Create(OwnerId, name, null, null, null, null, null, PersonaScope.Project, projectId, null, null, true);

    private Persona MakeGlobal(string name) =>
        _sut.Create(OwnerId, name, null, null, null, null, null, PersonaScope.Global, null, null, null, true);

    // Create сферу не выставляет (это шаг C) — переводим созданную персону вручную
    private Persona MakeSphere(string sphereId, string name)
    {
        var p = MakeGlobal(name);
        p.Scope = PersonaScope.Sphere;
        p.SphereId = sphereId;
        return p;
    }

    [Fact]
    public void GetForContext_ПерсонаСферыВидна_ВПроектеСферыИНеВидна_Вне()
    {
        _dir.Spheres["s1"] = ["p1", "p2"];
        var team = MakeSphere("s1", "Командир");

        _sut.GetForContext(OwnerId, "p1").Should().Contain(x => x.Id == team.Id);
        _sut.GetForContext(OwnerId, "p2").Should().Contain(x => x.Id == team.Id);
        _sut.GetForContext(OwnerId, "p3").Should().NotContain(x => x.Id == team.Id);
        _sut.GetForContext(OwnerId, null).Should().NotContain(x => x.Id == team.Id);
    }

    [Fact]
    public void GetForContext_ПроектВышелИзСферы_ПерсонаПропала()
    {
        _dir.Spheres["s1"] = ["p1"];
        var team = MakeSphere("s1", "Командир");
        _sut.GetForContext(OwnerId, "p1").Should().Contain(x => x.Id == team.Id);

        _dir.Spheres["s1"].Remove("p1");

        _sut.GetForContext(OwnerId, "p1").Should().NotContain(x => x.Id == team.Id);
    }

    [Fact]
    public void GetForContext_ПерсонаДругойСферы_НеВидна()
    {
        _dir.Spheres["s1"] = ["p1"];
        _dir.Spheres["s2"] = ["p2"];
        var other = MakeSphere("s2", "Чужая");

        _sut.GetForContext(OwnerId, "p1").Should().NotContain(x => x.Id == other.Id);
    }

    [Fact]
    public void GetByHandle_Коллизия_ПроектПобеждаетСферуСфераПобеждаетГлобальную()
    {
        _dir.Spheres["s1"] = ["p1"];
        var global = MakeGlobal("Аня");
        var sphere = MakeSphere("s1", "Аня");
        var project = MakeProject("p1", "Аня");
        // Принудительно одинаковый handle: Create разводит их по зоне уникальности
        foreach (var p in new[] { global, sphere, project }) p.Handle = "anya";

        _sut.GetByHandle(OwnerId, "anya", "p1")!.Id.Should().Be(project.Id);

        _sut.Delete(project.Id, OwnerId);
        _sut.GetByHandle(OwnerId, "anya", "p1")!.Id.Should().Be(sphere.Id);

        _dir.Spheres["s1"].Clear();
        _sut.GetByHandle(OwnerId, "anya", "p1")!.Id.Should().Be(global.Id);
    }

    [Fact]
    public void GetForContextWithExtras_СферныеПерсоныВходятВБазовыйПул()
    {
        _dir.Spheres["s1"] = ["p1"];
        var team = MakeSphere("s1", "Командир");
        var foreign = MakeProject("p9", "Чужой");

        var pool = _sut.GetForContextWithExtras(OwnerId, "p1", ["p9"], null);

        pool.Select(p => p.Id).Should().Contain([team.Id, foreign.Id]);
    }

    [Fact]
    public void ЗонаHandle_ПерсонаСферыКонфликтуетСПроектнымиПерсонамиПроектовСферы()
    {
        _dir.Spheres["s1"] = ["p1"];
        MakeProject("p1", "Борис"); // handle «boris»
        var sphere = MakeGlobal("Борис");
        sphere.Scope = PersonaScope.Sphere;
        sphere.SphereId = "s1";

        _sut.IsHandleAvailableForSphere(OwnerId, "boris", "s1", sphere.Id).Should().BeFalse();
        // Проектная персона проекта вне сферы конфликта не даёт
        MakeProject("p7", "Вера");
        _sut.IsHandleAvailableForSphere(OwnerId, "vera", "s1", sphere.Id).Should().BeTrue();
    }

    [Fact]
    public void GetSphereTeam_ТолькоПерсоныЭтойСферы()
    {
        var mine = MakeSphere("s1", "Своя");
        MakeSphere("s2", "Чужая");
        MakeGlobal("Глобальная");

        _sut.GetSphereTeam(OwnerId, "s1").Select(p => p.Id).Should().Equal(mine.Id);
    }
}
