using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClaudeHomeServer.Tests.Controllers;

// Каскад удаления проекта → шов уборки обучающих фото (ADR-026, раздел 5): биометрия не должна
// оставаться сиротой в data/lora-training, а сбой или отсутствие шва не должны ронять удаление.
public class ProjectsControllerTrainingPhotosCascadeTests
{
    private sealed class RecordingCleaner(bool throws = false) : IProjectTrainingPhotoCleaner
    {
        public List<(string Owner, string Project)> Calls { get; } = [];

        public void PurgeProject(string ownerId, string projectId)
        {
            Calls.Add((ownerId, projectId));
            if (throws) throw new IOException("диск недоступен");
        }
    }

    private static async Task<string> CreateProjectAsync(TestWebApplicationFactory factory, HttpClient client)
    {
        var dir = Path.Combine(factory.TempDir, "tp_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var response = await client.PostAsJsonAsync("/api/projects", new { name = "TP", rootPath = dir });
        response.EnsureSuccessStatusCode();
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        return body.GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Delete_ВызываетШовУборкиФотоСВладельцемИПроектом()
    {
        var cleaner = new RecordingCleaner();
        using var factory = new TestWebApplicationFactory { ExtraServices = s => s.AddSingleton<IProjectTrainingPhotoCleaner>(cleaner) };
        var client = factory.CreateAuthenticatedClient();
        var ownerId = factory.Services.GetRequiredService<UserStore>().FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        var projectId = await CreateProjectAsync(factory, client);

        var response = await client.DeleteAsync($"/api/projects/{projectId}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        cleaner.Calls.Should().ContainSingle().Which.Should().Be((ownerId, projectId));
    }

    [Fact]
    public async Task Delete_ШовБросилИсключение_УдалениеПроектаВсёРавноУспешно()
    {
        var cleaner = new RecordingCleaner(throws: true);
        using var factory = new TestWebApplicationFactory { ExtraServices = s => s.AddSingleton<IProjectTrainingPhotoCleaner>(cleaner) };
        var client = factory.CreateAuthenticatedClient();
        var projectId = await CreateProjectAsync(factory, client);

        var response = await client.DeleteAsync($"/api/projects/{projectId}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        cleaner.Calls.Should().HaveCount(1);
        (await client.GetAsync($"/api/projects/{projectId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_ШваНет_УдалениеПроектаНеЛомается()
    {
        using var factory = new TestWebApplicationFactory
        {
            ExtraServices = s =>
            {
                foreach (var d in s.Where(d => d.ServiceType == typeof(IProjectTrainingPhotoCleaner)).ToList()) s.Remove(d);
            }
        };
        var client = factory.CreateAuthenticatedClient();
        var projectId = await CreateProjectAsync(factory, client);

        var response = await client.DeleteAsync($"/api/projects/{projectId}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
