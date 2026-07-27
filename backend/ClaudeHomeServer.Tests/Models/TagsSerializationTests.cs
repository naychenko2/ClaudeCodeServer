using System.Text.Json;
using ClaudeHomeServer.Models;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Models;

/// <summary>
/// Тесты сериализации новых полей тегов (Session.Tags, Project.TagRegistry, ProjectTag).
/// Платформонезависимые: используют Path.GetTempPath для временных файлов.
/// </summary>
public class TagsSerializationTests : IDisposable
{
    private readonly string _tempDir;

    public TagsSerializationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tags_serial_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    // --- Session.Tags ---

    [Fact]
    public void SessionTags_RoundTrip_СохраняетСписокТегов()
    {
        var session = new Session
        {
            Id = "test-session",
            Tags = ["tag1", "tag2", "tag3"]
        };

        var file = Path.Combine(_tempDir, "session.json");
        var json = JsonSerializer.Serialize(session);
        File.WriteAllText(file, json);

        var deserialized = JsonSerializer.Deserialize<Session>(File.ReadAllText(file));

        deserialized.Should().NotBeNull();
        deserialized!.Tags.Should().BeEquivalentTo(["tag1", "tag2", "tag3"]);
    }

    [Fact]
    public void SessionTags_ПустойСписок_ДесериализуетсяКакПустойНеНул()
    {
        var session = new Session
        {
            Id = "test-session",
            Tags = []
        };

        var file = Path.Combine(_tempDir, "session.json");
        var json = JsonSerializer.Serialize(session);
        File.WriteAllText(file, json);

        var deserialized = JsonSerializer.Deserialize<Session>(File.ReadAllText(file));

        deserialized.Should().NotBeNull();
        deserialized!.Tags.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void SessionTags_ПоУмолчанию_ПустойСписокНеНул()
    {
        var session = new Session();

        session.Tags.Should().NotBeNull().And.BeEmpty();
    }

    // --- Project.TagRegistry и ProjectTag ---

    [Fact]
    public void ProjectTag_RoundTrip_СохраняетВсеПоля()
    {
        var tag = new ProjectTag
        {
            Name = "Frontend",
            Order = 2,
            Color = "blue"
        };

        var file = Path.Combine(_tempDir, "tag.json");
        var json = JsonSerializer.Serialize(tag);
        File.WriteAllText(file, json);

        var deserialized = JsonSerializer.Deserialize<ProjectTag>(File.ReadAllText(file));

        deserialized.Should().NotBeNull();
        deserialized!.Name.Should().Be("Frontend");
        deserialized.Order.Should().Be(2);
        deserialized.Color.Should().Be("blue");
    }

    [Fact]
    public void ProjectTag_БезЦвета_RoundTripСохраняет()
    {
        var tag = new ProjectTag
        {
            Name = "Backend",
            Order = 1,
            Color = null
        };

        var file = Path.Combine(_tempDir, "tag-nocolor.json");
        var json = JsonSerializer.Serialize(tag);
        File.WriteAllText(file, json);

        var deserialized = JsonSerializer.Deserialize<ProjectTag>(File.ReadAllText(file));

        deserialized.Should().NotBeNull();
        deserialized!.Name.Should().Be("Backend");
        deserialized.Order.Should().Be(1);
        deserialized.Color.Should().BeNull();
    }

    [Fact]
    public void ProjectTagRegistry_RoundTrip_СохраняетСписокТегов()
    {
        var project = new Project
        {
            Id = "test-project",
            Name = "Test Project",
            TagRegistry = new List<ProjectTag>
            {
                new() { Name = "Bug", Order = 0, Color = "red" },
                new() { Name = "Feature", Order = 1, Color = "green" },
                new() { Name = "Refactor", Order = 2, Color = "yellow" }
            }
        };

        var file = Path.Combine(_tempDir, "project.json");
        var json = JsonSerializer.Serialize(project);
        File.WriteAllText(file, json);

        var deserialized = JsonSerializer.Deserialize<Project>(File.ReadAllText(file));

        deserialized.Should().NotBeNull();
        deserialized!.TagRegistry.Should().HaveCount(3);
        deserialized.TagRegistry[0].Name.Should().Be("Bug");
        deserialized.TagRegistry[0].Order.Should().Be(0);
        deserialized.TagRegistry[0].Color.Should().Be("red");
        deserialized.TagRegistry[1].Name.Should().Be("Feature");
        deserialized.TagRegistry[2].Name.Should().Be("Refactor");
    }

    [Fact]
    public void ProjectTagRegistry_ПустойСписок_ДесериализуетсяКакПустойНеНул()
    {
        var project = new Project
        {
            Id = "test-project",
            Name = "Test Project",
            TagRegistry = []
        };

        var file = Path.Combine(_tempDir, "project-empty.json");
        var json = JsonSerializer.Serialize(project);
        File.WriteAllText(file, json);

        var deserialized = JsonSerializer.Deserialize<Project>(File.ReadAllText(file));

        deserialized.Should().NotBeNull();
        deserialized!.TagRegistry.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void ProjectTagRegistry_ПоУмолчанию_ПустойСписокНеНул()
    {
        var project = new Project();

        project.TagRegistry.Should().NotBeNull().And.BeEmpty();
    }
}
