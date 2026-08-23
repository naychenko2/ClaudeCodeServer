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
using Xunit;

namespace ClaudeHomeServer.Tests.Controllers;

// HTTP-прогон поля autoExport в GET /dossiers/export/status: гейт автовыгрузки стал
// опрашиваемым (DossierAutoExportGate), и статус обязан честно отдавать причину, по
// которой фон молчит, — иначе панель истории обещает «выгружается само» там, где
// выгрузка идёт только по кнопке. Сама логика гейта покрыта сервисными тестами
// DossierAutoExportTests — здесь транспорт: каждая ситуация доводится до своей
// wire-строки.
[Trait("Category", "Slow")]
public class DossierExportStatusAutoExportTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly GitService _git = new(TestLauncherFactory.Instance);

    public DossierExportStatusAutoExportTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    private async Task EnableFlagAsync() =>
        (await _client.PutAsJsonAsync(
            $"/api/feature-flags/{FeatureFlagKeys.ChangeDossiersRecall}", new { enabled = true }))
        .EnsureSuccessStatusCode();

    // Репозиторий + проект основного пользователя; withBranch — сразу пишем ветку
    // паспортов plumbing-методом (фикстура по образцу DossierImportEndpointTests,
    // субъект теста — только статус)
    private async Task<(string ProjectId, string Dir)> CreateGitProjectAsync(string name, bool withBranch)
    {
        var dir = Path.Combine(_factory.TempDir, name + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        await _git.InitAsync(null, dir);
        if (withBranch)
            await _git.WriteDossiersBranchAsync(null, dir,
                [new GitDossierFile("index.json", """{"version":1,"entries":[]}""")],
                "test: ветка паспортов");
        var response = await _client.PostAsJsonAsync("/api/projects", new { name, rootPath = dir });
        response.EnsureSuccessStatusCode();
        var id = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
        return (id, dir);
    }

    private async Task<string?> AutoExportAsync(string projectId)
    {
        var response = await _client.GetAsync($"/api/projects/{projectId}/dossiers/export/status");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync())
            .GetProperty("autoExport").GetString();
    }

    private Task<GitResult> GitAsync(string root, params string[] args) => _git.RunAsync(null, root, args);

    private string TestOwnerId() => _factory.Services.GetRequiredService<UserStore>()
        .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

    [Fact]
    public async Task СвояВетка_СтатусActive()
    {
        await EnableFlagAsync();
        var (projectId, dir) = await CreateGitProjectAsync("DossierStOwn", withBranch: true);

        // Tip помечен созданным нашей выгрузкой — как после ручной/авто выгрузки
        var tip = (await GitAsync(dir, "rev-parse", GitService.DossiersRef)).Stdout.Trim();
        _factory.Services.GetRequiredService<DossierCaptureState>().MarkOwnTip(TestOwnerId(), projectId, tip);

        (await AutoExportAsync(projectId)).Should().Be("active",
            "tip ветки создан нашей выгрузкой — фон пишет поверх, подсказка «выгружается само» честна");
    }

    [Fact]
    public async Task ЧужойTip_СтатусForeignTip()
    {
        await EnableFlagAsync();
        // Ветка есть, но метки MarkOwnTip нет — как после git pull соседа/второй машины
        var (projectId, _) = await CreateGitProjectAsync("DossierStForeign", withBranch: true);

        (await AutoExportAsync(projectId)).Should().Be("foreignTip",
            "tip не создан нашей выгрузкой — фон молчит, выгрузка только по кнопке");
    }

    [Fact]
    public async Task ТолькоOriginВетка_СтатусOriginOnly()
    {
        await EnableFlagAsync();
        var (projectId, dir) = await CreateGitProjectAsync("DossierStOrphan", withBranch: true);

        // Ветка уезжает в bare-origin, локальная копия сносится — репо, куда ветку
        // привёз fetch (фикстура по образцу DossierAutoExportTests.ТолькоОriginВетка)
        var bare = Path.Combine(_factory.TempDir, "remote_status_" + Guid.NewGuid().ToString("N")[..8] + ".git");
        Directory.CreateDirectory(bare);
        (await GitAsync(bare, "init", "--bare")).Ok.Should().BeTrue("bare-репозиторий обязан создаться");
        (await GitAsync(dir, "remote", "add", "origin", bare)).Ok.Should().BeTrue();
        (await GitAsync(dir, "push", "origin", GitService.DossiersRef)).Ok.Should()
            .BeTrue("фикстура: ветка запушена в origin");
        (await GitAsync(dir, "update-ref", "-d", GitService.DossiersRef)).Ok.Should().BeTrue();

        (await AutoExportAsync(projectId)).Should().Be("originOnly",
            "есть только origin-ветка — фон локальную сироту не создаёт, выгрузка по кнопке");
    }

    [Fact]
    public async Task ОбщаяПапка_СтатусSharedFolder()
    {
        await EnableFlagAsync();
        var (projectId, dir) = await CreateGitProjectAsync("DossierStShared", withBranch: false);

        // Второй владелец подключает ту же папку своим проектом (между владельцами
        // общая папка допустима — ограничение «одна папка = один проект» per-owner)
        using var second = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        (await second.PostAsJsonAsync("/api/projects", new { name = "DossierStSharedNb", rootPath = dir }))
            .EnsureSuccessStatusCode();

        (await AutoExportAsync(projectId)).Should().Be("sharedFolder",
            "папку делят два владельца — фон не выгружает без ведома человека");
    }
}
