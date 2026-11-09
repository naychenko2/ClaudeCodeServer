using System.Net;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Tests.Services;

// S2c (ADR-035, решения 8 и 13; ADR-036, решение 6): страницы и карточки источников —
// в отдельный датасет «{username}:wiki» с метаданными wiki_section; в :notes вики не
// попадает; проекции _* не индексируются нигде. Удаление пользователя уносит запись
// и каталог data/wiki/{ownerId} каскадом участников (Dify-датасеты — общим проходом
// по префиксу «{username}:», как у :notes).
public class WikiDifySyncTests : IDisposable
{
    private readonly string _dir;
    private readonly IConfiguration _config;
    private readonly FakeDify _dify = new();
    private readonly UserStore _users;
    private readonly ProjectManager _projects;
    private readonly KnowledgeService _knowledge;
    private readonly WorkspaceKnowledgeStore _wkStore;
    private readonly string _userId;
    private readonly string _username;
    private readonly string _projectId;
    private readonly NotesService _notes;
    private readonly WikiKeeper _keeper;
    private readonly NotesKnowledgeService _sut;

    public WikiDifySyncTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_dify_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        _users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _userId = _users.GetFirst()!.Id;
        _username = _users.GetFirst()!.Username;
        _projects = new ProjectManager(_config, _users, new AppSettingsService(_config));
        var projectDir = Path.Combine(_dir, "proj");
        Directory.CreateDirectory(projectDir);
        _projectId = _projects.Create("docs", projectDir, _userId, _username).Id;
        _wkStore = new WorkspaceKnowledgeStore(_config);
        // Dify — подменённый на записи хендлер: тот же настоящий KnowledgeService,
        // без сети. Через него идут и синк, и каскад — форма запросов проверяется как она есть.
        _knowledge = new KnowledgeService(new StubHttpClientFactory(_dify),
            Options.Create(new DifyOptions { ApiUrl = "http://dify.test/v1", ApiKey = "k" }), _wkStore);
        _notes = new NotesService(_projects, _config, NullLogger<NotesService>.Instance, null, flags: new WikiFlagOn());
        _keeper = new WikiKeeper(_notes, _config);
        _sut = new NotesKnowledgeService(_knowledge, _notes, _users, _config, NullLogger<NotesKnowledgeService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private async Task SeedWikiAsync()
    {
        await _keeper.EnsureInitializedAsync(_userId);
        var sec = "projects/" + _projectId;
        (await _keeper.WritePageAsync(_userId, sec, $"Проекты/{_projectId}/Знание.md",
            "---\ntype: topic\n---\n# Знание\n\nТекст страницы.\n"))
            .Status.Should().Be(WikiOpStatus.Applied);
        (await _keeper.WritePageAsync(_userId, sec, $"Проекты/{_projectId}/Источники/Карточка.md",
            "---\ntype: source\norigin: note\nref: note:abc\nhash: deadbeef\ncaptured: 2026-10-30T00:00:00Z\n---\n# Карточка\n\n> цитата\n"))
            .Status.Should().Be(WikiOpStatus.Applied);
    }

    private void SeedNote()
    {
        var dir = Path.Combine(_dir, "notes", _userId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Заметка.md"), "личная заметка", new UTF8Encoding(false));
    }

    // ─── 1. Синк: :wiki с wiki_section; в :notes — ни одной страницы вики (c3) ──

    [Fact]
    public async Task Синк_СтраницыИКарточкаВWikiСWikiSection_ВNotesНиОднойСтраницыВики_ПроекцииНигде()
    {
        await SeedWikiAsync();
        SeedNote();

        await _sut.SyncAllAsync(_userId);

        var notesDs = _dify.DatasetIdOf(_username + ":notes");
        var wikiDs = _dify.DatasetIdOf(_username + ":wiki");
        notesDs.Should().NotBeNull("датасет заметок создаётся штатно");
        wikiDs.Should().NotBeNull("датасет вики создаётся тем же механизмом");

        // Страница и карточка источника — в :wiki, обе с метаданной раздела (ключ из
        // WikiSections.Parse — тот же, что у видимости).
        var inWiki = _dify.IndexedDocs.Where(d => d.DatasetId == wikiDs).ToList();
        inWiki.Select(d => d.Name).Should().BeEquivalentTo("Знание", "Карточка");
        foreach (var d in inWiki) d.Meta.Should().ContainKey("wiki_section");
        foreach (var d in inWiki) d.Meta["wiki_section"].Should().Be("projects/" + _projectId);

        // :notes — только личная заметка: ни одной страницы вики в датасете заметок.
        _dify.IndexedDocs.Where(d => d.DatasetId == notesDs).Select(d => d.Name)
            .Should().BeEquivalentTo("Заметка");

        // Проекции (_schema/_index/_log) — ни в одном датасете (IsProjection / пропуск Scan).
        _dify.IndexedDocs.Select(d => d.Name).Should().NotContain(n => n.StartsWith("_"));
    }

    // ─── 2. Каскад: удаление пользователя уносит :wiki и data/wiki/{ownerId} (c4) ─

    [Fact]
    public async Task Каскад_УдалениеВладельцаУноситДатасетWikiИКаталогDataWiki()
    {
        await SeedWikiAsync();
        SeedNote();
        await _sut.SyncAllAsync(_userId);
        Directory.Exists(WikiPaths.Root(_dir, _userId)).Should().BeTrue();

        var cascade = new UserKnowledgeCascade(_knowledge, _wkStore, _projects, new PersonaDirectoryMock(),
            new IKnowledgeSyncParticipant[] { _sut }, NullLogger<UserKnowledgeCascade>.Instance);
        await cascade.CleanupAsync(_userId, _username);

        // Датасет :wiki назван шаблоном «{username}:wiki» — общий проход каскада по
        // префиксу имени удаляет его тем же шагом, что и :notes.
        var wikiDs = _dify.DatasetIdOf(_username + ":wiki");
        wikiDs.Should().NotBeNull();
        _dify.DeletedPaths.Should().Contain(p => p.EndsWith("/datasets/" + wikiDs));
        _dify.DeletedPaths.Should().Contain(p => p.EndsWith("/datasets/" + _dify.DatasetIdOf(_username + ":notes")));

        // Каталог вики и локальная запись индекса — за участником синка.
        Directory.Exists(WikiPaths.Root(_dir, _userId)).Should().BeFalse();
        _sut.HasIndex(_userId).Should().BeFalse();
        _sut.GetDatasetId(_userId).Should().BeNull();
    }

    // ─── Подменённый Dify ─────────────────────────────────────────────────────

    // Записывающий хендлер Dify: помнит созданные датасеты (id↔имя — их листингом
    // пользуется каскад), разворачивает create_by_text в (datasetId, имя, doc_metadata)
    // и держит журнал DELETE — по нему проверяется каскад датасетов.
    private sealed class FakeDify : HttpMessageHandler
    {
        private readonly List<(string Method, string Path, string? Body)> _calls = [];
        private readonly List<(string Id, string Name)> _datasets = [];
        private int _docs;

        public string? DatasetIdOf(string name) => _datasets.FirstOrDefault(d => d.Name == name).Id;

        public IReadOnlyList<(string DatasetId, string Name, Dictionary<string, string> Meta)> IndexedDocs
        {
            get
            {
                var res = new List<(string, string, Dictionary<string, string>)>();
                foreach (var (_, path, body) in _calls.Where(c => c.Path.Contains("/document/create_by_text")))
                {
                    var root = JsonDocument.Parse(body!).RootElement;
                    var meta = new Dictionary<string, string>();
                    if (root.TryGetProperty("doc_metadata", out var dm))
                        foreach (var p in dm.EnumerateObject())
                            if (p.Value.ValueKind == JsonValueKind.String) meta[p.Name] = p.Value.GetString()!;
                    var seg = path.Split('/');
                    res.Add((seg[Array.IndexOf(seg, "datasets") + 1], root.GetProperty("name").GetString()!, meta));
                }
                return res;
            }
        }

        public IReadOnlyList<string> DeletedPaths =>
            _calls.Where(c => c.Method == "DELETE").Select(c => c.Path).ToList();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            var path = request.RequestUri!.AbsolutePath;
            _calls.Add((request.Method.Method, path, body));

            if (path.EndsWith("/datasets") && request.Method == HttpMethod.Post)
            {
                var name = JsonDocument.Parse(body!).RootElement.GetProperty("name").GetString()!;
                var id = "ds-" + (_datasets.Count + 1);
                _datasets.Add((id, name));
                return Json($"{{\"id\":\"{id}\"}}");
            }
            if (path.EndsWith("/datasets"))
                // Имена датасетов тестовые ([A-Za-z:]+) — экранирование не нужно;
                // вложенный JsonSerializer давал двойные кавычки и битый JSON.
                return Json("{\"data\":[" + string.Join(",",
                    _datasets.Select(d => $"{{\"id\":\"{d.Id}\",\"name\":\"{d.Name}\"}}"))
                    + "],\"has_more\":false}");
            if (path.Contains("/document/create_by_text"))
                return Json($"{{\"document\":{{\"id\":\"doc-{++_docs}\",\"name\":\"doc\",\"indexing_status\":\"completed\"}}}}");
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class WikiFlagOn : ClaudeHomeServer.Services.Composition.IFeatureFlagGate
    {
        public bool IsEnabled(string ownerId, string key) => key == FeatureFlagKeys.LlmWiki;
    }
}
