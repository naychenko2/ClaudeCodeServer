using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Комментарии к документам: verify-guard (посимвольная сверка, 409), вставка ^id,
// каскадный резолвер (^id → заголовки → цитата → сирота), статусы, merge-защита,
// исключение из графа. Работаем с личным vault (personal = источник заметок,
// ^id пишется); go/no-go этапа 0 — документ с кириллической wikilink.
public class NoteAnnotationTests : IDisposable
{
    private const string User = "u1";
    private readonly string _dir;
    private readonly string _vault;
    private readonly NotesService _sut;

    public NoteAnnotationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "note_ann_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _vault = Path.Combine(_dir, "notes", User);
        Directory.CreateDirectory(_vault);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(config);
        var projects = new ProjectManager(config, users, appSettings);
        _sut = new NotesService(projects, config, NullLogger<NotesService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // Документ с кириллицей и wikilink — тот самый go/no-go кейс панели
    private const string Doc = """
        # Архитектура

        Изоляция выполняется per-пользователь, подробнее в [[Песочница|песочнице]].

        ## Слой запуска

        Все шесть точек запуска идут через единый интерфейс IProcessLauncher.
        Драйверы стартуют процессы и пробрасывают stdio насквозь.

        ## Прерывание хода

        Убийство docker-клиента на хосте не трогает процесс внутри контейнера.
        """;

    private string WriteDoc(string name = "Архитектура", string? content = null)
    {
        var full = Path.Combine(_vault, name + ".md");
        File.WriteAllText(full, content ?? Doc);
        return full;
    }

    private static AnnotateSelection Sel(string doc, string text)
    {
        var start = doc.IndexOf(text, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "выделение должно существовать в документе");
        return new AnnotateSelection(start, start + text.Length, text);
    }

    // ─── Создание + verify-guard ─────────────────────────────────────────────

    [Fact]
    public void Annotate_СоздаётКомментарийИВставляетБлочныйЯкорь()
    {
        var docPath = WriteDoc();
        var note = _sut.Annotate(User, new AnnotateRequest(
            new AnnotateDocRef("personal", "Архитектура.md"),
            Sel(Doc, "Все шесть точек запуска идут через единый интерфейс IProcessLauncher."),
            Comment: "Перечислить все шесть точек списком", Tags: ["правка"]));

        note.Annotation.Should().NotBeNull();
        note.Annotation!.DocScope.Should().Be("personal");
        note.Annotation.DocPath.Should().Be("Архитектура.md");
        note.Annotation.Status.Should().Be("open");
        note.Annotation.BlockId.Should().NotBeNullOrEmpty();
        note.Annotation.AnchorHeading.Should().Be("Архитектура › Слой запуска");
        note.Tags.Should().Contain("правка");

        // ^id дописан в конец якорного блока документа (последняя строка параграфа)
        var doc = File.ReadAllText(docPath);
        doc.Should().Contain("stdio насквозь. ^" + note.Annotation.BlockId);
        // Заметка легла в папку «Комментарии»
        note.Path.Should().StartWith("Комментарии/");
    }

    [Fact]
    public void Annotate_НеверныеОфсетыНоУникальныйТекст_ПринимаетсяПоВхождению()
    {
        WriteDoc();
        var text = "Убийство docker-клиента на хосте";
        var note = _sut.Annotate(User, new AnnotateRequest(
            new AnnotateDocRef("personal", "Архитектура.md"),
            new AnnotateSelection(0, text.Length, text),   // офсеты заведомо неверные
            Comment: "проверить"));
        note.Annotation!.AnchorHeading.Should().Be("Архитектура › Прерывание хода");
    }

    [Fact]
    public void Annotate_ТекстНеСовпадаетИНеУникален_Отказ409()
    {
        WriteDoc();
        var act = () => _sut.Annotate(User, new AnnotateRequest(
            new AnnotateDocRef("personal", "Архитектура.md"),
            new AnnotateSelection(0, 10, "такого текста в документе нет")));
        act.Should().Throw<AnnotationConflictException>();

        // Документ не изменён — порча исключена конструктивно
        File.ReadAllText(Path.Combine(_vault, "Архитектура.md")).Should().Be(Doc);
    }

    [Fact]
    public void Annotate_ПовторноТотЖеБлок_ПереиспользуетЯкорь()
    {
        var docPath = WriteDoc();
        var sel = "Все шесть точек запуска идут через единый интерфейс IProcessLauncher.";
        var first = _sut.Annotate(User, new AnnotateRequest(
            new AnnotateDocRef("personal", "Архитектура.md"), Sel(Doc, sel), "раз"));
        var docAfter = File.ReadAllText(docPath);
        var second = _sut.Annotate(User, new AnnotateRequest(
            new AnnotateDocRef("personal", "Архитектура.md"), Sel(docAfter, sel), "два"));

        second.Annotation!.BlockId.Should().Be(first.Annotation!.BlockId);
        // ^id в документе ровно один
        System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(docPath), @"\^" + first.Annotation.BlockId)
            .Count.Should().Be(1);
    }

    // ─── Резолвер: каскад ────────────────────────────────────────────────────

    [Fact]
    public void GetDocAnnotations_ПоБлочномуЯкорю_ТочнаяПривязка()
    {
        WriteDoc();
        _sut.Annotate(User, new AnnotateRequest(
            new AnnotateDocRef("personal", "Архитектура.md"),
            Sel(Doc, "Драйверы стартуют процессы и пробрасывают stdio насквозь."), "коммент"));

        var anns = _sut.GetDocAnnotations(User, "personal", "Архитектура.md");
        anns.Should().ContainSingle();
        anns[0].State.Should().Be("exact");
        anns[0].Status.Should().Be("open");
        anns[0].Start.Should().BeGreaterThan(0);
        anns[0].Excerpt.Should().Be("коммент");
    }

    [Fact]
    public void GetDocAnnotations_ЯкорьСтёрт_НаходитПоЦитате()
    {
        var docPath = WriteDoc();
        var note = _sut.Annotate(User, new AnnotateRequest(
            new AnnotateDocRef("personal", "Архитектура.md"),
            Sel(Doc, "Все шесть точек запуска идут через единый интерфейс IProcessLauncher."), "к"));

        // Кто-то стёр ^id из документа — каскад падает на дословную цитату
        var doc = File.ReadAllText(docPath).Replace(" ^" + note.Annotation!.BlockId, "");
        File.WriteAllText(docPath, doc);

        var anns = _sut.GetDocAnnotations(User, "personal", "Архитектура.md");
        anns.Single().State.Should().Be("exact");
        anns.Single().Start.Should().BeGreaterThan(0);
    }

    [Fact]
    public void GetDocAnnotations_БлокПереписан_ДеградируетВРазделПоЗаголовку()
    {
        var docPath = WriteDoc();
        var note = _sut.Annotate(User, new AnnotateRequest(
            new AnnotateDocRef("personal", "Архитектура.md"),
            Sel(Doc, "Все шесть точек запуска идут через единый интерфейс IProcessLauncher."), "к"));

        // Блок переписан целиком (якорь и цитата исчезли), раздел остался.
        // ^id живёт в конце ПОСЛЕДНЕЙ строки параграфа («…stdio насквозь»).
        var doc = File.ReadAllText(docPath)
            .Replace("Все шесть точек запуска идут через единый интерфейс IProcessLauncher.",
                "Совсем другой текст блока.")
            .Replace("Драйверы стартуют процессы и пробрасывают stdio насквозь. ^" + note.Annotation!.BlockId,
                "И тут всё иначе.");
        File.WriteAllText(docPath, doc);

        _sut.GetDocAnnotations(User, "personal", "Архитектура.md")
            .Single().State.Should().Be("changed");
    }

    [Fact]
    public void GetDocAnnotations_ДокументУдалён_ЧестнаяСирота()
    {
        var docPath = WriteDoc();
        _sut.Annotate(User, new AnnotateRequest(
            new AnnotateDocRef("personal", "Архитектура.md"),
            Sel(Doc, "Убийство docker-клиента на хосте не трогает процесс внутри контейнера."), "к"));
        File.Delete(docPath);

        var anns = _sut.GetDocAnnotations(User, "personal", "Архитектура.md");
        anns.Single().State.Should().Be("orphan");
        anns.Single().Start.Should().Be(-1);
    }

    // ─── Статусы и фильтры ───────────────────────────────────────────────────

    [Fact]
    public void SetAnnotationStatus_ПереключаетИФильтруется()
    {
        WriteDoc();
        var note = _sut.Annotate(User, new AnnotateRequest(
            new AnnotateDocRef("personal", "Архитектура.md"),
            Sel(Doc, "Изоляция выполняется per-пользователь, подробнее в [[Песочница|песочнице]]."), "вопрос"));

        _sut.GetSummaries(User, null, "status:open").Should().ContainSingle();
        _sut.GetSummaries(User, null, "status:resolved").Should().BeEmpty();

        var updated = _sut.SetAnnotationStatus(User, note.Id, "resolved")!;
        updated.Annotation!.Status.Should().Be("resolved");
        _sut.GetSummaries(User, null, "status:open").Should().BeEmpty();
        _sut.GetSummaries(User, null, "status:resolved").Should().ContainSingle();
    }

    [Fact]
    public void SetAnnotationStatus_НаОбычнойЗаметке_Ошибка()
    {
        var note = _sut.Create(User, new CreateNoteRequest("Обычная", "тело"));
        var act = () => _sut.SetAnnotationStatus(User, note.Id, "resolved");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Фильтр_Orphaned_НаходитСироту()
    {
        var docPath = WriteDoc();
        _sut.Annotate(User, new AnnotateRequest(
            new AnnotateDocRef("personal", "Архитектура.md"),
            Sel(Doc, "Убийство docker-клиента на хосте не трогает процесс внутри контейнера."), "к"));
        _sut.GetSummaries(User, null, "status:orphaned").Should().BeEmpty();

        File.Delete(docPath);
        _sut.GetSummaries(User, null, "status:orphaned").Should().ContainSingle();
    }

    // ─── Merge-защита и граф ─────────────────────────────────────────────────

    [Fact]
    public void Update_ПерезаписьКонтентаБезAnnotates_ВосстанавливаетПривязку()
    {
        WriteDoc();
        var note = _sut.Annotate(User, new AnnotateRequest(
            new AnnotateDocRef("personal", "Архитектура.md"),
            Sel(Doc, "Драйверы стартуют процессы и пробрасывают stdio насквозь."), "коммент"));

        // Агент перезаписал контент целиком, потеряв системные поля
        var updated = _sut.Update(User, note.Id, new UpdateNoteRequest(Content: "# Новый текст\n\nБез frontmatter."))!;
        updated.Annotation.Should().NotBeNull("merge-защита обязана восстановить annotates");
        updated.Annotation!.DocPath.Should().Be("Архитектура.md");
        updated.Annotation.Status.Should().Be("open");
    }

    [Fact]
    public void GetGraph_СВключённымиКомментариями_ДаётУзлыИСвязи()
    {
        var root = CreateRootComment();
        var reply = _sut.Reply(User, root.Id, new ReplyRequest("ответ в тред"));

        var graph = _sut.GetGraph(User, includeAnnotations: true);
        var rootNode = graph.Nodes.Single(n => n.Id == root.Id);
        rootNode.Kind.Should().Be("comment");
        rootNode.Status.Should().Be("open");
        graph.Nodes.Single(n => n.Id == reply.Id).Kind.Should().Be("reply");

        // Комментарий → документ-заметка «Архитектура»; ответ → корневой комментарий
        var docId = _sut.GetSummaries(User, null, null).Single(n => n.Title == "Архитектура").Id;
        graph.Edges.Should().Contain(e => e.Source == root.Id && e.Target == docId);
        graph.Edges.Should().Contain(e => e.Source == reply.Id && e.Target == root.Id);

        // Дефолт (без параметра) — комментариев в графе по-прежнему нет
        _sut.GetGraph(User).Nodes.Should().NotContain(n => n.Kind != null);
    }

    [Fact]
    public void GetGraph_КомментарийНаФайлПроекта_ДаётПризрачныйУзелДокумента()
    {
        WriteDoc();
        var note = _sut.Annotate(User, new AnnotateRequest(
            new AnnotateDocRef("personal", "Архитектура.md"),
            Sel(Doc, "Убийство docker-клиента на хосте не трогает процесс внутри контейнера."), "к"));
        // Симулируем цель-файл вне заметок: перепишем annotates на несуществующую заметку
        _sut.RewriteAnnotationTargets(User, "personal", "Архитектура.md", "personal", "docs/чужой-файл.md");

        var graph = _sut.GetGraph(User, includeAnnotations: true);
        var ghost = graph.Nodes.Single(n => n.Kind == "doc");
        ghost.Ghost.Should().BeTrue();
        ghost.Title.Should().Be("чужой-файл.md");
        graph.Edges.Should().Contain(e => e.Source == note.Id && e.Target == ghost.Id);
    }

    [Fact]
    public void GetGraph_КомментарииНеПопадаютВГраф()
    {
        WriteDoc();
        _sut.Create(User, new CreateNoteRequest("Обычная", "тело"));
        _sut.Annotate(User, new AnnotateRequest(
            new AnnotateDocRef("personal", "Архитектура.md"),
            Sel(Doc, "Изоляция выполняется per-пользователь, подробнее в [[Песочница|песочнице]]."),
            "комментарий с [[Ссылкой]] внутри"));

        var graph = _sut.GetGraph(User);
        graph.Nodes.Should().NotContain(n => n.Title.StartsWith("комментарий"));
        // Ghost «Ссылкой» из тела комментария не всплывает; «Песочница» из самого
        // документа (он тоже заметка vault) — легитимный ghost и остаётся.
        graph.Nodes.Should().NotContain(n => n.Ghost && n.Title == "Ссылкой");
        graph.Edges.Should().ContainSingle();   // Архитектура → ghost:Песочница
    }

    // ─── Треды: ответы (этап 3) ──────────────────────────────────────────────

    private NoteDetail CreateRootComment()
    {
        WriteDoc();
        return _sut.Annotate(User, new AnnotateRequest(
            new AnnotateDocRef("personal", "Архитектура.md"),
            Sel(Doc, "Все шесть точек запуска идут через единый интерфейс IProcessLauncher."),
            Comment: "Корневой комментарий"));
    }

    [Fact]
    public void Reply_СоздаётсяИВозвращаетсяВТреде()
    {
        var root = CreateRootComment();
        var reply = _sut.Reply(User, root.Id, new ReplyRequest("Согласен, дополню список", ["обсудить"]));

        reply.Annotation.Should().NotBeNull();
        reply.Annotation!.IsReply.Should().BeTrue("annotates указывает на заметку-комментарий");
        reply.Annotation.DocPath.Should().Be(root.Path);

        var replies = _sut.GetReplies(User, root.Id);
        replies.Should().ContainSingle(r => r.NoteId == reply.Id);
        replies[0].Excerpt.Should().Be("Согласен, дополню список");
    }

    [Fact]
    public void Reply_НеПопадаетВАннотацииДокументаНоСчитается()
    {
        var root = CreateRootComment();
        _sut.Reply(User, root.Id, new ReplyRequest("ответ раз"));
        _sut.Reply(User, root.Id, new ReplyRequest("ответ два"));

        var anns = _sut.GetDocAnnotations(User, "personal", "Архитектура.md");
        anns.Should().ContainSingle("ответы не аннотируют документ");
        anns[0].Replies.Should().Be(2);
    }

    [Fact]
    public void Reply_НаОтвет_Ошибка()
    {
        var root = CreateRootComment();
        var reply = _sut.Reply(User, root.Id, new ReplyRequest("ответ"));
        var act = () => _sut.Reply(User, reply.Id, new ReplyRequest("ответ на ответ"));
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Reply_НеФильтруетсяСтатусами()
    {
        var root = CreateRootComment();
        _sut.Reply(User, root.Id, new ReplyRequest("ответ"));
        // status:open — только корневой комментарий, ответ статусом не считается
        _sut.GetSummaries(User, null, "status:open").Should().ContainSingle(n => n.Id == root.Id);
    }

    // ─── Перепривязка к новому выделению ─────────────────────────────────────

    [Fact]
    public void Repin_ПереносимПривязкуНаДругоеМесто()
    {
        var docPath = WriteDoc();
        var note = _sut.Annotate(User, new AnnotateRequest(
            new AnnotateDocRef("personal", "Архитектура.md"),
            Sel(Doc, "Все шесть точек запуска идут через единый интерфейс IProcessLauncher."), "к"));
        var oldBlockId = note.Annotation!.BlockId;

        // Блок переписан — привязка деградировала в «changed»
        var doc = File.ReadAllText(docPath)
            .Replace("Все шесть точек запуска идут через единый интерфейс IProcessLauncher.", "Совсем другой текст.")
            .Replace("Драйверы стартуют процессы и пробрасывают stdio насквозь. ^" + oldBlockId, "И тут иначе.");
        File.WriteAllText(docPath, doc);
        _sut.Create(User, new CreateNoteRequest("Кэш-инвалидация", "x"));
        _sut.GetDocAnnotations(User, "personal", "Архитектура.md").Single().State.Should().Be("changed");

        // Перепривязка к новому выделению
        var newSel = "Убийство docker-клиента на хосте не трогает процесс внутри контейнера.";
        var updated = _sut.RepinAnnotation(User, note.Id, Sel(File.ReadAllText(docPath), newSel));
        updated.Annotation!.BlockId.Should().NotBe(oldBlockId);
        updated.Annotation.AnchorQuote.Should().Be(newSel);
        updated.Annotation.AnchorHeading.Should().Be("Архитектура › Прерывание хода");
        updated.Annotation.Status.Should().Be("open", "статус перепривязка не трогает");
        _sut.GetDocAnnotations(User, "personal", "Архитектура.md").Single().State.Should().Be("exact");
    }

    [Fact]
    public void Repin_ТекстНеНайден_409БезИзменений()
    {
        WriteDoc();
        var note = _sut.Annotate(User, new AnnotateRequest(
            new AnnotateDocRef("personal", "Архитектура.md"),
            Sel(Doc, "Все шесть точек запуска идут через единый интерфейс IProcessLauncher."), "к"));
        var act = () => _sut.RepinAnnotation(User, note.Id,
            new AnnotateSelection(0, 10, "нет такого текста в документе"));
        act.Should().Throw<AnnotationConflictException>();
        _sut.GetDetail(User, note.Id)!.Annotation!.AnchorQuote
            .Should().Contain("Все шесть точек", "привязка не изменилась");
    }

    // ─── Миграция привязки при переносе (этап 4) ─────────────────────────────

    [Fact]
    public void Move_ЗаметкиДокумента_ПереписываетAnnotates()
    {
        // Документ — заметка личного vault; комментируем, переносим документ в папку
        var root = CreateRootComment();
        var docId = _sut.GetSummaries(User, null, null).Single(n => n.Title == "Архитектура").Id;
        _sut.Move(User, docId, "Архив");

        var anns = _sut.GetDocAnnotations(User, "personal", "Архив/Архитектура.md");
        anns.Should().ContainSingle("привязка переехала вместе с документом");
        anns[0].State.Should().Be("exact");
        anns[0].NoteId.Should().Be(root.Id);
        // По старому пути — пусто
        _sut.GetDocAnnotations(User, "personal", "Архитектура.md").Should().BeEmpty();
    }

    [Fact]
    public void RewriteAnnotationTargets_ТочечноИПоПрефиксу()
    {
        var root = CreateRootComment();
        // Точечный перенос (эмуляция rename документа через files API)
        _sut.RewriteAnnotationTargets(User, "personal", "Архитектура.md", "personal", "docs/Архитектура.md")
            .Should().Be(1);
        _sut.GetDetail(User, root.Id)!.Annotation!.DocPath.Should().Be("docs/Архитектура.md");
        // Префиксный перенос (эмуляция rename папки)
        _sut.RewriteAnnotationTargets(User, "personal", "docs", "personal", "архив", prefix: true)
            .Should().Be(1);
        _sut.GetDetail(User, root.Id)!.Annotation!.DocPath.Should().Be("архив/Архитектура.md");
    }

    [Fact]
    public void DocMissing_ВзводитсяПослеУдаленияДокумента()
    {
        var docPath = WriteDoc();
        _sut.Annotate(User, new AnnotateRequest(
            new AnnotateDocRef("personal", "Архитектура.md"),
            Sel(Doc, "Убийство docker-клиента на хосте не трогает процесс внутри контейнера."), "к"));

        _sut.GetSummaries(User, null, null)
            .Single(n => n.Annotation != null).Annotation!.DocMissing.Should().BeFalse();

        File.Delete(docPath);
        // Мутация через сервис — сбрасывает 2-секундный кэш модели (внешнее удаление
        // файла кэш не инвалидирует, тест не должен ждать TTL)
        _sut.Create(User, new CreateNoteRequest("Инвалидация кэша", "x"));
        _sut.GetSummaries(User, null, null)
            .Single(n => n.Annotation != null).Annotation!.DocMissing.Should().BeTrue();
    }

    // ─── Резолвер как чистая функция ─────────────────────────────────────────

    [Fact]
    public void ResolveAnchor_ЦитатаНеУникальна_НеПривязывается()
    {
        var doc = "# Раз\n\nповторяющийся текст достаточной длины для якоря\n\n# Два\n\nповторяющийся текст достаточной длины для якоря\n";
        var a = new NoteAnnotationInfo("personal", "x.md", "open", null,
            "повторяющийся текст достаточной длины для якоря", "Нет такого раздела");
        NotesService.ResolveAnchor(doc, a).State.Should().Be("orphan");
    }

    [Fact]
    public void ResolveAnchor_ЦитатаСДругимиПереносами_НаходитсяПослеНормализации()
    {
        var doc = "Текст с    разными\nпереносами и   пробелами внутри достаточной длины.\n";
        var a = new NoteAnnotationInfo("personal", "x.md", "open", null,
            "Текст с разными переносами и пробелами внутри достаточной длины.", null);
        var r = NotesService.ResolveAnchor(doc, a);
        r.State.Should().Be("exact");
        r.Start.Should().Be(0);
    }
}
