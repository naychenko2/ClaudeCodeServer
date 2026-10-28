using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// REST тем чатов проекта: владение, авторизация, реестр, ручной перенос и запуск разбора
// (202). Идемпотентность повторного запуска с блокирующей моделью — в ChatSubjectSweeperTests:
// здесь раннер фабрики без локального маршрута.
[Trait("Category", "Host")]
public class ChatSubjectsEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>, IDisposable
{
    private readonly HttpClient _client = factory.CreateAuthenticatedClient();
    // Папки проектов теста — чистятся в Dispose, во временном каталоге не копятся
    private readonly List<string> _dirs = [];

    [Fact]
    public async Task БезАвторизации_401()
    {
        var anon = factory.CreateClient();
        (await anon.GetAsync("/api/projects/any/subjects")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ЧужойПроект_404()
    {
        var stranger = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var projectId = await CreateProjectAsync(stranger, "Чужие темы");

        (await _client.GetAsync($"/api/projects/{projectId}/subjects")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Реестр_ПереносИВозвратАвтоматике()
    {
        var projectId = await CreateProjectAsync(_client, "Темы с реестром");
        var put = await _client.PutAsJsonAsync($"/api/projects/{projectId}/subjects",
            new { subjects = new[] { new { name = "Деплой" }, new { name = "Интерфейс" } } });
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var registry = JsonSerializer.Deserialize<JsonElement>(await put.Content.ReadAsStringAsync());
        registry.GetArrayLength().Should().Be(2);
        var subjectId = registry[0].GetProperty("id").GetString();

        (await _client.PutAsJsonAsync($"/api/projects/{projectId}/subjects",
            new { subjects = new[] { new { name = "Дубль" }, new { name = "ДУБЛЬ" } } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var chat = await factory.Services.GetRequiredService<SessionManager>().CreateAsync(projectId, ClaudeMode.Auto);
        var before = chat.UpdatedAt;
        (await _client.PutAsJsonAsync($"/api/projects/{projectId}/sessions/{chat.Id}/subject", new { subjectId }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        chat.SubjectId.Should().Be(subjectId);
        chat.SubjectSource.Should().Be(SubjectSources.Human);
        chat.UpdatedAt.Should().Be(before);

        (await _client.PutAsJsonAsync($"/api/projects/{projectId}/sessions/{chat.Id}/subject",
            new { subjectId = "нет-такой" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await _client.PutAsJsonAsync($"/api/projects/{projectId}/sessions/{chat.Id}/subject", new { release = true }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        chat.SubjectId.Should().BeNull();
        chat.SubjectSource.Should().BeNull();
    }

    // С-1 ревью: две строки с одним id дали бы две темы с одним ключом, и следующий PUT падал
    // бы на ToDictionary — реестр через API больше не правился бы. Н-2: тело без списка — 400,
    // а не молчаливое «удалить все темы»
    [Fact]
    public async Task Реестр_ДубльIdИПустоеТело_400_РеестрЦел()
    {
        var projectId = await CreateProjectAsync(_client, "Темы дубль id");
        var put = await _client.PutAsJsonAsync($"/api/projects/{projectId}/subjects",
            new { subjects = new[] { new { name = "Деплой" } } });
        var id = JsonSerializer.Deserialize<JsonElement>(await put.Content.ReadAsStringAsync())[0]
            .GetProperty("id").GetString();

        (await _client.PutAsJsonAsync($"/api/projects/{projectId}/subjects",
            new { subjects = new[] { new { id, name = "Деплой" }, new { id, name = "Сборка" } } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _client.PutAsJsonAsync($"/api/projects/{projectId}/subjects", new { subjects = (object?)null }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _client.PutAsJsonAsync($"/api/projects/{projectId}/subjects", new { }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var registry = JsonSerializer.Deserialize<JsonElement>(
            await (await _client.GetAsync($"/api/projects/{projectId}/subjects")).Content.ReadAsStringAsync());
        registry.GetArrayLength().Should().Be(1, "отказы реестр не тронули");
        (await _client.PutAsJsonAsync($"/api/projects/{projectId}/subjects",
            new { subjects = new[] { new { id, name = "Выкатка" } } }))
            .StatusCode.Should().Be(HttpStatusCode.OK, "реестр по-прежнему правится");
    }

    [Fact]
    public async Task Запуск_202_ИСостояние()
    {
        var projectId = await CreateProjectAsync(_client, "Темы запуск");

        var resp = await _client.PostAsync($"/api/projects/{projectId}/subjects/run", null);

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        body.GetProperty("state").GetString().Should().BeOneOf("running", "idle");
        await (factory.Services.GetRequiredService<ChatSubjectSweeper>().CurrentManualRun ?? Task.CompletedTask);

        var state = JsonSerializer.Deserialize<JsonElement>(
            await (await _client.GetAsync($"/api/projects/{projectId}/subjects/run")).Content.ReadAsStringAsync());
        state.GetProperty("state").GetString().Should().Be("idle");
        state.GetProperty("lastError").GetString().Should().Contain("Локальная модель не настроена",
            "у раннера фабрики нет локального маршрута — в облако разбор не уходит");
    }

    // Проекция DTO проекта ручная (ProjectsController.WithCount): без явной строки поля тем
    // молча не доезжали бы до фронта — подсказке «старые чаты разбираются кнопкой» нужна граница
    [Fact]
    public async Task DtoПроекта_ОтдаётРеестрИНижнююГраницуФона()
    {
        var projectId = await CreateProjectAsync(_client, "Темы в DTO");
        var dto = await GetProjectAsync(projectId);
        dto.GetProperty("subjectsSweepSince").ValueKind.Should().Be(JsonValueKind.Null, "фон ещё не проходил");
        dto.GetProperty("subjectRegistry").GetArrayLength().Should().Be(0);

        var since = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        factory.Services.GetRequiredService<ProjectManager>().EnsureSubjectsSweepSince(projectId, since);
        (await _client.PutAsJsonAsync($"/api/projects/{projectId}/subjects",
            new { subjects = new[] { new { name = "Деплой" } } })).EnsureSuccessStatusCode();

        dto = await GetProjectAsync(projectId);
        dto.GetProperty("subjectsSweepSince").GetDateTime().ToUniversalTime().Should().Be(since);
        dto.GetProperty("subjectRegistry")[0].GetProperty("name").GetString().Should().Be("Деплой");
    }

    private async Task<JsonElement> GetProjectAsync(string projectId)
    {
        var resp = await _client.GetAsync($"/api/projects/{projectId}");
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
    }

    public void Dispose()
    {
        foreach (var dir in _dirs) TestFs.DeleteDirectoryResilient(dir);
    }

    private async Task<string> CreateProjectAsync(HttpClient client, string name)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
            "ccs_subjects_" + Guid.NewGuid().ToString("N"))).FullName;
        _dirs.Add(dir);
        var resp = await client.PostAsJsonAsync("/api/projects", new { name, rootPath = dir });
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }
}
