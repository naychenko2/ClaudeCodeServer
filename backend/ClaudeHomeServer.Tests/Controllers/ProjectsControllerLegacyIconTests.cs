using System.Net;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// Дефект d77bd1bf: записи projects.json, созданные до ADR-009, не несут поля icon.
// Такой проект не должен ронять весь список (админ тогда не видит проектов вовсе):
// «без значка» — состояние инициалов, а не ошибка. Обе формы старых данных —
// отсутствие поля и явный null — читаются в тот же дефолтный запись, что и
// недавно созданный проект без цвета: kind=initials, glyph=null, color=null.
[Trait("Category", "Host")]
public class ProjectsControllerLegacyIconTests
{
    private const string LegacyId = "legacy-no-icon";

    // Минимальная старая запись проекта: без ownerId — миграция стора назначает её
    // первому пользователю (testuser), как на проде. Формат пути экранируется —
    // фикстура читается одинаково на Windows и Linux.
    private static string FixtureJson(string rootPath, bool explicitNull) =>
        "[{\"id\":\"" + LegacyId + "\",\"name\":\"LegacyProject\",\"rootPath\":\"" +
        rootPath.Replace("\\", "\\\\") +
        "\",\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-01T00:00:00Z\"" +
        (explicitNull ? ",\"icon\":null" : "") + "}]";

    // Фикстура пишется ДО первого клиента: хост стартует на первом обращении, и
    // ProjectManager.Load читает именно этот файл — тот путь, которым старые данные
    // входят в систему.
    private static (TestWebApplicationFactory Factory, HttpClient Client) StartHostWithFixture(bool explicitNull)
    {
        var factory = new TestWebApplicationFactory();
        Directory.CreateDirectory(factory.TempDir);
        var rootPath = Path.Combine(factory.TempDir, "projects", "legacy");
        Directory.CreateDirectory(rootPath);
        File.WriteAllText(Path.Combine(factory.TempDir, "projects.json"), FixtureJson(rootPath, explicitNull));
        return (factory, factory.CreateAuthenticatedClient());
    }

    private static async Task AssertLegacyProjectListed(HttpClient client)
    {
        var response = await client.GetAsync("/api/projects");
        response.StatusCode.Should().Be(HttpStatusCode.OK, "старый проект без значка не должен ронять список проектов");

        var list = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        var entry = list.EnumerateArray().SingleOrDefault(e => e.GetProperty("id").GetString() == LegacyId);
        entry.Should().NotBeNull("старый проект обязан оставаться в списке");

        // Тот же «без значка», что отдаётся новому проекту без цвета: инициалы, ни значка, ни картинки
        var icon = entry.GetProperty("icon");
        icon.GetProperty("kind").GetString().Should().Be("initials");
        icon.GetProperty("glyph").ValueKind.Should().Be(JsonValueKind.Null);
        icon.GetProperty("color").ValueKind.Should().Be(JsonValueKind.Null);
        icon.GetProperty("imageFile").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task List_LegacyRecordWithoutIconField_ReturnsOkWithInitials()
    {
        var (factory, client) = StartHostWithFixture(explicitNull: false);
        try
        {
            await AssertLegacyProjectListed(client);
        }
        finally
        {
            factory.Dispose();
        }
    }

    [Fact]
    public async Task List_LegacyRecordWithNullIcon_ReturnsOkWithInitials()
    {
        var (factory, client) = StartHostWithFixture(explicitNull: true);
        try
        {
            await AssertLegacyProjectListed(client);
        }
        finally
        {
            factory.Dispose();
        }
    }
}
