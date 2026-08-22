using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Dossiers;
using ClaudeHomeServer.Services.Git;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// HTTP-тесты листинга паспортов GET /dossiers: контракт ответа { entries, coverage }
// (спринт «История решений», блок В) — метрика охвата коммитов паспортами за окно
// 7 суток. Коммиты делает живой git в temp-репозитории; записи стора сеет DossierStore
// напрямую — сам захват паспорта при коммите из чата тут не субъект.
[Trait("Category", "Slow")]
public class DossiersControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public DossiersControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    // Проект на git-репозитории с двумя коммитами: первый трогает a.txt (+ .gitignore
    // из InitAsync), второй — b.txt. Возвращает id проекта и sha первого коммита.
    private async Task<(string ProjectId, string FirstSha)> CreateGitProjectAsync(string name)
    {
        var dir = Path.Combine(_factory.TempDir, "dossiers_list_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var git = new GitService(TestLauncherFactory.Instance);
        await git.InitAsync(null, dir);
        await git.RunAsync(null, dir, ["config", "user.email", "test@test"]);
        await git.RunAsync(null, dir, ["config", "user.name", "Тест"]);

        await File.WriteAllTextAsync(Path.Combine(dir, "a.txt"), "первый\n");
        await git.StageAllAsync(null, dir);
        var firstSha = await git.CommitAsync(null, dir, "коммит один");
        await File.WriteAllTextAsync(Path.Combine(dir, "b.txt"), "второй\n");
        await git.StageAllAsync(null, dir);
        await git.CommitAsync(null, dir, "коммит два");

        var response = await _client.PostAsJsonAsync("/api/projects", new { name, rootPath = dir });
        response.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
        return (projectId, firstSha);
    }

    // Паспорт в стор владельца тестового пользователя (тот же синглтон, что в контроллере)
    private void SeedDossier(string projectId, string sha, string[]? files = null, DateTimeOffset? committedAt = null)
    {
        var owner = _factory.Services.GetRequiredService<UserStore>()
            .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        _factory.Services.GetRequiredService<DossierStore>().Add(new ChangeDossier
        {
            OwnerId = owner,
            ProjectId = projectId,
            CommitSha = sha,
            CommitSubject = "субъект",
            CommittedAt = committedAt ?? DateTimeOffset.UtcNow,
            Files = [.. (files ?? [])],
            Why = "проверка охвата",
        });
    }

    private async Task<JsonElement> GetListAsync(string query)
    {
        var response = await _client.GetAsync($"/api/projects/{query}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Листинг_ОтдаётЗаписи_ИМетрикуОхвата()
    {
        var (projectId, firstSha) = await CreateGitProjectAsync("DossiersList");
        SeedDossier(projectId, firstSha, ["a.txt"]);
        SeedDossier(projectId, "ffffffff", ["b.txt"]);

        // Без фильтров: обе записи, знаменатель — все коммиты окна
        var payload = await GetListAsync($"{projectId}/dossiers");
        payload.GetProperty("entries").EnumerateArray().Should().HaveCount(2);
        var coverage = payload.GetProperty("coverage");
        coverage.GetProperty("periodDays").GetInt32().Should().Be(7);
        coverage.GetProperty("commits").GetInt32().Should().Be(2, "оба коммита репозитория в окне");
        coverage.GetProperty("dossiers").GetInt32().Should().Be(2);

        // Файловый фильтр: и записи, и знаменатель сужаются до истории одного файла
        var filtered = await GetListAsync($"{projectId}/dossiers?file=a.txt");
        filtered.GetProperty("entries").EnumerateArray().Should().ContainSingle();
        var fileCoverage = filtered.GetProperty("coverage");
        fileCoverage.GetProperty("commits").GetInt32()
            .Should().Be(1, "a.txt трогал только первый коммит");
        fileCoverage.GetProperty("dossiers").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Листинг_ДубльSha_СчитаетОдинКоммит()
    {
        var (projectId, firstSha) = await CreateGitProjectAsync("DossiersDedup");
        // Свой и импортированный паспорта об одном коммите: sha даже в разном регистре —
        // числитель охвата один коммит, а не две записи (регрессия консилиума 2026-08-22)
        SeedDossier(projectId, firstSha, ["a.txt"]);
        SeedDossier(projectId, firstSha.ToUpperInvariant(), ["a.txt"]);

        var payload = await GetListAsync($"{projectId}/dossiers");
        payload.GetProperty("entries").EnumerateArray().Should().HaveCount(2);
        payload.GetProperty("coverage").GetProperty("dossiers").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Листинг_ЗаписиВнеОкна_НеУвеличиваютЧислитель()
    {
        var (projectId, firstSha) = await CreateGitProjectAsync("DossiersWindow");
        // Свежий паспорт в окне + два паспорта старше окна: числитель считает только окно,
        // иначе (регрессия прода 2026-08-22, «372 из 200») числитель перерастал знаменатель
        SeedDossier(projectId, firstSha, ["a.txt"]);
        SeedDossier(projectId, "deadbee1", ["b.txt"], DateTimeOffset.UtcNow.AddDays(-8));
        SeedDossier(projectId, "deadbee2", ["b.txt"], DateTimeOffset.UtcNow.AddDays(-30));

        var payload = await GetListAsync($"{projectId}/dossiers");
        payload.GetProperty("entries").EnumerateArray().Should().HaveCount(3);
        var coverage = payload.GetProperty("coverage");
        coverage.GetProperty("commits").GetInt32().Should().Be(2);
        coverage.GetProperty("dossiers").GetInt32().Should().Be(1, "паспорта старше окна не считаются");
    }

    [Fact]
    public async Task Листинг_БезGit_ОтдаётПустуюМетрику()
    {
        var dir = Path.Combine(_factory.TempDir, "dossiers_nogit_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var response = await _client.PostAsJsonAsync("/api/projects", new { name = "DossiersNoGit", rootPath = dir });
        response.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;

        var payload = await GetListAsync($"{projectId}/dossiers");
        payload.GetProperty("entries").EnumerateArray().Should().BeEmpty();
        var coverage = payload.GetProperty("coverage");
        coverage.GetProperty("periodDays").GetInt32().Should().Be(7);
        coverage.GetProperty("commits").GetInt32().Should().Be(0, "не git-репозиторий — метрика нулевая");
        coverage.GetProperty("dossiers").GetInt32().Should().Be(0);
    }
}
