using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Notes.Controllers;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Services.Spheres;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace ClaudeHomeServer.Tests.Controllers;

// B5 (ADR-038, §6 и §7): POST /api/wiki/adopt с mode=mine («Принять как мою правку») и
// POST /api/wiki/ingest/note с transfer=true («Перенести в вики»). Контракт: svc-токен — 403 до
// всего; mine и transfer при выключенном флаге — 409 wiki_unified_off, а as-is работает без флага;
// mine строит пакет human из диффа «манифест → диск» (журнал adopt, actor user); перенос создаёт
// страницу human и заменяет заметку ссылкой через NotesService. Гонки: дрейф изменился до adopt —
// 409; двойной перенос одной заметки — одна страница. Контроллеры строятся напрямую — фабрики
// хоста нет, метка Host не нужна.
public class WikiAdoptTransferTests : IDisposable
{
    private const string User = "u1";
    private const string Statement = "Сервер работает на порту пять тысяч.";
    private const string Edited = "Сервер работает на порту пять тысяч один.";
    private const string Added = "Новый факт принёс человек в Obsidian.";
    private const string AiCard = "Карточка ИИ";

    private readonly string _dir;
    private readonly string _pages;
    private readonly IConfiguration _config;
    private readonly ProjectManager _projects;
    private readonly NotesService _notes;
    private readonly WikiKeeper _keeper;
    private readonly WikiApplyService _apply;
    private readonly WikiNoteTransfer _transfer;
    private readonly string _key;
    private readonly string _pageKey;

    public WikiAdoptTransferTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_adopt_transfer_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _projects = new ProjectManager(_config, users, new AppSettingsService(_config));
        _pages = WikiPaths.Pages(_dir, User);
        _notes = new NotesService(_projects, _config, NullLogger<NotesService>.Instance, null, flags: new Flags(true, true));
        _keeper = new WikiKeeper(_notes, _config);
        _apply = new WikiApplyService(_notes, _keeper, new NoSpheres(), _config);
        _transfer = new WikiNoteTransfer(_notes, _apply, _projects);

        var root = Path.Combine(_dir, "repo", User, "Проект");
        Directory.CreateDirectory(root);
        var project = _projects.Create("Проект", root, User, User);
        _key = "projects/" + project.Id;
        _pageKey = $"Проекты/{project.Id}/Знание.md";
        SeedAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ─── adopt mine: svc, флаги, as-is ─────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("mine")]
    [InlineData("as-is")]
    public async Task СервисныйТокен_Adopt_ВЛюбомРежиме_403_ДрейфНаМесте(string? mode)
    {
        ExternalEdit();
        var res = await Controller(serviceToken: true).Adopt(AdoptReq(mode));
        ((IStatusCodeActionResult)res).StatusCode.Should().Be(403);
        IsDrifted().Should().BeTrue("svc не доходит ни до одного режима adopt");
        PackageFiles().Should().BeEmpty();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Adopt_Mine_ФлагВыключен_409_wiki_unified_off_ДрейфНаМесте(bool llmWiki, bool unified)
    {
        ExternalEdit();
        AssertCode(await Controller(flags: new Flags(llmWiki, unified)).Adopt(AdoptReq("mine")), 409, "wiki_unified_off");
        IsDrifted().Should().BeTrue();
        PackageFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task Adopt_AsIs_РаботаетБезФлагов_ИМолчаливыйРежимТоже()
    {
        ExternalEdit();
        var before = PageBytes();
        Json(await Controller(flags: new Flags(false, false)).Adopt(AdoptReq("as-is")), 200);
        IsDrifted().Should().BeFalse("«как есть» снимает дрейф без флагов");
        PageBytes().Should().Equal(before, "as-is файл не трогает");
        PackageFiles().Should().BeEmpty();

        ExternalEdit(Added + " Второй.");
        Json(await Controller(flags: new Flags(false, false)).Adopt(AdoptReq(null)), 200);
        IsDrifted().Should().BeFalse();
    }

    [Fact]
    public async Task Adopt_НеизвестныйРежим_400()
    {
        AssertCode(await Controller().Adopt(AdoptReq("theirs")), 400, "wiki_adopt_mode");
    }

    // ─── adopt mine: пакет human из диффа ──────────────────────────────────

    [Fact]
    public async Task Adopt_Mine_ДиффВПакетHuman_СноскиНаКарточку_ЖурналAdoptUser_ДрейфСнят()
    {
        ExternalEdit();
        var diskBefore = PageBytes();
        var body = Json(await Controller().Adopt(AdoptReq("mine")), 200);
        body.GetProperty("status").GetString().Should().Be("applied");
        body.GetProperty("packageId").GetString().Should().NotBeNullOrEmpty();

        IsDrifted().Should().BeFalse("файл принят в манифест и записан пакетом");
        var page = WikiPage.Parse(PageText()).Model!;
        var summary = page.Sections.Single(s => s.Name == "Сводка").Lines;
        summary.Should().Contain(l => l.StartsWith(Edited) && l.Contains("[^s"), "изменённое утверждение получает сноску human");
        summary.Should().Contain(l => l.StartsWith(Added) && l.Contains("[^s"), "добавленное — тоже");
        page.Sections.Single(s => s.Name == "История").Lines.Should().Contain(l => l.StartsWith(Statement),
            "прежняя формулировка уходит в «Историю»");
        CardFiles().Should().ContainSingle(f => Path.GetFileName(f).StartsWith("Правка человека"));

        var line = LastJournalLine();
        line.GetProperty("op").GetString().Should().Be("adopt");
        line.GetProperty("actor").GetString().Should().Be("user");
        line.GetProperty("source").GetString().Should().StartWith("human:");

        WikiJournalReader.ReadLastHumanEdits(WikiPaths.Root(_dir, User)).Should().ContainKey(_pageKey,
            "«как мою» — правка человека для условия «источник новее правки»");

        // Откат пакета возвращает файл человека (прообраз — его байты), а не основу.
        Directory.GetFiles(Path.Combine(WikiPaths.Keeper(_dir, User), "preimages"), "*", SearchOption.AllDirectories)
            .Should().ContainSingle(f => File.ReadAllBytes(f).SequenceEqual(diskBefore));
    }

    [Fact]
    public async Task Adopt_AsIs_НеСчитаетсяПравкойЧеловека_ВОтличиеОтMine()
    {
        ExternalEdit();
        Json(await Controller().Adopt(AdoptReq("as-is")), 200);
        LastJournalLine().GetProperty("op").GetString().Should().Be("adopt");
        WikiJournalReader.ReadLastHumanEdits(WikiPaths.Root(_dir, User)).Should().NotContainKey(_pageKey,
            "«Принять как есть» — не утверждения человека");
    }

    [Fact]
    public async Task Adopt_Mine_ДрейфаНет_Unchanged_ПакетаНет()
    {
        var res = await Controller().Adopt(AdoptReq("mine"));
        Json(res, 200).GetProperty("status").GetString().Should().Be("unchanged");
        PackageFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task Adopt_Mine_БезBaseHash_400()
    {
        ExternalEdit();
        AssertCode(await Controller().Adopt(new WikiAdoptRequest(_key, _pageKey, "mine")), 400, "wiki_edit_invalid");
        IsDrifted().Should().BeTrue();
    }

    [Fact]
    public async Task Adopt_Mine_ОсновыНет_400_МанифестНеТронут()
    {
        ExternalEdit();
        Directory.Delete(Path.Combine(WikiPaths.Keeper(_dir, User), "baselines"), recursive: true);
        var res = await Controller().Adopt(AdoptReq("mine"));
        Json(res, 400).GetProperty("code").GetString().Should().Be("wiki_edit_invalid");
        IsDrifted().Should().BeTrue("отказ ничего не принял в манифест");
        PackageFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task Adopt_Mine_ЗапретнаяПравкаШапки_400_МанифестНеТронут()
    {
        File.WriteAllText(PageFile, PageText().Replace("type: topic", "type: entity"), new UTF8Encoding(false));
        AssertCode(await Controller().Adopt(AdoptReq("mine")), 400, "wiki_edit_invalid");
        IsDrifted().Should().BeTrue("человек правил поле, которое ему не принадлежит: ничего не принято");
    }

    // ─── adopt mine: конкурентность ────────────────────────────────────────

    [Fact]
    public async Task Adopt_Mine_ДрейфИзменилсяДоAdopt_409_wiki_page_changed_СоСвежейВерсией()
    {
        ExternalEdit();
        var seen = PageHash(); // человек увидел сравнение для этой версии файла
        ExternalEdit(Added + " И ещё одна правка.");
        var res = await Controller().Adopt(new WikiAdoptRequest(_key, _pageKey, "mine", seen));
        var body = Json(res, 409);
        body.GetProperty("code").GetString().Should().Be("wiki_page_changed");
        body.GetProperty("page").GetProperty("hash").GetString().Should().Be(PageHash());
        IsDrifted().Should().BeTrue("устаревшее сравнение ничего не принимает");
        PackageFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task Adopt_Mine_ДваОдновременных_ОдноПрименение_ВторойChanged()
    {
        ExternalEdit();
        var req = AdoptReq("mine");
        var results = await Task.WhenAll(
            Task.Run(() => Controller().Adopt(req)), Task.Run(() => Controller().Adopt(req)));
        var statuses = results.Select(r => ((IStatusCodeActionResult)r).StatusCode).OrderBy(c => c).ToArray();
        statuses.Should().Equal(200, 409);
        PackageFiles().Should().HaveCount(1, "два adopt по одному baseHash — один пакет");
        var loser = results.Single(r => ((IStatusCodeActionResult)r).StatusCode == 409);
        AssertCode(loser, 409, "wiki_page_changed");
    }

    // ─── Перенести в вики ──────────────────────────────────────────────────

    [Fact]
    public async Task Перенос_СервисныйТокен_403_ЗаметкаИВикаНеТронуты()
    {
        var note = PersonalNote("Моя мысль", "Первое утверждение.\nВторое утверждение.");
        var before = NoteText(note);
        var res = await Controller(serviceToken: true).IngestNote(new WikiIngestNoteRequest(note.Id, null, true));
        ((IStatusCodeActionResult)res).StatusCode.Should().Be(403);
        NoteText(note).Should().Be(before);
        PackageFiles().Should().BeEmpty();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Перенос_ФлагВыключен_409_wiki_unified_off_ЗаметкаЦела(bool llmWiki, bool unified)
    {
        var note = PersonalNote("Моя мысль", "Первое утверждение.");
        var before = NoteText(note);
        AssertCode(await Controller(flags: new Flags(llmWiki, unified))
            .IngestNote(new WikiIngestNoteRequest(note.Id, null, true)), 409, "wiki_unified_off");
        NoteText(note).Should().Be(before);
        PackageFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task Перенос_ЛичнаяЗаметка_СтраницаВЛичном_ЗаметкаСтановитсяСсылкой()
    {
        var note = PersonalNote("Моя мысль", "---\ntags: [a]\n---\n# Заголовок\n- Первое утверждение.\n- Второе [[Другая|утверждение]].\n```\nкод\n```\n");
        var res = await Controller().IngestNote(new WikiIngestNoteRequest(note.Id, null, true));
        var body = Json(res, 200);
        body.GetProperty("outcome").GetString().Should().Be("transferred");
        body.GetProperty("section").GetString().Should().Be("personal");
        body.GetProperty("page").GetString().Should().Be("Личное/Моя мысль.md");

        var page = WikiPage.Parse(File.ReadAllText(Path.Combine(_pages, "Личное", "Моя мысль.md"))).Model!;
        var lines = page.Sections.Single(s => s.Name == "Сводка").Lines;
        lines.Should().HaveCount(3);
        lines[0].Should().StartWith("Заголовок").And.Contain("[^s");
        lines[1].Should().StartWith("Первое утверждение.");
        lines[2].Should().StartWith("Второе утверждение.");
        CardFiles("Личное").Should().ContainSingle(f => Path.GetFileName(f).StartsWith("Правка человека"));

        // Заметка заменена ссылкой на страницу — никакой копии текста.
        NoteText(note).Trim().Should().Be("Перенесено в вики: [[Личное/Моя мысль]]");
        NoteText(note).Should().NotContain("Первое утверждение");
        LastJournalLine().GetProperty("op").GetString().Should().Be("edit");
        LastJournalLine().GetProperty("actor").GetString().Should().Be("user");
    }

    [Fact]
    public async Task Перенос_ЗаметкаПроекта_СтраницаВРазделеПроекта()
    {
        var projectId = _key["projects/".Length..];
        var note = _notes.Create(User, new CreateNoteRequest("Проектная мысль", "Факт проекта.", projectId));
        Json(await Controller().IngestNote(new WikiIngestNoteRequest(note.Id, null, true)), 200)
            .GetProperty("page").GetString().Should().Be($"Проекты/{projectId}/Проектная мысль.md");
    }

    [Fact]
    public async Task Перенос_Повторный_409_wiki_note_transferred_СтраницаОдна()
    {
        var note = PersonalNote("Моя мысль", "Первое утверждение.");
        Json(await Controller().IngestNote(new WikiIngestNoteRequest(note.Id, null, true)), 200);
        var stub = NoteText(note);
        AssertCode(await Controller().IngestNote(new WikiIngestNoteRequest(note.Id, null, true)), 409, "wiki_note_transferred");
        NoteText(note).Should().Be(stub);
        PersonalPages().Should().HaveCount(1);
    }

    [Fact]
    public async Task Перенос_Гонка_ДваВызоваОднойЗаметки_ОднаСтраница_ВторомуЗаглушка()
    {
        var note = PersonalNote("Гонка", "Единственное утверждение.");
        // Первый вызов встаёт сразу после чтения заметки; второй подходит к замку. Под замком второй
        // не читает заметку, пока первый не закончил, — и видит заглушку. Без замка второй
        // прочёл бы оригинал и перенёс сам (а проиграл бы первый, с чужим кодом).
        var firstRead = new ManualResetEventSlim();
        var secondRequested = new ManualResetEventSlim();
        var requests = 0;
        var firstHeld = 0;
        _transfer.NoteLockRequestedHook = _ =>
        {
            if (Interlocked.Increment(ref requests) == 2) secondRequested.Set();
        };
        _transfer.NoteReadHook = _ =>
        {
            if (Interlocked.Exchange(ref firstHeld, 1) == 0)
            {
                firstRead.Set();
                secondRequested.Wait(TimeSpan.FromSeconds(20)).Should().BeTrue("второй вызов обязан подойти к замку");
            }
        };

        var first = Task.Run(() => Controller().IngestNote(new WikiIngestNoteRequest(note.Id, null, true)));
        firstRead.Wait(TimeSpan.FromSeconds(20)).Should().BeTrue();
        var second = Task.Run(() => Controller().IngestNote(new WikiIngestNoteRequest(note.Id, null, true)));
        var results = await Task.WhenAll(first, second);

        Json(results[0], 200);
        AssertCode(results[1], 409, "wiki_note_transferred");
        PersonalPages().Should().HaveCount(1, "двойной перенос одной заметки — одна страница");
    }

    [Fact]
    public async Task Перенос_СбойЗамены_Отказ_ПовторДоводит_СтраницаОдна_ЗаметкаСсылка()
    {
        var note = PersonalNote("Сбой", "Единственное утверждение.");
        var before = NoteText(note);
        var calls = 0;
        _transfer.ReplaceNoteHook = (u, id, req) =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new IOException("диск");
            return _notes.Update(u, id, req);
        };
        AssertCode(await Controller().IngestNote(new WikiIngestNoteRequest(note.Id, null, true)), 409, "wiki_refused");
        NoteText(note).Should().Be(before, "замена не состоялась: заметка цела");
        PersonalPages().Should().HaveCount(1);

        Json(await Controller().IngestNote(new WikiIngestNoteRequest(note.Id, null, true)), 200)
            .GetProperty("outcome").GetString().Should().Be("transferred");
        PersonalPages().Should().HaveCount(1, "повтор доводит замену, второй страницы нет");
        NoteText(note).Trim().Should().Be("Перенесено в вики: [[Личное/Сбой]]");
    }

    [Fact]
    public async Task Перенос_ЗаметкаИсчезлаДоЗамены_404_НеТранслируется200()
    {
        var note = PersonalNote("Исчезла", "Утверждение.");
        _transfer.ReplaceNoteHook = (_, _, _) => null;
        AssertCode(await Controller().IngestNote(new WikiIngestNoteRequest(note.Id, null, true)), 404, "not_found");
    }

    [Fact]
    public async Task Перенос_СтраницуПравилиПослеСбоя_НеДоводитЧужое()
    {
        var note = PersonalNote("Правленная", "Утверждение один.");
        _transfer.ReplaceNoteHook = (_, _, _) => throw new IOException("диск");
        await Controller().IngestNote(new WikiIngestNoteRequest(note.Id, null, true));
        var file = Path.Combine(_pages, "Личное", "Правленная.md");
        File.WriteAllText(file, File.ReadAllText(file).Replace("Утверждение один.", "Совсем другое."), new UTF8Encoding(false));
        _transfer.ReplaceNoteHook = null;
        var before = NoteText(note);
        // Правка мимо веб-морды — дрейф: перенос не доводит чужую версию и не создаёт вторую страницу.
        ((IStatusCodeActionResult)await Controller().IngestNote(new WikiIngestNoteRequest(note.Id, null, true)))
            .StatusCode.Should().Be(409);
        NoteText(note).Should().Be(before);
        PersonalPages().Should().HaveCount(1);
    }

    [Fact]
    public async Task Adopt_Mine_СтраницаДоФичи_БезОсновы_BackfillДаётОсновуИзФайла()
    {
        // Страница записана «до фичи»: основы нет, манифест есть, файл не дрейфует. Первый вход
        // под замок раздела донесёт основу, и правка мимо веб-морды принимается как мою.
        var baselines = Path.Combine(WikiPaths.Keeper(_dir, User), "baselines");
        Directory.Delete(baselines, recursive: true);
        var markers = Path.Combine(WikiPaths.Keeper(_dir, User), "baselines-backfilled");
        if (Directory.Exists(markers)) Directory.Delete(markers, recursive: true);

        await _keeper.WithSectionsLockedAsync(User, [_key], () => Task.FromResult(0)); // первый вход под замок раздела
        Directory.Exists(baselines).Should().BeTrue("backfill донёс основу из файла");
        ExternalEdit();
        var body = Json(await Controller().Adopt(AdoptReq("mine")), 200);
        body.GetProperty("status").GetString().Should().Be("applied");
        PackageFiles().Should().HaveCount(1);
    }

    [Fact]
    public async Task Перенос_ИмяСтраницыЗанято_409_ЗаметкаЦела()
    {
        var a = PersonalNote("Одинаковое", "Первая заметка.");
        var b = _notes.Create(User, new CreateNoteRequest("Одинаковое", "Вторая заметка.", null, null, "Папка"));
        Json(await Controller().IngestNote(new WikiIngestNoteRequest(a.Id, null, true)), 200);
        var before = NoteText(b);
        AssertCode(await Controller().IngestNote(new WikiIngestNoteRequest(b.Id, null, true)), 409, "wiki_page_changed");
        NoteText(b).Should().Be(before, "страницу не перезаписали, заметку не заменили");
    }

    [Fact]
    public async Task Перенос_НетЧужойЗаметки_404_ПустойТекст_400_БезTarget()
    {
        var empty = PersonalNote("Пустая", "---\ntags: [a]\n---\n");
        AssertCode(await Controller().IngestNote(new WikiIngestNoteRequest(empty.Id, null, true)), 400, "wiki_edit_invalid");
        AssertCode(await Controller().IngestNote(new WikiIngestNoteRequest("personal:нет-такой.md", null, true)), 404, "not_found");
        AssertCode(await Controller().IngestNote(new WikiIngestNoteRequest(empty.Id, "common", true)), 400, "wiki_transfer_target");
    }

    // ─── Помощники ─────────────────────────────────────────────────────────

    private NoteDetail PersonalNote(string title, string content) => _notes.Create(User, new CreateNoteRequest(title, content));

    private string NoteText(NoteDetail note) => _notes.GetDetail(User, note.Id)!.Content;

    private string[] PersonalPages() =>
        Directory.GetFiles(Path.Combine(_pages, "Личное"), "*.md", SearchOption.TopDirectoryOnly)
            .Where(f => !Path.GetFileName(f).StartsWith('_')).ToArray();

    private async Task SeedAsync()
    {
        await _keeper.EnsureInitializedAsync(User);
        var card = $"---\ntype: source\norigin: note\nref: note:n1\nhash: abc\ncaptured: 2026-11-01T10:00:00.0000000+00:00\naliases: []\nowns: []\nsources: []\nstatus: active\n---\n\n# {AiCard}\n";
        var cardKey = _pageKey.Replace("Знание.md", $"Источники/{AiCard}.md");
        (await _keeper.WritePageAsync(User, _key, cardKey, card)).Status.Should().Be(WikiOpStatus.Applied);
        var page = $"---\ntype: topic\naliases: []\nowns: []\nsources: []\nstatus: active\n---\n\n# Знание\n\n## Сводка\n{Statement} [^s1]\n\n## Хронология\n\n## Противоречия\n\n## История\n\n[^s1]: [[{AiCard}]] — «Сервер слушает порт пять тысяч в конфиге»\n";
        (await _keeper.WritePageAsync(User, _key, _pageKey, page)).Status.Should().Be(WikiOpStatus.Applied);
    }

    // Правка мимо веб-морды (Obsidian): меняет утверждение и добавляет новое.
    private void ExternalEdit(string? added = null)
    {
        var text = PageText();
        var baseText = text.Contains(Edited) ? text : text.Replace(Statement, Edited);
        if (!baseText.Contains(added ?? Added))
            baseText = baseText.Replace("\n\n## Хронология", $"\n{added ?? Added}\n\n## Хронология");
        File.WriteAllText(PageFile, baseText, new UTF8Encoding(false));
    }

    private bool IsDrifted() =>
        WikiManifest.TryRead(WikiPaths.Root(_dir, User), _key).Manifest.GetValueOrDefault(_pageKey) != PageHash();

    private string PageFile => Path.Combine(_pages, _pageKey.Replace('/', Path.DirectorySeparatorChar));
    private string PageText() => File.ReadAllText(PageFile);
    private byte[] PageBytes() => File.ReadAllBytes(PageFile);
    private string PageHash() => Convert.ToHexString(SHA256.HashData(PageBytes())).ToLowerInvariant();

    private WikiAdoptRequest AdoptReq(string? mode) => new(_key, _pageKey, mode, PageHash());

    private string[] PackageFiles()
    {
        var dir = Path.Combine(WikiPaths.Keeper(_dir, User), "packages");
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json") : [];
    }

    private string[] CardFiles(string? folder = null)
    {
        var dir = folder is null
            ? Path.Combine(Path.GetDirectoryName(PageFile)!, WikiSourceCapture.SourceDirName)
            : Path.Combine(_pages, folder, WikiSourceCapture.SourceDirName);
        return Directory.GetFiles(dir, "*.md");
    }

    private JsonElement LastJournalLine()
    {
        var path = Path.Combine(WikiPaths.Keeper(_dir, User), "log.jsonl");
        return JsonDocument.Parse(File.ReadAllLines(path).Last(l => l.Length > 0)).RootElement.Clone();
    }

    private static JsonElement Json(IActionResult result, int status)
    {
        var obj = result.Should().BeAssignableTo<ObjectResult>().Which;
        obj.StatusCode.Should().Be(status, JsonSerializer.Serialize(obj.Value));
        return JsonDocument.Parse(JsonSerializer.Serialize(obj.Value)).RootElement.Clone();
    }

    private static void AssertCode(IActionResult result, int status, string code) =>
        Json(result, status).GetProperty("code").GetString().Should().Be(code);

    private static ControllerContext Ctx(bool serviceToken)
    {
        var http = new DefaultHttpContext();
        var claims = new List<Claim> { new("sub", User) };
        if (serviceToken) claims.Add(new("typ", "svc"));
        http.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        return new ControllerContext { HttpContext = http };
    }

    private WikiController Controller(bool serviceToken = false, Flags? flags = null) =>
        new(_keeper, _projects, new NoSpheres(), null!, null!, flags ?? new Flags(true, true), _config,
            null!, null!, _apply, _transfer)
        {
            ControllerContext = Ctx(serviceToken),
        };

    private sealed class Flags(bool llmWiki, bool unified) : IFeatureFlagGate
    {
        public bool IsEnabled(string userId, string key) =>
            (key == FeatureFlagKeys.LlmWiki && llmWiki) || (key == FeatureFlagKeys.NotesWikiUnified && unified);
    }

    private sealed class NoSpheres : ISphereDirectory
    {
        public string? SphereOf(string ownerId, string projectId) => null;
        public IReadOnlyList<string> ProjectsOf(string ownerId, string sphereId) => [];
        public bool Enabled(string ownerId) => false;
        public string? SphereName(string ownerId, string sphereId) => null;
        public string? CharterOf(string ownerId, string sphereId) => null;
    }
}
