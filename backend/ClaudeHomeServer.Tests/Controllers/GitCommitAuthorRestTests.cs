using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// S3-1b: коммит из REST в общем проекте несёт автора-человека (git %an/%ae и трейлер
/// CCS-User). По решению Q1 автором может быть только владелец: Write участнику закрыт
/// (ProjectAccessService.cs:24), поэтому редактор на той же ручке получает 403 и коммита
/// не делает — это проверяет отдельный тест. Личный проект — без CCS-User, поведение
/// прежнее. И один блок трейлеров: AppendDossierTrailer (CCS-Session) и WithUserTrailer
/// (CCS-User) дописываются в существующий блок сообщения (Co-Authored-By, CCS-Task) —
/// git %(trailers) видит их все. Настоящий git во временной папке; тесты на живом хосте
/// (TestWebApplicationFactory).
/// </summary>
[Trait("Category", "Host")]
public class GitCommitAuthorRestTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory;
    private readonly string _repo;

    public GitCommitAuthorRestTests()
    {
        _factory = new TestWebApplicationFactory();
        _factory.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
        _repo = Path.Combine(_factory.TempDir, "gitauthor_rest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repo);
    }

    public void Dispose() => _factory.Dispose();

    private static string IdOf(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("id").GetString()!;

    // Сырой git для арранжа и для чтения результата: базовая идентичность репозитория —
    // «Тест» (она же ожидаемый автор/коммиттер там, где автора не передают).
    private static async Task<string> RunGitAsync(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["GIT_AUTHOR_NAME"] = "Тест";
        psi.Environment["GIT_AUTHOR_EMAIL"] = "test@test";
        psi.Environment["GIT_COMMITTER_NAME"] = "Тест";
        psi.Environment["GIT_COMMITTER_EMAIL"] = "test@test";
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        p.ExitCode.Should().Be(0, $"git {string.Join(' ', args)} должен выполниться: {stdout}");
        return stdout;
    }

    private async Task<string> SetupRepoAsync()
    {
        await RunGitAsync(_repo, "init", "-b", "main");
        await File.WriteAllTextAsync(Path.Combine(_repo, "a.txt"), "текст\n");
        await RunGitAsync(_repo, "add", "-A");
        return (await RunGitAsync(_repo, "commit", "-m", "начальный")).Trim();
    }

    private async Task<(HttpClient Owner, string ProjectId)> CreateSharedProjectAsync()
    {
        var owner = _factory.CreateAuthenticatedClient();
        var created = await owner.PostAsJsonAsync("/api/projects",
            new { name = "автор-rest-" + Guid.NewGuid().ToString("N")[..8], rootPath = _repo });
        created.EnsureSuccessStatusCode();
        var projectId = IdOf(await created.Content.ReadAsStringAsync());
        return (owner, projectId);
    }

    // Трейлерный блок git: строки «Ключ: значение» последнего абзаца — то, что git считает
    // трейлерами. Если склейка рвёт блок — здесь выпадают целые строки (дефект вердикта R-S3-1).
    private async Task<List<string>> GitTrailersAsync()
    {
        var outp = await RunGitAsync(_repo, "log", "-1", "--format=%(trailers:only=true,unfold=true)");
        return outp.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
    }

    private async Task<(string An, string Ae, string Cn, string Body)> HeadOfAsync()
    {
        var outp = await RunGitAsync(_repo, "log", "-1", "--format=%an\u001f%ae\u001f%cn\u001f%B");
        var parts = outp.Trim().Split('\u001f');
        parts.Should().HaveCount(4);
        return (parts[0], parts[1], parts[2], parts[3].Trim());
    }

    // Общий проект с участником-редактором: рубильник включён у обоих (без него участник
    // проекта не видит — ProjectAccessService.cs:22), участник добавлен. Возвращает
    // клиентов владельца и редактора, id проекта и их записи в UserStore.
    private async Task<(HttpClient Owner, HttpClient Editor, string ProjectId, User OwnerUser, User EditorUser)>
        SetupSharedWithEditorAsync()
    {
        foreach (var c in new[] { _factory.CreateAuthenticatedClient(),
                                 _factory.CreateAuthenticatedClient(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword) })
            (await c.PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = true })).EnsureSuccessStatusCode();

        var (owner, projectId) = await CreateSharedProjectAsync();
        var editorUser = _factory.Services.GetRequiredService<UserStore>()
            .FindByUsername(TestWebApplicationFactory.SecondUsername)!;
        var ownerUser = _factory.Services.GetRequiredService<UserStore>()
            .FindByUsername(TestWebApplicationFactory.TestUsername)!;
        _factory.Services.GetRequiredService<ProjectManager>().SetMembers(projectId,
            [new ProjectMember { UserId = editorUser.Id, Role = ProjectRole.Editor, AddedBy = ownerUser.Id }]);
        return (owner,
            _factory.CreateAuthenticatedClient(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword),
            projectId, ownerUser, editorUser);
    }

    [Fact]
    public async Task КоммитВладельцаОбщегоПроекта_АвторВладелец_ТрейлерыОднимБлоком()
    {
        await SetupRepoAsync();
        var (owner, _, projectId, ownerUser, _) = await SetupSharedWithEditorAsync();

        var chat = await owner.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto" });
        chat.EnsureSuccessStatusCode();
        var chatId = IdOf(await chat.Content.ReadAsStringAsync());

        // Сообщение из UI-коммита уже заканчивается блоком трейлеров (так их приносит ход
        // агента: Co-Authored-By + CCS-Task) — именно этот случай рвал AppendDossierTrailer.
        owner.DefaultRequestHeaders.Add("X-Caller-Session-Id", chatId);
        var resp = await owner.PostAsJsonAsync($"/api/projects/{projectId}/git/commit", new
        {
            message = "feat: правка\n\nCo-Authored-By: Claude Code <noreply@anthropic.com>\nCCS-Task: task-77",
        });
        resp.EnsureSuccessStatusCode();

        var (an, ae, cn, body) = await HeadOfAsync();
        an.Should().Be(ownerUser.Username, "автор коммита — текущий пользователь запроса (sub JWT)");
        ae.Should().Be($"{ownerUser.Username}@users.invalid");
        cn.Should().Be("Тест", "committer — прежний, из конфига репозитория");
        Regex.Matches(body, @"(?m)^CCS-User:").Count.Should().Be(1, "ровно один трейлер автора");
        body.Should().Contain($"CCS-User: {ownerUser.Id}");

        var trailers = await GitTrailersAsync();
        trailers.Should().Contain(t => t.StartsWith("Co-Authored-By:"));
        trailers.Should().Contain("CCS-Task: task-77");
        trailers.Should().Contain($"CCS-Session: {chatId}");
        trailers.Should().Contain($"CCS-User: {ownerUser.Id}");
    }

    // Q1: Write участнику закрыт (ProjectAccessService.cs:24) — коммитить из REST в общем
    // проекте редактор не может: тот же отказ, что и остальная запись участника, — 403
    // «Нет права на запись» (ProjectScopeAttribute.cs:87-97). Отказанное действие не оставляет
    // коммита: HEAD не двигается, автора-участника через REST нет ни в каком сценарии.
    [Fact]
    public async Task КоммитРедактораОбщегоПроекта_Отказ403_КоммитаНет()
    {
        var baseSha = await SetupRepoAsync();
        var (_, editor, projectId, _, _) = await SetupSharedWithEditorAsync();

        var resp = await editor.PostAsJsonAsync($"/api/projects/{projectId}/git/commit", new
        {
            message = "feat: правка редактора\n\nCo-Authored-By: Claude Code <noreply@anthropic.com>",
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden, "Write участнику закрыт (Q1)");

        var head = (await RunGitAsync(_repo, "rev-parse", "HEAD")).Trim();
        head.Should().Be(baseSha, "отказанное действие не оставило коммита");
    }

    [Fact]
    public async Task КоммитЛичногоПроекта_БезCCSUser_ПоведениеПрежнее()
    {
        await SetupRepoAsync();
        var (owner, projectId) = await CreateSharedProjectAsync(); // участников не добавляем — личный

        var chat = await owner.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto" });
        chat.EnsureSuccessStatusCode();
        var chatId = IdOf(await chat.Content.ReadAsStringAsync());

        owner.DefaultRequestHeaders.Add("X-Caller-Session-Id", chatId);
        var resp = await owner.PostAsJsonAsync($"/api/projects/{projectId}/git/commit", new
        {
            message = "feat: личное\n\nCo-Authored-By: Claude Code <noreply@anthropic.com>",
        });
        resp.EnsureSuccessStatusCode();

        var (an, _, _, body) = await HeadOfAsync();
        an.Should().Be("Тест", "личный проект — автора не передаём, git-конфиг как раньше");
        body.Should().NotContain("CCS-User");

        // служебный трейлер сессии — как раньше, и тоже в одном блоке с Co-Authored-By
        var trailers = await GitTrailersAsync();
        trailers.Should().Contain(t => t.StartsWith("Co-Authored-By:"));
        trailers.Should().Contain($"CCS-Session: {chatId}");
    }
}
