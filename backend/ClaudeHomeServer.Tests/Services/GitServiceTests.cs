using System.Diagnostics;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Git;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Интеграционные тесты GitService на настоящем git CLI: временный репозиторий на диске,
// запуск через LocalProcessRunner (среда local; container-путь проверяется смоуком).
public class GitServiceTests : IAsyncLifetime, IDisposable
{
    private sealed class LocalOnlyFactory : ILauncherFactory
    {
        public IProcessLauncher Local => LocalProcessRunner.Instance;
        public IProcessLauncher ForOwner(string? ownerId) => Local;
    }

    private readonly string _repo;
    private readonly GitService _git = new(new LocalOnlyFactory());

    public GitServiceTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "gitsvc_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repo);
    }

    public async Task InitializeAsync()
    {
        await _git.InitAsync(null, _repo);
        await RawGit("config", "user.email", "test@test");
        await RawGit("config", "user.name", "Тест");
        await File.WriteAllTextAsync(Path.Combine(_repo, "a.txt"), "один\nдва\nтри\n");
        await File.WriteAllTextAsync(Path.Combine(_repo, "b.txt"), "b1\n");
        await _git.StageAllAsync(null, _repo);
        await _git.CommitAsync(null, _repo, "начальный коммит");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); }
        catch { /* git на Windows держит readonly-объекты — не роняем прогон */ }
    }

    // Прямой git для арранжей (без ассертов на сам GitService)
    private async Task RawGit(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _repo, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync();
    }

    [Fact]
    public async Task Status_Разбирает_Staged_Unstaged_Untracked()
    {
        await File.WriteAllTextAsync(Path.Combine(_repo, "a.txt"), "один\nДВА\nтри\n"); // unstaged M
        await File.WriteAllTextAsync(Path.Combine(_repo, "new.txt"), "новый\n");        // untracked
        await File.WriteAllTextAsync(Path.Combine(_repo, "b.txt"), "b2\n");
        await _git.StageAsync(null, _repo, "b.txt");                                     // staged M

        var st = await _git.StatusAsync(null, _repo);

        st.IsRepo.Should().BeTrue();
        st.Branch.Should().Be("main");
        st.Staged.Should().ContainSingle(f => f.Path == "b.txt" && f.Status == "M");
        st.Unstaged.Should().ContainSingle(f => f.Path == "a.txt" && f.Status == "M");
        st.Untracked.Should().ContainSingle(f => f.Path == "new.txt");
    }

    [Fact]
    public async Task Commit_Кириллица_И_Amend()
    {
        await File.WriteAllTextAsync(Path.Combine(_repo, "a.txt"), "правка\n");
        await _git.StageAllAsync(null, _repo);
        var sha1 = await _git.CommitAsync(null, _repo, "фикс: правка файла а");

        var log = await _git.LogAsync(null, _repo, 5);
        log[0].Subject.Should().Be("фикс: правка файла а");

        // Amend меняет сообщение последнего коммита, не плодя новый
        var sha2 = await _git.CommitAsync(null, _repo, "фикс: правка файла а (уточнено)", amend: true);
        var log2 = await _git.LogAsync(null, _repo, 5);
        log2[0].Subject.Should().Be("фикс: правка файла а (уточнено)");
        log2.Count.Should().Be(log.Count);
        sha2.Should().NotBe(sha1);
    }

    [Fact]
    public async Task StageHunk_Индексирует_Только_Патч()
    {
        await File.WriteAllTextAsync(Path.Combine(_repo, "a.txt"), "ноль\nдва\nтри\nчетыре\n");
        var diff = await _git.DiffFileAsync(null, _repo, "a.txt", staged: false);
        diff.Should().NotBeNull();

        await _git.StageHunkAsync(null, _repo, diff!);
        var st = await _git.StatusAsync(null, _repo);
        st.Staged.Should().ContainSingle(f => f.Path == "a.txt");

        // Обратно: unstage того же патча очищает индекс
        await _git.UnstageHunkAsync(null, _repo, diff!);
        var st2 = await _git.StatusAsync(null, _repo);
        st2.Staged.Should().BeEmpty();
        st2.Unstaged.Should().ContainSingle(f => f.Path == "a.txt");
    }

    [Fact]
    public async Task Stash_Push_List_Pop()
    {
        await File.WriteAllTextAsync(Path.Combine(_repo, "a.txt"), "отложим\n");
        await _git.StashPushAsync(null, _repo, "проба стэша");

        (await _git.StatusAsync(null, _repo)).Unstaged.Should().BeEmpty();
        var list = await _git.StashListAsync(null, _repo);
        list.Should().ContainSingle(s => s.Message.Contains("проба стэша"));

        await _git.StashPopAsync(null, _repo, 0);
        (await _git.StatusAsync(null, _repo)).Unstaged.Should().ContainSingle(f => f.Path == "a.txt");
        (await _git.StashListAsync(null, _repo)).Should().BeEmpty();
    }

    [Fact]
    public async Task Revert_Создаёт_Обратный_Коммит()
    {
        await File.WriteAllTextAsync(Path.Combine(_repo, "a.txt"), "плохая правка\n");
        await _git.StageAllAsync(null, _repo);
        var bad = await _git.CommitAsync(null, _repo, "плохой коммит");

        await _git.RevertCommitAsync(null, _repo, bad);

        var log = await _git.LogAsync(null, _repo, 5);
        log[0].Subject.Should().StartWith("Revert");
        (await File.ReadAllTextAsync(Path.Combine(_repo, "a.txt"))).Should().NotContain("плохая правка");
    }

    [Fact]
    public async Task Blame_Отдаёт_Автора_Каждой_Строки()
    {
        var blame = await _git.BlameAsync(null, _repo, "a.txt");
        blame.Should().NotBeEmpty();
        blame.Should().OnlyContain(l => l.Author == "Тест" && l.ShortSha.Length == 7);
        blame.Select(l => l.Content).Should().ContainInOrder("один", "два", "три");
    }

    [Fact]
    public async Task Branch_Create_Checkout_List()
    {
        await _git.CreateBranchAsync(null, _repo, "feature/test", from: null);
        var branches = await _git.BranchesAsync(null, _repo);
        branches.Should().Contain(b => b.Name == "feature/test" && b.Current);

        await _git.CheckoutAsync(null, _repo, "main");
        (await _git.StatusAsync(null, _repo)).Branch.Should().Be("main");
    }

    [Fact]
    public async Task CommitDetail_Файлы_И_Дифф_Файла()
    {
        await File.WriteAllTextAsync(Path.Combine(_repo, "a.txt"), "для деталей\n");
        await _git.StageAllAsync(null, _repo);
        var sha = await _git.CommitAsync(null, _repo, "детальный коммит\n\nтело описания");

        var detail = await _git.CommitDetailAsync(null, _repo, sha);
        detail.Should().NotBeNull();
        detail!.Subject.Should().Be("детальный коммит");
        detail.Body.Should().Contain("тело описания");
        detail.Files.Should().ContainSingle(f => f.Path == "a.txt" && f.Status == "M");

        var diff = await _git.CommitFileDiffAsync(null, _repo, sha, "a.txt");
        diff.Should().Contain("+для деталей");
    }

    [Fact]
    public async Task Discard_Возвращает_Файл_К_HEAD()
    {
        // Сравнение с нормализацией переводов строк: git на Windows восстанавливает CRLF (autocrlf)
        static string Norm(string s) => s.Replace("\r\n", "\n");
        var before = Norm(await File.ReadAllTextAsync(Path.Combine(_repo, "a.txt")));
        await File.WriteAllTextAsync(Path.Combine(_repo, "a.txt"), "мусор\n");
        await _git.DiscardAsync(null, _repo, "a.txt");
        Norm(await File.ReadAllTextAsync(Path.Combine(_repo, "a.txt"))).Should().Be(before);
    }

    [Fact]
    public async Task Push_Без_Remote_Даёт_Понятную_Ошибку()
    {
        var act = () => _git.PushAsync(null, _repo);
        await act.Should().ThrowAsync<GitCommandException>();
    }
}
