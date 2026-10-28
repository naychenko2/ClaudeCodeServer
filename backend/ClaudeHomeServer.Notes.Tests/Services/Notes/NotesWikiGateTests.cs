using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Notes;

namespace ClaudeHomeServer.Tests.Services;

// Гейт «в вики пишет только Хранитель» на путях NotesService (ADR-035, решение 2).
//
// Путь под data/wiki/ заводится тестовым проектом, корень которого — в pages-каталоге вики:
// его источник notes/ попадает под data/wiki/{owner}/pages/ — это единственный способ
// привести существующие пути записи в поддерево владельца, не изобретая нового API.
// В бою data/ вне корней проектов; проверка по каноническому пути держит и обратный
// случай (корень проекта — предок data/).
public class NotesWikiGateTests : IDisposable
{
    private const string User = "u1";
    private readonly string _dir;        // dataDir
    private readonly string _vault;      // личный vault {dir}/notes/{User}
    private readonly string _wikiRoot;   // {dir}/wiki/{User}
    private readonly string _wikiNotes;  // источник тестового проекта: {dir}/wiki/{User}/pages/notes
    private readonly ProjectManager _projects;
    private readonly NotesService _sut;
    private readonly string _projId;

    public NotesWikiGateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "notes_wiki_gate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _vault = Path.Combine(_dir, "notes", User);
        Directory.CreateDirectory(_vault);
        _wikiRoot = Path.Combine(_dir, "wiki", User);
        _wikiNotes = Path.Combine(_wikiRoot, "pages", "notes");
        Directory.CreateDirectory(_wikiNotes);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(config);
        _projects = new ProjectManager(config, users, appSettings);
        var project = _projects.Create("Вики-проект", Path.Combine(_wikiRoot, "pages"), User, "u1", createDirectory: true);
        _projId = project.Id;
        _sut = new NotesService(_projects, config, NullLogger<NotesService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ─── Таблица методов (default-deny, образец — SubsystemBoundaryTests) ─────

    // Охраняемые: вызов с путём под data/wiki/ → wiki_readonly. Список — ровно
    // мутирующие методы NotesService по ADR-035, решение 2; Move проверяет обе стороны.
    // Писатели frontmatter комментариев (Repin/SetStatus) — тоже охраняемые: файл
    // комментария мог попасть под data/wiki/ помимо Create (ревью S1a, MAJOR 3).
    private static readonly string[] GuardedMethods =
    [
        "Create", "Update", "Delete", "Move", "MoveFolder",
        "CreateFolder", "DeleteFolder", "LinkMention",
        "RepinAnnotation", "SetAnnotationStatus",
    ];

    // Всё остальное публичное — чтение (не пишет) или не способно записать под data/wiki/:
    // каждая строка с причиной. Новый публичный метод без строки красит тест-перебор.
    private static readonly Dictionary<string, string> ExcludedMethods = new()
    {
        ["GetSources"] = "чтение",
        ["GetSummaries"] = "чтение",
        ["GetDetail"] = "чтение",
        ["GetBacklinks"] = "чтение",
        ["GetGraph"] = "чтение",
        ["GetFolders"] = "чтение",
        ["GetTemplates"] = "чтение",
        ["GetExpiredNotes"] = "чтение",
        ["GetReplies"] = "чтение",
        ["GetDocAnnotations"] = "чтение",
        ["ResolveByName"] = "чтение",
        ["ResolveAttachmentPath"] = "чтение",
        ["GetOrCreateDaily"] = "пишет только Journal/ личного vault: ResolveRoot(PersonalKey) = data/notes/{u} — не зайдёт под data/wiki/",
        ["RemoveWikilinksTo"] = "побочный писатель: пропускает файлы под data/wiki/ (тест RemoveWikilinksTo_ПропускаетФайлыПодDataWiki)",
        ["Annotate"] = "побочный писатель: не пишет ^id в файл под data/wiki/ (ResolveDoc); сам комментарий создаётся через Create (охраняем)",
        ["Reply"] = "создаёт заметку через Create (охраняем)",
        ["RewriteAnnotationTargets"] = "побочный писатель: пропускает файлы под data/wiki/ пофайлово (тест RewriteAnnotationTargets_ПропускаетФайлПодDataWiki)",
    };

    [Fact]
    public void ПубличныеМетодыNotesService_ВсеОписаныВТаблице()
    {
        var publicNames = typeof(NotesService)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .Distinct()
            .ToList();

        var covered = GuardedMethods.Concat(ExcludedMethods.Keys).Distinct().ToList();
        publicNames.Should().BeEquivalentTo(covered,
            "новый публичный метод NotesService обязан быть классифицирован: охраняемый или с причиной (ADR-035, решение 2)");
    }

    [Fact]
    public void МутирующиеМетоды_ОтказываютПоПутиПодDataWiki()
    {
        // Состояние всех драйверов готовится до первого чтения модели: она кэшируется
        // на владельца (TTL 2 с), и файл, созданный после построения, следующим IdOf уже
        // не виден. Вызовы — только действия, каждый обязан отказать.
        var calls = BuildRefusedCalls().ToList();
        calls.Select(c => c.Name.Split('/')[0]).Distinct()
            .Should().BeEquivalentTo(GuardedMethods, "у каждого охраняемого метода должен быть драйвер");

        foreach (var (name, call) in calls)
        {
            var ex = Record.Exception(call);
            var we = ex.Should().BeAssignableTo<WikiReadOnlyException>($"{name} обязан отказать путь под data/wiki/").Which;
            we.Code.Should().Be(WikiReadOnlyException.ErrorCode);
            we.Message.Should().Be(WikiReadOnlyException.ErrorText);
        }
    }

    // Подготовка выполняется при перечислении (до вызовов), сами вызовы — Action-и.
    private IEnumerable<(string Name, Action Call)> BuildRefusedCalls()
    {
        yield return ("Create", () => _sut.Create(User, new CreateNoteRequest("Вики-заметка", "тело", _projId)));

        // Update: с переименованием и без — цель одна, файл под data/wiki/
        WriteWikiNote("Без-имени");
        WriteWikiNote("С-именем");
        yield return ("Update/без переименования", () => _sut.Update(User, IdOf("Без-имени"), new UpdateNoteRequest(Content: "новое тело")));
        yield return ("Update/с переименованием", () => _sut.Update(User, IdOf("С-именем"), new UpdateNoteRequest(Title: "Новое имя")));

        WriteWikiNote("Удалить");
        yield return ("Delete", () => _sut.Delete(User, IdOf("Удалить")));

        // Move: обе стороны — источник под вики и цель под вики
        WriteWikiNote("Перенести");
        WriteFile(Path.Combine(_vault, "ИзВнешней.md"), "# ИзВнешней");
        yield return ("Move/источник под вики", () => _sut.Move(User, IdOf("Перенести"), "Подпапка"));
        yield return ("Move/цель под вики", () => _sut.Move(User, IdOf("ИзВнешней"), null, _projId));

        Directory.CreateDirectory(Path.Combine(_wikiNotes, "Папка"));
        yield return ("MoveFolder", () => _sut.MoveFolder(User, _projId, "Папка", "Новая-папка"));

        yield return ("CreateFolder", () => _sut.CreateFolder(User, _projId, "Новая"));

        Directory.CreateDirectory(Path.Combine(_wikiNotes, "Удалить-папку"));
        yield return ("DeleteFolder", () => _sut.DeleteFolder(User, _projId, "Удалить-папку"));

        WriteFile(Path.Combine(_vault, "Цель.md"), "# Цель");
        WriteWikiNote("Упоминание", "# Упоминание\n\nЗдесь есть Цель без связи.");
        yield return ("LinkMention", () => _sut.LinkMention(User, IdOf("Упоминание"), "Цель"));

        // Писатели frontmatter комментариев (ревью S1a, MAJOR 3): файл комментария под
        // data/wiki/ — отказ, до любого чтения/записи.
        WriteWikiComment("Коммент-статус");
        yield return ("SetAnnotationStatus", () => _sut.SetAnnotationStatus(User, IdOf("Коммент-статус"), "resolved"));
        WriteWikiComment("Коммент-репин");
        yield return ("RepinAnnotation", () => _sut.RepinAnnotation(User, IdOf("Коммент-репин"),
            new AnnotateSelection(0, 10, "не важно — отказ раньше")));
    }

    // ─── Побочные писатели: файлы под вики не трогает никто, кроме Хранителя ──

    [Fact]
    public void ПереименованиеЗаметки_НеПишетВФайлПодDataWiki()
    {
        // Файл под вики ссылается на обычную заметку личного vault. Переименование
        // заметки запускает RewriteLinks по всем источникам ссылок — файл под
        // data/wiki/ должен остаться нетронутым (ADR-035, решение 2).
        WriteFile(Path.Combine(_vault, "Цель.md"), "# Цель");
        var wikiFile = WriteWikiNote("Хроника", "См. [[Цель]].");
        var before = Sha256(wikiFile);

        _sut.Update(User, IdOf("Цель"), new UpdateNoteRequest(Title: "Новая цель"));

        File.Exists(Path.Combine(_vault, "Новая цель.md")).Should().BeTrue(); // переименование было
        Sha256(wikiFile).Should().Be(before);                                 // файл под вики — нет
    }

    [Fact]
    public void RemoveWikilinksTo_ПропускаетФайлыПодDataWiki()
    {
        WriteFile(Path.Combine(_vault, "Цель.md"), "# Цель");
        WriteFile(Path.Combine(_vault, "Исток.md"), "См. [[Цель]].");
        var wikiFile = WriteWikiNote("Хроника", "См. [[Цель]].");
        var before = Sha256(wikiFile);

        _sut.RemoveWikilinksTo(User, "Цель", IdOf("Цель"));

        File.ReadAllText(Path.Combine(_vault, "Исток.md")).Should().Be("См. Цель."); // обычная заметка переписана
        Sha256(wikiFile).Should().Be(before);                                        // файл под вики — нет
    }

    [Fact]
    public void Annotate_НеПишетЯкорьВФайлПодDataWiki()
    {
        var text = "Текст абзаца для цитаты.";
        var doc = Path.Combine(_wikiNotes, "Документ.md");
        File.WriteAllText(doc, "# Документ\n\n" + text + "\n", new UTF8Encoding(false));
        var at = File.ReadAllText(doc).IndexOf(text, StringComparison.Ordinal);
        var before = Sha256(doc);

        var ex = Record.Exception(() => _sut.Annotate(User, new AnnotateRequest(
            new AnnotateDocRef(_projId, "notes/Документ.md"),
            new AnnotateSelection(at, at + text.Length, text),
            Comment: "проверка якоря")));

        // Сам комментарий создаётся в источнике документа — под data/wiki/ — Create охраняем.
        ex.Should().BeOfType<WikiReadOnlyException>();
        // Но документ не изменён: ^id в файл под вики не вписан (ResolveDoc: canWrite=false).
        Sha256(doc).Should().Be(before);
    }

    // ─── Писатели frontmatter комментариев (ревью S1a, MAJOR 3) ──────────────
    // Файл комментария мог попасть под data/wiki/ помимо Create: остаться от прежней
    // версии вики, переехать с проектом-источником, быть создан мимо сервера.

    [Fact]
    public void SetAnnotationStatus_ОтказываетПоФайлуПодDataWiki()
    {
        var file = WriteWikiComment("Коммент-статус");
        var before = Sha256(file);

        var ex = Record.Exception(() => _sut.SetAnnotationStatus(User, IdOf("Коммент-статус"), "resolved"));

        ex.Should().BeOfType<WikiReadOnlyException>();
        Sha256(file).Should().Be(before); // статус не переписан
    }

    [Fact]
    public void RepinAnnotation_ОтказываетПоФайлуПодDataWiki()
    {
        // Документ — обычная заметка личного vault: без отказа она получила бы новый ^id,
        // а комментарий под вики остался бы с прежним annotates — полузапись. Отказ стоит
        // до ResolveDoc: ни файл, ни документ не тронуты.
        WriteFile(Path.Combine(_vault, "Документ.md"), "# Документ\n\nТекст абзаца для цитаты.\n");
        var file = WriteWikiComment("Коммент-репин", docPath: "personal/Документ.md");
        var before = Sha256(file);
        var docBefore = Sha256(Path.Combine(_vault, "Документ.md"));

        var ex = Record.Exception(() => _sut.RepinAnnotation(User, IdOf("Коммент-репин"),
            new AnnotateSelection(11, 34, "Текст абзаца для цитаты.")));

        ex.Should().BeOfType<WikiReadOnlyException>();
        Sha256(file).Should().Be(before);
        Sha256(Path.Combine(_vault, "Документ.md")).Should().Be(docBefore);
    }

    [Fact]
    public void RewriteAnnotationTargets_ПропускаетФайлПодDataWiki()
    {
        // Тот же пропуск пофайлово, что у RewriteLinks/RemoveWikilinksTo: обычный
        // комментарий переписывается, файл под вики — нет; пропуск до try, полузаписи нет.
        var normal = WriteCommentIn(_vault, "Коммент-обычный");
        var wiki = WriteWikiComment("Коммент-вики");
        var wikiBefore = Sha256(wiki);

        var changed = _sut.RewriteAnnotationTargets(User, "personal", "Документ.md", "personal", "Документ-новая.md");

        changed.Should().Be(1); // только обычный
        File.ReadAllText(normal).Should().Contain("annotates: \"personal/Документ-новая.md\"");
        Sha256(wiki).Should().Be(wikiBefore); // файл под вики — нет
    }

    // ─── Помощники ───────────────────────────────────────────────────────────

    // Заголовок заметки = имя файла: иначе IdOf по заголовку не найдёт заметку
    private string WriteWikiNote(string title, string? content = null)
    {
        var full = Path.Combine(_wikiNotes, title + ".md");
        File.WriteAllText(full, content ?? $"# {title}", new UTF8Encoding(false));
        return full;
    }

    // Файл-комментарий: обычная .md-заметка с frontmatter annotates (тот же формат, что
    // пишет Annotate). Под data/wiki/ — «унаследованный» комментарий: создан мимо сервера.
    private string WriteWikiComment(string title, string docPath = "personal/Документ.md") =>
        WriteCommentIn(_wikiNotes, title, docPath);

    private static string WriteCommentIn(string dir, string title, string docPath = "personal/Документ.md")
    {
        var full = Path.Combine(dir, "Комментарии", title + ".md");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full,
            "---\n" +
            $"title: {title}\n" +
            $"annotates: \"{docPath}\"\n" +
            "anchor_quote: \"Текст абзаца для цитаты.\"\n" +
            "status: open\n" +
            "---\n\n> Текст абзаца для цитаты.\n",
            new UTF8Encoding(false));
        return full;
    }

    private static void WriteFile(string full, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(false));
    }

    private string IdOf(string title) =>
        _sut.GetSummaries(User, null, null).Single(n => n.Title == title).Id;

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
