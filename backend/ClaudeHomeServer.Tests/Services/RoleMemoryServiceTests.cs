using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Память ролей: контекстная адресация, дедуп, триггер авто-summary, оптимистичная запись, миграция
public class RoleMemoryServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly RoleMemoryService _sut;

    public RoleMemoryServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rolemem_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _sut = new RoleMemoryService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            }).Build());
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private string MemPath(string roleId, string context) =>
        Path.Combine(_tempDir, "role-memory", roleId, context + ".md");

    private static Role MakeRole(string id = "r1") => new() { Id = id, Name = "Игорь" };

    // --- Контексты ---

    [Fact]
    public void ContextFor_ProjectSession_IsProjectId()
    {
        var s = new Session { ProjectId = "proj-1" };
        RoleMemoryService.ContextFor(s).Should().Be("proj-1");
    }

    [Fact]
    public void ContextFor_ProjectlessChat_IsChatsWithOwner()
    {
        var s = new Session { ProjectId = null, OwnerId = "user-7" };
        RoleMemoryService.ContextFor(s).Should().Be("chats-user-7");
    }

    [Fact]
    public void Contexts_AreIsolated()
    {
        _sut.Append("r1", "proj-1", ["факт проекта"]);
        _sut.Append("r1", "chats-u1", ["факт из чатов"]);

        _sut.Read("r1", "proj-1").Should().Contain("факт проекта").And.NotContain("факт из чатов");
        _sut.Read("r1", "chats-u1").Should().Contain("факт из чатов").And.NotContain("факт проекта");
    }

    // --- Append: дедуп ---

    [Fact]
    public void Append_DeduplicatesCaseInsensitive()
    {
        _sut.Append("r1", "p1", ["Проект на .NET 9"]);
        _sut.Append("r1", "p1", ["проект на .net 9", "Новый факт"]);

        var lines = _sut.Read("r1", "p1").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(2);
    }

    [Fact]
    public void Read_NoMemory_ReturnsEmpty()
    {
        _sut.Read("нет", "нигде").Should().BeEmpty();
    }

    // --- Маркеры [MEMORY] ---

    [Fact]
    public void ExtractMemoryMarkers_FindsFactsAnywhereInLine()
    {
        var text = "Обычный текст\n[MEMORY] Проект использует xunit\nещё текст [MEMORY]: юзер любит краткость\n[MEMORY]\n";
        var facts = RoleMemoryService.ExtractMemoryMarkers(text);
        facts.Should().BeEquivalentTo(["Проект использует xunit", "юзер любит краткость"]);
    }

    [Fact]
    public async Task ProcessTurn_AppendsMarkerFacts()
    {
        await _sut.ProcessTurnAsync(MakeRole(), "p1", "Ответ\n[MEMORY] важный факт", (_, _) => Task.FromResult<string?>(null));
        _sut.Read("r1", "p1").Should().Contain("важный факт");
    }

    // --- Авто-summary: триггер по росту ---

    [Fact]
    public async Task ProcessTurn_BelowGrowthThreshold_DoesNotSummarize()
    {
        _sut.Append("r1", "p1", Enumerable.Range(1, 5).Select(i => $"факт {i}"));
        var called = false;

        await _sut.ProcessTurnAsync(MakeRole(), "p1", "диалог",
            (_, _) => { called = true; return Task.FromResult<string?>("- сжато\n"); });

        called.Should().BeFalse();
    }

    [Fact]
    public async Task ProcessTurn_AboveGrowthThreshold_SummarizesAndResetsCounter()
    {
        _sut.Append("r1", "p1", Enumerable.Range(1, 20).Select(i => $"факт {i}"));
        var calls = 0;

        await _sut.ProcessTurnAsync(MakeRole(), "p1", "диалог",
            (_, _) => { calls++; return Task.FromResult<string?>("- всё сжато"); });

        calls.Should().Be(1);
        _sut.Read("r1", "p1").Should().Be("- всё сжато\n");

        // Счётчик сброшен: следующий ход без нового роста summary не зовёт
        await _sut.ProcessTurnAsync(MakeRole(), "p1", "диалог",
            (_, _) => { calls++; return Task.FromResult<string?>("- опять"); });
        calls.Should().Be(1);
    }

    [Fact]
    public async Task Summary_MemoryChangedDuringGeneration_ResultIsDropped()
    {
        _sut.Append("r1", "p1", Enumerable.Range(1, 20).Select(i => $"факт {i}"));

        await _sut.ProcessTurnAsync(MakeRole(), "p1", "диалог", (_, _) =>
        {
            // Пока «думает» claude — параллельный чат дописал факт
            _sut.Append("r1", "p1", ["параллельный факт"]);
            return Task.FromResult<string?>("- сжато (устарело)");
        });

        var mem = _sut.Read("r1", "p1");
        mem.Should().Contain("параллельный факт");        // не затёрло
        mem.Should().NotContain("сжато (устарело)");      // результат дропнут
    }

    // --- Overwrite / DeleteRole ---

    [Fact]
    public void Overwrite_ReplacesContent()
    {
        _sut.Append("r1", "p1", ["старое"]);
        _sut.Overwrite("r1", "p1", "- новое\n");
        _sut.Read("r1", "p1").Should().Be("- новое\n");
    }

    [Fact]
    public void DeleteRole_RemovesAllContexts()
    {
        _sut.Append("r1", "p1", ["a"]);
        _sut.Append("r1", "chats-u1", ["b"]);

        _sut.DeleteRole("r1");

        _sut.Read("r1", "p1").Should().BeEmpty();
        _sut.Read("r1", "chats-u1").Should().BeEmpty();
        Directory.Exists(Path.Combine(_tempDir, "role-memory", "r1")).Should().BeFalse();
    }

    // --- Миграция старого формата ---

    [Fact]
    public void MigrateLegacy_MovesFileToProjectContext()
    {
        var memDir = Path.Combine(_tempDir, "role-memory");
        Directory.CreateDirectory(memDir);
        File.WriteAllText(Path.Combine(memDir, "r1.md"), "- старая память\n");

        _sut.MigrateLegacy(new Dictionary<string, string> { ["r1"] = "proj-legacy" });

        _sut.Read("r1", "proj-legacy").Should().Be("- старая память\n");
        File.Exists(Path.Combine(memDir, "r1.md")).Should().BeFalse();
    }

    [Fact]
    public void MigrateLegacy_TargetExists_KeepsBoth()
    {
        var memDir = Path.Combine(_tempDir, "role-memory");
        Directory.CreateDirectory(memDir);
        File.WriteAllText(Path.Combine(memDir, "r1.md"), "- легаси\n");
        _sut.Append("r1", "proj-1", ["уже мигрировано"]);

        _sut.MigrateLegacy(new Dictionary<string, string> { ["r1"] = "proj-1" });

        _sut.Read("r1", "proj-1").Should().Contain("уже мигрировано").And.NotContain("легаси");
    }
}
