using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Git;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services;

// CommitAttributionService — детект коммита по сдвигу HEAD на настоящем git CLI:
// после коммита пути чата помечаются «зафиксировано» (Session.CommittedFilePaths)
// и уходят из атрибуции (ProjectFileSessionsIndex), причём коммит может идти как
// через продукт (GitService.CommitAsync), так и мимо него (сырой git commit).
// Хост — TestWebApplicationFactory (как в ProjectFileSessionsIndexTests).
[Trait("Category", "Slow")]
public class CommitAttributionServiceTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly ProjectManager _projects;
    private readonly SessionManager _sessions;
    private readonly ChatHistoryService _history;
    private readonly ProjectFileSessionsIndex _index;
    private readonly GitService _git;
    private readonly CommitAttributionService _sut;
    private readonly string _tempDir;

    public CommitAttributionServiceTests(TestWebApplicationFactory factory)
    {
        _projects = factory.Services.GetRequiredService<ProjectManager>();
        _sessions = factory.Services.GetRequiredService<SessionManager>();
        _history = factory.Services.GetRequiredService<ChatHistoryService>();
        _index = factory.Services.GetRequiredService<ProjectFileSessionsIndex>();
        _git = factory.Services.GetRequiredService<GitService>();
        _sut = factory.Services.GetRequiredService<CommitAttributionService>();
        _tempDir = Path.Combine(factory.TempDir, "cattr_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    // Репозиторий с начальным коммитом (user.* — per-repo, CI без глобального конфига)
    private async Task<string> MakeRepoAsync(string name)
    {
        var root = Directory.CreateDirectory(Path.Combine(_tempDir, name)).FullName;
        await _git.InitAsync(null, root);
        await _git.RunAsync(null, root, ["config", "user.email", "test@test"]);
        await _git.RunAsync(null, root, ["config", "user.name", "Тест"]);
        await File.WriteAllTextAsync(Path.Combine(root, "a.ts"), "v1\n");
        await File.WriteAllTextAsync(Path.Combine(root, "b.ts"), "v1\n");
        await _git.StageAllAsync(null, root);
        await _git.CommitAsync(null, root, "начальный коммит");
        return root;
    }

    // sha HEAD дерева — в бою его несёт branch.oid статуса (GitStatusDto.HeadSha)
    private async Task<string> HeadAsync(string root) =>
        (await _git.RunAsync(null, root, ["rev-parse", "HEAD"])).Stdout.Trim();

    // Наблюдение статуса, как его делает GitController.Status: HEAD берётся из статуса
    private async Task ObserveAsync(CommitAttributionService svc, string root) =>
        await svc.OnStatusRequestAsync(null, root, await HeadAsync(root));

    // Частичный коммит через продуктовый путь (панель: stage только a.ts → commit):
    // a.ts помечается и уходит из атрибуции, b.ts остаётся файлом чата
    [Fact]
    public async Task ЧастичныйКоммит_ПомечаетТолькоЕгоПути()
    {
        var root = await MakeRepoAsync("partial");
        var project = _projects.Create("Partial", root, "owner-cattr-1", "tester");
        var session = await _sessions.CreateAsync(project.Id, ClaudeMode.Auto, resumeSessionId: "cattr-part-1");
        await _history.SaveAsync(session.ClaudeSessionId!,
            [new StoredFileChangedMessage("a.ts", 1, 0), new StoredFileChangedMessage("b.ts", 1, 0)]);

        // Первое наблюдение — только запоминает HEAD, ничего не помечает
        await ObserveAsync(_sut, root);
        session.CommittedFilePaths.Should().BeEmpty();

        // Правки обоих файлов, но фиксируется ТОЛЬКО a.ts (staged)
        await File.WriteAllTextAsync(Path.Combine(root, "a.ts"), "v2\n");
        await File.WriteAllTextAsync(Path.Combine(root, "b.ts"), "v2\n");
        await _git.StageAsync(null, root, "a.ts");
        await _git.CommitAsync(null, root, "только a.ts");

        // Следующий запрос статуса видит сдвиг HEAD и помечает пути коммита
        await ObserveAsync(_sut, root);

        session.CommittedFilePaths.Should().BeEquivalentTo(["a.ts"]);
        var attributed = await _index.GetForProjectAsync(project.Id, ["a.ts", "b.ts"]);
        attributed.Should().NotContainKey("a.ts", "правки a.ts зафиксированы — чат за ним не числится");
        attributed.Should().ContainKey("b.ts", "b.ts остался незафиксированным файлом чата");
    }

    // Коммит МИМО продукта (сырой git commit — Bash в чате, терминал): детект по сдвигу
    // HEAD обязан отработать так же. Заодно: файл коммита, которого нет в множестве чата,
    // чату не помечается (пересечение, а не все пути коммита)
    [Fact]
    public async Task КоммитМимоПродукта_ПомечаетПоПересечению()
    {
        var root = await MakeRepoAsync("raw-commit");
        var project = _projects.Create("RawCommit", root, "owner-cattr-2", "tester");
        var session = await _sessions.CreateAsync(project.Id, ClaudeMode.Auto, resumeSessionId: "cattr-raw-1");
        await _history.SaveAsync(session.ClaudeSessionId!, [new StoredFileChangedMessage("a.ts", 1, 0)]);

        await ObserveAsync(_sut, root);

        // Коммит сырым git: правка a.ts (файл чата) + новый c.ts (чат его не менял)
        await File.WriteAllTextAsync(Path.Combine(root, "a.ts"), "v2\n");
        await File.WriteAllTextAsync(Path.Combine(root, "c.ts"), "v1\n");
        await _git.RunAsync(null, root, ["add", "-A"]);
        await _git.RunAsync(null, root, ["commit", "-m", "мимо продукта"]);

        await ObserveAsync(_sut, root);

        session.CommittedFilePaths.Should().BeEquivalentTo(["a.ts"],
            "c.ts чат не менял — пометка идёт по пересечению с множеством чата");
    }

    // Коммит в linked git worktree: его корень не равен Project.RootPath ни одного проекта —
    // базы чатов основного дерева не трогаются
    [Fact]
    public async Task КоммитВWorktree_НеТрогаетЧатыКорня()
    {
        var root = await MakeRepoAsync("wt-main");
        var project = _projects.Create("WtMain", root, "owner-cattr-3", "tester");
        var session = await _sessions.CreateAsync(project.Id, ClaudeMode.Auto, resumeSessionId: "cattr-wt-1");
        await _history.SaveAsync(session.ClaudeSessionId!, [new StoredFileChangedMessage("a.ts", 1, 0)]);

        var wtPath = Path.Combine(_tempDir, "wt-linked");
        await _git.WorktreeAddAsync(null, root, wtPath, "feature/wt-test");

        // Наблюдаем worktree, коммитим в нём файл чата — пометок у чатов корня быть не должно
        await ObserveAsync(_sut, wtPath);
        await File.WriteAllTextAsync(Path.Combine(wtPath, "a.ts"), "wt\n");
        await _git.RunAsync(null, wtPath, ["add", "-A"]);
        await _git.RunAsync(null, wtPath, ["commit", "-m", "коммит в worktree"]);
        await ObserveAsync(_sut, wtPath);

        session.CommittedFilePaths.Should().BeEmpty("корень worktree не резолвится в проект");
    }

    // Одна папка у РАЗНЫХ владельцев допустима (EnsureRootFree запрещает повтор только
    // внутри одного) — коммит в общем корне помечает чаты ОБОИХ проектов
    [Fact]
    public async Task ОбщийКорень_ПомечаютсяЧатыВсехПроектов()
    {
        var root = await MakeRepoAsync("shared-root");
        var p1 = _projects.Create("SharedA", root, "owner-cattr-4a", "tester");
        var p2 = _projects.Create("SharedB", root, "owner-cattr-4b", "tester");
        var s1 = await _sessions.CreateAsync(p1.Id, ClaudeMode.Auto, resumeSessionId: "cattr-shared-1");
        var s2 = await _sessions.CreateAsync(p2.Id, ClaudeMode.Auto, resumeSessionId: "cattr-shared-2");
        await _history.SaveAsync(s1.ClaudeSessionId!, [new StoredFileChangedMessage("a.ts", 1, 0)]);
        await _history.SaveAsync(s2.ClaudeSessionId!, [new StoredFileChangedMessage("a.ts", 1, 0)]);

        await ObserveAsync(_sut, root);
        await File.WriteAllTextAsync(Path.Combine(root, "a.ts"), "v2\n");
        await _git.RunAsync(null, root, ["add", "-A"]);
        await _git.RunAsync(null, root, ["commit", "-m", "общий корень"]);
        await ObserveAsync(_sut, root);

        s1.CommittedFilePaths.Should().BeEquivalentTo(["a.ts"]);
        s2.CommittedFilePaths.Should().BeEquivalentTo(["a.ts"], "у общего корня помечаются оба проекта");
    }

    // Подмена обработки диапазона: имитирует транзиентный сбой (таймаут git и т.п.)
    private sealed class FailingMarkService(
        GitService git, SessionManager sessions, ProjectManager projects, ProjectFileSessionsIndex index)
        : CommitAttributionService(git, sessions, projects, index)
    {
        public bool Fail;
        protected override Task MarkRangeAsync(string? ownerId, string root, string oldHead, string newHead)
        {
            if (Fail) throw new InvalidOperationException("искусственный сбой обработки диапазона");
            return base.MarkRangeAsync(ownerId, root, oldHead, newHead);
        }
    }

    // Сбой обработки диапазона НЕ съедает сдвиг HEAD: следующий статус повторяет попытку
    // и допомечает пути (иначе упавший/отменённый вызов терял бы коммит навсегда)
    [Fact]
    public async Task СбойОбработки_НеСъедаетСдвиг_СледующийСтатусПовторяет()
    {
        var root = await MakeRepoAsync("retry");
        var project = _projects.Create("Retry", root, "owner-cattr-5", "tester");
        var session = await _sessions.CreateAsync(project.Id, ClaudeMode.Auto, resumeSessionId: "cattr-retry-1");
        await _history.SaveAsync(session.ClaudeSessionId!, [new StoredFileChangedMessage("a.ts", 1, 0)]);
        var svc = new FailingMarkService(_git, _sessions, _projects, _index);

        await ObserveAsync(svc, root);
        await File.WriteAllTextAsync(Path.Combine(root, "a.ts"), "v2\n");
        await _git.RunAsync(null, root, ["add", "-A"]);
        await _git.RunAsync(null, root, ["commit", "-m", "коммит перед сбоем"]);

        // Сбой при обработке сдвига — пометок нет, но и сдвиг не потерян
        svc.Fail = true;
        await ObserveAsync(svc, root);
        session.CommittedFilePaths.Should().BeEmpty("обработка упала — пометки не поставлены");

        // Следующий статус (без сбоя) видит тот же диапазон и допомечает
        svc.Fail = false;
        await ObserveAsync(svc, root);
        session.CommittedFilePaths.Should().BeEquivalentTo(["a.ts"], "повтор после сбоя пометил пути");
    }
}
