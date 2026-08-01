using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClaudeHomeServer.Tests.Controllers;

// Гейт длины записи памяти команды (POST/PUT /api/projects/{id}/team-memory): запрет срабатывает
// только на РОСТ сверх TeamMemoryService.MaxTextLength — уже сохранённую сверхлимитную запись
// (легаси, авто-захват) можно пересохранить без изменений или сократить, как
// PersonaManager.ExceedsContractLimit для контракта персоны.
public class ProjectsControllerTeamMemoryTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly string _tempProjectDir;

    public ProjectsControllerTeamMemoryTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
        _tempProjectDir = Path.Combine(factory.TempDir, "team-memory-projects");
        Directory.CreateDirectory(_tempProjectDir);
    }

    private async Task<string> CreateProjectAsync()
    {
        var dir = Path.Combine(_tempProjectDir, "p_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var response = await _client.PostAsJsonAsync("/api/projects", new { name = "TM", rootPath = dir });
        response.EnsureSuccessStatusCode();
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        return body.GetProperty("id").GetString()!;
    }

    // Запись сверх лимита в обход HTTP-гейта — как легаси-запись или результат авто-захвата
    // (Add() в сервисе гейтом не покрыт, ограничение только на ручной записи через контроллер)
    private string SeedOversizedEntry(string ownerId, string projectId, int length)
    {
        var svc = _factory.Services.GetRequiredService<TeamMemoryService>();
        var entry = svc.Add(ownerId, projectId, new string('и', length));
        return entry.Id;
    }

    [Fact]
    public async Task Add_ТекстСверхЛимита_Возвращает400()
    {
        var projectId = await CreateProjectAsync();
        var text = new string('и', TeamMemoryService.MaxTextLength + 1);

        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/team-memory", new { text });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_РаздутаяЗаписьТемЖеТекстом_ПересохранениеБезРостаРазрешено()
    {
        var ownerId = _factory.Services.GetRequiredService<UserStore>().FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        var projectId = await CreateProjectAsync();
        var oversized = TeamMemoryService.MaxTextLength * 3;
        var entryId = SeedOversizedEntry(ownerId, projectId, oversized);
        var sameText = new string('и', oversized);

        var response = await _client.PutAsJsonAsync($"/api/projects/{projectId}/team-memory/{entryId}", new { text = sameText });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Update_РаздутаяЗапись_СокращениеВсёЕщёСверхЛимитаРазрешено()
    {
        var ownerId = _factory.Services.GetRequiredService<UserStore>().FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        var projectId = await CreateProjectAsync();
        var entryId = SeedOversizedEntry(ownerId, projectId, TeamMemoryService.MaxTextLength * 3);
        // Сокращаем с 3000 до 1500 (условный пример из задачи) — всё ещё сверх MaxTextLength=1000,
        // но меньше текущего размера, поэтому обязано пройти
        var shrunk = new string('и', TeamMemoryService.MaxTextLength * 3 / 2);

        var response = await _client.PutAsJsonAsync($"/api/projects/{projectId}/team-memory/{entryId}", new { text = shrunk });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Update_РаздутаяЗапись_РостЗапрещён()
    {
        var ownerId = _factory.Services.GetRequiredService<UserStore>().FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        var projectId = await CreateProjectAsync();
        var currentLength = TeamMemoryService.MaxTextLength * 2;
        var entryId = SeedOversizedEntry(ownerId, projectId, currentLength);
        var grown = new string('и', currentLength + 500);

        var response = await _client.PutAsJsonAsync($"/api/projects/{projectId}/team-memory/{entryId}", new { text = grown });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_ЗаписьВПределахЛимита_ПересохранениеТемЖеТекстомРазрешено()
    {
        var projectId = await CreateProjectAsync();
        var text = new string('и', TeamMemoryService.MaxTextLength - 10);
        var created = await _client.PostAsJsonAsync($"/api/projects/{projectId}/team-memory", new { text });
        created.EnsureSuccessStatusCode();
        var entry = JsonSerializer.Deserialize<JsonElement>(await created.Content.ReadAsStringAsync());
        var entryId = entry.GetProperty("id").GetString()!;

        var response = await _client.PutAsJsonAsync($"/api/projects/{projectId}/team-memory/{entryId}", new { text });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
