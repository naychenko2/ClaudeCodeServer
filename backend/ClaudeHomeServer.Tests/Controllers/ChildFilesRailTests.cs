using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Рельса «Файлы» проекта-предмета (Б11a, И8, ADR-041 ред. 5–6, решения 53/54/56/65):
/// [ChildAllowed]-подмножество FilesController судит путь одной функцией — ChildPathPolicy,
/// той же, что решению разрешения инструмента хода (Б10b). Hidden → 404 («нет такого файла»),
/// Deny → 403 с причиной и кодом child_path; список, дерево и поиск не дают скрытого ни именем,
/// ни количеством. Актёр выводится из роли в проекте: владелец — Child, участник-опекун с
/// ролью Editor — Curator; чужой ребёнку проект-предмета до этой границы не доходит (per-owner
/// [ProjectScope]).
///
/// Запись: «Материалы/» и «Для репетитора/» — только чтение ребёнку и репетитору, куратору всё;
/// защищённые имена (.claude, CLAUDE*.md, .mcp.json, .ccs-*, любой «.»-префикс) закрыты всем,
/// включая куратора, на любой глубине. Удаление ребёнка обратимо: прежняя версия идёт в
/// .ccs-trash/ до удаления (решение 54), POST restore возвращает её на исходный путь (решение 56);
/// куратор удаляет без снимка.
///
/// Фикстура: ребёнок заводится публичным путём стора (Add + SetChildProfile, как AuthControllerTests),
/// его проект — провижером предмета (единственная точка его создания). Куратор — Editor-участник
/// этого проекта при рубильнике инстанса и своём флаге общих проектов: без них он к чужому
/// проекту не доходит вовсе. Свой username и свой id предмета на каждый тест: хост класса общий.
/// </summary>
[Trait("Category", "Host")]
public class ChildFilesRailTests : IClassFixture<ChildFilesRailTests.Hosts>
{
    // Хост на класс: рубильник инстанса общих проектов — куратор-участник без него не доходит
    // до проекта-предмета ребёнка (ProjectAccessService.Resolve → fail-closed).
    public sealed class Hosts : TestWebApplicationFactory
    {
        public Hosts() => ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
    }

    private const string ChildPassword = "kid-rail-password";

    private readonly Hosts _host;

    public ChildFilesRailTests(Hosts host) => _host = host;

    private UserStore Users => _host.Services.GetRequiredService<UserStore>();
    private ProjectManager Projects => _host.Services.GetRequiredService<ProjectManager>();

    // --- Фикстура ---------------------------------------------------------------

    // Ребёнок с одним предметом и его проектом-предметом. Имя предмета — tag теста, чтобы
    // состояние одного теста не переходило в другой на общем хосте класса.
    private (string ChildId, HttpClient Child, Project Project, string Root) NewChild(string tag)
    {
        var username = "kid.rail." + tag;
        var kid = Users.Add(username, ChildPassword, "user");
        var subject = new ChildSubject
        {
            Id = tag, Label = "Предмет " + tag, Glyph = "Аа", Color = "info",
        };
        Users.SetChildProfile(kid.Id, new ChildProfile
        {
            Enabled = true, TutorPersonaId = "tutor-1", ProviderKey = "pool-key", Model = "sonnet",
            Spaces = { [ChildSpaces.Lessons] = new SpaceLimits
            {
                Windows = [new TimeWindow { From = "00:00", To = "23:59" }], DailyMinutes = 600,
            } },
            Subjects = [subject],
        }, resetRules: false).Should().BeTrue("профиль ребёнка пишется публичным путём стора");

        var provided = _host.Services.GetRequiredService<ChildSubjectProjectProvisioner>()
            .EnsureSubjectProjectAsync(kid.Id, subject).GetAwaiter().GetResult();
        provided.Should().NotBeNull("проект-предмета обеспечивает только его провижер");
        var project = provided!;
        ProjectCapabilities.IsChildSubject(project).Should().BeTrue();
        // Своя папка ребёнка: провижер предмета создаёт только «Материалы/» и «Для репетитора/»,
        // остальное заводит сам ребёнок. Тесту нужен факт существования обычной папки —
        // запись в несуществующую даёт 404 (нет каталога), а не вердикт границы.
        Directory.CreateDirectory(Path.Combine(project.RootPath!, "Заметки"));
        return (kid.Id, _host.CreateAuthenticatedClient(username, ChildPassword), project, project.RootPath!);
    }

    // Куратор: взрослый как Editor в проекте-предмета + его флаг общих проектов (оба нужны:
    // роль даёт право, гейт — доступ участника к чужому проекту).
    private HttpClient CuratorOf(Project project)
    {
        var adult = Users.FindByUsername(TestWebApplicationFactory.TestUsername)!;
        adult.FeatureFlags = new Dictionary<string, bool> { [FeatureFlagKeys.SharedProjects] = true };
        Projects.SetMembers(project.Id, [new ProjectMember
        {
            UserId = adult.Id, Role = ProjectRole.Editor, AddedBy = project.OwnerId!,
        }]);
        return _host.CreateAuthenticatedClient();
    }

    // Файл на диске напрямую: тесту нужен факт присутствия, а не способ его записать
    private static void WriteOnDisk(Project project, string rel, string content)
    {
        var abs = Path.Combine(project.RootPath!, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content);
    }

    private static string Q(string path) => Uri.EscapeDataString(path);

    private static async Task<string> Body(HttpResponseMessage r) => await r.Content.ReadAsStringAsync();

    /// <summary>Записи корзины этого предмета на диске (состояние проверяется после полного
    /// цикла ручки, а не по её ответу).</summary>
    private static IReadOnlyList<ChildTrashEntry> TrashOf(string root) => ChildTrash.List(root);

    /// <summary>Имена записей ответа zip: байты ответа читаются архивом — поток сборки
    /// живёт в временном файле с DeleteOnClose, надёжнее готовый массив.</summary>
    private static async Task<List<string>> ZipNames(HttpClient http, string projectId, string path)
    {
        var resp = await http.GetAsync($"/api/projects/{projectId}/files/zip?path={Q(path)}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var zip = new ZipArchive(new MemoryStream(await resp.Content.ReadAsByteArrayAsync()));
        return zip.Entries.Select(e => e.FullName).ToList();
    }

    // --- Видимость: список, дерево, поиск --------------------------------------

    [Fact]
    public async Task Tree_РебёнкуНеВидноДляРепетитораКорзииныастроек_МатериалыИТекстовыеКопииВидны()
    {
        var (_, child, project, _) = NewChild("t1");
        WriteOnDisk(project, "Материалы/лист.md", "лист");
        WriteOnDisk(project, "Для репетитора/записка.md", "записка");
        WriteOnDisk(project, ".claude/settings.json", "{}");
        WriteOnDisk(project, "CLAUDE.md", "# правила");
        WriteOnDisk(project, ".ccs-text/картинка.txt", "текстовая копия");
        WriteOnDisk(project, "Заметки/заметка.md", "заметка");

        // showHidden=true: скрытые по умолчанию имена названы явно — иначе тест был бы
        // зелёным на том, что их не показало обычное перечисление.
        var resp = await child.GetAsync($"/api/projects/{project.Id}/files/tree?showHidden=true");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Body(resp);

        // Видимое: своя папка, «Материалы/» (чтение разрешено) и .ccs-text/ (решение 67)
        body.Should().Contain("Материалы").And.Contain("лист.md")
            .And.Contain("Заметки").And.Contain("заметка.md")
            .And.Contain(".ccs-text").And.Contain("картинка.txt");
        // Невидимое: папка репетитора, корзина, настройки и правила CLI — ни именами, ни количеством
        body.Should().NotContain("Для репетитора").And.NotContain("записка.md")
            .And.NotContain(".ccs-trash").And.NotContain(".claude").And.NotContain("CLAUDE.md");
    }

    [Fact]
    public async Task СписокИЧтение_ДляРепетитора_РебёнкуНетТакогоФайла()
    {
        var (_, child, project, _) = NewChild("t2");
        WriteOnDisk(project, "Для репетитора/записка.md", "записка");

        // Названный невидимый путь — 404, а не 403:Hidden описывает не отказ, а невидимость
        // (тот же приём, что чужое private-вложение).
        var list = await child.GetAsync($"/api/projects/{project.Id}/files?path={Q("Для репетитора")}");
        list.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var tree = await child.GetAsync($"/api/projects/{project.Id}/files/tree?path={Q("Для репетитора")}");
        tree.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var content = await child.GetAsync($"/api/projects/{project.Id}/files/content?path={Q("Для репетитора/записка.md")}");
        content.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var stream = await child.GetAsync($"/api/projects/{project.Id}/files/stream?path={Q("Для репетитора/записка.png")}");
        stream.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var zip = await child.GetAsync($"/api/projects/{project.Id}/files/zip?path={Q("Для репетитора")}");
        zip.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Search_ПоСвоейПапкеРазрешён_ВыдачиДляРепетитораНет()
    {
        var (_, child, project, _) = NewChild("t3");
        WriteOnDisk(project, "Заметки/искомое.md", "своё");
        WriteOnDisk(project, "Для репетитора/искомое.md", "репетитор");
        WriteOnDisk(project, ".claude/искомое.md", "настройки");

        var resp = await child.GetAsync($"/api/projects/{project.Id}/files/search?q={Q("искомое")}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Body(resp);
        body.Should().Contain("Заметки/искомое.md");
        body.Should().NotContain("Для репетитора").And.NotContain(".claude");
    }

    // Архив — читающая ручка целиком, а не только её вход: корень предмета границей открыт,
    // а FolderZip обходил бы всю папку — каждая запись архива судится той же
    // ChildPathPolicy (Read), что дерево и список (консультация Александра).
    [Fact]
    public async Task Zip_КореньИПапка_ВАрхивеРебёнкаТолькоВидимоеЕму()
    {
        var (_, child, project, root) = NewChild("t3z");
        WriteOnDisk(project, "Заметки/своё.md", "своё");
        WriteOnDisk(project, "Заметки/.секрет/x.md", "защищённое");
        WriteOnDisk(project, "Материалы/лист.md", "лист");
        WriteOnDisk(project, "Для репетитора/записка.md", "записка");
        WriteOnDisk(project, ".claude/settings.json", "{}");
        WriteOnDisk(project, ".ccs-text/копия.txt", "копия");
        // Запись корзины — настоящим путём ручки: DELETE ребёнка оставляет снимок в .ccs-trash/
        (await child.PutAsJsonAsync($"/api/projects/{project.Id}/files/content?path={Q("Заметки/удалённое.md")}",
            new { content = "снимок" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await child.DeleteAsync($"/api/projects/{project.Id}/files?path={Q("Заметки/удалённое.md")}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var names = await ZipNames(child, project.Id, "");
        // Видимое в дереве — оно же в архиве: своя папка, «Материалы/» (чтение), .ccs-text/ (решение 67)
        names.Should().Contain("Заметки/своё.md").And.Contain("Материалы/лист.md").And.Contain(".ccs-text/копия.txt");
        // Невидимое — ни одной записью: папка репетитора, настройки, корзина, защищённое на глубине
        names.Should().NotContain("Для репетитора/записка.md").And.NotContain(".claude/settings.json")
            .And.NotContain("Заметки/.секрет/x.md");
        names.Should().NotContain(n => n.StartsWith(".ccs-trash/", StringComparison.Ordinal));

        // Архив папки: имя записи relative к упакованной папке, граница решает полный путь от
        // корня предмета — защищённое «.секрет/» внутри открытой «Заметки/» в архив не идёт
        var sub = await ZipNames(child, project.Id, "Заметки");
        sub.Should().Contain("своё.md");
        sub.Should().NotContain(".секрет/x.md");

        // Куратор того же корня: фильтр — свойство актора ребёнка, архив куратора не меняется —
        // «Для репетитора/» в нём на месте (иначе фильтр ушёл бы дальше ребёнка).
        var curatorNames = await ZipNames(CuratorOf(project), project.Id, "");
        curatorNames.Should().Contain("Для репетитора/записка.md");
    }

    // --- Запись: что можно и чего нельзя --------------------------------------

    [Fact]
    public async Task PutContent_МатериалыТолькоЧтение_СвояПапкаРебёнкуОткрыта()
    {
        var (_, child, project, root) = NewChild("t4");

        var denied = await child.PutAsJsonAsync(
            $"/api/projects/{project.Id}/files/content?path={Q("Материалы/лист.md")}", new { content = "х" });
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var why = await Body(denied);
        why.Should().Contain("child_path").And.Contain("только чтение");
        File.Exists(Path.Combine(root, "Материалы", "лист.md")).Should().BeFalse("отказ — до любого эффекта");

        var allowed = await child.PutAsJsonAsync(
            $"/api/projects/{project.Id}/files/content?path={Q("Заметки/заметка.md")}", new { content = "своё" });
        allowed.StatusCode.Should().Be(HttpStatusCode.OK);
        File.ReadAllText(Path.Combine(root, "Заметки", "заметка.md")).Should().Be("своё");
    }

    // Защита на ЛЮБОЙ глубине и на любом пути записи (решение 65, п. 2): та же граница у
    // PUT content, POST create и POST mkdir — все три двери вызывают одну функцию.
    [Theory]
    [InlineData("content")]
    [InlineData("create")]
    [InlineData("mkdir")]
    public async Task ЗаписьРебёнку_ЗащищённыеИменаНаЛюбойГлубине_403(string door)
    {
        var (_, child, project, root) = NewChild("t5" + door);
        var paths = new[]
        {
            ".claude/settings.json", "CLAUDE.md", "CLAUDE.local.md", ".mcp.json",
            ".ccs-text/копия.txt", "Заметки/.секрет/x.md", "a/b/.claude/settings.json",
            ".ccs-trash/20260101000000000-abcd1234/Заметки/заметка.md",
        };
        foreach (var path in paths)
        {
            var url = door == "content"
                ? $"/api/projects/{project.Id}/files/content?path={Q(path)}"
                : $"/api/projects/{project.Id}/files/{door}";
            var body = door == "content"
                ? (object)new { content = "х" }
                : door == "create" ? new { path } : new { path };
            var resp = door == "content"
                ? await child.PutAsJsonAsync(url, body)
                : await child.PostAsJsonAsync(url, body);

            resp.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"дверь {door}, путь {path}");
            (await Body(resp)).Should().Contain("child_path");
            File.Exists(Path.Combine(root, path)).Should().BeFalse($"дверь {door}, путь {path}: отказа не было бы — файл появился");
            Directory.Exists(Path.Combine(root, path)).Should().BeFalse($"дверь {door}, путь {path}");
        }
    }

    [Fact]
    public async Task ЧужойПроектПредмет_РебёнкуЗакрыт_СвойОткрыт()
    {
        var (_, childA, projectA, _) = NewChild("t6a");
        var (_, _, projectB, _) = NewChild("t6b");

        // Свой — 200: отказ ниже — не из-за того, что «проект-предмета», а из-за чужого владельца.
        (await childA.GetAsync($"/api/projects/{projectA.Id}/files/tree")).StatusCode
            .Should().Be(HttpStatusCode.OK);

        // Чужой проект-предмета: per-owner [ProjectScope] отказывает раньше детской границы
        var foreign = await childA.GetAsync($"/api/projects/{projectB.Id}/files/tree");
        ((int)foreign.StatusCode).Should().BeOneOf([403, 404], "чужой проект-предмета ребёнку не виден");
    }

    // Закрытые ручки рельсы (Б11a). Б11b дописал к открытым upload — он вынесен из этого
    // списка и проверяется в ChildFilesUploadTests; здесь остаются те, что метки не имеют.
    [Fact]
    public async Task DiffИRevert_РебёнкуЗакрытыГейтомИ1_МеткиНет()
    {
        var (_, child, project, _) = NewChild("t7");
        WriteOnDisk(project, "Заметки/заметка.md", "своё");

        // Закрытые ручки рельсы не помечаются [ChildAllowed]: 403 child_mode до контроллера
        // (гейт И1 — middleware до маршрутизации, поэтому метод запроса значения не меняет)
        var closed = new (string Method, string Url)[]
        {
            ("GET", $"/api/projects/{project.Id}/files/diff?path={Q("Заметки/заметка.md")}"),
            ("POST", $"/api/projects/{project.Id}/files/revert?path={Q("Заметки/заметка.md")}"),
            ("POST", $"/api/projects/{project.Id}/files/changed-by?path={Q("Заметки/заметка.md")}"),
        };
        foreach (var (method, url) in closed)
        {
            var resp = await child.SendAsync(new HttpRequestMessage(new HttpMethod(method), url));
            resp.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{method} {url}");
            (await Body(resp)).Should().Contain("child_mode", $"{method} {url}");
        }
    }

    // --- Корзина и возврат (решения 54 и 56) ----------------------------------

    [Fact]
    public async Task DeleteРебёнка_СнимокВКорзинеДоУдаления_RestoreВозвращаетФайл()
    {
        var (_, child, project, root) = NewChild("t8");
        const string rel = "Заметки/заметка.md";
        (await child.PutAsJsonAsync($"/api/projects/{project.Id}/files/content?path={Q(rel)}",
            new { content = "прежняя версия" })).StatusCode.Should().Be(HttpStatusCode.OK);

        var deleted = await child.DeleteAsync($"/api/projects/{project.Id}/files?path={Q(rel)}");
        deleted.StatusCode.Should().Be(HttpStatusCode.OK);
        var ids = (await JsonDocument.ParseAsync(await deleted.Content.ReadAsStreamAsync()))
            .RootElement.GetProperty("trashed");
        ids.GetArrayLength().Should().Be(1, "файл — своим снимком");
        var entryId = ids[0].GetString();

        File.Exists(Path.Combine(root, rel)).Should().BeFalse("удаление состоялось");
        var entries = TrashOf(root);
        var only = entries.Should().ContainSingle().Which;
        only.OriginalPath.Should().Be(rel);
        only.Id.Should().Be(entryId);

        var restored = await child.PostAsJsonAsync($"/api/projects/{project.Id}/files/restore",
            new { entryId });
        restored.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonDocument.ParseAsync(await restored.Content.ReadAsStreamAsync()))
            .RootElement.GetProperty("restored").GetString().Should().Be(rel);

        File.ReadAllText(Path.Combine(root, rel)).Should().Be("прежняя версия", "вернулась именно она");
        // Записи после возврата остаются: restore не сносит корзину (её срок — 30 дней, Sweep)
        TrashOf(root).Should().ContainSingle();
    }

    [Fact]
    public async Task RestoreНаЗанятоеИмя_ТекущийФайлУходитВКорзинуПервым()
    {
        var (_, child, project, root) = NewChild("t9");
        const string rel = "Заметки/двойной.md";

        (await child.PutAsJsonAsync($"/api/projects/{project.Id}/files/content?path={Q(rel)}",
            new { content = "v1" })).StatusCode.Should().Be(HttpStatusCode.OK);
        var deleted = await child.DeleteAsync($"/api/projects/{project.Id}/files?path={Q(rel)}");
        deleted.StatusCode.Should().Be(HttpStatusCode.OK);
        var ids = (await JsonDocument.ParseAsync(await deleted.Content.ReadAsStreamAsync()))
            .RootElement.GetProperty("trashed");
        var firstId = ids[0].GetString()!;

        (await child.PutAsJsonAsync($"/api/projects/{project.Id}/files/content?path={Q(rel)}",
            new { content = "v2" })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await child.PostAsJsonAsync($"/api/projects/{project.Id}/files/restore", new { entryId = firstId }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        File.ReadAllText(Path.Combine(root, rel)).Should().Be("v1", "вернулась вызванная запись");
        // Ничего не потеряно: то, что лежало на имени, само снято в корзину (решение 54)
        var entries = TrashOf(root);
        entries.Should().HaveCount(2, "снимок до перезаписи + вызванная запись");
        var current = entries.First(e => e.Id != firstId);
        current.OriginalPath.Should().Be(rel);
        File.ReadAllText(Path.Combine(root, ChildTrash.TrashDir, current.Id, current.OriginalPath))
            .Should().Be("v2");
    }

    [Fact]
    public async Task Restore_ЧужойЗаписиИПутиКоторыйНельзя_403_404_БезЭффекта()
    {
        var (childId, child, project, root) = NewChild("t10");

        // Чужой id (записи такой нет) — 404, как чужого файла: существование записи не раскрываем
        (await child.PostAsJsonAsync($"/api/projects/{project.Id}/files/restore",
            new { entryId = "20260101000000000-abcd1234" })).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Своя запись, но её путь закрыт ребёнку для записи: снимок сделал репетитор (его Write
        // в своей папке), вернуть его ребёнку нельзя — 403 до любого эффекта.
        WriteOnDisk(project, "Для репетитора/записка.md", "репетитор");
        var snapshotId = ChildTrash.Snapshot(root, "Для репетитора/записка.md", childId)!;
        var denied = await child.PostAsJsonAsync($"/api/projects/{project.Id}/files/restore",
            new { entryId = snapshotId });
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Body(denied)).Should().Contain("child_path");
        File.Exists(Path.Combine(root, "Для репетитора", "записка.md")).Should().BeTrue("отказ — без эффекта на диске");
        TrashOf(root).Should().ContainSingle("запись осталась: её можно вернуть тому, кому можно");

        // Тому, кому можно (куратору), тот же id возвращается
        (await CuratorOf(project).PostAsJsonAsync($"/api/projects/{project.Id}/files/restore",
            new { entryId = snapshotId })).StatusCode.Should().Be(HttpStatusCode.OK);
        File.ReadAllText(Path.Combine(root, "Для репетитора", "записка.md")).Should().Be("репетитор");
    }

    [Fact]
    public async Task КураторПишетВДляРепетитора_ИВидитЕё_СлужебноеЕмуЗакрыто()
    {
        var (_, child, project, root) = NewChild("t11");
        var curator = CuratorOf(project);

        // Куратору «Для репетитора/» открыта (решение 65): он ведёт папку своего ребёнка
        (await curator.PutAsJsonAsync($"/api/projects/{project.Id}/files/content?path={Q("Для репетитора/записка.md")}",
            new { content = "куратор" })).StatusCode.Should().Be(HttpStatusCode.OK);
        File.ReadAllText(Path.Combine(root, "Для репетитора", "записка.md")).Should().Be("куратор");

        // «Материалы/» куратору тоже открыты: их закрытие — право ребёнка и репетитора
        (await curator.PutAsJsonAsync($"/api/projects/{project.Id}/files/content?path={Q("Материалы/лист.md")}",
            new { content = "лист" })).StatusCode.Should().Be(HttpStatusCode.OK);

        // Дерево куратора — полное: и папка репетитора, и служебные имена (он их читает)
        var tree = await curator.GetAsync($"/api/projects/{project.Id}/files/tree?showHidden=true");
        tree.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Body(tree);
        body.Should().Contain("Для репетитора").And.Contain("записка.md").And.Contain("Материалы");

        // Служебное и настройки не правит никто, включая куратора, ни на какой глубине
        foreach (var path in new[] { ".claude/settings.json", "CLAUDE.md", "Заметки/.секрет/x.md" })
        {
            var denied = await curator.PutAsJsonAsync(
                $"/api/projects/{project.Id}/files/content?path={Q(path)}", new { content = "х" });
            denied.StatusCode.Should().Be(HttpStatusCode.Forbidden, path);
            (await Body(denied)).Should().Contain("child_path", path);
        }

        // Ребёнку эта папка по-прежнему не видна — запись куратора её не открывает
        (await child.GetAsync($"/api/projects/{project.Id}/files/content?path={Q("Для репетитора/записка.md")}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task КураторУдаляетБезСнимка_КорзинаНеРастёт()
    {
        var (_, child, project, root) = NewChild("t12");
        const string rel = "Заметки/кураторское.md";
        (await child.PutAsJsonAsync($"/api/projects/{project.Id}/files/content?path={Q(rel)}",
            new { content = "своё" })).StatusCode.Should().Be(HttpStatusCode.OK);

        var curator = CuratorOf(project);
        // Путь для куратора открыт (мягкая граница: что ребёнок записал, куратор прочтёт и правит)
        var deleted = await curator.DeleteAsync($"/api/projects/{project.Id}/files?path={Q(rel)}");
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "ответ без trashed: корзина — механизм ребёнка и репетитора, не взрослого");
        File.Exists(Path.Combine(root, rel)).Should().BeFalse();
        TrashOf(root).Should().BeEmpty("снимка не было");
    }

    // --- Гонка (решение 56): перестановка и удаление одного пути --------------

    [Fact]
    public async Task ГонкаRenameИDelete_ФайлНеТеряется_20Повторов()
    {
        var (_, child, project, root) = NewChild("t13");
        for (var iter = 0; iter < 20; iter++)
        {
            var relOld = $"Заметки/гонка{iter}.md";
            var relNew = $"Заметки/перенесено{iter}.md";
            (await child.PutAsJsonAsync($"/api/projects/{project.Id}/files/content?path={Q(relOld)}",
                new { content = "x" })).StatusCode.Should().Be(HttpStatusCode.OK);

            // Одновременные перестановка и удаление одного пути: обе берут замок старого пути
            // (rename — оба своих, в нормализованном порядке), поэтому сериализованы, а не
            // переплетены. Проигравшая сторона — 404: её цели уже нет.
            var rename = child.PostAsJsonAsync($"/api/projects/{project.Id}/files/rename",
                new { oldPath = relOld, newPath = relNew });
            var delete = child.DeleteAsync($"/api/projects/{project.Id}/files?path={Q(relOld)}");
            var responses = await Task.WhenAll(rename, delete);
            ((int)responses[0].StatusCode).Should().BeOneOf([200, 404], $"итерация {iter}: перестановка");
            ((int)responses[1].StatusCode).Should().BeOneOf([200, 404], $"итерация {iter}: удаление");

            var renamed = File.Exists(Path.Combine(root, relNew));
            var trashed = TrashOf(root).Any(e => e.OriginalPath == relOld);
            (renamed || trashed).Should().BeTrue($"итерация {iter}: файла нет ни на новом имени, ни в корзине");
            (renamed && trashed).Should().BeFalse($"итерация {iter}: файл и перенесён, и снят — одна операция съела другую");
            File.Exists(Path.Combine(root, relOld)).Should().BeFalse($"итерация {iter}: старый путь жив");
        }
    }

    [Fact]
    public async Task ГонкаДвухУдаленийОдногоПути_РовноОдинСнимок()
    {
        var (_, child, project, root) = NewChild("t14");
        for (var iter = 0; iter < 10; iter++)
        {
            var rel = $"Заметки/двойное{iter}.md";
            (await child.PutAsJsonAsync($"/api/projects/{project.Id}/files/content?path={Q(rel)}",
                new { content = "x" })).StatusCode.Should().Be(HttpStatusCode.OK);

            var first = child.DeleteAsync($"/api/projects/{project.Id}/files?path={Q(rel)}");
            var second = child.DeleteAsync($"/api/projects/{project.Id}/files?path={Q(rel)}");
            await Task.WhenAll(first, second);

            // Один снимок на один файл: второй доходит уже до отсутствующего пути — 404 без
            // записи (ChildTrash.Snapshot не пишет отсутствующего).
            TrashOf(root).Count(e => e.OriginalPath == rel).Should().Be(1, $"итерация {iter}");
        }
    }
}
