using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Валидация персоны-исполнителя/постановщика задачи (TaskPersonaValidator):
// проектная персона должна принадлежать своему проекту, ИЛИ иметь ProjectTasks-привязку
// с полным доступом (не readOnly) к целевому проекту.
public class TaskPersonaValidatorTests : IDisposable
{
    private const string OwnerId = "owner-1";
    private static readonly string ProjectA = Guid.NewGuid().ToString();
    private static readonly string ProjectB = Guid.NewGuid().ToString();

    private readonly string _tempDir;
    private readonly PersonaManager _personas;

    public TaskPersonaValidatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tpv_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PersonasPath"] = Path.Combine(_tempDir, "personas.json"),
            })
            .Build();
        _personas = new PersonaManager(config);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private Persona MakeProjectPersona(string projectId, string name,
        List<PersonaBinding>? bindings = null)
    {
        var p = _personas.Create(OwnerId, name, null, null, null, null, null,
            PersonaScope.Project, projectId, null, null, true);
        if (bindings is not null)
            _personas.UpdateBindings(p.Id, OwnerId, bindings);
        return _personas.Get(p.Id, OwnerId)!;
    }

    private static PersonaBinding ProjectTasksBinding(string projectId, bool readOnly) =>
        new()
        {
            Type = PersonaBindingType.ProjectTasks,
            Target = projectId,
            Path = readOnly ? "readonly" : "",
        };

    // --- Без привязок (существующее поведение) ---

    [Fact]
    public void СвояПерсона_СвойПроект_БезОшибки()
    {
        var p = MakeProjectPersona(ProjectA, "Своя");
        var err = TaskPersonaValidator.Error(_personas, OwnerId, p.Id, ProjectA);
        err.Should().BeNull("персона своего проекта может выполнять задачи");
    }

    [Fact]
    public void ЧужаяПерсона_БезПривязки_Ошибка()
    {
        var p = MakeProjectPersona(ProjectB, "Чужая");
        var err = TaskPersonaValidator.Error(_personas, OwnerId, p.Id, ProjectA);
        err.Should().Be("Проектная персона может выполнять только задачи своего проекта");
    }

    [Fact]
    public void НетПерсоны_Ошибка()
    {
        var err = TaskPersonaValidator.Error(_personas, OwnerId, "nonexistent", ProjectA);
        err.Should().Be("Персона не найдена или недоступна");
    }

    [Fact]
    public void ЛичнаяЗадача_ПроектнаяПерсона_Ошибка()
    {
        var p = MakeProjectPersona(ProjectA, "Своя");
        var err = TaskPersonaValidator.Error(_personas, OwnerId, p.Id, taskProjectId: null);
        err.Should().Be("Проектная персона может выполнять только задачи своего проекта");
    }

    [Fact]
    public void ЛичнаяЗадача_ПроектнаяПерсонаЧужая_Ошибка()
    {
        var p = MakeProjectPersona(ProjectB, "Чужая");
        var err = TaskPersonaValidator.Error(_personas, OwnerId, p.Id, taskProjectId: null);
        err.Should().Be("Проектная персона может выполнять только задачи своего проекта");
    }

    // --- С кросс-проектной ProjectTasks-привязкой ---

    [Fact]
    public void ProjectTasks_ПолныйДоступ_РазрешаетСоздание()
    {
        var p = MakeProjectPersona(ProjectB, "ЧужойСПривязкой",
            [ProjectTasksBinding(ProjectA, readOnly: false)]);
        var scopes = new List<(string ProjectId, bool ReadOnly)> { (ProjectA, false) };

        var err = TaskPersonaValidator.Error(_personas, OwnerId, p.Id, ProjectA, scopes);
        err.Should().BeNull("ProjectTasks с полным доступом разрешает");
    }

    [Fact]
    public void ProjectTasks_ReadOnly_БлокируетСоздание()
    {
        var p = MakeProjectPersona(ProjectB, "ЧужойСReadOnly",
            [ProjectTasksBinding(ProjectA, readOnly: true)]);
        var scopes = new List<(string ProjectId, bool ReadOnly)> { (ProjectA, true) };

        var err = TaskPersonaValidator.Error(_personas, OwnerId, p.Id, ProjectA, scopes);
        err.Should().Be("Проектная персона может выполнять только задачи своего проекта");
    }

    [Fact]
    public void ProjectTasks_НаДругойПроект_НеРазрешаетТретий()
    {
        var p = MakeProjectPersona(ProjectB, "Чужой",
            [ProjectTasksBinding(ProjectA, readOnly: false)]);
        var scopes = new List<(string ProjectId, bool ReadOnly)> { (ProjectA, false) };

        var err = TaskPersonaValidator.Error(_personas, OwnerId, p.Id, ProjectC, scopes);
        err.Should().Be("Проектная персона может выполнять только задачи своего проекта");
    }

    private static readonly string ProjectC = Guid.NewGuid().ToString();

    [Fact]
    public void ProjectTasks_СвойПроект_ИгнорируетScopes()
    {
        var p = MakeProjectPersona(ProjectA, "Своя",
            [ProjectTasksBinding(ProjectB, readOnly: false)]);
        var scopes = new List<(string ProjectId, bool ReadOnly)> { (ProjectB, false) };

        // Свой проект должен работать независимо от scopes (даже если они есть)
        var err = TaskPersonaValidator.Error(_personas, OwnerId, p.Id, ProjectA, scopes);
        err.Should().BeNull("свой проект разрешён всегда, scopes не влияют");
    }

    [Fact]
    public void ProjectTasks_БезScopes_НеВлияет()
    {
        var p = MakeProjectPersona(ProjectB, "ЧужойСПривязкой",
            [ProjectTasksBinding(ProjectA, readOnly: false)]);
        // Без передачи externalScopes — старое поведение
        var err = TaskPersonaValidator.Error(_personas, OwnerId, p.Id, ProjectA);
        err.Should().Be("Проектная персона может выполнять только задачи своего проекта");
    }
}
