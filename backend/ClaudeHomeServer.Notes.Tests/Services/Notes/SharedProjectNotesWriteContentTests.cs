using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Notes;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// S2c-7: двери заметок общего проекта переведены с Write на WriteContent — редактор пишет
/// содержимое поимённо названным дверям (Create, Update, Delete, Move внутри источника,
/// Folder создать/удалить, Folder-move, LinkMention, annotate, repin, reply, status).
/// Путь права — ResolveRoot(…, WriteContent) в NotesService; корень — notes/ проекта
/// (арендатор), автор — только из sub (userId вызывающего).
///
/// Остаются закрытыми для не-владельца (defense NotesSideWritePathsTests):
/// tasks/promote|toggle|set-due (явный owner-Write в мосту), перенос между источниками,
/// уборка истёкших. Читатель (Read без WriteContent) — отказен на каждой переведенной двери.
///
/// Край HTTP (200/403/404) покрыт SharedProjectNotesHttpTests; здесь — границы на уровне
/// сервиса. Полный хост в этом тестовом проекте не поднимается по его построению.
/// </summary>
public class SharedProjectNotesWriteContentTests : IDisposable
{
    private const string ProjectNote = "Заметка проекта";
    private const string NoteBody = "# Заметка проекта\n\nТекст проектной заметки. Упоминание ЦелеваяТема.\n";

    private readonly string _dir;
    private readonly string _projectRoot;
    private readonly UserStore _users;
    private readonly ProjectManager _projects;
    private readonly IConfiguration _config;
    private readonly string _ownerId;
    private readonly string _editorId;
    private readonly string _readerId;
    private readonly string _projectId;

    public SharedProjectNotesWriteContentTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "s2c7_notes_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = Config();

        // При пустом users.json UserStore создаёт дефолтного admin — он и есть владелец проекта
        _users = new UserStore(_config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _ownerId = _users.GetFirst()!.Id;
        _editorId = _users.Add("s2c7-editor", "pwd", "user").Id;
        _readerId = _users.Add("s2c7-reader", "pwd", "user").Id;
        foreach (var id in new[] { _ownerId, _editorId, _readerId })
            _users.SetFeatureFlag(id, FeatureFlagKeys.SharedProjects, true);

        _projects = new ProjectManager(_config, _users, new AppSettingsService(_config));

        _projectRoot = Path.Combine(_dir, "project");
        Directory.CreateDirectory(Path.Combine(_projectRoot, "notes"));
        File.WriteAllText(Path.Combine(_projectRoot, "notes", ProjectNote + ".md"), NoteBody);
        _projectId = _projects.Create("Проект S2c-7", _projectRoot, _ownerId, "owner").Id;
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private IConfiguration Config() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dir, "projects.json"),
            [SharedProjectsGate.SwitchKey] = "true",
        }).Build();

    // Новый экземпляр сервиса на каждый тест: кэш модели живёт 2 с и на старом экземпляре
    // вернул бы модель, собранную до изменения состава участников.
    private NotesService Service() =>
        new(_projects, Config(), NullLogger<NotesService>.Instance, null,
            new ProjectAccessService(_projects, new NoSessions(), new SharedProjectsGate(_config, new FeatureFlagService(_users))));

    private void AddMember(string userId, ProjectRole role)
    {
        var members = _projects.GetById(_projectId)!.Members?.ToList() ?? [];
        members.Add(new ProjectMember { UserId = userId, Role = role, AddedBy = _ownerId });
        _projects.SetMembers(_projectId, members);
    }

    private string ProjectNoteId(NotesService sut) =>
        sut.GetSummaries(_ownerId, _projectId, null).Single(n => n.Title == ProjectNote).Id;

    private string ProjectFile(string rel) => Path.Combine(_projectRoot, "notes", rel);

    private static AnnotateRequest ProjectAnnotation(string projectId, string comment) =>
        new(new AnnotateDocRef(projectId, "notes/" + ProjectNote + ".md"),
            new AnnotateSelection(0, 0, "Текст проектной заметки"), Comment: comment);

    // --- Полный цикл редактора: создать → править → удалить, файл всё время у арендатора ---

    [Fact]
    public void Редактор_ПолныйЦиклЗаметки_ФайлУАрендатораИВладелецВидитКаждыйШаг()
    {
        AddMember(_editorId, ProjectRole.Editor);
        var sut = Service();

        var created = sut.Create(_editorId, new CreateNoteRequest("Заметка редактора", "Текст редактора", _projectId));
        created.Source.Should().Be(_projectId);

        // Хранилище — арендатор: файл в notes/ проекта, а не в data/notes/{editor}
        File.Exists(ProjectFile("Заметка редактора.md")).Should().BeTrue();
        File.Exists(Path.Combine(_dir, "notes", _editorId, "Заметка редактора.md"))
            .Should().BeFalse("ключ хранилища — арендатор, не вызывающий");

        // Владелец видит ту же заметку по тому же id (корень один на всех)
        var asOwner = sut.GetSummaries(_ownerId, _projectId, null).Single(n => n.Title == "Заметка редактора");
        asOwner.Id.Should().Be(created.Id);

        var updated = sut.Update(_editorId, created.Id, new UpdateNoteRequest(Content: "Правка редактора"));
        updated.Should().NotBeNull();
        File.ReadAllText(ProjectFile("Заметка редактора.md")).Should().Contain("Правка редактора");

        sut.Delete(_editorId, created.Id).Should().BeTrue();
        File.Exists(ProjectFile("Заметка редактора.md")).Should().BeFalse();
        // Полный цикл закончен: у владельца её больше нет. Свежий экземпляр: кэш модели
        // per-owner живёт 2 с и в одном экземпляре список владельца мог бы остаться
        // снимком ДО удаления — здесь проверяется состояние после полного цикла
        Service().GetSummaries(_ownerId, _projectId, null).Should().NotContain(n => n.Title == "Заметка редактора");
    }

    // --- Двери папок: создать / перенести / удалить — структура содержимого проекта ---

    [Fact]
    public void Редактор_СоздатьПеренестиУдалитьПапку_Допустимо()
    {
        AddMember(_editorId, ProjectRole.Editor);
        var sut = Service();

        sut.CreateFolder(_editorId, _projectId, "Архив");
        Directory.Exists(ProjectFile("Архив")).Should().BeTrue();

        var note = sut.Create(_editorId, new CreateNoteRequest("Заметка в Архиве", "текст", _projectId, Folder: "Архив"));
        File.Exists(ProjectFile("Архив/Заметка в Архиве.md")).Should().BeTrue();

        var moved = sut.MoveFolder(_editorId, _projectId, "Архив", "Архив2");
        File.Exists(ProjectFile("Архив2/Заметка в Архиве.md")).Should().BeTrue();
        // id меняется вместе с путём: маппинг старый → новый
        moved.Should().ContainSingle(m => m.OldId == note.Id && m.NewId != note.Id);

        sut.DeleteFolder(_editorId, _projectId, "Архив2").Should().Be(1);
        Directory.Exists(ProjectFile("Архив2")).Should().BeFalse();
    }

    // --- LinkMention: правка содержимого проектной заметки ---

    [Fact]
    public void Редактор_СвяжетУпоминаниеВПроектнойЗаметке_СсылкаЗаписана()
    {
        AddMember(_editorId, ProjectRole.Editor);
        var sut = Service();
        var id = ProjectNoteId(sut);

        var note = sut.LinkMention(_editorId, id, "ЦелеваяТема");

        note.Should().NotBeNull();
        File.ReadAllText(ProjectFile(ProjectNote + ".md")).Should().Contain("[[ЦелеваяТема]]");
    }

    // --- Двери аннотаций: комментарий/перепривязка/ответ/статус — тред в notes/ проекта ---

    [Fact]
    public void Редактор_АннотируетПроектныйДокумент_ТредВПапкеПроектаИВиденВладельцу()
    {
        AddMember(_editorId, ProjectRole.Editor);
        var sut = Service();

        var comment = sut.Annotate(_editorId, ProjectAnnotation(_projectId, "Комментарий редактора"));
        comment.Source.Should().Be(_projectId);
        File.Exists(ProjectFile("Комментарии/Комментарий редактора.md")).Should().BeTrue();

        var repinned = sut.RepinAnnotation(_editorId, comment.Id, new AnnotateSelection(0, 0, "Текст проектной заметки"));
        repinned.Should().NotBeNull();

        var reply = sut.Reply(_editorId, comment.Id, new ReplyRequest("Ответ редактора"));
        reply.Source.Should().Be(_projectId);

        var resolved = sut.SetAnnotationStatus(_editorId, comment.Id, "resolved");
        resolved.Should().NotBeNull();
        resolved!.Annotation!.Status.Should().Be("resolved");

        // Свежий экземпляр: кэш модели живёт 2 с, владелец обязан увидеть состояние ПОСЛЕ
        // всех правок редактора, а не снимок до них
        var asOwner = Service().GetDocAnnotations(_ownerId, _projectId, "notes/" + ProjectNote + ".md");
        asOwner.Should().ContainSingle(a => a.NoteId == comment.Id)
            .Which.Status.Should().Be("resolved");
        // Тред читается резолвером ответов (тот же id, тот же файл)
        Service().GetReplies(_ownerId, comment.Id).Should().ContainSingle(r => r.Title.Contains("Ответ редактора"));
    }

    // --- Читатель: отказен на каждой переведенной двери, файл проекта не тронут ---

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("delete")]
    [InlineData("move")]
    [InlineData("create-folder")]
    [InlineData("delete-folder")]
    [InlineData("move-folder")]
    [InlineData("link-mention")]
    [InlineData("annotate")]
    [InlineData("repin")]
    [InlineData("reply")]
    [InlineData("status")]
    public void Читатель_ОтказенНаКаждойДвериЗаметок_ФайлНеизменён(string op)
    {
        AddMember(_readerId, ProjectRole.Reader);
        var sut = Service();
        var id = ProjectNoteId(sut);

        // Для repin/reply/status нужен комментарий — его создаёт владелец (до попытки читателя)
        var commentId = op is "repin" or "reply" or "status"
            ? sut.Annotate(_ownerId, ProjectAnnotation(_projectId, "Комментарий владельца")).Id
            : "";

        Action act = op switch
        {
            "create" => () => sut.Create(_readerId, new CreateNoteRequest("Чужая", "тело", _projectId)),
            "update" => () => sut.Update(_readerId, id, new UpdateNoteRequest(Content: "правка читателя")),
            "delete" => () => sut.Delete(_readerId, id),
            "move" => () => sut.Move(_readerId, id, "Архив"),
            "create-folder" => () => sut.CreateFolder(_readerId, _projectId, "Архив"),
            "delete-folder" => () => sut.DeleteFolder(_readerId, _projectId, "Архив"),
            "move-folder" => () => sut.MoveFolder(_readerId, _projectId, "Архив", "Архив2"),
            "link-mention" => () => sut.LinkMention(_readerId, id, "ЦелеваяТема"),
            "annotate" => () => sut.Annotate(_readerId, ProjectAnnotation(_projectId, "Комментарий читателя")),
            "repin" => () => sut.RepinAnnotation(_readerId, commentId, new AnnotateSelection(0, 0, "Текст проектной заметки")),
            "reply" => () => sut.Reply(_readerId, commentId, new ReplyRequest("Ответ читателя")),
            _ => (Action)(() => sut.SetAnnotationStatus(_readerId, commentId, "resolved")),
        };

        // «Пишет» = право WriteContent: у читателя его нет (ProjectAccess.cs:89)
        act.Should().Throw<UnauthorizedAccessException>();

        // Ни одна попытка не тронула содержимое проекта
        File.ReadAllText(ProjectFile(ProjectNote + ".md")).Should().Contain("Текст проектной заметки.");
        Directory.Exists(ProjectFile("Архив")).Should().BeFalse();
        Directory.Exists(ProjectFile("Комментарии")).Should().Be(op is "repin" or "reply" or "status");
    }

    // --- Конкурентность: правка редактора против удаления владельцем одной заметки ---

    [Fact]
    public async Task Гонка_ПравкаРедактораПротивУдаленияВладельца_ОдинСогласованныйИсход()
    {
        AddMember(_editorId, ProjectRole.Editor);
        var sut = Service();
        var id = ProjectNoteId(sut);
        var full = ProjectFile(ProjectNote + ".md");
        var newContent = "# " + ProjectNote + "\n\n" + new string('б', 2_000_000) + "\n";

        // Старт обеих сторон по одному сигналу (без Task.Delay/Thread.Sleep). Запись обеих
        // сторон идёт через замок на id заметки (S2c-2): исход сериализован, полуреального
        // состояния нет.
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var upd = Task.Run(async () => { await start.Task; return sut.Update(_editorId, id, new UpdateNoteRequest(Content: newContent)); });
        var del = Task.Run(async () => { await start.Task; return sut.Delete(_ownerId, id); });
        start.SetResult();
        var updated = await upd;
        var deleted = await del;

        // Удаление существующей заметки владельцем всегда состоялось: при любом порядке
        // замок сериализовал операции, а файл на старте гонки был. Мутация «корень по id
        // вызывающего» (проектная ветка ResolveRoot → Path.Combine(_dataDir, "notes", userId))
        // бьёт ровно здесь: владелец резолвит корень по своему id, не находит файл проекта
        // и Delete возвращает false.
        deleted.Should().BeTrue();

        // Полуреального состояния нет: Delete сказал «удалено» — правка редактора не
        // возродила файл (тот же инвариант, что у «УборкаИUpdateОднойЗаметки»)
        File.Exists(full).Should().BeFalse("заметка удалена: проигравшая правка не возрождает файл");

        if (updated is not null)
        {
            // Правка редактора выиграла гонку: возвращённое состояние — его содержимое,
            // затем заметка удалена владельцем
            updated.Content.Should().Be(newContent);
        }

        // Проигравшая правка не записалась мимо проекта: в личном vault редактора
        // такой файла нет ни при каком исходе
        File.Exists(Path.Combine(_dir, "notes", _editorId, ProjectNote + ".md")).Should().BeFalse();
    }

    // --- Конкурентность (S2c-11): перенос редактора против удаления той же заметки владельцем ---

    /// <summary>
    /// S2c-11: перенос редактора против удаления той же заметки владельцем. Без замка на id
    /// перемещаемой заметки их критические секции переплетаются: удаление проверило файл,
    /// перенос его унёс, File.Delete на исчезнувшем не бросает — Delete говорит «true», а файл
    /// стоит в целевом месте («воскресшая» заметка); либо наоборот — File.Move по уже удалённому
    /// исходному, и это исключение.
    ///
    /// Окно переноса расширяется целевой папкой в N уровней: Directory.CreateDirectory обходит
    /// их все и держит Move между «исходный файл есть» и File.Move — этого окна хватает, чтобы
    /// вторая сторона успела проверить и удалить. Повторов несколько: без замка переплетение
    /// зависит от того, сойдутся ли два потока в этом окне, с замком — не зависит ни разу.
    /// Барьер — одно событие старта на обе стороны; ни Task.Delay, ни Thread.Sleep.
    /// </summary>
    [Fact]
    public async Task Гонка_ПереносРедактораПротивУдаленияВладельца_ФайлНеВоскресаетЯ()
    {
        AddMember(_editorId, ProjectRole.Editor);
        var sut = Service();
        const int attempts = 5, depth = 300;

        for (var i = 0; i < attempts; i++)
        {
            var title = $"Заметка гонки {i}";
            var body = $"# {title}\n\nТело гонки {i}.\n";
            var created = sut.Create(_ownerId, new CreateNoteRequest(title, body, _projectId));
            var original = ProjectFile(title + ".md");
            var folder = string.Join('/', Enumerable.Range(1, depth).Select(n => $"у{n}"));
            var target = ProjectFile($"{folder}/{title}.md");

            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var mv = Task.Run(async () => { await start.Task; return sut.Move(_editorId, created.Id, folder); });
            var del = Task.Run(async () => { await start.Task; return sut.Delete(_ownerId, created.Id); });
            start.SetResult();
            var moved = await mv;
            var deleted = await del;

            // Исход ровно один: исходного места нет ни при каком переплетении — заметка либо
            // удалена совсем, либо целиком перенесена.
            File.Exists(original).Should().BeFalse($"«{title}»: перенос или удаление — исходного места не остаётся");

            if (deleted)
            {
                // Выиграло удаление: проигравший перенос не создаёт целевой файл и не возрождает заметку
                moved.Should().BeNull($"«{title}»: заметка удалена, перенос проиграл гонку");
                File.Exists(target).Should().BeFalse($"«{title}»: удалённая заметка не воскресает в целевом месте");
            }
            else
            {
                // Выиграл перенос: заметка целиком в целевом месте; удаление проиграло — исходного
                // файла к его проверке уже не было
                moved.Should().NotBeNull($"«{title}»: перенос выиграл гонку");
                File.ReadAllText(target).Should().Be(body);
            }
        }
    }

    // --- Кэш модели (S2c-11): каждая запись редактора сбрасывает модель владельца-арендатора ---

    [Fact]
    public void ЗаписьРедактора_СбрасываетКэшМоделиВладельца_ТотЖеЭкземплярОтвечаетСвежим()
    {
        AddMember(_editorId, ProjectRole.Editor);
        var sut = Service();

        // Модель владельца прогрета до чужих записей (первый GetSummaries её кэширует): без
        // сброса его ключа каждая проверка ниже читала бы кэш (TTL 2 с) и не видела бы запись
        // редактора. Один и тот же экземпляр сервиса во всех шагах — «свежий экземпляр», на
        // котором держались прежние проверки здесь и не должно быть.
        var id = ProjectNoteId(sut);
        sut.GetDetail(_ownerId, id)!.Content.Should().Contain("Текст проектной заметки.");

        // Правка
        sut.Update(_editorId, id, new UpdateNoteRequest(Content: "# Заметка проекта\n\nПравка редактора."));
        sut.GetDetail(_ownerId, id)!.Content.Should().Contain("Правка редактора.");

        // Создание
        var created = sut.Create(_editorId, new CreateNoteRequest("Заметка редактора", "Текст редактора", _projectId));
        sut.GetSummaries(_ownerId, _projectId, null).Should().Contain(n => n.Id == created.Id);

        // Перенос: владелец видит новый id, а не прежний
        var moved = sut.Move(_editorId, created.Id, "Архив");
        moved.Should().NotBeNull();
        var newId = moved!.Id;
        var asOwner = sut.GetSummaries(_ownerId, _projectId, null).Single(n => n.Title == "Заметка редактора");
        asOwner.Id.Should().Be(newId);

        // Удаление
        sut.Delete(_editorId, newId).Should().BeTrue();
        sut.GetSummaries(_ownerId, _projectId, null).Should().NotContain(n => n.Id == newId);
    }

    // --- Конкурентность (S2c-12): комментарий редактора против правки владельца той же заметки ---

    /// <summary>
    /// S2c-12: «закрыть комментарий» редактора против правки того же комментария владельцем.
    /// Без замка на id комментария их критические секции переплетаются: Update перечисляет
    /// файл ДО статуса и пишет его поверх — статус редактора потерян молча (или наоборот:
    /// правка владельца). Окно гонки расширяет большое тело комментария: обе стороны
    /// читают/пишут ~мегабайты, без замка переплетение происходит в каждой попытке.
    /// С замком (тот же _writeGates, что у Update/Delete) порядок сериализован и при любом
    /// порядке сохраняются ОБЕ правки: merge-защита Update возвращает системные поля
    /// комментария из только что перечитанного файла, статус редактора остаётся на месте.
    /// Барьер — одно событие старта на обе стороны; ни Task.Delay, ни Thread.Sleep.
    /// </summary>
    [Fact]
    public async Task Гонка_СтатусРедактораПротивПравкиВладельца_ОбеПравкиСохранены()
    {
        AddMember(_editorId, ProjectRole.Editor);
        const int attempts = 3;

        for (var i = 0; i < attempts; i++)
        {
            // Свежий экземпляр на попытку — та же причина, что у теста репина
            var sut = Service();
            var title = $"Статус-гонка {i}";
            var comment = sut.Annotate(_ownerId, AnnotateOnProjectNote(title, new string('к', 2_000_000)));
            var full = ProjectFile("Комментарии/" + title + ".md");
            var newBody = "# " + title + "\n\n" + new string('б', 2_000_000) + "\n";

            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var st = Task.Run(async () => { await start.Task; return sut.SetAnnotationStatus(_editorId, comment.Id, "resolved"); });
            var up = Task.Run(async () => { await start.Task; return sut.Update(_ownerId, comment.Id, new UpdateNoteRequest(Content: newBody)); });
            start.SetResult();
            await st;
            await up;

            // Файл целый и ни одна правка не потеряна: содержимое владельца записано,
            // статус редактора — на месте. Мутация «SetAnnotationStatus без замка» бьёт
            // здесь: Update пишет поверх статуса, статус=open.
            File.Exists(full).Should().BeTrue();
            File.ReadAllText(full).Should().Contain("ббб", "правка владельца записана");

            var asOwner = Service().GetDocAnnotations(_ownerId, _projectId, "notes/" + ProjectNote + ".md");
            asOwner.Should().ContainSingle(a => a.NoteId == comment.Id)
                .Which.Status.Should().Be("resolved", "статус редактора не теряется под правкой владельца");
        }
    }

    /// <summary>
    /// S2c-12: repin редактора (перепривязка к другому выделению) против правки того же
    /// комментария владельцем. Тот же замок на id комментария: без него новый якорь или
    /// содержимое владельца теряются молча; с ним — сохраняются оба.
    /// </summary>
    [Fact]
    public async Task Гонка_РепинРедактораПротивПравкиВладельца_ОбеПравкиСохранены()
    {
        AddMember(_editorId, ProjectRole.Editor);
        const int attempts = 3;

        for (var i = 0; i < attempts; i++)
        {
            // Свежий экземпляр на попытку: кэш модели per-owner живёт 2 с, и на общем
            // экземпляре следующая попытка читала бы снимок до чужой записи (запись
            // владельца-арендатора инвалидирует владельца и арендатора, не не-арендатора)
            var sut = Service();
            var title = $"Репин-гонка {i}";
            var comment = sut.Annotate(_ownerId, AnnotateOnProjectNote(title, new string('к', 2_000_000)));
            var full = ProjectFile("Комментарии/" + title + ".md");
            var newBody = "# " + title + "\n\n" + new string('р', 2_000_000) + "\n";

            // Модель редактора прогрета ДО гонки (в UI заметка открыта): иначе её первый
            // скан столкнулся бы с 2MB-записью владельца — File.ReadAllText дал бы sharing
            // violation, файл был бы пропущен и repin ответил бы ложным «не найдена»
            // (фрагильность скана вне замка — тот же класс, что и потерянная правка;
            // здесь она не предмет теста).
            sut.GetDetail(_editorId, comment.Id).Should().NotBeNull();

            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var rp = Task.Run(async () =>
            {
                await start.Task;
                return sut.RepinAnnotation(_editorId, comment.Id, new AnnotateSelection(0, 0, "Упоминание ЦелеваяТема"));
            });
            var up = Task.Run(async () => { await start.Task; return sut.Update(_ownerId, comment.Id, new UpdateNoteRequest(Content: newBody)); });
            start.SetResult();
            await rp;
            await up;

            // Перепривязка (новый якорь) и содержимое владельца — оба в файле.
            var text = File.ReadAllText(full);
            text.Should().Contain("ррр", "правка владельца записана");
            text.Should().Contain("Упоминание ЦелеваяТема", "якорь перепривязки не потерян под правкой");
        }
    }

    // --- Кэш модели (S2c-12): смена статуса комментария редактором видна владельцу сразу ---

    [Fact]
    public void СтатусРедактора_ТотЖеЭкземплярОтвечаетВладельцуСвежим()
    {
        AddMember(_editorId, ProjectRole.Editor);
        var sut = Service();

        var comment = sut.Annotate(_ownerId, AnnotateOnProjectNote("Статус-кэш", "тело"));

        // Модель владельца прогрета ДО чужой записи (GetDetail её кэширует): без сброса его
        // ключа следующая проверка читала бы кэш (TTL 2 с) и не видела бы статус редактора.
        sut.GetDetail(_ownerId, comment.Id)!.Annotation!.Status.Should().Be("open");

        sut.SetAnnotationStatus(_editorId, comment.Id, "resolved").Should().NotBeNull();

        // Тот же экземпляр, сразу после записи: свежее состояние, а не снимок до неё
        var asOwner = sut.GetDetail(_ownerId, comment.Id);
        asOwner.Should().NotBeNull();
        asOwner!.Annotation!.Status.Should().Be("resolved");
    }

    // Комментарий к проектной заметке с явным заголовком и телом (для тестов гонки — большое
    // тело: оно расширяет критическую секцию обеих сторон)
    private AnnotateRequest AnnotateOnProjectNote(string title, string body) =>
        new(new AnnotateDocRef(_projectId, "notes/" + ProjectNote + ".md"),
            new AnnotateSelection(0, 0, "Текст проектной заметки"), Comment: body, Title: title);

    private sealed class NoSessions : ISessionDirectory
    {
        public Session? GetById(string id) => null;
        public IReadOnlyCollection<Session> GetAll() => [];
        public string? ResolveOwnerId(Session s) => null;
        public Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId) =>
            Task.FromResult<IReadOnlyList<StoredMessage>>([]);
    }
}
