using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Spheres;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

public class PersonaZoneTests
{
    private const string Owner = "u1";

    // Справочник с изменяемым состоянием: тест двигает проекты и флаг между вызовами
    private sealed class FakeDir : ISphereDirectory
    {
        public bool IsEnabled = true;
        public Dictionary<string, List<string>> Spheres = new();

        public bool Enabled(string ownerId) => IsEnabled;
        public string? SphereOf(string ownerId, string projectId) =>
            IsEnabled ? Spheres.FirstOrDefault(s => s.Value.Contains(projectId)).Key : null;
        public IReadOnlyList<string> ProjectsOf(string ownerId, string sphereId) =>
            IsEnabled && Spheres.TryGetValue(sphereId, out var l) ? l.ToList() : [];
        public string? SphereName(string ownerId, string sphereId) => Spheres.ContainsKey(sphereId) ? "Работа" : null;
    }

    private static Persona SpherePersona(string? sphereId = "s1") =>
        new() { OwnerId = Owner, Scope = PersonaScope.Sphere, SphereId = sphereId };

    [Fact]
    public void Сфера_ФлагВыключен_ЗонаПуста()
    {
        var dir = new FakeDir { IsEnabled = false, Spheres = { ["s1"] = ["p1"] } };
        var persona = SpherePersona();

        PersonaZone.VisibleIn(persona, "p1", dir).Should().BeFalse();
        PersonaZone.ProjectIds(persona, dir).Should().BeEmpty();
    }

    [Fact]
    public void Сфера_СфераУдалена_ЗонаПуста()
    {
        var dir = new FakeDir { Spheres = { ["s1"] = ["p1"] } };
        var persona = SpherePersona();
        dir.Spheres.Remove("s1");

        PersonaZone.VisibleIn(persona, "p1", dir).Should().BeFalse();
        PersonaZone.ProjectIds(persona, dir).Should().BeEmpty();
    }

    [Fact]
    public void Сфера_SphereIdПуст_ЗонаПуста()
    {
        var dir = new FakeDir { Spheres = { ["s1"] = ["p1"] } };

        PersonaZone.VisibleIn(SpherePersona(null), "p1", dir).Should().BeFalse();
        PersonaZone.ProjectIds(SpherePersona(""), dir).Should().BeEmpty();
    }

    [Fact]
    public void Сфера_ПроектВошёлИВышел_СледующийВызовВидитИзменение()
    {
        var dir = new FakeDir { Spheres = { ["s1"] = [] } };
        var persona = SpherePersona();

        PersonaZone.VisibleIn(persona, "p1", dir).Should().BeFalse();
        dir.Spheres["s1"].Add("p1");
        PersonaZone.VisibleIn(persona, "p1", dir).Should().BeTrue();
        PersonaZone.OutOfZoneRefusal(persona, "p1", dir).Should().BeNull();

        dir.Spheres["s1"].Remove("p1");
        PersonaZone.VisibleIn(persona, "p1", dir).Should().BeFalse();
        PersonaZone.OutOfZoneRefusal(persona, "p1", dir).Should().Contain("«Работа»");
    }

    [Fact]
    public void ГлобальнаяИПроектная_ЗонаКакРаньше()
    {
        var dir = new FakeDir { IsEnabled = false };
        var global = new Persona { OwnerId = Owner, Scope = PersonaScope.Global };
        var project = new Persona { OwnerId = Owner, Scope = PersonaScope.Project, ProjectId = "p1" };

        PersonaZone.ProjectIds(global, dir).Should().BeNull();
        PersonaZone.VisibleIn(global, "any", dir).Should().BeTrue();
        PersonaZone.VisibleIn(project, "p1", dir).Should().BeTrue();
        PersonaZone.VisibleIn(project, "p2", dir).Should().BeFalse();
        PersonaZone.OutOfZoneRefusal(project, "p2", dir).Should().BeNull();
    }

    [Fact]
    public void ПамятьСферы_ПишетЧеловекИПерсонаЭтойСферы_ГлобальнаяИПроектнаяНет()
    {
        PersonaZone.CanWriteSphereMemory(null, "s1").Should().BeTrue();
        PersonaZone.CanWriteSphereMemory(SpherePersona("s1"), "s1").Should().BeTrue();
        PersonaZone.CanWriteSphereMemory(SpherePersona("s2"), "s1").Should().BeFalse();
        PersonaZone.CanWriteSphereMemory(new Persona { Scope = PersonaScope.Global, SphereId = "s1" }, "s1").Should().BeFalse();
        PersonaZone.CanWriteSphereMemory(new Persona { Scope = PersonaScope.Project, ProjectId = "p1", SphereId = "s1" }, "s1").Should().BeFalse();
    }
}
