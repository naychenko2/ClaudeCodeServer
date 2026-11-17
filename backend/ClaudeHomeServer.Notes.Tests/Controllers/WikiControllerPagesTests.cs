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

// B3b (ADR-038): REST правки страницы человеком — POST /api/wiki/pages/edit и /restore.
// Контракт: svc-токен — 403 до всего; флаг `llm-wiki` выключен — 409 wiki_unified_off;
// baseHash сверяется под замком раздела (гонка двух сохранений: один 200, один 409
// wiki_page_changed со свежей страницей); журнал — op edit|restore, actor user; «Вернуть мою»
// ссылается на исходную карточку и новой не создаёт. Контроллеры строятся напрямую
// (контракт действия, не транспорт) — фабрики хоста нет, метка Host не нужна.
// С LI-2 edit («Править» / новая страница) заводит вика заявителя: прежнего отказа «вики не
// заведена» этой ручке нет; svc (403) и выключенный флаг (409) — раньше заведения; restore —
// вне этой правки (без вики там нет того, что возвращать).
public class WikiControllerPagesTests : IDisposable
{
    private const string User = "u1";
    private const string Statement = "Сервер работает на порту пять тысяч.";
    private const string AiCard = "Карточка ИИ";

    private readonly string _dir;
    private readonly string _pages;
    private readonly IConfiguration _config;
    private readonly ProjectManager _projects;
    private readonly WikiKeeper _keeper;
    private readonly WikiJournalReader _journalReader;
    private readonly WikiApplyService _apply;
    private readonly string _key;
    private readonly string _pageKey;

    public WikiControllerPagesTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_pages_ctl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _projects = new ProjectManager(_config, users, new AppSettingsService(_config));
        _pages = WikiPaths.Pages(_dir, User);
        var notes = new NotesService(_projects, _config, NullLogger<NotesService>.Instance, null, flags: new Flags(true));
        _keeper = new WikiKeeper(notes, _config);
        // GET page читает журнал (overrides): имена источников там не нужны — каталог сессий не зовётся.
        _journalReader = new WikiJournalReader(notes, null!, _config);
        _apply = new WikiApplyService(notes, _keeper, new NoSpheres(), _config);

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

    // ─── 1. svc-токен — 403 до всего ───────────────────────────────────────

    [Fact]
    public async Task СервисныйТокен_Edit_403_СтраницаИПакетыНеТронуты()
    {
        var before = PageBytes();
        var res = await Controller(serviceToken: true).Edit(EditReq(Replace(PageText(), Statement, Statement + " Ещё.")));
        ((IStatusCodeActionResult)res).StatusCode.Should().Be(403);
        PageBytes().Should().Equal(before, "svc не доходит до записи");
        PackageFiles().Should().BeEmpty("пакет human создаёт только человек");
    }

    [Fact]
    public async Task СервисныйТокен_Restore_403_СтраницаИПакетыНеТронуты()
    {
        var before = PageBytes();
        var res = await Controller(serviceToken: true).Restore(
            new WikiPageRestoreRequest(_key, _pageKey, PageHash(), "что-то", "иное"));
        ((IStatusCodeActionResult)res).StatusCode.Should().Be(403);
        PageBytes().Should().Equal(before);
        PackageFiles().Should().BeEmpty();
    }

    // ─── 2. Флаг: llm-wiki выключен — 409 wiki_unified_off ───────────

    [Fact]
    public async Task Флаг_Выключен_Edit_409_wiki_unified_off()
    {
        var before = PageBytes();
        var res = await Controller(flags: new Flags(false)).Edit(EditReq(Replace(PageText(), Statement, Statement + " Ещё.")));
        AssertCode(res, 409, "wiki_unified_off");
        PageBytes().Should().Equal(before);
        PackageFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task Флаг_Выключен_Restore_409_wiki_unified_off()
    {
        var res = await Controller(flags: new Flags(false)).Restore(
            new WikiPageRestoreRequest(_key, _pageKey, PageHash(), "что-то", "иное"));
        AssertCode(res, 409, "wiki_unified_off");
        PackageFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task БезТокена_401_ЧужойРаздел_403()
    {
        ((IStatusCodeActionResult)await Controller(anonymous: true).Edit(EditReq("x"))).StatusCode.Should().Be(401);
        Directory.CreateDirectory(Path.Combine(_dir, "repo", "u2"));
        var foreign = _projects.Create("Чужой", Path.Combine(_dir, "repo", "u2"), "u2", "u2");
        var res = await Controller().Edit(new WikiPageEditRequest(
            "projects/" + foreign.Id, $"Проекты/{foreign.Id}/X.md", null, "x"));
        AssertCode(res, 403, "wiki_foreign");
    }

    // ─── 2b. LI-2: правка заводит вика заявителя ────────────────────────────

    [Fact]
    public async Task EditНезаведённойВикиЗаводитЕёИСтраницаСоздана()
    {
        // Мутация (критерий c2): убрать EnsureInitializedAsync в Edit — u3 остаётся без
        // pages/_schema.md, _apply отказывает, страницы нет: тест красный.
        var (key3, pageKey3) = OwnerProject("u3");
        var text = "---\ntype: topic\nsummary: про новое\naliases: []\nowns: []\nsources: []\nstatus: active\n---\n\n# Новая\n\n## Сводка\nНовая страница про порт пять тысяч.\n\n## Хронология\n\n## Противоречия\n\n## История\n";

        var res = await Controller(user: "u3").Edit(new WikiPageEditRequest(key3, pageKey3, null, text));

        Json(res, 200).GetProperty("status").GetString().Should().Be("applied");
        WikiPaths.IsInitialized(_dir, "u3").Should().BeTrue(
            "«править» / новая страница — действие человека: его вика создаётся сама");
        File.Exists(Path.Combine(WikiPaths.Pages(_dir, "u3"), pageKey3.Replace('/', Path.DirectorySeparatorChar)))
            .Should().BeTrue("действие прошло дальше заведения");
    }

    [Fact]
    public async Task Edit_СервисныйТокенИВыключенныйФлаг_ВикиНеЗаведены()
    {
        // svc (403) и выключенный флаг (409) — раньше заведения: у незаведённого владельца
        // ни байта в pages/. Мутация «заводить до проверок токена и флага» — красный.
        var svc = await Controller(serviceToken: true, user: "u4").Edit(new WikiPageEditRequest("common", "Личное/Нет.md", null, "x"));
        ((IStatusCodeActionResult)svc).StatusCode.Should().Be(403);
        AssertCode(await Controller(flags: new Flags(false), user: "u4")
            .Edit(new WikiPageEditRequest("common", "Личное/Нет.md", null, "x")), 409, "wiki_unified_off");

        WikiPaths.IsInitialized(_dir, "u4").Should().BeFalse();
        Directory.Exists(WikiPaths.Pages(_dir, "u4")).Should().BeFalse("ни байта в pages/");
    }

    // ─── 3. Правка: пакет human, сноска на карточку, журнал ────────────────

    [Fact]
    public async Task Edit_ИзменённоеУтверждение_ПрежнееВИстории_НоваяСноскаНаКарточкуHuman_ЖурналEditUser()
    {
        var edited = Statement.Replace("тысяч.", "тысяч один.");
        var res = await Controller().Edit(EditReq(Replace(PageText(), Statement, edited)));
        var body = Json(res, 200);
        body.GetProperty("status").GetString().Should().Be("applied");
        body.GetProperty("hash").GetString().Should().Be(PageHash());

        var page = WikiPage.Parse(PageText()).Model!;
        page.Sections.Single(s => s.Name == "Сводка").Lines.Should().ContainSingle()
            .Which.Should().Be(edited + " [^s2]");
        page.Sections.Single(s => s.Name == "История").Lines.Should().ContainSingle()
            .Which.Should().Be(Statement + " [^s1]", "прежняя формулировка дословно, со своей сноской");
        var cardTitle = page.Footnotes[1].Target.Text;
        page.Footnotes[1].Quote.Should().Be(edited);

        // Карточка правки: origin human, лежит в Источниках раздела, неизменяема, в манифесте.
        var cards = CardFiles();
        cards.Should().HaveCount(2, "карточка ИИ и карточка правки");
        var humanCard = WikiPage.Parse(File.ReadAllText(cards.Single(c => Path.GetFileNameWithoutExtension(c) == cardTitle))).Model!;
        humanCard.Frontmatter.Origin.Should().Be("human");
        humanCard.Frontmatter.Hash.Should().Be(WikiManifest.Sha256Hex(edited));

        var line = LastJournalLine();
        line.GetProperty("op").GetString().Should().Be("edit");
        line.GetProperty("actor").GetString().Should().Be("user");
        line.GetProperty("pages").EnumerateArray().Select(p => p.GetString())
            .Should().Contain(_pageKey);

        // Пакет — автор human, применён; файлы согласованы с манифестом (дрейфа нет).
        var pkg = WikiPackageStore.List(WikiPaths.Root(_dir, User)).Packages.Single();
        pkg.Author.Should().Be("human");
        pkg.Action.Should().Be("edit");
        pkg.Status.Should().Be(WikiPackage.StatusApplied);
        PageJson().GetProperty("drift").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Edit_ФормаUnicodeСтрокиНеПравка_ВИсториюТолькоИзменённая_СноскиHumanУНеизменённойНет()
    {
        const string other = "Всё идёт через шлюз, это ёжик.";
        var withOther = PageText().Replace(Statement + " [^s1]", Statement + " [^s1]\n" + other + " [^s1]");
        (await _keeper.WritePageAsync(User, _key, _pageKey, withOther)).Status.Should().Be(WikiOpStatus.Applied);

        var edited = Statement.Replace("тысяч.", "тысяч один.");
        var now = PageText().Replace(Statement, edited).Replace(other, other.Normalize(NormalizationForm.FormD));
        Json(await Controller().Edit(EditReq(now)), 200).GetProperty("status").GetString().Should().Be("applied");

        var page = WikiPage.Parse(PageText()).Model!;
        page.Sections.Single(s => s.Name == "История").Lines.Should().ContainSingle()
            .Which.Should().Be(Statement + " [^s1]");
        page.Sections.Single(s => s.Name == "Сводка").Lines
            .Single(l => l.StartsWith("Всё", StringComparison.Ordinal)).Should().EndWith("[^s1]", "ИИ-утверждение не получает сноску human");
    }

    [Fact]
    public async Task Edit_НовоеУтверждение_ДобавляетсяВСекциюСоСноскойНаКарточку()
    {
        var res = await Controller().Edit(EditReq(Replace(PageText(), "## Хронология\n", "## Хронология\nЗапустили в ноябре две тысячи двадцать шестого.\n")));
        Json(res, 200).GetProperty("status").GetString().Should().Be("applied");
        var page = WikiPage.Parse(PageText()).Model!;
        page.Sections.Single(s => s.Name == "Хронология").Lines.Should().ContainSingle()
            .Which.Should().Be("Запустили в ноябре две тысячи двадцать шестого. [^s2]");
        page.Sections.Single(s => s.Name == "Сводка").Lines.Should().ContainSingle("прежнее не тронуто");
        LastJournalLine().GetProperty("op").GetString().Should().Be("edit");
    }

    [Fact]
    public async Task Edit_УбранноеУтверждение_ВИсториюСПричиной_СамоУтверждениеНеУдалено()
    {
        var res = await Controller().Edit(EditReq(Replace(PageText(), Statement + " [^s1]\n", "")));
        Json(res, 200).GetProperty("status").GetString().Should().Be("applied");
        var page = WikiPage.Parse(PageText()).Model!;
        page.Sections.Single(s => s.Name == "Сводка").Lines.Should().BeEmpty();
        page.Sections.Single(s => s.Name == "История").Lines.Should().ContainSingle()
            .Which.Should().Be(Statement + " [^s1]");
        CardFiles().Should().HaveCount(1, "человек ничего не написал — карточки правки нет");
        var pkg = WikiPackageStore.List(WikiPaths.Root(_dir, User)).Packages.Single();
        pkg.Operations.Single().Reason.Should().Be("снято человеком");
    }

    [Fact]
    public async Task Edit_НовойСтраницы_БезBaseHash_Создаёт()
    {
        var newKey = _pageKey.Replace("Знание.md", "Новая.md");
        var text = "---\ntype: topic\nsummary: про новое\naliases: []\nowns: []\nsources: []\nstatus: active\n---\n\n# Новая\n\n## Сводка\nНовая страница про порт пять тысяч.\n\n## Хронология\n\n## Противоречия\n\n## История\n";
        var res = await Controller().Edit(new WikiPageEditRequest(_key, newKey, null, text));
        Json(res, 200).GetProperty("status").GetString().Should().Be("applied");
        var page = WikiPage.Parse(File.ReadAllText(Path.Combine(_pages, newKey.Replace('/', Path.DirectorySeparatorChar)))).Model!;
        page.Frontmatter.Summary.Should().Be("про новое");
        page.Sections.Single(s => s.Name == "Сводка").Lines.Should().ContainSingle()
            .Which.Should().EndWith("[^s1]");
        LastJournalLine().GetProperty("op").GetString().Should().Be("edit");

        // Повторное создание той же страницы без baseHash — не перезапись: страница уже есть.
        AssertCode(await Controller().Edit(new WikiPageEditRequest(_key, newKey, null, text)), 409, "wiki_page_changed");
    }

    [Fact]
    public async Task Edit_БезИзменений_200Unchanged_ПакетаНет()
    {
        var res = await Controller().Edit(EditReq(PageText()));
        var body = Json(res, 200);
        body.GetProperty("status").GetString().Should().Be("unchanged");
        PackageFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task Edit_НарушениеГейта_400СРусскойПричиной_СтраницаИПакетыНеТронуты()
    {
        var before = PageBytes();
        var res = await Controller().Edit(EditReq(Replace(PageText(), Statement, Statement + " См. [[Несуществующая страница]].")));
        var body = Json(res, 400);
        body.GetProperty("code").GetString().Should().Be("wiki_edit_invalid");
        body.GetProperty("message").GetString().Should().Contain("Несуществующая страница");
        PageBytes().Should().Equal(before);
        PackageFiles().Should().BeEmpty("сухой прогон до записи: отказанных пакетов нет");
        CardFiles().Should().HaveCount(1);
    }

    [Fact]
    public async Task Edit_ПравкаКодовогоПоляШапки_400()
    {
        var res = await Controller().Edit(EditReq(Replace(PageText(), "status: active", "status: stale")));
        var body = Json(res, 400);
        body.GetProperty("message").GetString().Should().Contain("status");
    }

    // ─── 3a. SV1-Г: issues с кодами в том же 400 (violations прежние) ────────

    private JsonElement FirstIssue(JsonElement body)
    {
        body.GetProperty("code").GetString().Should().Be("wiki_edit_invalid");
        body.GetProperty("violations").GetArrayLength().Should().BeGreaterThan(0, "violations остаются как были");
        body.GetProperty("issues").GetArrayLength().Should().Be(body.GetProperty("violations").GetArrayLength());
        return body.GetProperty("issues")[0];
    }

    [Fact]
    public async Task Edit_ССылкойВЧужойПроект_issues_wiki_link_sideways_ИмяПроектаБезGuid()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "repo", User, "Соседний"));
        var other = _projects.Create("Соседний", Path.Combine(_dir, "repo", User, "Соседний"), User, User);
        var link = $"Проекты/{other.Id}/Чужая страница";
        var res = await Controller().Edit(EditReq(Replace(PageText(), Statement, Statement + $" См. [[{link}]].")));
        var issue = FirstIssue(Json(res, 400));
        issue.GetProperty("code").GetString().Should().Be("wiki_link_sideways");
        issue.GetProperty("link").GetString().Should().Be(link);
        issue.GetProperty("page").GetString().Should().Be(_pageKey);
        var message = issue.GetProperty("message").GetString()!;
        message.Should().Contain("Соседний");
        message.Should().NotContain(other.Id);
    }

    [Fact]
    public async Task Edit_СсылкаБезНазначения_issues_wiki_link_unknown()
    {
        var res = await Controller().Edit(EditReq(Replace(PageText(), Statement, Statement + " См. [[Несуществующая страница]].")));
        var issue = FirstIssue(Json(res, 400));
        issue.GetProperty("code").GetString().Should().Be("wiki_link_unknown");
        issue.GetProperty("link").GetString().Should().Be("Несуществующая страница");
    }

    [Fact]
    public async Task Edit_СсылкаСНеразобраннымПрефиксом_issues_wiki_link_prefix()
    {
        var res = await Controller().Edit(EditReq(Replace(PageText(), Statement, Statement + " См. [[нет/раздела/Страница]].")));
        var issue = FirstIssue(Json(res, 400));
        issue.GetProperty("code").GetString().Should().Be("wiki_link_prefix");
        issue.GetProperty("link").GetString().Should().Be("нет/раздела/Страница");
    }

    [Fact]
    public async Task Edit_КодовоеПолеШапки_issues_wiki_header_field()
    {
        var res = await Controller().Edit(EditReq(Replace(PageText(), "status: active", "status: stale")));
        FirstIssue(Json(res, 400)).GetProperty("code").GetString().Should().Be("wiki_header_field");
    }

    [Fact]
    public async Task Edit_СменаЗаголовка_issues_wiki_title_change()
    {
        var res = await Controller().Edit(EditReq(Replace(PageText(), "# Знание", "# Иное")));
        FirstIssue(Json(res, 400)).GetProperty("code").GetString().Should().Be("wiki_title_change");
    }

    [Fact]
    public async Task Edit_СноскаВЗаметках_issues_wiki_notes_footnote()
    {
        var res = await Controller().Edit(EditReq(PageText().TrimEnd() + "\n\n## Заметки\nМой комментарий [^s1]\n"));
        FirstIssue(Json(res, 400)).GetProperty("code").GetString().Should().Be("wiki_notes_footnote");
    }

    [Fact]
    public async Task Edit_НеразобранныйТекст_issues_wiki_parse()
    {
        var res = await Controller().Edit(EditReq("просто текст без шапки и заголовка"));
        FirstIssue(Json(res, 400)).GetProperty("code").GetString().Should().Be("wiki_parse");
    }

    // ─── 4. Конкурентность: baseHash под замком раздела ────────────────────

    [Fact]
    public async Task Edit_ДваСохраненияПоОдномуBaseHash_Параллельно_Один200_Один409()
    {
        var hash = PageHash();
        var a = Replace(PageText(), Statement, Statement.Replace("тысяч.", "тысяч один."));
        var b = Replace(PageText(), Statement, Statement.Replace("тысяч.", "тысяч два."));

        var results = await Task.WhenAll(
            Task.Run(() => Controller().Edit(new WikiPageEditRequest(_key, _pageKey, hash, a))),
            Task.Run(() => Controller().Edit(new WikiPageEditRequest(_key, _pageKey, hash, b))));

        var codes = results.Select(r => ((IStatusCodeActionResult)r).StatusCode).OrderBy(c => c).ToList();
        codes.Should().Equal(new int?[] { 200, 409 }, "первое проходит под блокировкой раздела, второе видит чужую запись");
        var loser = results.Single(r => ((IStatusCodeActionResult)r).StatusCode == 409);
        var body = Json(loser, 409);
        body.GetProperty("code").GetString().Should().Be("wiki_page_changed");
        body.GetProperty("page").GetProperty("hash").GetString().Should().Be(PageHash(), "409 несёт свежую версию");
        body.GetProperty("page").GetProperty("content").GetString().Should().Be(PageText());
        WikiPackageStore.List(WikiPaths.Root(_dir, User)).Packages.Should().ContainSingle("проигравший пакета не создал");
    }

    [Fact]
    public async Task Edit_ПакетИИПрименилсяПослеОткрытияРедактора_409СоСвежейВерсией()
    {
        var openedHash = PageHash();
        var opened = PageText();
        // «Пакет ИИ»: страница изменена Хранителем после открытия редактора.
        var aiText = Replace(opened, "## Хронология\n", "## Хронология\nИИ дописал факт про порт.[^s1]\n");
        (await _keeper.WritePageAsync(User, _key, _pageKey, aiText)).Status.Should().Be(WikiOpStatus.Applied);

        var res = await Controller().Edit(new WikiPageEditRequest(
            _key, _pageKey, openedHash, Replace(opened, Statement, Statement + " Ещё.")));
        var body = Json(res, 409);
        body.GetProperty("code").GetString().Should().Be("wiki_page_changed");
        body.GetProperty("page").GetProperty("content").GetString().Should().Be(aiText);
        PageText().Should().Be(aiText, "правка человека поверх чужой не записана");
    }

    // ─── 5. «Вернуть мою» ──────────────────────────────────────────────────

    [Fact]
    public async Task Restore_ВозвращаетФормулировкуЧеловека_НаИсходнуюКарточку_НовойНеСоздаёт()
    {
        const string human = "Человек утверждает, что порт — пять тысяч.";
        const string ai = "ИИ утверждает иное про порт.";
        // Человек добавляет утверждение: появляется карточка правки.
        Json(await Controller().Edit(EditReq(Replace(PageText(), "## Хронология\n", "## Хронология\n" + human + "\n"))), 200);
        var humanPage = WikiPage.Parse(PageText()).Model!;
        var humanCard = humanPage.Footnotes[1].Target.Text;
        CardFiles().Should().HaveCount(2);

        // ИИ заменяет формулировку человека: действующая — ИИ, человеческая — в «Истории» (как B4b).
        var m = humanPage;
        var aiPage = new WikiPage
        {
            Title = m.Title,
            Frontmatter = m.Frontmatter,
            Sections = m.Sections.Select(sec => sec.Name switch
            {
                "Хронология" => new WikiPage.Section { Name = sec.Name, Lines = [ai + " [^s3]"] },
                "История" => new WikiPage.Section { Name = sec.Name, Lines = [human + " [^s2]"] },
                _ => sec,
            }).ToList(),
            Footnotes = [.. m.Footnotes, new WikiPage.WikiFootnote(new WikiPage.Target(true, AiCard), "ИИ утверждает иное про порт в источнике")],
        }.Render();
        (await _keeper.WritePageAsync(User, _key, _pageKey, aiPage)).Status.Should().Be(WikiOpStatus.Applied);
        var swapped = WikiPage.Parse(PageText()).Model!;
        swapped.Sections.Single(s => s.Name == "Хронология").Lines.Should().ContainSingle().Which.Should().StartWith(ai);

        var res = await Controller().Restore(new WikiPageRestoreRequest(_key, _pageKey, PageHash(), ai, human));
        Json(res, 200).GetProperty("status").GetString().Should().Be("applied");

        var restored = WikiPage.Parse(PageText()).Model!;
        restored.Sections.Single(s => s.Name == "Хронология").Lines.Should().ContainSingle()
            .Which.Should().Be(human + " [^s2]", "возвращена та же строка с прежней сноской на исходную карточку");
        restored.Sections.Single(s => s.Name == "История").Lines.Should().Contain(ai + " [^s3]")
            .And.NotContain(l => l.StartsWith(human));
        restored.Footnotes.Should().HaveCount(swapped.Footnotes.Count, "новой сноски нет");
        restored.Footnotes[1].Target.Text.Should().Be(humanCard);
        CardFiles().Should().HaveCount(2, "новой карточки «Вернуть мою» не создаёт");

        var line = LastJournalLine();
        line.GetProperty("op").GetString().Should().Be("restore");
        line.GetProperty("actor").GetString().Should().Be("user");
        WikiPackageStore.List(WikiPaths.Root(_dir, User)).Packages
            .Should().Contain(p => p.Action == "restore" && p.Author == "human");
    }

    [Fact]
    public async Task Restore_ФормулировкаНеЧеловека_400_СтраницаНеТронута()
    {
        // В «Истории» утверждение ИИ (сноска на карточку ИИ): «Вернуть мою» возвращает только своё.
        var text = Replace(PageText(), "## Хронология\n", "## Хронология\nАктуальное про порт. [^s2]\n");
        text = Replace(text, "## История\n", $"## История\n{Statement} [^s1]\n");
        text = text.TrimEnd() + $"\n[^s2]: [[{AiCard}]] — «Актуальное про порт в источнике»\n";
        (await _keeper.WritePageAsync(User, _key, _pageKey, text)).Status.Should().Be(WikiOpStatus.Applied);
        var before = PageBytes();

        var res = await Controller().Restore(new WikiPageRestoreRequest(_key, _pageKey, PageHash(), "Актуальное про порт.", Statement));
        var body = Json(res, 400);
        body.GetProperty("message").GetString().Should().Contain("человека");
        PageBytes().Should().Equal(before);
        PackageFiles().Should().BeEmpty();
    }

    // ─── 6. «Связанные» (Q1-S6b): поле related в GET /api/wiki/page ────────────

    [Fact]
    public async Task GET_Page_КарточкаИсточника_RelatedГруппаSource_ПустыхГруппНет()
    {
        // Фикстура: у страницы одна сноска на карточку источника того же раздела.
        var body = Json(PageResult(), 200);
        var related = body.GetProperty("related");
        related.EnumerateArray().Select(g => g.GetProperty("kind").GetString())
            .Should().Equal(new[] { "source" },
                "единственная связь фикстуры — общая карточка источника; пустых групп не отдаём");
        var item = related[0].GetProperty("items")[0];
        item.GetProperty("pageKey").GetString().Should().Be(CardKey);
        item.GetProperty("title").GetString().Should().Be(AiCard);
        item.GetProperty("type").GetString().Should().Be("source");
        foreach (var g in related.EnumerateArray())
            g.GetProperty("items").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GET_Page_СоседнююСтраницуВУчётЧтенийНеЗовёт()
    {
        // Инвариант шага (c2): чтение соседей для «Связанных» ни на одном пути не попадает
        // в учёт чтений (ADR-039 §7). Мутация «зовёт учёт чтения для соседей» в Page —
        // счётчик карточки сдвигается, тест красный.
        var root = WikiPaths.Root(_dir, User);
        var now = DateTimeOffset.UnixEpoch;
        WikiReadsStore.RecordMany(root, [(CardKey, WikiReadKind.Human)], now).Should().BeTrue();
        var cardBefore = WikiReadsStore.Read(root).Pages[CardKey];

        var body = Json(PageResult(), 200);
        RelatedPageKeys(body).Should().Contain(CardKey,
            "сосед фикстуры действительно в related — проверка ниже идёт по реальному соседу, не по пустому списку");

        var after = WikiReadsStore.Read(root).Pages;
        after[CardKey].Should().Be(cardBefore, "счётчики соседа не двинулись: его чтение — фон показа");
        after.ContainsKey(_pageKey).Should().BeTrue();
        after[_pageKey].Human.Should().Be(1, "цель учитывается как раньше (R4) — исключение только про соседей");
    }

    [Fact]
    public async Task GET_Page_НеразобранныйСоседПропущен_Ответ200_РеальныйСоседОстаётся()
    {
        // Сосед полузаписан/битый (не разбирается как страница): он выпадает из related,
        // страница отвечена как обычно — 200 без исключения. Выпадение локально: реальная
        // связь не исчезает вместе с битой.
        var brokenKey = _pageKey.Replace("Знание.md", "Битая.md");
        File.WriteAllText(Path.Combine(_pages, brokenKey.Replace('/', Path.DirectorySeparatorChar)),
            "полузаписанный текст без шапки и заголовка");

        var body = Json(PageResult(), 200); // сам 200 проверяет Json(..., 200)
        RelatedPageKeys(body).Should().NotContain(brokenKey, "битый сосед не часть «Связанных»");
        RelatedPageKeys(body).Should().Contain(CardKey, "реальная связь осталась");
    }

    // ─── Помощники ─────────────────────────────────────────────────────────


    // Карточка источника фикстуры: сосед страницы по сноске — он же цель «Связанных» (source).
    private string CardKey => _pageKey.Replace("Знание.md", $"Источники/{AiCard}.md");

    // GET /api/wiki/page напрямую (тот же путь, что у PageJson, но с web-сериализацией —
    // как отдаёт MVC): поле related и его группы проверяются по контрактным именам.
    private IActionResult PageResult() =>
        new WikiController(_keeper, _projects, new NoSpheres(), null!, _journalReader, new Flags(true), _config, null!, null!, null!, null!)
        {
            ControllerContext = Ctx(),
        }.Page(_key, _pageKey);

    private static IEnumerable<string> RelatedPageKeys(JsonElement body) =>
        body.GetProperty("related").EnumerateArray()
            .SelectMany(g => g.GetProperty("items").EnumerateArray())
            .Select(i => i.GetProperty("pageKey").GetString()!);

    private async Task SeedAsync()
    {
        await _keeper.EnsureInitializedAsync(User);
        var card = $"---\ntype: source\norigin: note\nref: note:n1\nhash: abc\ncaptured: 2026-11-01T10:00:00.0000000+00:00\naliases: []\nowns: []\nsources: []\nstatus: active\n---\n\n# {AiCard}\n";
        var cardKey = _pageKey.Replace("Знание.md", $"Источники/{AiCard}.md");
        (await _keeper.WritePageAsync(User, _key, cardKey, card)).Status.Should().Be(WikiOpStatus.Applied);
        var page = $"---\ntype: topic\naliases: []\nowns: []\nsources: []\nstatus: active\n---\n\n# Знание\n\n## Сводка\n{Statement} [^s1]\n\n## Хронология\n\n## Противоречия\n\n## История\n\n[^s1]: [[{AiCard}]] — «Сервер слушает порт пять тысяч в конфиге»\n";
        (await _keeper.WritePageAsync(User, _key, _pageKey, page)).Status.Should().Be(WikiOpStatus.Applied);
    }

    // Проект другого владельца и путь новой страницы в его разделе: точки LI-2 проверяются на
    // владельце без заведённой вики (у этого стенда заведена только u1).
    private (string Key, string PageKey) OwnerProject(string owner)
    {
        var root = Path.Combine(_dir, "repo", owner, "Проект");
        Directory.CreateDirectory(root);
        var p = _projects.Create("Проект " + owner, root, owner, owner);
        return ("projects/" + p.Id, $"Проекты/{p.Id}/Новая.md");
    }

    private string PageFile => Path.Combine(_pages, _pageKey.Replace('/', Path.DirectorySeparatorChar));
    private string PageText() => File.ReadAllText(PageFile);
    private byte[] PageBytes() => File.ReadAllBytes(PageFile);
    private string PageHash() => Convert.ToHexString(SHA256.HashData(PageBytes())).ToLowerInvariant();

    private WikiPageEditRequest EditReq(string content) => new(_key, _pageKey, PageHash(), content);

    private static string Replace(string text, string from, string to)
    {
        text.Should().Contain(from, "подстановка в тексте страницы теста");
        return text.Replace(from, to);
    }

    private string[] PackageFiles()
    {
        var dir = Path.Combine(WikiPaths.Keeper(_dir, User), "packages");
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json") : [];
    }

    private string[] CardFiles()
    {
        var dir = Path.Combine(Path.GetDirectoryName(PageFile)!, WikiSourceCapture.SourceDirName);
        return Directory.GetFiles(dir, "*.md");
    }

    private JsonElement LastJournalLine()
    {
        var path = Path.Combine(WikiPaths.Keeper(_dir, User), "log.jsonl");
        return JsonDocument.Parse(File.ReadAllLines(path).Last(l => l.Length > 0)).RootElement.Clone();
    }

    private JsonElement PageJson()
    {
        var res = new WikiController(_keeper, _projects, new NoSpheres(), null!, _journalReader, new Flags(true), _config, null!, null!, null!, null!)
        {
            ControllerContext = Ctx(),
        }.Page(_key, _pageKey);
        return JsonDocument.Parse(JsonSerializer.Serialize(((OkObjectResult)res).Value)).RootElement.Clone();
    }

    private static JsonElement Json(IActionResult result, int status)
    {
        var obj = result.Should().BeAssignableTo<ObjectResult>().Which;
        obj.StatusCode.Should().Be(status, JsonSerializer.Serialize(obj.Value));
        return JsonDocument.Parse(JsonSerializer.Serialize(obj.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web))).RootElement.Clone();
    }

    private static void AssertCode(IActionResult result, int status, string code) =>
        Json(result, status).GetProperty("code").GetString().Should().Be(code);

    private static ControllerContext Ctx(bool serviceToken = false, bool anonymous = false, string user = User)
    {
        var http = new DefaultHttpContext();
        if (!anonymous)
        {
            var claims = new List<Claim> { new("sub", user) };
            if (serviceToken) claims.Add(new("typ", "svc"));
            http.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        }
        return new ControllerContext { HttpContext = http };
    }

    private WikiPagesController Controller(bool serviceToken = false, Flags? flags = null, bool anonymous = false, string user = User) =>
        new(_apply, _projects, _keeper, flags ?? new Flags(true))
        {
            ControllerContext = Ctx(serviceToken, anonymous, user),
        };

    // Один флаг: llm-wiki; остальные ключи выключены.
    private sealed class Flags(bool llmWiki) : IFeatureFlagGate
    {
        public bool IsEnabled(string userId, string key) => key == FeatureFlagKeys.LlmWiki && llmWiki;
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
