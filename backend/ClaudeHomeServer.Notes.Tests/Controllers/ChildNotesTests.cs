using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Notes.Controllers;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Б20b (ADR-041 ред. 8, разрез §14.2): «Заметки» ребёнку — CRUD по notes/ его проекта-
/// предмета и удаление в .ccs-trash/ вместо «насовсем». Ребёнок опознаётся боевой политикой
/// ChildModePolicy по стору (не по claim); проект-предмет — по маркеру ChildSubjectId, как
/// его ставит провижер Б8b (сам провижер и персона-репетитор здесь не нужны: в этой проверке
/// их никто не зовёт). Куратор-опекун — участник с ролью Editor (ред. 6): он правит обычной
/// рельсой, и снимков в корзину ему не делается (корзина — механизм ребёнка и репетитора).
///
/// Контроллер и сервисы конструируются напрямую (контракт действия, не транспорт) — по образцу
/// WikiReadsControllerTests и SharedProjectNotesAccessTests: полный хост в этом тестовом
/// проекте не поднимается по его построению. Отказ на НЕразмеченных ручках («граф»,
/// «бэклинки», «перенос», «источники») делает гейт ChildModeGate, а не контроллер: здесь
/// проверяется отсутствие метки [ChildAllowed] на них, сам 403 боевым токеном ребёнка — в
/// ChildModeEndpointGuardTests (там же замороженный список размеченных).
/// </summary>
public class ChildNotesTests : IDisposable
{
    private const string SubjectId = "russian";
    private const string Title = "Урок";
    private const string Body = "# Урок\n\nТекст заметки предмета.\n";
    private const string KidEdit = "# Урок\n\nПравка ребёнка.\n";
    private const string CuratorEdit = "# Урок\n\nПравка куратора.\n";

    private readonly string _dir;
    private readonly string _subjectRoot;
    private readonly IConfiguration _config;
    private readonly UserStore _users;
    private readonly ProjectManager _projects;
    private readonly NotesService _notes;
    private readonly NotesKnowledgeService _kb;
    private readonly string _kid;
    private readonly string _curator;
    private readonly string _subjectProject;
    private readonly string _foreignProject;

    public ChildNotesTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "b20b_notes_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dir, "projects.json"),
            [SharedProjectsGate.SwitchKey] = "true",
        }).Build();

        _users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var kidUser = _users.Add("b20b-kid", "pwd", "user");
        var adult = _users.Add("b20b-curator", "pwd", "user");
        _kid = kidUser.Id;
        _curator = adult.Id;
        // Рубильник общих проектов включён + флаг опекуна: без них куратор — посторонний,
        // а по разрезанию ред. 6 он обязан иметь доступ к проекту-предмету.
        _users.SetFeatureFlag(_curator, FeatureFlagKeys.SharedProjects, true);

        // Живой профиль ребёнка — записью в стор (тот же способ, что SetChild в
        // ChildModeEndpointGuardTests): политика читает его по ownerId, а не по claim.
        kidUser.ChildMode = new ChildProfile
        {
            Enabled = true, TutorPersonaId = "tutor-1", ProviderKey = "k", Model = "sonnet",
            Subjects =
            {
                new ChildSubject
                {
                    Id = SubjectId, Label = "Русский язык", Glyph = "Аа", Color = "info",
                },
            },
        };

        _projects = new ProjectManager(_config, _users, new AppSettingsService(_config));

        // Проект-предмет — по тому же признаку, что у провижнера Б8b: свой ребёнку и маркер
        // ChildSubjectId, записанный вместе с проектом.
        _subjectRoot = Path.Combine(_dir, "home", "Предметы", SubjectId);
        Directory.CreateDirectory(_subjectRoot);
        _subjectProject = _projects.Create("Русский язык", _subjectRoot, _kid, "b20b-kid",
            childSubjectId: SubjectId).Id;
        _projects.SetMembers(_subjectProject,
            [new ProjectMember { UserId = _curator, Role = ProjectRole.Editor, AddedBy = _kid }]);

        // Чужой проект (взрослого) — «чужой проект» из списка отказа.
        var foreignRoot = Path.Combine(_dir, "home", "Чужой");
        Directory.CreateDirectory(foreignRoot);
        _foreignProject = _projects.Create("Чужой проект", foreignRoot, _curator, "b20b-curator").Id;

        // Точка доступа — боевая: проект-предмет общий (у опекуна роль Editor), и корень
        // проектного источника берётся из проекта, а не из id вызывающего.
        IProjectAccess access = new ProjectAccessService(_projects, new NoSessions(),
            new SharedProjectsGate(_config, new FeatureFlagService(_users)));
        _notes = new NotesService(_projects, _config, NullLogger<NotesService>.Instance, null, access);
        _kb = new NotesKnowledgeService(new NoDify(), _notes, _users, _config,
            NullLogger<NotesKnowledgeService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ─── 1. CRUD своей заметки: все ручки отвечают ────────────────────────────

    [Fact]
    public async Task Ребёнок_CRUD_ЗаметкиСвоегоПредмета_ВсеРучкиОтвечают()
    {
        var created = await Ctl(_kid).Create(New(Title, Body, _subjectProject));
        var note = ValueOf<NoteDetail>(Ok200(created.Result!, "создание"));
        note.Source.Should().Be(_subjectProject);
        File.ReadAllText(NoteFile(note.Path)).Should().Be(Body);

        ValueOf<IReadOnlyList<NoteSummary>>(Ok200(Ctl(_kid).List(_subjectProject, null).Result!, "список"))
            .Single(n => n.Id == note.Id).Path.Should().Be(note.Path);

        ValueOf<NoteDetail>(Ok200(Ctl(_kid).Get(note.Id).Result!, "чтение")).Content.Should().Be(Body);

        Ok200((await Ctl(_kid).Update(note.Id, new UpdateNoteRequest(Content: KidEdit))).Result!, "правка");
        File.ReadAllText(NoteFile(note.Path)).Should().Be(KidEdit);
    }

    // ─── 2. Источник: только свой проект-предмет ─────────────────────────────

    [Fact]
    public async Task Ребёнку_ЛичныйVault_403_НаСпискеЧтенииИСоздании()
    {
        // Заметка личного vault ребёнка есть на диске (записывает её сервис: граница REST
        // к сервису не доходит). Ребёнку она закрыта как «не его источник», а не как
        // несуществующая — 403 с кодом, а не 404.
        var own = _notes.Create(_kid, New("Личное дело", "личное", "personal"));

        ChildRefused(Ctl(_kid).List("personal", null).Result!, "список личного vault");
        ChildRefused(Ctl(_kid).Get(own.Id).Result!, "чтение личной заметки");
        ChildRefused((await Ctl(_kid).Create(New("Новое личное", "текст", "personal"))).Result!, "создание в личный vault");
    }

    [Fact]
    public async Task Ребёнку_ЧужойПроект_403_НаСпискеИСоздании()
    {
        ChildRefused(Ctl(_kid).List(_foreignProject, null).Result!, "список чужого проекта");
        ChildRefused((await Ctl(_kid).Create(New("В чужом", "текст", _foreignProject))).Result!, "создание в чужом проекте");
    }

    [Fact]
    public void Ребёнку_БезИсточника_403_СпискаВсехИсточниковЕмуНет()
    {
        // Без source список — все источники владельца, в них личный vault: открыть его
        // целиком значило бы пустить личный vault ребёнка через дверь, которая его
        // отказывает построчно.
        ChildRefused(Ctl(_kid).List(null, null).Result!, "список без источника");
    }

    [Fact]
    public async Task Взрослому_СвойПроектИОбычнаяРельса_БезИзменений()
    {
        // Куратор (Editor общего проекта-предмета) — обычной рельсой: та же ручка отвечает
        // так, как до шага.
        var created = await Ctl(_curator).Create(New("Заметка опекуна", "текст опекуна", _subjectProject));
        var note = ValueOf<NoteDetail>(Ok200(created.Result!, "создание опекуном"));
        ValueOf<NoteDetail>(Ok200(Ctl(_curator).Get(note.Id).Result!, "чтение опекуном"));
        ValueOf<IReadOnlyList<NoteSummary>>(Ok200(Ctl(_curator).List(_subjectProject, null).Result!, "список опекуна"))
            .Select(n => n.Id).Should().Contain(note.Id);
    }

    // ─── 3. Размечено ровно то, что открыто ребёнку ──────────────────────────

    [Fact]
    public void ГрафБэклинкиПереносИсточники_МеткиРебёнкуНет_ОтказДелаетГейт()
    {
        // Метки нет → ChildModeGate отвечает 403 child_mode (боевым токеном ребёнка это
        // проверяется в ChildModeEndpointGuardTests). Здесь — сама граница списка: эти ручки
        // помечены НЕ должны быть, а пять ручек CRUD — обязаны.
        var marked = typeof(NotesController).GetMethods()
            .Where(m => m.GetCustomAttributes(typeof(ChildAllowedAttribute), false).Length > 0)
            .Select(m => m.Name).Order(StringComparer.Ordinal).ToArray();
        marked.Should().Equal(["Create", "Delete", "Get", "List", "Update"],
            "ребёнку открыты ровно эти пять действий");

        foreach (var name in new[] { "Graph", "Backlinks", "Move", "Sources", "Semantic", "MoveFolder", "Daily" })
            typeof(NotesController).GetMethod(name)!.GetCustomAttributes(typeof(ChildAllowedAttribute), false)
                .Should().BeEmpty($"«{name}» ребёнку не открывается");
    }

    // ─── 4. Удаление — в корзину, не насовсем ────────────────────────────────

    [Fact]
    public async Task УдалениеРебёнком_ФайлВКорзине_СНежнейВерсией()
    {
        var created = await Ctl(_kid).Create(New(Title, Body, _subjectProject));
        var note = ValueOf<NoteDetail>(Ok200(created.Result!, "создание"));

        var deleted = await Ctl(_kid).Delete(note.Id);
        ((IStatusCodeActionResult)deleted).StatusCode.Should().Be(204);

        File.Exists(NoteFile(note.Path)).Should().BeFalse();
        var entries = ChildTrash.List(_subjectRoot);
        entries.Should().HaveCount(1, "один снимок — одна запись");
        var entry = entries[0];
        entry.OriginalPath.Should().Be("notes/" + note.Path,
            "путь в корзине — от корня предмета, та же шкала, что у ChildPathPolicy");
        StoredOf(entry).Should().Be(Body);
        entry.Actor.Should().Be(_kid);
        ValueOf<IReadOnlyList<NoteSummary>>(Ok200(Ctl(_kid).List(_subjectProject, null).Result!, "список после удаления"))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task УдаленияНесуществующей_404_ЗаписиВКорзинеНет()
    {
        var deleted = await Ctl(_kid).Delete(NoteIdOf("нет-такой-заметки.md"));
        ((IStatusCodeActionResult)deleted).StatusCode.Should().Be(404);
        ChildTrash.List(_subjectRoot).Should().BeEmpty("несуществующее не снимается");
    }

    // ─── 5. Конкурентность: проигравшая версия не теряется ───────────────────

    // 20 прогонов (c2). Порядок намеренный: куратор правит первым (его версия — на диске),
    // затем ребёнок перезаписывает её без If-Match. Проигравшая — версия куратора — обязана
    // остаться в .ccs-trash/: снимок делается ДО записи ребёнка.
    // Мутация «не снимать версию перед записью» — красный: версия куратора исчезает с диска
    // и нигде не остаётся.
    // Обратный порядок (ребёнок первым, куратор последним) здесь не проверяется: взрослая
    // правка снимка не делает (корзина — механизм ребёнка и репетитора), и версия ребёнка в
    // ней не оказалась бы. Это граница механизма, а не потеря: последняя взрослая правка — на
    // диске, прежнее состояние — в корзине и в журнале правок.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    public async Task Гонка_ПравкиКуратораИРебёнка_ОбеВерсииСохранены(int iter)
    {
        var created = await Ctl(_curator).Create(New(Title, Body, _subjectProject));
        var note = ValueOf<NoteDetail>(Ok200(created.Result!, "создание"));

        Ok200((await Ctl(_curator).Update(note.Id, new UpdateNoteRequest(Content: CuratorEdit))).Result!, "правка куратора");
        Ok200((await Ctl(_kid).Update(note.Id, new UpdateNoteRequest(Content: KidEdit))).Result!, "правка ребёнка");

        // Победила последняя запись; версия, которую перезаписал ребёнок, — в корзине.
        File.ReadAllText(NoteFile(note.Path)).Should().Be(KidEdit);
        ChildTrash.List(_subjectRoot).Select(StoredOf).Should().Equal([CuratorEdit],
            "версия, которую перезаписал ребёнок, сохранена в корзине");
    }

    // 20 прогонов (c2): десять — удаление раньше правки, десять — правка раньше удаления.
    // Мутация «удалять насовсем» (снять снимок перед удалением) — красный в обоих порядках:
    // в первом содержимое остаётся только в корзине, во втором — только на диске.
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    [InlineData(7, true)]
    [InlineData(8, true)]
    [InlineData(9, true)]
    [InlineData(10, false)]
    [InlineData(11, false)]
    [InlineData(12, false)]
    [InlineData(13, false)]
    [InlineData(14, false)]
    [InlineData(15, false)]
    [InlineData(16, false)]
    [InlineData(17, false)]
    [InlineData(18, false)]
    [InlineData(19, false)]
    public async Task Гонка_УдаленияРебёнкомИПравкиКуратора_ЗаметкаНеПотеряна(int iter, bool deleteFirst)
    {
        var created = await Ctl(_kid).Create(New(Title, Body, _subjectProject));
        var note = ValueOf<NoteDetail>(Ok200(created.Result!, "создание"));

        string lastOnDisk;
        if (deleteFirst)
        {
            var deleted = await Ctl(_kid).Delete(note.Id);
            ((IStatusCodeActionResult)deleted).StatusCode.Should().Be(204);
            // Файла нет — правке нечего править: 404, и ничего не потеряно.
            var updated = (await Ctl(_curator).Update(note.Id, new UpdateNoteRequest(Content: CuratorEdit))).Result!;
            ((IStatusCodeActionResult)updated).StatusCode.Should().Be(404);
            lastOnDisk = Body;
        }
        else
        {
            Ok200((await Ctl(_curator).Update(note.Id, new UpdateNoteRequest(Content: CuratorEdit))).Result!, "правка куратора");
            var deleted = await Ctl(_kid).Delete(note.Id);
            ((IStatusCodeActionResult)deleted).StatusCode.Should().Be(204);
            lastOnDisk = CuratorEdit;
        }

        // Удаление состоялось в обоих порядках; последнее содержимое заметки — в корзине.
        File.Exists(NoteFile(note.Path)).Should().BeFalse();
        ChildTrash.List(_subjectRoot).Select(StoredOf).Should().Equal([lastOnDisk],
            "последнее содержимое заметки сохранено");
    }

    [Fact]
    public async Task ПравкаРебёнком_ПрежняяВерсияВКорзине_ФайлСНовой()
    {
        var created = await Ctl(_kid).Create(New(Title, Body, _subjectProject));
        var note = ValueOf<NoteDetail>(Ok200(created.Result!, "создание"));
        Ok200((await Ctl(_kid).Update(note.Id, new UpdateNoteRequest(Content: KidEdit))).Result!, "правка");

        File.ReadAllText(NoteFile(note.Path)).Should().Be(KidEdit);
        ChildTrash.List(_subjectRoot).Select(StoredOf).Should().Equal([Body],
            "прежняя версия — в корзине");
    }

    [Fact]
    public async Task ПравкиВзрослого_СнимкаВКорзинуНет_КорзинаЭтоМеханизмРебёнка()
    {
        var created = await Ctl(_kid).Create(New(Title, Body, _subjectProject));
        var note = ValueOf<NoteDetail>(Ok200(created.Result!, "создание"));
        Ok200((await Ctl(_curator).Update(note.Id, new UpdateNoteRequest(Content: CuratorEdit))).Result!, "правка куратора");
        Ok200((await Ctl(_curator).Create(New("Заметка опекуна", "текст", _subjectProject))).Result!, "создание опекуна");
        ChildTrash.List(_subjectRoot).Should().BeEmpty("взрослому и опекуну прежняя версия не нужна");
    }

    [Fact]
    public async Task ПравкаРебёнкаПоStaleIfMatch_409_СнимкаНет_ФайлНеТронут()
    {
        var created = await Ctl(_kid).Create(New(Title, Body, _subjectProject));
        var note = ValueOf<NoteDetail>(Ok200(created.Result!, "создание"));
        var ctl = Ctl(_kid);
        ctl.Request.Headers.IfMatch = "\"несущий-токен\"";
        var updated = (await ctl.Update(note.Id, new UpdateNoteRequest(Content: KidEdit))).Result!;
        ((IStatusCodeActionResult)updated).StatusCode.Should().Be(409);
        File.ReadAllText(NoteFile(note.Path)).Should().Be(Body);
        ChildTrash.List(_subjectRoot).Should().BeEmpty("записи не было — нечего и защищать");
    }

    // ─── 6. Путь внутри источника судит ChildPathPolicy ──────────────────────

    [Fact]
    public async Task СкрытаяПапка_РебёнкаНетНиСтрокиНиЧтения_ВзрослыйВидит()
    {
        // Заметку в скрытой папке записывает взрослый (он её видит): ребёнку путь Hidden →
        // ни строки в списке, ни чтения (404), а не отказа.
        var created = await Ctl(_curator).Create(New("Черновик", "текст", _subjectProject, ".черновики"));
        var note = ValueOf<NoteDetail>(Ok200(created.Result!, "создание опекуном"));

        ValueOf<IReadOnlyList<NoteSummary>>(Ok200(Ctl(_kid).List(_subjectProject, null).Result!, "список ребёнка"))
            .Select(n => n.Id).Should().NotContain(note.Id);
        ((IStatusCodeActionResult)Ctl(_kid).Get(note.Id).Result!).StatusCode.Should().Be(404);

        ValueOf<NoteDetail>(Ok200(Ctl(_curator).Get(note.Id).Result!, "чтение опекуном"));
    }

    [Fact]
    public async Task Ребёнок_ЗаголовокСТочкой_403_НичегоНеЗаписано()
    {
        // Имя файла — заголовок; ведущая «.» дала бы скрытый файл, которого ребёнок не
        // видит: отказ до записи, а не после.
        ChildRefused((await Ctl(_kid).Create(New(".тайна", "текст", _subjectProject))).Result!, "создание скрытого имени", "child_path");
        Directory.Exists(Path.Combine(_subjectRoot, "notes")).Should().BeFalse("до записи дело не дошло");
    }

    [Fact]
    public async Task Ребёнок_ВложеннаяПапкаСИменемМатериалы_ОбычнаяПапка_200()
    {
        // «Материалы» защищён на верхнем уровне предмета; notes/Материалы — обычная папка
        // ребёнка (п. 2 правил ChildPathPolicy), и заметка в ней пишется.
        var created = await Ctl(_kid).Create(New("Конспект", "текст", _subjectProject, "Материалы"));
        var note = ValueOf<NoteDetail>(Ok200(created.Result!, "создание в notes/Материалы"));
        File.Exists(NoteFile(note.Path)).Should().BeTrue();
    }

    // ─── Помощники ───────────────────────────────────────────────────────────

    private static CreateNoteRequest New(string title, string content, string? source, string? folder = null) =>
        new(title, content, source, Folder: folder);

    private NotesController Ctl(string userId) =>
        new(_notes, _kb, null!, null!, new NotesNoopHubNotifier(), _config, new ChildModePolicy(_users), _projects)
        { ControllerContext = Ctx(userId) };

    private static ControllerContext Ctx(string userId)
    {
        var http = new DefaultHttpContext();
        // JwtRegisteredClaimNames.Sub == "sub": та же точка, что в бою (NotesController.UserId).
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", userId)], "test"));
        return new ControllerContext { HttpContext = http };
    }

    private static OkObjectResult Ok200(IActionResult result, string what)
    {
        var obj = result.Should().BeAssignableTo<OkObjectResult>($"{what}: ожидаем 200").Which;
        obj.StatusCode.Should().Be(200);
        return obj;
    }

    private static void ChildRefused(IActionResult result, string what, string code = "child_source")
    {
        var obj = result.Should().BeAssignableTo<ObjectResult>($"{what}: ожидаем отказ").Which;
        obj.StatusCode.Should().Be(403);
        JsonDocument.Parse(JsonSerializer.Serialize(obj.Value)).RootElement.GetProperty("code")
            .GetString().Should().Be(code);
    }

    private static T ValueOf<T>(OkObjectResult ok) => ok.Value.Should().BeAssignableTo<T>().Which;

    private string NoteFile(string relPath) => Path.Combine(_subjectRoot, "notes", relPath);

    private string TrashDir(string entryId) => Path.Combine(_subjectRoot, ChildTrash.TrashDir, entryId);

    // Содержимое записи корзины: она лежит на своём исходном пути (он от корня предмета).
    private string StoredOf(ChildTrashEntry e) => File.ReadAllText(Path.Combine(TrashDir(e.Id), e.OriginalPath));

    // id несуществующей заметки того же источника — та же сборка, что у EncodeId в сервисе.
    private string NoteIdOf(string relPath) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_subjectProject}|{relPath}"))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private sealed class NoSessions : ISessionDirectory
    {
        public Session? GetById(string id) => null;
        public IReadOnlyCollection<Session> GetAll() => [];
        public string? ResolveOwnerId(Session s) => null;
        public Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId) =>
            Task.FromResult<IReadOnlyList<StoredMessage>>([]);
        public bool SetWikiMark(string sessionId, string? mark) => false;
    }

    private sealed class NoDify : IKnowledgeIndex
    {
        public bool IsConfigured => false;
        public Task<string> CreateDatasetAsync(string name, string permission = "only_me",
            string? description = null, string? indexingTechnique = null) => throw new NotSupportedException();
        public Task DeleteDocumentAsync(string datasetId, string documentId) => throw new NotSupportedException();
        public Task DeleteDatasetAsync(string datasetId) => throw new NotSupportedException();
        public Task RenameDatasetAsync(string datasetId, string newName) => throw new NotSupportedException();
        public Task<DifyDocumentInfo> IndexFileByTextAsync(string datasetId, string fileName, string content,
            List<string>? tags = null, IReadOnlyDictionary<string, string>? metadata = null) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<DifyRetrieveChunk>> RetrieveAsync(string datasetId, string query, int topK = 8,
            IReadOnlyList<KnowledgeMetadataFilter>? filters = null, string logic = "and") =>
            throw new NotSupportedException();
    }
}
