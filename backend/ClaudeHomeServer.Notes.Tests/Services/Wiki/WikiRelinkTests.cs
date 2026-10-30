using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Tests.Helpers;

namespace ClaudeHomeServer.Tests.Services;

// S2c (ADR-035, решения 2, 3, 6, 10): relink — системная операция Хранителя вместо
// побочной правки NotesService в файлах под data/wiki/. Переименование меняет только
// цель [[…]] (подпись/якорь сохраняются, код не трогается), истечение разворачивает
// ссылку в текст. Операция под блокировкой затронутых разделов и с строкой журнала
// op=relink; страница в дрейфе не трогается. Алиасы страниц и префикс раздела
// резолвятся тем же индексом ByName; коллизия алиаса с заголовком — в пользу заголовка.
public class WikiRelinkTests : IDisposable
{
    private const string User = "u1";
    private readonly string _dir;
    private readonly NotesService _notes;
    private readonly WikiKeeper _sut;

    public WikiRelinkTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_relink_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var projects = new ProjectManager(config, users, new AppSettingsService(config));
        // Флаг llm-wiki включён: страницы вики должны быть в графе — именно их входящие
        // ссылки триггерят relink (тот же setup, что у WikiKeeperTests).
        _notes = new NotesService(projects, config, NullLogger<NotesService>.Instance, null, new WikiFlagOn());
        _sut = new WikiKeeper(_notes, config);
        // Та же обвязка, что ставит регистрация вертикали: побочный писатель →
        // внутренний IWikiRelink Хранителя (NotesSubsystem.Register, factory WikiKeeper).
        _notes.WikiRelink = _sut;
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string PageFull(string pageKey) => Path.Combine(WikiPaths.Pages(_dir, User), pageKey);

    private void WriteNote(string title, string content)
    {
        var dir = Path.Combine(_dir, "notes", User);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, title + ".md"), content, new UTF8Encoding(false));
    }

    private string NoteId(string title) =>
        _notes.GetSummaries(User, null, null).Single(s => s.Title == title).Id;

    private List<JsonElement> LogLines()
    {
        var path = Path.Combine(WikiPaths.Keeper(_dir, User), "log.jsonl");
        File.Exists(path).Should().BeTrue("журнал Хранителя должен быть записан");
        return [.. File.ReadAllLines(path).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l)).Select(d => d.RootElement)];
    }

    // ─── 1. Переименование: меняется только цель [[…]] (c1) ───────────────────

    private const string PageBody =
        "# Страница\n" +
        "\n" +
        "Ссылка [[Заметка|подпись]] и голая [[Заметка]] в тексте.\n" +
        "В коде: `[[Заметка]]` — не ссылка.\n" +
        "Чужое [[Другое]] остаётся.\n";

    private const string PageBodyRenamed =
        "# Страница\n" +
        "\n" +
        "Ссылка [[Заметка новая|подпись]] и голая [[Заметка новая]] в тексте.\n" +
        "В коде: `[[Заметка]]` — не ссылка.\n" +
        "Чужое [[Другое]] остаётся.\n";

    [Fact]
    public async Task Релинк_ПереименованиеЗаметки_МеняетТолькоЦельСсылки_СтрокаЖурнала_МанифестБезЛожногоДрейфа()
    {
        await _sut.EnsureInitializedAsync(User);
        WriteNote("Заметка", "текст заметки");
        (await _sut.WritePageAsync(User, "common", "Общее/Страница.md", PageBody))
            .Status.Should().Be(WikiOpStatus.Applied);

        var updated = _notes.Update(User, NoteId("Заметка"), new UpdateNoteRequest(Title: "Заметка новая"));
        updated.Should().NotBeNull();

        // Побайтно: вся страница равна ожидаемой — изменены только цели двух ссылок,
        // подпись сохранена, ссылка в коде и чужая ссылка не тронуты.
        File.ReadAllText(PageFull("Общее/Страница.md"), Encoding.UTF8).Should().Be(PageBodyRenamed);

        var line = LogLines().Single(l => l.GetProperty("op").GetString() == "relink");
        line.GetProperty("actor").GetString().Should().Be("keeper", "relink — системная операция Хранителя");
        line.GetProperty("section").GetString().Should().Be("common");
        line.GetProperty("pages")[0].GetString().Should().Be("Общее/Страница.md");

        // Манифест обновлён relink'ом: следующая запись Хранителя по текущим байтам диска —
        // Applied, а не ложный Drift (иначе relink ломал бы контракт дрейфа).
        var disk = File.ReadAllText(PageFull("Общее/Страница.md"), Encoding.UTF8);
        (await _sut.WritePageAsync(User, "common", "Общее/Страница.md", disk))
            .Status.Should().Be(WikiOpStatus.Applied);

        // Граф видит исправленную связь сразу: страница — входящая на новый заголовок.
        var wikiId = _notes.GetSummaries(User, "wiki", null).Single(s => s.Title == "Страница").Id;
        updated!.Backlinks.Should().Contain(b => b.SourceId == wikiId);
    }

    // ─── 2. Истечение: ссылка разворачивается в текст ─────────────────────────

    [Fact]
    public async Task Релинк_ИстекшаяЗаметка_СсылкаРазворачиваетсяВТекст_С_СтрокойЖурнала()
    {
        await _sut.EnsureInitializedAsync(User);
        WriteNote("Заметка", "текст заметки");
        (await _sut.WritePageAsync(User, "common", "Общее/Страница.md", "До [[Заметка|подпись]] и после [[Заметка]].\n"))
            .Status.Should().Be(WikiOpStatus.Applied);

        // Тот же путь, что делает NoteExpiryService для истёкшей заметки.
        _notes.RemoveWikilinksTo(User, "Заметка", NoteId("Заметка"));

        File.ReadAllText(PageFull("Общее/Страница.md"), Encoding.UTF8)
            .Should().Be("До подпись и после Заметка.\n");
        LogLines().Should().Contain(l => l.GetProperty("op").GetString() == "relink");
    }

    // ─── 3. Дрейф: системная операция чужую правку не затирает ────────────────

    [Fact]
    public async Task Релинк_СтраницуВДрейфеНеТрогает_СтрокиЖурналаНет()
    {
        await _sut.EnsureInitializedAsync(User);
        WriteNote("Заметка", "текст заметки");
        (await _sut.WritePageAsync(User, "common", "Общее/Страница.md", "Хранитель писал: [[Заметка]].\n"))
            .Status.Should().Be(WikiOpStatus.Applied);

        // Внешняя правка мимо Хранителя → дрейф (решение 2).
        File.WriteAllText(PageFull("Общее/Страница.md"), "Внешняя правка [[Заметка]].\n", new UTF8Encoding(false));

        _notes.Update(User, NoteId("Заметка"), new UpdateNoteRequest(Title: "Заметка новая"));

        File.ReadAllText(PageFull("Общее/Страница.md"), Encoding.UTF8).Should().Be("Внешняя правка [[Заметка]].\n");
        // Ни одной системной записи: страница в дрейфе не тронута, журнал не создан вовсе
        // (append был бы единственной строкой op=relink).
        File.Exists(Path.Combine(WikiPaths.Keeper(_dir, User), "log.jsonl")).Should().BeFalse();
    }

    // ─── 4. Гонка relink против записи пакета: обе записи целы (решение 10) ───

    [Fact]
    public async Task РелинкПодБлокировкойРаздела_ГонкаСЗаписьюСтраницыОбеЦелы_МанифестСовпадаетСДиском()
    {
        await _sut.EnsureInitializedAsync(User);
        WriteNote("Заметка", "текст заметки");
        (await _sut.WritePageAsync(User, "common", "Общее/A.md", "Ссылка [[Заметка]].\n"))
            .Status.Should().Be(WikiOpStatus.Applied);
        (await _sut.WritePageAsync(User, "common", "Общее/B.md", "Исходный B.\n"))
            .Status.Should().Be(WikiOpStatus.Applied);

        // relink (правит A) и запись страницы B одновременно, в одном разделе.
        // Без блокировки раздела это было бы гонка записи; с ней — сериализация,
        // и итог один при любом порядке: A relink-нут, B записан.
        await Task.WhenAll(
            _sut.RelinkAsync(User, "Заметка", "Заметка новая"),
            _sut.WritePageAsync(User, "common", "Общее/B.md", "Новый B.\n"));

        File.ReadAllText(PageFull("Общее/A.md"), Encoding.UTF8).Should().Be("Ссылка [[Заметка новая]].\n");
        File.ReadAllText(PageFull("Общее/B.md"), Encoding.UTF8).Should().Be("Новый B.\n");

        // Манифест после обеих операций равен байтам диска: ни одна запись не потеряна.
        var manifest = WikiManifest.TryRead(WikiPaths.Root(_dir, User), "common");
        manifest.State.Should().Be(WikiManifestState.Valid);
        foreach (var pageKey in new[] { "Общее/A.md", "Общее/B.md" })
        {
            var bytes = File.ReadAllBytes(PageFull(pageKey));
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            manifest.Manifest[pageKey].Should().Be(hash);
        }
    }

    // ─── 5. Алиасы и префикс раздела в резолве (c2, решение 3) ────────────────

    [Fact]
    public async Task АлиасИПрефиксРаздела_РезолвятсяБезПризраков_КоллизияВПользуЗаголовка()
    {
        await _sut.EnsureInitializedAsync(User);
        const string pageA = "---\ntype: topic\naliases: [Другое имя, Особый алиас]\n---\n# Страница\n";
        (await _sut.WritePageAsync(User, "common", "Общее/Страница.md", pageA))
            .Status.Should().Be(WikiOpStatus.Applied);
        (await _sut.WritePageAsync(User, "personal", "Личное/Другое имя.md", "# Другое имя\n"))
            .Status.Should().Be(WikiOpStatus.Applied);
        (await _sut.WritePageAsync(User, "common", "Общее/Цитата.md", "См. [[Особый алиас]].\n"))
            .Status.Should().Be(WikiOpStatus.Applied);

        // Уникальный алиас резолвится в страницу; в графе ссылка разрешена — призрака нет.
        var byAlias = _notes.ResolveByName(User, "Особый алиас", null);
        byAlias.Should().NotBeNull();
        byAlias!.Value.Note.Path.Should().Be("Общее/Страница.md");
        var citeId = _notes.GetSummaries(User, "wiki", null).Single(s => s.Title == "Цитата").Id;
        var link = _notes.GetDetail(User, citeId)!.Links.Single(l => l.TargetId == byAlias.Value.Note.Id);
        link.Resolved.Should().BeTrue();

        // Коллизия алиаса (A) с заголовком (B) — в пользу заголовка (решение 3).
        var byTitle = _notes.ResolveByName(User, "Другое имя", null);
        byTitle.Should().NotBeNull();
        byTitle!.Value.Note.Path.Should().Be("Личное/Другое имя.md");

        // Префикс раздела резолвит код; вне раздела — неразрешена, а не чужая страница.
        var prefixed = _notes.ResolveByName(User, "Общее/Страница", null);
        prefixed.Should().NotBeNull();
        prefixed!.Value.Note.Path.Should().Be("Общее/Страница.md");
        _notes.ResolveByName(User, "Личное/Страница", null).Should().BeNull();
        var prefixedTitle = _notes.ResolveByName(User, "Личное/Другое имя", null);
        prefixedTitle.Should().NotBeNull();
        prefixedTitle!.Value.Note.Path.Should().Be("Личное/Другое имя.md");
    }

    private sealed class WikiFlagOn : ClaudeHomeServer.Services.Composition.IFeatureFlagGate
    {
        public bool IsEnabled(string ownerId, string key) => key == FeatureFlagKeys.LlmWiki;
    }
}
