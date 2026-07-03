using System.Text.Json;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Реестр глобальных ролей: пул, прикомандирование к проектам, миграция старого формата
public class RoleManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IConfiguration _config;

    public RoleManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "roles_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            }).Build();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private RoleManager Create() => new(_config);

    // --- Создание: глобальный найм и найм из проекта ---

    [Fact]
    public void Create_WithoutProject_RoleIsInPoolOnly()
    {
        var sut = Create();
        var role = sut.Create(null, "Игорь", "Бэкендер", "🔧", "#D97757", "дотошный", null, null, null, null);

        role.ProjectIds.Should().BeEmpty();
        sut.GetAll().Should().ContainSingle(r => r.Id == role.Id);
    }

    [Fact]
    public void Create_WithProject_RoleAssignedToIt()
    {
        var sut = Create();
        var role = sut.Create("proj-1", "Игорь", "", "", "", "", null, null, null, null);

        role.ProjectIds.Should().BeEquivalentTo(["proj-1"]);
        sut.GetByProject("proj-1").Should().ContainSingle(r => r.Id == role.Id);
        sut.GetByProject("proj-2").Should().BeEmpty();
    }

    // --- Прикомандирование / открепление ---

    [Fact]
    public void Assign_AddsProject_AndIsIdempotent()
    {
        var sut = Create();
        var role = sut.Create(null, "Оля", "", "", "", "", null, null, null, null);

        sut.Assign(role.Id, "proj-1").Should().BeTrue();
        sut.Assign(role.Id, "proj-1").Should().BeTrue();   // повторное — не дублирует

        sut.GetById(role.Id)!.ProjectIds.Should().BeEquivalentTo(["proj-1"]);
    }

    [Fact]
    public void Unassign_RemovesProject_RoleStaysInPool()
    {
        var sut = Create();
        var role = sut.Create("proj-1", "Оля", "", "", "", "", null, null, null, null);

        sut.Unassign(role.Id, "proj-1").Should().BeTrue();

        sut.GetByProject("proj-1").Should().BeEmpty();
        sut.GetAll().Should().ContainSingle(r => r.Id == role.Id);   // осталась в пуле
    }

    [Fact]
    public void Assign_UnknownRole_ReturnsFalse()
    {
        Create().Assign("нет-такой", "proj-1").Should().BeFalse();
    }

    // --- Персистентность и миграция ---

    [Fact]
    public void Roles_SurviveReload()
    {
        var role = Create().Create("proj-1", "Игорь", "Бэкендер", "🔧", "#D97757", "дотошный",
            ["backend-dev"], "доп", "haiku", "low", ["Почини сборку", "Проверь тесты"]);

        var reloaded = Create().GetById(role.Id);

        reloaded.Should().NotBeNull();
        reloaded!.Name.Should().Be("Игорь");
        reloaded.ProjectIds.Should().BeEquivalentTo(["proj-1"]);
        reloaded.AgentNames.Should().BeEquivalentTo(["backend-dev"]);
        reloaded.Model.Should().Be("haiku");
        reloaded.Suggestions.Should().BeEquivalentTo(["Почини сборку", "Проверь тесты"]);
    }

    [Fact]
    public void Load_LegacyPerProjectFormat_MigratesToProjectIds()
    {
        // Старый формат: поле ProjectId строкой, ProjectIds нет
        var legacyJson = JsonSerializer.Serialize(new[]
        {
            new { Id = "r1", ProjectId = "proj-legacy", Name = "Старожил", Title = "", Avatar = "",
                  Color = "", Persona = "", AgentNames = new string[0],
                  CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        });
        File.WriteAllText(Path.Combine(_tempDir, "roles.json"), legacyJson);

        var sut = Create();

        var role = sut.GetById("r1");
        role.Should().NotBeNull();
        role!.ProjectIds.Should().BeEquivalentTo(["proj-legacy"]);
        // Карта миграции — для переноса памяти (roleId → прежний проект)
        sut.MigratedLegacyProjects.Should().ContainKey("r1").WhoseValue.Should().Be("proj-legacy");

        // Файл пересохранён в новом формате: повторная загрузка уже не считается миграцией
        var again = Create();
        again.GetById("r1")!.ProjectIds.Should().BeEquivalentTo(["proj-legacy"]);
        again.MigratedLegacyProjects.Should().BeEmpty();
    }

    // --- Обновление и удаление ---

    [Fact]
    public void Update_EmptyString_ClearsOptionalFields()
    {
        var sut = Create();
        var role = sut.Create(null, "Игорь", "", "", "", "", null, "промпт", "haiku", "low");

        var updated = sut.Update(role.Id, null, null, null, null, null, null, "", "", "");

        updated!.SystemPrompt.Should().BeNull();
        updated.Model.Should().BeNull();
        updated.Effort.Should().BeNull();
        updated.Name.Should().Be("Игорь");   // null-поля не трогаются
    }

    [Fact]
    public void Delete_RemovesRole()
    {
        var sut = Create();
        var role = sut.Create("proj-1", "Игорь", "", "", "", "", null, null, null, null);

        sut.Delete(role.Id).Should().BeTrue();

        sut.GetAll().Should().BeEmpty();
        Create().GetAll().Should().BeEmpty();   // и после перезагрузки
    }
}
