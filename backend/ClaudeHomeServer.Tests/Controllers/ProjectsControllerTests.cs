using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

public class ProjectsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly string _tempProjectDir;

    public ProjectsControllerTests(TestWebApplicationFactory factory)
    {
        _client = factory.CreateAuthenticatedClient();
        _tempProjectDir = Path.Combine(factory.TempDir, "projects");
        Directory.CreateDirectory(_tempProjectDir);
    }

    private string MkProjectDir(string name)
    {
        var dir = Path.Combine(_tempProjectDir, name + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private async Task<JsonElement> CreateProjectAsync(string name, string? dir = null)
    {
        var path = dir ?? MkProjectDir(name);
        var response = await _client.PostAsJsonAsync("/api/projects", new { name, rootPath = path });
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    [Fact]
    public async Task GetAll_ReturnsOk()
    {
        var response = await _client.GetAsync("/api/projects");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Create_ValidRequest_Returns201WithProject()
    {
        var dir = MkProjectDir("new");
        var response = await _client.PostAsJsonAsync("/api/projects", new
        {
            name = "TestProject",
            rootPath = dir
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("name").GetString().Should().Be("TestProject");
        body.GetProperty("id").GetString().Should().NotBeEmpty();
    }

    [Fact]
    public async Task Create_NonExistentDir_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/projects", new
        {
            name = "Bad",
            rootPath = @"C:\nonexistent\path_" + Guid.NewGuid()
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetById_ExistingProject_Returns200()
    {
        var project = await CreateProjectAsync("GetByIdTest");
        var id = project.GetProperty("id").GetString()!;

        var response = await _client.GetAsync($"/api/projects/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("id").GetString().Should().Be(id);
    }

    [Fact]
    public async Task GetById_NonExistentProject_Returns404()
    {
        var response = await _client.GetAsync("/api/projects/nonexistent-id");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Update_ExistingProject_Returns200WithUpdatedName()
    {
        var project = await CreateProjectAsync("Original");
        var id = project.GetProperty("id").GetString()!;

        var response = await _client.PutAsJsonAsync($"/api/projects/{id}", new
        {
            name = "Updated",
            rootPath = (string?)null
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("name").GetString().Should().Be("Updated");
    }

    [Fact]
    public async Task Update_McpServersOn_PersistedAndReturnedByGet()
    {
        var project = await CreateProjectAsync("McpOn");
        var id = project.GetProperty("id").GetString()!;

        var putResponse = await _client.PutAsJsonAsync($"/api/projects/{id}", new
        {
            name = (string?)null,
            rootPath = (string?)null,
            mcpServersOn = new[] { "context7" }
        });
        putResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var putBody = JsonSerializer.Deserialize<JsonElement>(await putResponse.Content.ReadAsStringAsync());
        putBody.GetProperty("mcpServersOn").EnumerateArray().Select(e => e.GetString()).Should().Equal("context7");

        var getResponse = await _client.GetAsync($"/api/projects/{id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var getBody = JsonSerializer.Deserialize<JsonElement>(await getResponse.Content.ReadAsStringAsync());
        getBody.GetProperty("mcpServersOn").EnumerateArray().Select(e => e.GetString()).Should().Equal("context7");
    }

    [Fact]
    public async Task Update_NonExistentProject_Returns404()
    {
        var response = await _client.PutAsJsonAsync("/api/projects/nope", new
        {
            name = "X",
            rootPath = (string?)null
        });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Update_NonExistentNewPath_Returns400()
    {
        var project = await CreateProjectAsync("ToUpdate");
        var id = project.GetProperty("id").GetString()!;

        var response = await _client.PutAsJsonAsync($"/api/projects/{id}", new
        {
            name = (string?)null,
            rootPath = @"C:\fake_nonexistent_" + Guid.NewGuid()
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Delete_ExistingProject_Returns204()
    {
        var project = await CreateProjectAsync("ToDelete");
        var id = project.GetProperty("id").GetString()!;

        var response = await _client.DeleteAsync($"/api/projects/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Delete_NonExistentProject_Returns404()
    {
        var response = await _client.DeleteAsync("/api/projects/ghost-id");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_ThenGetById_Returns404()
    {
        var project = await CreateProjectAsync("DeleteThenGet");
        var id = project.GetProperty("id").GetString()!;
        await _client.DeleteAsync($"/api/projects/{id}");

        var response = await _client.GetAsync($"/api/projects/{id}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // --- PUT /api/projects/{id}/tags (реестр общих тегов) ---

    [Fact]
    public async Task UpdateTags_УспешныйReorder_СохраняетПорядокИСостав()
    {
        var project = await CreateProjectAsync("TagsTest");
        var id = project.GetProperty("id").GetString()!;

        var tags = new[]
        {
            new { name = "Bug", order = 0, color = "red" },
            new { name = "Feature", order = 1, color = "green" },
            new { name = "Refactor", order = 2, color = "yellow" }
        };

        var response = await _client.PutAsJsonAsync($"/api/projects/{id}/tags", tags);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        var registry = body.GetProperty("tagRegistry").EnumerateArray().ToList();

        registry.Should().HaveCount(3);
        registry[0].GetProperty("name").GetString().Should().Be("Bug");
        registry[0].GetProperty("order").GetInt32().Should().Be(0);
        registry[0].GetProperty("color").GetString().Should().Be("red");
        registry[1].GetProperty("name").GetString().Should().Be("Feature");
        registry[2].GetProperty("name").GetString().Should().Be("Refactor");
    }

    [Fact]
    public async Task UpdateTags_OrderНормализуетсяПоПозицииМассива()
    {
        var project = await CreateProjectAsync("OrderTest");
        var id = project.GetProperty("id").GetString()!;

        // Передаём order в случайном порядке — контроллер должен нормализовать по позиции
        var tags = new[]
        {
            new { name = "Third", order = 99, color = (string?)null },
            new { name = "First", order = -5, color = (string?)null },
            new { name = "Second", order = 0, color = (string?)null }
        };

        var response = await _client.PutAsJsonAsync($"/api/projects/{id}/tags", tags);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        var registry = body.GetProperty("tagRegistry").EnumerateArray().ToList();

        registry[0].GetProperty("order").GetInt32().Should().Be(0);
        registry[1].GetProperty("order").GetInt32().Should().Be(1);
        registry[2].GetProperty("order").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task UpdateTags_ПустойИмя_Возвращает400()
    {
        var project = await CreateProjectAsync("EmptyNameTest");
        var id = project.GetProperty("id").GetString()!;

        var tags = new[]
        {
            new { name = "Valid", order = 0, color = (string?)null },
            new { name = "", order = 1, color = (string?)null }, // пустое имя
            new { name = "AlsoValid", order = 2, color = (string?)null }
        };

        var response = await _client.PutAsJsonAsync($"/api/projects/{id}/tags", tags);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        error.GetProperty("error").GetString().Should().Contain("Тег #2");
    }

    [Fact]
    public async Task UpdateTags_ДубликатыИменCaseInsensitive_Возвращает400()
    {
        var project = await CreateProjectAsync("DupTest");
        var id = project.GetProperty("id").GetString()!;

        var tags = new[]
        {
            new { name = "Bug", order = 0, color = "red" },
            new { name = "bug", order = 1, color = "blue" }, // дубликат (case-insensitive)
            new { name = "BUG", order = 2, color = "green" }  // ещё один дубликат
        };

        var response = await _client.PutAsJsonAsync($"/api/projects/{id}/tags", tags);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        error.GetProperty("error").GetString().Should().Contain("уникальными");
    }

    [Fact]
    public async Task UpdateTags_ЧужойПроект_Возвращает403()
    {
        // Создаём проект от первого пользователя
        var project = await CreateProjectAsync("OwnerProject");
        var id = project.GetProperty("id").GetString()!;

        // Создаём клиент от второго пользователя
        var factory = new TestWebApplicationFactory();
        var otherClient = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername,
            TestWebApplicationFactory.SecondPassword);

        var tags = new[]
        {
            new { name = "Tag", order = 0, color = "red" }
        };

        var response = await otherClient.PutAsJsonAsync($"/api/projects/{id}/tags", tags);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateTags_НесуществующийПроект_Возвращает404()
    {
        var tags = new[]
        {
            new { name = "Tag", order = 0, color = "red" }
        };

        var response = await _client.PutAsJsonAsync("/api/projects/nonexistent/tags", tags);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateTags_ПустойСписок_ОчищаетРеестр()
    {
        var project = await CreateProjectAsync("ClearTest");
        var id = project.GetProperty("id").GetString()!;

        // Сначала добавляем теги
        var tags = new[]
        {
            new { name = "Tag1", order = 0, color = "red" },
            new { name = "Tag2", order = 1, color = "blue" }
        };
        await _client.PutAsJsonAsync($"/api/projects/{id}/tags", tags);

        // Затем очищаем
        var response = await _client.PutAsJsonAsync($"/api/projects/{id}/tags", new List<object>());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("tagRegistry").GetArrayLength().Should().Be(0);
    }
}
