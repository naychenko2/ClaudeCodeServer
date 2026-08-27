using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Инъекция git-опций через branch на REST-пути /git/log (блокер приёмки волны 3.1).
/// Валидация живёт в GitService.ValidateRevision — общий слой для MCP (git_log) и REST;
/// юнит-оси на живом git — GitServiceTests, здесь проверяем, что тонкая прокси контроллера
/// не пропускает опцию наружу (раньше «--output=…» перезаписывала произвольный файл).
/// </summary>
public class GitControllerLogTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();
    private readonly string _repo;

    public GitControllerLogTests()
    {
        _repo = Path.Combine(_factory.TempDir, "gitlog_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repo);
    }

    public void Dispose() => _factory.Dispose();

    // Коммит без опоры на глобальный git-конфиг (в CI user.name не задан)
    private static async Task CommitAsync(string dir, string message)
    {
        ProcessStartInfo Git(params string[] args)
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
            return psi;
        }

        async Task RunAsync(string because, params string[] args)
        {
            using var p = Process.Start(Git(args))!;
            await p.WaitForExitAsync();
            p.ExitCode.Should().Be(0, because);
        }

        await RunAsync("арранж: init обязан получиться", "init", "-b", "main");
        await File.WriteAllTextAsync(Path.Combine(dir, "a.txt"), "текст\n");
        await RunAsync("арранж: add обязан получиться", "add", "-A");
        await RunAsync("арранж: базовый коммит обязан получиться", "commit", "-m", message);
    }

    [Fact]
    public async Task Log_ВеткаОпция_ОтказИФайлНеСоздан()
    {
        await CommitAsync(_repo, "начальный");

        var client = _factory.CreateAuthenticatedClient();
        var project = await client.PostAsJsonAsync("/api/projects", new
        {
            name = "gitlog-" + Guid.NewGuid().ToString("N")[..8],
            rootPath = _repo,
        });
        project.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(
            await project.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;

        var target = Path.Combine(_factory.TempDir,
            "gitlog_inject_" + Guid.NewGuid().ToString("N") + ".txt");
        var resp = await client.GetAsync(
            $"/api/projects/{projectId}/git/log?branch=--output={target.Replace('\\', '/')}");

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "некорректная ревизия — GitCommandException контроллер маппит в 409");
        (await resp.Content.ReadAsStringAsync()).Should().Contain("Некорректная ревизия");
        File.Exists(target).Should().BeFalse(
            "git не должен был выполниться — файл вне репо не создаётся и не затирается");
    }
}
