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

    // ─── 3. Переезд страницы в другой раздел (дефект 7b3dc3db, п. А) ──────────

    [Fact]
    public async Task Синк_ПереездСтраницыВДругойРаздел_ПереиндексированаСНовымЗначениемРаздела()
    {
        await SeedWikiAsync();
        await _sut.SyncAllAsync(_userId);

        var oldDocId = _dify.DocIdOf("Знание")!;
        var oldHash = RegistryWikiDocs(ReadRegistry()).First(e => e.DocId == oldDocId).Hash;

        // Переезд без правки текста: файл удалён из раздела проекта, тот же текст — в «Общем».
        File.Delete(Path.Combine(WikiPaths.Pages(_dir, _userId), "Проекты", _projectId, "Знание.md"));
        _notes.Invalidate(_userId);
        var createsBefore = CountCreateCalls("Знание");
        (await _keeper.WritePageAsync(_userId, WikiSections.Common, "Общее/Знание.md",
            "---\ntype: topic\n---\n# Знание\n\nТекст страницы.\n")).Status.Should().Be(WikiOpStatus.Applied);

        await _sut.SyncAllAsync(_userId);

        // Ровно один create_by_text перенесённой страницы — с новым значением раздела.
        CountCreateCalls("Знание").Should().Be(createsBefore + 1);
        var newDocId = _dify.DocIdOf("Знание")!;
        newDocId.Should().NotBe(oldDocId);
        _dify.MetaOf(newDocId).Should().ContainKey("wiki_section");
        _dify.MetaOf(newDocId)["wiki_section"].Should().Be(WikiSections.Common);

        // Старый документ удалён после создания нового — тем же проходом (запись переехала,
        // старая исчезла из выборки).
        _dify.DeletedPaths.Should().Contain(p => p.EndsWith("/documents/" + oldDocId));

        // Раздел — часть хеша: тот же текст в другом разделе даёт другой хеш. Без раздела
        // в строке хеша это утверждение падает (мутация М1) — и переезд не переиндексируется никогда.
        var newHash = RegistryWikiDocs(ReadRegistry()).First(e => e.DocId == newDocId).Hash;
        newHash.Should().NotBe(oldHash);
    }

    // ─── 4. Отказ назначения метаданных: страница остаётся со старым документом ─
    //      (дефект 7b3dc3db, п. Б и В) ──────────────────────────────────────────

    [Fact]
    public async Task Синк_ОтказНазначенияМетаданных_СтраницаСохраняетСтарыйДокумент_ПроходНеРвётся()
    {
        await SeedWikiAsync();
        await _sut.SyncAllAsync(_userId);
        var oldDocId = _dify.DocIdOf("Знание")!;

        // Правка текста — документ нужно пересоздать; назначение метаданных отказывает.
        (await _keeper.WritePageAsync(_userId, "projects/" + _projectId, $"Проекты/{_projectId}/Знание.md",
            "---\ntype: topic\n---\n# Знание\n\nНовый текст страницы.\n")).Status.Should().Be(WikiOpStatus.Applied);
        _dify.FailAssignFor.Add("Знание");

        // Не бросает: отказ одной страницы ловится на странице (п. В).
        await _sut.SyncAllAsync(_userId);

        // Старый документ нетронут: его DELETE в журнала нет — удаление старого идёт только
        // после успешного создания (п. Б); осиротевший новый документ удалил сам индексер.
        _dify.DeletedPaths.Should().NotContain(p => p.EndsWith("/documents/" + oldDocId));

        // Реестр на диске — прежний DocRef: старый DocId, его хеш (не равен текущему →
        // следующий проход повторит индексацию сам).
        RegistryWikiDocs(ReadRegistry()).Should().Contain(e => e.DocId == oldDocId);
    }

    // ─── 5. Повтор после устранённого отказа: несовпадение хеша — признак повтора ─
    //      (п. В; новый признак «нужен повтор» не заведён) ────────────────────────

    [Fact]
    public async Task Синк_ПослеУстраненияОтказаНазначения_СледующийПроходПовторяетИндексациюСтраницы()
    {
        await SeedWikiAsync();
        await _sut.SyncAllAsync(_userId);
        var oldDocId = _dify.DocIdOf("Знание")!;
        (await _keeper.WritePageAsync(_userId, "projects/" + _projectId, $"Проекты/{_projectId}/Знание.md",
            "---\ntype: topic\n---\n# Знание\n\nНовый текст страницы.\n")).Status.Should().Be(WikiOpStatus.Applied);
        _dify.FailAssignFor.Add("Знание");
        await _sut.SyncAllAsync(_userId);
        // Как в тесте 4: отказ сохранён на старом DocRef, хеш реестра не равен текущему.
        RegistryWikiDocs(ReadRegistry()).Should().Contain(e => e.DocId == oldDocId);

        _dify.FailAssignFor.Clear();
        // Не считая отказного прохода (создать он успел, назначение — нет): ровно ещё один create.
        var createsBefore = CountCreateCalls("Знание");
        await _sut.SyncAllAsync(_userId);

        // Страница переиндексирована: ещё один create, метаданные с разделом, старый удалён,
        // в реестре — новый DocId.
        CountCreateCalls("Знание").Should().Be(createsBefore + 1);
        var newDocId = _dify.DocIdOf("Знание")!;
        newDocId.Should().NotBe(oldDocId);
        _dify.MetaOf(newDocId).Should().ContainKey("wiki_section");
        _dify.MetaOf(newDocId)["wiki_section"].Should().Be("projects/" + _projectId);
        _dify.DeletedPaths.Should().Contain(p => p.EndsWith("/documents/" + oldDocId));
        RegistryWikiDocs(ReadRegistry()).Should().Contain(e => e.DocId == newDocId)
            .And.NotContain(e => e.DocId == oldDocId);
    }

    // ─── 6. Сбой одной страницы не рвёт проход владельца (п. В) ────────────────

    [Fact]
    public async Task Синк_СбойОднойСтраницыНеРвётПроход_ОстальныеСтраницыИндексированы()
    {
        await _keeper.EnsureInitializedAsync(_userId);
        // Порядок обхода — GetSummaries: UpdatedAt desc, т.е. обратный порядку записи.
        // Отказ — на ожидаемо первой («Первая» записана последней); проверка стоит на второй:
        // она индексируется при любом порядке этих двух в обходе.
        (await _keeper.WritePageAsync(_userId, WikiSections.Common, "Общее/Вторая.md",
            "---\ntype: topic\n---\n# Вторая\n\nТекст второй страницы.\n")).Status.Should().Be(WikiOpStatus.Applied);
        (await _keeper.WritePageAsync(_userId, WikiSections.Common, "Общее/Первая.md",
            "---\ntype: topic\n---\n# Первая\n\nТекст первой страницы.\n")).Status.Should().Be(WikiOpStatus.Applied);
        _dify.FailAssignFor.Add("Первая");

        await _sut.SyncAllAsync(_userId);

        var wikiDs = _dify.DatasetIdOf(_username + ":wiki")!;
        var second = _dify.IndexedDocs.Single(d => d.Name == "Вторая");
        second.DatasetId.Should().Be(wikiDs);
        second.Meta.Should().ContainKey("wiki_section");
        second.Meta["wiki_section"].Should().Be(WikiSections.Common);

        // Реестр на диске: запись второй есть, записи упавшей первой — нет (её не касались:
        // повторит следующий проход по несовпадению хеша).
        var docId = _dify.DocIdOf("Вторая")!;
        RegistryWikiDocs(ReadRegistry()).Should().Contain(e => e.DocId == docId);
        RegistryWikiDocs(ReadRegistry()).Should().HaveCount(1);
    }

    // ─── 7. Save не теряется при исключении из ветки :notes (п. Г) ─────────────

    [Fact]
    public async Task Синк_ИсключениеВеткиNotes_УжеЗаписанныеDocRefПопадаютНаДиск()
    {
        var dir = Path.Combine(_dir, "notes", _userId);
        Directory.CreateDirectory(dir);
        // Тот же порядок обхода: «Заметка-2» записана первой → обход идёт по UpdatedAt desc:
        // сначала «Заметка-1» (успешно), затем «Заметка-2» — её отказ выбрасывает проход наружу
        // (ветка :notes — вне шага, её исключение пробрасывается).
        File.WriteAllText(Path.Combine(dir, "Заметка-2.md"), "вторая заметка", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir, "Заметка-1.md"), "первая заметка", new UTF8Encoding(false));
        _dify.FailCreateFor.Add("Заметка-2");

        await Assert.ThrowsAsync<HttpRequestException>(() => _sut.SyncAllAsync(_userId));

        // Уже записанный DocRef первой заметки — на диске (Save — в finally, не в конце try):
        // иначе после рестарта реестра у этих DocRef нет, а их документы — есть в индексе.
        var docId = _dify.DocIdOf("Заметка-1");
        docId.Should().NotBeNull();
        RegistryDocIds(ReadRegistry()).Should().Contain(docId);
    }

    // ─── 8. Отказ записи реестра не удерживает семафор (ревью 48348ac9, Major 1) ──
    //    Проба Глеба: Save() бросающий + два прохода подряд — второй обязан вернуться. ─

    [Fact]
    public async Task Синк_ОтказЗаписиРеестра_СемафорОсвобождён_ВторойПроходВозвращается()
    {
        // Невозможность записи — каталогом на месте файла реестра: File.Move поверх
        // каталога падает и на Windows, и на Linux (тот же эффект, что у каталога
        // данных только для чтения).
        Directory.CreateDirectory(Path.Combine(_dir, "notes-knowledge.json"));
        // Тот же порядок обхода, что в тесте 7: «Заметка-1» индексуется успешно (changed > 0),
        // её DocRef идёт в Save() этого finally — он и отказывает.
        var dir = Path.Combine(_dir, "notes", _userId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Заметка-2.md"), "вторая заметка", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir, "Заметка-1.md"), "первая заметка", new UTF8Encoding(false));

        // Первый проход: датасет создан, первая же запись реестра (сразу после создания
        // датасета) бросает — это ещё не проверяемый путь, но состояние для него.
        await Assert.ThrowsAsync<IOException>(() => _sut.SyncAllAsync(_userId));

        // Второй проход: индексация доходит до конца, Save() в finally отказывает.
        // С дефектом: бросок из finally наружу и семафор занят навсегда. Поэтому ожидание
        // — гонка с таймаутом (Task.WhenAny), а не прямой await: зависший проход не вернётся никогда.
        var second = _sut.SyncAllAsync(_userId);
        (await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(10)))).Should().Be(second,
            "отказ Save() не оставляет _syncLock занятым: второй проход обязан вернуться");
        (await second).Should().Be(2);

        // Третий проход — тоже возвращается: семафор свободен при любом исходе записи.
        // Реестр на диске по-прежнему каталог (ничего не записано), в памяти — живой:
        // хеши совпадают, повторной индексации нет.
        var third = _sut.SyncAllAsync(_userId);
        (await Task.WhenAny(third, Task.Delay(TimeSpan.FromSeconds(10)))).Should().Be(third);
        (await third).Should().Be(0);
        File.Exists(Path.Combine(_dir, "notes-knowledge.json")).Should().BeFalse();
    }

    // ─── 9. Отказ прохода и отказ записи: наружу — исключение прохода (c3) ───────

    [Fact]
    public async Task Синк_ОтказПроходаИОтказЗаписи_НаружуИдётИсключениеПрохода()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "notes-knowledge.json")); // любой Save() падает
        var dir = Path.Combine(_dir, "notes", _userId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Заметка-2.md"), "вторая заметка", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir, "Заметка-1.md"), "первая заметка", new UTF8Encoding(false));
        _dify.FailCreateFor.Add("Заметка-2"); // ветка :notes бросает (тот же порядок, что в тесте 7)

        await Assert.ThrowsAsync<IOException>(() => _sut.SyncAllAsync(_userId)); // как в тесте 8

        // Исключение прохода (ветка :notes) идёт наружу; ошибка записи (тот же каталог) ловится
        // в finally и логируется отдельно — с дефектом она затмила бы исходную (бросок из
        // finally) и тест краснеет на типе исключения.
        await Assert.ThrowsAsync<HttpRequestException>(() => _sut.SyncAllAsync(_userId));
    }

    // Реестр на диске (notes-knowledge.json): { userId: { Docs: { id: { DocId, Hash } },
    // WikiDocs: … } } — имена свойств как в Entry/DocRef (JsonFileStore.Save без опций).
    private JsonDocument ReadRegistry() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "notes-knowledge.json")));

    private List<(string DocId, string Hash)> RegistryWikiDocs(JsonDocument root) =>
        root.RootElement.GetProperty(_userId).GetProperty("WikiDocs").EnumerateObject()
            .Select(p => (p.Value.GetProperty("DocId").GetString()!, p.Value.GetProperty("Hash").GetString()!))
            .ToList();

    private List<string> RegistryDocIds(JsonDocument root) =>
        root.RootElement.GetProperty(_userId).GetProperty("Docs").EnumerateObject()
            .Select(p => p.Value.GetProperty("DocId").GetString()!)
            .ToList();

    private int CountCreateCalls(string name) => _dify.Calls.Count(c =>
        c.Method == "POST" && c.Path.Contains("/document/create_by_text") &&
        c.Body is not null && JsonDocument.Parse(c.Body).RootElement.GetProperty("name").GetString() == name);

    // ─── Подменённый Dify ─────────────────────────────────────────────────────

    // Записывающий хендлер Dify: помнит созданные датасеты (id↔имя — их листингом
    // пользуется каскад), разворачивает двухшаговую запись метаданных (дефект 87bb0af6:
    // Dify отбрасывает doc_metadata из тела create_by_text): create даёт (datasetId, docId,
    // имя), значения живут только в documents/metadata; объявление поля — GET/POST metadata
    // по датасету. Журнал DELETE — по нему проверяется каскад датасетов.
    private sealed class FakeDify : HttpMessageHandler
    {
        private readonly List<(string Method, string Path, string? Body)> _calls = [];
        private readonly List<(string Id, string Name)> _datasets = [];
        private readonly List<(string DatasetId, string DocId, string Name)> _docs = [];
        // datasetId → объявленные поля метаданных (id у каждого датасета свой)
        private readonly Dictionary<string, List<(string Id, string Name)>> _fields = [];
        // docId → назначенные значения (name → value)
        private readonly Dictionary<string, Dictionary<string, string>> _docMeta = [];
        private int _docsSeq;

        // Переключатели отказа (дефект 7b3dc3db): имена документов, на которых фейк отвечает
        // 500 — по назначению метаданных (documents/metadata) и по созданию (create_by_text,
        // без записи в _docs: такого документа в индексе нет).
        public HashSet<string> FailAssignFor { get; } = [];
        public HashSet<string> FailCreateFor { get; } = [];

        public string? DatasetIdOf(string name) => _datasets.FirstOrDefault(d => d.Name == name).Id;

        public IReadOnlyList<(string Method, string Path, string? Body)> Calls => _calls;
        public Dictionary<string, string> MetaOf(string docId) => _docMeta.GetValueOrDefault(docId) ?? [];
        public string? DocIdOf(string name) => _docs.Where(d => d.Name == name).Select(d => d.DocId).LastOrDefault();

        public IReadOnlyList<(string DatasetId, string Name, Dictionary<string, string> Meta)> IndexedDocs
        {
            get
            {
                var res = new List<(string, string, Dictionary<string, string>)>();
                foreach (var (ds, docId, name) in _docs)
                    res.Add((ds, name, _docMeta.GetValueOrDefault(docId) ?? []));
                return res;
            }
        }

        public IReadOnlyList<string> DeletedPaths =>
            _calls.Where(c => c.Method == "DELETE").Select(c => c.Path).ToList();

        private string DatasetOf(string path)
        {
            var seg = path.Split('/');
            return seg[Array.IndexOf(seg, "datasets") + 1];
        }

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
            {
                var name = JsonDocument.Parse(body!).RootElement.GetProperty("name").GetString()!;
                if (FailCreateFor.Contains(name))
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError); // без записи в _docs
                var docId = "doc-" + (++_docsSeq);
                _docs.Add((DatasetOf(path), docId, name));
                return Json($"{{\"document\":{{\"id\":\"{docId}\",\"name\":\"doc\",\"indexing_status\":\"completed\"}}}}");
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/documents/metadata"))
            {
                var ops = new List<(string DocId, List<(string Name, string Value)> Items)>();
                foreach (var op in JsonDocument.Parse(body!).RootElement.GetProperty("operation_data").EnumerateArray())
                {
                    var items = new List<(string, string)>();
                    foreach (var item in op.GetProperty("metadata_list").EnumerateArray())
                        if (item.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String)
                            items.Add((item.GetProperty("name").GetString()!, v.GetString()!));
                    ops.Add((op.GetProperty("document_id").GetString()!, items));
                }
                // Отказ назначения — по имени документа (имя — из журнала создания по document_id)
                if (ops.Any(o => FailAssignFor.Contains(_docs.FirstOrDefault(d => d.DocId == o.DocId).Name)))
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                foreach (var (docId, items) in ops)
                {
                    if (!_docMeta.TryGetValue(docId, out var meta)) _docMeta[docId] = meta = [];
                    foreach (var (name, value) in items) meta[name] = value;
                }
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (request.Method == HttpMethod.Get && path.EndsWith("/metadata"))
            {
                var fields = string.Join(",", _fields.GetValueOrDefault(DatasetOf(path), [])
                    .Select(f => $"{{\"id\":\"{f.Id}\",\"name\":\"{f.Name}\",\"type\":\"string\"}}"));
                return Json($"{{\"doc_metadata\":[{fields}]}}");
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/metadata"))
            {
                var name = JsonDocument.Parse(body!).RootElement.GetProperty("name").GetString()!;
                var ds = DatasetOf(path);
                if (!_fields.TryGetValue(ds, out var list)) _fields[ds] = list = [];
                // id поля — per-dataset (минор 5 ревью R-Ш1): поле принадлежит датасету,
                // счётчик — свой на датасет (та же форма, что в KnowledgeServiceMetadataTests)
                var id = $"f-{ds}-{list.Count + 1}";
                list.Add((id, name));
                return Json($"{{\"id\":\"{id}\",\"name\":\"{name}\",\"type\":\"string\"}}");
            }
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
