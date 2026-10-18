using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Plugins;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PluginBridge = ClaudeHomeServer.Services.Plugins.PluginBridge;

namespace ClaudeHomeServer.Tests.Plugins;

// Обработчик notes:read (ADR-031 §2.4, §3.4, шаг 3.5): операции notes.search / notes.read.
// Тесты (критерий):
//   - изоляция: владелец A не видит заметки владельца B;
//   - чужой id / обход «..» / чужой проект → not_found (не denied);
//   - лимит и truncated;
//   - сквозной через мост: с правом → данные, без права → denied.
// Мутации (критерий): убрать фильтр по владельцу → тест изоляции красный;
// убрать усечение → тест лимита красный.
public class NotesPluginCapabilityTests : IDisposable
{
    private const string OwnerA = "owner-a";
    private const string OwnerB = "owner-b";

    private readonly string _dir;
    private readonly NotesService _notes;
    private readonly NotesPluginCapability _sut;

    public NotesPluginCapabilityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ccs_notes_cap_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
            })
            .Build();
        _notes = new NotesService(new StubProjectManager(), config, NullLogger<NotesService>.Instance);
        _sut = new NotesPluginCapability(_notes);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    // --- Хелперы ---

    private void WritePersonalNote(string owner, string name, string content)
    {
        var vault = Path.Combine(_dir, "notes", owner);
        Directory.CreateDirectory(vault);
        File.WriteAllText(Path.Combine(vault, name + ".md"), content);
    }

    private static PluginCallContext Ctx(string owner, string? project = null) =>
        new(owner, "local-a/pkg", project, null);

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    // --- 1. Изоляция: владелец A не видит заметки владельца B ---
    // Мутация: убрать фильтр по владельцу (OwnerId) → этот тест красный
    // (A увидит заметки B, потому что id личного vault не несёт владельца).

    [Fact]
    public async Task Изоляция_ВладелецA_НеВидитЗаметкиB()
    {
        WritePersonalNote(OwnerA, "a-note", "# A\n");
        WritePersonalNote(OwnerB, "b-note", "# B\n");

        var result = await _sut.HandleAsync(Ctx(OwnerA), "notes.search", Args("{}"),
            [PluginPermission.NotesRead], CancellationToken.None);

        result.Ok.Should().BeTrue();
        var json = JsonSerializer.SerializeToElement(result.Data!);
        var notes = json.GetProperty("notes");
        notes.GetArrayLength().Should().Be(1, "A видит только свою заметку");
        notes[0].GetProperty("title").GetString().Should().Be("a-note");
    }

    // --- 2. Чужой id → not_found (не denied, чтобы не подтверждать существование) ---
    // Владелец B читает id заметки владельца A: в модели B её нет → not_found.

    [Fact]
    public async Task ЧужойId_ОтдаётNotFound()
    {
        WritePersonalNote(OwnerA, "secret", "# Secret\n");
        var id = _notes.GetSummaries(OwnerA, null, null).Single(n => n.Title == "secret").Id;

        var result = await _sut.HandleAsync(Ctx(OwnerB), "notes.read", Args($"{{\"id\":\"{id}\"}}"),
            [PluginPermission.NotesRead], CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error!.Code.Should().Be("not_found");
    }

    // --- 3. Обход «..» → not_found ---
    // id кодирует «personal|../escape.md»: путь с «..» выходит за vault, но в модели
    // (построенной из нормализованных путей) его нет → not_found.

    [Fact]
    public async Task ОбходЧерезТочкуТочку_ОтдаётNotFound()
    {
        WritePersonalNote(OwnerA, "real", "# Real\n");
        // Кодируем «personal|../escape.md» тем же способом, что NotesService.EncodeId.
        var raw = System.Text.Encoding.UTF8.GetBytes("personal|../escape.md");
        var id = Convert.ToBase64String(raw).Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var result = await _sut.HandleAsync(Ctx(OwnerA), "notes.read", Args($"{{\"id\":\"{id}\"}}"),
            [PluginPermission.NotesRead], CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error!.Code.Should().Be("not_found");
    }

    // --- 4. Проектная заметка вне проекта слота → not_found ---
    // Заметка проекта X, но слот — проект Y (или нет слота) → не в границе → not_found.

    [Fact]
    public async Task ПроектнаяЗаметка_ВнеПроектаСлота_ОтдаётNotFound()
    {
        // Проект X (не слот) — заметка в его notes/.
        var projectX = new Project { Id = "proj-x", Name = "X", RootPath = Path.Combine(_dir, "proj-x"), OwnerId = OwnerA };
        var projectY = new Project { Id = "proj-y", Name = "Y", RootPath = Path.Combine(_dir, "proj-y"), OwnerId = OwnerA };
        var notes = new NotesService(
            new StubProjectManager(projectX, projectY),
            NewConfig(), NullLogger<NotesService>.Instance);
        var sut = new NotesPluginCapability(notes);

        var projXNotes = Path.Combine(_dir, "proj-x", "notes");
        Directory.CreateDirectory(projXNotes);
        File.WriteAllText(Path.Combine(projXNotes, "x-note.md"), "# X\n");

        // Слот — проект Y: заметка проекта X вне границы → not_found.
        var id = notes.GetSummaries(OwnerA, "proj-x", null).Single(n => n.Title == "x-note").Id;
        var result = await sut.HandleAsync(Ctx(OwnerA, "proj-y"), "notes.read", Args($"{{\"id\":\"{id}\"}}"),
            [PluginPermission.NotesRead], CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error!.Code.Should().Be("not_found");
    }

    // --- 5. Лимит: >100 записей → усечение, truncated=true ---
    // Мутация: убрать усечение (всегда вернуть все) → этот тест красный.

    [Fact]
    public async Task ЛимитЗаписей_Больше100_УсекаетСTruncated()
    {
        for (var i = 0; i < 150; i++)
            WritePersonalNote(OwnerA, $"note-{i:D3}", $"# {i}\n");

        var result = await _sut.HandleAsync(Ctx(OwnerA), "notes.search", Args("{}"),
            [PluginPermission.NotesRead], CancellationToken.None);

        result.Ok.Should().BeTrue();
        var json = JsonSerializer.SerializeToElement(result.Data!);
        json.GetProperty("notes").GetArrayLength().Should().Be(100, "потолок 100 записей");
        json.GetProperty("truncated").GetBoolean().Should().BeTrue();
    }

    // --- 6. Лимит: объём >200 KiB → усечение, truncated=true ---

    [Fact]
    public async Task ЛимитОбъёма_Больше200Kb_УсекаетСTruncated()
    {
        // Одна заметка с телом 300 KiB → больше лимита 200 KiB → усечение.
        var big = new string('x', 300 * 1024);
        WritePersonalNote(OwnerA, "big", $"# Big\n{big}\n");

        _sut.MaxTotalBytes = 200 * 1024;
        var result = await _sut.HandleAsync(Ctx(OwnerA), "notes.read",
            Args($"{{\"id\":\"{_notes.GetSummaries(OwnerA, null, null).Single(n => n.Title == "big").Id}\"}}"),
            [PluginPermission.NotesRead], CancellationToken.None);

        result.Ok.Should().BeTrue();
        var json = JsonSerializer.SerializeToElement(result.Data!);
        json.GetProperty("truncated").GetBoolean().Should().BeTrue();
        json.GetProperty("content").GetString()!.Length.Should().BeLessOrEqualTo(200 * 1024);
    }

    // --- 7. Сквозной через мост: с правом → данные, без права → denied ---

    [Fact]
    public async Task СквознойЧерезМост_СПравом_ОтдаётДанные()
    {
        WritePersonalNote(OwnerA, "target", "# Target\n");
        var id = _notes.GetSummaries(OwnerA, null, null).Single(n => n.Title == "target").Id;

        var bridge = new PluginBridge([_sut]);
        var result = await bridge.DispatchAsync(Ctx(OwnerA), "notes.read",
            Args($"{{\"id\":\"{id}\"}}"), [PluginPermission.NotesRead], CancellationToken.None);

        result.Ok.Should().BeTrue("право notes:read выдано → обработчик возвращает заметку");
        var json = JsonSerializer.SerializeToElement(result.Data!);
        json.GetProperty("title").GetString().Should().Be("target");
    }

    [Fact]
    public async Task СквознойЧерезМост_БезПрава_ОтдаётDenied()
    {
        WritePersonalNote(OwnerA, "target", "# Target\n");
        var id = _notes.GetSummaries(OwnerA, null, null).Single(n => n.Title == "target").Id;

        var bridge = new PluginBridge([_sut]);
        // granted пуст — право notes:read не выдано → мост отбивает denied.
        var result = await bridge.DispatchAsync(Ctx(OwnerA), "notes.read",
            Args($"{{\"id\":\"{id}\"}}"), [], CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error!.Code.Should().Be("denied");
    }

    // --- Тестовые заглушки ---

    private static IConfiguration NewConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(Path.GetTempPath(), "ccs_notes_cap_" + Guid.NewGuid().ToString("N") + "/projects.json"),
            })
            .Build();

    // Stub IProjectManager: фиксированный набор проектов (по умолчанию пуст).
    private sealed class StubProjectManager : IProjectManager
    {
        private readonly List<Project> _projects;
        public StubProjectManager(params Project[] projects) => _projects = projects.ToList();
        public Project? GetById(string id) => _projects.FirstOrDefault(p => p.Id == id);
        public IReadOnlyCollection<Project> GetByOwner(string userId) => _projects.Where(p => p.OwnerId == userId).ToList();
        public IReadOnlyCollection<Project> GetAll() => _projects;
        public IReadOnlyCollection<Project> GetByRootPath(string rootPath) => _projects.Where(p => p.RootPath == rootPath).ToList();
    }
}
