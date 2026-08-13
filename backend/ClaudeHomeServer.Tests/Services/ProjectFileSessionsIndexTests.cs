using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services;

// ProjectFileSessionsIndex — обратный индекс «файл → какие ещё чаты его меняли» с кешем
// per-чат по LastWriteUtc истории. Хост — TestWebApplicationFactory: SessionManager
// собран полностью (LLM-адаптер подменён фейком, реального claude.exe не запускает),
// поэтому проще резолвить сервисы из контейнера, чем вручную собирать граф зависимостей.
public class ProjectFileSessionsIndexTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly ProjectManager _projects;
    private readonly SessionManager _sessions;
    private readonly ChatHistoryService _history;
    private readonly ProjectFileSessionsIndex _sut;
    private readonly string _tempDir;
    // history.json чата лежит по фиксированному шаблону {DataPath-папка}/sessions/{id}/history.json
    // (ChatHistoryService.GetPath — private); TestWebApplicationFactory кладёт DataPath прямо в TempDir
    private readonly string _historiesDir;

    public ProjectFileSessionsIndexTests(TestWebApplicationFactory factory)
    {
        _projects = factory.Services.GetRequiredService<ProjectManager>();
        _sessions = factory.Services.GetRequiredService<SessionManager>();
        _history = factory.Services.GetRequiredService<ChatHistoryService>();
        _sut = factory.Services.GetRequiredService<ProjectFileSessionsIndex>();
        _tempDir = Path.Combine(factory.TempDir, "pfsi_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _historiesDir = Path.Combine(factory.TempDir, "sessions");
    }

    private Project MakeProject(string name) =>
        _projects.Create(name, Directory.CreateDirectory(Path.Combine(_tempDir, name)).FullName,
            "owner-" + Guid.NewGuid().ToString("N")[..8], "tester");

    private Task<Session> MakeSessionAsync(string projectId, string claudeSessionId, string? name = null) =>
        _sessions.CreateAsync(projectId, ClaudeMode.Auto, resumeSessionId: claudeSessionId, name: name);

    [Fact]
    public async Task GetForProjectAsync_НетСовпадающихПутей_ReturnsEmpty()
    {
        var project = MakeProject("empty");
        var session = await MakeSessionAsync(project.Id, "pfsi-empty-1");
        await _history.SaveAsync(session.ClaudeSessionId!, [new StoredFileChangedMessage("src/a.ts", 1, 0)]);

        var result = await _sut.GetForProjectAsync(project.Id, ["src/other.ts"]);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetForProjectAsync_НесколькоЧатовМенялиОдинФайл_ВозвращаетОбоих()
    {
        var project = MakeProject("multi");
        var s1 = await MakeSessionAsync(project.Id, "pfsi-multi-1", "Первый чат");
        var s2 = await MakeSessionAsync(project.Id, "pfsi-multi-2", "Второй чат");
        await _history.SaveAsync(s1.ClaudeSessionId!, [new StoredFileChangedMessage("src/shared.ts", 1, 0)]);
        await _history.SaveAsync(s2.ClaudeSessionId!, [new StoredFileChangedMessage("src/shared.ts", 2, 1)]);

        var result = await _sut.GetForProjectAsync(project.Id, ["src/shared.ts"]);

        result.Should().ContainKey("src/shared.ts");
        result["src/shared.ts"].Select(r => r.SessionId).Should().BeEquivalentTo([s1.Id, s2.Id]);
        result["src/shared.ts"].Select(r => r.Name).Should().BeEquivalentTo(["Первый чат", "Второй чат"]);
    }

    // Ходы worktree-чата (WorktreePath != null) не участвуют в индексе — их правки живут
    // в другом дереве, а не в rootPath проекта
    [Fact]
    public async Task GetForProjectAsync_WorktreeЧат_Игнорируется()
    {
        var project = MakeProject("worktree");
        var s = await MakeSessionAsync(project.Id, "pfsi-wt-1");
        await _history.SaveAsync(s.ClaudeSessionId!, [new StoredFileChangedMessage("src/wt.ts", 1, 0)]);
        _sessions.GetById(s.Id)!.WorktreePath = Path.Combine(_tempDir, "wt");

        var result = await _sut.GetForProjectAsync(project.Id, ["src/wt.ts"]);

        result.Should().BeEmpty();
    }

    // Нетронутая лента (LastWriteUtc истории не сдвинулся) не перечитывается: подменяем
    // содержимое файла на диске, но насильно возвращаем прежний LastWriteUtc — если бы
    // индекс перечитал файл, новый путь оказался бы в результате
    [Fact]
    public async Task GetForProjectAsync_НетронутаяИстория_ДоверяетКешу()
    {
        var project = MakeProject("stale");
        var s = await MakeSessionAsync(project.Id, "pfsi-stale-1");
        await _history.SaveAsync(s.ClaudeSessionId!, [new StoredFileChangedMessage("src/old.ts", 1, 0)]);

        // Первый вызов — строит кеш
        (await _sut.GetForProjectAsync(project.Id, ["src/old.ts"])).Should().ContainKey("src/old.ts");

        var lastWrite = _history.LastWriteUtc(s.ClaudeSessionId!);
        await _history.SaveAsync(s.ClaudeSessionId!, [new StoredFileChangedMessage("src/new.ts", 1, 0)]);
        // Возвращаем прежний LastWriteUtc — с точки зрения индекса лента «не менялась»
        File.SetLastWriteTimeUtc(HistoryPath(s.ClaudeSessionId!), lastWrite!.Value);

        var result = await _sut.GetForProjectAsync(project.Id, ["src/old.ts", "src/new.ts"]);

        result.Should().ContainKey("src/old.ts", "кеш не инвалидировался — старый путь виден");
        result.Should().NotContainKey("src/new.ts", "лента не перечитана — новый путь ещё не в кеше");
    }

    // Сдвиг LastWriteUtc одной ленты пересчитывает ТОЛЬКО её — вторая (тоже подменённая
    // на диске, но с насильно возвращённым старым LastWriteUtc) остаётся на прежнем кеше
    [Fact]
    public async Task GetForProjectAsync_СдвигLastWriteUtc_ПересчитываетТолькоЕё()
    {
        var project = MakeProject("shift");
        var s1 = await MakeSessionAsync(project.Id, "pfsi-shift-1");
        var s2 = await MakeSessionAsync(project.Id, "pfsi-shift-2");
        await _history.SaveAsync(s1.ClaudeSessionId!, [new StoredFileChangedMessage("src/one-old.ts", 1, 0)]);
        await _history.SaveAsync(s2.ClaudeSessionId!, [new StoredFileChangedMessage("src/two-old.ts", 1, 0)]);
        await _sut.GetForProjectAsync(project.Id, ["src/one-old.ts", "src/two-old.ts"]); // строим кеш обоих

        var s2LastWrite = _history.LastWriteUtc(s2.ClaudeSessionId!);

        // s1: реальное обновление — LastWriteUtc реально сдвигается
        await _history.SaveAsync(s1.ClaudeSessionId!, [new StoredFileChangedMessage("src/one-new.ts", 1, 0)]);
        // s2: подменяем содержимое, но откатываем LastWriteUtc — «лента не менялась»
        await _history.SaveAsync(s2.ClaudeSessionId!, [new StoredFileChangedMessage("src/two-new.ts", 1, 0)]);
        File.SetLastWriteTimeUtc(HistoryPath(s2.ClaudeSessionId!), s2LastWrite!.Value);

        var result = await _sut.GetForProjectAsync(project.Id,
            ["src/one-old.ts", "src/one-new.ts", "src/two-old.ts", "src/two-new.ts"]);

        result.Should().NotContainKey("src/one-old.ts", "s1 реально перечитан — старый путь ушёл");
        result.Should().ContainKey("src/one-new.ts", "s1 реально перечитан — новый путь появился");
        result.Should().ContainKey("src/two-old.ts", "s2 не перечитан — старый путь всё ещё в кеше");
        result.Should().NotContainKey("src/two-new.ts", "s2 не перечитан — новый путь не увиден");
    }

    // Кеш общий на все проекты процесса (ключ — ClaudeSessionId): чистка «мёртвых» записей
    // при вызове для ПРОЕКТА B не должна выбивать кеш чата ПРОЕКТА A — иначе с двумя
    // открытыми проектами лента любого чата перечитывалась бы с диска на каждый запрос
    // соседа. Ловим тем же приёмом, что «нетронутая история»: подменяем содержимое
    // чата проекта A на диске, но откатываем LastWriteUtc — если бы вызов для проекта B
    // выбил его кеш, следующий вызов для A перечитал бы файл и увидел подмену
    [Fact]
    public async Task GetForProjectAsync_ВызовДругогоПроекта_НеВыбиваетКешПервого()
    {
        var p1 = MakeProject("two-proj-a");
        var p2 = MakeProject("two-proj-b");
        var s1 = await MakeSessionAsync(p1.Id, "pfsi-two-a-1");
        var s2 = await MakeSessionAsync(p2.Id, "pfsi-two-b-1");
        await _history.SaveAsync(s1.ClaudeSessionId!, [new StoredFileChangedMessage("src/a-old.ts", 1, 0)]);
        await _history.SaveAsync(s2.ClaudeSessionId!, [new StoredFileChangedMessage("src/b.ts", 1, 0)]);

        // Строим кеш проекта A
        (await _sut.GetForProjectAsync(p1.Id, ["src/a-old.ts"])).Should().ContainKey("src/a-old.ts");
        var s1LastWrite = _history.LastWriteUtc(s1.ClaudeSessionId!);

        // Подменяем содержимое чата A на диске, но откатываем LastWriteUtc — «лента не менялась»
        await _history.SaveAsync(s1.ClaudeSessionId!, [new StoredFileChangedMessage("src/a-new.ts", 1, 0)]);
        File.SetLastWriteTimeUtc(HistoryPath(s1.ClaudeSessionId!), s1LastWrite!.Value);

        // Вызов для ДРУГОГО проекта — раньше чистка кеша по чатам текущего projectId
        // выбивала бы запись чата A (его нет в проекте B)
        await _sut.GetForProjectAsync(p2.Id, ["src/b.ts"]);

        var result = await _sut.GetForProjectAsync(p1.Id, ["src/a-old.ts", "src/a-new.ts"]);

        result.Should().ContainKey("src/a-old.ts", "кеш чата A пережил вызов для проекта B — лента не перечитана");
        result.Should().NotContainKey("src/a-new.ts", "лента A не перечитана — новый путь не должен быть виден");
    }

    [Fact]
    public async Task GetForProjectAsync_УдалённыйЧат_Выпадает()
    {
        var project = MakeProject("deleted");
        var s = await MakeSessionAsync(project.Id, "pfsi-del-1");
        await _history.SaveAsync(s.ClaudeSessionId!, [new StoredFileChangedMessage("src/gone.ts", 1, 0)]);
        (await _sut.GetForProjectAsync(project.Id, ["src/gone.ts"])).Should().ContainKey("src/gone.ts");

        await _sessions.DeleteAsync(s.Id);

        var result = await _sut.GetForProjectAsync(project.Id, ["src/gone.ts"]);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetForProjectAsync_ПараллельныйХолодныйВызов_НеПадает()
    {
        var project = MakeProject("parallel");
        var s1 = await MakeSessionAsync(project.Id, "pfsi-par-1");
        var s2 = await MakeSessionAsync(project.Id, "pfsi-par-2");
        await _history.SaveAsync(s1.ClaudeSessionId!, [new StoredFileChangedMessage("src/p1.ts", 1, 0)]);
        await _history.SaveAsync(s2.ClaudeSessionId!, [new StoredFileChangedMessage("src/p2.ts", 1, 0)]);

        var calls = Enumerable.Range(0, 8)
            .Select(_ => _sut.GetForProjectAsync(project.Id, ["src/p1.ts", "src/p2.ts"]));

        var act = () => Task.WhenAll(calls);

        await act.Should().NotThrowAsync();
        var results = await act();
        results.Should().AllSatisfy(r =>
        {
            r.Should().ContainKey("src/p1.ts");
            r.Should().ContainKey("src/p2.ts");
        });
    }

    private string HistoryPath(string claudeSessionId) =>
        Path.Combine(_historiesDir, claudeSessionId, "history.json");
}
