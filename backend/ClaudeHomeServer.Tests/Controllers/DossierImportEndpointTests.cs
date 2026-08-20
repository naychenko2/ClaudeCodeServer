using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Git;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Controllers;

// HTTP-смоук ручного импорта «Историй решений» (этап 4, волна 2): живой TestServer,
// репозиторий с веткой ccs/dossiers/v1 пишется напрямую plumbing-методом GitService
// (фикстура, не субъект теста), дальше всё через HTTP — флаг, POST /dossiers/import,
// повторный вызов (идемпотентность на уровне файла стора) и GET /dossiers с признаком
// происхождения в ответе. Содержательная логика коллизий/парсинга покрыта
// DossierImporterTests — здесь только транспорт.
[Trait("Category", "Slow")]
public class DossierImportEndpointTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public DossierImportEndpointTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    private async Task<string> CreateGitProjectAsync()
    {
        // Репозиторий с веткой паспортов: init без коммитов рабочего дерева не нужен —
        // WriteDossiersBranchAsync пишет первый коммит ветки без родителя
        var dir = Path.Combine(_factory.TempDir, "dossier_import_http_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var git = new GitService(TestLauncherFactory.Instance);
        await git.InitAsync(null, dir);

        // Паспорт в формате экспортёра: содержимое — вручную по формату FormatDossier
        // (контракт формата проверяется в DossierImporterTests раунд-трипом)
        const string md = """
            # feat: импорт через HTTP

            - Коммит: `1a2b3c4d` (2026-07-01)
            - Источник: чат sess-http

            ## Зачем

            проверка импорта

            ## Решения

            - решение одно
            """;
        const string filePath = "dossiers/2026/07/1a2b3c4d-import-http.md";
        const string index = """
            {"version":1,"entries":[{"sha":"1a2b3c4d","file":"dossiers/2026/07/1a2b3c4d-import-http.md",
            "subject":"feat: импорт через HTTP","committedAt":"2026-07-01T10:00:00Z","discussion":null,
            "taskId":null,"supersededSha":[]}]}
            """;
        await git.WriteDossiersBranchAsync(null, dir,
            [new GitDossierFile(filePath, md), new GitDossierFile("index.json", index)],
            "test: ветка паспортов");

        var response = await _client.PostAsJsonAsync("/api/projects", new { name = "DossierImport", rootPath = dir });
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> ImportAsync(string projectId)
    {
        var response = await _client.PostAsync($"/api/projects/{projectId}/dossiers/import", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "импорт на репозитории с веткой обязан проходить");
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }

    // Файл стора проекта (владелец один в фикстуре, но каталог ищем по проекту — не завязываемся на формат id)
    private string StoreFile(string projectId) =>
        Directory.GetFiles(Path.Combine(_factory.TempDir, "dossiers"), projectId + ".json",
            SearchOption.AllDirectories).Single();

    [Fact]
    public async Task Импорт_ДобавляетЗаписи_Идемпотентен_ИОтдаётПроисхождение()
    {
        // Флаг выставляем явно (состояние per-user живёт в общей фабрике класса)
        (await _client.PutAsJsonAsync(
            $"/api/feature-flags/{FeatureFlagKeys.ChangeDossiersRecall}", new { enabled = true }))
            .EnsureSuccessStatusCode();

        var projectId = await CreateGitProjectAsync();

        // Первый вызов: запись добавлена с признаком происхождения
        var first = await ImportAsync(projectId);
        first.GetProperty("status").GetString().Should().Be("imported");
        first.GetProperty("added").GetInt32().Should().Be(1);
        first.GetProperty("skipped").GetInt32().Should().Be(0);

        var storeFile = StoreFile(projectId);
        (await File.ReadAllTextAsync(storeFile))
            .Should().Contain("\"Origin\":\"imported\"", "файл стора хранит признак происхождения");

        var list = JsonSerializer.Deserialize<JsonElement>(
            await (await _client.GetAsync($"/api/projects/{projectId}/dossiers")).Content.ReadAsStringAsync());
        var imported = list.EnumerateArray().Should().ContainSingle().Subject;
        imported.GetProperty("commitSha").GetString().Should().Be("1a2b3c4d");
        imported.GetProperty("origin").GetString().Should().Be("imported", "GET отдаёт признак происхождения");
        imported.GetProperty("importedAuthor").GetString().Should().Be("AI Home");
        imported.GetProperty("importedFromBranch").GetString().Should().Be("ccs/dossiers/v1");
        imported.GetProperty("why").GetString().Should().Be("проверка импорта");
        imported.GetProperty("decisions")[0].GetString().Should().Be("решение одно");

        // Повторный вызов того же состояния ветки: no-op, файл стора не переписывается
        var bytes = await File.ReadAllBytesAsync(storeFile);
        var second = await ImportAsync(projectId);
        second.GetProperty("status").GetString().Should().Be("nothingToImport");
        second.GetProperty("added").GetInt32().Should().Be(0);
        second.GetProperty("skipped").GetInt32().Should().Be(1);
        (await File.ReadAllBytesAsync(storeFile)).Should().Equal(bytes, "файл стора не изменился");
    }

    [Fact]
    public async Task БезВетки_ЯвныйNoBranch()
    {
        (await _client.PutAsJsonAsync(
            $"/api/feature-flags/{FeatureFlagKeys.ChangeDossiersRecall}", new { enabled = true }))
            .EnsureSuccessStatusCode();

        var dir = Path.Combine(_factory.TempDir, "dossier_import_nobranch_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var git = new GitService(TestLauncherFactory.Instance);
        await git.InitAsync(null, dir);
        var response = await _client.PostAsJsonAsync("/api/projects", new { name = "DossierNoBranch", rootPath = dir });
        response.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;

        var result = await ImportAsync(projectId);

        result.GetProperty("status").GetString().Should().Be("noBranch");
        result.GetProperty("added").GetInt32().Should().Be(0);
        result.GetProperty("skipped").GetInt32().Should().Be(0);
    }

    // Признак наличия ветки паспортов в exportStatus: им фронт гейтит кнопку «Загрузить».
    // Ветку пишет фикстура WriteDossiersBranchAsync — проверяем только отражение в ответе.
    private async Task<JsonElement> ExportStatusAsync(string projectId)
    {
        var response = await _client.GetAsync($"/api/projects/{projectId}/dossiers/export/status");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ExportStatus_ОтражаетНаличиеВеткиПаспортов()
    {
        (await _client.PutAsJsonAsync(
            $"/api/feature-flags/{FeatureFlagKeys.ChangeDossiersRecall}", new { enabled = true }))
            .EnsureSuccessStatusCode();

        // Репозиторий с веткой: фикстура уже записала refs/heads/ccs/dossiers/v1
        var withBranch = await ExportStatusAsync(await CreateGitProjectAsync());
        withBranch.GetProperty("isGitRepo").GetBoolean().Should().BeTrue();
        withBranch.GetProperty("hasDossierBranch").GetBoolean()
            .Should().BeTrue("ветка ccs/dossiers/v1 существует");

        // Репозиторий без ветки паспортов
        var dir = Path.Combine(_factory.TempDir, "dossier_status_nobranch_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        await new GitService(TestLauncherFactory.Instance).InitAsync(null, dir);
        var response = await _client.PostAsJsonAsync("/api/projects", new { name = "DossierStatusNoBranch", rootPath = dir });
        response.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;

        var noBranch = await ExportStatusAsync(projectId);
        noBranch.GetProperty("hasDossierBranch").GetBoolean()
            .Should().BeFalse("ветки ccs/dossiers/v1 нет");
    }
}
