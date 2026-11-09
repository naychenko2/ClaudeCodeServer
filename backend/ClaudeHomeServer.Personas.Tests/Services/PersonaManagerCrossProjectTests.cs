using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Резолв персон другого проекта через кросс-проектные extra-скоупы (ProjectPersonas-привязки):
// ResolveHandleCandidates (persona_ask по handle) и GetReachable (persona_ask по personaId) —
// personaId ОБЯЗАН идти через тот же пул достижимости, что и handle, иначе стал бы лазейкой
// мимо привязок в любую чужую персону владельца.
public class PersonaManagerCrossProjectTests : IDisposable
{
    private const string OwnerId = "owner-1";
    private readonly string _tempDir;
    private readonly PersonaManager _sut;

    public PersonaManagerCrossProjectTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "pmgr_xproj_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PersonasPath"] = Path.Combine(_tempDir, "personas.json"),
            })
            .Build();
        _sut = new PersonaManager(config);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private Persona MakeProjectPersona(string projectId, string name) =>
        _sut.Create(OwnerId, name, null, null, null, null, null,
            PersonaScope.Project, projectId, null, null, true);

    [Fact]
    public void GetReachable_БезExtraСкоупа_ЧужаяПерсонаНедостижима()
    {
        var stranger = MakeProjectPersona("projB", "Чужая");
        _sut.GetReachable(OwnerId, stranger.Id, "projA", null, null).Should().BeNull();
    }

    [Fact]
    public void GetReachable_extraPersonaIds_ДаётДоступТолькоКНазваннойПерсоне()
    {
        var target = MakeProjectPersona("projB", "Разрешённая");
        var teammate = MakeProjectPersona("projB", "СоседПоКоманде");

        _sut.GetReachable(OwnerId, target.Id, "projA", null, [target.Id]).Should().NotBeNull();
        // Точечная привязка НЕ открывает всю команду проекта — сосед недостижим
        _sut.GetReachable(OwnerId, teammate.Id, "projA", null, [target.Id]).Should().BeNull();
    }

    [Fact]
    public void GetReachable_extraProjectIds_ОткрываетВсюКомандуПроекта()
    {
        var a = MakeProjectPersona("projB", "Первая");
        var b = MakeProjectPersona("projB", "Вторая");

        _sut.GetReachable(OwnerId, a.Id, "projA", ["projB"], null).Should().NotBeNull();
        _sut.GetReachable(OwnerId, b.Id, "projA", ["projB"], null).Should().NotBeNull();
    }

    [Fact]
    public void ResolveHandleCandidates_ДваТёзкиВРазныхПроектах_ДваКандидата()
    {
        var a = MakeProjectPersona("projB", "Маша");
        var b = MakeProjectPersona("projC", "Маша");
        // Оба тёзки видны только через extra-скоупы (не через текущий контекст projA)
        var candidates = _sut.ResolveHandleCandidates(OwnerId, a.Handle, "projA", ["projB", "projC"], null);
        candidates.Select(p => p.Id).Should().BeEquivalentTo([a.Id, b.Id]);
    }
}
