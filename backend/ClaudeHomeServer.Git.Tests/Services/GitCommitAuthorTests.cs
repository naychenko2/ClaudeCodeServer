using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Git;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Автор коммита рабочего дерева (S3-1): GitCommitAuthor подменяет автора через env
// GIT_AUTHOR_*, коммиттер остаётся из конфига репозитория; id человека несёт ровно один
// трейлер `CCS-User:` в теле. Настоящий git в временной папке (пути — Path.GetTempPath).
public class GitCommitAuthorTests : IAsyncLifetime, IDisposable
{
    private sealed class LocalOnlyFactory : ILauncherFactory
    {
        public IProcessLauncher Local => LocalProcessRunner.Instance;
        public IProcessLauncher ForOwner(string? ownerId) => Local;
        public IProcessLauncher ForProject(ClaudeHomeServer.Models.Project project) => Local;
    }

    private static readonly Regex UserTrailerLine = new(@"(?m)^CCS-User:", RegexOptions.Compiled);

    private readonly string _repo;
    private readonly GitService _git = new(new LocalOnlyFactory());

    public GitCommitAuthorTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "gitauthor_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repo);
    }

    public async Task InitializeAsync()
    {
        await _git.InitAsync(null, _repo);
        // Базовая идентичность репозитория — она же ожидаемый committer в тестах с автором.
        await _git.RunAsync(null, _repo, ["config", "user.email", "test@test"]);
        await _git.RunAsync(null, _repo, ["config", "user.name", "Тест"]);
        await File.WriteAllTextAsync(Path.Combine(_repo, "a.txt"), "один\n");
        await _git.StageAllAsync(null, _repo);
        await _git.CommitAsync(null, _repo, "начальный коммит");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); }
        catch { /* git на Windows держит readonly-объекты — не роняем прогон */ }
    }

    // HEAD: автор/коммиттер и сырое тело (%B) — одним вызовом, разделитель \u001f
    // (в тестовых сообщениях его нет — split даёт ровно 5 частей).
    private async Task<(string An, string Ae, string Cn, string Ce, string Body)> HeadOf()
    {
        var r = await _git.RunAsync(null, _repo, ["log", "-1", "--format=%an\u001f%ae\u001f%cn\u001f%ce\u001f%B"]);
        r.Ok.Should().BeTrue();
        var parts = r.Stdout.Trim().Split('\u001f');
        parts.Should().HaveCount(5);
        return (parts[0], parts[1], parts[2], parts[3], parts[4].Trim());
    }

    // git commit с exit 1 и пустым stderr, когда менять нечего («nothing to commit» — в stdout).
    // Каждый тест сначала создаёт свою правку и стейджит её — как в соседних наборах.
    private async Task StageChange(string name)
    {
        await File.WriteAllTextAsync(Path.Combine(_repo, name), "содержимое\n");
        await _git.StageAsync(null, _repo, name);
    }

    // Трейлерный блок так, как его видит git: строки `Ключ: значение` из последнего абзаца.
    // Пустой вывод — пустой массив.
    private async Task<string[]> GitTrailers()
    {
        var r = await _git.RunAsync(null, _repo, ["log", "-1", "--format=%(trailers:only=true,unfold=true)"]);
        r.Ok.Should().BeTrue();
        var s = r.Stdout.Trim();
        return s.Length == 0 ? Array.Empty<string>() : s.Split('\n');
    }

    [Fact]
    public async Task Автор_СтавитсяЧерезEnv_ИТрейлерВДоле()
    {
        await StageChange("f1.txt");
        var sha = await _git.CommitAsync(null, _repo, "feat: правка a",
            author: new GitCommitAuthor("user-42", "Мария", "maria"));
        sha.Should().NotBeNullOrWhiteSpace();

        var (an, ae, cn, ce, body) = await HeadOf();
        an.Should().Be("Мария");
        ae.Should().Be("maria@users.invalid");
        // Коммиттер прежний: GIT_COMMITTER_* не ставим, git берёт конфиг репозитория.
        cn.Should().Be("Тест");
        ce.Should().Be("test@test");
        body.Should().StartWith("feat: правка a");
        UserTrailerLine.Matches(body).Count.Should().Be(1);
        body.Should().Contain("CCS-User: user-42");
    }

    [Fact]
    public async Task Автор_НеДублирует_УжеЕстьТрейлер()
    {
        // Сообщение принёс вызывающий с готовым трейлером — второй не добавляем:
        // источник правды о человеке — существующая строка, ровно одна.
        await StageChange("f2.txt");
        await _git.CommitAsync(null, _repo,
            "fix: правка\n\nCCS-User: user-7\nCCS-Session: sess-1",
            author: new GitCommitAuthor("user-42", "Мария", "maria"));

        var (_, _, _, _, body) = await HeadOf();
        UserTrailerLine.Matches(body).Count.Should().Be(1);
        body.Should().Contain("CCS-User: user-7");
        body.Should().NotContain("user-42");
    }

    [Fact]
    public async Task ТрейлерыВызывающего_НеВыпадаютИзБлока()
    {
        // Тело уже заканчивается блоком Co-Authored-By/CCS-Session: CCS-User обязан встать
        // в него одной строкой. Отдельный абзац вывел бы существующие трейлеры из блока —
        // git читает трейлеры только в последнем абзаце (пункт вердикта ревью 0ff5aee9).
        await StageChange("f4.txt");
        await _git.CommitAsync(null, _repo,
            "feat: правка\n\nCo-Authored-By: X <x@y>\nCCS-Session: s1",
            author: new GitCommitAuthor("user-42", "Мария", "maria"));

        var trailers = await GitTrailers();
        trailers.Should().HaveCount(3);
        trailers.Should().Contain(l => l.StartsWith("Co-Authored-By: X <x@y>"));
        trailers.Should().Contain(l => l.StartsWith("CCS-Session: s1"));
        trailers.Should().Contain(l => l.StartsWith("CCS-User: user-42"));
    }

    [Fact]
    public async Task ТелоБезТрейлеров_АбзацемЧерезПустуюСтроку()
    {
        // Блока нет — прежняя склейка через пустую строку: трейлер — единственный абзац.
        await StageChange("f5.txt");
        await _git.CommitAsync(null, _repo, "feat: правка b",
            author: new GitCommitAuthor("user-42", "Мария", "maria"));

        var (_, _, _, _, body) = await HeadOf();
        body.Should().EndWith("\n\nCCS-User: user-42");
        (await GitTrailers()).Should().Equal("CCS-User: user-42");
    }

    [Fact]
    public async Task ТелоИзОдногоЗаголовка_АбзацемЧерезПустуюСтроку()
    {
        // Одноабзацное сообщение git блока трейлеров не видит (проба на git 2.53: без
        // пустой строки перед блоком — пусто), поэтому здесь обязана пустая строка.
        await StageChange("f6.txt");
        await _git.CommitAsync(null, _repo, "feat: только заголовок",
            author: new GitCommitAuthor("user-42", "Мария", "maria"));

        var (_, _, _, _, body) = await HeadOf();
        body.Should().Be("feat: только заголовок\n\nCCS-User: user-42");
        (await GitTrailers()).Should().Equal("CCS-User: user-42");
    }

    [Fact]
    public async Task БезАвтора_КоммитКакРаньше()
    {
        await StageChange("f3.txt");
        await _git.CommitAsync(null, _repo, "обычная правка");

        var (an, ae, cn, ce, body) = await HeadOf();
        an.Should().Be("Тест");
        ae.Should().Be("test@test");
        cn.Should().Be("Тест");
        ce.Should().Be("test@test");
        UserTrailerLine.Matches(body).Count.Should().Be(0);
    }
}
